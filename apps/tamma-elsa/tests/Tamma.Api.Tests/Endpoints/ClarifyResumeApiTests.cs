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
/// Story 3.5 — the RBAC-gated clarify resume surface (<c>POST /api/adl/clarify/resume</c>).
/// Verifies the handler:
///   - forwards a well-formed answer to the engine with the caller's AMBIENT tenant id so the
///     engine can only resolve THIS tenant's gate (IDOR guard via tenant-in-bookmark-name);
///   - derives the resolver from the authenticated principal (a client-supplied resolver is
///     impossible — the request type carries none);
///   - validates the session + answers and rejects junk with 400 before forwarding;
///   - maps a "no gate waiting" engine response (incl. a cross-tenant miss) to 404.
///
/// The route group applies <c>WorkflowsManage</c> (tenant owner/admin; member SaaS callers hit
/// 403 at the policy) — the same posture as the merge/deploy/blocker resume routes.
/// </summary>
[TestFixture]
public class ClarifyResumeApiTests
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
    public async Task ResumeClarify_Valid_ForwardsAmbientTenantAndDerivedResolver_Returns200()
    {
        var tenant = Guid.NewGuid();
        var session = Guid.NewGuid();
        _elsa
            .Setup(s => s.ResumeClarifyingQuestionsAsync(
                session, tenant.ToString(), "we mean 30s", "alice@example.com"))
            .ReturnsAsync(new MergeApprovalResumeResult(Resumed: true, GateNotFound: false, WorkflowInstanceId: "wf-c1"));

        var req = new AdlEndpoints.ClarifyAnswersRequest(session, "we mean 30s");

        var result = await AdlEndpoints.ResumeClarify(
            req, _elsa.Object, TenantContext(tenant), Principal("alice@example.com"), _loggerFactory);

        StatusCodeOf(result).Should().Be(StatusCodes.Status200OK);
        _elsa.Verify(s => s.ResumeClarifyingQuestionsAsync(
            session, tenant.ToString(), "we mean 30s", "alice@example.com"), Times.Once);
    }

    [Test]
    public async Task ResumeClarify_ResolverDerivedFromPrincipal_NotForgeable()
    {
        var tenant = Guid.NewGuid();
        string? capturedResolver = null;
        _elsa
            .Setup(s => s.ResumeClarifyingQuestionsAsync(
                It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Callback<Guid, string?, string, string?>((_, _, _, resolver) => capturedResolver = resolver)
            .ReturnsAsync(new MergeApprovalResumeResult(true, false, "wf-9"));

        var req = new AdlEndpoints.ClarifyAnswersRequest(Guid.NewGuid(), "answer text");

        await AdlEndpoints.ResumeClarify(
            req, _elsa.Object, TenantContext(tenant), Principal("bob@corp.test"), _loggerFactory);

        capturedResolver.Should().Be("bob@corp.test",
            "the resolver must be derived from the authenticated principal for non-repudiation");
    }

    [Test]
    public async Task ResumeClarify_CrossTenantGate_Returns404_NeverActs()
    {
        // The victim's gate lives under a different bookmark name (keyed by the victim's tenant),
        // so the engine reports GateNotFound for the caller's ambient tenant. The handler must
        // surface 404 and forward the CALLER's tenant id, never the victim's.
        var callerTenant = Guid.NewGuid();
        var session = Guid.NewGuid();
        _elsa
            .Setup(s => s.ResumeClarifyingQuestionsAsync(
                session, callerTenant.ToString(), "sneaky", It.IsAny<string?>()))
            .ReturnsAsync(new MergeApprovalResumeResult(Resumed: false, GateNotFound: true, WorkflowInstanceId: null));

        var req = new AdlEndpoints.ClarifyAnswersRequest(session, "sneaky");

        var result = await AdlEndpoints.ResumeClarify(
            req, _elsa.Object, TenantContext(callerTenant), Principal("attacker@evil.test"), _loggerFactory);

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound,
            "a cross-tenant resume must be a not-found, never an action on the victim's gate");
        _elsa.Verify(s => s.ResumeClarifyingQuestionsAsync(
            session, callerTenant.ToString(), "sneaky", It.IsAny<string?>()), Times.Once);
    }

    [Test]
    public async Task ResumeClarify_GateNotWaiting_Returns404()
    {
        var tenant = Guid.NewGuid();
        var session = Guid.NewGuid();
        _elsa
            .Setup(s => s.ResumeClarifyingQuestionsAsync(
                session, It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(new MergeApprovalResumeResult(Resumed: false, GateNotFound: true, WorkflowInstanceId: null));

        var req = new AdlEndpoints.ClarifyAnswersRequest(session, "answered");

        var result = await AdlEndpoints.ResumeClarify(
            req, _elsa.Object, TenantContext(tenant), Principal("dev@x.test"), _loggerFactory);

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Test]
    public async Task ResumeClarify_EmptySession_Returns400_NoForward()
    {
        var req = new AdlEndpoints.ClarifyAnswersRequest(Guid.Empty, "answered");
        var result = await AdlEndpoints.ResumeClarify(
            req, _elsa.Object, TenantContext(Guid.NewGuid()), Principal("dev@x.test"), _loggerFactory);

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
        _elsa.Verify(s => s.ResumeClarifyingQuestionsAsync(
            It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [TestCase("")]
    [TestCase("   ")]
    public async Task ResumeClarify_EmptyAnswers_Returns400_NoForward(string answers)
    {
        var req = new AdlEndpoints.ClarifyAnswersRequest(Guid.NewGuid(), answers);
        var result = await AdlEndpoints.ResumeClarify(
            req, _elsa.Object, TenantContext(Guid.NewGuid()), Principal("dev@x.test"), _loggerFactory);

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
        _elsa.Verify(s => s.ResumeClarifyingQuestionsAsync(
            It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }
}
