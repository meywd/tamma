using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Api.Services.PromptStore;

namespace Tamma.Api.Extensions;

/// <summary>
/// DI registration for the prompt store services. The parent will call
/// <see cref="AddPromptStoreServices"/> from <c>Program.cs</c> after merging the
/// parallel workstreams.
/// </summary>
public static class PromptStoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PromptStoreService"/> and <see cref="PromptEventsService"/>
    /// as scoped services. Assumes <c>AddTammaData()</c> has already registered
    /// <c>IPromptRepository</c> and <c>IEventRepository</c>.
    /// </summary>
    public static IServiceCollection AddPromptStoreServices(this IServiceCollection services)
    {
        services.AddScoped<PromptStoreService>();
        services.AddScoped<PromptEventsService>();
        // Story 27-2 — process-wide operating mode. Used by handlers that
        // need to pick between the user-scoped and tenant-scoped resolver
        // surfaces on PromptStoreService. Singleton because the value is
        // resolved from configuration once at startup and never changes
        // (CLAUDE.md "Operating Modes / Mode detection").
        services.TryAddSingleton<ITammaModeProvider, TammaModeProvider>();
        return services;
    }
}
