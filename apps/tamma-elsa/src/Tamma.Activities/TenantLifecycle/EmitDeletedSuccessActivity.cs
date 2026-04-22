using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Data;
using Tamma.Data.Abstractions;

namespace Tamma.Activities.TenantLifecycle;

/// <summary>
/// Step 5 of <c>DeleteTenantWorkflow</c>. Marks the tenant row as
/// soft-deleted (sets <c>DeletedAt = now()</c>, flips
/// <c>Status='deleted'</c>, nulls the encrypted connection string +
/// KEK slot) and emits the terminal <c>TENANT.DELETED.SUCCESS</c> event
/// via <see cref="IPlatformEventPublisher"/>.
///
/// <para>The CP row stays around for audit; deletion is logical. The
/// tombstone row is what downstream analytics/dashboard rely on to show
/// "deleted at" markers (Story 28-11).</para>
/// </summary>
[Activity(
    "Tamma.TenantLifecycle",
    "Emit Deleted Success",
    "Soft-delete the tenant row and emit TENANT.DELETED.SUCCESS.",
    Kind = ActivityKind.Task)]
public sealed class EmitDeletedSuccessActivity : TenantLifecycleActivity
{
    public override string StepName => "emit-deleted-success";

    // The terminal step does its own bespoke event emission below; suppress
    // the generic STEP_* envelope so we don't double-stamp the timeline.
    protected override bool EmitStepEvents => false;

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
                $"EmitDeletedSuccess: tenant {tenantId} not found in CP.");

        if (tenant.DeletedAt is null)
        {
            tenant.DeletedAt = DateTime.UtcNow;
            tenant.UpdatedAt = DateTime.UtcNow;
        }
        db.Entry(tenant).Property("Status").CurrentValue = "deleted";
        db.Entry(tenant).Property("EncryptedConnectionString").CurrentValue = (byte[]?)null;
        db.Entry(tenant).Property("KekVersion").CurrentValue = (int?)null;
        await db.SaveChangesAsync(context.CancellationToken);

        var publisher = context.GetRequiredService<IPlatformEventPublisher>();
        await publisher.AppendAndPublishAsync(
            TenantLifecycleEvents.BuildEvent(
                TenantLifecycleEvents.DeletedSuccess,
                tenantId,
                data: new Dictionary<string, object?>
                {
                    ["deletedAt"] = DateTime.UtcNow,
                }),
            context.CancellationToken);

        Logger?.LogInformation(
            "tenant.lifecycle.deleted_success completed tenantId={TenantId}",
            tenantId);
    }
}
