using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.Diagnostics;
using Tamma.Api.Services.Diagnostics.Models;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using BudgetConfigModel = Tamma.Api.Services.Diagnostics.Models.BudgetConfig;

namespace Tamma.Api.Tests.Epic28;

/// <summary>
/// Story 28-1 PR C — proves cross-tenant admin queries are routed per
/// Decision #2 (see <c>.dev/decisions/story-28-1-design-calls.md</c>):
///
/// <list type="bullet">
///   <item><b>Per-tenant queries</b> route through
///     <see cref="ITenantDbContextFactory"/> instead of a direct
///     <see cref="ControlPlaneDbContext"/> scan, so the call site stays
///     correct after Story 28-1 PR D moves the entities off CP.</item>
///   <item><b>Tenant-less queries</b> against
///     <see cref="IEventRepository"/> read from
///     <see cref="IPlatformEventRepository"/> (CP-resident
///     <c>platform_events</c>) for platform-lifecycle events.</item>
///   <item><b>Cross-tenant tenant-scoped queries</b> that have no
///     current user story are deferred with
///     <see cref="NotSupportedException"/> rather than silently
///     returning rows from the wrong table.</item>
/// </list>
/// </summary>
[TestFixture]
public class Story28_1_PrC_CrossTenantQueryRoutingTests
{
    // ── EventRepository.QueryAsync(tenantId, ...) per-tenant routing ─

    [Test]
    public async Task EventRepository_QueryAsync_WithTenantId_RoutesViaFactory()
    {
        await using var fx = new InMemoryDbFixture();
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();

        // Spy on the factory to assert it's called with the right tenant id.
        var factory = new SpyTenantDbContextFactory(fx.TenantOptions);
        var repo = new EventRepository(
            factory,
            new TenantContext(),
            new PlatformEventRepository(fx.Cp));

        // Seed two events, one per tenant. Story 28-1 PR D — domain_events
        // live on the tenant DB, so seeding goes through the factory.
        await using (var tdb1 = await fx.Factory.CreateAsync(tenantId))
        {
            tdb1.DomainEvents.Add(new DomainEvent
            {
                Id = Guid.NewGuid(),
                Type = "WORKFLOW.STARTED.SUCCESS",
                TenantId = tenantId,
                CreatedAt = DateTime.UtcNow,
            });
            await tdb1.SaveChangesAsync();
        }
        await using (var tdb2 = await fx.Factory.CreateAsync(otherTenantId))
        {
            tdb2.DomainEvents.Add(new DomainEvent
            {
                Id = Guid.NewGuid(),
                Type = "WORKFLOW.STARTED.SUCCESS",
                TenantId = otherTenantId,
                CreatedAt = DateTime.UtcNow,
            });
            await tdb2.SaveChangesAsync();
        }

        var rows = await repo.QueryAsync(tenantId, null, null, 50);

        // Factory was called with the requested tenant id.
        factory.Calls.Should().ContainSingle()
            .Which.Should().Be(tenantId);
        // Only the queried tenant's row came back.
        rows.Should().ContainSingle()
            .Which.TenantId.Should().Be(tenantId);
    }

    // ── EventRepository.QueryAsync(null, type, ...) → platform_events ─

    [Test]
    public async Task EventRepository_QueryAsync_WithNullTenantAndType_ReadsPlatformEvents()
    {
        await using var fx = new InMemoryDbFixture();
        var repo = new EventRepository(
            fx.Factory,
            new TenantContext(),
            new PlatformEventRepository(fx.Cp));

        // Seed a platform-lifecycle event into platform_events directly
        // (mirrors how PlatformEventPublisher writes lifecycle events).
        fx.Cp.PlatformEvents.Add(new PlatformEvent
        {
            Id = Guid.NewGuid(),
            Type = "TENANT.PROVISIONED.SUCCESS",
            TenantId = Guid.NewGuid(),
            Tags = "{}",
            Metadata = "{}",
            Data = "{}",
            CreatedAt = DateTime.UtcNow,
        });
        await fx.Cp.SaveChangesAsync();

        var rows = await repo.QueryAsync(
            tenantId: null,
            type: "TENANT.PROVISIONED.SUCCESS",
            issueNumber: null,
            limit: 10);

        rows.Should().ContainSingle()
            .Which.Type.Should().Be("TENANT.PROVISIONED.SUCCESS");
    }

