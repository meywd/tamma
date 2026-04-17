using System.Text.Json;
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
    private readonly IInstallationRepository _installations;
    private readonly IEventRepository _events;
    private readonly ITenantRepository _tenants;
    private readonly IUserRepository _users;
    private readonly ITaskQueue? _taskQueue;
    private readonly ILogger<InstallationRouterService> _logger;

    public InstallationRouterService(
        IInstallationRepository installations,
        IEventRepository events,
        ITenantRepository tenants,
        IUserRepository users,
        ILogger<InstallationRouterService> logger,
        ITaskQueue? taskQueue = null)
    {
        _installations = installations;
        _events = events;
        _tenants = tenants;
        _users = users;
        _taskQueue = taskQueue;
        _logger = logger;
    }

    // ─── OAuth callback ─────────────────────────────────────────────────────

    public async Task<CallbackResult> HandleCallbackAsync(
        long installationId,
        int? setupActionId,
        Guid callingUserId)
    {
        var user = await _users.GetByIdAsync(callingUserId);
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

        var tenant = await _tenants.GetByIdAsync(user.TenantId.Value);
        if (tenant is null)
        {
            _logger.LogWarning(
                "Install callback rejected: tenant {TenantId} not found for user {UserId}",
                user.TenantId, callingUserId);
            return new CallbackResult(false, null, installationId, null, "tenant_not_found");
        }

        var existing = await _installations.GetByInstallationIdAsync(installationId);
        GitHubInstallation stored;

        if (existing is null)
        {
            stored = await _installations.CreateAsync(new GitHubInstallation
            {
                InstallationId = installationId,
                AccountLogin = user.GitHubLogin ?? tenant.Slug,
                AccountType = "User",
                AppId = 0,
                TenantId = tenant.Id
            });
        }
        else
        {
            existing.TenantId = tenant.Id;
            stored = await _installations.UpsertAsync(existing);
        }

        await EmitEventAsync(
            "INSTALLATION.LINKED.SUCCESS",
            tenant.Id,
            new Dictionary<string, object?>
            {
                ["installationId"] = installationId,
                ["tenantId"] = tenant.Id,
                ["userId"] = callingUserId,
                ["setupAction"] = setupActionId
            });

        _logger.LogInformation(
            "Linked GitHub installation {InstallationId} to tenant {TenantId} (user {UserId})",
            installationId, tenant.Id, callingUserId);

        return new CallbackResult(true, stored.Id, installationId, tenant.Id, null);
    }

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
        Guid? tenantId = null;
        if (installationId is not null)
        {
            var install = await _installations.GetByInstallationIdAsync(installationId.Value);
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
                await _installations.SoftDeleteAsync(installationId.Value);
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
                await EmitEventAsync(
                    "INSTALLATION.SUSPENDED.SUCCESS",
                    null,
                    new Dictionary<string, object?> { ["installationId"] = installationId });
                return new WebhookResult("installation", action, Skipped: false);

            case "unsuspend":
                await _installations.SetSuspendedAsync(installationId.Value, false);
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
