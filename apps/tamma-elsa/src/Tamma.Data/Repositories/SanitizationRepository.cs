using Tamma.Data.Abstractions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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
/// <para>Epic 28: rows with <c>TenantId = &lt;guid&gt;</c> live on tenant DBs
/// via <see cref="ITenantDbContextFactory"/>; the platform-default row
/// (<c>TenantId IS NULL</c>) lives on <see cref="ControlPlaneDbContext"/>.
/// The repo routes each method to the right plane based on the
/// <paramref name="tenantId"/> argument.</para>
/// </summary>
public class SanitizationRepository : ISanitizationRepository
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly ITenantDbContextFactory _factory;
    private readonly ControlPlaneDbContext _cp;
    private readonly ISanitizationDefaultsProvider _defaults;

    public SanitizationRepository(
        ITenantDbContextFactory factory,
        ControlPlaneDbContext cp,
        IEnumerable<ISanitizationDefaultsProvider> defaults)
    {
        _factory = factory;
        _cp = cp;
        _defaults = defaults?.FirstOrDefault() ?? EmptyDefaultsProvider.Instance;
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

        var cpRow = await LoadOrCreateRowAsync(_cp.SanitizationRules, _cp, tenantId);
        var cpCurrent = DeserializeRules(cpRow.Rules).ToList();
        var cpIdx = cpCurrent.FindIndex(r => string.Equals(r.Name, rule.Name, StringComparison.Ordinal));
        if (cpIdx >= 0) cpCurrent[cpIdx] = rule;
        else cpCurrent.Add(rule);
        cpRow.Rules = JsonSerializer.Serialize(cpCurrent, JsonOpts);
        cpRow.UpdatedAt = DateTime.UtcNow;
        await _cp.SaveChangesAsync();
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

        var cpRow = await _cp.SanitizationRules.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId);
        if (cpRow is null) return;
        var cpCurrent = DeserializeRules(cpRow.Rules).ToList();
        var cpRemoved = cpCurrent.RemoveAll(r => string.Equals(r.Name, ruleName, StringComparison.Ordinal));
        if (cpRemoved == 0) return;
        cpRow.Rules = JsonSerializer.Serialize(cpCurrent, JsonOpts);
        cpRow.UpdatedAt = DateTime.UtcNow;
        await _cp.SaveChangesAsync();
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

        var cpRow = await LoadOrCreateRowAsync(_cp.SanitizationRules, _cp, tenantId);
        cpRow.Rules = JsonSerializer.Serialize(list, JsonOpts);
        cpRow.UpdatedAt = DateTime.UtcNow;
        await _cp.SaveChangesAsync();
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
        return await _cp.SanitizationRules.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId);
    }

    // ─── helpers ────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<SanitizationRuleDefinition>> LoadOverridesAsync(Guid? tenantId)
    {
        SanitizationRule? row;
        if (tenantId is Guid tid)
        {
            await using var db = await _factory.CreateAsync(tid);
            row = await db.SanitizationRules.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(r => r.TenantId == tenantId);
        }
        else
        {
            row = await _cp.SanitizationRules.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(r => r.TenantId == tenantId);
        }
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
            Rules = "[]",
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
