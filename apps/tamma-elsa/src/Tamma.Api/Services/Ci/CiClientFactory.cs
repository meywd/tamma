using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Core.Interfaces;

namespace Tamma.Api.Services.Ci;

/// <summary>
/// Story 38 (Phase 1) — mints an <see cref="ICIIntegrationService"/> bound to a
/// SPECIFIC resolved token so the CI (GitHub Actions) call uses the token that was
/// resolved (the "token used == token resolved" invariant). Mirrors
/// <see cref="Tamma.Api.Services.Git.GitHubClientFactory"/>: the bound service
/// overrides the named "github" client's static <c>GitHub:Token</c> bearer with the
/// request-scoped token for that one call. The token NEVER leaves the service
/// instance — no logging, no return, no event.
/// </summary>
public interface ICiClientFactory
{
    ICIIntegrationService Create(string token);
}

/// <inheritdoc />
public sealed class CiClientFactory : ICiClientFactory
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CIIntegrationService> _logger;

    public CiClientFactory(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<CIIntegrationService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ICIIntegrationService Create(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("A non-empty CI (git) token is required.", nameof(token));
        }

        return new CIIntegrationService(_httpClientFactory, _configuration, _logger, token);
    }
}
