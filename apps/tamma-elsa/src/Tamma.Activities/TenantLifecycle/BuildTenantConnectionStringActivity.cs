using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Data.Abstractions;

namespace Tamma.Activities.TenantLifecycle;

/// <summary>
/// Step 5 of <c>CreateTenantWorkflow</c>. Mints the per-tenant connection
/// string for the assigned placement: the pool row's Host/Port/SSL + the
/// row's database + the tenant role/password +
/// <c>Search Path=t_&lt;hex&gt;</c>. Unified-tenancy Phase 2 delegates to
/// <see cref="ITenantProvisioningService.BuildConnectionStringAsync"/> so
/// the SaaS workflow and the single-user middleware mint the SAME shape.
/// The output is consumed by <see cref="MigrateTenantDatabaseActivity"/>,
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
    "Mint the per-tenant Npgsql connection string (pool row host + role + Search Path=t_<hex>).",
    Kind = ActivityKind.Task)]
public sealed class BuildTenantConnectionStringActivity : TenantLifecycleActivity
{
    public override string StepName => "build-connection-string";

    [Input(Description = "Assigned pool row id (output of AssignTenantPlacementActivity).")]
    public Input<string> DatabaseId { get; set; } = default!;

    [Input(Description = "Assigned schema name (output of AssignTenantPlacementActivity).")]
    public Input<string> SchemaName { get; set; } = default!;

    [Input(Description = "Tenant role password (output of CreateTenantRoleActivity).")]
    public Input<string> Password { get; set; } = default!;

    [Output(Description = "The minted per-tenant connection string.")]
    public Output<string> ConnectionString { get; set; } = default!;

    protected override async Task ProcessAsync(
        ActivityExecutionContext context,
        Guid tenantId,
        int attempt)
    {
        var placement = AssignTenantPlacementActivity.ReconstructPlacement(
            DatabaseId.Get(context), SchemaName.Get(context), "BuildConnectionString");

        var pwd = Password.Get(context);
        if (string.IsNullOrWhiteSpace(pwd))
            throw new InvalidOperationException(
                "BuildConnectionString: Password is empty. The role most likely already existed "
                + "from a partial-run; the operator runbook calls for DROP OWNED BY + DROP ROLE "
                + "on the placement database, then retry.");

        var cs = await context.GetRequiredService<ITenantProvisioningService>()
            .BuildConnectionStringAsync(tenantId, placement, pwd, context.CancellationToken);
        ConnectionString.Set(context, cs);

        // DO NOT log the connection string. The length is OK to log for
        // diagnostics — it asserts the builder produced something
        // non-trivial without leaking a secret.
        Logger?.LogInformation(
            "tenant.lifecycle.build_connection_string ok tenantId={TenantId} csLength={Len}",
            tenantId, cs.Length);
    }
}
