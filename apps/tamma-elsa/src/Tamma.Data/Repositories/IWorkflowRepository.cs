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
}
