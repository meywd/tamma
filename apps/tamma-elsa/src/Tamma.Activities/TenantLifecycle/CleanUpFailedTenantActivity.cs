using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Security;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Pooling;

namespace Tamma.Activities.TenantLifecycle;

/// <summary>
/// Story 28-5 AC7 — operator-triggered "best effort" cleanup for a
/// tenant left in a half-provisioned or half-deleted state. Runs the
/// physical teardown sequence (evict pool → drop database → drop role
/// → soft-delete CP row) with each step's failure isolated: a failed
/// step logs <c>TENANT.DELETE.STEP_FAILED</c> + continues; the
/// activity emits a single terminal event at the end based on whether
/// any step failed.
///
/// <para>Triggered by <c>POST /api/admin/tenants/{id}/cleanup</c> via
/// the global-Elsa <see cref="WorkflowsCleanUpFailedTenant"/> workflow.
/// Unlike <c>DeleteTenantWorkflow</c> which requires
/// <c>Status='deleting'</c> and aborts on the first step failure (so
/// retries can resume cleanly), this activity assumes the tenant is in
/// a damaged state and pushes through every step regardless of
/// individual failures.</para>
///
/// <para><b>Terminal outcomes</b>:
/// <list type="bullet">
///   <item><description>All steps succeed → emits
///     <c>TENANT.DELETED.SUCCESS</c>, sets <c>Status='deleted'</c> +
///     <c>DeletedAt=now()</c>.</description></item>
///   <item><description>Any step fails →
///     emits <c>TENANT.DELETE.FAILED</c> with a
///     <c>failedSteps</c> array, sets
///     <c>ProvisioningState='requires_manual_cleanup'</c> +
///     <c>ProvisioningDetail=&lt;failure summary&gt;</c>. Operator must
///     intervene before another cleanup attempt is meaningful.</description></item>
/// </list>
/// </para>
///
/// <para><b>Idempotency</b>: each underlying primitive
/// (<see cref="EvictTenantPoolActivity"/>,
/// <see cref="DropTenantDatabaseActivity"/>,
/// <see cref="DropTenantRoleActivity"/>) is already idempotent (probes
/// before destructive ops). Re-running this activity after a
/// half-success is safe — already-completed steps short-circuit, the
/// remaining steps make further progress.</para>
/// </summary>
[Activity(
    "Tamma.TenantLifecycle",
    "Clean Up Failed Tenant",
    "Best-effort teardown for a tenant in a damaged state. Runs every step regardless of individual failures.",
    Kind = ActivityKind.Task)]
public sealed class CleanUpFailedTenantActivity : Elsa.Workflows.Activity
{
    [Input(Description = "Tenant id to clean up.")]
    public Input<Guid> TenantId { get; set; } = default!;

    [Input(Description = "Optional operator note attached to the terminal event.")]
    public Input<string?> Note { get; set; } = new(default(string));

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var tenantId = TenantId.Get(context);
        if (tenantId == Guid.Empty)
            throw new InvalidOperationException(
                "CleanUpFailedTenantActivity requires a non-empty tenant id.");

        var note = Note.Get(context);
        var logger = context.GetService<ILogger<CleanUpFailedTenantActivity>>();
        var publisher = context.GetRequiredService<IPlatformEventPublisher>();
        // M1 — IErrorRedactor scrubs bearer tokens, internal URLs, and
        // stack traces from any exception message that crosses the
        // long-lived storage boundary (ProvisioningDetail column +
        // platform_events.data JSONB). Resolved from DI so the redactor
        // can be replaced in tests.
        var redactor = context.GetService<IErrorRedactor>();
        var failedSteps = new List<string>();
        var stepDetails = new Dictionary<string, CleanupFailureRecord>();

