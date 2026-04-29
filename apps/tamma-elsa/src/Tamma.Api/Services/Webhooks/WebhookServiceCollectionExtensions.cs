using Microsoft.Extensions.DependencyInjection;
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

        // ── Dispatcher + handler registry ──
        services.AddSingleton<IWebhookEventDispatcher, WebhookEventDispatcher>();

        // ── Idempotency repo ──
        services.AddScoped<IPlatformWebhookDeliveryRepository, PlatformWebhookDeliveryRepository>();

        // ── Mappers / resolvers ──
        services.AddSingleton<IWebhookEventCategoryMapper, DefaultWebhookEventCategoryMapper>();
        services.AddScoped<IWebhookSecretResolver, WebhookSecretResolver>();

        return services;
    }
}
