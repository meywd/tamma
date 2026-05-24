using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Api.Services.Alerts;
using Tamma.Api.Services.Alerts.Rules;
using Tamma.Api.Services.Conventions;
using Tamma.Api.Services.PlatformTasks;

namespace Tamma.Api.Tests.Infrastructure;

/// <summary>
/// Shared helper for test fixtures: gates the always-on hosted
/// services wired into <c>Program.cs</c>
/// (<see cref="BuiltInAlertRuleSeeder"/>,
/// <see cref="AlertRuleEvaluator"/>, <see cref="NotificationDispatcher"/>,
/// the Story 28-6 platform task worker, and the Story 27-16
/// <see cref="ConventionStoreSeeder"/>)
/// off for the test host. Each gated service still ships its public
/// drive-once entry point (<c>SeedAsync</c>, <c>ProcessOnceAsync</c>,
/// <c>DispatchOnceAsync</c>) so opt-in tests can exercise them
/// deterministically.
///
/// <para>Skipping these saves ~1-2s per <c>WebApplicationFactory</c>
/// boot. With 75 integration tests in <c>Tamma.Api.Tests</c> the
/// regression added ~2.5 minutes of wall-clock time; this helper claws
/// it back.</para>
/// </summary>
internal static class AlertHostedServiceTestExtensions
{
    public static IWebHostBuilder DisableAlertHostedServices(
        this IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<BuiltInAlertRuleSeederOptions>();
            services.AddSingleton(new BuiltInAlertRuleSeederOptions
            {
                RunOnStartup = false,
            });

            services.RemoveAll<AlertRuleEvaluatorOptions>();
            services.AddSingleton(new AlertRuleEvaluatorOptions
            {
                RunOnStartup = false,
            });

            services.RemoveAll<NotificationDispatcherOptions>();
            services.AddSingleton(new NotificationDispatcherOptions
            {
                RunOnStartup = false,
            });

            // Story 28-6 — gate the platform task worker the same way.
            services.RemoveAll<PlatformTaskWorkerOptions>();
            services.AddSingleton(new PlatformTaskWorkerOptions
            {
                RunOnStartup = false,
            });

            // Story 27-16 — gate the convention system-default seeder so the
            // WebApplicationFactory boot doesn't round-trip the DB per test.
            services.RemoveAll<ConventionStoreSeederOptions>();
            services.AddSingleton(new ConventionStoreSeederOptions
            {
                RunOnStartup = false,
            });
        });

        return builder;
    }
}
