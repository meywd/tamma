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
/// </summary>
/// <param name="Role">Agent role wire string (e.g. <c>developer</c>).</param>
/// <param name="Action">Agent action wire string (e.g. <c>implement-feature</c>).</param>
/// <param name="Body">The resolved convention body (non-empty for a valid pair).</param>
/// <param name="Source">Which tier produced <see cref="Body"/>.</param>
public sealed record ConventionResolution(
    string Role,
    string Action,
    string Body,
    ConventionSource Source);

/// <summary>
/// A single row in the <see cref="IConventionStore.ListAsync"/> result — the
/// resolved convention for one taxonomy cell, plus its source tier.
/// </summary>
/// <param name="Role">Agent role wire string.</param>
/// <param name="Action">Agent action wire string.</param>
/// <param name="Body">Resolved body (tenant override if present-and-enabled, else system default).</param>
/// <param name="Source">Which tier produced <see cref="Body"/>.</param>
public sealed record ConventionSummary(
    string Role,
    string Action,
    string Body,
    ConventionSource Source);

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
    /// </summary>
    Task<Convention?> GetAsync(Guid? tenantId, AgentRole role, AgentAction action, CancellationToken ct);

    /// <summary>
    /// Create/update a tenant override (AC2). Operates ONLY on tenant-override
    /// rows (<c>tenant_id = @tenantId</c>); never mutates system defaults. Sets
    /// <c>CreatedBy</c> / <c>UpdatedBy = userId</c> and bumps <c>Version</c> on
    /// update.
    /// </summary>
    Task UpsertAsync(Guid tenantId, AgentRole role, AgentAction action, string body, Guid userId, CancellationToken ct);

    /// <summary>
    /// Delete a tenant override (AC2). Operates ONLY on tenant-override rows;
    /// never deletes system defaults. A no-op when no override exists.
    /// </summary>
    Task DeleteAsync(Guid tenantId, AgentRole role, AgentAction action, CancellationToken ct);

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
}
