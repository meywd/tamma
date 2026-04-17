namespace Tamma.Data.Entities;

public class WorkflowInstance
{
    public Guid Id { get; set; }
    public Guid DefinitionId { get; set; }
    public Guid? TenantId { get; set; }
    public string Status { get; set; } = "pending";
    public string? CurrentActivity { get; set; }
    public string Variables { get; set; } = "{}";

    /// <summary>
    /// Terminal result payload (JSON) posted via the SaaS
    /// <c>POST /api/v1/workflows/:id/result</c> endpoint. Null until a result
    /// has been recorded.
    /// </summary>
    public string? Result { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public WorkflowDefinition Definition { get; set; } = null!;
}
