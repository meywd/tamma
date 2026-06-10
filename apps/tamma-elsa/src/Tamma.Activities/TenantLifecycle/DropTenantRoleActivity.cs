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
/// Step D of <c>DeleteTenantWorkflow</c>. Drops the per-tenant Postgres
/// role. <c>DROP OWNED BY</c> first to release any objects the role
/// owned, then <c>DROP ROLE IF EXISTS</c> so a redundant call is silent.
///
/// <para><b>Unified-tenancy Phase 2 — placement-aware:</b> roles are
/// CLUSTER-scoped and <c>DROP OWNED BY</c> acts per-database, so when the
/// tenant has an assigned <c>tenant_databases</c> row both statements run
/// via <see cref="ITenantDatabasePool"/> against the ASSIGNED row's
/// database. Tenants without a placement (pre-Phase-2 dev runs) fall back
/// to the legacy central <see cref="ITenantAdminConnection"/> path — the
/// old db-per-tenant create made the role on the central cluster.</para>
///
/// <para>Sequence requirement: must run AFTER
/// <see cref="DropTenantSchemaActivity"/>, otherwise <c>DROP ROLE</c>
/// fails because the role still owns the schema (<c>DROP OWNED BY</c>
/// covers it, but the explicit ordering keeps the audit trail of
/// "schema dropped" distinct from "role dropped").</para>
///
/// <para><b>Throws</b> on connection failure so the surrounding
/// <c>DeleteTenantWorkflow</c> aborts cleanly. For continue-on-error
/// cleanup semantics see
/// <see cref="DropTenantRoleForCleanupActivity"/>.</para>
/// </summary>
[Activity(
    "Tamma.TenantLifecycle",
    "Drop Tenant Role",
    "DROP OWNED BY then DROP ROLE IF EXISTS on the assigned cluster — idempotent.",
    Kind = ActivityKind.Task)]
public sealed class DropTenantRoleActivity : TenantLifecycleActivity
{
    public override string StepName => "drop-role";

    protected override async Task ProcessAsync(
        ActivityExecutionContext context,
        Guid tenantId,
        int attempt)
    {
        var factory = context.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
        var placement = await TenantPlacementShadow.LoadAsync(
            factory, tenantId, context.CancellationToken).ConfigureAwait(false);

        await DropRoleAsync(
            context.GetRequiredService<ITenantDatabasePool>(),
            context.GetRequiredService<ITenantAdminConnection>(),
            tenantId,
            placement.DatabaseId,
            Logger,
            "lifecycle",
            context.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Pure-DI entry point — testable without a live Elsa context.
    /// Returns true when the role was dropped, false on the
    /// idempotent skip (role already gone).
    /// </summary>
    public static async Task<bool> DropRoleAsync(
        ITenantDatabasePool pool,
        ITenantAdminConnection centralAdmin,
        Guid tenantId,
        Guid? databaseId,
        ILogger? logger,
        string logScope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(centralAdmin);

        var roleName = TenantNaming.RoleName(tenantId);
        var quoted = TenantNaming.Quote(roleName);

        if (databaseId is not null)
        {
            // Placement path: the role lives on the assigned pool row's
            // cluster, and DROP OWNED BY only releases objects/grants in
            // the database it runs against — so both statements go
            // through the pool, never the central admin connection.
            if (!await pool.RoleExistsOnAsync(databaseId.Value, roleName, cancellationToken)
                .ConfigureAwait(false))
            {
                logger?.LogInformation(
                    "tenant.{Scope}.drop_role idempotent_skip tenantId={TenantId} role={Role} databaseId={DatabaseId}",
                    logScope, tenantId, roleName, databaseId);
                return false;
            }

            // DROP OWNED BY removes the schema-scoped grants + any object
            // the role still owns in the target DB (the schema itself is
            // already gone). Own statement so a transient grant somewhere
            // doesn't make the DROP ROLE fail.
            await pool.ExecuteOnAsync(
                databaseId.Value,
                $"DROP OWNED BY {quoted};",
                cancellationToken).ConfigureAwait(false);

            await pool.ExecuteOnAsync(
                databaseId.Value,
                $"DROP ROLE IF EXISTS {quoted};",
                cancellationToken).ConfigureAwait(false);

            logger?.LogInformation(
                "tenant.{Scope}.drop_role completed tenantId={TenantId} role={Role} databaseId={DatabaseId}",
                logScope, tenantId, roleName, databaseId);
            return true;
        }

        // Legacy fallback (placement null): roles from pre-Phase-2 dev
        // runs were created by the old db-per-tenant CreateTenantRole
        // step on the CENTRAL cluster — keep dropping them there.
        if (!await centralAdmin.RoleExistsAsync(roleName, cancellationToken)
            .ConfigureAwait(false))
        {
            logger?.LogInformation(
                "tenant.{Scope}.drop_role idempotent_skip tenantId={TenantId} role={Role}",
                logScope, tenantId, roleName);
            return false;
        }

        await centralAdmin.ExecuteAsync(
            $"DROP OWNED BY {quoted};",
            cancellationToken).ConfigureAwait(false);

        await centralAdmin.ExecuteAsync(
            $"DROP ROLE IF EXISTS {quoted};",
            cancellationToken).ConfigureAwait(false);

        logger?.LogInformation(
            "tenant.{Scope}.drop_role completed tenantId={TenantId} role={Role}",
            logScope, tenantId, roleName);
        return true;
    }
}

/// <summary>
/// H6 / Story 28-5 AC7 — continue-on-error variant of
/// <see cref="DropTenantRoleActivity"/> used by
/// <c>CleanUpFailedTenantWorkflow</c>. Same placement-aware
/// DROP OWNED BY → DROP ROLE sequence; on failure the exception is
/// swallowed and recorded into the workflow's per-step state.
/// </summary>
[Activity(
    "Tamma.TenantLifecycle",
    "Drop Tenant Role (Cleanup)",
    "Continue-on-error variant — never throws; records failure to workflow state.",
    Kind = ActivityKind.Task)]
public sealed class DropTenantRoleForCleanupActivity : CleanupStepActivity
{
    public override string StepName => CleanupSteps.DropRole;

    protected override async Task DoStepAsync(
        ActivityExecutionContext context,
        Guid tenantId)
    {
        var factory = context.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
        var placement = await TenantPlacementShadow.LoadAsync(
            factory, tenantId, context.CancellationToken).ConfigureAwait(false);

        await DropTenantRoleActivity.DropRoleAsync(
            context.GetRequiredService<ITenantDatabasePool>(),
            context.GetRequiredService<ITenantAdminConnection>(),
            tenantId,
            placement.DatabaseId,
            Logger,
            "cleanup",
            context.CancellationToken).ConfigureAwait(false);
    }
}
