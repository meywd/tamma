using Tamma.Data.Entities;

namespace Tamma.Api.Services.Providers;

/// <summary>
/// Story 34-11 — EffectiveFrom-windowed cost-row resolution over the
/// <c>provider_model_prices</c> table. Impure / DB-backed (a sibling of the
/// pure <see cref="IProviderPricingService"/> seam): given a
/// <c>(provider, model)</c> request it returns the matching cost rows so a
/// caller can price under the rate active at any point in time.
///
/// <para>The lookup preserves the frozen table's load-bearing quirks via the
/// shared <see cref="ProviderRateLookup"/>: alias normalization,
/// <c>null</c>/<c>"default"</c> → first model, exact then loose-prefix match.</para>
/// </summary>
public interface IProviderCostResolver
{
    /// <summary>
    /// Resolve the currently-<c>active</c> price row for <c>(provider, model)</c>,
    /// or <c>null</c> when the pair is unknown (caller treats null as 0m / unknown).
    /// </summary>
    Task<ProviderModelPrice?> ResolveActiveAsync(
        string provider, string? model, CancellationToken ct = default);

    /// <summary>
    /// Resolve the price row effective at <paramref name="atTimestamp"/>: the
    /// most-recent row (active OR superseded) for <c>(provider, model)</c> whose
    /// <c>EffectiveFrom &lt;= atTimestamp</c>. <c>null</c> when no such row
    /// exists (unknown pair, or the timestamp predates v1).
    /// </summary>
    Task<ProviderModelPrice?> ResolveAtAsync(
        string provider, string? model, DateTime atTimestamp, CancellationToken ct = default);

    /// <summary>Invalidate any cached snapshot (called on an admin write).</summary>
    void Invalidate();
}
