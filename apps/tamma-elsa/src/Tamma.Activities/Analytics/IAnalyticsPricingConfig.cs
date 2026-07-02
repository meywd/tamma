namespace Tamma.Activities.Analytics;

/// <summary>
/// Story 36-2 — read-only margin seam consumed by
/// <see cref="ComputeTenantDimensionalRollupActivity"/> to derive
/// <c>PlatformBilledUsd = CostUsd * (1 + margin)</c> for
/// <see cref="Tamma.Core.Enums.CostBasis.Platform"/> rows.
///
/// <para>This story does <b>not</b> own the margin math — it is produced by
/// Story 36-7 (pricing/markup config). This interface is the consumption
/// seam so the projection stays green before 36-7 lands: when no real
/// implementation is registered, <see cref="NullAnalyticsPricingConfig"/>
/// supplies a zero margin (Tamma bills exactly cost) and logs a WARN.</para>
///
/// <para>The margin is a fraction, not a percentage — <c>0.20m</c> means a
/// 20% markup, so a $1.00 platform cost bills at $1.20. A margin of
/// <c>0m</c> means no markup. BYOK rows are never marked up (the activity
/// short-circuits <c>PlatformBilledUsd = 0</c> before calling this seam).</para>
/// </summary>
public interface IAnalyticsPricingConfig
{
    /// <summary>
    /// The platform markup margin (fraction, e.g. <c>0.20m</c> = 20%) applied
    /// to the raw <c>CostUsd</c> for a platform-fronted call on the given
    /// <paramref name="provider"/>. Never negative; a provider with no
    /// configured margin yields <c>0m</c>.
    /// </summary>
    decimal MarginFor(string provider);
}
