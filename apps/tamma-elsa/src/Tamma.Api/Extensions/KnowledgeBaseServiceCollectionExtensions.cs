using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tamma.Api.Services.KnowledgeBase;

namespace Tamma.Api.Extensions;

/// <summary>
/// Registers the HTTP client bridge to the TypeScript
/// <c>@tamma/intelligence-server</c> sidecar.
///
/// <para>
/// Contract with the parent composition root: the only edit the auth-foundation
/// stream needs to make to Program.cs is calling
/// <see cref="AddKnowledgeBaseServices"/> once during service registration.
/// This extension reads <c>IntelligenceServer:Url</c> from configuration
/// (defaulting to the docker-compose service hostname) and wires up a typed
/// <see cref="IIntelligenceHttpClient"/> via <c>IHttpClientFactory</c>.
/// </para>
///
/// <para>
/// The default 10-second timeout balances dashboard UX (never hang the UI)
/// with allowing larger RAG queries + vector upserts to complete. On
/// timeout or 5xx, <see cref="IntelligenceHttpClient"/> returns a degraded
/// payload — see that class for details.
/// </para>
/// </summary>
public static class KnowledgeBaseServiceCollectionExtensions
{
    public const string HttpClientName = "intelligence-server";

    private const string DefaultUrl = "http://intelligence-server:4100";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    public static IServiceCollection AddKnowledgeBaseServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var baseUrl = configuration["IntelligenceServer:Url"] ?? DefaultUrl;
        var timeoutSeconds = configuration.GetValue<int?>("IntelligenceServer:TimeoutSeconds");
        var timeout = timeoutSeconds is > 0
            ? TimeSpan.FromSeconds(timeoutSeconds.Value)
            : DefaultTimeout;

        services.AddHttpClient<IIntelligenceHttpClient, IntelligenceHttpClient>(HttpClientName, client =>
        {
            client.BaseAddress = new Uri(baseUrl);
            client.Timeout = timeout;
            client.DefaultRequestHeaders.Add("User-Agent", "Tamma-Api/1.0 (intelligence-bridge)");
        });

        return services;
    }
}
