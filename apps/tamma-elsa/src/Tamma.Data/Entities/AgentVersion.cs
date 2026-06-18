namespace Tamma.Data.Entities;

/// <summary>
/// Story 32-1 — an immutable, monotonically-versioned saved-config snapshot for
/// an <see cref="Agent"/>. Insert-only: never <c>UPDATE</c>d after insert. Any
/// action/metric ties to the exact config version that produced it, so
/// benchmarking can slice by version. Rollback = repoint
/// <see cref="Agent.CurrentVersionId"/>, never delete-and-recreate; the
/// <c>(AgentId, Version)</c> unique index guarantees monotonic, non-duplicated
/// versions, and the FK uses <c>OnDelete(Restrict)</c> to protect history.
/// </summary>
public class AgentVersion
{
    public Guid Id { get; set; }

    public Guid AgentId { get; set; }

    /// <summary>1-based, monotonic per <see cref="AgentId"/>. Unique on <c>(AgentId, Version)</c>.</summary>
    public int Version { get; set; }

    /// <summary>
    /// The saved-config snapshot (jsonb). Credential-agnostic by design
    /// (provider + model + prompt + settings, never raw keys). Validated by
    /// <c>AgentConfigValidator</c> before any write.
    /// </summary>
    public string ConfigJson { get; set; } = "{}";

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }

    public Agent? Agent { get; set; }
}
