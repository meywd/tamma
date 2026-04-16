using System.Collections.Concurrent;
using Tamma.Api.Services.Diagnostics.Models;

namespace Tamma.Api.Services.Diagnostics;

/// <summary>
/// Default in-memory budget registry. Safe for singleton registration —
/// reads and writes are backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// </summary>
public sealed class InMemoryBudgetConfigProvider : IBudgetConfigProvider
{
    private readonly ConcurrentDictionary<Guid, BudgetConfig> _configs = new();

    /// <summary>Default period length when no config is registered.</summary>
    public static readonly TimeSpan DefaultPeriod = TimeSpan.FromDays(30);

    /// <inheritdoc />
    public BudgetConfig GetConfig(Guid accountId)
    {
        if (_configs.TryGetValue(accountId, out var cfg))
            return cfg;

        // Sensible fallback — zero cap, effectively no budget enforcement.
        var now = DateTime.UtcNow;
        return new BudgetConfig(
            LimitUsd: 0m,
            AlertThreshold: 0.8,
            PeriodStart: now - DefaultPeriod,
            PeriodEnd: now + DefaultPeriod);
    }

    /// <inheritdoc />
    public void SetConfig(Guid accountId, BudgetConfig config)
    {
        _configs[accountId] = config;
    }
}
