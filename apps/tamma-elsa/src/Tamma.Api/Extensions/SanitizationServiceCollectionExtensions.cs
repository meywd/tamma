using Microsoft.Extensions.DependencyInjection;
using Tamma.Api.Services.Sanitization;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Extensions;

/// <summary>
/// Registers all services required by the sanitization rule engine.
///
/// <para>
/// The parent <c>Program.cs</c> is owned by the auth foundation stream and
/// must not be edited from here — the contract between streams is that
/// sanitization wiring is exposed via this single extension method, which
/// the parent calls once from its composition root.
/// </para>
/// </summary>
public static class SanitizationServiceCollectionExtensions
{
    /// <summary>
    /// Register:
    /// <list type="bullet">
    ///   <item><description><see cref="ISanitizationService"/> as a scoped
    ///     dependency (it depends on the scoped <see cref="ISanitizationRepository"/>).</description></item>
    ///   <item><description><see cref="ISanitizationDefaultsProvider"/> as a
    ///     singleton — the default rule list is immutable and shared across
    ///     every tenant.</description></item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddSanitizationServices(this IServiceCollection services)
    {
        services.AddSingleton<ISanitizationDefaultsProvider, SystemSanitizationDefaultsProvider>();
        services.AddScoped<ISanitizationService, SanitizationService>();
        return services;
    }

    /// <summary>
    /// Bridge between the persistence layer and the canonical API-layer
    /// default rule list. Keeps <c>Tamma.Data</c> independent of
    /// <c>Tamma.Api</c> (the project reference goes one way only).
    /// </summary>
    private sealed class SystemSanitizationDefaultsProvider : ISanitizationDefaultsProvider
    {
        public IReadOnlyList<SanitizationRuleDefinition> DefaultRules
            => SystemSanitizationRules.DefaultRules;
    }
}
