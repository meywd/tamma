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
///
/// <para><b>Throws</b> on resolver failure so the surrounding
/// <c>DeleteTenantWorkflow</c> aborts cleanly. For continue-on-error
/// cleanup semantics see
/// <see cref="EvictTenantPoolForCleanupActivity"/>.</para>
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

/// <summary>
/// H6 / Story 28-5 AC7 — continue-on-error variant of
/// <see cref="EvictTenantPoolActivity"/> used by
/// <c>CleanUpFailedTenantWorkflow</c>. Functionally identical (calls
/// <c>ITenantConnectionResolver.EvictAsync</c>), but on failure the
/// activity swallows the exception, records the failure into the
/// workflow's per-step state (see <see cref="CleanupWorkflowState"/>),
/// emits <c>TENANT.DELETE.STEP_FAILED</c>, and returns normally so the
/// next sibling step in the cleanup <c>Sequence</c> still runs.
/// </summary>
[Activity(
    "Tamma.TenantLifecycle",
    "Evict Tenant Pool (Cleanup)",
    "Continue-on-error variant — never throws; records failure to workflow state.",
    Kind = ActivityKind.Task)]
public sealed class EvictTenantPoolForCleanupActivity : CleanupStepActivity
{
    public override string StepName => CleanupSteps.EvictPool;

    protected override async Task DoStepAsync(
        ActivityExecutionContext context,
        Guid tenantId)
    {
        var resolver = context.GetRequiredService<ITenantConnectionResolver>();
        await resolver.EvictAsync(tenantId, context.CancellationToken);
        Logger?.LogInformation(
            "tenant.cleanup.evict_pool completed tenantId={TenantId}",
            tenantId);
    }
}
