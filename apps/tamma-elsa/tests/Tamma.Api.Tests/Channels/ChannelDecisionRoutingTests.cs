using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Endpoints;
using Tamma.Api.Hubs;
using Tamma.Api.Services;
using Tamma.Api.Services.Channels;
using Tamma.Api.Services.Documents;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Channels;

/// <summary>
/// Story 39-18 (AC2 decision half, AC4 no-double-resume half, D7) — the orchestrator
/// hub's decision methods DELEGATE to the SAME 39-8 resume surface the REST endpoint
/// uses, with the server-derived <c>orchestrator</c> channel, and never mutate state
/// themselves. Runs locally (Moq'd IElsaWorkflowService / event repo).
/// </summary>
[TestFixture]
public class ChannelDecisionRoutingTests
{
    private Mock<IElsaWorkflowService> _elsa = null!;
    private Mock<IChannelOutboxRepository> _outbox = null!;
    private Mock<IEventRepository> _events = null!;

    [SetUp]
    public void SetUp()
    {
        _elsa = new Mock<IElsaWorkflowService>();
        _outbox = new Mock<IChannelOutboxRepository>();
        _events = new Mock<IEventRepository>();
    }

    private OrchestratorChannelHub BuildHub(ClaimsPrincipal principal)
    {
        var decisions = new DocumentDecisionSubmissionService(_elsa.Object);
        var escalations = new EscalationDispositionService(_events.Object, NullLogger<EscalationDispositionService>.Instance);
        // ChannelOutboxService is not exercised by the decision methods; a stub is fine.
        var channels = new ChannelOutboxService(
            _outbox.Object, _events.Object,
            new Api.Services.Access.InitiatorOnlyTaskAudienceResolver(),
            Mock.Of<IHubContext<OrchestratorChannelHub, IOrchestratorChannelClient>>(),
            Mock.Of<IHubContext<UserChannelHub, IUserChannelClient>>(),
            NullLogger<ChannelOutboxService>.Instance);

        var hub = new OrchestratorChannelHub(
            _outbox.Object, decisions, escalations, channels,
            new AgentOfflineChatRelay(), NullLogger<OrchestratorChannelHub>.Instance);

        var ctx = new Mock<HubCallerContext>();
        ctx.SetupGet(c => c.User).Returns(principal);
        ctx.SetupGet(c => c.ConnectionId).Returns("conn-1");
        ctx.SetupGet(c => c.UserIdentifier).Returns("orchestrator-agent");
        ctx.SetupGet(c => c.ConnectionAborted).Returns(CancellationToken.None);
        hub.Context = ctx.Object;
        return hub;
    }

    private static ClaimsPrincipal Orchestrator(Guid tenantId) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ApprovalChannels.PrincipalTypeClaim, ApprovalChannels.OrchestratorPrincipalType),
            new Claim("tenantId", tenantId.ToString()),
            new Claim("email", "orchestrator@tamma"),
        }, "AuthenticationTypes.Federation"));

    [Test]
    public async Task SubmitDecision_ForwardsToResumeService_WithServerDerivedOrchestratorChannel()
    {
        var tenant = Guid.NewGuid();
        var session = Guid.NewGuid();
        string? capturedChannel = null;
        string? capturedTenant = null;
        _elsa
            .Setup(s => s.ResumeDocumentDecisionAsync(
                session, It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Callback<Guid, string?, string, string?, string?, string?, string, string?>(
                (_, t, _, _, _, _, ch, _) => { capturedTenant = t; capturedChannel = ch; })
            .ReturnsAsync(new MergeApprovalResumeResult(true, false, "wf-1"));

        var hub = BuildHub(Orchestrator(tenant));
        var result = await hub.SubmitDecision(session, """{"kind":"accept"}""", "ship it");

        result.Resumed.Should().BeTrue();
        result.GateNotFound.Should().BeFalse();
        result.Channel.Should().Be("orchestrator", "the channel is derived from the orchestrator principal, never the payload");
        result.Kind.Should().Be("accept");
        capturedChannel.Should().Be("orchestrator");
        capturedTenant.Should().Be(tenant.ToString());
    }

    [Test]
    public async Task SubmitDecision_SecondSubmitForSameSession_SurfacesGateNotFound_NoDoubleResume()
    {
        var session = Guid.NewGuid();
        _elsa
            .Setup(s => s.ResumeDocumentDecisionAsync(
                session, It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(new MergeApprovalResumeResult(false, true, null));

        var hub = BuildHub(Orchestrator(Guid.NewGuid()));
        var result = await hub.SubmitDecision(session, """{"kind":"accept"}""", null);

        result.Resumed.Should().BeFalse();
        result.GateNotFound.Should().BeTrue("a duplicate submit hits the 39-8 404/409 discipline — no double-resume");
        // The hub never touched outbox/decision state itself.
        _outbox.VerifyNoOtherCalls();
    }

    [Test]
    public async Task SubmitEscalationDisposition_DelegatesToEscalationService()
    {
        // No ESCALATION.TRIGGERED exists → the disposition service returns NotFound,
        // proving the hub delegated to it (rather than applying anything itself).
        _events
            .Setup(e => e.QueryAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int>()))
            .ReturnsAsync(new List<DomainEvent>());

        var hub = BuildHub(Orchestrator(Guid.NewGuid()));
        var result = await hub.SubmitEscalationDisposition("esc-404", "resolved", null);

        result.NotFound.Should().BeTrue();
        result.Resolved.Should().BeFalse();
        _events.Verify(e => e.QueryAsync(It.IsAny<Guid?>(), "ESCALATION.TRIGGERED", null, It.IsAny<int>()), Times.AtLeastOnce);
    }

    [Test]
    public async Task SubmitEscalationDisposition_InvalidDisposition_ReturnsInvalid_NoServiceCall()
    {
        var hub = BuildHub(Orchestrator(Guid.NewGuid()));
        var result = await hub.SubmitEscalationDisposition("esc-1", "not-a-disposition", null);

        result.Invalid.Should().BeTrue();
        _events.Verify(e => e.QueryAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int>()), Times.Never);
    }
}
