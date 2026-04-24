namespace Tamma.Data.Entities;

public class DomainEvent
{
    public Guid Id { get; set; }
    public string Type { get; set; } = null!;
    public Guid? TenantId { get; set; }
    public int? IssueNumber { get; set; }
    public string Tags { get; set; } = "{}";
    public string Metadata { get; set; } = "{}";
    public string Data { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Monotonic per-stream sequence populated server-side by a
    /// PostgreSQL <c>BIGSERIAL</c> identity. Provides a total-order
    /// cursor that is immune to same-millisecond <see cref="CreatedAt"/>
    /// collisions — two events that share a timestamp will still have
    /// distinct, strictly-increasing sequence numbers in insertion
    /// order. Consumers (e.g. <c>AlertRuleEvaluator</c>) use this
    /// column as their cursor tiebreak, never <see cref="Id"/>.
    /// </summary>
    public long SequenceNumber { get; set; }
}
