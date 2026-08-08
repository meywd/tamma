using Microsoft.Extensions.Logging;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Platforms.GitLab;

/// <summary>
/// Story 31-6 §Step 11 — per-tenant factory consumed by 31-2's
/// <c>PlatformResolver</c>. Builds a fully-wired driver bound to a
/// single <see cref="PlatformInstallation"/>.
///
/// <para>The factory uses <see cref="IHttpClientFactory"/> to mint
/// HTTP clients so socket pooling + DNS refresh come from the
/// platform — the underlying handler is shared across tenants but
/// the client + auth are per-driver-instance.</para>
/// </summary>
internal sealed class GitLabPlatformDriverFactory : IGitPlatformDriverFactory
{
    /// <summary>Named HTTP client used by all GitLab driver instances.</summary>
    public const string HttpClientName = "tamma-gitlab";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;

    public GitLabPlatformDriverFactory(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
    }

    public PlatformKind Kind => PlatformKind.GitLab;

    public Task<IGitPlatformDriver> CreateAsync(
        PlatformInstallation installation,
        string credentialPlaintext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialPlaintext);
        if (installation.Kind != PlatformKind.GitLab)
        {
            throw new ArgumentException(
                $"GitLabPlatformDriverFactory cannot build driver for kind={installation.Kind}",
                nameof(installation));
        }

        var auth = GitLabAuth.FromPlaintext(credentialPlaintext);
        var http = _httpClientFactory.CreateClient(HttpClientName);
        // The factory does NOT own the HttpClient (IHttpClientFactory
        // pools it). Pass ownsHttpClient=false.
        var typed = new GitLabHttpClient(http, auth, installation.BaseUrl, ownsHttpClient: false);

        var clientLogger = _loggerFactory.CreateLogger<GitLabPlatformClient>();
        var actionsLogger = _loggerFactory.CreateLogger<GitLabActionsClient>();

        var client = new GitLabPlatformClient(typed, clientLogger);
        var actions = new GitLabActionsClient(typed, actionsLogger);

        // Epic 31 P4 M4 — mount Story 31-8's CI-secrets (variables)
        // provisioner. The authorize delegate applies the driver's credential
        // shape: PAT / project / group tokens ride PRIVATE-TOKEN; OAuth2
        // rides the standard Bearer header.
        var authRef = auth;
        ICiSecretsProvisioner ciSecrets = new GitLabCiSecretsProvisioner(
            http,
            installation.BaseUrl,
            (req, _) =>
            {
                switch (authRef)
                {
                    case GitLabAuth.PersonalAccessToken t:
                        req.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", t.Token);
                        return Task.FromResult(true);
                    case GitLabAuth.ProjectAccessToken t:
                        req.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", t.Token);
                        return Task.FromResult(true);
                    case GitLabAuth.GroupAccessToken t:
                        req.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", t.Token);
                        return Task.FromResult(true);
                    case GitLabAuth.OAuth2 o:
                        req.Headers.Authorization =
                            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", o.AccessToken);
                        return Task.FromResult(true);
                    default:
                        return Task.FromResult(false);
                }
            },
            _loggerFactory.CreateLogger<GitLabCiSecretsProvisioner>());

        IGitPlatformDriver driver = new GitLabPlatformDriver(client, actions, ciSecrets);
        return Task.FromResult(driver);
    }
}
