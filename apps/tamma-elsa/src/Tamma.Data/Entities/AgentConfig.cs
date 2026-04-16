namespace Tamma.Data.Entities;

public class AgentConfig
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string Config { get; set; } = "{}";
    public int Version { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
}
