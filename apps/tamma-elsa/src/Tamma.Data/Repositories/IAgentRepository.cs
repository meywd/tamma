using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Story 32-1 — persistence port for the CP-resident <see cref="Agent"/> /
/// <see cref="AgentVersion"/> entities. The canonical owner of the agent
/// identity + versioning lifecycle the rest of Epic 32 joins on.
///
/// <para>Resolves against <see cref="ControlPlaneDbContext"/> (definitions are
/// control-plane-resident). DCB events are appended via
/// <see cref="IEventRepository"/> only after a real state transition.</para>
/// </summary>
public interface IAgentRepository
{
    /// <summary>
    /// Create a new agent + its <c>Version=1</c> immutable snapshot in a single
    /// transaction, set <see cref="Agent.CurrentVersionId"/>, and append
    /// <c>AGENT.CREATED.SUCCESS</c>. The entity-level ownership guard rejects a
    /// private create whose principal columns contradict the process mode
    /// (belt to the DB CHECK's suspenders).
    /// </summary>
    Task<Agent> CreateAsync(
        Agent agent,
        string firstVersionConfigJson,
        string? notes,
        Guid? createdBy,
        CancellationToken ct = default);

    /// <summary>
    /// Publish a new immutable version: insert <c>Version = max(existing)+1</c>,
    /// atomically repoint <see cref="Agent.CurrentVersionId"/> +
    /// <c>UpdatedAt/By</c>, append <c>AGENT.VERSION_PUBLISHED.SUCCESS</c>.
    /// Concurrent double-publish is safe — the second INSERT loses the
    /// <c>(AgentId, Version)</c> unique index and is retried with a fresh
    /// <c>max+1</c>. Prior versions stay queryable for rollback. Returns
    /// <c>null</c> if the agent does not exist.
    /// </summary>
    Task<AgentVersion?> PublishVersionAsync(
        Guid agentId,
        string configJson,
        string? notes,
        Guid? updatedBy,
        CancellationToken ct = default);

    /// <summary>
    /// Set <see cref="Agent.Status"/> to <see cref="AgentStatus.Archived"/> and
    /// append <c>AGENT.ARCHIVED.SUCCESS</c>. Idempotent — archiving an
    /// already-archived agent is a no-op with no second event. Returns
    /// <c>null</c> if the agent does not exist.
    /// </summary>
    Task<Agent?> ArchiveAsync(Guid agentId, Guid? updatedBy, CancellationToken ct = default);

    Task<Agent?> GetByIdAsync(Guid agentId, CancellationToken ct = default);

    Task<AgentVersion?> GetVersionAsync(Guid agentId, int version, CancellationToken ct = default);

    Task<IReadOnlyList<AgentVersion>> ListVersionsAsync(Guid agentId, CancellationToken ct = default);

    /// <summary>
    /// Visibility-scoped list: all public agents ∪ the caller's own private
    /// agents. In SaaS mode the principal is <paramref name="tenantId"/>; in
    /// single-user mode it is <paramref name="userId"/>. A null principal sees
    /// public agents only.
    /// </summary>
    Task<IReadOnlyList<Agent>> ListVisibleAsync(
        Guid? tenantId, Guid? userId, CancellationToken ct = default);
}
