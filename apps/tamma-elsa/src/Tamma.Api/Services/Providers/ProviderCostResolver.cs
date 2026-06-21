using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Providers;

/// <summary>
/// Story 34-11 — EffectiveFrom-windowed cost-row resolution over
/// <c>provider_model_prices</c>. Singleton; resolves a scoped
/// <see cref="ControlPlaneDbContext"/> per call through
/// <see cref="IServiceScopeFactory"/> (the established pattern for a singleton
/// that needs EF — mirrors <c>PostgresBudgetConfigProvider</c>). Holds a
/// short-lived snapshot of <c>active</c> rows invalidated on an admin write.
/// </summary>
public sealed class ProviderCostResolver : IProviderCostResolver
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<ProviderCostResolver> _logger;
    private readonly TimeSpan _snapshotTtl;

    private readonly object _gate = new();
    private IReadOnlyList<ProviderModelPrice>? _activeSnapshot;
    private DateTimeOffset _snapshotExpiresAt;

    public ProviderCostResolver(
        IServiceScopeFactory scopeFactory,
        TimeProvider time,
        ILogger<ProviderCostResolver> logger,
        TimeSpan? snapshotTtl = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _snapshotTtl = snapshotTtl ?? TimeSpan.FromSeconds(30);
    }

    public async Task<ProviderModelPrice?> ResolveActiveAsync(
        string provider, string? model, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(provider)) return null;

        var canonical = ProviderRateLookup.Canonicalize(provider);
        var snapshot = await GetActiveSnapshotAsync(ct);

        var rows = snapshot
            .Where(r => string.Equals(r.ProviderKey, canonical, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.EffectiveFrom)
            .ToList();
        if (rows.Count == 0) return null;

        var resolvedModel = ResolveModelKey(model, rows.Select(r => r.Model));
        if (resolvedModel is null) return null;

        return rows.FirstOrDefault(r =>
            string.Equals(r.Model, resolvedModel, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ProviderModelPrice?> ResolveAtAsync(
        string provider, string? model, DateTime atTimestamp, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(provider)) return null;

        var canonical = ProviderRateLookup.Canonicalize(provider);
        var at = atTimestamp.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(atTimestamp, DateTimeKind.Utc)
            : atTimestamp.ToUniversalTime();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();

        // All rows (active + superseded) for this provider — the model-key
        // resolution (default/prefix) needs the candidate model set first.
        var providerRows = await db.ProviderModelPrices
            .AsNoTracking()
            .Where(r => r.ProviderKey == canonical)
            .OrderBy(r => r.EffectiveFrom)
            .ToListAsync(ct);
        if (providerRows.Count == 0) return null;

        // First-model resolution uses the EARLIEST-effective row per model (the
        // seed v1 order) so null/"default" matches the frozen first-model rule.
        var resolvedModel = ResolveModelKey(
            model,
            providerRows
                .GroupBy(r => r.Model)
                .OrderBy(g => g.Min(r => r.EffectiveFrom))
                .Select(g => g.Key));
        if (resolvedModel is null) return null;

        // Most-recent row for the resolved model effective at-or-before the
        // timestamp (active OR superseded) — the time-travel selection.
        var match = providerRows
            .Where(r => string.Equals(r.Model, resolvedModel, StringComparison.OrdinalIgnoreCase)
                        && r.EffectiveFrom <= at)
            .OrderByDescending(r => r.EffectiveFrom)
            .FirstOrDefault();

        if (match is null)
        {
            _logger.LogWarning(
                "No provider cost row effective at {AtTimestamp} for {ProviderKey}/{Model} — pricing at 0",
                at, canonical, resolvedModel);
        }

        return match;
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _activeSnapshot = null;
            _snapshotExpiresAt = default;
        }
        _logger.LogDebug("Provider cost snapshot invalidated");
    }

    /// <summary>
    /// Apply the shared model-key rule (<c>null</c>/<c>"default"</c> → first
    /// model; exact then loose-prefix) over a candidate model set. The candidate
    /// ordering follows the seed order (anthropic, openai, … each in declared
    /// order) so the first-model + prefix rules are deterministic.
    /// </summary>
    private static string? ResolveModelKey(string? requested, IEnumerable<string> candidates)
    {
        var models = candidates.ToList();
        if (models.Count == 0) return null;

        if (string.IsNullOrWhiteSpace(requested) || requested == "default")
        {
            return models[0];
        }

        var exact = models.FirstOrDefault(m =>
            string.Equals(m, requested, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        return models.FirstOrDefault(m =>
            m.StartsWith(requested, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IReadOnlyList<ProviderModelPrice>> GetActiveSnapshotAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_activeSnapshot is not null && now < _snapshotExpiresAt)
            {
                return _activeSnapshot;
            }
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var rows = await db.ProviderModelPrices
            .AsNoTracking()
            .Where(r => r.Status == "active")
            .ToListAsync(ct);

        lock (_gate)
        {
            _activeSnapshot = rows;
            _snapshotExpiresAt = now + _snapshotTtl;
        }
        return rows;
    }
}
