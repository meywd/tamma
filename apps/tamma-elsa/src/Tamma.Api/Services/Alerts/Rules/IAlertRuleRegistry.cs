using Microsoft.EntityFrameworkCore;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Alerts.Rules;

/// <summary>
/// Story 5.6 (Wave C.2) — caches the current set of enabled
/// <see cref="IAlertRule"/> instances + their channel linkages. Hot-
/// reloaded periodically (30s by default) or on admin CRUD bumps via
/// <see cref="RefreshAsync"/>.
///
/// <para>The registry is the evaluator's single source of truth for
/// "which rules match this event type"; admins who flip
/// <c>is_enabled</c> on the table see the change within one refresh
/// cycle or immediately via the CRUD-bump path.</para>
/// </summary>
public interface IAlertRuleRegistry
{
    /// <summary>
    /// Current rules subscribed to <paramref name="eventType"/>
    /// (exact match plus the wildcard <c>*</c>). Enabled-only.
    /// </summary>
    IReadOnlyList<DatabaseBackedAlertRule> GetRulesForEventType(string eventType);

    /// <summary>Refresh the in-memory cache from the database.</summary>
    Task RefreshAsync(CancellationToken ct);

    /// <summary>
    /// Total number of cached rules — exposed for tests + admin
    /// health endpoints.
    /// </summary>
    int Count { get; }
}

/// <summary>
/// Default <see cref="IAlertRuleRegistry"/> implementation. Loads
/// enabled rules from <c>alert_rules</c> + their predicate JSON,
/// bucket-indexes by event type for O(1) lookup on the hot path.
///
/// <para>A malformed predicate on a single row is logged and skipped
/// — one bad row doesn't take down the whole evaluator. The row
/// stays in the DB for admin inspection; the admin UI will surface
/// the validation error via the same parser.</para>
/// </summary>
public sealed class AlertRuleRegistry : IAlertRuleRegistry
{
    private readonly IServiceProvider _services;
    private readonly ILogger<AlertRuleRegistry> _logger;
    private readonly object _lock = new();

    // Copy-on-write: the hot path reads without locking; writers
    // build a new snapshot under the lock + swap references.
    private volatile Snapshot _snapshot = Snapshot.Empty;

    public AlertRuleRegistry(
        IServiceProvider services,
        ILogger<AlertRuleRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _logger = logger;
    }

    public int Count => _snapshot.All.Count;

    public IReadOnlyList<DatabaseBackedAlertRule> GetRulesForEventType(
        string eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        var snap = _snapshot;
        var specific = snap.ByEventType.TryGetValue(eventType, out var s)
            ? s
            : Array.Empty<DatabaseBackedAlertRule>();
        var wild = snap.Wildcard;
        if (wild.Count == 0) return specific;
        if (specific.Count == 0) return wild;
        var combined = new List<DatabaseBackedAlertRule>(
            specific.Count + wild.Count);
        combined.AddRange(specific);
        combined.AddRange(wild);
        return combined;
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<ControlPlaneDbContext>();

        var rows = await db.AlertRules
            .AsNoTracking()
            .Where(r => r.IsEnabled)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var byType = new Dictionary<string, List<DatabaseBackedAlertRule>>(
            StringComparer.Ordinal);
        var wildcard = new List<DatabaseBackedAlertRule>();
        var all = new List<DatabaseBackedAlertRule>(rows.Count);

        foreach (var row in rows)
        {
            DatabaseBackedAlertRule rule;
            try
            {
                rule = new DatabaseBackedAlertRule(row);
            }
            catch (InvalidAlertRulePredicateException ex)
            {
                _logger.LogError(ex,
                    "Alert rule {RuleId} ('{Name}') has an invalid " +
                    "predicate; skipping. Fix via admin UI.",
                    row.Id, row.Name);
                continue;
            }
            all.Add(rule);
            if (rule.EventType == "*")
            {
                wildcard.Add(rule);
                continue;
            }
            if (!byType.TryGetValue(rule.EventType, out var list))
            {
                list = new List<DatabaseBackedAlertRule>();
                byType[rule.EventType] = list;
            }
            list.Add(rule);
        }

        lock (_lock)
        {
            _snapshot = new Snapshot(byType.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<DatabaseBackedAlertRule>)kv.Value,
                StringComparer.Ordinal),
                wildcard,
                all);
        }
    }

    private sealed record Snapshot(
        IReadOnlyDictionary<string, IReadOnlyList<DatabaseBackedAlertRule>> ByEventType,
        IReadOnlyList<DatabaseBackedAlertRule> Wildcard,
        IReadOnlyList<DatabaseBackedAlertRule> All)
    {
        public static Snapshot Empty { get; } = new(
            new Dictionary<string, IReadOnlyList<DatabaseBackedAlertRule>>(
                StringComparer.Ordinal),
            Array.Empty<DatabaseBackedAlertRule>(),
            Array.Empty<DatabaseBackedAlertRule>());
    }
}
