using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Endpoints;
using Tamma.Api.Services;
using Tamma.Data;

namespace Tamma.Api.Tests.Endpoints;

/// <summary>
/// Story 3.7 — the RBAC-gated design resume surface (<c>POST /api/adl/design/resume</c>).
/// Verifies the handler:
///   - forwards a well-formed approve/reject to the engine with the caller's AMBIENT tenant id
///     so the engine can only resolve THIS tenant's gate (IDOR guard via tenant-in-bookmark-name);
///   - maps "approve"/"reject" to the boolean the engine expects and validates the decision;
///   - derives the reviewer from the authenticated principal (a client-supplied reviewer is
///     impossible — the request type carries none);
///   - validates the session + decision and rejects junk with 400 before forwarding;
///   - maps a "no gate waiting" engine response (incl. a cross-tenant miss) to 404.
///
/// The route group applies <c>WorkflowsManage</c> (tenant owner/admin; member SaaS callers hit
/// 403 at the policy) — the same posture as the merge/deploy/clarify resume routes.
/// </summary>
[TestFixture]
public class DesignResumeApiTests
{
    private Mock<IElsaWorkflowService> _elsa = null!;
    private readonly NullLoggerFactory _loggerFactory = NullLoggerFactory.Instance;

    [SetUp]
    public void SetUp() => _elsa = new Mock<IElsaWorkflowService>();

    private static ITenantContext TenantContext(Guid? tenantId)
    {
        var ctx = new Mock<ITenantContext>();
        ctx.SetupGet(c => c.TenantId).Returns(tenantId);
        return ctx.Object;
    }

    private static ClaimsPrincipal Principal(string email)
        => new(new ClaimsIdentity(new[] { new Claim(JwtRegisteredClaimNames.Email, email) }, "test"));

    private static int? StatusCodeOf(IResult result)
        => (result as IStatusCodeHttpResult)?.StatusCode;

    [Test]
    public async Task ResumeDesign_ValidApprove_ForwardsAmbientTenantApprovedTrueAndDerivedReviewer_Returns200()
    {
        var tenant = Guid.NewGuid();
        var session = Guid.NewGuid();
        _elsa
            .Setup(s => s.ResumeDesignApprovalAsync(
                session, tenant.ToString(), true, "ship it", "alice@example.com"))
            .ReturnsAsync(new MergeApprovalResumeResult(Resumed: true, GateNotFound: false, WorkflowInstanceId: "wf-d1"));

        var req = new AdlEndpoints.DesignReviewRequest(session, "approve", "ship it");

        var result = await AdlEndpoints.ResumeDesign(
            req, _elsa.Object, TenantContext(tenant), Principal("alice@example.com"), _loggerFactory);

        StatusCodeOf(result).Should().Be(StatusCodes.Status200OK);
        _elsa.Verify(s => s.ResumeDesignApprovalAsync(
            session, tenant.ToString(), true, "ship it", "alice@example.com"), Times.Once);
    }

    [Test]
    public async Task ResumeDesign_ValidReject_ForwardsApprovedFalse()
    {
        var tenant = Guid.NewGuid();
        var session = Guid.NewGuid();
        _elsa
            .Setup(s => s.ResumeDesignApprovalAsync(
                session, tenant.ToString(), false, "revise", It.IsAny<string?>()))
            .ReturnsAsync(new MergeApprovalResumeResult(true, false, "wf-d2"));

        var req = new AdlEndpoints.DesignReviewRequest(session, "reject", "revise");

        var result = await AdlEndpoints.ResumeDesign(
            req, _elsa.Object, TenantContext(tenant), Principal("bob@corp.test"), _loggerFactory);

        StatusCodeOf(result).Should().Be(StatusCodes.Status200OK);
        _elsa.Verify(s => s.ResumeDesignApprovalAsync(
            session, tenant.ToString(), false, "revise", It.IsAny<string?>()), Times.Once);
    }

    [Test]
    public async Task ResumeDesign_ReviewerDerivedFromPrincipal_NotForgeable()
    {
        var tenant = Guid.NewGuid();
        string? capturedReviewer = null;
        _elsa
            .Setup(s => s.ResumeDesignApprovalAsync(
                It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Callback<Guid, string?, bool, string?, string?>((_, _, _, _, reviewer) => capturedReviewer = reviewer)
            .ReturnsAsync(new MergeApprovalResumeResult(true, false, "wf-9"));

        var req = new AdlEndpoints.DesignReviewRequest(Guid.NewGuid(), "approve", null);

        await AdlEndpoints.ResumeDesign(
            req, _elsa.Object, TenantContext(tenant), Principal("bob@corp.test"), _loggerFactory);

        capturedReviewer.Should().Be("bob@corp.test",
            "the reviewer must be derived from the authenticated principal for non-repudiation");
    }

    [Test]
    public async Task ResumeDesign_CrossTenantGate_Returns404_NeverActs()
    {
        // The victim's gate lives under a different bookmark name (keyed by the victim's tenant),
        // so the engine reports GateNotFound for the caller's ambient tenant. The handler must
        // surface 404 and forward the CALLER's tenant id, never the victim's.
        var callerTenant = Guid.NewGuid();
        var session = Guid.NewGuid();
        _elsa
            .Setup(s => s.ResumeDesignApprovalAsync(
                session, callerTenant.ToString(), true, It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(new MergeApprovalResumeResult(Resumed: false, GateNotFound: true, WorkflowInstanceId: null));

        var req = new AdlEndpoints.DesignReviewRequest(session, "approve", "sneaky");

        var result = await AdlEndpoints.ResumeDesign(
            req, _elsa.Object, TenantContext(callerTenant), Principal("attacker@evil.test"), _loggerFactory);

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound,
            "a cross-tenant resume must be a not-found, never an action on the victim's gate");
        _elsa.Verify(s => s.ResumeDesignApprovalAsync(
            session, callerTenant.ToString(), true, It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
    }

    [Test]
    public async Task ResumeDesign_GateNotWaiting_Returns404()
    {
        var tenant = Guid.NewGuid();
        var session = Guid.NewGuid();
        _elsa
            .Setup(s => s.ResumeDesignApprovalAsync(
                session, It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(new MergeApprovalResumeResult(Resumed: false, GateNotFound: true, WorkflowInstanceId: null));

        var req = new AdlEndpoints.DesignReviewRequest(session, "approve", null);

        var result = await AdlEndpoints.ResumeDesign(
            req, _elsa.Object, TenantContext(tenant), Principal("dev@x.test"), _loggerFactory);

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Test]
    public async Task ResumeDesign_EmptySession_Returns400_NoForward()
    {
        var req = new AdlEndpoints.DesignReviewRequest(Guid.Empty, "approve", null);
        var result = await AdlEndpoints.ResumeDesign(
            req, _elsa.Object, TenantContext(Guid.NewGuid()), Principal("dev@x.test"), _loggerFactory);

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
        _elsa.Verify(s => s.ResumeDesignApprovalAsync(
            It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("maybe")]
    public async Task ResumeDesign_InvalidDecision_Returns400_NoForward(string decision)
    {
        var req = new AdlEndpoints.DesignReviewRequest(Guid.NewGuid(), decision, null);
        var result = await AdlEndpoints.ResumeDesign(
            req, _elsa.Object, TenantContext(Guid.NewGuid()), Principal("dev@x.test"), _loggerFactory);

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
        _elsa.Verify(s => s.ResumeDesignApprovalAsync(
            It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }
}
