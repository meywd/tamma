using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Data.Abstractions;

namespace Tamma.Activities.TenantLifecycle;

/// <summary>
/// Unified-tenancy Phase 2 — step 4 of <c>CreateTenantWorkflow</c>.
/// Creates the tenant's <c>t_&lt;hex&gt;</c> schema (owned by the role
/// minted in <see cref="CreateTenantRoleActivity"/>) plus
/// <c>GRANT CONNECT</c> and the per-database default <c>search_path</c>,
/// all on the ASSIGNED pool row's database. Thin wrapper over
/// <see cref="ITenantProvisioningService.CreateSchemaAsync"/> — the
/// shared step engine owns the SQL so the SaaS workflow and the
/// single-user middleware provision identically. Idempotent
/// (IF NOT EXISTS / re-grant / re-set).
/// </summary>
[Activity(
    "Tamma.TenantLifecycle",
    "Create Tenant Schema",
    "CREATE SCHEMA t_<hex> AUTHORIZATION role + grants on the assigned pool database (idempotent).",
    Kind = ActivityKind.Task)]
public sealed class CreateTenantSchemaActivity : TenantLifecycleActivity
{
    public override string StepName => "create-schema";

    [Input(Description = "Assigned pool row id (output of AssignTenantPlacementActivity).")]
    public Input<string> DatabaseId { get; set; } = default!;

    [Input(Description = "Assigned schema name (output of AssignTenantPlacementActivity).")]
    public Input<string> SchemaName { get; set; } = default!;

    protected override async Task ProcessAsync(
        ActivityExecutionContext context,
        Guid tenantId,
        int attempt)
    {
        var placement = AssignTenantPlacementActivity.ReconstructPlacement(
            DatabaseId.Get(context), SchemaName.Get(context), "CreateTenantSchema");

        await context.GetRequiredService<ITenantProvisioningService>()
            .CreateSchemaAsync(tenantId, placement, context.CancellationToken);

        Logger?.LogInformation(
            "tenant.lifecycle.create_schema ok tenantId={TenantId} schema={Schema} databaseId={DatabaseId}",
            tenantId, placement.SchemaName, placement.DatabaseId);
    }
}
