namespace Tamma.Data.Entities;

public class WorkflowDefinition
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public int Version { get; set; } = 1;
    public string Steps { get; set; } = "[]";
    public Guid? TenantId { get; set; }
    public DateTime SyncedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<WorkflowInstance> Instances { get; set; } = [];
}
