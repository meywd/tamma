using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tamma.Activities.AgentDispatch;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Platforms.GitHub;

/// <summary>
/// Story 31-3 — keyed-DI factory consumed by 31-2's
/// <c>PlatformResolver</c> when an installation row's
/// <c>platform_kind = "github"</c>. Builds a fully-wired
/// <see cref="GitHubPlatformDriver"/> bound to a single
/// <see cref="PlatformInstallation"/> by composing the
/// scoped <see cref="IGitHubActionsClient"/> from
/// <c>Tamma.Activities</c>.
///
/// <para>The factory does NOT construct or own the inner Octokit
/// clients — those live in <c>Tamma.Api</c> and are wired by
/// <c>GitHubInstallationServiceCollectionExtensions</c>. This
/// factory consumes them via <see cref="IServiceProvider"/> so the
/// scoped <c>OctokitGitHubActionsClient</c> registration lives within
/// the caller's request scope (matches the captive-dependency rules
/// when the factory itself is a keyed singleton).</para>
///
/// <para>Per-tenant bookkeeping (base URL host, installation external
/// id) is recorded on the driver's inner client at construction so
/// future error / metric paths can surface tenant-aware context.</para>
/// </summary>
public sealed class GitHubPlatformDriverFactory : IGitPlatformDriverFactory
{
    /// <summary>Default GitHub Cloud host used when an installation
    /// row carries no base URL. GHES installs override via
    /// <see cref="PlatformInstallation.BaseUrl"/>.</summary>
    public const string DefaultHost = "github.com";

    private readonly IServiceProvider _services;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Construct the factory. <paramref name="services"/> is used to
    /// resolve the scoped <see cref="IGitHubActionsClient"/> on each
    /// <see cref="CreateAsync"/> call (it is registered as Scoped in
    /// production so it depends on a request-scoped repository).
    /// <paramref name="loggerFactory"/> is optional; null passes
    /// through to <see cref="NullLoggerFactory"/>.
    /// </summary>
    public GitHubPlatformDriverFactory(
        IServiceProvider services,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
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
        // GitHub installation auth uses an App private key + installation
        // id rather than a per-tenant plaintext bearer; the inner
        // OctokitGitHubAppClient holds the App key as a process
        // singleton. We accept the plaintext value for interface
        // symmetry but do not consume it here — the inner client uses
        // its cached App credentials. An empty string would still build
        // a valid (though no-op) driver against NullGitHubActionsClient.
        _ = credentialPlaintext;

        var actionsClient = _services.GetRequiredService<IGitHubActionsClient>();

        var host = ExtractHost(installation.BaseUrl);
        var clientLogger = _loggerFactory.CreateLogger<GitHubPlatformClient>();
        var actionsLogger = _loggerFactory.CreateLogger<GitHubActionsPlatformClient>();

        var client = new GitHubPlatformClient(actionsClient, host, clientLogger);
        IGitPlatformActionsClient actions = new GitHubActionsPlatformClient(actionsClient, actionsLogger);

        IGitPlatformDriver driver = new GitHubPlatformDriver(client, actions);
        return Task.FromResult(driver);
    }

    /// <summary>
    /// Strip scheme + path from a base URL to recover the host, with a
    /// default for empty / un-parseable values. <c>https://api.github.com</c>
    /// → <c>api.github.com</c>; <c>github.acme.corp</c> → unchanged.
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
