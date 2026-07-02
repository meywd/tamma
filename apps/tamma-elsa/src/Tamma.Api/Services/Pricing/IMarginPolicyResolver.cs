using Tamma.Data.Entities;

namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-5 — resolves the applicable <see cref="MarginPolicy"/> for a usage
/// line. This is the IMPURE half of the engine (the ONLY DB read); the pure
/// <see cref="IUsagePricingEngine"/> takes the resolved policy as an input.
///
/// <para>Resolution order is strictly <b>provider-override -> plan -> global</b>:
/// the most-specific scope with a policy whose <c>EffectiveFrom &lt;=
/// atTimestamp</c> wins. Selection within a scope is timestamp-effective — the
/// row with the greatest <c>EffectiveFrom &lt;= atTimestamp</c> (which may now be
/// <c>superseded</c> but was active during that window), so a historical event
/// prices under the policy that was active at its <c>OccurredAt</c>, not the
/// latest. If NO scope has a matching policy, it throws
/// <c>PRICING.MARGIN.NO_POLICY</c> — it never silently prices at a zero margin
/// (the no-empty-fallback rule applied to pricing).</para>
/// </summary>
public interface IMarginPolicyResolver
{
    /// <summary>
    /// Resolve the margin policy for <paramref name="provider"/> under
    /// <paramref name="planSlug"/> (nullable — single-user / unassigned tenants
    /// skip the plan scope) effective at <paramref name="atTimestamp"/>. Throws
    /// <c>PRICING.MARGIN.NO_POLICY</c> (severity High) when no policy matches at
    /// any scope.
    /// </summary>
    Task<MarginPolicy> ResolveAsync(
        string provider, string? planSlug, DateTime atTimestamp, CancellationToken ct = default);
}
