namespace Tamma.Data.Entities;

public class AgentConfig
{
    public Guid Id { get; set; }

    /// <summary>
    /// Tenant id. NULL = system-default row (one allowed). Non-NULL = tenant
    /// override (one per tenant). Uniqueness is split into two partial indexes
    /// by the Phase-1 hardening migration so plain Postgres NULL semantics
    /// don't allow multiple system defaults to coexist.
    /// </summary>
    public Guid? TenantId { get; set; }

    public string Config { get; set; } = "{}";
    public int Version { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }

    public Tenant? Tenant { get; set; }
}
