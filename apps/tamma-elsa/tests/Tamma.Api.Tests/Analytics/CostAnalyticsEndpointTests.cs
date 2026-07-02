using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Authorization;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.Analytics;
using Tamma.Api.Services.PromptStore;
using Tamma.Data.Abstractions;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Analytics;

/// <summary>
/// Story 36-4 — endpoint-level tests for the tenant cost analytics API:
/// validation (400s), member-read (no owner/admin gate — AC8), current-month
/// window defaulting + UTC (AC12), mode threading (AC9), the read-only /
/// no-control-plane invariant (AC10), and the cross-tenant 403 that stops the
/// handler before it can read another tenant's schema (AC8). No Postgres —
/// validation short-circuits before the service, the service is mocked for happy
/// paths, and the membership filter is driven directly.
/// </summary>
[TestFixture]
public class CostAnalyticsEndpointTests
{
    private static readonly DateTime From = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset July15 = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    private static ServiceProvider MinimalServices() =>
        new ServiceCollection().AddLogging().BuildServiceProvider();

    private static async Task<int> Status(IResult result)
    {
        var ctx = new DefaultHttpContext { RequestServices = MinimalServices() };
        ctx.Response.Body = new MemoryStream();
        await result.ExecuteAsync(ctx);
        return ctx.Response.StatusCode;
    }

    private static Mock<ITammaModeProvider> Mode(TammaMode mode)
    {
        var m = new Mock<ITammaModeProvider>();
        m.SetupGet(x => x.Mode).Returns(mode);
        return m;
    }

    private static CostAnalyticsResponse EmptyResponse(Guid tenantId) => new(
        tenantId,
        new CostWindow(new DateOnly(2026, 7, 1), new DateOnly(2026, 8, 1)),
        null,
        Array.Empty<CostSeriesBucket>(),
        new CostSummary(0m, 0m, 0m, null, null, null, null, false),
        new CostTrend(0m, null, 0m, null));

    // ─────────────────────────────── Validation ───────────────────────────────

