using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Data.Abstractions;

namespace Tamma.Activities.TenantLifecycle;

/// <summary>
/// Step 2 of <c>DeleteTenantWorkflow</c>. Evicts the tenant from the LRU
/// pool cache. Required before <see cref="DropTenantDatabaseActivity"/>
/// because <c>DROP DATABASE … WITH (FORCE)</c> kills the active backends
/// in the pool but leaves the cached <see cref="Npgsql.NpgsqlDataSource"/>
/// holding stale connections — the resolver needs to forget the tenant
/// so subsequent requests don't try to reuse the dropped pool.
///
/// <para>Idempotent — eviction of a non-cached tenant is a no-op.</para>
/// </summary>
[Activity(
    "Tamma.TenantLifecycle",
    "Evict Tenant Pool",
    "ITenantConnectionResolver.EvictAsync(tenantId) so the pool is forgotten.",
    Kind = ActivityKind.Task)]
public sealed class EvictTenantPoolActivity : TenantLifecycleActivity
{
    public override string StepName => "evict-pool";

    protected override async Task ProcessAsync(
        ActivityExecutionContext context,
        Guid tenantId,
        int attempt)
    {
        var resolver = context.GetRequiredService<ITenantConnectionResolver>();
        await resolver.EvictAsync(tenantId, context.CancellationToken);
        Logger?.LogInformation(
            "tenant.lifecycle.evict_pool completed tenantId={TenantId}",
            tenantId);
    }
}
