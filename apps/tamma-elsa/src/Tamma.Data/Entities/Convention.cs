namespace Tamma.Data.Entities;

/// <summary>
/// A per-(role, action) convention body — either a system default
/// (<c>TenantId IS NULL</c>) or a tenant override (<c>TenantId</c> set).
///
/// <para>Resolution order (service layer, Story 27-9):</para>
/// <list type="number">
///   <item>Tenant override for <c>(TenantId, Role, Action)</c> → use if
///     found.</item>
///   <item>System default for <c>(NULL, Role, Action)</c> → use if
///     found.</item>
/// </list>
///
/// <para>The two-tier model (null = system, non-null = tenant) differs from
/// <see cref="PromptOverride"/> which is dual-keyed (user_id XOR
/// tenant_id). <c>conventions</c> has only <c>tenant_id</c> — the
/// <c>principal_xor</c> CHECK and <c>user_id</c> column are
/// intentionally absent. No per-user override layer exists here: tenant
/// admins own the team's conventions; members cannot personalise them.
/// </para>
///
/// <para>Schema is seed-free at migration time — system-default rows are
/// loaded by Story 27-16's seed step; this entity/migration is schema
/// only.
/// </para>
///
/// <para>
/// <b>EPIC 28 CUTOVER NOTE (Task #12 — pure documentation).</b> Per the
/// 2026-05-25 decision, system defaults are DB-managed at runtime by a
/// platform admin (not compile-time constants). The <c>conventions</c>
/// table physically lives on the tenant tier (Story 28-1 PR D moved it
/// there alongside <c>prompt_overrides</c>). When Epic 28 flips the
/// deployment to per-tenant physical DBs, two hazards land on the admin
/// CRUD path:
/// </para>
/// <list type="bullet">
///   <item><b>I-2 (silent partial system-default write).</b> A platform-admin
///     mutating a system default (<see cref="TenantId"/>=NULL row) writes ONLY
///     into the admin's ambient tenant DB. The change is invisible to every
///     other tenant. Either a control-plane fanout or a runtime fail-loud
///     guard must ship with the cutover.</item>
///   <item><b>X-1 (defence-in-depth cross-tenant write guard).</b> In
///     per-tenant-DB mode the ambient tenant id determines the physical DB;
///     the tenantId argument to a write must equal it, else the write is
///     mis-routed. Add a runtime assertion at the repository methods when the
///     cutover ships.</item>
/// </list>
/// <para>Detail per-method on
/// <see cref="Tamma.Api.Services.Conventions.IConventionStore"/> and
/// <see cref="Tamma.Data.Repositories.IConventionRepository"/>. Grep
/// "I-2 hazard" / "X-1" to find every annotated site.
/// </para>
/// </summary>
public class Convention
{
    public Guid Id { get; set; }

    /// <summary>
    /// <c>NULL</c> identifies a system-default row (shipped by Tamma);
    /// a non-null value identifies a tenant override.
    /// No FK to the tenants table — the per-tenant DB routing model makes
    /// a hard cross-table FK awkward (following <see cref="PromptOverride"/>
    /// precedent).
    /// </summary>
    public Guid? TenantId { get; set; }

    /// <summary>Agent role (e.g. "developer", "architect"). Stored as plain
    /// TEXT; validation against AgentRole enum happens at the API boundary
    /// in later stories.</summary>
    public string Role { get; set; } = null!;

    /// <summary>Agent action (e.g. "implement-feature", "code-review"). Stored as
    /// plain TEXT; validated at the API boundary.</summary>
    public string Action { get; set; } = null!;

    /// <summary>The convention body injected into LLM prompts via
    /// <c>{{conventions}}</c>.</summary>
    public string Body { get; set; } = null!;

    /// <summary>Application-layer version counter, incremented by the service
    /// on each update. This is NOT an EF-enforced concurrency token
    /// (<c>.IsConcurrencyToken()</c> is not set); it is used by the service
    /// layer for optimistic-conflict detection and audit purposes only.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Whether this convention row is active. Disabled rows are
    /// skipped during resolution.</summary>
    public bool Enabled { get; set; } = true;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>User id that originally created this row.</summary>
    public Guid? CreatedBy { get; set; }

    /// <summary>User id of the most recent updater.</summary>
    public Guid? UpdatedBy { get; set; }
}
