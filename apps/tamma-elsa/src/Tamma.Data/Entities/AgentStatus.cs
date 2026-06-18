namespace Tamma.Data.Entities;

/// <summary>
/// Story 32-1 — lifecycle state for an <see cref="Agent"/>. Stored as an
/// <c>int</c> (<see cref="Active"/> = 0, <see cref="Archived"/> = 1) via
/// <c>HasConversion&lt;int&gt;()</c>. Archive is the terminal state — agent
/// definitions and their version history are never hard-deleted (versions are
/// immutable audit history; the FK uses <c>OnDelete(Restrict)</c>).
/// </summary>
public enum AgentStatus
{
    /// <summary>The agent is live and resolvable.</summary>
    Active = 0,

    /// <summary>The agent has been retired. History stays queryable; no new versions.</summary>
    Archived = 1,
}
