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

    public async Task<IGitPlatformDriver> CreateAsync(
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

        // Epic 31 P6 M1 — probe GET /version (authenticated; present since
        // GitLab 8.13) to feature-detect the PR-lifecycle floor. Best-effort:
        // a failed probe yields null and ComputeCapabilities conservatively
        // drops PrLifecycle (mirrors the Gitea factory's posture).
        var detected = await DetectVersionAsync(typed, ct).ConfigureAwait(false);

        var clientLogger = _loggerFactory.CreateLogger<GitLabPlatformClient>();
        var actionsLogger = _loggerFactory.CreateLogger<GitLabActionsClient>();

        var client = new GitLabPlatformClient(typed, clientLogger, detected);
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

        return new GitLabPlatformDriver(client, actions, ciSecrets, detected);
    }

    /// <summary>
    /// Probe <c>GET /version</c> (API v4). Returns null on any failure —
    /// missing version conservatively drops the PR-lifecycle capability.
    /// GitLab answers shapes like <c>{"version":"16.11.1-ee","revision":"..."}</c>;
    /// any <c>-suffix</c> (edition) is trimmed before parsing.
    /// </summary>
    internal static async Task<Version?> DetectVersionAsync(
        GitLabHttpClient http, CancellationToken ct)
    {
        try
        {
            var (resp, dto) = await http
                .GetJsonAsync<Dtos.GitLabVersionResponse>("version", ct)
                .ConfigureAwait(false);
            using (resp)
            {
                if (!resp.Response.IsSuccessStatusCode) return null;
                return ParseVersion(dto?.Version);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // caller-requested cancellation propagates
        }
        catch (Exception ex) when (ex is HttpRequestException
            or OperationCanceledException // HttpClient timeout
            or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Trim <c>-ee</c>/<c>-ce</c>/pre-release suffixes and parse.</summary>
    internal static Version? ParseVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var canonical = raw;
        var dash = canonical.IndexOf('-');
        if (dash >= 0) canonical = canonical[..dash];
        var plus = canonical.IndexOf('+');
        if (plus >= 0) canonical = canonical[..plus];
        return Version.TryParse(canonical, out var parsed) ? parsed : null;
    }
}
