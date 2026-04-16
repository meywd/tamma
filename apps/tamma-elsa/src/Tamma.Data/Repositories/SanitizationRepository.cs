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
/// under the <see cref="SanitizationRule.Rules"/> column. This lets us evolve
/// the per-rule shape without migrations while still giving the Api and
/// service layers a structured CRUD surface.
/// </para>
///
/// <para>
/// <see cref="GetRulesAsync"/> applies the merge policy: every system default
/// from <see cref="ISanitizationDefaultsProvider.DefaultRules"/> is included,
/// and any tenant-stored rule with the same <see cref="SanitizationRuleDefinition.Name"/>
/// replaces the default in-place.
/// </para>
/// </summary>
public class SanitizationRepository : ISanitizationRepository
{
    /// <summary>
    /// JSON options applied to the rules blob. camelCase matches the
    /// <c>[JsonPropertyName]</c> attributes on <see cref="SanitizationRuleDefinition"/>
    /// and produces stable on-disk form regardless of caller-supplied options.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly TammaDbContext _db;
    private readonly ISanitizationDefaultsProvider _defaults;

    /// <summary>
    /// DI-friendly constructor. <paramref name="defaults"/> is injected as an
    /// <see cref="IEnumerable{T}"/> so the container does not fail activation
    /// when the downstream <c>AddSanitizationServices</c> has not yet been
    /// called. If nothing is registered, we fall back to
    /// <see cref="EmptyDefaultsProvider"/>.
    /// </summary>
    public SanitizationRepository(TammaDbContext db, IEnumerable<ISanitizationDefaultsProvider> defaults)
    {
        _db = db;
        _defaults = defaults?.FirstOrDefault() ?? EmptyDefaultsProvider.Instance;
    }

    /// <summary>
    /// Fallback for when no <see cref="ISanitizationDefaultsProvider"/> is
    /// registered. Returning an empty default list means the tenant sees only
    /// their own overrides — safe and predictable rather than throwing at
    /// service-provider validation time.
    /// </summary>
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

        // Merge: start from defaults, replace any same-named override.
        // Use ordinal comparison — rule names are machine identifiers, not locale text.
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

        var existing = await LoadOrCreateRowAsync(tenantId).ConfigureAwait(false);
        var current = DeserializeRules(existing.Rules).ToList();

        var idx = current.FindIndex(r => string.Equals(r.Name, rule.Name, StringComparison.Ordinal));
        if (idx >= 0) current[idx] = rule;
        else current.Add(rule);

        existing.Rules = JsonSerializer.Serialize(current, JsonOpts);
        existing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteRuleAsync(Guid? tenantId, string ruleName)
    {
        if (string.IsNullOrWhiteSpace(ruleName)) return;

        var existing = await _db.SanitizationRules
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId)
            .ConfigureAwait(false);
        if (existing is null) return;

        var current = DeserializeRules(existing.Rules).ToList();
        var removed = current.RemoveAll(r =>
            string.Equals(r.Name, ruleName, StringComparison.Ordinal));
        if (removed == 0) return;

        existing.Rules = JsonSerializer.Serialize(current, JsonOpts);
        existing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ReplaceRulesAsync(Guid? tenantId, IEnumerable<SanitizationRuleDefinition> rules)
    {
        var list = rules?.ToList() ?? new List<SanitizationRuleDefinition>();
        var existing = await LoadOrCreateRowAsync(tenantId).ConfigureAwait(false);
        existing.Rules = JsonSerializer.Serialize(list, JsonOpts);
        existing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SanitizationRule?> GetRawAsync(Guid? tenantId)
        => await _db.SanitizationRules
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId)
            .ConfigureAwait(false);

    // ─── helpers ────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<SanitizationRuleDefinition>> LoadOverridesAsync(Guid? tenantId)
    {
        var row = await _db.SanitizationRules
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId)
            .ConfigureAwait(false);
        return row is null
            ? Array.Empty<SanitizationRuleDefinition>()
            : DeserializeRules(row.Rules);
    }

    private async Task<SanitizationRule> LoadOrCreateRowAsync(Guid? tenantId)
    {
        var row = await _db.SanitizationRules
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId)
            .ConfigureAwait(false);
        if (row is not null) return row;

        row = new SanitizationRule
        {
            TenantId = tenantId,
            Rules = "[]",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.SanitizationRules.Add(row);
        return row;
    }

    /// <summary>
    /// Parse the <see cref="SanitizationRule.Rules"/> JSONB. The column was
    /// previously used for a free-form object, so we tolerate non-array JSON
    /// by returning an empty list instead of throwing.
    /// </summary>
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
            // Corrupt blob — treat as empty so the tenant falls back to system defaults.
            return Array.Empty<SanitizationRuleDefinition>();
        }
    }
}
