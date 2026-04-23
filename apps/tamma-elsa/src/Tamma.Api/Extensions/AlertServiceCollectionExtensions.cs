using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Api.Services.Alerts;
using Tamma.Api.Services.Alerts.Channels;
using Tamma.Api.Services.Alerts.Rules;
using Tamma.Api.Services.Secrets.Postgres;
using Tamma.Data.Abstractions;

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

        // Wave C.4 — emitter used by activities/clients to push the 5
        // built-in rule-trigger events into the DCB store. Scoped
        // because the inner IEventRepository is scoped.
        services.TryAddScoped<IAlertEventEmitter, AlertEventEmitter>();

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

    /// <summary>
    /// Story 5.6 (Wave C.2) — register the alert rule engine on top
    /// of <see cref="AddTammaAlerts"/>. Idempotent: safe to call in
    /// addition to AddTammaAlerts.
    /// </summary>
    public static IServiceCollection AddTammaAlertRuleEngine(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        // Rolling-window counter — singleton so all rule evaluations
        // in the process share the same bucket. The InMemory impl is
        // thread-safe.
        services.TryAddSingleton<IRuleWindowStore, InMemoryRuleWindowStore>();

        // Evaluator options — configurable via post-configure in
        // tests, defaults suit production.
        services.TryAddSingleton<AlertRuleEvaluatorOptions>();

        // Registry — singleton facade that caches rules + hot-swaps
        // snapshots on refresh. Lifetime singleton because readers
        // must see a consistent snapshot across scopes; internal
        // reads use copy-on-write.
        services.TryAddSingleton<IAlertRuleRegistry, AlertRuleRegistry>();

        // Seeder runs at startup and emits the five built-ins. Must
        // be registered BEFORE the evaluator hosted service so its
        // StartAsync completes first.
        services.AddHostedService<BuiltInAlertRuleSeeder>();

        // Evaluator background service. Uses IServiceProvider to
        // spawn per-tick scopes so the scoped ControlPlaneDbContext /
        // IAlertSink / IEventRepository dependencies are fresh each
        // tick.
        services.AddHostedService<AlertRuleEvaluator>();

        return services;
    }
}
