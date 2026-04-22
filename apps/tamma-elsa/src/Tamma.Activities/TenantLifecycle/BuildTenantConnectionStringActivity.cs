using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Data.Abstractions;

namespace Tamma.Activities.TenantLifecycle;

/// <summary>
/// Story 28-5 — assembles the per-tenant connection string from the
/// admin connection's host/port/SSL plus the freshly-minted tenant role,
/// password, and database name. The output is consumed by
/// <see cref="MigrateTenantDatabaseActivity"/>,
/// <see cref="SeedTenantDefaultsActivity"/>, and
/// <see cref="EncryptAndPersistConnectionStringActivity"/> in the same
/// run.
///
/// <para>Lives separately rather than being inlined into
/// <see cref="CreateTenantRoleActivity"/> so the unit test for the role
/// step can assert the role exists without also having to set up a
/// connection-string builder. Keeping this as its own activity also
/// gives the workflow a natural seam to inject a previously-known
/// connection string on a replay where the password was lost (the role
/// already exists from a prior partial run — the operator runbook
/// covers this).</para>
/// </summary>
[Activity(
    "Tamma.TenantLifecycle",
    "Build Tenant Connection String",
    "Materialise the per-tenant Npgsql connection string from admin host + role + password.",
    Kind = ActivityKind.Task)]
public sealed class BuildTenantConnectionStringActivity : TenantLifecycleActivity
{
    public override string StepName => "build-connection-string";

    [Input(Description = "Tenant database name (output of CreateTenantDatabaseActivity).")]
    public Input<string> DatabaseName { get; set; } = default!;

    [Input(Description = "Tenant role name (output of CreateTenantRoleActivity).")]
    public Input<string> RoleName { get; set; } = default!;

    [Input(Description = "Tenant role password (output of CreateTenantRoleActivity).")]
    public Input<string> Password { get; set; } = default!;

    [Output(Description = "The assembled per-tenant connection string.")]
    public Output<string> ConnectionString { get; set; } = default!;

    protected override Task ProcessAsync(
        ActivityExecutionContext context,
        Guid tenantId,
        int attempt)
    {
        var dbName = DatabaseName.Get(context);
        var role = RoleName.Get(context);
        var pwd = Password.Get(context);

        if (string.IsNullOrWhiteSpace(dbName))
            throw new InvalidOperationException("BuildConnectionString: DatabaseName is empty.");
        if (string.IsNullOrWhiteSpace(role))
            throw new InvalidOperationException("BuildConnectionString: RoleName is empty.");
        if (string.IsNullOrWhiteSpace(pwd))
            throw new InvalidOperationException(
                "BuildConnectionString: Password is empty. The role most likely already existed "
                + "from a partial-run; the operator runbook calls for DROP ROLE + retry.");

        var admin = context.GetRequiredService<ITenantAdminConnection>();
        var cs = admin.BuildTenantConnectionString(dbName, role, pwd);
        ConnectionString.Set(context, cs);

        // DO NOT log the connection string. The length is OK to log for
        // diagnostics — it asserts the builder produced something
        // non-trivial without leaking a secret.
        Logger?.LogInformation(
            "tenant.lifecycle.build_connection_string ok tenantId={TenantId} csLength={Len}",
            tenantId, cs.Length);

        return Task.CompletedTask;
    }
}
