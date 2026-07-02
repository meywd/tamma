using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Dtos.Pricing;
using Tamma.Api.Endpoints;
using Tamma.Api.Endpoints.Admin;
using Tamma.Api.Services.Pricing;
using Tamma.Api.Services.PromptStore;
using Tamma.Core;
using Tamma.Core.Enums;
using Tamma.Data;

namespace Tamma.Api.Tests.Pricing;

/// <summary>
/// Story 34-6 (AC4, AC5, AC11, AC13) — the two entitlement read endpoints via
/// direct handler invocation (the project's endpoint-test convention): member
/// self-read (per-mode principal, tenant from ITenantContext / sole user),
/// admin read-by-route, <c>NO_ASSIGNMENT</c> → 404, and tenant isolation (the
/// member route resolves the ambient tenant, never a request param).
/// </summary>
[TestFixture]
public class EntitlementEndpointsTests
{
    private sealed class FakeMode : ITammaModeProvider
    {
        public TammaMode Mode { get; init; }
    }

    private static ResolvedEntitlements ResolvedFor(Guid tenantId, long? seatLimit = 10) =>
        new(tenantId, Guid.NewGuid(), 1, false,
            EntitlementDefaults.AllMetrics.ToDictionary(
                m => m,
                m => new ResolvedEntitlement(
                    m,
                    m == EntitlementMetricKey.Seats ? seatLimit : 100,
                    "monthly", "block")));

