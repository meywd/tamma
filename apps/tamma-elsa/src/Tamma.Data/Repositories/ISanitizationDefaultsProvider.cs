using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Supplies the system-default sanitization rule set to
/// <see cref="ISanitizationRepository"/> so the repository can return merged
/// (defaults + tenant overrides) results without depending on the Api layer.
///
/// <para>
/// The canonical default rules live in
/// <c>Tamma.Api.Services.Sanitization.SystemSanitizationRules</c>. The
/// concrete implementation of this interface (registered in
/// <c>SanitizationServiceCollectionExtensions</c>) simply returns that list,
/// inverting the dependency so that Tamma.Data never references Tamma.Api.
/// </para>
/// </summary>
public interface ISanitizationDefaultsProvider
{
    /// <summary>The immutable default rule set.</summary>
    IReadOnlyList<SanitizationRuleDefinition> DefaultRules { get; }
}