    [Test]
    public async Task GetCost_UnknownGroupBy_Returns400()
    {
        var svc = new Mock<ICostAnalyticsService>(MockBehavior.Strict);
        var result = await TenantAnalyticsEndpoints.GetCost(
            Guid.NewGuid(), From, To, "team", svc.Object, Mode(TammaMode.SaaS).Object,
            new FixedClock(July15), NullLoggerFactory.Instance, default);

        (await Status(result)).Should().Be(StatusCodes.Status400BadRequest);
        svc.Verify(s => s.GetCostAsync(It.IsAny<Guid>(), It.IsAny<AnalyticsWindow>(),
            It.IsAny<AnalyticsDimension?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task GetCost_WorkflowGroupBy_IsRejected_CostOnlySplitsByProviderOrAgent()
    {
        var svc = new Mock<ICostAnalyticsService>(MockBehavior.Strict);
        var result = await TenantAnalyticsEndpoints.GetCost(
            Guid.NewGuid(), From, To, "workflow", svc.Object, Mode(TammaMode.SaaS).Object,
            new FixedClock(July15), NullLoggerFactory.Instance, default);

        (await Status(result)).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task GetCost_FromAfterTo_Returns400()
    {
        var svc = new Mock<ICostAnalyticsService>(MockBehavior.Strict);
        var result = await TenantAnalyticsEndpoints.GetCost(
            Guid.NewGuid(), To, From, null, svc.Object, Mode(TammaMode.SaaS).Object,
            new FixedClock(July15), NullLoggerFactory.Instance, default);

        (await Status(result)).Should().Be(StatusCodes.Status400BadRequest);
    }

    // ─────────────────────── Defaulting / mode / happy path ───────────────────────

    [Test]
    public async Task GetCost_OmittedWindow_DefaultsToCurrentCalendarMonth_Utc()
    {
        var tenantId = Guid.NewGuid();
        AnalyticsWindow captured = default;
        var svc = new Mock<ICostAnalyticsService>();
        svc.Setup(s => s.GetCostAsync(tenantId, It.IsAny<AnalyticsWindow>(),
                It.IsAny<AnalyticsDimension?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, AnalyticsWindow, AnalyticsDimension?, string, CancellationToken>(
                (_, w, _, _, _) => captured = w)
            .ReturnsAsync(EmptyResponse(tenantId));

        var result = await TenantAnalyticsEndpoints.GetCost(
            tenantId, null, null, null, svc.Object, Mode(TammaMode.SaaS).Object,
            new FixedClock(July15), NullLoggerFactory.Instance, default);

        (await Status(result)).Should().Be(StatusCodes.Status200OK);
        captured.From.Should().Be(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        captured.To.Should().Be(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        captured.From.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Test]
    public async Task GetCost_ThreadsTheProcessModeIntoTheServiceCall()
    {
        var tenantId = Guid.NewGuid();
        string? capturedMode = null;
        var svc = new Mock<ICostAnalyticsService>();
        svc.Setup(s => s.GetCostAsync(tenantId, It.IsAny<AnalyticsWindow>(),
                It.IsAny<AnalyticsDimension?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, AnalyticsWindow, AnalyticsDimension?, string, CancellationToken>(
                (_, _, _, m, _) => capturedMode = m)
            .ReturnsAsync(EmptyResponse(tenantId));

        await TenantAnalyticsEndpoints.GetCost(
            tenantId, From, To, "agent", svc.Object, Mode(TammaMode.SingleUser).Object,
            new FixedClock(July15), NullLoggerFactory.Instance, default);

        capturedMode.Should().Be("single-user");
    }

    [Test]
    public async Task GetCost_HappyPath_MemberReadable_Returns200()
    {
        var tenantId = Guid.NewGuid();
        var svc = new Mock<ICostAnalyticsService>();
        svc.Setup(s => s.GetCostAsync(tenantId, It.IsAny<AnalyticsWindow>(),
                It.IsAny<AnalyticsDimension?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyResponse(tenantId));

        // No role argument on the handler — a plain authenticated member read
        // reaches the service (RBAC is MemberAccess + the membership filter at the
        // wiring site; NO owner/admin gate — AC8).
        var result = await TenantAnalyticsEndpoints.GetCost(
            tenantId, From, To, "provider", svc.Object, Mode(TammaMode.SaaS).Object,
            new FixedClock(July15), NullLoggerFactory.Instance, default);

        (await Status(result)).Should().Be(StatusCodes.Status200OK);
    }

    // ─────────────────── AC10 — read-only, no control-plane / markup dep ───────────────────

    [Test]
    public void CostAnalyticsService_DependsOnlyOnTheTenantReadPlane_NoControlPlaneNoMarkup()
    {
        var ctorParams = typeof(CostAnalyticsService)
            .GetConstructors().Single()
            .GetParameters().Select(p => p.ParameterType).ToList();

        ctorParams.Should().Contain(typeof(ITenantDbContextFactory),
            "the read seam is the per-tenant factory (physical isolation)");
        ctorParams.Should().Contain(typeof(IBudgetConfigRepository), "budget context is read-only");
        ctorParams.Should().Contain(typeof(IEventRepository), "the only write is the budget-exceeded DCB event");
        ctorParams.Should().NotContain(typeof(IPlatformAnalyticsService),
            "AC1/AC10 — the tenant cost surface must never read the control-plane analytics service");
    }

    // ─────────────────────── AC8 — cross-tenant 403, no leakage ───────────────────────

    [Test]
    public async Task CrossTenantRoute_NonMember_Returns403_AndTheCostHandlerNeverRuns()
    {
        var memberOfA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var membership = new Mock<ITenantMembershipRepository>();
        membership.Setup(r => r.GetRoleAsync(tenantB, memberOfA)).ReturnsAsync((string?)null);
        var filter = new RequireTenantMembershipFilter(membership.Object);

        var ctx = new DefaultHttpContext { RequestServices = MinimalServices() };
        ctx.Response.Body = new MemoryStream();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, memberOfA.ToString()) }, "Test"));
        ctx.Request.RouteValues["tenantId"] = tenantB.ToString();

        var handlerRan = false;
        EndpointFilterDelegate next = _ =>
        {
            handlerRan = true; // would be the cost handler reading tenant B's schema
            return ValueTask.FromResult<object?>(Results.Ok());
        };

        var result = await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(ctx), next);
        if (result is IResult r)
        {
            await r.ExecuteAsync(ctx);
        }

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        handlerRan.Should().BeFalse(
            "the membership filter 403s before the handler → no tenant-B cost row is ever returned (AC8)");
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
