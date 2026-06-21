using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Data;

namespace Tamma.Api.Services.Providers;

/// <summary>
/// Story 34-11 — the DB-backed <see cref="IProviderPricingService"/>. Reads the
/// <c>provider_model_prices</c> table (active rows) and reproduces the frozen
/// table's behaviour VERBATIM via the shared <see cref="ProviderRateLookup"/>:
/// alias normalization, <c>null</c>/<c>"default"</c> → first model, exact then
/// loose-prefix match, and the unknown → <c>0m</c> / <c>IsKnown=false</c>
/// robustness contract (never throws on an unpriced model).
///
/// <para>Registered IN PLACE OF the frozen <see cref="ProviderPricingService"/>
/// (a one-line DI swap). The <see cref="IProviderPricingService"/> interface is
/// UNCHANGED; the EffectiveFrom-aware path lives on the sibling
/// <see cref="IProviderCostResolver"/> (used by the metering path), not on this
/// seam.</para>
///
/// <para><b>Fail-loud empty-table fallback.</b> If the cost table is empty at
/// read time (e.g. a misconfigured boot before the seeder ran) this service
/// falls back to the retained frozen rate sheet and logs a WARN — it NEVER
/// silently prices at 0 (consistent with <c>feedback_resolution_no_empty_fallback</c>).</para>
///
/// <para>Holds a short-lived in-memory snapshot of active rows (per-provider
/// per-token rate maps) invalidated on an admin write via
/// <see cref="IProviderCostResolver.Invalidate"/> + its own TTL.</para>
/// </summary>
public sealed class DbProviderPricingService : IProviderPricingService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IProviderCostResolver _resolver;
    private readonly ProviderPricingService _frozenFallback;
    private readonly TimeProvider _time;
    private readonly ILogger<DbProviderPricingService> _logger;
    private readonly TimeSpan _snapshotTtl;

    private readonly object _gate = new();
    // canonical provider key → (model → per-token rate). Ordering of the inner
    // dictionary follows the row order so default/prefix rules are deterministic.
    private IReadOnlyDictionary<string, IReadOnlyDictionary<string, ProviderRateLookup.Rate>>? _snapshot;
    private DateTimeOffset _snapshotExpiresAt;
    private bool _warnedEmpty;

    public DbProviderPricingService(
        IServiceScopeFactory scopeFactory,
        IProviderCostResolver resolver,
        TimeProvider time,
        ILogger<DbProviderPricingService> logger,
        ProviderPricingService? frozenFallback = null,
        TimeSpan? snapshotTtl = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _frozenFallback = frozenFallback ?? new ProviderPricingService();
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _snapshotTtl = snapshotTtl ?? TimeSpan.FromSeconds(30);
    }

    public decimal Compute(string provider, string? model, int inputTokens, int outputTokens)
    {
        if (string.IsNullOrWhiteSpace(provider)) return 0m;
        if (!TryGetRate(provider, model, out var rate, out var usedFallback))
        {
            // Unknown (provider, model) — robustness contract: 0m, never throw.
            return usedFallback
                ? _frozenFallback.Compute(provider, model, inputTokens, outputTokens)
                : 0m;
        }
        return ProviderRateLookup.Cost(rate, inputTokens, outputTokens);
    }

    public bool IsKnown(string provider, string? model)
    {
        if (string.IsNullOrWhiteSpace(provider)) return false;
        if (TryGetRate(provider, model, out _, out var usedFallback)) return true;
        return usedFallback && _frozenFallback.IsKnown(provider, model);
    }

    /// <summary>
    /// Story 34-11 — the EffectiveFrom-windowed cost used by the metering path
    /// (34-5 / 32-9). Selects the cost row effective at <paramref name="atTimestamp"/>
    /// so a usage event prices under the rate active when the call happened.
    /// Unknown / no-effective-row → <c>0m</c> (never throws).
    /// </summary>
    public async Task<decimal> ComputeAtAsync(
        string provider, string? model, int inputTokens, int outputTokens,
        DateTime atTimestamp, CancellationToken ct = default)
    {
        var row = await _resolver.ResolveAtAsync(provider, model, atTimestamp, ct);
        if (row is null) return 0m;

        var rate = ToPerToken(row.InputUsdPer1M, row.OutputUsdPer1M);
        return ProviderRateLookup.Cost(rate, inputTokens, outputTokens);
    }

    private bool TryGetRate(
        string provider, string? model, out ProviderRateLookup.Rate rate, out bool usedFallback)
    {
        rate = default;
        usedFallback = false;

        var snapshot = GetSnapshot();
        if (snapshot.Count == 0)
        {
            // Empty table → fail-loud fallback to the frozen sheet.
            usedFallback = true;
            return false;
        }

        var canonical = ProviderRateLookup.Canonicalize(provider);
        if (!snapshot.TryGetValue(canonical, out var modelMap))
        {
            return false;
        }

        return ProviderRateLookup.TryGetRate(provider, model, modelMap, out rate);
    }

    private static ProviderRateLookup.Rate ToPerToken(decimal in1M, decimal out1M) =>
        new(in1M / 1_000_000m, out1M / 1_000_000m);

    private IReadOnlyDictionary<string, IReadOnlyDictionary<string, ProviderRateLookup.Rate>> GetSnapshot()
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_snapshot is not null && now < _snapshotExpiresAt)
            {
                return _snapshot;
            }
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();

        var rows = db.ProviderModelPrices
            .AsNoTracking()
            .Where(r => r.Status == "active")
            .OrderBy(r => r.ProviderKey).ThenBy(r => r.EffectiveFrom)
            .ToList();

        var map = new Dictionary<string, IReadOnlyDictionary<string, ProviderRateLookup.Rate>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var group in rows.GroupBy(r => r.ProviderKey, StringComparer.OrdinalIgnoreCase))
        {
            var inner = new Dictionary<string, ProviderRateLookup.Rate>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in group)
            {
                inner[r.Model] = ToPerToken(r.InputUsdPer1M, r.OutputUsdPer1M);
            }
            map[group.Key] = inner;
        }

        if (map.Count == 0 && !_warnedEmpty)
        {
            _logger.LogWarning(
                "provider_model_prices is empty — falling back to the frozen cost rate sheet. "
                + "Run ProviderPricingSeeder.SeedAsync to populate the cost book.");
            _warnedEmpty = true;
        }
        else if (map.Count > 0)
        {
            _warnedEmpty = false;
        }

        lock (_gate)
        {
            _snapshot = map;
            _snapshotExpiresAt = now + _snapshotTtl;
        }
        return map;
    }
}
