using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Api.Services.Alerts;
using Tamma.Api.Services.Alerts.Rules;
using Tamma.Api.Services.Conventions;
using Tamma.Api.Services.Email;
using Tamma.Api.Services.Engine.Lifecycle;
using Tamma.Api.Services.PlatformTasks;
using Tamma.Api.Services.Providers;
using Tamma.Api.Services.TaskQueue;

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
///
/// <para>Task #10 (post-review) — also gates four BackgroundService loops
/// that race test assertions on per-row DB state
/// (<see cref="OutboxSmtpSender"/>, <see cref="TaskQueueProcessor"/>,
/// <see cref="ProviderSessionCleanupService"/>,
/// <see cref="EngineRegistryHeartbeatService"/>). The
/// <c>AuthRegisterTxnIdIntegrationTests.Register_OutboxRowPersistedWithMatchingTxnId</c>
/// flake was caused by the sender flipping <c>status="pending"</c> to
/// <c>"sent"</c>/<c>"failed"</c> before the assertion ran; the other three
/// are pre-emptive because they all do DB work on startup.</para>
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

            // Task #10 — OutboxSmtpSender is the root cause of the
            // AuthRegisterTxnId flake. Gate it off; opt-in email tests can
            // override per-fixture.
            services.RemoveAll<OutboxSmtpSenderOptions>();
            services.AddSingleton(new OutboxSmtpSenderOptions
            {
                RunOnStartup = false,
            });

            // Task #10 — TaskQueueProcessor, ProviderSessionCleanupService,
            // and EngineRegistryHeartbeatService were all visible in the
            // flaky test logs hitting the DB on startup. Pre-emptive gate.
            services.RemoveAll<TaskQueueProcessorOptions>();
            services.AddSingleton(new TaskQueueProcessorOptions
            {
                RunOnStartup = false,
            });

            services.RemoveAll<ProviderSessionOptions>();
            services.AddSingleton(new ProviderSessionOptions
            {
                RunOnStartup = false,
            });

            services.RemoveAll<EngineRegistryHeartbeatOptions>();
            services.AddSingleton(new EngineRegistryHeartbeatOptions
            {
                RunOnStartup = false,
            });
        });

        return builder;
    }
}
