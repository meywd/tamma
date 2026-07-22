using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Endpoints;
using Tamma.Api.Services;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;
using Tamma.Data;

namespace Tamma.Api.Tests.Endpoints;

/// <summary>
/// Story 39-8 (AC5 public half, AC1 decider clause, AC3 channel derivation). The RBAC-gated
/// document-decision surface (<c>POST /api/documents/decisions/{sessionId}/resume</c>) must:
/// derive tenant + decider + channel SERVER-SIDE (the request carries no decider/channel);
/// build the 39-5 <see cref="AcceptanceDecision"/> JSON server-side; reject an invalid
/// <c>kind</c> with 400 BEFORE any forward; surface an engine 404 as 404; and pin
/// <see cref="ApprovalChannels.Derive"/> onto its three members. A member-role caller is
/// allowed (D10 — the group is AuthenticatedAny, the handler has no owner/admin gate).
/// </summary>
[TestFixture]
public class DocumentDecisionApiTests
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

    private static ClaimsPrincipal Human(string email)
        => new(new ClaimsIdentity(new[] { new Claim(JwtRegisteredClaimNames.Email, email) }, "AuthenticationTypes.Federation"));

    private static ClaimsPrincipal Orchestrator()
        => new(new ClaimsIdentity(new[]
        {
            new Claim(ApprovalChannels.PrincipalTypeClaim, ApprovalChannels.OrchestratorPrincipalType),
            new Claim(JwtRegisteredClaimNames.Sub, "orchestrator-agent"),
        }, "AuthenticationTypes.Federation"));

    private static ClaimsPrincipal ServiceCredential()
        => new(new ClaimsIdentity(new[] { new Claim(JwtRegisteredClaimNames.Sub, "svc-1") }, "ApiKey"));

    private static int? StatusCodeOf(IResult result)
        => (result as IStatusCodeHttpResult)?.StatusCode;

    // ── server-side derivation of tenant + decider + channel ───────────────

    [Test]
    public async Task ResumeDecision_ValidAccept_ForwardsAmbientTenant_ServerBuiltDecision_DerivedDeciderAndChannel()
    {
        var tenant = Guid.NewGuid();
        var session = Guid.NewGuid();
        string? capturedDecisionJson = null;
        string? capturedTenant = null;
        string? capturedDecider = null;
        string? capturedChannel = null;

        _elsa
            .Setup(s => s.ResumeDocumentDecisionAsync(
                session, It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Callback<Guid, string?, string, string?, string?, string?, string, string?>(
                (_, t, dj, _, did, _, ch, _) => { capturedTenant = t; capturedDecisionJson = dj; capturedDecider = did; capturedChannel = ch; })
            .ReturnsAsync(new MergeApprovalResumeResult(Resumed: true, GateNotFound: false, WorkflowInstanceId: "wf-1"));

        var req = new DocumentDecisionEndpoints.DecisionRequest("accept", null, null, null, "ship it");

        var result = await DocumentDecisionEndpoints.ResumeDecision(
            session, req, _elsa.Object, TenantContext(tenant), Human("alice@example.com"), _loggerFactory);

        StatusCodeOf(result).Should().Be(StatusCodes.Status200OK);
        capturedTenant.Should().Be(tenant.ToString(), "the ambient tenant is folded into the bookmark name server-side");
        capturedDecider.Should().Be("alice@example.com", "the decider is derived from the principal (non-repudiation)");
        capturedChannel.Should().Be("user", "a human session derives the user channel");

        // The decision JSON is built server-side and is a valid 39-5 AcceptanceDecision.
        capturedDecisionJson.Should().NotBeNull();
        JsonSerializer.Deserialize<AcceptanceDecision>(capturedDecisionJson!).Should().BeOfType<AcceptanceDecision.Accept>();
    }

    [Test]
    public async Task ResumeDecision_RequestRevision_BuildsRequestRevisionWithNotes()
    {
        var session = Guid.NewGuid();
        string? capturedDecisionJson = null;
        _elsa
            .Setup(s => s.ResumeDocumentDecisionAsync(
                session, It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Callback<Guid, string?, string, string?, string?, string?, string, string?>(
                (_, _, dj, _, _, _, _, _) => capturedDecisionJson = dj)
            .ReturnsAsync(new MergeApprovalResumeResult(true, false, "wf-2"));

        var req = new DocumentDecisionEndpoints.DecisionRequest("request-revision", "tighten it", null, null, null);

        await DocumentDecisionEndpoints.ResumeDecision(
            session, req, _elsa.Object, TenantContext(Guid.NewGuid()), Human("bob@x.test"), _loggerFactory);

        var decision = JsonSerializer.Deserialize<AcceptanceDecision>(capturedDecisionJson!);
        decision.Should().BeOfType<AcceptanceDecision.RequestRevision>()
            .Which.Notes.Should().Be("tighten it");
    }

    // ── validation before forward ──────────────────────────────────────────

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("maybe")]
    [TestCase("auto-accept")]
    public async Task ResumeDecision_InvalidKind_Returns400_NoForward(string kind)
    {
        var req = new DocumentDecisionEndpoints.DecisionRequest(kind, null, null, null, null);
        var result = await DocumentDecisionEndpoints.ResumeDecision(
            Guid.NewGuid(), req, _elsa.Object, TenantContext(Guid.NewGuid()), Human("dev@x.test"), _loggerFactory);

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
        _elsa.Verify(s => s.ResumeDocumentDecisionAsync(
            It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Test]
    public async Task ResumeDecision_EmptySession_Returns400_NoForward()
    {
        var req = new DocumentDecisionEndpoints.DecisionRequest("accept", null, null, null, null);
        var result = await DocumentDecisionEndpoints.ResumeDecision(
            Guid.Empty, req, _elsa.Object, TenantContext(Guid.NewGuid()), Human("dev@x.test"), _loggerFactory);

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
        _elsa.Verify(s => s.ResumeDocumentDecisionAsync(
            It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Test]
    public async Task ResumeDecision_EngineGateNotWaiting_Returns404()
    {
        var session = Guid.NewGuid();
        _elsa
            .Setup(s => s.ResumeDocumentDecisionAsync(
                session, It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(new MergeApprovalResumeResult(Resumed: false, GateNotFound: true, WorkflowInstanceId: null));

        var req = new DocumentDecisionEndpoints.DecisionRequest("accept", null, null, null, null);
        var result = await DocumentDecisionEndpoints.ResumeDecision(
            session, req, _elsa.Object, TenantContext(Guid.NewGuid()), Human("dev@x.test"), _loggerFactory);

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Test]
    public async Task ResumeDecision_MemberRoleCaller_IsAllowed_D10()
    {
        // The handler has no owner/admin gate (the group is AuthenticatedAny) — a plain member
        // human forwards successfully. Hardening to WorkflowsManage would be a conscious change
        // that trips THIS test.
        var session = Guid.NewGuid();
        _elsa
            .Setup(s => s.ResumeDocumentDecisionAsync(
                session, It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(new MergeApprovalResumeResult(true, false, "wf-9"));

        var req = new DocumentDecisionEndpoints.DecisionRequest("accept", null, null, null, null);
        var result = await DocumentDecisionEndpoints.ResumeDecision(
            session, req, _elsa.Object, TenantContext(Guid.NewGuid()), Human("member@x.test"), _loggerFactory);

        StatusCodeOf(result).Should().Be(StatusCodes.Status200OK);
    }

    // ── ApprovalChannels.Derive pins (AC3) ─────────────────────────────────

    [Test]
    public void Derive_OrchestratorClaim_MapsToOrchestrator() =>
        ApprovalChannels.Derive(Orchestrator()).Should().Be(ApprovalChannel.Orchestrator);

    [Test]
    public void Derive_HumanSession_MapsToUser() =>
        ApprovalChannels.Derive(Human("alice@x.test")).Should().Be(ApprovalChannel.User);

    [Test]
    public void Derive_ServiceCredential_MapsToApi() =>
        ApprovalChannels.Derive(ServiceCredential()).Should().Be(ApprovalChannel.Api);

    [Test]
    public async Task ResumeDecision_OrchestratorPrincipal_DerivesOrchestratorChannel()
    {
        var session = Guid.NewGuid();
        string? capturedChannel = null;
        _elsa
            .Setup(s => s.ResumeDocumentDecisionAsync(
                session, It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Callback<Guid, string?, string, string?, string?, string?, string, string?>(
                (_, _, _, _, _, _, ch, _) => capturedChannel = ch)
            .ReturnsAsync(new MergeApprovalResumeResult(true, false, "wf-o"));

        var req = new DocumentDecisionEndpoints.DecisionRequest("accept", null, null, null, null);
        await DocumentDecisionEndpoints.ResumeDecision(
            session, req, _elsa.Object, TenantContext(Guid.NewGuid()), Orchestrator(), _loggerFactory);

        capturedChannel.Should().Be("orchestrator");
    }
}
