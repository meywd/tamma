using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Data.Abstractions;
using Tamma.Data.Pooling;

namespace Tamma.Activities.TenantLifecycle;

/// <summary>
/// Step 3 of <c>CreateTenantWorkflow</c>. Creates the tenant's app
/// database <c>tamma_tenant_&lt;hex&gt;</c> owned by the role minted in
/// <see cref="CreateTenantRoleActivity"/>. Idempotent via a
/// <c>pg_database</c> probe.
///
/// <para><c>CREATE DATABASE</c> cannot run inside a transaction; the
/// admin runner explicitly issues each statement on its own connection
/// without a <c>BEGIN</c> wrapper for exactly this reason.</para>
/// </summary>
[Activity(
    "Tamma.TenantLifecycle",
    "Create Tenant Database",
    "CREATE DATABASE tamma_tenant_<hex> OWNER tamma_tenant_<hex> (idempotent).",
    Kind = ActivityKind.Task)]
public sealed class CreateTenantDatabaseActivity : TenantLifecycleActivity
{
    public override string StepName => "create-database";

    [Output(Description = "Canonical database name (tamma_tenant_<hex>).")]
    public Output<string> DatabaseName { get; set; } = default!;

    protected override async Task ProcessAsync(
        ActivityExecutionContext context,
        Guid tenantId,
        int attempt)
    {
        var admin = context.GetRequiredService<ITenantAdminConnection>();
        var dbName = TenantNaming.DatabaseName(tenantId);
        var roleName = TenantNaming.RoleName(tenantId);
        DatabaseName.Set(context, dbName);

        if (await admin.DatabaseExistsAsync(dbName, context.CancellationToken))
        {
            Logger?.LogInformation(
                "tenant.lifecycle.create_database idempotent_skip tenantId={TenantId} db={Db}",
                tenantId, dbName);
            return;
        }

        // ENCODING + LC_* defaults from the cluster's template1; we set
        // them explicitly to guard against a template that isn't UTF8.
        var sql =
            $"CREATE DATABASE {TenantNaming.Quote(dbName)} "
            + $"WITH OWNER = {TenantNaming.Quote(roleName)} "
            + "ENCODING = 'UTF8' "
            + "TEMPLATE = template1 "
            + "CONNECTION LIMIT = -1;";

        await admin.ExecuteAsync(sql, context.CancellationToken);
        Logger?.LogInformation(
            "tenant.lifecycle.create_database created tenantId={TenantId} db={Db}",
            tenantId, dbName);
    }
}
