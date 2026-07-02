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
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Analytics;

/// <summary>
/// Story 36-3 — endpoint-level tests for the tenant usage analytics API:
/// request validation (400s), member-read (no owner/admin gate), the
/// CP-untouched invariant (AC11), and the cross-tenant 403 + no-leakage guard
/// (AC4/AC12). No Postgres — validation short-circuits before the service, the
/// service is mocked for happy paths, and the cross-tenant guard is proven by
/// driving <see cref="RequireTenantMembershipFilter"/> directly (its 403 stops
/// the handler from ever reading the other tenant's schema).
/// </summary>
[TestFixture]
public class TenantAnalyticsEndpointsTests
{
    private static readonly DateTime From = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ServiceProvider MinimalServices() =>
        new ServiceCollection().AddLogging().BuildServiceProvider();

    private static async Task<int> Status(IResult result)
    {
        var ctx = new DefaultHttpContext { RequestServices = MinimalServices() };
        ctx.Response.Body = new MemoryStream();
        await result.ExecuteAsync(ctx);
        return ctx.Response.StatusCode;
    }

    // ─────────────────────────── GetUsage validation ───────────────────────────

    [Test]
    public async Task GetUsage_UnknownGroupBy_Returns400()
    {
        var svc = new Mock<ITenantAnalyticsService>(MockBehavior.Strict);
        var result = await TenantAnalyticsEndpoints.GetUsage(
            Guid.NewGuid(), From, To, "day", "team", svc.Object, NullLoggerFactory.Instance, default);

        (await Status(result)).Should().Be(StatusCodes.Status400BadRequest);
        svc.Verify(s => s.GetUsageAsync(It.IsAny<Guid>(), It.IsAny<AnalyticsWindow>(),
            It.IsAny<AnalyticsDimension?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task GetUsage_UnknownGranularity_Returns400()
    {
        var svc = new Mock<ITenantAnalyticsService>(MockBehavior.Strict);
        var result = await TenantAnalyticsEndpoints.GetUsage(
            Guid.NewGuid(), From, To, "weekly", null, svc.Object, NullLoggerFactory.Instance, default);

        (await Status(result)).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task GetUsage_HourGranularityOverCap_Returns400()
    {
        var svc = new Mock<ITenantAnalyticsService>(MockBehavior.Strict);
        var result = await TenantAnalyticsEndpoints.GetUsage(
            Guid.NewGuid(), To.AddDays(-90), To, "hour", null, svc.Object, NullLoggerFactory.Instance, default);

        (await Status(result)).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task GetUsage_HappyPath_MemberReadable_Returns200()
    {
        var tenantId = Guid.NewGuid();
        var svc = new Mock<ITenantAnalyticsService>();
        svc.Setup(s => s.GetUsageAsync(tenantId, It.IsAny<AnalyticsWindow>(),
                It.IsAny<AnalyticsDimension?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UsageResponse(tenantId, From, To, "day", "provider", Array.Empty<UsageBucketRow>()));

        // No role argument exists on the handler — a plain authenticated member
        // read reaches the service (the only RBAC is MemberAccess + the
        // membership filter at the wiring site; NO owner/admin gate — AC5).
        var result = await TenantAnalyticsEndpoints.GetUsage(
            tenantId, From, To, "day", "provider", svc.Object, NullLoggerFactory.Instance, default);

        (await Status(result)).Should().Be(StatusCodes.Status200OK);
    }

    // ───────────────────────── GetBreakdown validation ─────────────────────────

    [Test]
    public async Task GetBreakdown_MissingDimension_Returns400()
    {
        var svc = new Mock<ITenantAnalyticsService>(MockBehavior.Strict);
        var result = await TenantAnalyticsEndpoints.GetBreakdown(
            Guid.NewGuid(), From, To, dimension: null, metric: "tokens", limit: 10,
            svc.Object, NullLoggerFactory.Instance, default);

        (await Status(result)).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task GetBreakdown_UnknownMetric_Returns400()
    {
        var svc = new Mock<ITenantAnalyticsService>(MockBehavior.Strict);
        var result = await TenantAnalyticsEndpoints.GetBreakdown(
            Guid.NewGuid(), From, To, dimension: "agent", metric: "latency", limit: 10,
            svc.Object, NullLoggerFactory.Instance, default);

        (await Status(result)).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task GetBreakdown_ClampsLimitTo100_AndDelegates()
    {
        var tenantId = Guid.NewGuid();
        int? capturedLimit = null;
        var svc = new Mock<ITenantAnalyticsService>();
        svc.Setup(s => s.GetBreakdownAsync(tenantId, It.IsAny<AnalyticsWindow>(),
                It.IsAny<AnalyticsDimension>(), It.IsAny<AnalyticsMetric>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, AnalyticsWindow, AnalyticsDimension, AnalyticsMetric, int, CancellationToken>(
                (_, _, _, _, lim, _) => capturedLimit = lim)
            .ReturnsAsync(new BreakdownResponse(tenantId, From, To, "agent", "tokens", 100, Array.Empty<BreakdownRow>()));

        var result = await TenantAnalyticsEndpoints.GetBreakdown(
            tenantId, From, To, dimension: "agent", metric: "tokens", limit: 9999,
            svc.Object, NullLoggerFactory.Instance, default);

        (await Status(result)).Should().Be(StatusCodes.Status200OK);
        capturedLimit.Should().Be(100, "limit is clamped to 1..100");
    }

    // ─────────────────────── AC11 — control-plane untouched ───────────────────────

    [Test]
    public void TenantAnalyticsService_DependsOnlyOnTenantFactory_NeverTheControlPlaneAnalyticsSurface()
    {
        var ctorParamTypes = typeof(TenantAnalyticsService)
            .GetConstructors().Single()
            .GetParameters().Select(p => p.ParameterType).ToList();

        ctorParamTypes.Should().Contain(typeof(ITenantDbContextFactory),
            "the read seam is the per-tenant factory (the physical isolation plane)");
        ctorParamTypes.Should().NotContain(typeof(IPlatformAnalyticsService),
            "AC11 — this tenant surface must never read the control-plane analytics service");
        ctorParamTypes.Should().NotContain(typeof(ControlPlaneDbContext),
            "AC11 — this tenant surface must never read the control-plane DbContext");
    }

    // ──────────────────── AC4/AC12 — cross-tenant 403, no leakage ────────────────────

    [Test]
    public async Task CrossTenantRoute_NonMember_Returns403_AndTheAnalyticsHandlerNeverRuns()
    {
        var memberOfA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        // The caller is NOT a member of tenant B (the route tenant).
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
            handlerRan = true; // would be the analytics handler reading tenant B's schema
            return ValueTask.FromResult<object?>(Results.Ok());
        };

        var result = await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(ctx), next);
        if (result is IResult r)
        {
            await r.ExecuteAsync(ctx);
        }

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        handlerRan.Should().BeFalse(
            "the membership filter 403s before the handler → no tenant-B fact row is ever returned (AC4/AC12)");
    }
}
