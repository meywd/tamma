namespace Tamma.Data.Entities;

/// <summary>
/// Control-plane margin policy applied by the cost->price engine (Story 34-5).
/// Versioned: an edit SUPERSEDES the prior active row rather than mutating it,
/// so a usage event is always priced under the policy that was active at its
/// timestamp (reproducible historical invoices — the sell-side companion of the
/// 34-11 <see cref="ProviderModelPrice"/> cost-side versioning). Platform-owned
/// global config — there are NO per-tenant margin rows; only
/// <c>PlatformOwnerAccess</c> may mutate these in SaaS, and the sole user owns
/// them in single-user mode.
///
/// <para>Resolution is provider-override -> plan -> global (most specific wins).
/// The cost basis itself is NOT here — it comes from
/// <c>IProviderPricingService</c> (34-11); this row only supplies the margin
/// applied on top.</para>
/// </summary>
public class MarginPolicy
{
    /// <summary>
    /// Primary key. New admin rows get a v4 GUID (DB default
    /// <c>gen_random_uuid()</c> / <c>Guid.NewGuid()</c>); ONLY the seeder bakes a
    /// deterministic, UUIDv7-shaped id (for insert-missing-only idempotency).
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>Scope discriminator: <c>global</c> | <c>plan</c> | <c>provider</c>. A CHECK pins the enum.</summary>
    public string Scope { get; set; } = null!;

    /// <summary>
    /// NULL for <c>global</c>; the plan slug for <c>plan</c> scope; the canonical
    /// provider key for <c>provider</c> scope.
    /// </summary>
    public string? RefKey { get; set; }

    /// <summary>
    /// Multiplicative markup on the provider cost basis (e.g. <c>1.3</c> = +30%).
    /// Nullable — at least one of this / <see cref="FixedUsdPer1M"/> is set
    /// (CHECK <c>ck_margin_policies_has_knob</c>). Null is treated as
    /// <c>1.0</c> (no multiplier) by the engine.
    /// </summary>
    public decimal? MarkupMultiplier { get; set; }

    /// <summary>
    /// Additive USD per 1,000,000 total (input+output) tokens. Nullable — at
    /// least one of this / <see cref="MarkupMultiplier"/> is set. Null is treated
    /// as <c>0</c> by the engine.
    /// </summary>
    public decimal? FixedUsdPer1M { get; set; }

    /// <summary>UTC instant this policy became effective (the resolution-window key).</summary>
    public DateTime EffectiveFrom { get; set; }

    /// <summary>Lifecycle: <c>active</c> | <c>superseded</c>. A CHECK pins the enum.</summary>
    public string Status { get; set; } = "active";

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
