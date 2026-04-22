using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Data.Abstractions;

namespace Tamma.Activities.TenantLifecycle;

/// <summary>
/// Step 7 of <c>CreateTenantWorkflow</c>. Eagerly resolves the tenant's
/// data source via <see cref="ITenantConnectionResolver"/> so the LRU
/// pool entry is already warm when the first tenant request lands.
/// Idempotent; the resolver's miss-path semantics make redundant calls a
/// fast cache hit.
///
/// <para>Failure handling: a warm-pool failure here is non-fatal — the
/// pool will be lazily built on first request anyway. The activity
/// classifies the failure as a soft warning rather than throwing, so the
/// workflow continues to the welcome-email + mark-active steps. The
/// resolver's own metrics record the cold open later.</para>
/// </summary>
[Activity(
    "Tamma.TenantLifecycle",
    "Warm Tenant Connection Pool",
    "Eagerly build the LRU pool entry so the first user request isn't cold.",
    Kind = ActivityKind.Task)]
public sealed class WarmTenantPoolActivity : TenantLifecycleActivity
{
    public override string StepName => "warm-pool";

    protected override async Task ProcessAsync(
        ActivityExecutionContext context,
        Guid tenantId,
        int attempt)
    {
        var resolver = context.GetRequiredService<ITenantConnectionResolver>();
        try
        {
            await resolver.GetDataSourceAsync(tenantId, context.CancellationToken);
            Logger?.LogInformation(
                "tenant.lifecycle.warm_pool ok tenantId={TenantId}",
                tenantId);
        }
        catch (Exception ex)
        {
            // Non-fatal — the pool builds lazily on first request. Record
            // a warning and continue.
            Logger?.LogWarning(ex,
                "tenant.lifecycle.warm_pool failed tenantId={TenantId} continuing=true",
                tenantId);
        }
    }
}
