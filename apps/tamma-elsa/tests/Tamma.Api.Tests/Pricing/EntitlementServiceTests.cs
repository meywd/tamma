using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Pricing;
using Tamma.Api.Services.PromptStore;
using Tamma.Api.Tests.TestDoubles;
using Tamma.Core;
using Tamma.Core.Enums;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Pricing;

/// <summary>
/// Story 34-6 (AC1-3, AC7, AC10, AC13) — core resolution: per-mode principal,
/// complete closed map + default backfill, unlimited, custom-plan override,
/// fail-loud on no assignment / catalog-unavailable (never an empty set), and
/// cache hit/miss + event emission. All sibling contracts (34-1 catalog, 34-4
/// interim assignment source) are mocked.
/// </summary>
[TestFixture]
public class EntitlementServiceTests
{
    private sealed class FakeMode : ITammaModeProvider
    {
        public TammaMode Mode { get; init; }
    }

    private static PlanSnapshot Snapshot(
        Guid planId, int version, bool isCustom, params PlanEntitlementView[] ents) =>
        new(planId, "team", "Team", version, "active", isCustom, "month", null,
            Array.Empty<PlanFeatureView>(), ents, Array.Empty<PlanPriceView>());

    private static PlanEntitlementView[] AllSeven() =>
        EntitlementDefaults.AllMetrics
            .Select((m, i) => new PlanEntitlementView(m, 10 + i, "monthly", "block"))
            .ToArray();

    private static EntitlementService Build(
        TammaMode mode,
        Mock<IActivePlanAssignmentSource> assignments,
        Mock<IPlanCatalogService> catalog,
        RecordingPlatformEventPublisher events,
        EntitlementSnapshotCache cache,
        Mock<IUserRepository>? users = null)
    {
        users ??= new Mock<IUserRepository>();
        return new EntitlementService(
            assignments.Object,
            catalog.Object,
            cache,
            new FakeMode { Mode = mode },
            users.Object,
            events,
            NullLogger<EntitlementService>.Instance);
    }

    private static EntitlementSnapshotCache NewCache() =>
        new(new PricingTestClock());

