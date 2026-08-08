using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Platforms.GitHub;

/// <summary>
/// Epic 31 P1 stage 2 — keyed-DI factory consumed by 31-2's
/// <c>PlatformResolver</c> when an installation row's
/// <c>platform_kind = "github"</c>. Builds a fully-wired
/// <see cref="GitHubPlatformDriver"/> bound to a single
/// <see cref="PlatformInstallation"/> + credential plaintext — the
/// factory now HONORS both of its arguments (the old
/// <c>_ = credentialPlaintext;</c> discard is gone):
///
/// <list type="number">
///   <item><paramref name="credentialPlaintext"/> parses into
///         <see cref="GitHubAuth"/> — PAT/BYOK plaintext token, or the
///         JSON App-installation shape (<c>kind:"app"</c> with
///         <c>appId</c> + <c>privateKeyPem</c> and an optional
///         <c>installationId</c> that falls back to the row's
///         <see cref="PlatformInstallation.InstallationExternalId"/>).
///         See <see cref="GitHubAuth.Parse"/> for the wire format.</item>
///   <item><see cref="PlatformInstallation.BaseUrl"/> is the API root
///         (default <see cref="DefaultBaseUrl"/> when blank) — GHES
///         installs pass their <c>/api/v3</c> root and the driver
///         derives the matching <c>/api/graphql</c> endpoint.</item>
/// </list>
///
/// <para>Both credential modes build the SAME driver classes — App
/// token minting (absorbed from <c>OctokitGitHubAppClient</c>) is a
/// driver-internal concern, so the old "real Actions only when the
/// process-level GitHub App is configured" conditional cannot recur.</para>
/// </summary>
public sealed class GitHubPlatformDriverFactory : IGitPlatformDriverFactory
{
    /// <summary>Default GitHub Cloud API root used when an
    /// installation row carries no base URL.</summary>
    public const string DefaultBaseUrl = "https://api.github.com";

    /// <summary>Default GitHub Cloud host (kept for callers/tests that
    /// key off the host name).</summary>
    public const string DefaultHost = "github.com";

    /// <summary>
    /// Named-client identifier — tests inject a custom
    /// <see cref="HttpMessageHandler"/> by registering this name
    /// (the Gitea driver's <c>tamma-gitea</c> pattern).
    /// </summary>
    public const string GitHubHttpClientName = "tamma-github";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TimeProvider _time;

    public GitHubPlatformDriverFactory(
        IHttpClientFactory httpClientFactory,
        ILoggerFactory? loggerFactory = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public PlatformKind Kind => PlatformKind.GitHub;

    /// <inheritdoc />
    public Task<IGitPlatformDriver> CreateAsync(
        PlatformInstallation installation,
        string credentialPlaintext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Kind != PlatformKind.GitHub)
        {
            throw new ArgumentException(
                $"GitHubPlatformDriverFactory cannot build driver for kind={installation.Kind}",
                nameof(installation));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialPlaintext);

        var auth = GitHubAuth.Parse(credentialPlaintext);
        var baseUrl = string.IsNullOrWhiteSpace(installation.BaseUrl)
            ? DefaultBaseUrl
            : installation.BaseUrl.TrimEnd('/');
        var host = ExtractHost(installation.BaseUrl);

        var http = _httpClientFactory.CreateClient(GitHubHttpClientName);

        GitHubAppTokenMinter? minter = null;
        if (auth is GitHubAuth.App app)
        {
            var installationId = ResolveInstallationId(app, installation);
            minter = new GitHubAppTokenMinter(
                http,
                baseUrl,
                app.AppId,
                app.PrivateKeyPem,
                installationId,
                _time,
                _loggerFactory.CreateLogger<GitHubAppTokenMinter>());
        }

        var githubHttp = new GitHubHttpClient(
            http,
            baseUrl,
            auth,
            minter,
            _loggerFactory.CreateLogger<GitHubHttpClient>());

        var client = new GitHubPlatformClient(
            githubHttp,
            host,
            appMode: auth is GitHubAuth.App,
            _loggerFactory.CreateLogger<GitHubPlatformClient>());
        IGitPlatformActionsClient actions = new GitHubActionsPlatformClient(
            githubHttp,
            _loggerFactory.CreateLogger<GitHubActionsPlatformClient>());

        var capabilities = GitHubPlatformDriver.ComputeCapabilities(auth);

        // Epic 31 P4 M4 — mount Story 31-8's CI-secrets provisioner (seam 11's
        // first severed point: driver.CiSecrets was null on every driver). The
        // authorize delegate applies the SAME credential mode as the rest of
        // the driver — PAT bearer, or an App installation token minted (and
        // cached) by the minter — so per-tenant BYOK and GHES both work.
        ICiSecretsProvisioner? ciSecrets = null;
        if (capabilities.Contains(PlatformCapability.Secrets))
        {
            var patToken = auth is GitHubAuth.Pat pat ? pat.Token : null;
            var minterRef = minter;
            ciSecrets = new GitHubCiSecretsProvisioner(
                http,
                baseUrl,
                async (req, innerCt) =>
                {
                    var token = patToken ?? (minterRef is null
                        ? null
                        : await minterRef.GetInstallationTokenAsync(false, innerCt).ConfigureAwait(false));
                    if (string.IsNullOrEmpty(token)) return false;
                    req.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                    req.Headers.Accept.ParseAdd("application/vnd.github+json");
                    return true;
                },
                _loggerFactory.CreateLogger<GitHubCiSecretsProvisioner>());
        }

        IGitPlatformDriver driver = new GitHubPlatformDriver(
            client, actions, capabilities, ciSecrets);
        return Task.FromResult(driver);
    }

    /// <summary>
    /// App mode needs an installation id to mint tokens for. The
    /// credential's own <c>installationId</c> wins; else the row's
    /// <see cref="PlatformInstallation.InstallationExternalId"/> (where
    /// the registry bridge records the GitHub installation id). Neither
    /// present → fail loud at the factory, not at first API use.
    /// </summary>
    internal static long ResolveInstallationId(
        GitHubAuth.App app, PlatformInstallation installation)
    {
        if (app.InstallationId is { } fromCredential && fromCredential > 0)
        {
            return fromCredential;
        }
        if (long.TryParse(installation.InstallationExternalId, out var fromRow) && fromRow > 0)
        {
            return fromRow;
        }
        throw new ArgumentException(
            "GitHub App-mode credential requires an installation id — either " +
            "'installationId' in the credential JSON or a numeric " +
            "InstallationExternalId on the installation row.",
            nameof(installation));
    }

    /// <summary>
    /// Strip scheme + path from a base URL to recover the host, with a
    /// default for empty / un-parseable values.
    /// <c>https://api.github.com</c> → <c>api.github.com</c>;
    /// <c>github.acme.corp</c> → unchanged.
    /// </summary>
    internal static string ExtractHost(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return DefaultHost;
        }
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            && !string.IsNullOrEmpty(uri.Host))
        {
            return uri.Host;
        }
        return baseUrl;
    }
}
