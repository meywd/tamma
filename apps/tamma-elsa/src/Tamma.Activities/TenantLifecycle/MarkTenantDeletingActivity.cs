using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Data;
using Tamma.Data.Abstractions;

namespace Tamma.Activities.TenantLifecycle;

/// <summary>
/// Step 1 of <c>DeleteTenantWorkflow</c>. Soft-marks the tenant
/// (<c>Status='deleting'</c>, <c>DeleteRequestedAt=now()</c>) and emits
/// <c>TENANT.DELETE.REQUESTED</c>. Idempotent — a redundant call is a
/// fast no-op.
///
/// <para>The cooling-off window (Doc 04 §6.5) is honoured by the workflow
/// scheduling a delay between this step and the destructive
/// <see cref="DropTenantDatabaseActivity"/>; the activity itself just
/// flips the row.</para>
/// </summary>
[Activity(
    "Tamma.TenantLifecycle",
    "Mark Tenant Deleting",
    "Set tenants.Status='deleting' and emit TENANT.DELETE.REQUESTED.",
    Kind = ActivityKind.Task)]
public sealed class MarkTenantDeletingActivity : TenantLifecycleActivity
{
    public override string StepName => "mark-deleting";

    protected override async Task ProcessAsync(
        ActivityExecutionContext context,
        Guid tenantId,
        int attempt)
    {
        var factory = context.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
        await using var db = await factory.CreateDbContextAsync(context.CancellationToken);

        var tenant = await db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tenantId, context.CancellationToken)
            ?? throw new InvalidOperationException(
                $"MarkDeleting: tenant {tenantId} not found in CP.");

        var current = (string?)db.Entry(tenant).Property("Status").CurrentValue;
        if (string.Equals(current, "deleting", StringComparison.OrdinalIgnoreCase))
        {
            // No-op
        }
        else
        {
            db.Entry(tenant).Property("Status").CurrentValue = "deleting";
            db.Entry(tenant).Property("DeleteRequestedAt").CurrentValue = DateTime.UtcNow;
            tenant.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(context.CancellationToken);
        }

        var publisher = context.GetRequiredService<IPlatformEventPublisher>();
        await publisher.AppendAndPublishAsync(
            TenantLifecycleEvents.BuildEvent(
                TenantLifecycleEvents.DeleteRequested,
                tenantId,
                data: new Dictionary<string, object?>
                {
                    ["requestedAt"] = DateTime.UtcNow,
                }),
            context.CancellationToken);

        Logger?.LogInformation(
            "tenant.lifecycle.mark_deleting completed tenantId={TenantId}",
            tenantId);
    }
}
