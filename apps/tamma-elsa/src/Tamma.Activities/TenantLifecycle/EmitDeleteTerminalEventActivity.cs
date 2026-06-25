using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;
using Tamma.Data;
using Tamma.Data.Abstractions;

namespace Tamma.Activities.TenantLifecycle;

/// <summary>
/// Story 28-5 item #3 — terminal step of the rebuilt
/// <c>DeleteTenantWorkflow</c>. The delete analogue of
/// <see cref="EmitCleanupTerminalEventActivity"/>: it reads the accumulated
/// per-step state and emits EXACTLY ONE terminal event.
///
/// <list type="bullet">
///   <item><description><b>All destructive steps succeeded</b> →
///     soft-delete the CP row (<c>DeletedAt</c>, <c>Status='deleted'</c>,
///     null the encrypted connection string), release the unified-tenancy
///     placement (pool <c>TenantCount</c> decrement + <c>DatabaseId</c>/
///     <c>SchemaName</c> nulled) in the same <c>SaveChanges</c>, and emit
///     <c>TENANT.DELETED.SUCCESS</c>. This folds in what
///     <see cref="EmitDeletedSuccessActivity"/> used to do as a separate
///     mid-sequence step.</description></item>
///   <item><description><b>Any step failed</b> → emit
///     <c>TENANT.DELETE.FAILED</c> with a <c>failedSteps</c> array + the
///     redacted per-step detail, and set
///     <c>tenants.ProvisioningState='requires_manual_cleanup'</c> so an
///     operator sees the row needs intervention. The row is left
///     recoverable via <c>POST /cleanup</c>; it is NOT soft-deleted.</description></item>
/// </list>
///
/// <para><b>Why fold the soft-delete in here</b>: the previous shape
/// soft-deleted in a mid-sequence <see cref="EmitDeletedSuccessActivity"/>
/// and a later throw would leave a soft-deleted row with a still-failed
/// teardown. Folding the soft-delete into the terminal means the row is
/// only marked deleted when every destructive step actually succeeded —
/// fail-closed.</para>
///
/// <para><b>Single terminal event invariant</b>: only this activity emits
/// a terminal event; the per-step activities emit
/// <c>TENANT.DELETE.STEP_*</c> markers (step-scoped, not terminal).</para>
/// </summary>
[Activity(
    "Tamma.TenantLifecycle",
    "Emit Delete Terminal Event",
    "Read accumulated step state; on full success soft-delete row + release placement + emit TENANT.DELETED.SUCCESS, else emit TENANT.DELETE.FAILED + quarantine.",
    Kind = ActivityKind.Task)]
public sealed class EmitDeleteTerminalEventActivity : TammaAsyncActivity
{
    private const int MaxSummaryChars = 1900;

    [Input(Description = "Tenant id whose delete is concluding. If unbound, reads the workflow variable 'TenantId'.")]
    public Input<Guid> TenantId { get; set; } = new(Guid.Empty);

    public override string? EventType => "TENANT.DELETE.TERMINAL";

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        Logger ??= context.GetService<ILogger<EmitDeleteTerminalEventActivity>>();

        var tenantId = ResolveTenantId(context);

        var failedSteps = CleanupWorkflowState.GetFailedSteps(context);
        var succeededSteps = CleanupWorkflowState.GetSucceededSteps(context);
        var stepDetails = CleanupWorkflowState.GetStepDetails(context);

        if (tenantId == Guid.Empty)
        {
            Logger?.LogError(
                "tenant.delete.terminal_event_skipped reason=empty_tenant_id failedSteps={FailedSteps}",
                string.Join(",", failedSteps));
            return;
        }

        var publisher = context.GetRequiredService<IPlatformEventPublisher>();

        if (failedSteps.Count == 0)
        {
            // Full success — soft-delete + release placement in ONE save,
            // then fire the terminal success event. Fail-closed: if the
            // soft-delete itself throws, we fall through to the FAILED
            // path rather than reporting a success the row doesn't reflect.
            var softDeleted = await TrySoftDeleteAndReleaseAsync(context, tenantId)
                .ConfigureAwait(false);

            if (softDeleted)
            {
                await publisher.AppendAndPublishAsync(
                    TenantLifecycleEvents.BuildEvent(
                        TenantLifecycleEvents.DeletedSuccess,
                        tenantId,
                        data: new Dictionary<string, object?>
                        {
                            ["source"] = "delete-workflow",
                            ["deletedAt"] = DateTime.UtcNow,
                            ["succeededSteps"] = succeededSteps,
                        }),
                    context.CancellationToken).ConfigureAwait(false);

                Logger?.LogInformation(
                    "tenant.delete.success tenantId={TenantId} succeededSteps={SucceededSteps}",
                    tenantId, string.Join(",", succeededSteps));
                return;
            }

            // Soft-delete failed even though every prior step succeeded —
            // attribute it as a failed terminal step and quarantine.
            var detail = $"{CleanupSteps.SoftDeleteRow}: soft-delete + placement release failed";
            var syntheticFailed = new List<string> { CleanupSteps.SoftDeleteRow };
            var syntheticDetails = new Dictionary<string, string>
            {
                [CleanupSteps.SoftDeleteRow] = detail,
            };
            await EmitFailedAsync(
                context, publisher, tenantId, syntheticFailed, succeededSteps, syntheticDetails)
                .ConfigureAwait(false);
            return;
        }

