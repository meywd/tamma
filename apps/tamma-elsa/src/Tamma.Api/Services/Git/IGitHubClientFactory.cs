using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Core.Interfaces;

namespace Tamma.Api.Services.Git;

/// <summary>
/// Story 38-1 (AC3/AC4) — mints an <see cref="IGitHubIntegrationService"/> bound
/// to a SPECIFIC resolved token so the platform call uses the token that was
/// resolved (the invariant: "the token used == the token resolved"). The bound
/// service overrides the named "github" client's static <c>GitHub:Token</c>
/// bearer with the request-scoped token for that one call and drops it after.
/// The token NEVER leaves the service instance — no logging, no return, no event.
/// </summary>
public interface IGitHubClientFactory
{
    IGitHubIntegrationService Create(string token);
}

/// <inheritdoc />
public sealed class GitHubClientFactory : IGitHubClientFactory
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GitHubIntegrationService> _logger;

    public GitHubClientFactory(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<GitHubIntegrationService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IGitHubIntegrationService Create(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("A non-empty git token is required.", nameof(token));
        }

        return new Tamma.Api.Services.GitHubIntegrationService(_httpClientFactory, _configuration, _logger, token);
    }
}
