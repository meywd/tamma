using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        var failedSteps = new List<string>();
        var stepDetails = new Dictionary<string, string>();

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
                stepDetails[step] = $"{ex.GetType().Name}: {ex.Message}";
                logger?.LogWarning(
                    ex,
                    "tenant.cleanup.step_failed step={Step} tenantId={TenantId}",
                    step,
                    tenantId);
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
                                ["message"] = ex.Message,
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
            await publisher.AppendAndPublishAsync(
                TenantLifecycleEvents.BuildEvent(
                    "TENANT.DELETE.FAILED",
                    tenantId,
                    data: new Dictionary<string, object?>
                    {
                        ["source"] = "cleanup-workflow",
                        ["failedSteps"] = failedSteps,
                        ["stepDetails"] = stepDetails,
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

    private static string BuildFailureSummary(
        IReadOnlyList<string> failedSteps,
        IReadOnlyDictionary<string, string> details)
    {
        // Cap at 1900 chars to fit comfortably within typical column
        // limits + leave headroom for diagnostic prefixes.
        var summary = $"Cleanup partial — {failedSteps.Count} step(s) failed: " +
            string.Join("; ",
                failedSteps.Select(s => $"{s}: {details.GetValueOrDefault(s, "(no detail)")}"));
        return summary.Length > 1900 ? summary[..1900] : summary;
    }
}
