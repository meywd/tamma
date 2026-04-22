using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Persistence layer for per-tenant sanitization rule overrides.
///
/// <para>
/// The repository stores rules as a single JSONB blob per tenant (one row per
/// <see cref="SanitizationRule.TenantId"/>) but exposes structured CRUD on
/// <see cref="SanitizationRuleDefinition"/> for callers. Rules returned by
/// <see cref="GetRulesAsync"/> are always the merged result — system defaults
/// from <c>SystemSanitizationRules.DefaultRules</c> plus any tenant overrides,
/// keyed by <see cref="SanitizationRuleDefinition.Name"/>.
/// </para>
/// </summary>
public interface ISanitizationRepository
{
    /// <summary>
    /// Return the effective rule set for a tenant: system defaults, with any
    /// same-name tenant override replacing the default entirely.
    /// </summary>
    Task<IReadOnlyList<SanitizationRuleDefinition>> GetRulesAsync(Guid? tenantId);

    /// <summary>
    /// Create or replace a single rule on the tenant's override blob. Other
    /// rules on the same tenant remain untouched.
    /// </summary>
    Task UpsertRuleAsync(Guid? tenantId, SanitizationRuleDefinition rule);

    /// <summary>
    /// Delete a tenant-specific override by name. If the name also exists in
    /// the system-default rule set, the system default takes over on the next
    /// <see cref="GetRulesAsync"/> call.
    /// </summary>
    Task DeleteRuleAsync(Guid? tenantId, string ruleName);

    /// <summary>
    /// Replace the tenant's entire override set with <paramref name="rules"/>.
    /// Useful for the settings endpoint's full-set PUT.
    /// </summary>
    Task ReplaceRulesAsync(Guid? tenantId, IEnumerable<SanitizationRuleDefinition> rules);

    /// <summary>
    /// Low-level access to the raw entity row. Prefer the structured methods
    /// above for new code.
    /// </summary>
    Task<SanitizationRule?> GetRawAsync(Guid? tenantId);
}
