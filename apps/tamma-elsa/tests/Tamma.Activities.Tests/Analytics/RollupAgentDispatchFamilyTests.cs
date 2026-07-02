using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Tamma.Activities.Analytics;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Activities.Tests.Analytics;

/// <summary>
/// Regression guard for the Story 28-10 hourly-rollup dispatch counter.
/// The LIVE agent-dispatch family is emitted with an UNDERSCORE prefix
/// (<c>AGENT_DISPATCH.RUN_TRIGGERED.*</c>, see
/// <c>AgentDispatchEventTypes</c>); the older alert/analytics family is
/// dotted (<c>AGENT.DISPATCH.*</c>). The original code filtered with a
/// dotted <c>LIKE "AGENT.DISPATCH.%"</c> that never matched the underscore
/// family (and '_' is itself a LIKE single-char wildcard), so both the
/// platform-wide and per-tenant rollups recorded ~0 real dispatches.
///
/// <para>These tests assert the dual-family <see cref="string.StartsWith(string)"/>
/// predicate (matching <see cref="ComputeTenantDimensionalRollupActivity"/>):
/// a <c>RUN_TRIGGERED</c> event IS counted, a legacy dotted event IS counted,
/// and a follow-up <c>RUN_POLLED</c> is NOT (only the RUN_TRIGGERED terminal is
/// a dispatch). They FAIL against the old dotted-LIKE code and PASS after.</para>
/// </summary>
[TestFixture]
public class RollupAgentDispatchFamilyTests
{
    private static readonly DateTime Hour = new(2026, 04, 18, 12, 00, 00, DateTimeKind.Utc);

    private IDbContextFactory<ControlPlaneDbContext> _cpFactory = null!;
    private FakeTenantDbContextFactory _tenantFactory = null!;
    private Mock<IPlatformEventPublisher> _publisher = null!;
    private List<IDisposable> _opened = null!;

    [SetUp]
    public void SetUp()
    {
        _opened = new List<IDisposable>();
        _cpFactory = new InMemoryCpFactory($"cp-dispatch-{Guid.NewGuid()}", _opened);
        _tenantFactory = new FakeTenantDbContextFactory(_opened);
        _publisher = new Mock<IPlatformEventPublisher>();
        _publisher
            .Setup(p => p.AppendAndPublishAsync(It.IsAny<PlatformEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlatformEvent evt, CancellationToken _) => evt);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var ctx in _opened) ctx.Dispose();
    }

    // ── Platform-wide rollup counts the LIVE underscore family + legacy dotted ──
    [Test]
    public async Task ComputePlatformRollup_CountsRunTriggeredAndLegacy_NotRunPolled()
    {
        using (var seed = _cpFactory.CreateDbContext())
        {
            seed.PlatformEvents.AddRange(
                // LIVE Story 38-2 mediation family — underscore. Counted.
                PlatformEvt("AGENT_DISPATCH.RUN_TRIGGERED.SUCCESS", Hour.AddMinutes(5)),
                PlatformEvt("AGENT_DISPATCH.RUN_TRIGGERED.FAILED", Hour.AddMinutes(6)),
                // Legacy alert/analytics family — dotted. Still counted.
                PlatformEvt("AGENT.DISPATCH.SUCCESS", Hour.AddMinutes(7)),
                // Follow-up ops — NOT dispatches, must be excluded.
                PlatformEvt("AGENT_DISPATCH.RUN_POLLED.SUCCESS", Hour.AddMinutes(8)),
                PlatformEvt("AGENT_DISPATCH.RESULTS_COLLECTED.SUCCESS", Hour.AddMinutes(9)));
            await seed.SaveChangesAsync();
        }

        await ComputePlatformRollupActivity.ComputeAsync(
            _cpFactory, _publisher.Object, Hour, logger: null, CancellationToken.None);

        using var read = _cpFactory.CreateDbContext();
        var row = await read.PlatformAnalyticsHourly.SingleAsync(r => r.Hour == Hour && r.TenantId == null);

        row.AgentDispatches.Should().Be(3,
            "both RUN_TRIGGERED terminals + the legacy dotted event count; RUN_POLLED / RESULTS_COLLECTED do not");
    }

    // ── Per-tenant rollup counts the LIVE underscore family + legacy dotted ──
    [Test]
    public async Task ComputeTenantRollup_CountsRunTriggeredAndLegacy_NotRunPolled()
    {
        var tenantId = Guid.NewGuid();
        var db = _tenantFactory.Register(tenantId);
        db.DomainEvents.AddRange(
            // LIVE Story 38-2 mediation family — underscore. Counted.
            TenantEvt("AGENT_DISPATCH.RUN_TRIGGERED.SUCCESS", 1, Hour.AddMinutes(5)),
            TenantEvt("AGENT_DISPATCH.RUN_TRIGGERED.FAILED", 2, Hour.AddMinutes(6)),
            // Legacy alert/analytics family — dotted. Still counted.
            TenantEvt("AGENT.DISPATCH.SUCCESS", 3, Hour.AddMinutes(7)),
            // Follow-up ops — NOT dispatches, must be excluded.
            TenantEvt("AGENT_DISPATCH.RUN_POLLED.SUCCESS", 4, Hour.AddMinutes(8)),
            TenantEvt("AGENT_DISPATCH.RESULTS_COLLECTED.SUCCESS", 5, Hour.AddMinutes(9)));
        await db.SaveChangesAsync();

        await ComputeTenantRollupActivity.ComputeAsync(
            _cpFactory, _tenantFactory, _publisher.Object, tenantId, Hour, logger: null, CancellationToken.None);

        using var cp = _cpFactory.CreateDbContext();
        var row = await cp.PlatformAnalyticsHourly.SingleAsync(r => r.Hour == Hour && r.TenantId == tenantId);

        row.AgentDispatches.Should().Be(3,
            "both RUN_TRIGGERED terminals + the legacy dotted event count; RUN_POLLED / RESULTS_COLLECTED do not");
    }

    private static PlatformEvent PlatformEvt(string type, DateTime at) => new()
    {
        Id = Guid.NewGuid(),
        Type = type,
        CreatedAt = at,
        Tags = "{}",
        Metadata = "{}",
        Data = "{}",
    };

    private static DomainEvent TenantEvt(string type, long seq, DateTime at) => new()
    {
        Id = Guid.NewGuid(),
        Type = type,
        CreatedAt = at,
        SequenceNumber = seq,
        Tags = "{}",
        Metadata = "{}",
        Data = "{}",
    };

    private sealed class InMemoryCpFactory : IDbContextFactory<ControlPlaneDbContext>
    {
        private readonly string _dbName;
        private readonly List<IDisposable> _opened;

        public InMemoryCpFactory(string dbName, List<IDisposable> opened)
        {
            _dbName = dbName;
            _opened = opened;
        }

        public ControlPlaneDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
                .UseInMemoryDatabase(_dbName)
                .ConfigureWarnings(w => w.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var ctx = new ControlPlaneDbContext(options);
            _opened.Add(ctx);
            return ctx;
        }

        public Task<ControlPlaneDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
