using Tamma.Api.Services.Agents;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Conventions;

/// <summary>
/// The layer a resolved convention came from. Conventions are a two-tier model
/// (Story 27-9): a tenant override (<c>tenant_id = X</c>) or the system default
/// (<c>tenant_id IS NULL</c>). There is no per-user tier and — under the locked
/// user mandate — no "empty/plain" terminal: a taxonomy-valid pair always
/// resolves to <c>tenant</c> or <c>system</c>, or else throws.
/// </summary>
public enum ConventionSource
{
    /// <summary>Tenant's <c>(role, action)</c> override (enabled).</summary>
    Tenant,
    /// <summary>System-default <c>(role, action)</c> row (<c>tenant_id IS NULL</c>).</summary>
    System,
}

/// <summary>
/// A fully-resolved convention. <see cref="Body"/> is what substitutes into the
/// <c>{{conventions}}</c> workflow variable (AC7). There are deliberately NO
/// <c>Triggered</c> / <c>Skipped</c> lists — the keyword/tokeniser design is
/// deleted (AC6); resolution is a single exact-key seek.
///
/// <para>
/// <see cref="Id"/>, <see cref="Version"/>, and <see cref="UpdatedAt"/> carry the
/// real metadata from the winning row so callers (endpoint handlers) can build a
/// complete response in a single DB roundtrip without re-fetching via
/// <see cref="IConventionStore.GetAsync"/>. They match the fields already carried
/// by <see cref="ConventionSummary"/> (the list-result shape), keeping both
/// single-item and list paths consistent.
/// </para>
/// </summary>
/// <param name="Role">Agent role wire string (e.g. <c>developer</c>).</param>
/// <param name="Action">Agent action wire string (e.g. <c>implement-feature</c>).</param>
/// <param name="Body">The resolved convention body (non-empty for a valid pair).</param>
/// <param name="Source">Which tier produced <see cref="Body"/>.</param>
/// <param name="Id">Primary key of the winning row.</param>
/// <param name="Version">Application-layer version counter of the winning row.</param>
/// <param name="UpdatedAt">Last-modified timestamp of the winning row.</param>
public sealed record ConventionResolution(
    string Role,
    string Action,
    string Body,
    ConventionSource Source,
    Guid Id,
    int Version,
    DateTime UpdatedAt);

/// <summary>
/// A single row in the <see cref="IConventionStore.ListAsync"/> result — the
/// resolved convention for one taxonomy cell, plus its source tier and the
/// real row metadata (id, version, enabled, updatedAt) from the winning row.
/// Carrying these avoids the list/detail metadata disagreement that arises when
/// callers hardcode constants (e.g. <c>Version = 1</c>) instead of surfacing
/// the actual stored values.
/// </summary>
/// <param name="Role">Agent role wire string.</param>
/// <param name="Action">Agent action wire string.</param>
/// <param name="Body">Resolved body (tenant override if present-and-enabled, else system default).</param>
/// <param name="Source">Which tier produced <see cref="Body"/>.</param>
/// <param name="Id">Primary key of the winning row.</param>
/// <param name="Enabled">Whether the winning row is enabled (always true for rows returned by
/// <see cref="IConventionStore.ListAsync"/>, since disabled cells are omitted).</param>
/// <param name="Version">Application-layer version counter of the winning row.</param>
/// <param name="UpdatedAt">Last-modified timestamp of the winning row.</param>
public sealed record ConventionSummary(
    string Role,
    string Action,
    string Body,
    ConventionSource Source,
    Guid Id,
    bool Enabled,
    int Version,
    DateTime UpdatedAt);

/// <summary>
/// Convention store service (Story 27-9). Resolves a coding-convention body by
/// EXACT <c>(tenant_id, role, action)</c> lookup with a tenant-override layer
/// over the seeded system defaults, so the <c>{{conventions}}</c> workflow
/// variable is populated by the same shape as the prompt store.
///
/// <para><b>Typed taxonomy.</b> The interface takes typed
/// <see cref="AgentRole"/> / <see cref="AgentAction"/> — the taxonomy is
/// validated by the type system. String→enum <c>Parse</c> happens at the API
/// boundary (Story 27-10), not in this service. The columns are TEXT, so the
/// service stores/queries the wire strings (<c>role.ToWire()</c> /
/// <c>action.ToWire()</c>).</para>
///
/// <para><b>Two-tier, tenant-scoped (NOT three-tier).</b> Unlike the prompt
/// store, there is NO <c>userId</c>-keyed parallel surface. The <c>userId</c>
/// argument on <see cref="UpsertAsync"/> is ONLY the audit attribution
/// (<c>CreatedBy</c> / <c>UpdatedBy</c>), never a scoping key (SPEC §2/§3.3,
/// Story 27-8 schema has no <c>user_id</c> column).</para>
/// </summary>
public interface IConventionStore
{
    /// <summary>
    /// Raw fetch (AC1) — returns the resolved-or-null row WITHOUT the fail-loud
    /// guarantee. Prefers the tenant override (enabled), else the system
    /// default, else <c>null</c>. Use <see cref="ResolveAsync"/> for the
    /// fail-loud path that backs <c>{{conventions}}</c>.
    ///
    /// <para><b>Enabled asymmetry.</b> The tenant-override tier is
    /// <c>Enabled</c>-filtered (a disabled override is treated as absent and
    /// falls through). The system-default tier is returned REGARDLESS of its
    /// <c>Enabled</c> flag — callers needing the fully enabled-filtered chain
    /// (both tiers) must use <see cref="ResolveAsync"/>.</para>
    /// </summary>
    Task<Convention?> GetAsync(Guid? tenantId, AgentRole role, AgentAction action, CancellationToken ct);

