using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Tamma.Api.Services.TaskQueue;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.GitHub;

/// <summary>
/// Concrete <see cref="IInstallationRouterService"/> implementation.
///
/// Ported from the deleted TypeScript <c>installation-router</c> + webhook
/// handler under <c>packages/api</c> (removed in Epic 19 Phase 3).
/// </summary>
public sealed class InstallationRouterService : IInstallationRouterService
{
    /// <summary>
    /// Audit finding 029 — TS maintained a 60s TTL cache keyed by installation
    /// id. Webhook dispatch hit the cache on the steady-state path; only one
    /// DB lookup per minute per installation. The C# port dropped this and
    /// took a DB roundtrip per webhook (~30-50ms with the
    /// <c>.Include(i => i.Repos)</c> fan-out), regressing p99 dispatch
    /// latency under load.
    /// </summary>
    private static readonly TimeSpan InstallationCacheTtl = TimeSpan.FromSeconds(60);

    private const string InstallationApiKeyScope = "installation";
    private const string InstallationApiKeyLabel = "installation-key";
    private const int InstallationApiKeyPrefixLength = 16;

    private readonly IInstallationRepository _installations;
    private readonly IEventRepository _events;
    private readonly ITenantRepository _tenants;
    private readonly IUserRepository _users;
    private readonly ITaskQueue? _taskQueue;
    private readonly IMemoryCache _cache;
    private readonly IGitHubAppClient _gitHubApp;
    private readonly IGitHubSecretsProvisioner _provisioner;
    private readonly IApiKeyRepository _apiKeys;
    private readonly ILogger<InstallationRouterService> _logger;

    public InstallationRouterService(
        IInstallationRepository installations,
        IEventRepository events,
        ITenantRepository tenants,
        IUserRepository users,
        IMemoryCache cache,
        IGitHubAppClient gitHubApp,
        IGitHubSecretsProvisioner provisioner,
        IApiKeyRepository apiKeys,
        ILogger<InstallationRouterService> logger,
        ITaskQueue? taskQueue = null)
    {
        _installations = installations;
        _events = events;
        _tenants = tenants;
        _users = users;
        _cache = cache;
        _gitHubApp = gitHubApp;
        _provisioner = provisioner;
        _apiKeys = apiKeys;
        _taskQueue = taskQueue;
        _logger = logger;
    }

    private static string CacheKeyForInstallation(long installationId) =>
        $"install:{installationId}";

