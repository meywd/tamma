using Microsoft.Extensions.Logging;

namespace Tamma.Activities.Analytics;

/// <summary>
/// Story 36-2 — zero-margin fallback for <see cref="IAnalyticsPricingConfig"/>.
/// Registered in DI so the dimensional rollup is green before Story 36-7
/// (pricing/markup config) lands.
///
/// <para>Every call to <see cref="MarginFor"/> returns <c>0m</c> (Tamma bills
/// exactly cost, no markup) and logs a WARN once per provider so the runbook
/// can see the projection is running without a real price book. It never
/// throws and never hardcodes a non-zero margin — a real margin only ever
/// comes from the 36-7 implementation replacing this seam.</para>
/// </summary>
public sealed class NullAnalyticsPricingConfig : IAnalyticsPricingConfig
{
    private readonly ILogger<NullAnalyticsPricingConfig>? _logger;
    private readonly HashSet<string> _warned = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public NullAnalyticsPricingConfig(ILogger<NullAnalyticsPricingConfig>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public decimal MarginFor(string provider)
    {
        var key = provider ?? string.Empty;

        bool firstTime;
        lock (_gate)
        {
            firstTime = _warned.Add(key);
        }

        if (firstTime)
        {
            _logger?.LogWarning(
                "analytics.pricing.unavailable provider={Provider} — Story 36-7 pricing config "
                + "not wired; PlatformBilledUsd computed with zero margin (billed = cost).",
                key);
        }

        return 0m;
    }
}