    [Test]
    public async Task Resolve_SaaS_ByTenantId_ReturnsCompleteMap_EmitsSuccess()
    {
        var tenant = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var assignments = new Mock<IActivePlanAssignmentSource>();
        assignments.Setup(a => a.GetActiveAsync(tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivePlanAssignment(planId, null));
        var catalog = new Mock<IPlanCatalogService>();
        catalog.Setup(c => c.GetByIdAsync(planId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Snapshot(planId, 3, false, AllSeven()));
        var events = new RecordingPlatformEventPublisher();

        var svc = Build(TammaMode.SaaS, assignments, catalog, events, NewCache());

        var resolved = await svc.ResolveAsync(EntitlementPrincipal.ForTenant(tenant));

        resolved.TenantId.Should().Be(tenant);
        resolved.PlanId.Should().Be(planId);
        resolved.PlanVersion.Should().Be(3);
        resolved.Limits.Should().HaveCount(EntitlementDefaults.AllMetrics.Count);
        foreach (var m in EntitlementDefaults.AllMetrics)
        {
            resolved.Limits.Should().ContainKey(m);
        }

        events.Events.Should().ContainSingle(e => e.Type == EntitlementEventTypes.ResolvedSuccess);
        var evt = events.Events.Single(e => e.Type == EntitlementEventTypes.ResolvedSuccess);
        evt.Tags.Should().Contain("cache-miss");
        evt.TenantId.Should().Be(tenant);
    }

    [Test]
    public async Task Resolve_SingleUser_ByUserId_ResolvesPersonalTenant()
    {
        var user = Guid.NewGuid();
        var tenant = Guid.NewGuid();
        var planId = Guid.NewGuid();

        var users = new Mock<IUserRepository>();
        users.Setup(u => u.GetByIdAsync(user)).ReturnsAsync(new User { Id = user, TenantId = tenant });

        var assignments = new Mock<IActivePlanAssignmentSource>();
        assignments.Setup(a => a.GetActiveAsync(tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivePlanAssignment(planId, null));
        var catalog = new Mock<IPlanCatalogService>();
        catalog.Setup(c => c.GetByIdAsync(planId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Snapshot(planId, 1, false, AllSeven()));

        var svc = Build(TammaMode.SingleUser, assignments, catalog,
            new RecordingPlatformEventPublisher(), NewCache(), users);

        var resolved = await svc.ResolveAsync(EntitlementPrincipal.ForUser(user));

        resolved.TenantId.Should().Be(tenant);
        assignments.Verify(a => a.GetActiveAsync(tenant, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Resolve_CompleteSevenKeys_BackfillsMissingWithDocumentedDefault()
    {
        var tenant = Guid.NewGuid();
        var planId = Guid.NewGuid();
        // Only 3 of 7 rows present.
        var present = new[]
        {
            new PlanEntitlementView(EntitlementMetricKey.Seats, 5, "monthly", "block"),
            new PlanEntitlementView(EntitlementMetricKey.Agents, 3, "total", "allow"),
            new PlanEntitlementView(EntitlementMetricKey.LlmTokens, null, "monthly", "meter"),
        };
        var assignments = new Mock<IActivePlanAssignmentSource>();
        assignments.Setup(a => a.GetActiveAsync(tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivePlanAssignment(planId, null));
        var catalog = new Mock<IPlanCatalogService>();
        catalog.Setup(c => c.GetByIdAsync(planId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Snapshot(planId, 1, false, present));

        var svc = Build(TammaMode.SaaS, assignments, catalog,
            new RecordingPlatformEventPublisher(), NewCache());

        var resolved = await svc.ResolveAsync(EntitlementPrincipal.ForTenant(tenant));

        resolved.Limits.Should().HaveCount(EntitlementDefaults.AllMetrics.Count);
        // Present rows win verbatim.
        resolved.Get(EntitlementMetricKey.Seats).LimitValue.Should().Be(5);
        resolved.Get(EntitlementMetricKey.Agents).Period.Should().Be("total");
        // Missing rows backfill the documented default (0, monthly, block).
        var repos = resolved.Get(EntitlementMetricKey.Repos);
        repos.LimitValue.Should().Be(EntitlementDefaults.DefaultLimit);
        repos.Period.Should().Be(EntitlementDefaults.DefaultPeriod);
        repos.OverageMode.Should().Be(EntitlementDefaults.DefaultOverageMode);
    }

    [Test]
    public async Task Resolve_Unlimited_NullLimit_FlowsThroughHeadroom()
    {
        var tenant = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var ents = new[]
        {
            new PlanEntitlementView(EntitlementMetricKey.LlmTokens, null, "monthly", "meter"),
        };
        var assignments = new Mock<IActivePlanAssignmentSource>();
        assignments.Setup(a => a.GetActiveAsync(tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivePlanAssignment(planId, null));
        var catalog = new Mock<IPlanCatalogService>();
        catalog.Setup(c => c.GetByIdAsync(planId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Snapshot(planId, 1, false, ents));

        var svc = Build(TammaMode.SaaS, assignments, catalog,
            new RecordingPlatformEventPublisher(), NewCache());

        var resolved = await svc.ResolveAsync(EntitlementPrincipal.ForTenant(tenant));

        resolved.Get(EntitlementMetricKey.LlmTokens).LimitValue.Should().BeNull();
        var h = svc.CheckHeadroom(resolved, EntitlementMetricKey.LlmTokens, currentUsage: 9_999_999);
        h.Remaining.Should().BeNull();
        h.IsOver.Should().BeFalse();
    }

    [Test]
    public void Resolve_NoAssignment_FailsLoud_EmitsFailed_NeverEmptySet()
    {
        var tenant = Guid.NewGuid();
        var assignments = new Mock<IActivePlanAssignmentSource>();
        assignments.Setup(a => a.GetActiveAsync(tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ActivePlanAssignment?)null);
        var catalog = new Mock<IPlanCatalogService>();
        var events = new RecordingPlatformEventPublisher();
        var cache = NewCache();

        var svc = Build(TammaMode.SaaS, assignments, catalog, events, cache);

        var act = async () => await svc.ResolveAsync(EntitlementPrincipal.ForTenant(tenant));

        var err = act.Should().ThrowAsync<TammaError>().Result;
        err.Which.Code.Should().Be("ENTITLEMENT.RESOLVE.NO_ASSIGNMENT");
        err.Which.Severity.Should().Be(TammaErrorSeverity.High);

        events.Events.Should().ContainSingle(e => e.Type == EntitlementEventTypes.ResolvedFailed);
        events.Events.Single(e => e.Type == EntitlementEventTypes.ResolvedFailed)
            .Tags.Should().Contain("no_assignment");
        cache.Count.Should().Be(0, "a failed resolve never caches an empty set");
        catalog.Verify(c => c.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public void Resolve_CatalogUnavailable_FailsLoud_EmitsFailed()
    {
        var tenant = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var assignments = new Mock<IActivePlanAssignmentSource>();
        assignments.Setup(a => a.GetActiveAsync(tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivePlanAssignment(planId, null));
        var catalog = new Mock<IPlanCatalogService>();
        catalog.Setup(c => c.GetByIdAsync(planId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlanSnapshot?)null);
        var events = new RecordingPlatformEventPublisher();

        var svc = Build(TammaMode.SaaS, assignments, catalog, events, NewCache());

        var act = async () => await svc.ResolveAsync(EntitlementPrincipal.ForTenant(tenant));

        act.Should().ThrowAsync<TammaError>()
            .Result.Which.Code.Should().Be("ENTITLEMENT.RESOLVE.CATALOG_UNAVAILABLE");
        events.Events.Single(e => e.Type == EntitlementEventTypes.ResolvedFailed)
            .Tags.Should().Contain("catalog_unavailable");
    }

    [Test]
    public async Task Resolve_CustomPlan_Overrides_PublicDefaults()
    {
        var tenant = Guid.NewGuid();
        var planId = Guid.NewGuid();
        // Bespoke enterprise plan: unlimited agents, huge seats.
        var ents = new[]
        {
            new PlanEntitlementView(EntitlementMetricKey.Agents, null, "monthly", "allow"),
            new PlanEntitlementView(EntitlementMetricKey.Seats, 5000, "monthly", "allow"),
        };
        var assignments = new Mock<IActivePlanAssignmentSource>();
        assignments.Setup(a => a.GetActiveAsync(tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivePlanAssignment(planId, null));
        var catalog = new Mock<IPlanCatalogService>();
        catalog.Setup(c => c.GetByIdAsync(planId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Snapshot(planId, 2, isCustom: true, ents));

        var svc = Build(TammaMode.SaaS, assignments, catalog,
            new RecordingPlatformEventPublisher(), NewCache());

        var resolved = await svc.ResolveAsync(EntitlementPrincipal.ForTenant(tenant));

        resolved.IsCustom.Should().BeTrue();
        resolved.Get(EntitlementMetricKey.Agents).LimitValue.Should().BeNull("custom unlimited");
        resolved.Get(EntitlementMetricKey.Seats).LimitValue.Should().Be(5000);
    }

    [Test]
    public async Task Resolve_CacheHit_SkipsReads_AndEmitsNoSecondEvent()
    {
        var tenant = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var assignments = new Mock<IActivePlanAssignmentSource>();
        assignments.Setup(a => a.GetActiveAsync(tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivePlanAssignment(planId, null));
        var catalog = new Mock<IPlanCatalogService>();
        catalog.Setup(c => c.GetByIdAsync(planId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Snapshot(planId, 1, false, AllSeven()));
        var events = new RecordingPlatformEventPublisher();

        var svc = Build(TammaMode.SaaS, assignments, catalog, events, NewCache());

        await svc.ResolveAsync(EntitlementPrincipal.ForTenant(tenant));
        await svc.ResolveAsync(EntitlementPrincipal.ForTenant(tenant));

        assignments.Verify(a => a.GetActiveAsync(tenant, It.IsAny<CancellationToken>()), Times.Once);
        catalog.Verify(c => c.GetByIdAsync(planId, It.IsAny<CancellationToken>()), Times.Once);
        events.Events.Count(e => e.Type == EntitlementEventTypes.ResolvedSuccess).Should().Be(1);
    }

    [Test]
    public void Resolve_SingleUser_NoActiveTenant_FailsLoud()
    {
        var user = Guid.NewGuid();
        var users = new Mock<IUserRepository>();
        users.Setup(u => u.GetByIdAsync(user)).ReturnsAsync(new User { Id = user, TenantId = null });

        var svc = Build(TammaMode.SingleUser,
            new Mock<IActivePlanAssignmentSource>(), new Mock<IPlanCatalogService>(),
            new RecordingPlatformEventPublisher(), NewCache(), users);

        var act = async () => await svc.ResolveAsync(EntitlementPrincipal.ForUser(user));
        act.Should().ThrowAsync<TammaError>()
            .Result.Which.Code.Should().Be("ENTITLEMENT.RESOLVE.NO_ASSIGNMENT");
    }
}
