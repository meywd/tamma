using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Data;
using Tamma.Data.Abstractions;

namespace Tamma.Activities.TenantLifecycle;

/// <summary>
/// Step 8 of <c>CreateTenantWorkflow</c>. Flips the tenant row to
/// <c>Status='active'</c> and emits the terminal
/// <c>TENANT.CREATED.SUCCESS</c> + <c>TENANT.PROVISIONED.SUCCESS</c>
/// events via <see cref="IPlatformEventPublisher"/>. Two type names are
/// emitted because the user task asks for the former and Doc 03 §2.3
/// names the latter — both are aliases. Subscribers reading either name
/// receive the event; the dedup index makes a replay safely no-op.
///
/// <para>Idempotent on the status update via the
/// <c>WHERE Status = 'provisioning'</c> predicate; redundant runs flip
/// nothing.</para>
/// </summary>
[Activity(
    "Tamma.TenantLifecycle",
    "Mark Tenant Active",
    "UPDATE tenants SET Status='active' and emit TENANT.CREATED.SUCCESS.",
    Kind = ActivityKind.Task)]
public sealed class MarkTenantActiveActivity : TenantLifecycleActivity
{
    public override string StepName => "mark-active";

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
                $"MarkActive: tenant {tenantId} not found in CP.");

        var current = (string?)db.Entry(tenant).Property("Status").CurrentValue;
        // PR #329 review: enforce the documented `WHERE Status='provisioning'`
        // guard. Two legitimate cases reach here:
        //   1. Status == 'provisioning' — happy path, flip to 'active'.
        //   2. Status == 'active' — replay (workflow re-runs after the same
        //      activity already succeeded). No-op; events still emit so the
        //      step-dedup index swallows the duplicate row.
        // Anything else (e.g. 'failed', 'deleted', 'suspended') is an
        // accidental re-activation we MUST refuse — flipping a deleted or
        // suspended tenant to 'active' on workflow replay would silently
        // resurrect the row.
        if (string.Equals(current, "provisioning", StringComparison.OrdinalIgnoreCase))
        {
            db.Entry(tenant).Property("Status").CurrentValue = "active";
            tenant.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(context.CancellationToken);
        }
        else if (!string.Equals(current, "active", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"MarkActive: refusing to flip tenant {tenantId} to 'active' "
                + $"from unexpected status '{current ?? "(null)"}'. Expected "
                + "'provisioning' (happy path) or 'active' (replay).");
        }

        var publisher = context.GetRequiredService<IPlatformEventPublisher>();

        // Emit both names — see class doc-comment.
        await publisher.AppendAndPublishAsync(
            TenantLifecycleEvents.BuildEvent(
                TenantLifecycleEvents.CreatedSuccess,
                tenantId,
                data: new Dictionary<string, object?>
                {
                    ["activatedAt"] = DateTime.UtcNow,
                }),
            context.CancellationToken);

        await publisher.AppendAndPublishAsync(
            TenantLifecycleEvents.BuildEvent(
                TenantLifecycleEvents.ProvisionedSuccess,
                tenantId,
                data: new Dictionary<string, object?>
                {
                    ["activatedAt"] = DateTime.UtcNow,
                }),
            context.CancellationToken);

        Logger?.LogInformation(
            "tenant.lifecycle.mark_active completed tenantId={TenantId}",
            tenantId);
    }
}
