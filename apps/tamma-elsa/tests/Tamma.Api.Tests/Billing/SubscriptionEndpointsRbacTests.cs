using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Authorization;
using Tamma.Api.Endpoints.Billing;
using Tamma.Api.Services.Billing;

namespace Tamma.Api.Tests.Billing;

/// <summary>
/// Story 35-4 (AC2, AC9) — subscription endpoint RBAC. A <c>member</c> caller is
/// rejected with 403 on every mutation BEFORE the service is touched;
/// <c>admin</c>/<c>owner</c> pass; GET is allowed for any member.
/// </summary>
[TestFixture]
public class SubscriptionEndpointsRbacTests
{
    private static readonly ILoggerFactory Logs = NullLoggerFactory.Instance;

    private static HttpContext Ctx(string? role)
    {
        var http = new DefaultHttpContext();
        if (role is not null)
            http.Items[RequireTenantMembershipFilter.TenantRoleItemKey] = role;
        return http;
    }

    private static int Status(IResult result)
        => ((IStatusCodeHttpResult)result).StatusCode ?? StatusCodes.Status200OK;

    [TestCase("member")]
    [TestCase(null)]
    public async Task Member_Gets_403_On_Every_Mutation_Without_Service_Call(string? role)
    {
        var svc = new Mock<ISubscriptionService>(MockBehavior.Strict);
        var tenantId = Guid.NewGuid();

        var checkout = await SubscriptionEndpoints.Checkout(
            tenantId, new SubscriptionEndpoints.CheckoutRequest("team", null, null),
            Ctx(role), svc.Object, Logs, CancellationToken.None);
        var change = await SubscriptionEndpoints.ChangePlan(
            tenantId, new SubscriptionEndpoints.ChangePlanRequest("team"),
            Ctx(role), svc.Object, Logs, CancellationToken.None);
        var cancel = await SubscriptionEndpoints.Cancel(
            tenantId, new SubscriptionEndpoints.CancelRequest(true),
            Ctx(role), svc.Object, Logs, CancellationToken.None);
        var seats = await SubscriptionEndpoints.ChangeSeats(
            tenantId, new SubscriptionEndpoints.SeatsRequest(3),
            Ctx(role), svc.Object, Logs, CancellationToken.None);

        Status(checkout).Should().Be(StatusCodes.Status403Forbidden);
        Status(change).Should().Be(StatusCodes.Status403Forbidden);
        Status(cancel).Should().Be(StatusCodes.Status403Forbidden);
        Status(seats).Should().Be(StatusCodes.Status403Forbidden);

        // Strict mock — never invoked (403 short-circuits before the service).
        svc.VerifyNoOtherCalls();
    }

    [TestCase("admin")]
    [TestCase("owner")]
    public async Task Admin_And_Owner_Pass_Mutations_To_Service(string role)
    {
        var svc = new Mock<ISubscriptionService>();
        svc.Setup(s => s.CancelAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SubscriptionProjection.FreeDefault());
        var tenantId = Guid.NewGuid();

        var result = await SubscriptionEndpoints.Cancel(
            tenantId, new SubscriptionEndpoints.CancelRequest(false),
            Ctx(role), svc.Object, Logs, CancellationToken.None);

        Status(result).Should().Be(StatusCodes.Status200OK);
        svc.Verify(s => s.CancelAsync(tenantId, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Member_Can_Read_Subscription()
    {
        var svc = new Mock<ISubscriptionService>();
        svc.Setup(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SubscriptionProjection.FreeDefault());
        var tenantId = Guid.NewGuid();

        var result = await SubscriptionEndpoints.GetSubscription(
            tenantId, Ctx("member"), svc.Object, CancellationToken.None);

        Status(result).Should().Be(StatusCodes.Status200OK);
        svc.Verify(s => s.GetAsync(tenantId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Seats_Floor_Conflict_Maps_To_409()
    {
        var svc = new Mock<ISubscriptionService>();
        svc.Setup(s => s.ChangeSeatsAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Tamma.Core.TammaError(
                SubscriptionService.SeatsBelowActiveMembersCode, "too few seats"));
        var tenantId = Guid.NewGuid();

        var result = await SubscriptionEndpoints.ChangeSeats(
            tenantId, new SubscriptionEndpoints.SeatsRequest(1),
            Ctx("admin"), svc.Object, Logs, CancellationToken.None);

        Status(result).Should().Be(StatusCodes.Status409Conflict);
    }
}
