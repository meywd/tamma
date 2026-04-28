using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Tamma.Activities.Analytics;
using Tamma.Core.Entities;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Epic28;

/// <summary>
/// Story 28-10 — integration-style tests for
/// <see cref="ComputeTenantRollupActivity.ComputeAsync"/>. Uses a
/// FakeTenantDbContextFactory that returns an InMemory-backed
/// <see cref="TenantDbContext"/> so we can exercise the
/// read-compute-upsert cycle without a real Postgres server.
/// </summary>
[TestFixture]
public class ComputeTenantRollupActivityTests
{
    private static readonly DateTime Hour =
        new(2026, 04, 18, 12, 00, 00, DateTimeKind.Utc);

    private IDbContextFactory<ControlPlaneDbContext> _cpFactory = null!;
    private FakeTenantDbContextFactory _tenantFactory = null!;
    private Mock<IPlatformEventPublisher> _publisher = null!;
    private List<IDisposable> _opened = null!;

    [SetUp]
    public void SetUp()
    {
        _opened = new List<IDisposable>();
        _cpFactory = new InMemoryCpFactory($"cp-tenant-{Guid.NewGuid()}", _opened);
        _tenantFactory = new FakeTenantDbContextFactory(_opened);
        _publisher = new Mock<IPlatformEventPublisher>(MockBehavior.Strict);
        _publisher
            .Setup(p => p.AppendAndPublishAsync(It.IsAny<PlatformEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlatformEvent evt, CancellationToken _) => evt);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var ctx in _opened) ctx.Dispose();
    }

    [Test]
    public async Task ComputeAsync_AggregatesWorkflowsAndCosts()
    {
        var tenantId = Guid.NewGuid();

        var tenantDb = _tenantFactory.Register(tenantId);
        tenantDb.WorkflowInstances.AddRange(
            NewWorkflow("completed", Hour.AddMinutes(10)),
            NewWorkflow("completed", Hour.AddMinutes(20)),
            NewWorkflow("failed", Hour.AddMinutes(30)),
            // Out of window — excluded.
            NewWorkflow("completed", Hour.AddHours(-1)),
            NewWorkflow("completed", Hour.AddHours(1)));
        tenantDb.DomainEvents.AddRange(
            new DomainEvent { Id = Guid.NewGuid(), Type = "LLM.CALL.SUCCESS",
                              CreatedAt = Hour.AddMinutes(15), Tags = "{}", Metadata = "{}",
                              Data = "{\"costUsd\":0.25,\"inputTokens\":100,\"outputTokens\":50}" },
            new DomainEvent { Id = Guid.NewGuid(), Type = "LLM.CALL.SUCCESS",
                              CreatedAt = Hour.AddMinutes(45), Tags = "{}", Metadata = "{}",
                              Data = "{\"costUsd\":0.75,\"inputTokens\":200,\"outputTokens\":100}" },
            new DomainEvent { Id = Guid.NewGuid(), Type = "AGENT.DISPATCH.SUCCESS",
                              CreatedAt = Hour.AddMinutes(5), Tags = "{}", Metadata = "{}", Data = "{}" },
            new DomainEvent { Id = Guid.NewGuid(), Type = "AGENT.DISPATCH.FAILED",
                              CreatedAt = Hour.AddMinutes(35), Tags = "{}", Metadata = "{}", Data = "{}" });
        await tenantDb.SaveChangesAsync();

        await ComputeTenantRollupActivity.ComputeAsync(
            _cpFactory, _tenantFactory, _publisher.Object,
            tenantId, Hour, logger: null, CancellationToken.None);

        using var cp = _cpFactory.CreateDbContext();
        var row = await cp.PlatformAnalyticsHourly
            .SingleAsync(r => r.Hour == Hour && r.TenantId == tenantId);

        row.WorkflowsStarted.Should().Be(3);
        row.WorkflowsCompleted.Should().Be(2);
        row.WorkflowsFailed.Should().Be(1);
        row.AgentDispatches.Should().Be(2);
        row.CostUsd.Should().Be(1.0m);
        row.TokensIn.Should().Be(300L);
        row.TokensOut.Should().Be(150L);

        _publisher.Verify(
            p => p.AppendAndPublishAsync(
                It.Is<PlatformEvent>(e =>
                    e.Type == AnalyticsRollupEvents.TenantRollupCompleted
                    && e.TenantId == tenantId),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task ComputeAsync_Idempotent_ReplayUpdatesNotDuplicates()
    {
        var tenantId = Guid.NewGuid();
        var tenantDb = _tenantFactory.Register(tenantId);
        tenantDb.WorkflowInstances.Add(NewWorkflow("completed", Hour.AddMinutes(10)));
        await tenantDb.SaveChangesAsync();

        await ComputeTenantRollupActivity.ComputeAsync(
            _cpFactory, _tenantFactory, _publisher.Object,
            tenantId, Hour, logger: null, CancellationToken.None);

        // Add another workflow and rerun.
        tenantDb.WorkflowInstances.Add(NewWorkflow("completed", Hour.AddMinutes(30)));
        await tenantDb.SaveChangesAsync();

        await ComputeTenantRollupActivity.ComputeAsync(
            _cpFactory, _tenantFactory, _publisher.Object,
            tenantId, Hour, logger: null, CancellationToken.None);

        using var cp = _cpFactory.CreateDbContext();
        var rows = await cp.PlatformAnalyticsHourly
            .Where(r => r.Hour == Hour && r.TenantId == tenantId)
            .ToListAsync();

        rows.Should().ContainSingle("replay must upsert, not duplicate");
        rows[0].WorkflowsCompleted.Should().Be(2);
    }

    [Test]
    public async Task ComputeAsync_ThrowsWhenTenantFactoryUnreachable()
    {
        var tenantId = Guid.NewGuid();
        // Do NOT register the tenant — factory will throw.

        Func<Task> act = () => ComputeTenantRollupActivity.ComputeAsync(
            _cpFactory, _tenantFactory, _publisher.Object,
            tenantId, Hour, logger: null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "compute must surface tenant-DB failures to the fan-out catch block");
    }

    private static WorkflowInstance NewWorkflow(string status, DateTime createdAt) => new()
    {
        Id = Guid.NewGuid(),
        DefinitionId = Guid.NewGuid(),
        Status = status,
        Variables = "{}",
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
    };

    private sealed class InMemoryCpFactory : IDbContextFactory<ControlPlaneDbContext>
    {
        private readonly string _dbName;
        private readonly List<IDisposable> _opened;
        public InMemoryCpFactory(string dbName, List<IDisposable> opened) { _dbName = dbName; _opened = opened; }

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

        public Task<ControlPlaneDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(CreateDbContext());
    }

    /// <summary>
    /// Fake — routes each tenant id to its own named InMemory database so
    /// parallel test fixtures don't bleed events into each other.
    /// Throws on unregistered ids to simulate the "tenant DB unreachable"
    /// failure mode exercised by the fan-out activity.
    /// </summary>
    private sealed class FakeTenantDbContextFactory : ITenantDbContextFactory
    {
        private readonly Dictionary<Guid, string> _names = new();
        private readonly List<IDisposable> _opened;

        public FakeTenantDbContextFactory(List<IDisposable> opened) { _opened = opened; }

        public TenantDbContext Register(Guid tenantId)
        {
            var name = $"tenant-{tenantId:N}";
            _names[tenantId] = name;
            return OpenContext(name);
        }

        public ValueTask<TenantDbContext> CreateAsync(Guid tenantId, CancellationToken ct = default)
        {
            if (!_names.TryGetValue(tenantId, out var name))
                throw new InvalidOperationException($"Tenant {tenantId} not reachable.");
            return new ValueTask<TenantDbContext>(OpenContext(name));
        }

        private TenantDbContext OpenContext(string name)
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

    /// <summary>
    /// InMemory-friendly variant of <see cref="TenantDbContext"/>. Drops
    /// the mentorship aggregate (jsonb + rowversion columns the InMemory
    /// provider rejects) — the Story 28-10 rollup only reads
    /// <c>workflow_instances</c> and <c>domain_events</c>, so mentorship
    /// entities are irrelevant to the test.
    /// </summary>
    private sealed class InMemoryFriendlyTenantDbContext : TenantDbContext
    {
        public InMemoryFriendlyTenantDbContext(DbContextOptions<TenantDbContext> options)
            : base(options)
        {
        }

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