        await EmitFailedAsync(
            context, publisher, tenantId, failedSteps, succeededSteps, stepDetails)
            .ConfigureAwait(false);
    }

    private async Task EmitFailedAsync(
        ActivityExecutionContext context,
        IPlatformEventPublisher publisher,
        Guid tenantId,
        IReadOnlyList<string> failedSteps,
        IReadOnlyList<string> succeededSteps,
        IReadOnlyDictionary<string, string> stepDetails)
    {
        await QuarantineRowAsync(context, tenantId, failedSteps, stepDetails)
            .ConfigureAwait(false);

        await publisher.AppendAndPublishAsync(
            TenantLifecycleEvents.BuildEvent(
                TenantLifecycleEvents.DeleteFailed,
                tenantId,
                data: new Dictionary<string, object?>
                {
                    ["source"] = "delete-workflow",
                    ["failedSteps"] = failedSteps,
                    ["succeededSteps"] = succeededSteps,
                    ["stepDetails"] = stepDetails,
                    ["requiresManualCleanup"] = true,
                }),
            context.CancellationToken).ConfigureAwait(false);

        Logger?.LogWarning(
            "tenant.delete.partial tenantId={TenantId} failedSteps={FailedSteps} succeededSteps={SucceededSteps}",
            tenantId,
            string.Join(",", failedSteps),
            string.Join(",", succeededSteps));
    }

    /// <summary>
    /// Soft-delete the CP row + release the placement in a single
    /// <c>SaveChanges</c>. Returns false (without throwing) on any
    /// failure so the caller can route to the FAILED terminal — the
    /// terminal event must always fire.
    /// </summary>
    private async Task<bool> TrySoftDeleteAndReleaseAsync(
        ActivityExecutionContext context,
        Guid tenantId)
    {
        try
        {
            var factory = context.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
            await using var db = await factory
                .CreateDbContextAsync(context.CancellationToken)
                .ConfigureAwait(false);

            var tenant = await db.Tenants
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.Id == tenantId, context.CancellationToken)
                .ConfigureAwait(false);
            if (tenant is null)
            {
                Logger?.LogWarning(
                    "tenant.delete.terminal_row_not_found tenantId={TenantId}", tenantId);
                return false;
            }

            if (tenant.DeletedAt is null)
                tenant.DeletedAt = DateTime.UtcNow;
            tenant.UpdatedAt = DateTime.UtcNow;
            db.Entry(tenant).Property("Status").CurrentValue = "deleted";
            db.Entry(tenant).Property("EncryptedConnectionString").CurrentValue = (byte[]?)null;
            // KekVersion is smallint NOT NULL DEFAULT 1 — clearing is a no-op.

            tenant.ProvisioningState = "none";
            tenant.ProvisioningUpdatedAt = DateTime.UtcNow;

            await TenantPlacementShadow.ReleaseAsync(
                db, tenant, Logger, context.CancellationToken).ConfigureAwait(false);

            await db.SaveChangesAsync(context.CancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Logger?.LogError(
                ex, "tenant.delete.soft_delete_failed tenantId={TenantId}", tenantId);
            return false;
        }
    }

    /// <summary>
    /// Flag the row for manual cleanup on partial failure — best-effort,
    /// like the cleanup sibling. Even if the row stamp fails the terminal
    /// FAILED event still fires so the dashboard sees the run completed.
    /// </summary>
    private async Task QuarantineRowAsync(
        ActivityExecutionContext context,
        Guid tenantId,
        IReadOnlyList<string> failedSteps,
        IReadOnlyDictionary<string, string> stepDetails)
    {
        try
        {
            var factory = context.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
            await using var db = await factory
                .CreateDbContextAsync(context.CancellationToken)
                .ConfigureAwait(false);

            var tenant = await db.Tenants
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.Id == tenantId, context.CancellationToken)
                .ConfigureAwait(false);
            if (tenant is null)
            {
                Logger?.LogWarning(
                    "tenant.delete.terminal_row_not_found tenantId={TenantId}", tenantId);
                return;
            }

            tenant.UpdatedAt = DateTime.UtcNow;
            tenant.ProvisioningState = "requires_manual_cleanup";
            tenant.ProvisioningDetail = BuildFailureSummary(failedSteps, stepDetails);
            tenant.ProvisioningUpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger?.LogError(
                ex, "tenant.delete.terminal_row_update_failed tenantId={TenantId}", tenantId);
        }
    }

    private Guid ResolveTenantId(ActivityExecutionContext context)
    {
        var bound = TenantId.Get(context);
        if (bound != Guid.Empty) return bound;
        var raw = context.GetVariable<object?>("TenantId");
        return raw switch
        {
            Guid g => g,
            string s when Guid.TryParse(s, out var p) => p,
            _ => Guid.Empty,
        };
    }

    private static string BuildFailureSummary(
        IReadOnlyList<string> failedSteps,
        IReadOnlyDictionary<string, string> details)
    {
        var summary = $"Delete partial — {failedSteps.Count} step(s) failed: " +
            string.Join("; ",
                failedSteps.Select(s => $"{s}: {(details.TryGetValue(s, out var d) ? d : "(no detail)")}"));
        return summary.Length > MaxSummaryChars ? summary[..MaxSummaryChars] : summary;
    }

    /// <summary>Exposed for test callers — same truncation contract used by
    /// the activity at runtime.</summary>
    public static string BuildFailureSummaryForTesting(
        IReadOnlyList<string> failedSteps,
        IReadOnlyDictionary<string, string> details) =>
        BuildFailureSummary(failedSteps, details);
}
