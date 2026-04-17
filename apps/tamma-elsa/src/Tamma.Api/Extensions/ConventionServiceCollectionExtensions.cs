using Microsoft.Extensions.DependencyInjection;
using Tamma.Api.Services.Conventions;

namespace Tamma.Api.Extensions;

/// <summary>
/// DI registration helpers for the convention template service layer.
/// </summary>
public static class ConventionServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IConventionTemplateService"/> as a singleton backed
    /// by the shipped static template data. Safe to call multiple times.
    /// </summary>
    public static IServiceCollection AddConventionServices(this IServiceCollection services)
    {
        services.AddSingleton<IConventionTemplateService, ConventionTemplateService>();
        return services;
    }
}
