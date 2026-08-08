using Microsoft.Extensions.DependencyInjection;
using Tamma.Api.Services.GitHub;
using Tamma.Core.Logging;
using Tamma.Platforms.Abstractions;

namespace Tamma.Api.Services.Webhooks.Handlers;

/// <summary>
/// Epic 31 P4 M1 — the FIRST production <see cref="IWebhookHandler"/> on the
/// 31-7 dispatcher: GitHub App installation lifecycle (install-linking).
///
/// <para><b>Port, not invention.</b> The legacy <c>POST /api/github/webhooks</c>
/// route processes <c>installation</c> (created / deleted / suspend /
/// unsuspend) and <c>installation_repositories</c> (added / removed) by
/// delegating to <see cref="IInstallationRouterService.HandleWebhookAsync"/>.
/// This handler delegates to the SAME service with the SAME payload, so the
/// behavior (github_installations upsert / hard-delete / suspend flags, repo
/// seeding, <c>INSTALLATION.*</c> DCB events, 60s cache invalidation) is
/// byte-for-byte the legacy path's — reached through the platform-agnostic
/// receiver (<c>POST /api/webhooks/github</c>) instead of the GitHub-only
/// route.</para>
///
/// <para><b>Tenant scoping.</b> Installation events are keyed by the payload's
/// <c>installation.id</c>; the router resolves tenancy through the
/// <c>github_installations</c> row exactly as the legacy route does. The
/// handler never widens scope beyond that id.</para>
///
/// <para><b>Idempotency.</b> The receiver's
/// <c>platform_webhook_deliveries</c> table dedupes re-deliveries before
/// dispatch; the router's own operations (upsert / idempotent delete /
/// AddRepo) are additionally safe under replay.</para>
///
/// <para>Registered TWICE (one instance per pattern —
/// <c>installation.*</c> and <c>installation_repositories.*</c>) because the
/// dispatcher keys handlers by (kind, pattern).</para>
/// </summary>
public sealed class GitHubInstallationWebhookHandler : IWebhookHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GitHubInstallationWebhookHandler> _logger;

    public GitHubInstallationWebhookHandler(
        IServiceScopeFactory scopeFactory,
        string eventTypePattern,
        ILogger<GitHubInstallationWebhookHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventTypePattern);
        ArgumentNullException.ThrowIfNull(logger);
        _scopeFactory = scopeFactory;
        EventTypePattern = eventTypePattern;
        _logger = logger;
    }

    public PlatformKind Kind => PlatformKind.GitHub;

    public string EventTypePattern { get; }

    public async Task HandleAsync(PlatformWebhookEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        // Scoped resolve — the router touches the per-request DbContext
        // via its repositories; the handler itself is a singleton.
        using var scope = _scopeFactory.CreateScope();
        var router = scope.ServiceProvider.GetService<IInstallationRouterService>();
        if (router is null)
        {
            _logger.LogWarning(
                "GitHub {EventType} webhook received but IInstallationRouterService is not registered — skipping",
                LogSanitizer.Clean(evt.EventType));
            return;
        }

        var result = await router
            .HandleWebhookAsync(evt.EventType, evt.ParsedJson)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "GitHub {EventType} (action={Action}, delivery={DeliveryId}) handled via platform receiver, skipped={Skipped}",
            LogSanitizer.Clean(result.EventType), LogSanitizer.Clean(result.Action),
            LogSanitizer.Clean(evt.DeliveryId), result.Skipped);
    }
}
