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
/// IMPORTANT-2 + SECURITY (C1 cross-tenant IDOR, I2 forged approver, MINOR
/// decision validation) — the RBAC-gated merge-approval resume surface
/// (<c>POST /api/adl/merge-approval/resume</c>). Verifies the handler:
///   - forwards a well-formed decision to the engine with the caller's AMBIENT
///     tenant id + repository so the engine can only resolve THIS tenant's gate;
///   - derives the approver from the authenticated principal (a client-supplied
///     approver is impossible — the request type carries none);
///   - validates the decision against {merge,test,reject} and rejects junk 400;
///   - maps a "no gate waiting" engine response (incl. a cross-tenant miss) to 404.
/// </summary>
[TestFixture]
public class AdlEndpointsTests
{
    private Mock<IElsaWorkflowService> _elsa = null!;
    private readonly NullLoggerFactory _loggerFactory = NullLoggerFactory.Instance;

    [SetUp]
    public void SetUp()
    {
        _elsa = new Mock<IElsaWorkflowService>();
    }

    /// <summary>A tenant context fixed to a given tenant (or ambient-null).</summary>
    private static ITenantContext TenantContext(Guid? tenantId)
    {
        var ctx = new Mock<ITenantContext>();
        ctx.SetupGet(c => c.TenantId).Returns(tenantId);
        return ctx.Object;
    }

