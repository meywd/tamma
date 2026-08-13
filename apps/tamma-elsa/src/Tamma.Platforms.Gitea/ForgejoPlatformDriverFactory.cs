using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Platforms.Gitea;

/// <summary>
/// Story 31-5 — factory the platform resolver picks up via keyed-DI
/// for <see cref="PlatformKind.Forgejo"/>. Builds the same
/// <see cref="GiteaHttpClient"/> + <see cref="GiteaPlatformClient"/> +
/// <see cref="GiteaActionsPlatformClient"/> stack as the Gitea factory
/// (Forgejo retains REST + DB compat with its Gitea fork-base) and
/// wraps the result in a <see cref="ForgejoPlatformDriver"/> so
/// <see cref="IGitPlatformDriver.Kind"/> reports
/// <see cref="PlatformKind.Forgejo"/>.
///
/// <para>Sequence:</para>
/// <list type="number">
///   <item>Parse <paramref name="credentialPlaintext"/> via the same
///         <see cref="GiteaAuth.Parse"/> seam (token / OAuth2 shapes
///         match Forgejo's).</item>
///   <item>Construct <see cref="GiteaHttpClient"/> against the
///         Forgejo <see cref="PlatformInstallation.BaseUrl"/>.</item>
///   <item>Probe <c>/api/v1/version</c> — Forgejo returns shapes like
///         <c>1.21.5+forgejo-3</c>; the Gitea factory's
///         '+'/'-' suffix-strip handles this unchanged.</item>
///   <item>Build the inner <see cref="GiteaPlatformDriver"/> +
///         wrap in <see cref="ForgejoPlatformDriver"/>.</item>
/// </list>
///
/// <para>The factory itself rejects installations whose
/// <see cref="PlatformInstallation.Kind"/> is not
/// <see cref="PlatformKind.Forgejo"/> — defensive symmetric check
/// matching <see cref="GiteaPlatformDriverFactory"/>.</para>
/// </summary>
public sealed class ForgejoPlatformDriverFactory : IGitPlatformDriverFactory
{
    /// <summary>
    /// Named-client identifier — distinct from Gitea's so tests +
    /// hosts can swap a custom <c>HttpMessageHandler</c> on Forgejo
    /// without disturbing Gitea wiring.
    /// </summary>
    public const string ForgejoHttpClientName = "tamma-forgejo";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GiteaOAuth2TokenCache _tokenCache;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IConfiguration? _configuration;

    public ForgejoPlatformDriverFactory(
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
    public PlatformKind Kind => PlatformKind.Forgejo;

    /// <inheritdoc />
    public async Task<IGitPlatformDriver> CreateAsync(
        PlatformInstallation installation,
        string credentialPlaintext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Kind != PlatformKind.Forgejo)
        {
            throw new ArgumentException(
                $"ForgejoPlatformDriverFactory cannot create a driver for kind={installation.Kind}",
                nameof(installation));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialPlaintext);

        var auth = GiteaAuth.Parse(credentialPlaintext);
        var http = _httpClientFactory.CreateClient(ForgejoHttpClientName);
        var giteaHttp = new GiteaHttpClient(
            http,
            installation.Id,
            installation.BaseUrl,
            auth,
            _tokenCache,
            _loggerFactory.CreateLogger<GiteaHttpClient>());

        // Forgejo's /api/v1/version returns `1.21.5+forgejo-N` — the
        // existing strip-after-'+' logic in DetectVersionAsync handles
        // it identically to Gitea's `1.21.0+gitea-1.21.0`.
        var detected = await GiteaPlatformDriverFactory
            .DetectVersionAsync(giteaHttp, ct)
            .ConfigureAwait(false);

        // Capability set is computed against the Forgejo matrix row;
        // narrowing logic mirrors the Gitea driver (no Actions before
        // 1.21). ForgejoPlatformDriver.Capabilities re-runs this
        // computation against the inner driver's DetectedVersion so
        // the wrapper stays self-consistent.
        var capabilities = ForgejoPlatformDriver.ComputeCapabilities(detected);
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

        // Epic 31 P4 M4 — mount the CI-secrets provisioner (Forgejo keeps the
        // Gitea secrets API; results stamp the Forgejo kind via the wrapper).
        ICiSecretsProvisioner? ciSecrets = null;
        if (capabilities.Contains(PlatformCapability.Secrets))
        {
            var installationId = installation.Id;
            var authRef = auth;
            var cacheRef = _tokenCache;
            ciSecrets = new ForgejoCiSecretsProvisioner(
                http,
                installation.BaseUrl,
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

        // Compose: build the inner Gitea driver (its Kind reports
        // Gitea, intentionally — only the wrapper exposes
        // PlatformKind.Forgejo to the resolver), then wrap.
        var inner = new GiteaPlatformDriver(client, actions, capabilities, detected, ciSecrets);
        return new ForgejoPlatformDriver(inner);
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
