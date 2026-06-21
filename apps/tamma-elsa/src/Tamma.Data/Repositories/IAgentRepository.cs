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

    /// <summary>
    /// Story 32-2 — rollback: repoint <see cref="Agent.CurrentVersionId"/> at an
    /// EXISTING prior version (no new snapshot is inserted; history stays
    /// immutable). Appends <c>AGENT.VERSION_PUBLISHED.SUCCESS</c> tagged
    /// <c>activated=rollback</c> only on a real pointer move. Returns the
    /// re-activated <see cref="AgentVersion"/>, or <c>null</c> if the agent or
    /// the target version does not exist.
    /// </summary>
    Task<AgentVersion?> SetActiveVersionAsync(
        Guid agentId, int version, Guid? updatedBy, CancellationToken ct = default);

    Task<Agent?> GetByIdAsync(Guid agentId, CancellationToken ct = default);

    Task<AgentVersion?> GetVersionAsync(Guid agentId, int version, CancellationToken ct = default);

    /// <summary>
    /// Story 32-2 — the agent's CURRENTLY-ACTIVE version (the one
    /// <see cref="Agent.CurrentVersionId"/> points at). After a rollback this is
    /// the re-activated prior version, not the highest version number. Returns
    /// <c>null</c> if the agent has no active-version pointer.
    /// </summary>
    Task<AgentVersion?> GetActiveVersionAsync(Guid agentId, CancellationToken ct = default);

    Task<IReadOnlyList<AgentVersion>> ListVersionsAsync(Guid agentId, CancellationToken ct = default);

    /// <summary>
    /// Visibility-scoped list: all public agents ∪ the caller's own private
    /// agents. In SaaS mode the principal is <paramref name="tenantId"/>; in
    /// single-user mode it is <paramref name="userId"/>. A null principal sees
    /// public agents only.
    /// </summary>
    Task<IReadOnlyList<Agent>> ListVisibleAsync(
        Guid? tenantId, Guid? userId, CancellationToken ct = default);

    /// <summary>
    /// Story 32-15 — fetch the single PUBLIC agent (persona) with the given
    /// <paramref name="name"/> (case-sensitive handle match), regardless of
    /// role. Public persona handles are globally unique (IX_agents_public_name),
    /// so this returns at most one row. Returns <c>null</c> when no public
    /// persona by that name exists. Backs the configured-default-persona lookup
    /// (<c>GetSystemDefaultPublicAsync</c>).
    /// </summary>
    Task<Agent?> GetPublicByNameAsync(string name, CancellationToken ct = default);
}
