using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public interface IWorkflowRepository
{
    Task<WorkflowDefinition> UpsertDefinitionAsync(WorkflowDefinition def);
    Task<WorkflowDefinition?> GetDefinitionAsync(Guid id);
    Task<List<WorkflowDefinition>> ListDefinitionsAsync();
    Task<WorkflowInstance> CreateInstanceAsync(WorkflowInstance instance);
    Task<WorkflowInstance?> UpdateInstanceAsync(Guid id, Action<WorkflowInstance> update);
    Task<WorkflowInstance?> GetInstanceAsync(Guid id);
    Task<bool> DeleteInstanceAsync(Guid id);
    Task<(List<WorkflowInstance> Instances, int Total)> ListInstancesAsync(Guid? definitionId, Guid? tenantId, int page, int pageSize);

    /// <summary>
    /// Aggregate the tenant's workflow instances into per-status and
    /// per-definition counts over an optional [from, to) time window (matched
    /// on <see cref="WorkflowInstance.CreatedAt"/>). Tenant-scoped read backing
    /// the Story 23-5 Workflow Monitor — counts only, never cost/economics.
    /// </summary>
    Task<WorkflowInstanceSummary> SummarizeInstancesAsync(Guid tenantId, DateTime? from, DateTime? to);
}
