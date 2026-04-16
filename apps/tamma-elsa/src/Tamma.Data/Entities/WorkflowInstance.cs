namespace Tamma.Data.Entities;

public class WorkflowInstance
{
    public Guid Id { get; set; }
    public Guid DefinitionId { get; set; }
    public Guid? TenantId { get; set; }
    public string Status { get; set; } = "pending";
    public string? CurrentActivity { get; set; }
    public string Variables { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public WorkflowDefinition Definition { get; set; } = null!;
}
