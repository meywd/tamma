using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Persistence seam for the prompt-overrides table. Story 27-2 introduces
/// the parallel tenant-scoped surface — single-user mode rows are keyed on
/// <c>UserId</c>, SaaS mode rows are keyed on <c>TenantId</c>. The DB
/// <c>principal_xor</c> CHECK constraint enforces exactly-one-of, so a
/// caller never has to disambiguate at runtime.
/// </summary>
public interface IPromptRepository
{
    // ───────────────────────── single-user mode ─────────────────────────

    /// <summary>
    /// Read a single-user override (<c>tenant_id IS NULL</c>) for the given
    /// <paramref name="userId"/>. Returns null when no row matches.
    /// </summary>
    Task<PromptOverride?> GetAsync(Guid? userId, string scope, string? role, string? action);

    /// <summary>
    /// Upsert a prompt override. Returns the persisted entity and a flag
    /// indicating whether this was a fresh insert (<c>true</c>) or an update
    /// of an existing row (<c>false</c>). The flag drives DCB event emission
    /// (CREATED vs UPDATED) at the endpoint layer. Routes to the user-scoped
    /// row when <see cref="PromptOverride.UserId"/> is set, or the
    /// tenant-scoped row when <see cref="PromptOverride.TenantId"/> is set —
    /// per the <c>principal_xor</c> CHECK exactly one MUST be non-null.
    /// </summary>
    Task<(PromptOverride Entity, bool WasCreated)> UpsertAsync(PromptOverride prompt, Guid? actingUserId = null);

    Task<bool> DeleteAsync(Guid? userId, string scope, string? role, string? action);
    Task<List<PromptOverride>> ListAsync(Guid? userId);

    // ───────────────────────── SaaS mode (Story 27-2) ───────────────────

    /// <summary>
    /// Read a tenant-scoped override (<c>user_id IS NULL</c>) for the given
    /// <paramref name="tenantId"/>. Returns null when no row matches.
    /// SaaS-mode resolution uses this — there is intentionally NO per-user
    /// fallback layer on top of tenant overrides (CLAUDE.md "Prompt Store
    /// Architecture / Resolution Order — SaaS mode").
    /// </summary>
    Task<PromptOverride?> GetByTenantAsync(Guid tenantId, string scope, string? role, string? action);

    /// <summary>
    /// Delete a tenant-scoped override. Returns false when no row matched
    /// — caller falls back to the system default.
    /// </summary>
    Task<bool> DeleteByTenantAsync(Guid tenantId, string scope, string? role, string? action);

    /// <summary>
    /// List every tenant-scoped override for <paramref name="tenantId"/>.
    /// Used by GET /api/prompts in SaaS mode to render the "what has the
    /// org customised?" view.
    /// </summary>
    Task<List<PromptOverride>> ListByTenantAsync(Guid tenantId);
}