    [Test]
    [Ignore("Story 28-1 PR D — cp.domain_events is gone; the transitional "
        + "UNION (legacy cp.DomainEvents + platform_events) was the PR-C "
        + "compat shim and was removed when PR D landed. Tenant-less "
        + "EventRepository.QueryAsync now reads ONLY platform_events. The "
        + "union test is no longer the contract; "
        + "EventRepository_QueryAsync_WithNullTenantAndType_ReadsPlatformEvents "
        + "covers the post-D behaviour.")]
    public async Task EventRepository_QueryAsync_WithNullTenantAndType_UnionsLegacyDomainEvents()
    {
        await using var fx = new InMemoryDbFixture();
        var repo = new EventRepository(
            fx.Factory,
            new TenantContext(),
            new PlatformEventRepository(fx.Cp));

        fx.Cp.PlatformEvents.Add(new PlatformEvent
        {
            Id = Guid.NewGuid(),
            Type = "EMAIL.QUEUED.SUCCESS",
            TenantId = null,
            Tags = "{}",
            Metadata = "{}",
            Data = "{}",
            CreatedAt = DateTime.UtcNow.AddSeconds(1),
        });
        await fx.Cp.SaveChangesAsync();

        var rows = await repo.QueryAsync(null, "EMAIL.QUEUED.SUCCESS", null, 10);

        rows.Should().HaveCount(2);
    }

    [Test]
    public async Task EventRepository_QueryAsync_WithNullTenantAndIssueNumber_DefersWithNotSupported()
    {
        // issueNumber is a tenant-scoped predicate (DomainEvent column),
        // so this combination signals "I forgot the tenant scope". Decision
        // #2 says defer with a loud exception rather than scan cross-tenant.
        await using var fx = new InMemoryDbFixture();
        var repo = new EventRepository(
            fx.Factory,
            new TenantContext(),
            new PlatformEventRepository(fx.Cp));

        var act = async () =>
            await repo.QueryAsync(tenantId: null, type: null, issueNumber: 42, limit: 10);

        var ex = await act.Should().ThrowAsync<NotSupportedException>();
        ex.WithMessage("*Cross-tenant tenant-scoped event search is not implemented*");
        ex.WithMessage("*tenant-scoped predicate*");
    }

    [Test]
    public async Task EventRepository_QueryAsync_WithNullTenantAndNoFilters_ReturnsPlatformEvents()
    {
        // Defensive sanity check: the no-filters path is the
        // ResendEmailServiceTests "scan all events" variant. After PR D
        // it returns every platform-scope row; today it unions the
        // transitional cp.DomainEvents leftovers + platform_events.
        await using var fx = new InMemoryDbFixture();
        var repo = new EventRepository(
            fx.Factory,
            new TenantContext(),
            new PlatformEventRepository(fx.Cp));

        fx.Cp.PlatformEvents.Add(new PlatformEvent
        {
            Id = Guid.NewGuid(),
            Type = "EMAIL.SENT.SUCCESS",
            TenantId = null,
            Tags = "{}",
            Metadata = "{}",
            Data = "{}",
            CreatedAt = DateTime.UtcNow,
        });
        await fx.Cp.SaveChangesAsync();

        var rows = await repo.QueryAsync(null, null, null, 20);
        rows.Should().HaveCount(1);
    }

    // ── UserDashboardEndpoints.GetOrgSummary per-tenant DB factory ────

