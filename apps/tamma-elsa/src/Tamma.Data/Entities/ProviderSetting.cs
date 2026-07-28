namespace Tamma.Data.Entities;

/// <summary>
/// Story 46-1 — persisted provider model selection (and platform enable flag).
/// One row per <c>(principal, provider)</c>, where the principal is the
/// PLATFORM (both id columns null), a TENANT (SaaS override) or a USER
/// (single-user override) — the <c>prompt_overrides</c> XOR pattern extended
/// with an explicit all-null platform principal.
///
/// <para><b>Control-plane resident in BOTH modes</b> (epic 46 D3a): the
/// resolver runs on hot LLM egress paths (<c>InlineToolLoopRunner</c>,
/// <c>LlmProxyService</c>) that carry a <c>tenantId</c> but no tenant
/// <c>DbContext</c>, so all three row kinds live in one CP table behind one
/// in-process snapshot (<c>ProviderSettingsStore</c>).</para>
///
/// <para><b>Row kinds</b> (tied to <see cref="Scope"/> by
/// <c>ck_provider_settings_scope</c>):</para>
/// <list type="bullet">
///   <item><description><c>platform</c> — <see cref="TenantId"/> and
///     <see cref="UserId"/> both NULL; carries the platform default model
///     and/or the <see cref="Enabled"/> flag.</description></item>
///   <item><description><c>principal</c> — exactly one of
///     <see cref="TenantId"/> / <see cref="UserId"/> set
///     (<c>ck_provider_settings_principal_xor</c>); carries a per-tenant
///     (SaaS) or per-user (single-user) model override. Principal rows are
///     always <see cref="Enabled"/> = true — enable/disable is
///     platform-level only in Epic 46.</description></item>
/// </list>
///
/// <para><b>No FK to <c>tenants</c>/<c>users</c>, deliberately.</b> The Epic 19
/// startup wipe drops the whole CP table set (<c>tenants</c> CASCADE included)
/// on redeploy; <c>provider_settings</c> is EXCLUDED from that DROP list so a
/// model picked in the UI survives redeploys — an FK would cascade the rows
/// away with the wiped principals. Rows for principals that no longer exist
/// are inert (the resolver only reads rows for live principals).</para>
///
/// <para><b>Uniqueness:</b> <c>UNIQUE NULLS NOT DISTINCT
/// (TenantId, UserId, ProviderKey)</c> — at most one row per principal per
/// provider, including the all-null platform principal.</para>
/// </summary>
public class ProviderSetting
{
    public Guid Id { get; set; }

    /// <summary>SaaS principal — set iff <see cref="UserId"/> is NULL and
    /// <see cref="Scope"/> is <c>principal</c>.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>Single-user principal — set iff <see cref="TenantId"/> is NULL
    /// and <see cref="Scope"/> is <c>principal</c>.</summary>
    public Guid? UserId { get; set; }

    /// <summary><c>platform</c> | <c>principal</c>. Derivable from the null
    /// pattern but stored for query legibility; a CHECK ties it to the null
    /// pattern so it cannot lie (46-1 plan D1).</summary>
    public string Scope { get; set; } = null!;

    /// <summary>CANONICAL provider key (never an alias — endpoints normalize
    /// via <c>ProviderCatalog</c> before writing).</summary>
    public string ProviderKey { get; set; } = null!;

    /// <summary>The selected model id. Always non-empty on a model row —
    /// validated at the endpoint AND pinned by <c>ck_provider_settings_model</c>.
    /// NULL on a platform row that only carries the <see cref="Enabled"/>
    /// flag.</summary>
    public string? DefaultModel { get; set; }

    /// <summary>Platform rows only: whether the provider is enabled. Principal
    /// rows are always true (the endpoint rejects attempts to set it).
    /// NOT enforced on the egress path in Epic 46 (allowlist inversion is a
    /// later phase) — persisted + reported so the UIs can hide/grey.</summary>
    public bool Enabled { get; set; } = true;

    public DateTime UpdatedAt { get; set; }

    /// <summary>User id that performed the most recent write (audit).</summary>
    public Guid? UpdatedBy { get; set; }
}
