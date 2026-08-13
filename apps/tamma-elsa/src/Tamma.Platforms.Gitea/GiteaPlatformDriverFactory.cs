using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;
using Tamma.Platforms.Gitea.Dtos;

namespace Tamma.Platforms.Gitea;

/// <summary>
/// Story 31-4 / 31-2 — factory the platform resolver picks up via
/// keyed-DI for <see cref="PlatformKind.Gitea"/>. Builds a fully-wired
/// <see cref="GiteaPlatformDriver"/> bound to a single
/// <see cref="PlatformInstallation"/> + credential plaintext.
///
/// <para>Plan §8 sequence:</para>
/// <list type="number">
///   <item>Parse <paramref name="credentialPlaintext"/> into
///         <see cref="GiteaAuth"/>.</item>
///   <item>Construct <see cref="GiteaHttpClient"/> against
///         <see cref="PlatformInstallation.BaseUrl"/>.</item>
///   <item>Probe <c>/api/v1/version</c> to detect the Gitea version
///         (best-effort — falls back to read-only capabilities if the
///         probe fails).</item>
///   <item>Instantiate <see cref="GiteaPlatformClient"/> +
///         <see cref="GiteaActionsPlatformClient"/> as appropriate and
///         wrap them in <see cref="GiteaPlatformDriver"/>.</item>
/// </list>
/// </summary>
public sealed class GiteaPlatformDriverFactory : IGitPlatformDriverFactory
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GiteaOAuth2TokenCache _tokenCache;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IConfiguration? _configuration;

    public GiteaPlatformDriverFactory(
        IHttpClientFactory httpClientFactory,
        GiteaOAuth2TokenCache tokenCache,
        ILoggerFactory? loggerFactory = null,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(tokenCache);
        _httpClientFactory = httpClientFactory;
        _tokenCache = tokenCache;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _configuration = configuration;
    }

    /// <inheritdoc />
    public PlatformKind Kind => PlatformKind.Gitea;

    /// <inheritdoc />
    public async Task<IGitPlatformDriver> CreateAsync(
        PlatformInstallation installation,
        string credentialPlaintext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Kind != PlatformKind.Gitea)
        {
            throw new ArgumentException(
                $"GiteaPlatformDriverFactory cannot create a driver for kind={installation.Kind}",
                nameof(installation));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialPlaintext);

        var auth = GiteaAuth.Parse(credentialPlaintext);
        var http = _httpClientFactory.CreateClient(GiteaHttpClientName);
        var giteaHttp = new GiteaHttpClient(
            http,
            installation.Id,
            installation.BaseUrl,
            auth,
            _tokenCache,
            _loggerFactory.CreateLogger<GiteaHttpClient>());

        var detected = await DetectVersionAsync(giteaHttp, ct).ConfigureAwait(false);
        var capabilities = GiteaPlatformDriver.ComputeCapabilities(detected);
        var host = ExtractHost(installation.BaseUrl);

        var client = new GiteaPlatformClient(
            giteaHttp,
            host,
            _loggerFactory.CreateLogger<GiteaPlatformClient>(),
            detected);

        IGitPlatformActionsClient? actions = null;
        if (capabilities.Contains(PlatformCapability.Actions))
        {
            actions = new GiteaActionsPlatformClient(
                giteaHttp,
                _loggerFactory.CreateLogger<GiteaActionsPlatformClient>(),
                _configuration);
        }

        // Epic 31 P4 M4 — mount Story 31-8's CI-secrets provisioner when the
        // detected version advertises the secrets API (1.21+). The authorize
        // delegate applies the bot token directly; OAuth2 mode reads the
        // cached access token (minted by the driver's own request path) and
        // degrades to a typed auth_unavailable per-target failure when no
        // token is cached yet.
        ICiSecretsProvisioner? ciSecrets = null;
        if (capabilities.Contains(PlatformCapability.Secrets))
        {
            var installationId = installation.Id;
            var authRef = auth;
            var cacheRef = _tokenCache;
            var secretsBaseUrl = installation.BaseUrl;
            ciSecrets = new GiteaCiSecretsProvisioner(
                http,
                secretsBaseUrl,
                (req, _) =>
                {
                    switch (authRef)
                    {
                        case GiteaAuth.BotToken bot:
                            req.Headers.Authorization =
                                new System.Net.Http.Headers.AuthenticationHeaderValue("token", bot.Token);
                            return Task.FromResult(true);
                        case GiteaAuth.OAuth2:
                            var cached = cacheRef.TryGet(installationId);
                            if (string.IsNullOrEmpty(cached)) return Task.FromResult(false);
                            req.Headers.Authorization =
                                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cached);
                            return Task.FromResult(true);
                        default:
                            return Task.FromResult(false);
                    }
                },
                _loggerFactory.CreateLogger<GiteaCiSecretsProvisioner>());
        }

        return new GiteaPlatformDriver(client, actions, capabilities, detected, ciSecrets);
    }

    /// <summary>
    /// Named-client identifier used by 31-4 — tests inject a custom
    /// <c>HttpMessageHandler</c> by registering this name.
    /// </summary>
    public const string GiteaHttpClientName = "tamma-gitea";

    /// <summary>
    /// Probe <c>/api/v1/version</c>. Returns null on any failure —
    /// missing version means we conservatively assume pre-Actions
    /// (1.20-style) and ship the read-only capability set.
    /// </summary>
    internal static async Task<Version?> DetectVersionAsync(
        GiteaHttpClient http, CancellationToken ct)
    {
        var result = await http.GetJsonAsync<GiteaVersionDto>("/api/v1/version", ct)
            .ConfigureAwait(false);
        if (result is not PlatformResult<GiteaVersionDto>.Ok ok) return null;
        var raw = ok.Value.Version;
        if (string.IsNullOrEmpty(raw)) return null;
        // Gitea returns shapes like "1.21.0+gitea-1.21.0" or "1.21.4".
        // Trim any "+suffix" or "-suffix" piece before Version.Parse.
        var canonical = raw;
        var plus = canonical.IndexOf('+');
        if (plus >= 0) canonical = canonical[..plus];
        var dash = canonical.IndexOf('-');
        if (dash >= 0) canonical = canonical[..dash];
        return Version.TryParse(canonical, out var parsed) ? parsed : null;
    }

    private static string ExtractHost(string baseUrl)
    {
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }
        return baseUrl;
    }
}