    [Test]
    public async Task UserDashboard_GetOrgSummary_RoutesViaTenantDbContextFactory()
    {
        // The previous PR-B-and-before signature took a ControlPlaneDbContext
        // and counted rows directly off cp.DomainEvents / cp.WorkflowInstances.
        // PR C swaps that for an ITenantDbContextFactory route so the same
        // query lands on the tenant DB once PR D moves the entities. This
        // test asserts the factory IS the entry point — failing if anyone
        // re-introduces a direct cp.* scan.
        await using var fx = new InMemoryDbFixture();
        var tenantId = Guid.NewGuid();
        var foreignTenantId = Guid.NewGuid();

        // Story 28-1 PR D — domain_events + workflow_instances live on the
        // tenant DB. Seed via the factory.
        await using (var tdb = await fx.Factory.CreateAsync(tenantId))
        {
            tdb.DomainEvents.Add(new DomainEvent
            {
                Id = Guid.NewGuid(),
                Type = "WORKFLOW.STARTED.SUCCESS",
                TenantId = tenantId,
                CreatedAt = DateTime.UtcNow,
            });
            tdb.WorkflowInstances.Add(new WorkflowInstance
            {
                Id = Guid.NewGuid(),
                DefinitionId = Guid.NewGuid(),
                TenantId = tenantId,
                Status = "completed",
            });
            await tdb.SaveChangesAsync();
        }
        await using (var tdbForeign = await fx.Factory.CreateAsync(foreignTenantId))
        {
            tdbForeign.DomainEvents.Add(new DomainEvent
            {
                Id = Guid.NewGuid(),
                Type = "WORKFLOW.STARTED.SUCCESS",
                TenantId = foreignTenantId,
                CreatedAt = DateTime.UtcNow,
            });
            await tdbForeign.SaveChangesAsync();
        }

        var spyFactory = new SpyTenantDbContextFactory(fx.TenantOptions);
        // Story 28-1 PR D — WorkflowRepository.ListDefinitionsAsync now
        // requires an ambient tenant id. Pin TenantContext.TenantId to
        // the test tenant so the endpoint resolves correctly.
        var tenantContext = new TenantContext();
        tenantContext.SetTenantId(tenantId);
        var eventRepo = new EventRepository(
            spyFactory,
            tenantContext,
            new PlatformEventRepository(fx.Cp));
        var workflowRepo = new WorkflowRepository(spyFactory, tenantContext);

        var result = await UserDashboardEndpoints.GetOrgSummary(
            tenantId, spyFactory, eventRepo, workflowRepo);

        result.Should().NotBeNull();

        // The factory was opened at least twice in GetOrgSummary —
        // once for the totalEvents/totalWorkflows count block, and at
        // least once more by eventRepo.QueryAsync(tenantId, …). Every
        // call MUST be against `tenantId`.
        spyFactory.Calls.Should().NotBeEmpty();
        spyFactory.Calls.Should().OnlyContain(t => t == tenantId);
    }

    // ── DiagnosticsService.GetDimensionReportAsync — null-tenant defer ─

    [Test]
    public async Task DiagnosticsService_GetDimensionReportAsync_NullTenant_DefersWithNotSupported()
    {
        await using var fx = new InMemoryDbFixture();
        var services = new ServiceCollection();
        services.AddSingleton<ControlPlaneDbContext>(fx.Cp);
        services.AddSingleton<ITenantDbContextFactory>(fx.Factory);
        var sp = services.BuildServiceProvider();

        var service = new DiagnosticsService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new StubBudgetConfigProvider());

        var act = async () => await service.GetDimensionReportAsync(
            tenantId: null,
            from: DateTime.UtcNow.AddDays(-1),
            to: DateTime.UtcNow,
            groupBy: DimensionGroup.Provider);

