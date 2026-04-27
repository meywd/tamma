using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Data.Defaults;

/// <summary>
/// Code-resident platform defaults for <see cref="SanitizationRule"/>.
///
/// <para>
/// Story 28-1 PR A (Decision #1, <c>.dev/decisions/story-28-1-design-calls.md</c>):
/// the legacy <c>sanitization_rules.tenant_id IS NULL</c> CP row no longer
/// influences platform defaults. Reads with <c>tenantId == null</c> resolve
/// to the rule list supplied by <see cref="ISanitizationDefaultsProvider"/>
/// (whose canonical implementation wraps
/// <c>Tamma.Api.Services.Sanitization.SystemSanitizationRules.DefaultRules</c>).
/// </para>
///
/// <para>
/// This class is a thin descriptor — the actual default rule list still
/// lives in the Api layer to avoid pulling regex / sanitization heuristics
/// into <c>Tamma.Data</c>. The interface seam is the dependency-inversion
/// glue between the two. See <see cref="ISanitizationDefaultsProvider"/>.
/// </para>
/// </summary>
public static class SanitizationRuleDefaults
{
    /// <summary>
    /// Build a fresh, mutable <see cref="SanitizationRule"/> snapshot whose
    /// JSONB <see cref="SanitizationRule.Rules"/> column carries the
    /// supplied default <paramref name="defaultsJson"/>. Repositories use
    /// this when callers ask for the platform-default raw row directly.
    /// </summary>
    /// <remarks>
    /// A new object is returned on every call so EF / serializers cannot
    /// observe shared mutable state across requests.
    /// </remarks>
    public static SanitizationRule Snapshot(string defaultsJson)
    {
        ArgumentNullException.ThrowIfNull(defaultsJson);
        return new SanitizationRule
        {
            Id = Guid.Empty,
            TenantId = null,
            Rules = defaultsJson,
            CreatedAt = DateTime.MinValue,
            UpdatedAt = DateTime.MinValue,
        };
    }

    /// <summary>
    /// Empty JSON-array marker used when no default-rules JSON has been
    /// materialised yet. Identical to the entity-level <c>"[]"</c>
    /// initializer used by <see cref="SanitizationRule.Rules"/> after a
    /// fresh construct path.
    /// </summary>
    public const string EmptyRulesJson = "[]";
}
