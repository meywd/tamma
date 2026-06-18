namespace Tamma.Data.Entities;

/// <summary>
/// Story 32-1 — ownership/visibility discriminator for an <see cref="Agent"/>.
///
/// <para>Stored as an <c>int</c> (<see cref="Public"/> = 0,
/// <see cref="Private"/> = 1) via <c>HasConversion&lt;int&gt;()</c> so the
/// <c>ck_agents_visibility_ownership</c> CHECK can compare the numeric
/// discriminator. The ordinal values are load-bearing — the CHECK constraint
/// SQL in <c>TammaModelConfiguration</c> hard-codes <c>= 0</c> / <c>= 1</c>.
/// Do NOT reorder.</para>
/// </summary>
public enum AgentVisibility
{
    /// <summary>
    /// Platform-owned / system-wide agent. Available to every tenant; owned and
    /// edited by the platform admin. Both owner columns are NULL. Shipped
    /// default agents are public.
    /// </summary>
    Public = 0,

    /// <summary>
    /// Tenant-owned (SaaS) or user-owned (single-user) agent. Available only to
    /// its principal. Exactly one owner column is set, picked by the process
    /// mode: SaaS → <see cref="Agent.OwnerTenantId"/>; single-user →
    /// <see cref="Agent.OwnerUserId"/>.
    /// </summary>
    Private = 1,
}