        var ex = await act.Should().ThrowAsync<NotSupportedException>();
        ex.WithMessage("*Cross-tenant ProviderDiagnostics dimension reports*");
    }

    [Test]
    public async Task DiagnosticsService_GetDimensionReportAsync_PerTenant_RoutesViaFactory()
    {
        await using var fx = new InMemoryDbFixture();
        var tenantId = Guid.NewGuid();

        // Story 28-1 PR D — provider_diagnostics live on the tenant DB.
        await using (var tdb = await fx.Factory.CreateAsync(tenantId))
        {
            tdb.ProviderDiagnostics.Add(new ProviderDiagnostic
            {
                Id = Guid.NewGuid(),
                ProviderKey = "anthropic",
                Model = "claude-3-7-sonnet",
                AgentType = "developer",
                Success = true,
                TenantId = tenantId,
                CreatedAt = DateTime.UtcNow.AddMinutes(-5),
                RequestDurationMs = 100,
                Cost = 0.01m,
                TokensUsed = 100,
            });
            await tdb.SaveChangesAsync();
        }

        var spyFactory = new SpyTenantDbContextFactory(fx.TenantOptions);

        var services = new ServiceCollection();
        services.AddSingleton<ITenantDbContextFactory>(spyFactory);
        var sp = services.BuildServiceProvider();

        var service = new DiagnosticsService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new StubBudgetConfigProvider());

        var report = await service.GetDimensionReportAsync(
            tenantId: tenantId,
            from: DateTime.UtcNow.AddHours(-1),
            to: DateTime.UtcNow.AddHours(1),
            groupBy: DimensionGroup.Provider);

        report.Should().NotBeNull();
        report.GroupBy.Should().Be(DimensionGroup.Provider);
        // Factory was opened with our tenant id, never another.
        spyFactory.Calls.Should().NotBeEmpty();
        spyFactory.Calls.Should().OnlyContain(t => t == tenantId);
    }

    // ── EventRepository optional-platform-repo fallback ─────────────

    [Test]
    [Ignore("Story 28-1 PR D — the legacy cp.DomainEvents fallback path "
        + "this test asserted is gone. Tenant-less queries with no "
        + "IPlatformEventRepository wired now return an empty list, not a "
        + "filtered cp.DomainEvents read (the entity is excluded from the "
        + "CP model graph entirely). The defensive guard against NRE on "
        + "null platformEvents still holds and is exercised by the live "
        + "platform path; this fixture's seed shape (cp.DomainEvents.Add) "
        + "no longer compiles to a runtime model after PR D.")]
    public async Task EventRepository_QueryAsync_NullPlatformRepo_ReturnsLegacyOnly_NoThrow()
    {
        // #340 MEDIUM finding — exercise the optional ctor parameter
        // (IPlatformEventRepository=null) directly. With no platform repo
        // wired, the cross-tenant admin query MUST fall back to the
        // legacy half (cp.DomainEvents filtered to TenantId == null) and
        // MUST NOT throw an NRE on the platform half.
        await using var fx = new InMemoryDbFixture();
        var repo = new EventRepository(
            fx.Factory,
            new TenantContext(),
            platformEvents: null);

        var platformEventId = Guid.NewGuid();
        // Seed a platform-scope row in the legacy CP DomainEvents table.
        fx.Cp.DomainEvents.Add(new DomainEvent
        {
            Id = platformEventId,
            Type = "EMAIL.QUEUED.SUCCESS",
            TenantId = null,
            CreatedAt = DateTime.UtcNow,
        });
        // Seed a tenant-scoped row that should NOT come back from a
        // tenant-less query (the new TenantId == null filter in the
        // legacy half is what guarantees that).
        fx.Cp.DomainEvents.Add(new DomainEvent
        {
            Id = Guid.NewGuid(),
            Type = "EMAIL.QUEUED.SUCCESS",
            TenantId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
        });
        await fx.Cp.SaveChangesAsync();

        // Should return ONLY the platform-scope legacy row, no NRE on
        // the platform half (which is skipped when platformEvents is null).
        var rows = await repo.QueryAsync(
            tenantId: null,
            type: "EMAIL.QUEUED.SUCCESS",
            issueNumber: null,
            limit: 100);

        rows.Should().ContainSingle()
            .Which.Id.Should().Be(platformEventId);
    }

    [Test]
    [Ignore("Story 28-1 PR D — cp.DomainEvents is gone; the legacy half "
        + "this test guarded (filter tenant-scoped rows out of the legacy "
        + "UNION) is no longer reachable. Tenant-scoped rows now live on "
        + "the per-tenant DB and are unreachable from a tenant-less query "
        + "by physical isolation, not by a EF predicate. Coverage of the "
        + "tenant-less query result shape lives in "
        + "EventRepository_QueryAsync_WithNullTenantAndType_ReadsPlatformEvents.")]
    public async Task EventRepository_QueryAsync_LegacyHalf_FiltersTenantScopedEventsOut()
    {
        // #340 HIGH finding — the legacy UNION half MUST filter to
        // TenantId == null. Without that filter, a caller passing a
        // tenant-scoped event type with tenantId=null gets rows from
        // every tenant via the legacy half. This test seeds a platform
        // row + a tenant-scoped row in the SAME physical cp.DomainEvents
        // table (the transitional shared-DB phase) and asserts that the
        // tenant-less query returns ONLY the platform row.
        await using var fx = new InMemoryDbFixture();
        var repo = new EventRepository(
            fx.Factory,
            new TenantContext(),
            new PlatformEventRepository(fx.Cp));

        var platformEventId = Guid.NewGuid();
        var tenantEventId = Guid.NewGuid();
        var leakingTenant = Guid.NewGuid();

        // Use a tenant-scoped event type ("CODE.GENERATED.SUCCESS") to
        // make the leak vector concrete: pre-fix, a caller asking for
        // this type with no tenant filter would see every tenant's
        // generated-code events.
        fx.Cp.DomainEvents.Add(new DomainEvent
        {
            Id = platformEventId,
            Type = "CODE.GENERATED.SUCCESS",
            TenantId = null,
            CreatedAt = DateTime.UtcNow,
        });
        fx.Cp.DomainEvents.Add(new DomainEvent
        {
            Id = tenantEventId,
            Type = "CODE.GENERATED.SUCCESS",
            TenantId = leakingTenant,
            CreatedAt = DateTime.UtcNow,
        });
        await fx.Cp.SaveChangesAsync();

        var rows = await repo.QueryAsync(
            tenantId: null,
            type: "CODE.GENERATED.SUCCESS",
            issueNumber: null,
            limit: 100);

        rows.Should().ContainSingle();
        rows.Should().NotContain(e => e.Id == tenantEventId,
            "tenant-scoped rows must not bleed into cross-tenant admin views");
        rows.Single().Id.Should().Be(platformEventId);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Decorates <see cref="TestTenantDbContextFactory"/> with a recording
    /// list of every tenant id passed to <c>CreateAsync</c>. Tests use the
    /// tape to assert the call site routed per-tenant (vs sneaking in a
    /// direct CP scan).
    /// </summary>
    private sealed class SpyTenantDbContextFactory : ITenantDbContextFactory
    {
        private readonly TestTenantDbContextFactory _inner;
        public List<Guid> Calls { get; } = new();

        public SpyTenantDbContextFactory(DbContextOptions<TenantDbContext> options)
        {
            _inner = new TestTenantDbContextFactory(options);
        }

        public ValueTask<TenantDbContext> CreateAsync(
            Guid tenantId, CancellationToken cancellationToken = default)
        {
            Calls.Add(tenantId);
            return _inner.CreateAsync(tenantId, cancellationToken);
        }
    }

    private sealed class StubBudgetConfigProvider : IBudgetConfigProvider
    {
        public BudgetConfigModel GetConfig(Guid accountId)
            => new(
                LimitUsd: 0m,
                AlertThreshold: 0.8,
                PeriodStart: DateTime.UtcNow.AddDays(-1),
                PeriodEnd: DateTime.UtcNow.AddDays(30));

        public void SetConfig(Guid accountId, BudgetConfigModel config) { }
    }
}
