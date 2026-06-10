using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Pooling;

namespace Tamma.Activities.TenantLifecycle;

/// <summary>
/// Unified-tenancy Phase 2 — step C of <c>DeleteTenantWorkflow</c>
/// (replaces the db-per-tenant <c>DropTenantDatabaseActivity</c>). Drops
/// the tenant's <c>t_&lt;hex&gt;</c> schema on the ASSIGNED pool row's
/// database via <c>DROP SCHEMA IF EXISTS … CASCADE</c>.
///
/// <para>The placement is read from the tenant's
/// <c>DatabaseId</c>/<c>SchemaName</c> shadow columns. A tenant that
/// predates placement (both null — e.g. created before Phase 2 landed)
/// has no schema to drop; the activity logs and exits cleanly.</para>
///
/// <para>Pre-conditions: <see cref="EvictTenantPoolActivity"/> must have
/// run first so the resolver isn't holding a cached
/// <see cref="Npgsql.NpgsqlDataSource"/> with the schema on its
/// <c>Search Path</c>.</para>
///
/// <para>Idempotent: <c>IF EXISTS</c> makes a replay after a successful
/// drop a silent no-op.</para>
///
/// <para><b>Throws</b> on pool-connection failure so the surrounding
/// <c>DeleteTenantWorkflow</c> aborts cleanly. For continue-on-error
/// cleanup semantics see
/// <see cref="DropTenantSchemaForCleanupActivity"/>.</para>
/// </summary>
[Activity(
    "Tamma.TenantLifecycle",
    "Drop Tenant Schema",
    "DROP SCHEMA IF EXISTS t_<hex> CASCADE on the assigned pool database — idempotent.",
    Kind = ActivityKind.Task)]
public sealed class DropTenantSchemaActivity : TenantLifecycleActivity
{
    public override string StepName => "drop-schema";

    protected override async Task ProcessAsync(
        ActivityExecutionContext context,
        Guid tenantId,
        int attempt)
    {
        var factory = context.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
        var placement = await TenantPlacementShadow.LoadAsync(
            factory, tenantId, context.CancellationToken).ConfigureAwait(false);

        var pool = context.GetRequiredService<ITenantDatabasePool>();
        await DropSchemaAsync(
            pool, tenantId, placement.DatabaseId, placement.SchemaName,
            Logger, "lifecycle", context.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Pure-DI entry point — testable without a live Elsa context.
    /// Returns true when the schema drop was issued, false when skipped
    /// (tenant has no placement — predates Phase 2, or the row was
    /// half-stamped, which placement treats as unplaced).
    /// </summary>
    public static async Task<bool> DropSchemaAsync(
        ITenantDatabasePool pool,
        Guid tenantId,
        Guid? databaseId,
        string? schemaName,
        ILogger? logger,
        string logScope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pool);

        if (databaseId is null || schemaName is null)
        {
            logger?.LogInformation(
                "tenant.{Scope}.drop_schema no_placement_skip tenantId={TenantId} databaseId={DatabaseId} schema={Schema}",
                logScope, tenantId, databaseId, schemaName);
            return false;
        }

        // CASCADE: the schema owns every tenant table/index/sequence —
        // wall-clock O(1) on tenant data volume, matching the epic-28
        // success metric the old DROP DATABASE gave us.
        await pool.ExecuteOnAsync(
            databaseId.Value,
            $"DROP SCHEMA IF EXISTS {TenantNaming.Quote(schemaName)} CASCADE;",
            cancellationToken).ConfigureAwait(false);

        logger?.LogInformation(
            "tenant.{Scope}.drop_schema completed tenantId={TenantId} databaseId={DatabaseId} schema={Schema}",
            logScope, tenantId, databaseId, schemaName);
        return true;
    }
}

/// <summary>
/// H6 / Story 28-5 AC7 — continue-on-error variant of
/// <see cref="DropTenantSchemaActivity"/> used by
/// <c>CleanUpFailedTenantWorkflow</c> (replaces the db-per-tenant
/// <c>DropTenantDatabaseForCleanupActivity</c>). Same placement lookup +
/// <c>DROP SCHEMA IF EXISTS … CASCADE</c>; on failure the exception is
/// swallowed and recorded into the workflow's per-step state.
/// </summary>
[Activity(
    "Tamma.TenantLifecycle",
    "Drop Tenant Schema (Cleanup)",
    "Continue-on-error variant — never throws; records failure to workflow state.",
    Kind = ActivityKind.Task)]
public sealed class DropTenantSchemaForCleanupActivity : CleanupStepActivity
{
    public override string StepName => CleanupSteps.DropSchema;

    protected override async Task DoStepAsync(
        ActivityExecutionContext context,
        Guid tenantId)
    {
        var factory = context.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
        var placement = await TenantPlacementShadow.LoadAsync(
            factory, tenantId, context.CancellationToken).ConfigureAwait(false);

        var pool = context.GetRequiredService<ITenantDatabasePool>();
        await DropTenantSchemaActivity.DropSchemaAsync(
            pool, tenantId, placement.DatabaseId, placement.SchemaName,
            Logger, "cleanup", context.CancellationToken).ConfigureAwait(false);
    }
}
