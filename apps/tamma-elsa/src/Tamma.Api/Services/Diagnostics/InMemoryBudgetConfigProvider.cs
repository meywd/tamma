using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Tamma.Api.Services.Diagnostics.Models;

namespace Tamma.Api.Services.Diagnostics;

/// <summary>
/// Default in-memory budget registry. Safe for singleton registration —
/// reads and writes are backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
///
/// <para>
/// Finding 005 (audit): the previous implementation always returned
/// <c>LimitUsd = 0m</c>, which silently disabled budget enforcement for
/// every tenant. The provider now seeds a global default from
/// configuration (<c>Budget:LimitUsd</c>, <c>Budget:AlertThreshold</c>,
/// <c>Budget:PeriodDays</c>) and accepts per-tenant overrides through
/// <see cref="SetConfig"/>. The <c>PUT /api/providers/budget/{tenantId}</c>
/// endpoint binds to <see cref="SetConfig"/> so SaaS callers can persist
/// budget caps from the dashboard.
/// </para>
///
/// <para>
/// Persistence to Postgres is deferred (it's tracked separately as a
/// follow-up to finding 005). The in-memory store is sufficient for
/// single-replica deployments and for tests; a redeploy clears tenant
/// overrides back to the configured default. Multi-replica deployments
/// should mirror the Postgres-backed implementation tracked in the
/// budget-persistence story.
/// </para>
/// </summary>
public sealed class InMemoryBudgetConfigProvider : IBudgetConfigProvider
{
    private readonly ConcurrentDictionary<Guid, BudgetConfig> _configs = new();
    private readonly decimal _defaultLimitUsd;
    private readonly double _defaultAlertThreshold;
    private readonly TimeSpan _defaultPeriod;

    /// <summary>Default period length when configuration omits <c>Budget:PeriodDays</c>.</summary>
    public static readonly TimeSpan DefaultPeriod = TimeSpan.FromDays(30);

    public InMemoryBudgetConfigProvider() : this(null) { }

    public InMemoryBudgetConfigProvider(IConfiguration? configuration)
    {
        // Read defaults from configuration if available; otherwise fall back
        // to zero (i.e. no enforcement) so dev / test setups still work.
        _defaultLimitUsd = configuration?.GetValue<decimal?>("Budget:LimitUsd") ?? 0m;
        _defaultAlertThreshold = configuration?.GetValue<double?>("Budget:AlertThreshold") ?? 0.8;
        var days = configuration?.GetValue<int?>("Budget:PeriodDays") ?? (int)DefaultPeriod.TotalDays;
        _defaultPeriod = TimeSpan.FromDays(Math.Max(1, days));
    }

    /// <inheritdoc />
    public BudgetConfig GetConfig(Guid accountId)
    {
        if (_configs.TryGetValue(accountId, out var cfg))
            return cfg;

        var now = DateTime.UtcNow;
        return new BudgetConfig(
            LimitUsd: _defaultLimitUsd,
            AlertThreshold: _defaultAlertThreshold,
            PeriodStart: now - _defaultPeriod,
            PeriodEnd: now + _defaultPeriod);
    }

    /// <inheritdoc />
    public void SetConfig(Guid accountId, BudgetConfig config)
    {
        _configs[accountId] = config;
    }
}