    private static (Mock<IEntitlementService> svc, Mock<IEntitlementUsageReader> usage) Mocks(
        Guid tenantId, ResolvedEntitlements? resolved = null)
    {
        var svc = new Mock<IEntitlementService>();
        svc.Setup(s => s.ResolveAsync(It.IsAny<EntitlementPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolved ?? ResolvedFor(tenantId));

        var usage = new Mock<IEntitlementUsageReader>();
        usage.Setup(u => u.GetCurrentAsync(
                tenantId, It.IsAny<Guid?>(), EntitlementMetricKey.Seats, It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);
        usage.Setup(u => u.GetCurrentAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.Is<EntitlementMetricKey>(
                m => m != EntitlementMetricKey.Seats), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long?)null);
        return (svc, usage);
    }

    private static ClaimsPrincipal UserPrincipal(Guid userId)
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "Test");
        return new ClaimsPrincipal(identity);
    }

    private static ResolvedEntitlementsDto DtoOf(IResult result)
    {
        result.Should().BeAssignableTo<IValueHttpResult>();
        return (ResolvedEntitlementsDto)((IValueHttpResult)result).Value!;
    }

    private static int StatusOf(IResult result)
    {
        result.Should().BeAssignableTo<IStatusCodeHttpResult>();
        return ((IStatusCodeHttpResult)result).StatusCode!.Value;
    }

    [Test]
    public async Task Member_SaaS_ReadsOwnTenant_FromContext_WithLiveCounts()
    {
        var tenant = Guid.NewGuid();
        var (svc, usage) = Mocks(tenant);
        var tenantContext = new TenantContext();
        tenantContext.SetTenantId(tenant);

        var result = await PricingEndpoints.GetEntitlements(
            UserPrincipal(Guid.NewGuid()),
            tenantContext,
            new FakeMode { Mode = TammaMode.SaaS },
            svc.Object, usage.Object, NullLoggerFactory.Instance, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        var dto = DtoOf(result);
        dto.TenantId.Should().Be(tenant.ToString());
        dto.Limits.Should().HaveCount(EntitlementDefaults.AllMetrics.Count);
        dto.Limits.Single(l => l.MetricKey == "seats").CurrentUsage.Should().Be(3);
        dto.Limits.Single(l => l.MetricKey == "seats").Remaining.Should().Be(7);

        // Isolation: the tenant is taken from ITenantContext, never a param.
        svc.Verify(s => s.ResolveAsync(
            It.Is<EntitlementPrincipal>(p => p.TenantId == tenant && p.UserId == null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Member_SingleUser_ResolvesByUserPrincipal()
    {
        var user = Guid.NewGuid();
        var tenant = Guid.NewGuid();
        var (svc, usage) = Mocks(tenant);

        var result = await PricingEndpoints.GetEntitlements(
            UserPrincipal(user),
            new TenantContext(),
            new FakeMode { Mode = TammaMode.SingleUser },
            svc.Object, usage.Object, NullLoggerFactory.Instance, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        svc.Verify(s => s.ResolveAsync(
            It.Is<EntitlementPrincipal>(p => p.UserId == user && p.TenantId == null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Member_NoAssignment_Returns404()
    {
        var tenant = Guid.NewGuid();
        var svc = new Mock<IEntitlementService>();
        svc.Setup(s => s.ResolveAsync(It.IsAny<EntitlementPrincipal>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TammaError("ENTITLEMENT.RESOLVE.NO_ASSIGNMENT", "no plan"));
        var tenantContext = new TenantContext();
        tenantContext.SetTenantId(tenant);

        var result = await PricingEndpoints.GetEntitlements(
            UserPrincipal(Guid.NewGuid()),
            tenantContext,
            new FakeMode { Mode = TammaMode.SaaS },
            svc.Object, Mock.Of<IEntitlementUsageReader>(),
            NullLoggerFactory.Instance, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Test]
    public async Task Member_SaaS_NoTenant_Returns404()
    {
        var result = await PricingEndpoints.GetEntitlements(
            UserPrincipal(Guid.NewGuid()),
            new TenantContext(), // no tenant set
            new FakeMode { Mode = TammaMode.SaaS },
            Mock.Of<IEntitlementService>(), Mock.Of<IEntitlementUsageReader>(),
            NullLoggerFactory.Instance, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Test]
    public async Task Admin_ReadsByRoute()
    {
        var tenant = Guid.NewGuid();
        var (svc, usage) = Mocks(tenant);

        var result = await AdminTenantsEndpoints.GetTenantEntitlements(
            tenant, svc.Object, usage.Object, NullLoggerFactory.Instance, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        DtoOf(result).TenantId.Should().Be(tenant.ToString());
        svc.Verify(s => s.ResolveAsync(
            It.Is<EntitlementPrincipal>(p => p.TenantId == tenant),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Admin_UnknownOrNoAssignment_Returns404()
    {
        var svc = new Mock<IEntitlementService>();
        svc.Setup(s => s.ResolveAsync(It.IsAny<EntitlementPrincipal>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TammaError("ENTITLEMENT.RESOLVE.NO_ASSIGNMENT", "no plan"));

        var result = await AdminTenantsEndpoints.GetTenantEntitlements(
            Guid.NewGuid(), svc.Object, Mock.Of<IEntitlementUsageReader>(),
            NullLoggerFactory.Instance, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Test]
    public async Task Member_CatalogUnavailable_Returns503()
    {
        // A pinned plan with no catalog snapshot throws CATALOG_UNAVAILABLE.
        // Without an explicit map it would surface as a bare 500 (no global
        // TammaError→ProblemDetails middleware); it must be a fail-loud 503.
        var tenant = Guid.NewGuid();
        var svc = new Mock<IEntitlementService>();
        svc.Setup(s => s.ResolveAsync(It.IsAny<EntitlementPrincipal>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TammaError(
                "ENTITLEMENT.RESOLVE.CATALOG_UNAVAILABLE", "pinned plan snapshot missing"));
        var tenantContext = new TenantContext();
        tenantContext.SetTenantId(tenant);

        var result = await PricingEndpoints.GetEntitlements(
            UserPrincipal(Guid.NewGuid()),
            tenantContext,
            new FakeMode { Mode = TammaMode.SaaS },
            svc.Object, Mock.Of<IEntitlementUsageReader>(),
            NullLoggerFactory.Instance, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Test]
    public async Task Admin_CatalogUnavailable_Returns503()
    {
        var svc = new Mock<IEntitlementService>();
        svc.Setup(s => s.ResolveAsync(It.IsAny<EntitlementPrincipal>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TammaError(
                "ENTITLEMENT.RESOLVE.CATALOG_UNAVAILABLE", "pinned plan snapshot missing"));

        var result = await AdminTenantsEndpoints.GetTenantEntitlements(
            Guid.NewGuid(), svc.Object, Mock.Of<IEntitlementUsageReader>(),
            NullLoggerFactory.Instance, CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Test]
    public async Task Isolation_TwoTenants_EachSeesOwnSet()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var (svcA, usageA) = Mocks(a, ResolvedFor(a, seatLimit: 5));
        var (svcB, usageB) = Mocks(b, ResolvedFor(b, seatLimit: 50));

        var ctxA = new TenantContext(); ctxA.SetTenantId(a);
        var ctxB = new TenantContext(); ctxB.SetTenantId(b);

        var resA = await PricingEndpoints.GetEntitlements(
            UserPrincipal(Guid.NewGuid()), ctxA, new FakeMode { Mode = TammaMode.SaaS },
            svcA.Object, usageA.Object, NullLoggerFactory.Instance, CancellationToken.None);
        var resB = await PricingEndpoints.GetEntitlements(
            UserPrincipal(Guid.NewGuid()), ctxB, new FakeMode { Mode = TammaMode.SaaS },
            svcB.Object, usageB.Object, NullLoggerFactory.Instance, CancellationToken.None);

        DtoOf(resA).TenantId.Should().Be(a.ToString());
        DtoOf(resA).Limits.Single(l => l.MetricKey == "seats").LimitValue.Should().Be(5);
        DtoOf(resB).TenantId.Should().Be(b.ToString());
        DtoOf(resB).Limits.Single(l => l.MetricKey == "seats").LimitValue.Should().Be(50);
    }
}
