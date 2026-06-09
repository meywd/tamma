using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Data;

namespace Tamma.Activities.TenantLifecycle;

/// <summary>
/// H6 / Story 28-5 AC7 — fourth step of the cleanup
/// <see cref="Elsa.Workflows.Activities.Sequence"/>. Soft-deletes the CP
/// <c>tenants</c> row: stamps <c>DeletedAt = now()</c> on first run,
/// flips <c>Status = 'deleted'</c>, and nulls the encrypted connection
/// string slot + its KEK version pointer.
///
/// <para>This is deliberately split off from
/// <see cref="EmitCleanupTerminalEventActivity"/> so that:</para>
/// <list type="bullet">
///   <item><description>The row update itself is a per-step Elsa activity
///     with its own event-history boundary — i.e. Elsa can suspend /
///     replay / cancel between this step and the terminal-event step.</description></item>
///   <item><description>If the soft-delete fails (CP DB unhealthy, the
///     row was reaped by a parallel job, …) the failure is recorded
///     into the workflow's per-step state and the terminal step still
///     fires — the operator gets a single
///     <c>TENANT.DELETE.FAILED</c> with <c>failedSteps</c> including
///     <c>soft-delete-cp-row</c> rather than a half-completed cleanup
///     that leaves no audit trail.</description></item>
/// </list>
///
/// <para>Idempotent: repeated execution against an already-soft-deleted
/// row updates only the timestamps + leaves the connection-string
/// nullification intact.</para>
///
/// <para>The provisioning-state field is intentionally NOT touched here
/// — that's <see cref="EmitCleanupTerminalEventActivity"/>'s job, since
/// it depends on whether OTHER steps in the cleanup sequence
/// failed.</para>
/// </summary>
[Activity(
    "Tamma.TenantLifecycle",
    "Soft Delete Tenant Row",
    "Set Status='deleted' + DeletedAt=now() + null encrypted connection string. Continue-on-error.",
    Kind = ActivityKind.Task)]
public sealed class SoftDeleteTenantRowActivity : CleanupStepActivity
{
    public override string StepName => CleanupSteps.SoftDeleteRow;

    [Input(Description = "Optional operator note appended to ProvisioningDetail when this is the only successful path.")]
    public Input<string?> Note { get; set; } = new(default(string));

    protected override async Task DoStepAsync(
        ActivityExecutionContext context,
        Guid tenantId)
    {
        var factory = context.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
        await using var db = await factory.CreateDbContextAsync(context.CancellationToken)
            .ConfigureAwait(false);

        var tenant = await db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tenantId, context.CancellationToken)
            .ConfigureAwait(false);
        if (tenant is null)
        {
            // Treat as a step failure rather than a no-op: cleanup runs
            // against a damaged tenant, but if the CP row is gone we
            // genuinely can't soft-delete it, and the operator should
            // see this in the terminal failure summary. (The base will
            // catch the throw and record the failure code.)
            throw new InvalidOperationException(
                $"SoftDeleteTenantRow: tenant {tenantId} not found in CP.");
        }

        if (tenant.DeletedAt is null)
            tenant.DeletedAt = DateTime.UtcNow;

        db.Entry(tenant).Property("Status").CurrentValue = "deleted";
        db.Entry(tenant).Property("EncryptedConnectionString").CurrentValue = (byte[]?)null;
        // KekVersion is smallint NOT NULL DEFAULT 1 — clearing on delete is a no-op (plan 2026-06-09 §2.2).

        tenant.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(context.CancellationToken).ConfigureAwait(false);

        Logger?.LogInformation(
            "tenant.cleanup.soft_delete_row completed tenantId={TenantId}",
            tenantId);
    }
}
