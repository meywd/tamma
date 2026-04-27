using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Data.Abstractions;
using Tamma.Data.Pooling;

namespace Tamma.Activities.TenantLifecycle;

/// <summary>
/// Step 3 of <c>DeleteTenantWorkflow</c>. Drops the tenant database via
/// <c>DROP DATABASE … WITH (FORCE)</c>. Postgres 17's FORCE option kicks
/// any lingering backends out before dropping (saves us a separate
/// <c>pg_terminate_backend</c> step on the happy path).
///
/// <para>Pre-conditions: <see cref="EvictTenantPoolActivity"/> must have
/// run first so the resolver isn't holding a cached
/// <see cref="Npgsql.NpgsqlDataSource"/> against this database.</para>
///
/// <para>Idempotent: when the database does not exist (a previous
/// successful drop or a tenant that never reached Step 3 of create), the
/// activity logs and exits cleanly.</para>
///
/// <para><b>Throws</b> on admin-connection failure so the surrounding
/// <c>DeleteTenantWorkflow</c> aborts cleanly. For continue-on-error
/// cleanup semantics see
/// <see cref="DropTenantDatabaseForCleanupActivity"/>.</para>
/// </summary>
[Activity(
    "Tamma.TenantLifecycle",
    "Drop Tenant Database",
    "DROP DATABASE tamma_tenant_<hex> WITH (FORCE) — idempotent.",
    Kind = ActivityKind.Task)]
public sealed class DropTenantDatabaseActivity : TenantLifecycleActivity
{
    public override string StepName => "drop-database";

    protected override async Task ProcessAsync(
        ActivityExecutionContext context,
        Guid tenantId,
        int attempt)
    {
        var admin = context.GetRequiredService<ITenantAdminConnection>();
        var dbName = TenantNaming.DatabaseName(tenantId);

        if (!await admin.DatabaseExistsAsync(dbName, context.CancellationToken))
        {
            Logger?.LogInformation(
                "tenant.lifecycle.drop_database idempotent_skip tenantId={TenantId} db={Db}",
                tenantId, dbName);
            return;
        }

        // FORCE requires the issuer to NOT be connected to this DB —
        // already guaranteed because the admin runner connects to the
        // admin DB (postgres / tamma_provisioner host DB).
        var sql = $"DROP DATABASE {TenantNaming.Quote(dbName)} WITH (FORCE);";
        await admin.ExecuteAsync(sql, context.CancellationToken);
        Logger?.LogInformation(
            "tenant.lifecycle.drop_database completed tenantId={TenantId} db={Db}",
            tenantId, dbName);
    }
}

/// <summary>
/// H6 / Story 28-5 AC7 — continue-on-error variant of
/// <see cref="DropTenantDatabaseActivity"/> used by
/// <c>CleanUpFailedTenantWorkflow</c>. Same probe-then-DROP pattern; on
/// failure the exception is swallowed, recorded into the workflow's
/// per-step state, and the workflow's next sibling step still runs.
/// </summary>
[Activity(
    "Tamma.TenantLifecycle",
    "Drop Tenant Database (Cleanup)",
    "Continue-on-error variant — never throws; records failure to workflow state.",
    Kind = ActivityKind.Task)]
public sealed class DropTenantDatabaseForCleanupActivity : CleanupStepActivity
{
    public override string StepName => CleanupSteps.DropDatabase;

    protected override async Task DoStepAsync(
        ActivityExecutionContext context,
        Guid tenantId)
    {
        var admin = context.GetRequiredService<ITenantAdminConnection>();
        var dbName = TenantNaming.DatabaseName(tenantId);

        if (!await admin.DatabaseExistsAsync(dbName, context.CancellationToken)
            .ConfigureAwait(false))
        {
            Logger?.LogInformation(
                "tenant.cleanup.drop_database idempotent_skip tenantId={TenantId} db={Db}",
                tenantId, dbName);
            return;
        }

        var sql = $"DROP DATABASE {TenantNaming.Quote(dbName)} WITH (FORCE);";
        await admin.ExecuteAsync(sql, context.CancellationToken).ConfigureAwait(false);
        Logger?.LogInformation(
            "tenant.cleanup.drop_database completed tenantId={TenantId} db={Db}",
            tenantId, dbName);
    }
}
