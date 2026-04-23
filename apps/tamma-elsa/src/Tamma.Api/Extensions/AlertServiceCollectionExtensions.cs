using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Api.Services.Alerts;
using Tamma.Api.Services.Alerts.Channels;
using Tamma.Api.Services.Secrets.Postgres;

namespace Tamma.Api.Extensions;

/// <summary>
/// Story 5.6 / 1.5-37 (Wave C.1) — DI registration for the alert
/// system. Single entry-point so Program.cs can wire it with one
/// call; the order + lifetime choices match the constraints
/// documented on each service.
/// </summary>
public static class AlertServiceCollectionExtensions
{
    public const string AlertChannelHttpClientName = "AlertChannelHttp";

    /// <summary>
    /// Register the Wave C.1 alert core: sink, dispatcher, four
    /// channels, rate limiter, secret reader.
    /// </summary>
    public static IServiceCollection AddTammaAlerts(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // TimeProvider is registered by Program.cs already; fall back
        // to the system provider when running in tests that haven't
        // staged one explicitly.
        services.TryAddSingleton(TimeProvider.System);

        // Rate limiter — singleton in-memory token bucket. The
        // options type is also singleton so tests can pre-stage a
        // non-default ceiling.
        services.TryAddSingleton<AlertRateLimiterOptions>();
        services.TryAddSingleton<IAlertRateLimiter, TokenBucketAlertRateLimiter>();

        // Dispatcher options — tests override via post-configure.
        services.TryAddSingleton<NotificationDispatcherOptions>();

        // Secret reader — default implementation resolves through the
        // Story 29-2 SecretsDbContextFactory + ISecretStoreBackend.
        // We only register the default when the SecretsDbContextFactory
        // is available (the factory is conditionally wired by
        // AddTammaPostgresSecrets, Story 29-2). When the factory is
        // absent (tests that don't exercise secret-backed channels) we
        // register a NotAvailable stub that throws on read so callers
        // get a clear error instead of a DI validation crash at startup.
        if (services.Any(d => d.ServiceType == typeof(IDbContextFactory<SecretsDbContext>)))
        {
            services.TryAddScoped<
                IAlertChannelSecretReader,
                DefaultAlertChannelSecretReader>();
        }
        else
        {
            services.TryAddSingleton<
                IAlertChannelSecretReader,
                NoSecretStoreAlertChannelSecretReader>();
        }

        // Alert sink — scoped because it depends on the
        // ControlPlaneDbContext + IEventRepository, both scoped.
        services.TryAddScoped<IAlertSink, PostgresAlertSink>();

        // Channel registry — singleton facade over the channel
        // implementations registered below. All four channels resolve
        // scoped dependencies under the hood, so the registry hands
        // out implementations that themselves instantiate the right
        // DI scope per call.
        services.AddScoped<IAlertChannel, EmailAlertChannel>();
        services.AddScoped<IAlertChannel, SlackAlertChannel>();
        services.AddScoped<IAlertChannel, PagerDutyAlertChannel>();
        services.AddScoped<IAlertChannel, WebhookAlertChannel>();
        // The registry must be scoped so IEnumerable<IAlertChannel>
        // resolves the four scoped channel impls from the same scope.
        services.TryAddScoped<IAlertChannelRegistry, AlertChannelRegistry>();

        // Shared HTTP client used by Slack / PagerDuty / webhook
        // channels. 5-second timeout per Wave C.1 plan.
        services.AddHttpClient(AlertChannelHttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(5);
            client.DefaultRequestHeaders.UserAgent.TryParseAdd(
                "tamma-alert-dispatcher/1.0");
        });

        // Background service — hosted singleton. Dispatch work runs
        // in a scoped DI context spawned per tick.
        services.AddHostedService<NotificationDispatcher>();

        return services;
    }
}
