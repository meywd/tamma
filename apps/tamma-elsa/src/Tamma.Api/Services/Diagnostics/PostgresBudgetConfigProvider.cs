using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Tamma.Api.Services.Diagnostics.Models;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Diagnostics;

/// <summary>
/// Postgres-backed <see cref="IBudgetConfigProvider"/>. Writes go straight to
/// the <c>budget_configs</c> table; reads hit an in-memory cache with a short
/// TTL to shield the hot <c>GetBudgetAsync</c> path from a DB round-trip on
/// every provider invocation.
///
/// <para>
/// Audit finding providers/005 (persistence follow-up): the original
/// <see cref="InMemoryBudgetConfigProvider"/> lost overrides on redeploy —
/// this impl closes that gap. Registered in DI by
/// <c>AddDiagnosticsServices</c> and resolves the per-request
/// <see cref="IBudgetConfigRepository"/> via an <see cref="IServiceScopeFactory"/>
/// so the provider itself can remain a singleton.
/// </para>
///
/// <para>
/// Cache behaviour: an accountId's entry is invalidated on
/// <see cref="SetConfig"/>. Reads cache for <see cref="CacheTtl"/> (default
/// 60s) before refetching. Missing rows (i.e. "no override, fall back to
/// default") are also cached as a sentinel so repeat GETs don't hammer the DB.
/// </para>
/// </summary>
public sealed class PostgresBudgetConfigProvider : IBudgetConfigProvider
{
    /// <summary>How long resolved configs stay cached before a refetch.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly decimal _defaultLimitUsd;
    private readonly double _defaultAlertThreshold;
    private readonly TimeSpan _defaultPeriod;
    private readonly int _defaultPeriodDays;

    private readonly ConcurrentDictionary<Guid, CacheEntry> _cache = new();

    /// <summary>Default period length when configuration omits <c>Budget:PeriodDays</c>.</summary>
    public static readonly TimeSpan DefaultPeriod = TimeSpan.FromDays(30);

    public PostgresBudgetConfigProvider(
        IServiceScopeFactory scopeFactory,
        IConfiguration? configuration)
    {
        _scopeFactory = scopeFactory;
        _defaultLimitUsd = configuration?.GetValue<decimal?>("Budget:LimitUsd") ?? 0m;
        _defaultAlertThreshold = configuration?.GetValue<double?>("Budget:AlertThreshold") ?? 0.8;
        _defaultPeriodDays = configuration?.GetValue<int?>("Budget:PeriodDays") ?? (int)DefaultPeriod.TotalDays;
        _defaultPeriod = TimeSpan.FromDays(Math.Max(1, _defaultPeriodDays));
    }

    /// <inheritdoc />
    public BudgetConfig GetConfig(Guid accountId)
    {
        if (_cache.TryGetValue(accountId, out var entry) && entry.FetchedAt + CacheTtl > DateTime.UtcNow)
        {
            return MaterializeConfig(entry.PersistedLimitUsd, entry.PersistedAlertThreshold, entry.PersistedPeriodDays);
        }

        // Synchronously load from DB. The interface is sync; the caller
        // treats budget lookup as a cheap in-process resolve. Scope the
        // repo because the DbContext is scoped.
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IBudgetConfigRepository>();
        var key = accountId.ToString();
        var row = repo.GetAsync(accountId, key).GetAwaiter().GetResult();

        decimal limit;
        double threshold;
        int periodDays;
        if (row is null)
        {
            limit = _defaultLimitUsd;
            threshold = _defaultAlertThreshold;
            periodDays = _defaultPeriodDays;
        }
        else
        {
            limit = row.LimitUsd;
            threshold = row.AlertThreshold;
            periodDays = row.PeriodDays;
        }

        _cache[accountId] = new CacheEntry(DateTime.UtcNow, limit, threshold, periodDays);
        return MaterializeConfig(limit, threshold, periodDays);
    }

    /// <inheritdoc />
    public void SetConfig(Guid accountId, BudgetConfig config)
    {
        // Invert the PeriodStart/End pair back into a day-count. The in-memory
        // API still expresses periods as (start, end) — consistent with the TS
        // shape — so we compute the days here.
        var periodDays = Math.Max(1, (int)Math.Round((config.PeriodEnd - config.PeriodStart).TotalDays));

        var entity = new Tamma.Data.Entities.BudgetConfig
        {
            TenantId = accountId,
            AccountId = accountId.ToString(),
            LimitUsd = config.LimitUsd,
            AlertThreshold = config.AlertThreshold,
            PeriodDays = periodDays,
        };

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IBudgetConfigRepository>();
        repo.UpsertAsync(entity).GetAwaiter().GetResult();

        // Invalidate cache so the next GetConfig refetches from DB.
        _cache.TryRemove(accountId, out _);
    }

    private BudgetConfig MaterializeConfig(decimal limit, double threshold, int periodDays)
    {
        var now = DateTime.UtcNow;
        var period = TimeSpan.FromDays(Math.Max(1, periodDays));
        return new BudgetConfig(
            LimitUsd: limit,
            AlertThreshold: threshold,
            PeriodStart: now - period,
            PeriodEnd: now + period);
    }

    private readonly record struct CacheEntry(
        DateTime FetchedAt,
        decimal PersistedLimitUsd,
        double PersistedAlertThreshold,
        int PersistedPeriodDays);
}
