using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Data;

namespace Tamma.Activities.TenantLifecycle;

/// <summary>
/// Step 1 of <c>CreateTenantWorkflow</c>. Marks the tenant row as
/// <c>Status='provisioning'</c> in the control plane. Idempotent — when
/// the tenant is already <c>active</c> the activity is a no-op so a
/// double-fired <c>TENANT.PROVISIONING_REQUESTED</c> trigger doesn't
/// re-run the workflow against a healthy tenant.
///
/// <para>Uses <see cref="IDbContextFactory{ControlPlaneDbContext}"/> rather
/// than the request-scoped context so the activity works on the Elsa
/// background runtime where there is no per-request scope.</para>
/// </summary>
[Activity(
    "Tamma.TenantLifecycle",
    "Mark Tenant Provisioning",
    "Set tenants.Status='provisioning' (idempotent; no-op when already active).",
    Kind = ActivityKind.Task)]
public sealed class MarkProvisioningActivity : TenantLifecycleActivity
{
    public override string StepName => "mark-provisioning";

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
                $"MarkProvisioning: tenant {tenantId} not found in control plane.");

        var current = (string?)db.Entry(tenant).Property("Status").CurrentValue;

        if (string.Equals(current, "active", StringComparison.OrdinalIgnoreCase))
        {
            Logger?.LogInformation(
                "tenant.lifecycle.mark_provisioning skipped tenantId={TenantId} reason=already_active",
                tenantId);
            // Signal upstream so the workflow can short-circuit.
            context.WorkflowExecutionContext.Properties["tenant.skip_provision"] = true;
            return;
        }

        if (string.Equals(current, "provisioning", StringComparison.OrdinalIgnoreCase))
        {
            // Already in the right state — proceed but don't write.
            return;
        }

        db.Entry(tenant).Property("Status").CurrentValue = "provisioning";
        tenant.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(context.CancellationToken);
    }
}
