using Tamma.Api.Services.Notifications;

namespace Tamma.Api.Extensions;

/// <summary>
/// Story 38-3 — wires the Slack notification mediation subsystem into DI: the
/// out-of-band <see cref="OutboxSlackSender"/> (the sole webhook-credential holder)
/// and its options. The <c>ISlackOutboxRepository</c> is registered by
/// <c>Tamma.Data</c>'s <c>AddTammaData</c>; <c>ISlackIntegrationService</c> and
/// <c>IPlatformEventRepository</c> are already registered by the composition root.
/// The sender is registered unconditionally — its <c>ExecuteAsync</c> bails out
/// immediately when <c>Slack:WebhookUrl</c> is unset, so no-webhook deployments pay
/// zero polling overhead.
/// </summary>
public static class NotificationServiceCollectionExtensions
{
    public static IServiceCollection AddSlackNotificationServices(this IServiceCollection services)
    {
        services.AddSingleton<OutboxSlackSenderOptions>();
        services.AddHostedService<OutboxSlackSender>();
        return services;
    }
}
