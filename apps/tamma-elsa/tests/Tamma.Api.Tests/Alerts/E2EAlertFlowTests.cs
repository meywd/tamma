using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NUnit.Framework;
using Tamma.Api.Extensions;
using Tamma.Api.Services.Alerts;
using Tamma.Api.Services.Alerts.Channels;
using Tamma.Api.Services.Alerts.Rules;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Alerts;

/// <summary>
/// Wave C.4 — end-to-end integration tests for the alert pipeline:
///
/// <code>
/// emitter → IEventRepository / IPlatformEventPublisher
///         → domain_events / platform_events
///         → AlertRuleEvaluator (1s poll)
///         → matched rule → IAlertSink.RaiseAsync
///         → alerts row + alert_delivery_attempts row (status=pending)
///         → NotificationDispatcher (1s poll) → IAlertChannel.SendAsync
///         → webhook stub receives POST
/// </code>
///
/// <para>The stub is a <see cref="DelegatingHandler"/> injected into
/// the named HttpClient used by <see cref="WebhookAlertChannel"/>. It
/// records every captured request body so tests can assert both "the
/// pipeline completed" (delivery_attempt.status=success) and "the
/// right payload left the process" (body contains alert id/title).</para>
///
/// <para>Uses the shared <see cref="ApiTestFixture"/> Postgres
/// container but a per-test <see cref="WebApplicationFactory{TEntryPoint}"/>
/// so we can swap the outbound HTTP handler without polluting other
/// integration-test fixtures.</para>
/// </summary>
[TestFixture]
public class E2EAlertFlowTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private CapturingHttpMessageHandler _capturingHandler = null!;

    [SetUp]
    public async Task SetUp()
    {
        await ApiTestFixture.ResetDatabaseAsync();

        _capturingHandler = new CapturingHttpMessageHandler();
        _factory = ApiTestFixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                // Swap the AlertChannelHttp named client's primary handler
                // with our capturing delegate so webhook POSTs are
                // recorded instead of hitting the network.
                services.AddHttpClient(
                    AlertServiceCollectionExtensions.AlertChannelHttpClientName,
                    c =>
                    {
                        c.Timeout = TimeSpan.FromSeconds(5);
                    })
                    .ConfigurePrimaryHttpMessageHandler(() => _capturingHandler);

                // Swap the IAlertChannelSecretReader for one that
                // returns a well-known HMAC plaintext for the webhook
                // secret id we stage in the DB. Keeps the real secret
                // store out of the test path.
                services.RemoveAll<IAlertChannelSecretReader>();
                services.AddSingleton<IAlertChannelSecretReader>(
                    new StaticSecretReader("webhook-test-secret"));

                // Parent ApiTestFixture gates the three alert hosted
                // services off to save ~2 min across the 75 non-alert
                // integration tests. E2E alert flow tests explicitly
                // opt BACK IN by staging options with RunOnStartup=true
                // AFTER the parent's DisableAlertHostedServices callback
                // ran. RemoveAll+AddSingleton here wins over the parent
                // because ConfigureTestServices runs later in the pipeline.

                // Re-enable the seeder so built-in rules get planted.
                services.RemoveAll<BuiltInAlertRuleSeederOptions>();
                services.AddSingleton(new BuiltInAlertRuleSeederOptions
                {
                    RunOnStartup = true,
                });

                // Shorten the dispatcher poll so E2E tests complete
                // within seconds rather than the production 10s cadence.
                // RunOnStartup defaults to true on a fresh options
                // instance, so no need to set it explicitly.
                services.RemoveAll<NotificationDispatcherOptions>();
                services.AddSingleton(new NotificationDispatcherOptions
                {
                    PollInterval = TimeSpan.FromMilliseconds(200),
                    BackoffSchedule = new[]
                    {
                        TimeSpan.FromMilliseconds(50),
                        TimeSpan.FromMilliseconds(100),
                        TimeSpan.FromMilliseconds(200),
                        TimeSpan.FromMilliseconds(500),
                        TimeSpan.FromSeconds(1),
                    },
                });

                // Give this factory's evaluator a unique cursor id so
                // it doesn't collide with the parent ApiTestFixture's
                // evaluator (both share the same physical Postgres).
                // Without this, both evaluators race on the
                // alert_evaluator_cursor primary key and the test-side
                // evaluator throws a duplicate-key exception on every
                // cursor save.
                services.RemoveAll<Tamma.Api.Services.Alerts.Rules.AlertRuleEvaluatorOptions>();
                services.AddSingleton(
                    new Tamma.Api.Services.Alerts.Rules.AlertRuleEvaluatorOptions
                    {
                        PollInterval = TimeSpan.FromMilliseconds(200),
                        RegistryRefreshInterval = TimeSpan.FromSeconds(30),
                        BatchSize = 100,
                        EvaluatorId = "e2e-" + Guid.NewGuid().ToString("N")[..8],
                    });
            });
        });
    }

    [TearDown]
    public void TearDown()
    {
        _factory?.Dispose();
        _capturingHandler?.Dispose();
    }

    [Test]
    public async Task BudgetExhaustedEvent_FlowsThroughRuleEngineToWebhook()
    {
        // Boot the factory so the seeder + evaluator + dispatcher start.
        _ = _factory.CreateClient();

        // Pre-seed a webhook channel. No tenant scope (platform-wide).
        // The built-in budget-exhausted rule exists from
        // BuiltInAlertRuleSeeder (runs in Program.cs on startup).
        var tenantId = Guid.NewGuid();
        var correlation = Guid.NewGuid().ToString("N");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            db.AlertChannels.Add(new AlertChannel
            {
                Id = Guid.NewGuid(),
                Name = "E2E Webhook",
                ChannelType = AlertChannelType.Webhook,
                TenantId = null,
                Config = """{"url":"https://webhook.test/alert"}""",
                CredentialsSecretId = Guid.NewGuid(),
                IsEnabled = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            // Story 28-1 PR D — domain_events live on the tenant DB and
            // AlertRuleEvaluator fans out across active tenants from CP.
            // The seeded tenant id MUST exist in cp.tenants for the fan-out
            // to discover the per-tenant DB containing the budget-exhausted
            // event.
            db.Tenants.Add(new Data.Entities.Tenant
            {
                Id = tenantId,
                Name = $"e2e-{tenantId:N}",
                Slug = $"e2e-{tenantId:N}".Substring(0, 16),
                Type = "personal",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // Emit directly via IAlertEventEmitter — this is the shortest
        // path that mirrors what CheckBudgetActivity will do in a
        // running workflow. We could equivalently insert a raw
        // domain_events row; going through the emitter validates that
        // the seam produces correctly-shaped rows the evaluator picks up.
        using (var scope = _factory.Services.CreateScope())
        {
            var emitter = scope.ServiceProvider.GetRequiredService<IAlertEventEmitter>();
            await emitter.EmitBudgetExhaustedAsync(new BudgetExhaustedEvent(
                TenantId: tenantId,
                CorrelationId: correlation,
                Source: "api",
                Spent: 12.50m,
                Limit: 12.00m,
                ProviderName: "anthropic-claude",
                WorkflowInstanceId: "wf-e2e-1"), default);
        }

        // Wait for: evaluator tick (1s) → sink writes alert + pending
        // delivery → dispatcher tick (1s) → webhook POST → attempt
        // flipped to 'success'. Worst case ≤ 4s; we budget 10s.
        var alertDelivered = await WaitForAsync(
            async () =>
            {
                using var scope = _factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
                var attempt = await db.AlertDeliveryAttempts
                    .AsNoTracking()
                    .FirstOrDefaultAsync(a =>
                        a.Status == AlertDeliveryStatus.Success);
                return attempt is not null;
            },
            timeout: TimeSpan.FromSeconds(10));

        alertDelivered.Should().BeTrue(
            "the full pipeline emitter → evaluator → sink → dispatcher → " +
            "webhook should complete within 10s");

        // Assert: at least one alert row matching a budget-exhausted
        // rule.
        //
        // Note: the shared-fixture topology means the parent
        // ApiTestFixture.Factory's AlertRuleEvaluator + seeder + the
        // per-test _factory's seeder ALL write into the same physical
        // Postgres container. The parent fixture's seeder populates
        // rules independently from the per-test factory's seeder, so
        // we can see multiple built-in rule rows with BuiltInKey =
        // "budget-exhausted" (one per factory-boot in the test run).
        // That's an artifact of the ApiTestFixture design, not a
        // production concern — in real Tamma there's one host and one
        // seeder. Assert "at least one" of each entity instead of
        // "exactly one".
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var budgetRules = await db.AlertRules.AsNoTracking()
                .Where(r => r.BuiltInKey == "budget-exhausted")
                .Select(r => r.Id).ToListAsync();
            budgetRules.Should().NotBeEmpty();

            var budgetAlerts = await db.Alerts.AsNoTracking()
                .Where(a => a.RuleId != null && budgetRules.Contains(a.RuleId!.Value))
                .ToListAsync();
            budgetAlerts.Should().NotBeEmpty();
            budgetAlerts[0].Severity.Should().Be(AlertSeverity.Warning);
            budgetAlerts[0].TenantId.Should().Be(tenantId);

            var attempts = await db.AlertDeliveryAttempts.AsNoTracking()
                .Where(a => a.Status == AlertDeliveryStatus.Success)
                .ToListAsync();
            attempts.Should().NotBeEmpty();
        }

        // Webhook stub captured at least one matching POST. Shape
        // matches the webhook-alert-channel envelope (alert body +
        // deliveredAt). Because the shared fixture's parent factory +
        // per-test factory both produced alerts, we may see multiple
        // POSTs here — the contract we care about is "the payload is
        // correctly shaped + the URL + method are right", not "exactly
        // one POST".
        _capturingHandler.Captured.Should().NotBeEmpty();
        var captured = _capturingHandler.Captured[0];
        captured.Method.Should().Be(HttpMethod.Post);
        captured.Url.Should().Be("https://webhook.test/alert");
        captured.Body.Should().NotBeNullOrEmpty();
        captured.Body!.Should().Contain("budget-exhausted",
            "webhook body includes the rule name");
    }

    [Test]
    public async Task SecretRotationFailedEvent_FlowsThroughRuleEngineToWebhook()
    {
        _ = _factory.CreateClient();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            db.AlertChannels.Add(new AlertChannel
            {
                Id = Guid.NewGuid(),
                Name = "E2E Rotation Webhook",
                ChannelType = AlertChannelType.Webhook,
                TenantId = null,
                Config = """{"url":"https://webhook.test/rotation"}""",
                CredentialsSecretId = Guid.NewGuid(),
                IsEnabled = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // Direct DB insert rather than going through the emitter —
        // the saga emitter path is covered by SagaRunnerAlertEmissionTests;
        // this test asserts the rule-evaluator consumes an event already
        // shaped as SECRET.ROTATION.FAILED.
        var tenantId = Guid.NewGuid();
        // Story 28-1 PR D — domain_events live on the tenant DB. Seed the
        // tenant in CP so AlertRuleEvaluator's per-tenant fan-out finds
        // it, then route the event seed through ITenantDbContextFactory.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            db.Tenants.Add(new Data.Entities.Tenant
            {
                Id = tenantId,
                Name = $"e2e-rot-{tenantId:N}",
                Slug = $"e2e-rot-{tenantId:N}".Substring(0, 16),
                Type = "personal",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();

            var factory = scope.ServiceProvider
                .GetRequiredService<Tamma.Data.Abstractions.ITenantDbContextFactory>();
            await using var tdb = await factory.CreateAsync(tenantId);
            tdb.DomainEvents.Add(new DomainEvent
            {
                Id = Guid.NewGuid(),
                Type = "SECRET.ROTATION.FAILED",
                TenantId = tenantId,
                Tags = JsonSerializer.Serialize(new
                {
                    tenantId = tenantId.ToString(),
                    cabinetName = "db/app-role",
                    handlerType = "postgres",
                }),
                Metadata = """{"eventSource":"system"}""",
                Data = JsonSerializer.Serialize(new
                {
                    targetKind = "postgres-role",
                    cabinetName = "db/app-role",
                    handlerType = "postgres",
                    failureStage = "push",
                    compensationApplied = true,
                    lastError = "conn_refused",
                }),
                CreatedAt = DateTime.UtcNow,
            });
            await tdb.SaveChangesAsync();
        }

        var delivered = await WaitForAsync(
            async () =>
            {
                using var scope = _factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
                return await db.AlertDeliveryAttempts.AsNoTracking()
                    .AnyAsync(a => a.Status == AlertDeliveryStatus.Success);
            },
            timeout: TimeSpan.FromSeconds(10));

        delivered.Should().BeTrue();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var rule = await db.AlertRules.AsNoTracking()
                .SingleAsync(r => r.BuiltInKey == "secret-rotation-failed");
            var alerts = await db.Alerts.AsNoTracking()
                .Where(a => a.RuleId == rule.Id).ToListAsync();
            alerts.Should().NotBeEmpty();
            alerts[0].Severity.Should().Be(AlertSeverity.Critical);
            alerts[0].TenantId.Should().Be(tenantId);
        }

        _capturingHandler.Captured.Should().NotBeEmpty();
        _capturingHandler.Captured[0].Body.Should().NotBeNullOrEmpty();
    }

    // ── Helpers ─────────────────────────────────────────────────

    private static async Task<bool> WaitForAsync(
        Func<Task<bool>> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await predicate()) return true;
            await Task.Delay(200);
        }
        return false;
    }

    private sealed class CapturingHttpMessageHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Captured { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = null;
            if (request.Content is not null)
            {
                body = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            Captured.Add(new CapturedRequest(
                Method: request.Method,
                Url: request.RequestUri?.ToString() ?? string.Empty,
                Body: body));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, string Url, string? Body);

    private sealed class StaticSecretReader : IAlertChannelSecretReader
    {
        private readonly string _plaintext;
        public StaticSecretReader(string plaintext) => _plaintext = plaintext;
        public Task<string?> GetPlaintextAsync(Guid secretId, CancellationToken ct)
            => Task.FromResult<string?>(_plaintext);
    }
}
