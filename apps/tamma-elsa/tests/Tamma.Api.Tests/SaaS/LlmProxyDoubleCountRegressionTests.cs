using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using NUnit.Framework;
using Tamma.Activities.Analytics;
using Tamma.Api.Services.Billing;
using Tamma.Api.Services.Diagnostics;
using Tamma.Api.Services.Diagnostics.Models;
using Tamma.Api.Services.SaaS;
using Tamma.Core.Entities;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.SaaS;

/// <summary>
/// Fix 1 (CRITICAL) regression — <see cref="LlmProxyService"/> writes BOTH a
/// <see cref="ProviderDiagnostic"/> row AND an <c>LLM.CALL.SUCCESS</c> DCB event
/// for the SAME proxied call. The dimensional rollup folds both into one bucket,
/// so without a shared correlation id it double-counts cost/tokens/platform-billed
/// (2x). This end-to-end test captures exactly what the proxy writes, feeds the
/// pair into <see cref="ComputeTenantDimensionalRollupActivity.ComputeAsync"/>,
/// and asserts a SINGLE count. It FAILS before the fix (2x) and PASSES after (1x).
/// </summary>
[TestFixture]
public class LlmProxyDoubleCountRegressionTests
{
    private static readonly DateTime Hour = new(2026, 04, 18, 12, 00, 00, DateTimeKind.Utc);

    [Test]
    public async Task ProxyDiagnosticAndUsageEvent_ForOneCall_AreCountedOnce_InDimensionalRollup()
    {
        // ── Arrange: capture the diagnostic + usage event the proxy writes ──
        var tenantId = Guid.NewGuid();
        ProviderDiagnostic? diag = null;
        DomainEvent? evt = null;

        var diagnostics = new Mock<IDiagnosticsService>();
        diagnostics
            .Setup(d => d.GetBudgetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BudgetStatus(
                AccountId: Guid.Empty,
                PeriodStart: DateTime.UtcNow.AddDays(-1),
                PeriodEnd: DateTime.UtcNow.AddDays(1),
                Spent: 0m, Limit: 1_000_000m, Remaining: 1_000_000m,
                PercentUsed: 0, AlertThreshold: 0.8, ShouldAlert: false, IsOverBudget: false));
        diagnostics
            .Setup(d => d.RecordEventAsync(It.IsAny<ProviderDiagnostic>(), It.IsAny<CancellationToken>()))
            .Callback((ProviderDiagnostic p, CancellationToken _) => diag = p)
            .ReturnsAsync(Guid.NewGuid());

        var events = new Mock<IEventRepository>();
        events
            .Setup(e => e.AppendAsync(It.IsAny<DomainEvent>()))
            .ReturnsAsync((DomainEvent d) => d)
            .Callback((DomainEvent d) => evt = d);

        var tagger = new Mock<IBillingModeTagger>();
        tagger
            .Setup(t => t.ResolveTagAsync(
                It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BillingModeTokens.Platform);

        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"model":"claude-sonnet-4.5","content":[{"type":"text","text":"ok"}],"usage":{"input_tokens":1000,"output_tokens":1000}}""",
                    Encoding.UTF8, "application/json"),
            });
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory
            .Setup(f => f.CreateClient("anthropic"))
            .Returns(new HttpClient(handler.Object) { BaseAddress = new Uri("https://api.anthropic.com") });

        var service = new LlmProxyService(
            httpFactory.Object, diagnostics.Object, tagger.Object, events.Object,
            new Mock<ILogger<LlmProxyService>>().Object);

        var resp = await service.ChatAsync(
            new ChatRequest("claude-sonnet-4.5", new[] { new ChatMessage("user", "hi") }, 2048, null),
            tenantId);

        resp.Success.Should().BeTrue();
        diag.Should().NotBeNull();
        evt.Should().NotBeNull();
        var expectedCost = resp.CostUsd; // 1000/1000*0.003 + 1000/1000*0.015 = 0.018
        expectedCost.Should().BeGreaterThan(0m);

        // ── Seed the captured pair into one tenant hour bucket and roll up ──
        var opened = new List<IDisposable>();
        try
        {
            var factory = new InMemoryTenantFactory(opened);
            var db = factory.Register(tenantId);
            diag!.CreatedAt = Hour.AddMinutes(5);
            evt!.CreatedAt = Hour.AddMinutes(5);
            evt.SequenceNumber = 1;
            db.ProviderDiagnostics.Add(diag);
            db.DomainEvents.Add(evt);
            await db.SaveChangesAsync();

            var publisher = new Mock<IPlatformEventPublisher>();
            publisher
                .Setup(p => p.AppendAndPublishAsync(It.IsAny<PlatformEvent>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((PlatformEvent e, CancellationToken _) => e);

            await ComputeTenantDimensionalRollupActivity.ComputeAsync(
                factory, publisher.Object, tenantId, Hour, new NullAnalyticsPricingConfig(),
                resetCheckpoint: false, logger: null, CancellationToken.None);

            // ── Assert: cost / tokens / platform-billed counted EXACTLY ONCE ──
            var verify = await factory.CreateAsync(tenantId);
            var rows = await verify.AnalyticsUsageHourly.ToListAsync();

            rows.Sum(r => r.CostUsd).Should().Be(expectedCost,
                "the diagnostic and its paired LLM.CALL.SUCCESS event describe ONE call — the shared "
                + "correlationId must dedup them to a single count (this summed to 2x before the fix)");
            rows.Sum(r => r.PlatformBilledUsd).Should().Be(expectedCost,
                "billed once at zero margin (null pricing config)");
            rows.Sum(r => r.TokensIn).Should().Be(2000L, "the 2000-token call is counted once");
            rows.Sum(r => r.TokensOut).Should().Be(0L, "the diagnostic total attributes to TokensIn; no event double-add");
        }
        finally
        {
            foreach (var d in opened) d.Dispose();
        }
    }

    /// <summary>Routes a tenant id to its own named InMemory database.</summary>
    private sealed class InMemoryTenantFactory : ITenantDbContextFactory
    {
        private readonly Dictionary<Guid, string> _names = new();
        private readonly List<IDisposable> _opened;
        public InMemoryTenantFactory(List<IDisposable> opened) => _opened = opened;

        public TenantDbContext Register(Guid tenantId)
        {
            var name = $"llmproxy-dim-{tenantId:N}";
            _names[tenantId] = name;
            return Open(name);
        }

        public ValueTask<TenantDbContext> CreateAsync(Guid tenantId, CancellationToken ct = default)
        {
            if (!_names.TryGetValue(tenantId, out var name))
                throw new InvalidOperationException($"Tenant {tenantId} not reachable.");
            return new ValueTask<TenantDbContext>(Open(name));
        }

        private TenantDbContext Open(string name)
        {
            var options = new DbContextOptionsBuilder<TenantDbContext>()
                .UseInMemoryDatabase(name)
                .ConfigureWarnings(w => w.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var ctx = new InMemoryFriendlyTenantDbContext(options);
            _opened.Add(ctx);
            return ctx;
        }
    }

    /// <summary>InMemory-friendly <see cref="TenantDbContext"/> (drops the mentorship
    /// aggregate's jsonb/rowversion columns the InMemory provider rejects).</summary>
    private sealed class InMemoryFriendlyTenantDbContext : TenantDbContext
    {
        public InMemoryFriendlyTenantDbContext(DbContextOptions<TenantDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Ignore<JuniorDeveloper>();
            modelBuilder.Ignore<Story>();
            modelBuilder.Ignore<MentorshipSession>();
            modelBuilder.Ignore<MentorshipEvent>();
        }
    }
}