    private async Task<GitHubInstallation?> GetInstallationCachedAsync(long installationId)
    {
        // 60-second TTL — short enough to surface install/uninstall/suspend
        // state changes without a process restart, long enough to amortise
        // the steady-state webhook flood.
        return await _cache.GetOrCreateAsync(CacheKeyForInstallation(installationId), entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = InstallationCacheTtl;
            return _installations.GetByInstallationIdAsync(installationId);
        });
    }

    private void InvalidateInstallationCache(long installationId)
    {
        _cache.Remove(CacheKeyForInstallation(installationId));
    }

    // ─── OAuth callback ─────────────────────────────────────────────────────

    public async Task<CallbackResult> HandleCallbackAsync(
        long installationId,
        int? setupActionId,
        Guid? callingUserId)
    {
        // Audit finding 020 — orphan-persist when no user session is present
        // (typical for Marketplace installs). Return Success=true with
        // TenantId=null; the endpoint redirects to a "claim installation"
        // landing page.
        User? user = null;
        Tenant? tenant = null;
        if (callingUserId is not null)
        {
            user = await _users.GetByIdAsync(callingUserId.Value);
            if (user is null)
            {
                _logger.LogWarning(
                    "Install callback rejected: unknown user {UserId} for installation {InstallationId}",
                    callingUserId, installationId);
                return new CallbackResult(false, null, installationId, null, "unknown_user");
            }

            if (user.TenantId is null)
            {
                _logger.LogWarning(
                    "Install callback rejected: user {UserId} has no active tenant", callingUserId);
                return new CallbackResult(false, null, installationId, null, "no_active_tenant");
            }

            tenant = await _tenants.GetByIdAsync(user.TenantId.Value);
            if (tenant is null)
            {
                _logger.LogWarning(
                    "Install callback rejected: tenant {TenantId} not found for user {UserId}",
                    user.TenantId, callingUserId);
                return new CallbackResult(false, null, installationId, null, "tenant_not_found");
            }
        }

        // Audit finding 007 — fetch authoritative installation metadata from
        // GitHub when the App client is wired. Falls back to local placeholder
        // values when the Null impl returns ServiceUnavailable; the install
        // still links to the tenant so the Marketplace + onboarding-redirect
        // flows keep working with degraded fidelity.
        var installFetch = await _gitHubApp.GetInstallationAsync(installationId);
        GitHubInstallationDetails? details = installFetch.ServiceUnavailable
            ? null
            : installFetch.Result;

        var existing = await _installations.GetByInstallationIdAsync(installationId);
        GitHubInstallation stored;

        if (existing is null)
        {
            stored = await _installations.CreateAsync(new GitHubInstallation
            {
                InstallationId = installationId,
                AccountLogin = details?.AccountLogin
                    ?? user?.GitHubLogin ?? tenant?.Slug ?? $"orphan-{installationId}",
                AccountType = details?.AccountType ?? "User",
                AppId = details?.AppId ?? 0,
                Permissions = details?.PermissionsJson ?? "{}",
                SuspendedAt = details?.SuspendedAt,
                TenantId = tenant?.Id  // null = orphan (audit finding 020)
            });
        }
        else
        {
            // Only overwrite the tenant link when we have a real one — orphan
            // callbacks must NOT clear an existing tenant binding.
            if (tenant is not null)
            {
                existing.TenantId = tenant.Id;
            }
            if (details is not null)
            {
                existing.AccountLogin = details.AccountLogin;
                existing.AccountType = details.AccountType;
                existing.AppId = details.AppId;
                existing.Permissions = details.PermissionsJson;
                existing.SuspendedAt = details.SuspendedAt;
            }
            stored = await _installations.UpsertAsync(existing);
        }
        InvalidateInstallationCache(installationId);

        // Audit finding 007 — fetch authoritative repo list when the client is
        // wired. The webhook also seeds repos from `payload.repositories` so
        // there's redundancy on both legs; in TS the callback was the
        // authoritative source.
        if (!installFetch.ServiceUnavailable)
        {
            var repoFetch = await _gitHubApp.ListInstallationReposAsync(installationId);
            if (!repoFetch.ServiceUnavailable && repoFetch.Result is not null)
            {
                foreach (var repo in repoFetch.Result)
                {
                    await _installations.AddRepoAsync(
                        stored.Id, repo.RepoId, repo.FullName);
                }
            }
        }

        // Audit findings 008 + 013 — generate the installation API key and
        // push it to every accessible repo as `TAMMA_API_KEY`. Skipped on the
        // orphan path because there is no tenant to scope the key to; the
        // claim-installation flow re-runs this once the user signs in.
        KeyIssueOutcome? keyResult = null;
        if (stored.TenantId is not null)
        {
            keyResult = await IssueInstallationKeyAsync(stored, stored.TenantId.Value);
        }

        await EmitEventAsync(
            stored.TenantId is null
                ? "INSTALLATION.ORPHAN_PERSISTED.SUCCESS"
                : "INSTALLATION.LINKED.SUCCESS",
            stored.TenantId,
            new Dictionary<string, object?>
            {
                ["installationId"] = installationId,
                ["tenantId"] = stored.TenantId,
                ["userId"] = callingUserId,
                ["setupAction"] = setupActionId,
                ["apiKeyIssued"] = keyResult?.Issued ?? false,
                ["apiKeyId"] = keyResult?.KeyId,
                ["reposProvisioned"] = keyResult?.ReposProvisioned ?? 0,
                ["reposFailed"] = keyResult?.ReposFailed ?? 0
            });

        if (stored.TenantId is null)
        {
            _logger.LogInformation(
                "Persisted orphan GitHub installation {InstallationId} (no caller session)",
                installationId);
        }
        else
        {
            _logger.LogInformation(
                "Linked GitHub installation {InstallationId} to tenant {TenantId} (user {UserId}); apiKeyIssued={Issued} reposProvisioned={Ok} reposFailed={Failed}",
                installationId, stored.TenantId, callingUserId,
                keyResult?.Issued ?? false, keyResult?.ReposProvisioned ?? 0, keyResult?.ReposFailed ?? 0);
        }

        return new CallbackResult(true, stored.Id, installationId, stored.TenantId, null);
    }

    /// <summary>
    /// Generate a new <c>installation</c>-scope API key for the install,
    /// persist its hash/prefix on the <c>api_keys</c> table, and push the
    /// plaintext to every active repo as <c>TAMMA_API_KEY</c>. Returns a
    /// summary describing what got issued; the plaintext itself is never
    /// logged.
    /// </summary>
    private async Task<KeyIssueOutcome> IssueInstallationKeyAsync(
        GitHubInstallation install, Guid tenantId)
    {
        // Skip when a key already exists — the callback is rerunnable (the
        // user can revisit the install URL) and we don't want to mint a new
        // key on every re-link.
        var existing = (await _apiKeys.ListByOwnerAsync(install.Id.ToString()))
            .FirstOrDefault(k => string.Equals(
                k.Scope, InstallationApiKeyScope, StringComparison.OrdinalIgnoreCase)
                && k.RevokedAt is null);
        if (existing is not null)
        {
            return new KeyIssueOutcome(false, existing.Id, 0, 0);
        }

        var plaintext = GenerateInstallationKey();
        var keyHash = HashKey(plaintext);
        var keyPrefix = plaintext.Length >= InstallationApiKeyPrefixLength
            ? plaintext[..InstallationApiKeyPrefixLength]
            : plaintext;

        var stored = await _apiKeys.CreateAsync(new ApiKey
        {
            Scope = InstallationApiKeyScope,
            OwnerId = install.Id.ToString(),
            KeyHash = keyHash,
            KeyPrefix = keyPrefix,
            Label = InstallationApiKeyLabel,
            Permissions = Array.Empty<string>(),
            TenantId = tenantId
        });

        // Provision to every active repo. The Null provisioner returns
        // `github_client_not_configured` per-repo until the real impl lands;
        // either way we record the summary in the linked event.
        var repos = await _installations.ListReposAsync(install.Id);
        var repoTuples = (IReadOnlyList<(string Owner, string Repo)>)repos
            .Where(r => r.IsActive
                && !string.IsNullOrEmpty(r.Owner)
                && !string.IsNullOrEmpty(r.Name))
            .Select(r => (r.Owner, r.Name))
            .ToList();

        var provisionResults = await _provisioner.ProvisionSecretAsync(
            install.InstallationId, repoTuples, "TAMMA_API_KEY", plaintext);

        var ok = provisionResults.Count(r => r.Success);
        var failed = provisionResults.Count - ok;

        return new KeyIssueOutcome(true, stored.Id, ok, failed);
    }

    private sealed record KeyIssueOutcome(
        bool Issued, Guid? KeyId, int ReposProvisioned, int ReposFailed);

    private static string GenerateInstallationKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var body = Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        return $"tamma_sk_{body}";
    }

    private static string HashKey(string plaintext)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext))).ToLowerInvariant();

    // ─── Webhook dispatch ───────────────────────────────────────────────────

    public async Task<WebhookResult> HandleWebhookAsync(string eventType, JsonElement payload)
    {
        var action = TryGetString(payload, "action");

        switch (eventType)
        {
            case "installation":
                return await HandleInstallationEventAsync(payload, action);

            case "installation_repositories":
                return await HandleInstallationRepositoriesEventAsync(payload, action);

            // Deferred events — enqueue for async processing so the webhook
            // handler can return quickly. Ported from the TS queueing path.
            case "push":
            case "issues":
            case "pull_request":
                return await EnqueueDeferredEventAsync(eventType, action, payload);

            default:
                _logger.LogDebug(
                    "Webhook event {Event} (action={Action}) skipped (not handled)",
                    Logging.LogSanitizer.Clean(eventType), Logging.LogSanitizer.Clean(action));
                return new WebhookResult(eventType, action, Skipped: true);
        }
    }

    /// <summary>
    /// Push/issues/pull_request events are deferred to the task queue so the
    /// webhook handler returns fast. When the task queue is not wired (tests
    /// that only register the installation router) the event falls through to
    /// <c>skipped = true</c> so old behaviour remains observable.
    /// </summary>
    private async Task<WebhookResult> EnqueueDeferredEventAsync(
        string eventType, string? action, JsonElement payload)
    {
        if (_taskQueue is null)
        {
            _logger.LogDebug(
                "Webhook event {Event} (action={Action}) skipped: task queue not registered",
                Logging.LogSanitizer.Clean(eventType), Logging.LogSanitizer.Clean(action));
            return new WebhookResult(eventType, action, Skipped: true);
        }

        long? installationId = null;
        if (payload.TryGetProperty("installation", out var installationEl))
        {
            installationId = GetInstallationId(installationEl);
        }

        // Bind to the tenant that owns this installation (if any). Unknown
        // installations are still enqueued with a null tenant — callers can
        // decide at handler-time whether to drop them.
        // (Audit finding 029) cache lookup avoids DB roundtrip on hot path.
        Guid? tenantId = null;
        if (installationId is not null)
        {
            var install = await GetInstallationCachedAsync(installationId.Value);
            tenantId = install?.TenantId;
        }

        var taskType = string.IsNullOrEmpty(action)
            ? $"github.{eventType}"
            : $"github.{eventType}.{action}";

        var task = await _taskQueue.EnqueueAsync(
            type: taskType,
            payloadJson: payload.GetRawText(),
            installationId: installationId,
            tenantIdOverride: tenantId);

        _logger.LogInformation(
            "Webhook {Event} (action={Action}) queued as task {TaskId} (installation={InstallationId}, tenant={TenantId})",
            Logging.LogSanitizer.Clean(eventType), Logging.LogSanitizer.Clean(action),
            task.Id, installationId, tenantId);

        return new WebhookResult(eventType, action, Skipped: false, TaskId: task.Id);
    }

    private async Task<WebhookResult> HandleInstallationEventAsync(
        JsonElement payload, string? action)
    {
        if (!payload.TryGetProperty("installation", out var installationEl))
        {
            _logger.LogWarning("installation event missing installation object");
            return new WebhookResult("installation", action, Skipped: true);
        }

        var installationId = GetInstallationId(installationEl);
        if (installationId is null)
        {
            _logger.LogWarning("installation event missing installation.id");
            return new WebhookResult("installation", action, Skipped: true);
        }

        switch (action)
        {
            case "created":
            {
                var accountLogin = "unknown";
                var accountType = "User";
                if (installationEl.TryGetProperty("account", out var account))
                {
                    accountLogin = TryGetString(account, "login") ?? accountLogin;
                    accountType = TryGetString(account, "type") ?? accountType;
                }

                var appId = 0;
                if (installationEl.TryGetProperty("app_id", out var appIdEl) &&
                    appIdEl.ValueKind == JsonValueKind.Number)
                {
                    appId = appIdEl.GetInt32();
                }

                string permissions = "{}";
                if (installationEl.TryGetProperty("permissions", out var permsEl))
                {
                    permissions = permsEl.GetRawText();
                }

                var stored = await _installations.UpsertAsync(new GitHubInstallation
                {
                    InstallationId = installationId.Value,
                    AccountLogin = accountLogin,
                    AccountType = accountType,
                    AppId = appId,
                    Permissions = permissions
                });

                // Audit finding 029 — invalidate cache so the next webhook
                // observes the new installation immediately.
                InvalidateInstallationCache(installationId.Value);

                // Seed initial repositories (if the payload carries them).
                if (payload.TryGetProperty("repositories", out var reposEl) &&
                    reposEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var repo in reposEl.EnumerateArray())
                    {
                        var repoId = TryGetLong(repo, "id");
                        var fullName = TryGetString(repo, "full_name");
                        if (repoId is not null && !string.IsNullOrWhiteSpace(fullName))
                        {
                            await _installations.AddRepoAsync(
                                stored.Id, repoId.Value, fullName);
                        }
                    }
                }

                await EmitEventAsync(
                    "INSTALLATION.CREATED.SUCCESS",
                    stored.TenantId,
                    new Dictionary<string, object?>
                    {
                        ["installationId"] = installationId,
                        ["accountLogin"] = accountLogin,
                        ["accountType"] = accountType
                    });

                return new WebhookResult("installation", action, Skipped: false);
            }

            case "deleted":
            {
                // Audit finding 030 — Option A (match TS hard-delete). Audit
                // is preserved by the INSTALLATION.DELETED.SUCCESS event below
                // (the event carries the installation id and survives the row
                // deletion). Reusing SuspendedAt as a soft-delete marker
                // collided with the suspend/unsuspend lifecycle and let an
                // unsuspend webhook resurrect a deleted record.
                await _installations.DeleteAsync(installationId.Value);
                InvalidateInstallationCache(installationId.Value);
                await EmitEventAsync(
                    "INSTALLATION.DELETED.SUCCESS",
                    null,
                    new Dictionary<string, object?>
                    {
                        ["installationId"] = installationId
                    });
                return new WebhookResult("installation", action, Skipped: false);
            }

            case "suspend":
                await _installations.SetSuspendedAsync(installationId.Value, true);
                InvalidateInstallationCache(installationId.Value);
                await EmitEventAsync(
                    "INSTALLATION.SUSPENDED.SUCCESS",
                    null,
                    new Dictionary<string, object?> { ["installationId"] = installationId });
                return new WebhookResult("installation", action, Skipped: false);

            case "unsuspend":
                await _installations.SetSuspendedAsync(installationId.Value, false);
                InvalidateInstallationCache(installationId.Value);
                await EmitEventAsync(
                    "INSTALLATION.UNSUSPENDED.SUCCESS",
                    null,
                    new Dictionary<string, object?> { ["installationId"] = installationId });
                return new WebhookResult("installation", action, Skipped: false);

            default:
                _logger.LogDebug(
                    "installation action {Action} not handled — skipping", action);
                return new WebhookResult("installation", action, Skipped: true);
        }
    }

    private async Task<WebhookResult> HandleInstallationRepositoriesEventAsync(
        JsonElement payload, string? action)
    {
        if (!payload.TryGetProperty("installation", out var installationEl))
        {
            return new WebhookResult("installation_repositories", action, Skipped: true);
        }

        var installationId = GetInstallationId(installationEl);
        if (installationId is null)
        {
            return new WebhookResult("installation_repositories", action, Skipped: true);
        }

        var install = await _installations.GetByInstallationIdAsync(installationId.Value);
        if (install is null)
        {
            _logger.LogWarning(
                "installation_repositories event for unknown installation {InstallationId} — skipping",
                installationId);
            return new WebhookResult("installation_repositories", action, Skipped: true);
        }

        if (payload.TryGetProperty("repositories_added", out var added) &&
            added.ValueKind == JsonValueKind.Array)
        {
            foreach (var repo in added.EnumerateArray())
            {
                var repoId = TryGetLong(repo, "id");
                var fullName = TryGetString(repo, "full_name");
                if (repoId is not null && !string.IsNullOrWhiteSpace(fullName))
                {
                    await _installations.AddRepoAsync(install.Id, repoId.Value, fullName);
                }
            }
        }

        if (payload.TryGetProperty("repositories_removed", out var removed) &&
            removed.ValueKind == JsonValueKind.Array)
        {
            foreach (var repo in removed.EnumerateArray())
            {
                var repoId = TryGetLong(repo, "id");
                if (repoId is not null)
                {
                    await _installations.RemoveRepoAsync(install.Id, repoId.Value);
                }
            }
        }

        await EmitEventAsync(
            action == "removed"
                ? "INSTALLATION_REPOSITORIES.REMOVED.SUCCESS"
                : "INSTALLATION_REPOSITORIES.ADDED.SUCCESS",
            install.TenantId,
            new Dictionary<string, object?>
            {
                ["installationId"] = installationId,
                ["action"] = action
            });

        return new WebhookResult("installation_repositories", action, Skipped: false);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private Task EmitEventAsync(string type, Guid? tenantId, Dictionary<string, object?> data)
    {
        return _events.AppendAsync(new DomainEvent
        {
            Type = type,
            TenantId = tenantId,
            Tags = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["eventSource"] = "system"
            }),
            Metadata = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["eventSource"] = "system",
                ["workflowVersion"] = "1.0.0"
            }),
            Data = JsonSerializer.Serialize(data)
        });
    }

    private static long? GetInstallationId(JsonElement installationEl)
    {
        if (!installationEl.TryGetProperty("id", out var idEl)) return null;
        return idEl.ValueKind switch
        {
            JsonValueKind.Number => idEl.TryGetInt64(out var n) ? n : null,
            JsonValueKind.String when long.TryParse(idEl.GetString(), out var s) => s,
            _ => null
        };
    }

    private static string? TryGetString(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }

    private static long? TryGetLong(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind switch
        {
            JsonValueKind.Number => prop.TryGetInt64(out var n) ? n : null,
            JsonValueKind.String when long.TryParse(prop.GetString(), out var s) => s,
            _ => null
        };
    }
}
