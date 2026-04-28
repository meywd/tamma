using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Tamma.Activities.Analytics;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Epic28;

/// <summary>
/// Story 28-10 — integration-style tests for
/// <see cref="ComputePlatformRollupActivity"/>. Uses the pure-DI
/// <c>ComputeAsync</c> helper to exercise the upsert path without
/// running an Elsa workflow.
/// </summary>
[TestFixture]
public class ComputePlatformRollupActivityTests
{
    private static readonly DateTime Hour =
        new(2026, 04, 18, 12, 00, 00, DateTimeKind.Utc);

    private IDbContextFactory<ControlPlaneDbContext> _cpFactory = null!;
    private Mock<IPlatformEventPublisher> _publisher = null!;
    private List<ControlPlaneDbContext> _openedContexts = null!;

    [SetUp]
    public void SetUp()
    {
        var dbName = $"cp-plat-{Guid.NewGuid()}";
        _openedContexts = new List<ControlPlaneDbContext>();
        _cpFactory = new InMemoryCpFactory(dbName, _openedContexts);
        _publisher = new Mock<IPlatformEventPublisher>(MockBehavior.Strict);
        _publisher
            .Setup(p => p.AppendAndPublishAsync(It.IsAny<PlatformEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlatformEvent evt, CancellationToken _) => evt);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var ctx in _openedContexts)
            ctx.Dispose();
    }

    [Test]
    public async Task ComputeAsync_InsertsPlatformWideRow_OnFirstRun()
    {
        using var seedCtx = _cpFactory.CreateDbContext();
        seedCtx.Tenants.AddRange(
            new Tenant { Id = Guid.NewGuid(), Name = "a", Slug = "a", Type = "personal",
                         Plan = "free", CreatedAt = Hour.AddDays(-1), UpdatedAt = Hour },
            new Tenant { Id = Guid.NewGuid(), Name = "b", Slug = "b", Type = "personal",
                         Plan = "free", CreatedAt = Hour.AddDays(-1), UpdatedAt = Hour });
        seedCtx.PlatformEvents.AddRange(
            new PlatformEvent { Id = Guid.NewGuid(), Type = "AGENT.DISPATCH.SUCCESS",
                                CreatedAt = Hour.AddMinutes(15), Tags = "{}", Metadata = "{}", Data = "{}" },
            new PlatformEvent { Id = Guid.NewGuid(), Type = "AGENT.DISPATCH.FAILED",
                                CreatedAt = Hour.AddMinutes(45), Tags = "{}", Metadata = "{}", Data = "{}" },
            // Out-of-window event — must not be counted.
            new PlatformEvent { Id = Guid.NewGuid(), Type = "AGENT.DISPATCH.SUCCESS",
                                CreatedAt = Hour.AddHours(-2), Tags = "{}", Metadata = "{}", Data = "{}" });
        await seedCtx.SaveChangesAsync();

        await ComputePlatformRollupActivity.ComputeAsync(
            _cpFactory, _publisher.Object, Hour, logger: null, CancellationToken.None);

        using var readCtx = _cpFactory.CreateDbContext();
        var row = await readCtx.PlatformAnalyticsHourly
            .SingleAsync(r => r.Hour == Hour && r.TenantId == null);

        row.AgentDispatches.Should().Be(2);
        row.ActiveTenantsAtHourEnd.Should().Be(2);
        row.ComputedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(10));

        _publisher.Verify(
            p => p.AppendAndPublishAsync(
                It.Is<PlatformEvent>(e => e.Type == AnalyticsRollupEvents.PlatformRollupCompleted),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task ComputeAsync_Idempotent_ReplayUpdatesInPlace()
    {
        // First run with no events — expect zero counts.
        await ComputePlatformRollupActivity.ComputeAsync(
            _cpFactory, _publisher.Object, Hour, logger: null, CancellationToken.None);

        // Add events and re-run — row should update, not duplicate.
        using (var ctx = _cpFactory.CreateDbContext())
        {
            ctx.PlatformEvents.Add(new PlatformEvent
            {
                Id = Guid.NewGuid(),
                Type = "AGENT.DISPATCH.SUCCESS",
                CreatedAt = Hour.AddMinutes(5),
                Tags = "{}",
                Metadata = "{}",
                Data = "{}",
            });
            await ctx.SaveChangesAsync();
        }

        await ComputePlatformRollupActivity.ComputeAsync(
            _cpFactory, _publisher.Object, Hour, logger: null, CancellationToken.None);

        using var readCtx = _cpFactory.CreateDbContext();
        var rows = await readCtx.PlatformAnalyticsHourly
            .Where(r => r.Hour == Hour && r.TenantId == null)
            .ToListAsync();

        rows.Should().ContainSingle("replay must upsert, not duplicate");
        rows[0].AgentDispatches.Should().Be(1);
    }

    [Test]
    public async Task ComputeAsync_DoesNotCountSoftDeletedTenants()
    {
        using var seedCtx = _cpFactory.CreateDbContext();
        seedCtx.Tenants.AddRange(
            new Tenant { Id = Guid.NewGuid(), Name = "a", Slug = "a", Type = "personal",
                         Plan = "free", CreatedAt = Hour.AddDays(-1), UpdatedAt = Hour },
            new Tenant { Id = Guid.NewGuid(), Name = "b", Slug = "b", Type = "personal",
                         Plan = "free", CreatedAt = Hour.AddDays(-1), UpdatedAt = Hour,
                         DeletedAt = Hour.AddMinutes(-5) });
        await seedCtx.SaveChangesAsync();

        await ComputePlatformRollupActivity.ComputeAsync(
            _cpFactory, _publisher.Object, Hour, logger: null, CancellationToken.None);

        using var readCtx = _cpFactory.CreateDbContext();
        var row = await readCtx.PlatformAnalyticsHourly
            .SingleAsync(r => r.Hour == Hour && r.TenantId == null);

        row.ActiveTenantsAtHourEnd.Should().Be(1, "soft-deleted tenants are excluded");
    }

    [Test]
    public async Task ComputeAsync_TruncatesInputHour()
    {
        var offHourInput = Hour.AddMinutes(33).AddSeconds(17);

        await ComputePlatformRollupActivity.ComputeAsync(
            _cpFactory, _publisher.Object, offHourInput, logger: null, CancellationToken.None);

        using var readCtx = _cpFactory.CreateDbContext();
        var row = await readCtx.PlatformAnalyticsHourly
            .SingleAsync(r => r.TenantId == null);

        row.Hour.Should().Be(Hour, "off-hour input must be truncated to top-of-hour");
    }

    private sealed class InMemoryCpFactory : IDbContextFactory<ControlPlaneDbContext>
    {
        private readonly string _dbName;
        private readonly List<ControlPlaneDbContext> _opened;

        public InMemoryCpFactory(string dbName, List<ControlPlaneDbContext> opened)
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

        public Task<ControlPlaneDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