        // The four cleanup steps. Each step is wrapped in a try-catch
        // so a downstream failure doesn't abort the cleanup — the
        // operator wants every step attempted, then a single terminal
        // event reporting the overall outcome.
        async Task RunStep(string step, Func<Task> work)
        {
            try
            {
                await publisher.AppendAndPublishAsync(
                    TenantLifecycleEvents.BuildEvent(
                        TenantLifecycleEvents.DeleteStepStarted,
                        tenantId,
                        step: step,
                        attempt: 1),
                    context.CancellationToken).ConfigureAwait(false);

                await work().ConfigureAwait(false);

                await publisher.AppendAndPublishAsync(
                    TenantLifecycleEvents.BuildEvent(
                        TenantLifecycleEvents.DeleteStepCompleted,
                        tenantId,
                        step: step,
                        attempt: 1),
                    context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failedSteps.Add(step);
                // M1 — store the structured failure code + a short,
                // redacted snippet of the message in long-lived storage.
                // Full text (with stack) goes to ILogger only, where
                // log retention is bounded and PII rules apply.
                var record = ClassifyFailure(step, ex, redactor);
                stepDetails[step] = record;
                logger?.LogWarning(
                    ex,
                    "tenant.cleanup.step_failed step={Step} tenantId={TenantId} failureCode={FailureCode}",
                    step,
                    tenantId,
                    record.FailureCode);
                try
                {
                    await publisher.AppendAndPublishAsync(
                        TenantLifecycleEvents.BuildEvent(
                            TenantLifecycleEvents.DeleteStepFailed,
                            tenantId,
                            step: step,
                            attempt: 1,
                            data: new Dictionary<string, object?>
                            {
                                ["errorType"] = ex.GetType().Name,
                                // Long-lived event store — code only +
                                // redacted message snippet (capped at
                                // 200 chars). The full ex.Message is
                                // intentionally NOT serialised here.
                                ["failureCode"] = record.FailureCode,
                                ["message"] = record.RedactedSnippet,
                            }),
                        context.CancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort event emission only — even if the
                    // event publisher is unhealthy we still want to
                    // record the step failure in the in-memory list so
                    // the terminal event captures it.
                }
            }
        }

        // ── Step 1: evict pool ──────────────────────────────────────
        await RunStep("evict-pool", async () =>
        {
            var resolver = context.GetRequiredService<ITenantConnectionResolver>();
            await resolver.EvictAsync(tenantId, context.CancellationToken)
                .ConfigureAwait(false);
        });

        // ── Step 2: drop database (probe-before-drop) ───────────────
        await RunStep("drop-tenant-db", async () =>
        {
            var admin = context.GetRequiredService<ITenantAdminConnection>();
            var dbName = TenantNaming.DatabaseName(tenantId);
            if (!await admin.DatabaseExistsAsync(dbName, context.CancellationToken)
                .ConfigureAwait(false))
                return;
            // DROP DATABASE WITH (FORCE) terminates active backends so
            // an in-flight migration / hung worker doesn't block teardown.
            await admin.ExecuteAsync(
                $"DROP DATABASE \"{dbName}\" WITH (FORCE)",
                context.CancellationToken).ConfigureAwait(false);
        });

        // ── Step 3: drop role ───────────────────────────────────────
        await RunStep("drop-tenant-role", async () =>
        {
            var admin = context.GetRequiredService<ITenantAdminConnection>();
            var roleName = TenantNaming.RoleName(tenantId);
            if (!await admin.RoleExistsAsync(roleName, context.CancellationToken)
                .ConfigureAwait(false))
                return;
            // DROP OWNED BY first — strips any residual grants on tables
            // the role still owns from a half-completed provisioning.
            await admin.ExecuteAsync(
                $"DROP OWNED BY \"{roleName}\"",
                context.CancellationToken).ConfigureAwait(false);
            await admin.ExecuteAsync(
                $"DROP ROLE \"{roleName}\"",
                context.CancellationToken).ConfigureAwait(false);
        });

        // ── Step 4: terminal CP-row updates + terminal event ────────
        var factory = context.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
        await using (var db = await factory.CreateDbContextAsync(context.CancellationToken))
        {
            var tenant = await db.Tenants
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.Id == tenantId, context.CancellationToken);
            if (tenant is not null)
            {
                if (failedSteps.Count == 0)
                {
                    // Full cleanup success — soft-delete the row
                    // (matching EmitDeletedSuccessActivity's terminal
                    // contract).
                    if (tenant.DeletedAt is null)
                        tenant.DeletedAt = DateTime.UtcNow;
                    db.Entry(tenant).Property("Status").CurrentValue = "deleted";
                    db.Entry(tenant).Property("EncryptedConnectionString").CurrentValue = (byte[]?)null;
                    db.Entry(tenant).Property("KekVersion").CurrentValue = (int?)null;
                    tenant.ProvisioningState = "none";
                    tenant.ProvisioningDetail = note ?? "Cleaned up via /api/admin/tenants/{id}/cleanup.";
                    tenant.ProvisioningUpdatedAt = DateTime.UtcNow;
                }
                else
                {
                    // Partial cleanup — surface the operator-actionable
                    // state via ProvisioningState. Status stays at its
                    // current value (could be deleting / provisioning /
                    // failed / etc.) so an operator looking at the admin
                    // UX still sees the lifecycle state alongside the
                    // cleanup signal.
                    tenant.ProvisioningState = "requires_manual_cleanup";
                    tenant.ProvisioningDetail = BuildFailureSummary(failedSteps, stepDetails);
                    tenant.ProvisioningUpdatedAt = DateTime.UtcNow;
                }
                tenant.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(context.CancellationToken)
                    .ConfigureAwait(false);
            }
        }

        // ── Terminal event ──────────────────────────────────────────
        if (failedSteps.Count == 0)
        {
            await publisher.AppendAndPublishAsync(
                TenantLifecycleEvents.BuildEvent(
                    TenantLifecycleEvents.DeletedSuccess,
                    tenantId,
                    data: new Dictionary<string, object?>
                    {
                        ["source"] = "cleanup-workflow",
                        ["note"] = note,
                    }),
                context.CancellationToken).ConfigureAwait(false);

            logger?.LogInformation(
                "tenant.cleanup.success tenantId={TenantId}",
                tenantId);
        }
        else
        {
            // M1 — project the redacted records into a plain dictionary
            // before serialising. Keeps the long-lived event payload to
            // {step → {failureCode, message}} only.
            var stepDetailsForEvent = stepDetails.ToDictionary(
                kv => kv.Key,
                kv => (object?)new Dictionary<string, string>
                {
                    ["failureCode"] = kv.Value.FailureCode,
                    ["message"] = kv.Value.RedactedSnippet,
                });
            await publisher.AppendAndPublishAsync(
                TenantLifecycleEvents.BuildEvent(
                    "TENANT.DELETE.FAILED",
                    tenantId,
                    data: new Dictionary<string, object?>
                    {
                        ["source"] = "cleanup-workflow",
                        ["failedSteps"] = failedSteps,
                        ["stepDetails"] = stepDetailsForEvent,
                        ["note"] = note,
                        ["requiresManualCleanup"] = true,
                    }),
                context.CancellationToken).ConfigureAwait(false);

            logger?.LogWarning(
                "tenant.cleanup.partial tenantId={TenantId} failedSteps={FailedSteps}",
                tenantId,
                string.Join(",", failedSteps));
        }
    }