    /// <summary>
    /// Create/update a tenant override (AC2). Operates ONLY on tenant-override
    /// rows (<c>tenant_id = @tenantId</c>); never mutates system defaults. Sets
    /// <c>CreatedBy</c> / <c>UpdatedBy = userId</c>, persists
    /// <paramref name="enabled"/> on both insert and update (Story 27-10 — a
    /// tenant edit must be able to disable an override without silently
    /// re-enabling it), and bumps <c>Version</c> on update. Returns
    /// <c>(row, wasCreated)</c> where <c>wasCreated</c> is <c>true</c> for an
    /// INSERT and <c>false</c> for an UPDATE — used internally to emit the
    /// correct DCB audit event type (Story 27-14: emission moves to the service).
    /// </summary>
    Task<(Convention Row, bool WasCreated)> UpsertAsync(Guid tenantId, AgentRole role, AgentAction action, string body, bool enabled, Guid userId, CancellationToken ct);

    /// <summary>
    /// Delete a tenant override (AC2). Operates ONLY on tenant-override rows;
    /// never deletes system defaults. Returns <c>true</c> when a row was
    /// actually removed, <c>false</c> when no override existed (no-op). Callers
    /// use the return value to decide whether to emit a DCB deletion event.
    /// </summary>
    Task<bool> DeleteAsync(Guid tenantId, AgentRole role, AgentAction action, CancellationToken ct);

    /// <summary>
    /// Fail-loud resolution (AC4) — tenant override (enabled) → system default
    /// (enabled) → <see cref="Tamma.Core.TammaError"/> (<c>CONVENTION_NOT_FOUND</c>).
    /// NEVER returns null/empty/plain (locked user mandate). A disabled tenant
    /// override falls through to the system default (AC9).
    /// </summary>
    Task<ConventionResolution> ResolveAsync(Guid? tenantId, AgentRole role, AgentAction action, CancellationToken ct);

    /// <summary>
    /// List the resolved convention for every taxonomy cell (AC3) — tenant
    /// override if present-and-enabled, else the system default. Returns one
    /// <see cref="ConventionSummary"/> per <c>RolePhaseMap</c> cell.
    /// </summary>
    Task<IReadOnlyList<ConventionSummary>> ListAsync(Guid? tenantId, CancellationToken ct);

    // ------------------------------------------------------------------
    // System-default admin CRUD + reset (Story 27-10 enablement).
    //
    // Per the product decision (2026-05-25), convention system defaults are
    // DB-managed at runtime by a PLATFORM ADMIN — distinct from the
    // tenant-override surface above. These methods operate ONLY on
    // system-default rows (tenant_id IS NULL) and are named distinct from the
    // tenant-override methods (NOT overloads) so the two tiers can never be
    // confused at a call site. They are the mutation-safe mirror-image of the
    // tenant methods: they NEVER touch tenant-override rows.
    // ------------------------------------------------------------------

    /// <summary>
    /// Create/update the SYSTEM-DEFAULT convention (<c>tenant_id IS NULL</c>)
    /// for <c>(role, action)</c>. Platform-admin only (authz enforced at the
    /// API boundary — Story 27-10). Validates <paramref name="body"/> non-empty,
    /// persists <paramref name="enabled"/> on both insert and update (Story
    /// 27-10 — a platform-admin edit must be able to disable a default;
    /// <see cref="ResetSystemDefaultAsync"/> passes <c>enabled: true</c> for a
    /// canonical restore), stamps <paramref name="adminUserId"/> as the updater
    /// (and creator on insert / first edit of a seeded row), and bumps
    /// <c>Version</c> on update. NEVER mutates a tenant override. Returns
    /// <c>(row, wasCreated)</c> — used by callers to emit the correct DCB audit
    /// event type.
    /// </summary>
    Task<(Convention Row, bool WasCreated)> UpsertSystemDefaultAsync(
        AgentRole role, AgentAction action, string body, bool enabled, Guid adminUserId, CancellationToken ct);

    /// <summary>
    /// Delete the SYSTEM-DEFAULT convention (<c>tenant_id IS NULL</c>) for
    /// <c>(role, action)</c>. Platform-admin only. Returns <c>true</c> when a
    /// row was actually removed, <c>false</c> when no system default existed
    /// (no-op). Callers use the return value to decide whether to emit a DCB
    /// deletion event. NEVER deletes a tenant override.
    /// </summary>
    Task<bool> DeleteSystemDefaultAsync(
        AgentRole role, AgentAction action, CancellationToken ct);

    /// <summary>
    /// Reset the SYSTEM-DEFAULT convention for <c>(role, action)</c> back to the
    /// code baseline (<see cref="ConventionSeedSpecs.DefaultBodyFor"/>) —
    /// re-applies exactly what a fresh seed would write, overwriting any admin
    /// edit. This is the EXPLICIT reset source the seeder no longer provides on
    /// startup. Platform-admin only. Throws <see cref="Tamma.Core.TammaError"/>
    /// (<c>CONVENTION_NOT_A_TAXONOMY_CELL</c>) when <c>(role, action)</c> is not
    /// a taxonomy cell — such a pair has no baseline to reset to.
    ///
    /// <para>Returns <c>(row, wasCreated)</c> — the restored system-default row
    /// and whether it was freshly inserted (as opposed to updating an existing
    /// row). Mirrors <see cref="UpsertSystemDefaultAsync"/>'s return shape so
    /// callers can build a complete HTTP response without a second DB round-trip.</para>
    /// </summary>
    Task<(Convention Row, bool WasCreated)> ResetSystemDefaultAsync(
        AgentRole role, AgentAction action, Guid adminUserId, CancellationToken ct);
}
