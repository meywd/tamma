using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Data.Abstractions;

namespace Tamma.Activities.TenantLifecycle;

/// <summary>
/// Step 4 of <c>CreateTenantWorkflow</c>. Runs the tenant-app EF
/// migration set against the just-created database via
/// <see cref="ITenantDbMigrator.MigrateTenantAppAsync"/>. Idempotent
/// because EF reads <c>__TenantMigrationsHistory</c> before applying.
///
/// <para>Compensation strategy: <c>MigrateTenantApp</c> failure is
/// rolled back by Step 3's compensator (<c>DROP DATABASE</c>) rather
/// than by per-migration rollback — see the impl plan §9.</para>
///
/// <para>Input: the tenant connection string output by
/// <see cref="EncryptAndPersistConnectionStringActivity"/> is NOT yet
/// available at this stage; instead, the workflow assembles the
/// connection string in-line from <see cref="CreateTenantRoleActivity"/>'s
/// outputs and feeds it here as <see cref="TenantConnectionString"/>.</para>
/// </summary>
[Activity(
    "Tamma.TenantLifecycle",
    "Migrate Tenant Database",
    "Apply the per-tenant EF migration set (idempotent).",
    Kind = ActivityKind.Task)]
public sealed class MigrateTenantDatabaseActivity : TenantLifecycleActivity
{
    public override string StepName => "migrate-database";

    [Input(
        Description = "Per-tenant connection string. Produced by the workflow "
                      + "by combining the admin host/port with the new role + "
                      + "freshly-generated password from CreateTenantRoleActivity.")]
    public Input<string> TenantConnectionString { get; set; } = default!;

    protected override async Task ProcessAsync(
        ActivityExecutionContext context,
        Guid tenantId,
        int attempt)
    {
        var cs = TenantConnectionString.Get(context);
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException(
                "MigrateTenantDatabase: TenantConnectionString input is empty.");

        var migrator = context.GetRequiredService<ITenantDbMigrator>();
        await migrator.MigrateTenantAppAsync(cs, context.CancellationToken);

        Logger?.LogInformation(
            "tenant.lifecycle.migrate_database completed tenantId={TenantId}",
            tenantId);
    }
}
