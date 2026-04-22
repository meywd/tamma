using Microsoft.Extensions.DependencyInjection;
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
        return services;
    }
}
