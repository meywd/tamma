namespace Tamma.Data.Repositories;

/// <summary>
/// Aggregate view over a tenant's workflow instances for the Story 23-5
/// Workflow Monitor. Pure counts — no cost / economics fields are ever
/// projected here (the monitor never reads a MarginPolicy or platform price).
/// </summary>
/// <param name="Total">Total instances matching the window.</param>
/// <param name="ByStatus">Instance count grouped by workflow status.</param>
/// <param name="ByDefinition">Instance count grouped by workflow definition
/// (id + friendly name).</param>
public sealed record WorkflowInstanceSummary(
    int Total,
    IReadOnlyList<WorkflowStatusCount> ByStatus,
    IReadOnlyList<WorkflowDefinitionCount> ByDefinition);

/// <summary>Instance count for a single workflow status value.</summary>
public sealed record WorkflowStatusCount(string Status, int Count);

/// <summary>Instance count for a single workflow definition.</summary>
public sealed record WorkflowDefinitionCount(Guid DefinitionId, string DefinitionName, int Count);
