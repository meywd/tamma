using Microsoft.Extensions.Logging.Abstractions;
using Tamma.Platforms.Gitea;

namespace Tamma.Platforms.Gitea.Tests;

/// <summary>
/// Shared test factory: builds a <see cref="GiteaPlatformClient"/> +
/// <see cref="GiteaActionsPlatformClient"/> wired against a
/// <see cref="FakeHttpMessageHandler"/> at the canonical
/// <c>https://gitea.example.com</c> base URL.
/// </summary>
internal static class GiteaTestFixtures
{
    public const string BaseUrl = "https://gitea.example.com";
    public const string BotToken = "ghs_test_token_12345";

    public static (
        GiteaPlatformClient Client,
        GiteaActionsPlatformClient Actions,
        FakeHttpMessageHandler Handler,
        GiteaHttpClient Http)
        Build(GiteaAuth? auth = null, GiteaOAuth2TokenCache? cache = null,
            Microsoft.Extensions.Configuration.IConfiguration? configuration = null,
            string baseUrl = BaseUrl)
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler);
        var tokenCache = cache ?? new GiteaOAuth2TokenCache();
        var giteaHttp = new GiteaHttpClient(
            http,
            installationId: Guid.NewGuid(),
            baseUrl: baseUrl,
            auth: auth ?? new GiteaAuth.BotToken(BotToken),
            tokenCache: tokenCache,
            logger: NullLogger.Instance);
        var host = new Uri(baseUrl).Host;
        var client = new GiteaPlatformClient(giteaHttp, host, NullLogger.Instance);
        var actions = new GiteaActionsPlatformClient(giteaHttp, NullLogger.Instance, configuration);
        return (client, actions, handler, giteaHttp);
    }
}