    /// <summary>An authenticated principal carrying an email claim (the approver).</summary>
    private static ClaimsPrincipal Principal(string email, string? sub = null)
    {
        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Email, email) };
        if (sub is not null) claims.Add(new Claim(JwtRegisteredClaimNames.Sub, sub));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Test]
    public async Task ResumeMergeApproval_ValidMerge_ForwardsTenantRepoAndDerivedApprover_Returns200()
    {
        var tenant = Guid.NewGuid();
        _elsa
            .Setup(s => s.ResumeMergeApprovalAsync(
                42, 7, tenant.ToString(), "octo/repo", "merge", "lgtm", "alice@example.com"))
            .ReturnsAsync(new MergeApprovalResumeResult(Resumed: true, GateNotFound: false, WorkflowInstanceId: "wf-1"));

        var req = new AdlEndpoints.MergeApprovalDecisionRequest(42, 7, "octo/repo", "merge", "lgtm");

        var result = await AdlEndpoints.ResumeMergeApproval(
            req, _elsa.Object, TenantContext(tenant), Principal("alice@example.com"), _loggerFactory);

        StatusCodeOf(result).Should().Be(StatusCodes.Status200OK);
        // The ambient tenant id + repository must be forwarded so the engine
        // scopes the bookmark lookup; the approver must be the principal's email,
        // NOT anything from the request body.
        _elsa.Verify(s => s.ResumeMergeApprovalAsync(
            42, 7, tenant.ToString(), "octo/repo", "merge", "lgtm", "alice@example.com"), Times.Once);
    }

    [Test]
    public async Task ResumeMergeApproval_ApproverDerivedFromPrincipal_NotForgeable()
    {
        // I2 — even though the principal claims one identity, the engine must
        // receive THAT identity as approver. The request type carries no approver
        // field at all, so a client cannot forge it.
        var tenant = Guid.NewGuid();
        string? capturedApprover = null;
        _elsa
            .Setup(s => s.ResumeMergeApprovalAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Callback<int, int, string?, string?, string, string?, string?>(
                (_, _, _, _, _, _, approver) => capturedApprover = approver)
            .ReturnsAsync(new MergeApprovalResumeResult(true, false, "wf-9"));

        var req = new AdlEndpoints.MergeApprovalDecisionRequest(1, 2, "octo/repo", "reject", null);

        await AdlEndpoints.ResumeMergeApproval(
            req, _elsa.Object, TenantContext(tenant), Principal("bob@corp.test"), _loggerFactory);

        capturedApprover.Should().Be("bob@corp.test",
            "the approver must be derived from the authenticated principal for non-repudiation");
    }

    [Test]
    public async Task ResumeMergeApproval_CrossTenantGate_Returns404_NeverActs()
    {
        // C1 — a caller in tenant A resumes with their ambient tenant id. Tenant
        // B's gate lives under a different bookmark name, so the engine reports
        // GateNotFound. The handler must surface 404 — it must NOT act on another
        // tenant's gate. (We assert the handler forwards the CALLER's tenant id,
        // never the victim's, and maps the miss to 404.)
        var callerTenant = Guid.NewGuid();
        _elsa
            .Setup(s => s.ResumeMergeApprovalAsync(
                5, 5, callerTenant.ToString(), "victim/repo", "merge", null, It.IsAny<string?>()))
            .ReturnsAsync(new MergeApprovalResumeResult(Resumed: false, GateNotFound: true, WorkflowInstanceId: null));

        var req = new AdlEndpoints.MergeApprovalDecisionRequest(5, 5, "victim/repo", "merge", null);

        var result = await AdlEndpoints.ResumeMergeApproval(
            req, _elsa.Object, TenantContext(callerTenant), Principal("attacker@evil.test"), _loggerFactory);

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound,
            "a cross-tenant resume must be a not-found, never an action on the victim's gate");
        _elsa.Verify(s => s.ResumeMergeApprovalAsync(
            5, 5, callerTenant.ToString(), "victim/repo", "merge", null, It.IsAny<string?>()), Times.Once);
        // The caller's tenant id was used — there is no path that forwards a
        // different (victim) tenant id.
    }

    [Test]
    public async Task ResumeMergeApproval_GateNotWaiting_Returns404()
    {
        _elsa
            .Setup(s => s.ResumeMergeApprovalAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(new MergeApprovalResumeResult(Resumed: false, GateNotFound: true, WorkflowInstanceId: null));

        var req = new AdlEndpoints.MergeApprovalDecisionRequest(1, 2, "octo/repo", "reject", null);

        var result = await AdlEndpoints.ResumeMergeApproval(
            req, _elsa.Object, TenantContext(Guid.NewGuid()), Principal("a@b.test"), _loggerFactory);

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound,
            "a resume for a gate that is not suspended must 404, not 500");
    }

    [Test]
    public async Task ResumeMergeApproval_EmptyDecision_Returns400_NoForward()
    {
        var req = new AdlEndpoints.MergeApprovalDecisionRequest(1, 2, "octo/repo", "   ", null);

        var result = await AdlEndpoints.ResumeMergeApproval(
            req, _elsa.Object, TenantContext(Guid.NewGuid()), Principal("a@b.test"), _loggerFactory);

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
        VerifyNeverForwarded();
    }

    [TestCase("approve")]
    [TestCase("yes")]
    [TestCase("delete")]
    [TestCase("merge; drop table")]
    public async Task ResumeMergeApproval_UnknownDecision_Returns400_NoForward(string decision)
    {
        // MINOR — an arbitrary decision string must be rejected up front, never
        // forwarded to silently escalate at the gate.
        var req = new AdlEndpoints.MergeApprovalDecisionRequest(1, 2, "octo/repo", decision, null);

        var result = await AdlEndpoints.ResumeMergeApproval(
            req, _elsa.Object, TenantContext(Guid.NewGuid()), Principal("a@b.test"), _loggerFactory);

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest,
            "only merge|test|reject are accepted");
        VerifyNeverForwarded();
    }

    [Test]
    public async Task ResumeMergeApproval_NonPositiveIssueOrPr_Returns400()
    {
        var req = new AdlEndpoints.MergeApprovalDecisionRequest(0, 0, "octo/repo", "merge", null);

        var result = await AdlEndpoints.ResumeMergeApproval(
            req, _elsa.Object, TenantContext(Guid.NewGuid()), Principal("a@b.test"), _loggerFactory);

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
        VerifyNeverForwarded();
    }

    [Test]
    public async Task ResumeMergeApproval_MissingRepository_Returns400()
    {
        var req = new AdlEndpoints.MergeApprovalDecisionRequest(1, 2, "   ", "merge", null);

        var result = await AdlEndpoints.ResumeMergeApproval(
            req, _elsa.Object, TenantContext(Guid.NewGuid()), Principal("a@b.test"), _loggerFactory);

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest,
            "repository is part of the bookmark scope and must be supplied");
        VerifyNeverForwarded();
    }

    [Test]
    public void ResolveApprover_PrefersEmail_ThenName_ThenSub()
    {
        AdlEndpoints.ResolveApprover(Principal("e@x.test", sub: "sub-1")).Should().Be("e@x.test");

        var nameOnly = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("name", "Carol"), new Claim(JwtRegisteredClaimNames.Sub, "sub-2") }, "test"));
        AdlEndpoints.ResolveApprover(nameOnly).Should().Be("Carol");

        var subOnly = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(JwtRegisteredClaimNames.Sub, "sub-3") }, "test"));
        AdlEndpoints.ResolveApprover(subOnly).Should().Be("sub-3");

        AdlEndpoints.ResolveApprover(new ClaimsPrincipal(new ClaimsIdentity()))
            .Should().Be("unknown");
    }

    // ================================================================
    // Production-deploy approval gate (completeness audit P0 item 3) —
    // POST /api/adl/deploy-approval/resume. Same security model as the merge gate.
    // ================================================================

    [Test]
    public async Task ResumeDeploymentApproval_ValidApprove_ForwardsTenantRepoShaAndDerivedApprover_Returns200()
    {
        var tenant = Guid.NewGuid();
        _elsa
            .Setup(s => s.ResumeDeploymentApprovalAsync(
                42, tenant.ToString(), "octo/repo", "deadbeef", "approve", "ship it", "alice@example.com"))
            .ReturnsAsync(new MergeApprovalResumeResult(Resumed: true, GateNotFound: false, WorkflowInstanceId: "wf-1"));

        var req = new AdlEndpoints.DeployApprovalDecisionRequest(42, "octo/repo", "deadbeef", "approve", "ship it");

        var result = await AdlEndpoints.ResumeDeploymentApproval(
            req, _elsa.Object, TenantContext(tenant), Principal("alice@example.com"), _loggerFactory);

        StatusCodeOf(result).Should().Be(StatusCodes.Status200OK);
        _elsa.Verify(s => s.ResumeDeploymentApprovalAsync(
            42, tenant.ToString(), "octo/repo", "deadbeef", "approve", "ship it", "alice@example.com"), Times.Once);
    }

    [Test]
    public async Task ResumeDeploymentApproval_ApproverDerivedFromPrincipal_NotForgeable()
    {
        var tenant = Guid.NewGuid();
        string? capturedApprover = null;
        _elsa
            .Setup(s => s.ResumeDeploymentApprovalAsync(
                It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Callback<int, string?, string?, string?, string, string?, string?>(
                (_, _, _, _, _, _, approver) => capturedApprover = approver)
            .ReturnsAsync(new MergeApprovalResumeResult(true, false, "wf-9"));

        var req = new AdlEndpoints.DeployApprovalDecisionRequest(1, "octo/repo", "sha", "reject", null);

        await AdlEndpoints.ResumeDeploymentApproval(
            req, _elsa.Object, TenantContext(tenant), Principal("bob@corp.test"), _loggerFactory);

        capturedApprover.Should().Be("bob@corp.test",
            "the prod-deploy approver must be derived from the authenticated principal for non-repudiation");
    }

    [Test]
    public async Task ResumeDeploymentApproval_CrossTenantGate_Returns404_NeverActs()
    {
        var callerTenant = Guid.NewGuid();
        _elsa
            .Setup(s => s.ResumeDeploymentApprovalAsync(
                5, callerTenant.ToString(), "victim/repo", "sha", "approve", null, It.IsAny<string?>()))
            .ReturnsAsync(new MergeApprovalResumeResult(Resumed: false, GateNotFound: true, WorkflowInstanceId: null));

        var req = new AdlEndpoints.DeployApprovalDecisionRequest(5, "victim/repo", "sha", "approve", null);

        var result = await AdlEndpoints.ResumeDeploymentApproval(
            req, _elsa.Object, TenantContext(callerTenant), Principal("attacker@evil.test"), _loggerFactory);

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound);
        _elsa.Verify(s => s.ResumeDeploymentApprovalAsync(
            5, callerTenant.ToString(), "victim/repo", "sha", "approve", null, It.IsAny<string?>()), Times.Once);
    }

    [TestCase("yes")]
    [TestCase("merge")]      // wrong gate's vocabulary
    [TestCase("approve; drop table")]
    public async Task ResumeDeploymentApproval_UnknownDecision_Returns400_NoForward(string decision)
    {
        var req = new AdlEndpoints.DeployApprovalDecisionRequest(1, "octo/repo", "sha", decision, null);

        var result = await AdlEndpoints.ResumeDeploymentApproval(
            req, _elsa.Object, TenantContext(Guid.NewGuid()), Principal("a@b.test"), _loggerFactory);

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest, "only approve|reject are accepted");
        VerifyDeployNeverForwarded();
    }

    [Test]
    public async Task ResumeDeploymentApproval_MissingMergeSha_Returns400()
    {
        var req = new AdlEndpoints.DeployApprovalDecisionRequest(1, "octo/repo", "  ", "approve", null);

        var result = await AdlEndpoints.ResumeDeploymentApproval(
            req, _elsa.Object, TenantContext(Guid.NewGuid()), Principal("a@b.test"), _loggerFactory);

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest,
            "mergeSha is part of the bookmark scope and must be supplied");
        VerifyDeployNeverForwarded();
    }

    private void VerifyDeployNeverForwarded() =>
        _elsa.Verify(s => s.ResumeDeploymentApprovalAsync(
            It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Never, "invalid input must be rejected up front, not forwarded");

    private void VerifyNeverForwarded() =>
        _elsa.Verify(s => s.ResumeMergeApprovalAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Never, "invalid input must be rejected up front, not forwarded");

    /// <summary>Read the HTTP status code off any minimal-API result (typed
    /// results implement IStatusCodeHttpResult).</summary>
    private static int? StatusCodeOf(IResult result)
        => (result as IStatusCodeHttpResult)?.StatusCode;
}
