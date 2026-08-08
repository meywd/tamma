using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.Webhooks.Handlers;
using Tamma.Data.Repositories;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Webhooks;

namespace Tamma.Api.Services.Webhooks;

/// <summary>
/// Story 31-7 — DI registration helpers for the webhook receiver.
/// Wires:
/// <list type="bullet">
///   <item>Per-platform <see cref="IWebhookSignatureVerifier"/> under
///         keyed-DI keys.</item>
///   <item><see cref="IWebhookEventDispatcher"/> as a singleton.</item>
///   <item><see cref="IPlatformWebhookDeliveryRepository"/> as a scoped
///         service over the control-plane DbContext.</item>
///   <item><see cref="IWebhookEventCategoryMapper"/> +
///         <see cref="IWebhookSecretResolver"/>.</item>
/// </list>
///
/// <para>Call from <c>Program.cs</c>: <c>builder.Services.AddTammaWebhookReceiver();</c></para>
/// </summary>
public static class WebhookServiceCollectionExtensions
{
    public static IServiceCollection AddTammaWebhookReceiver(
        this IServiceCollection services)
    {
        // ── Verifiers (keyed by PlatformKind) ──
        // Each verifier targets a single PlatformKind. Production
        // bindings: GitHub + Gitea + Forgejo use HMAC-SHA256 with
        // platform-specific header names; GitLab uses static-token.
        services.AddKeyedSingleton<IWebhookSignatureVerifier>(
            PlatformKind.GitHub,
            (sp, _) => new HmacWebhookSignatureVerifier(
                PlatformKind.GitHub,
                primaryHeader: "X-Hub-Signature-256",
                fallbackHeader: null,
                logger: sp.GetService<ILogger<HmacWebhookSignatureVerifier>>()));

        services.AddKeyedSingleton<IWebhookSignatureVerifier>(
            PlatformKind.Gitea,
            (sp, _) => new HmacWebhookSignatureVerifier(
                PlatformKind.Gitea,
                primaryHeader: "X-Gitea-Signature",
                fallbackHeader: null,
                logger: sp.GetService<ILogger<HmacWebhookSignatureVerifier>>()));

        services.AddKeyedSingleton<IWebhookSignatureVerifier>(
            PlatformKind.Forgejo,
            (sp, _) => new HmacWebhookSignatureVerifier(
                PlatformKind.Forgejo,
                primaryHeader: "X-Forgejo-Signature",
                fallbackHeader: "X-Gitea-Signature", // Forgejo derives from Gitea
                logger: sp.GetService<ILogger<HmacWebhookSignatureVerifier>>()));

        services.AddKeyedSingleton<IWebhookSignatureVerifier>(
            PlatformKind.GitLab,
            (_, _) => new StaticTokenWebhookSignatureVerifier(
                PlatformKind.GitLab,
                headerName: "X-Gitlab-Token"));

        // ── Production handlers (Epic 31 P4 M1) ──
        // The 31-7 dispatcher shipped with ZERO registered handlers — verified
        // deliveries were dropped on the floor. These are the first production
        // registrations. Handlers are singletons that open their own DI scope
        // per dispatch (the IWebhookHandler lifetime contract).
        //
        // (a) GitHub installation / install-linking — ports the legacy
        //     /api/github/webhooks installation handling (created/deleted/
        //     suspend/unsuspend + repo add/remove) onto the neutral receiver.
        services.AddSingleton<IWebhookHandler>(sp => new GitHubInstallationWebhookHandler(
            sp.GetRequiredService<IServiceScopeFactory>(),
            "installation.*",
            sp.GetRequiredService<ILogger<GitHubInstallationWebhookHandler>>()));
        services.AddSingleton<IWebhookHandler>(sp => new GitHubInstallationWebhookHandler(
            sp.GetRequiredService<IServiceScopeFactory>(),
            "installation_repositories.*",
            sp.GetRequiredService<ILogger<GitHubInstallationWebhookHandler>>()));

        // (b) CI-run completion → wake the suspended CI wait early (DG-5
        //     accelerator; the P3 poller stays as the fallback). One instance
        //     per (platform, event vocabulary):
        //     GitHub fires workflow_run with action=completed; Gitea/Forgejo
        //     mirror the payload but action vocabularies vary across versions,
        //     so they bind the bare event type and filter on a terminal
        //     conclusion; GitLab's Pipeline Hook has no action field at all.
        services.AddSingleton<IWebhookHandler>(sp => BuildCiWakeHandler(
            sp, PlatformKind.GitHub, "workflow_run.completed"));
        services.AddSingleton<IWebhookHandler>(sp => BuildCiWakeHandler(
            sp, PlatformKind.Gitea, "workflow_run"));
        services.AddSingleton<IWebhookHandler>(sp => BuildCiWakeHandler(
            sp, PlatformKind.Forgejo, "workflow_run"));
        services.AddSingleton<IWebhookHandler>(sp => BuildCiWakeHandler(
            sp, PlatformKind.GitLab, "pipeline"));

        // ── Dispatcher + handler registry ──
        // Built as a factory so every registered IWebhookHandler lands in the
        // dispatcher's registry at first resolve — no hosted service needed
        // (and none wanted: registration is pure in-memory wiring, not a
        // background actor).
        services.AddSingleton<IWebhookEventDispatcher>(sp =>
        {
            var dispatcher = new WebhookEventDispatcher(
                sp.GetRequiredService<ILogger<WebhookEventDispatcher>>());
            foreach (var handler in sp.GetServices<IWebhookHandler>())
            {
                dispatcher.RegisterHandler(handler);
            }
            return dispatcher;
        });

        // ── Idempotency repo ──
        services.AddScoped<IPlatformWebhookDeliveryRepository, PlatformWebhookDeliveryRepository>();

        // ── Mappers / resolvers ──
        services.AddSingleton<IWebhookEventCategoryMapper, DefaultWebhookEventCategoryMapper>();
        services.AddScoped<IWebhookSecretResolver, WebhookSecretResolver>();

        return services;
    }

    private static CiRunCompletionWebhookHandler BuildCiWakeHandler(
        IServiceProvider sp, PlatformKind kind, string pattern) =>
        new(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            kind,
            pattern,
            sp.GetRequiredService<ILogger<CiRunCompletionWebhookHandler>>());
}
