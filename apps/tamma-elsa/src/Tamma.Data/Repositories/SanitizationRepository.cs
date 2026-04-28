using Tamma.Data.Abstractions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tamma.Data.Defaults;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Postgres-backed implementation of <see cref="ISanitizationRepository"/>.
///
/// <para>
/// The physical storage is a single row per tenant in the
/// <c>sanitization_rules</c> table; the per-rule array is serialized as JSONB
/// under the <see cref="SanitizationRule.Rules"/> column.
/// </para>
///
/// <para>
/// Story 28-1 PR A (Decision #1, <c>.dev/decisions/story-28-1-design-calls.md</c>):
/// the legacy <c>sanitization_rules.tenant_id IS NULL</c> CP row is no longer
/// the source of platform defaults. Reads with <c>tenantId == null</c>
/// resolve to <see cref="ISanitizationDefaultsProvider.DefaultRules"/>
/// (whose canonical impl wraps <c>SystemSanitizationRules.DefaultRules</c>);
/// writes with <c>tenantId == null</c> are dropped with a structured warning.
/// </para>
///
/// <para>Tenant-scoped reads/writes (non-null <c>TenantId</c>) continue to
/// flow through <see cref="ITenantDbContextFactory"/>.</para>
/// </summary>
public class SanitizationRepository : ISanitizationRepository
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly ITenantDbContextFactory _factory;
    private readonly ISanitizationDefaultsProvider _defaults;
    private readonly ILogger<SanitizationRepository>? _logger;

    public SanitizationRepository(
        ITenantDbContextFactory factory,
        IEnumerable<ISanitizationDefaultsProvider> defaults,
        ILogger<SanitizationRepository>? logger = null)
    {
        _factory = factory;
        _defaults = defaults?.FirstOrDefault() ?? EmptyDefaultsProvider.Instance;
        _logger = logger;
    }

    private sealed class EmptyDefaultsProvider : ISanitizationDefaultsProvider
    {
        public static readonly EmptyDefaultsProvider Instance = new();
        public IReadOnlyList<SanitizationRuleDefinition> DefaultRules { get; }
            = Array.Empty<SanitizationRuleDefinition>();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SanitizationRuleDefinition>> GetRulesAsync(Guid? tenantId)
    {
        var overrides = await LoadOverridesAsync(tenantId).ConfigureAwait(false);

        var byName = _defaults.DefaultRules
            .ToDictionary(r => r.Name, r => r, StringComparer.Ordinal);

        foreach (var tenantRule in overrides)
        {
            byName[tenantRule.Name] = tenantRule;
        }

        return byName.Values.ToList();
    }

    /// <inheritdoc />
    public async Task UpsertRuleAsync(Guid? tenantId, SanitizationRuleDefinition rule)
    {
        if (rule is null) throw new ArgumentNullException(nameof(rule));
        if (string.IsNullOrWhiteSpace(rule.Name))
            throw new ArgumentException("Rule name must be non-empty", nameof(rule));

        if (tenantId is Guid tid)
        {
            await using var db = await _factory.CreateAsync(tid);
            var row = await LoadOrCreateRowAsync(db.SanitizationRules, db, tenantId);
            var current = DeserializeRules(row.Rules).ToList();
            var idx = current.FindIndex(r => string.Equals(r.Name, rule.Name, StringComparison.Ordinal));
            if (idx >= 0) current[idx] = rule;
            else current.Add(rule);
            row.Rules = JsonSerializer.Serialize(current, JsonOpts);
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return;
        }

        // Story 28-1 PR A: platform-default writes are no-ops. Defaults live
        // in SystemSanitizationRules; pretending to persist the override
        // would silently shadow code defaults next time the row reappeared.
        _logger?.LogWarning(
            "SanitizationRepository.UpsertRuleAsync called with tenantId=null " +
            "for rule={RuleName} — platform defaults moved to code per Story " +
            "28-1 Decision #1. Discarding the requested rule.",
            rule.Name);
    }

    /// <inheritdoc />
    public async Task DeleteRuleAsync(Guid? tenantId, string ruleName)
    {
        if (string.IsNullOrWhiteSpace(ruleName)) return;

        if (tenantId is Guid tid)
        {
            await using var db = await _factory.CreateAsync(tid);
            var row = await db.SanitizationRules.IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.TenantId == tenantId);
            if (row is null) return;
            var current = DeserializeRules(row.Rules).ToList();
            var removed = current.RemoveAll(r => string.Equals(r.Name, ruleName, StringComparison.Ordinal));
            if (removed == 0) return;
            row.Rules = JsonSerializer.Serialize(current, JsonOpts);
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return;
        }

        // Story 28-1 PR A: platform-default deletes are no-ops.
        _logger?.LogWarning(
            "SanitizationRepository.DeleteRuleAsync called with tenantId=null " +
            "for rule={RuleName} — defaults are code-resident; nothing to " +
            "remove (Story 28-1 Decision #1).",
            ruleName);
    }

    /// <inheritdoc />
    public async Task ReplaceRulesAsync(Guid? tenantId, IEnumerable<SanitizationRuleDefinition> rules)
    {
        var list = rules?.ToList() ?? new List<SanitizationRuleDefinition>();

        if (tenantId is Guid tid)
        {
            await using var db = await _factory.CreateAsync(tid);
            var row = await LoadOrCreateRowAsync(db.SanitizationRules, db, tenantId);
            row.Rules = JsonSerializer.Serialize(list, JsonOpts);
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return;
        }

        // Story 28-1 PR A: bulk platform-default writes are no-ops.
        _logger?.LogWarning(
            "SanitizationRepository.ReplaceRulesAsync called with tenantId=null " +
            "(count={Count}) — platform defaults moved to code per Story 28-1 " +
            "Decision #1. Discarding the requested rule set.",
            list.Count);
    }

    /// <inheritdoc />
    public async Task<SanitizationRule?> GetRawAsync(Guid? tenantId)
    {
        if (tenantId is Guid tid)
        {
            await using var db = await _factory.CreateAsync(tid);
            return await db.SanitizationRules.IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.TenantId == tenantId);
        }
        // Story 28-1 PR A: synthesise a snapshot row from the in-code defaults
        // so callers that want the raw shape (e.g. admin dashboards) still
        // observe non-null content. The Id is Guid.Empty to signal "synthetic
        // / not persisted" — callers can treat that as a sentinel.
        var defaultsJson = JsonSerializer.Serialize(_defaults.DefaultRules, JsonOpts);
        return SanitizationRuleDefaults.Snapshot(defaultsJson);
    }

    // ─── helpers ────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<SanitizationRuleDefinition>> LoadOverridesAsync(Guid? tenantId)
    {
        if (tenantId is not Guid tid)
        {
            // Story 28-1 PR A: there are no platform-default overrides — the
            // defaults themselves are returned by GetRulesAsync's merge step.
            return Array.Empty<SanitizationRuleDefinition>();
        }
        await using var db = await _factory.CreateAsync(tid);
        var row = await db.SanitizationRules.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId);
        return row is null
            ? Array.Empty<SanitizationRuleDefinition>()
            : DeserializeRules(row.Rules);
    }

    private static async Task<SanitizationRule> LoadOrCreateRowAsync(
        DbSet<SanitizationRule> set, DbContext ctx, Guid? tenantId)
    {
        var row = await set.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId);
        if (row is not null) return row;

        row = new SanitizationRule
        {
            TenantId = tenantId,
            Rules = SanitizationRuleDefaults.EmptyRulesJson,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        set.Add(row);
        return row;
    }

    private static IReadOnlyList<SanitizationRuleDefinition> DeserializeRules(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<SanitizationRuleDefinition>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<SanitizationRuleDefinition>();
            }
            var arr = doc.RootElement
                .Deserialize<List<SanitizationRuleDefinition>>(JsonOpts);
            return arr ?? (IReadOnlyList<SanitizationRuleDefinition>)Array.Empty<SanitizationRuleDefinition>();
        }
        catch (JsonException)
        {
            return Array.Empty<SanitizationRuleDefinition>();
        }
    }
}
