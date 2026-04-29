using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Tamma.Activities.AgentDispatch;
using Tamma.Api.Services.TaskQueue;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.GitHub;

#pragma warning disable CS0618 // Story 31-8: transitional consumer of obsolete IGitHubSecretsProvisioner.

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
    private readonly IPlatformQueuedTaskRepository? _platformTasks;
    private readonly IMemoryCache _cache;
    private readonly IGitHubAppClient _gitHubApp;
    private readonly IGitHubSecretsProvisioner _provisioner;
    private readonly IApiKeyRepository _apiKeys;
    private readonly IWebhookSignalRegistry? _webhookSignals;
    private readonly ILogger<InstallationRouterService> _logger;

    /// <summary>
    /// Story 28-1 PR B — webhook deferral now routes by tenancy:
    /// tenant-bound webhooks (installation has a TenantId) go to the
    /// per-tenant queue via <see cref="ITaskQueue"/>; orphan webhooks
    /// (no TenantId on the installation row) go to the platform queue
    /// via <see cref="IPlatformQueuedTaskRepository"/>. Both repos are
    /// optional so the existing test fixtures that wire only one of
    /// them keep working.
    /// </summary>
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
        ITaskQueue? taskQueue = null,
        IPlatformQueuedTaskRepository? platformTasks = null,
        IWebhookSignalRegistry? webhookSignals = null)
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
        _platformTasks = platformTasks;
        _webhookSignals = webhookSignals;
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

            // Story 19-3 AC-7 — webhook-mode monitor wake-up. Match the
            // workflow_run to a suspended AgentMonitorService by
            // (repo, run_id) or (repo, branch, session_id). Non-matching
            // workflow_runs (not Tamma-dispatched) fall through to Skipped.
            case "workflow_run":
                return HandleWorkflowRunEvent(payload, action);

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
    /// Story 19-3 AC-7 — match an incoming <c>workflow_run.completed</c>
    /// webhook to a suspended <see cref="IAgentMonitorService"/> call.
    /// Skipped when:
    /// <list type="bullet">
    ///   <item>the webhook-signal registry is not wired (self-hosted mode);</item>
    ///   <item>the <c>action</c> is not <c>completed</c> (we don't care about in-flight updates — the monitor is interested in terminal states only);</item>
    ///   <item>no waiter is registered for the matched key (not every workflow_run is Tamma-dispatched).</item>
    /// </list>
    /// </summary>
    private WebhookResult HandleWorkflowRunEvent(JsonElement payload, string? action)
    {
        if (_webhookSignals is null)
        {
            _logger.LogDebug(
                "workflow_run event received but IWebhookSignalRegistry is not registered — skipping");
            return new WebhookResult("workflow_run", action, Skipped: true);
        }

        // Only terminal transitions matter. GitHub fires this event on every
        // status change (requested / in_progress / completed); the monitor
        // only cares when the run is done.
        if (!string.Equals(action, "completed", StringComparison.OrdinalIgnoreCase))
        {
            return new WebhookResult("workflow_run", action, Skipped: true);
        }

        if (!payload.TryGetProperty("workflow_run", out var runEl) ||
            runEl.ValueKind != JsonValueKind.Object)
        {
            _logger.LogWarning("workflow_run.completed payload missing 'workflow_run' object");
            return new WebhookResult("workflow_run", action, Skipped: true);
        }

        var runId = TryGetLong(runEl, "id");
        if (runId is null)
        {
            _logger.LogWarning("workflow_run.completed payload missing run id");
            return new WebhookResult("workflow_run", action, Skipped: true);
        }

        // Repo slug. GitHub puts it on both `repository.full_name` (outer) and
        // `workflow_run.repository.full_name` — prefer the outer as it's the
        // canonical delivery-context repo.
        string? repoFullName = null;
        if (payload.TryGetProperty("repository", out var repoEl) &&
            repoEl.ValueKind == JsonValueKind.Object)
        {
            repoFullName = TryGetString(repoEl, "full_name");
        }
        if (string.IsNullOrEmpty(repoFullName) &&
            runEl.TryGetProperty("repository", out var nestedRepo) &&
            nestedRepo.ValueKind == JsonValueKind.Object)
        {
            repoFullName = TryGetString(nestedRepo, "full_name");
        }
        if (string.IsNullOrEmpty(repoFullName))
        {
            _logger.LogWarning("workflow_run.completed payload missing repository.full_name");
            return new WebhookResult("workflow_run", action, Skipped: true);
        }

        var status = TryGetString(runEl, "status") ?? "completed";
        var conclusion = TryGetString(runEl, "conclusion") ?? string.Empty;
        var htmlUrl = TryGetString(runEl, "html_url") ?? string.Empty;
        var artifactsUrl = TryGetString(runEl, "artifacts_url") ?? string.Empty;
        var headBranch = TryGetString(runEl, "head_branch");
        var createdAt = TryGetDateTime(runEl, "created_at") ?? DateTime.UtcNow;
        var updatedAt = TryGetDateTime(runEl, "updated_at") ?? DateTime.UtcNow;

        // Extract installation.id from the outer payload so the publish key
        // is tenant-scoped (review-session 2026-04-20 finding 5). Without
        // this, two tenants with Tamma installed on the same owner/repo can
        // cross-wake each other's AgentMonitorService via the branch-
        // fallback alias.
        long? installationId = null;
        if (payload.TryGetProperty("installation", out var installEl) &&
            installEl.ValueKind == JsonValueKind.Object)
        {
            installationId = GetInstallationId(installEl);
        }
        if (installationId is null)
        {
            _logger.LogWarning(
                "workflow_run.completed payload missing installation.id — " +
                "publishing unscoped key (back-compat path, cross-tenant risk)");
        }

        var signal = new AgentWebhookSignal(
            WorkflowRunId: runId.Value,
            Status: status,
            Conclusion: conclusion,
            WorkflowRunUrl: htmlUrl,
            CreatedAt: createdAt,
            UpdatedAt: updatedAt,
            ArtifactsUrl: artifactsUrl);

        // We don't know the session id on the webhook side, so we publish
        // under the run-id key. The registry also tries a branch-fallback
        // lookup, which lets a webhook that beats discovery match the
        // (repo, branch, *) waiter. The installation id scopes all aliases
        // to the specific GitHub App installation so two tenants sharing
        // an owner/repo + branch cannot cross-wake each other.
        var publishKey = new AgentWebhookSignalKey(
            Repository: repoFullName,
            HeadBranch: headBranch,
            SessionId: null,
            WorkflowRunId: runId.Value,
            InstallationId: installationId);

        var matched = _webhookSignals.PublishSignal(publishKey, signal);
        if (matched)
        {
            _logger.LogInformation(
                "workflow_run.completed webhook matched waiter: repo={Repo} run={RunId} conclusion={Conclusion}",
                Logging.LogSanitizer.Clean(repoFullName), runId, Logging.LogSanitizer.Clean(conclusion));
        }
        else
        {
            _logger.LogDebug(
                "workflow_run.completed webhook with no matching waiter (not a Tamma-dispatched run)");
        }

        return new WebhookResult("workflow_run", action, Skipped: !matched);
    }

    private static DateTime? TryGetDateTime(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(name, out var prop)) return null;
        if (prop.ValueKind != JsonValueKind.String) return null;
        if (DateTime.TryParse(prop.GetString(),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal |
            System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var dt))
        {
            return dt;
        }
        return null;
    }

    /// <summary>
    /// Push/issues/pull_request events are deferred to the task queue so the
    /// webhook handler returns fast. When the task queue is not wired (tests
    /// that only register the installation router) the event falls through to
    /// <c>skipped = true</c> so old behaviour remains observable.
    ///
    /// <para>Story 28-1 PR B — routing splits by tenancy:
    /// <list type="bullet">
    ///   <item><description>installation has a TenantId → tenant queue
    ///     (<see cref="ITaskQueue"/>). Per-tenant DB drains via the
    ///     <c>TaskQueueProcessor</c>.</description></item>
    ///   <item><description>orphan installation (TenantId is null) →
    ///     platform queue (<see cref="IPlatformQueuedTaskRepository"/>).
    ///     Drained by the <c>PlatformTaskWorker</c>. Without a tenant DB
    ///     the per-tenant queue can't accept the row, so the platform
    ///     queue is the only viable home.</description></item>
    /// </list></para>
    /// </summary>
    private async Task<WebhookResult> EnqueueDeferredEventAsync(
        string eventType, string? action, JsonElement payload)
    {
        if (_taskQueue is null && _platformTasks is null)
        {
            _logger.LogDebug(
                "Webhook event {Event} (action={Action}) skipped: no task queue registered",
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

        Guid taskId;
        string scope;
        if (tenantId is Guid tid && tid != Guid.Empty && _taskQueue is not null)
        {
            var task = await _taskQueue.EnqueueAsync(
                type: taskType,
                payloadJson: payload.GetRawText(),
                installationId: installationId,
                tenantIdOverride: tid);
            taskId = task.Id;
            scope = "tenant";
        }
        else if (_platformTasks is not null)
        {
            // Orphan webhook (no installation→tenant mapping yet) OR the
            // per-tenant queue isn't wired. Fall back to the platform
            // queue — handlers can decide at dispatch time whether to
            // process or drop.
            var pt = await _platformTasks.EnqueueAsync(new Tamma.Data.Entities.PlatformQueuedTask
            {
                Type = taskType,
                TenantId = tenantId,
                InstallationId = installationId,
                Payload = payload.GetRawText(),
            });
            taskId = pt.Id;
            scope = "platform";
        }
        else
        {
            // _taskQueue is null and we'd want a tenant-scope enqueue —
            // there's no orphan-tolerant fallback. Skip.
            _logger.LogDebug(
                "Webhook event {Event} (action={Action}) skipped: tenant queue unavailable",
                Logging.LogSanitizer.Clean(eventType), Logging.LogSanitizer.Clean(action));
            return new WebhookResult(eventType, action, Skipped: true);
        }

        _logger.LogInformation(
            "Webhook {Event} (action={Action}) queued as task {TaskId} (installation={InstallationId}, tenant={TenantId}, scope={Scope})",
            Logging.LogSanitizer.Clean(eventType), Logging.LogSanitizer.Clean(action),
            taskId, installationId, tenantId, scope);

        return new WebhookResult(eventType, action, Skipped: false, TaskId: taskId);
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
