using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Data.Abstractions;

namespace Tamma.Activities.TenantLifecycle;

/// <summary>
/// Unified-tenancy Phase 2 — step 2 of <c>CreateTenantWorkflow</c>.
/// Assigns the tenant to a <c>tenant_databases</c> pool row by plan tier
/// via <see cref="ITenantPlacementService"/> and surfaces the placement
/// to the rest of the workflow as two variables: <see cref="DatabaseId"/>
/// (Guid in "D" format — workflow variables stay string-typed so the
/// journal serialisation is trivially stable) and
/// <see cref="SchemaName"/> (<c>t_&lt;hex&gt;</c>). Idempotent — an
/// already-placed tenant gets its existing placement back unchanged, so
/// a workflow retry never re-assigns.
/// </summary>
[Activity(
    "Tamma.TenantLifecycle",
    "Assign Tenant Placement",
    "Pick the tenant_databases pool row by plan tier and stamp tenants.DatabaseId/SchemaName (idempotent).",
    Kind = ActivityKind.Task)]
public sealed class AssignTenantPlacementActivity : TenantLifecycleActivity
{
    public override string StepName => "assign-placement";

    [Output(Description = "Assigned tenant_databases row id (Guid, \"D\" format).")]
    public Output<string> DatabaseId { get; set; } = default!;

    [Output(Description = "Assigned tenant schema name (t_<hex>).")]
    public Output<string> SchemaName { get; set; } = default!;

    protected override async Task ProcessAsync(
        ActivityExecutionContext context,
        Guid tenantId,
        int attempt)
    {
        var placement = await context.GetRequiredService<ITenantPlacementService>()
            .AssignAsync(tenantId, context.CancellationToken);

        DatabaseId.Set(context, placement.DatabaseId.ToString("D"));
        SchemaName.Set(context, placement.SchemaName);

        Logger?.LogInformation(
            "tenant.lifecycle.assign_placement ok tenantId={TenantId} databaseId={DatabaseId} schema={Schema}",
            tenantId, placement.DatabaseId, placement.SchemaName);
    }

    /// <summary>
    /// Rebuilds the <see cref="TenantPlacement"/> the downstream
    /// activities need from the two string workflow variables this
    /// activity set. Fail-fast with the step name so a mis-wired
    /// workflow (placement step skipped / variables unbound) surfaces
    /// as a clear error instead of <c>Guid.Empty</c> DDL downstream.
    /// </summary>
    internal static TenantPlacement ReconstructPlacement(
        string? databaseId, string? schemaName, string consumerStep)
    {
        if (string.IsNullOrWhiteSpace(databaseId) || !Guid.TryParse(databaseId, out var parsed)
            || parsed == Guid.Empty)
            throw new InvalidOperationException(
                $"{consumerStep}: workflow variable 'DatabaseId' is missing or not a valid Guid "
                + "— AssignTenantPlacementActivity must run before this step.");

        if (string.IsNullOrWhiteSpace(schemaName))
            throw new InvalidOperationException(
                $"{consumerStep}: workflow variable 'SchemaName' is empty "
                + "— AssignTenantPlacementActivity must run before this step.");

        return new TenantPlacement(parsed, schemaName);
    }
}