    /// <summary>
    /// Build the operator-readable summary that lands in
    /// <c>tenants.ProvisioningDetail</c>. M1 — uses the structured
    /// failure code + redacted snippet, never the raw exception message.
    /// Capped at 1900 chars to fit typical column limits with headroom.
    /// </summary>
    private static string BuildFailureSummary(
        IReadOnlyList<string> failedSteps,
        IReadOnlyDictionary<string, CleanupFailureRecord> details)
    {
        var summary = $"Cleanup partial — {failedSteps.Count} step(s) failed: " +
            string.Join("; ",
                failedSteps.Select(s =>
                {
                    if (!details.TryGetValue(s, out var rec))
                        return $"{s}: (no detail)";
                    return $"{s}: {rec.FailureCode} — {rec.RedactedSnippet}";
                }));
        return summary.Length > 1900 ? summary[..1900] : summary;
    }

    /// <summary>
    /// M1 — Structured failure record. <see cref="FailureCode"/> is a
    /// short, fixed-vocabulary identifier suitable for long-lived
    /// storage + dashboards; <see cref="RedactedSnippet"/> is at most
    /// 200 chars of redacted exception text. Raw <see cref="System.Exception.Message"/>
    /// is intentionally NOT carried — full text goes to
    /// <see cref="ILogger"/> only.
    /// </summary>
    internal sealed record CleanupFailureRecord(string FailureCode, string RedactedSnippet);

    /// <summary>
    /// Maps an exception thrown by a cleanup step to a structured
    /// failure code. Codes are stable across releases so dashboards +
    /// alerts can group on them. Order of checks: step-specific failure
    /// (DROP DATABASE / DROP ROLE / network) first, then generic.
    /// </summary>
    internal static CleanupFailureRecord ClassifyFailure(
        string step,
        Exception ex,
        IErrorRedactor? redactor)
    {
        var typeName = ex.GetType().Name;
        var rawMessage = ex.Message ?? string.Empty;
        // Trim the redacted message to a bounded snippet so the long-
        // lived store doesn't accumulate verbose Postgres / network
        // diagnostics. The full text remains in ILogger.
        var redacted = redactor?.Redact(rawMessage) ?? rawMessage;
        var snippet = redacted.Length > 200 ? redacted[..200] : redacted;

        // Step-specific classifiers — these dominate the operator UX
        // because the cleanup workflow has well-known failure shapes.
        var code = step switch
        {
            "evict-pool" => "evict_pool_failed",
            "drop-tenant-db" => ClassifyDatabaseFailure(typeName, rawMessage),
            "drop-tenant-role" => ClassifyRoleFailure(typeName, rawMessage),
            _ => ClassifyGeneric(typeName, rawMessage),
        };
        return new CleanupFailureRecord(code, snippet);
    }

    private static string ClassifyDatabaseFailure(string typeName, string rawMessage)
    {
        if (LooksLikeNetwork(typeName, rawMessage))
            return "network_error";
        if (LooksLikeAuth(rawMessage))
            return "permission_denied";
        return "drop_database_failed";
    }

    private static string ClassifyRoleFailure(string typeName, string rawMessage)
    {
        if (LooksLikeNetwork(typeName, rawMessage))
            return "network_error";
        if (LooksLikeAuth(rawMessage))
            return "permission_denied";
        return "drop_role_failed";
    }

    private static string ClassifyGeneric(string typeName, string rawMessage)
    {
        if (LooksLikeNetwork(typeName, rawMessage))
            return "network_error";
        if (string.Equals(typeName, "OperationCanceledException", StringComparison.Ordinal)
            || string.Equals(typeName, "TaskCanceledException", StringComparison.Ordinal))
            return "cancelled";
        return "step_failed";
    }

    private static bool LooksLikeNetwork(string typeName, string rawMessage)
    {
        if (string.Equals(typeName, "TimeoutException", StringComparison.Ordinal)
            || string.Equals(typeName, "SocketException", StringComparison.Ordinal)
            || string.Equals(typeName, "IOException", StringComparison.Ordinal))
            return true;
        return rawMessage.Contains("connection", StringComparison.OrdinalIgnoreCase)
            && (rawMessage.Contains("refused", StringComparison.OrdinalIgnoreCase)
                || rawMessage.Contains("reset", StringComparison.OrdinalIgnoreCase)
                || rawMessage.Contains("timeout", StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeAuth(string rawMessage)
    {
        return rawMessage.Contains("permission", StringComparison.OrdinalIgnoreCase)
            || rawMessage.Contains("must be owner", StringComparison.OrdinalIgnoreCase)
            || rawMessage.Contains("not allowed", StringComparison.OrdinalIgnoreCase);
    }
}
