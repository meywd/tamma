using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Hubs;
using Tamma.Api.Services.Access;
using Tamma.Api.Services.Channels;
using Tamma.Core;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Channels;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Channels;

/// <summary>
/// Story 39-18 (AC4 write half, AC6, AC3 scoping half) — the outbox write path:
/// persist BEFORE publish, guidance events fail-loud, conversation kinds refused,
/// task fan-out per resolver-approved recipient, kind→audience mismatch rejected.
/// Runs locally (Moq'd repository / hub contexts / event repo).
/// </summary>
[TestFixture]
public class ChannelOutboxServiceTests
{
    private Mock<IChannelOutboxRepository> _outbox = null!;
    private Mock<IEventRepository> _events = null!;
    private FakeAudienceResolver _audience = null!;
    private Mock<IHubContext<OrchestratorChannelHub, IOrchestratorChannelClient>> _orchestratorHub = null!;
    private Mock<IHubContext<UserChannelHub, IUserChannelClient>> _userHub = null!;
    private Mock<IOrchestratorChannelClient> _orchestratorClient = null!;
    private Mock<IUserChannelClient> _userClient = null!;

    [SetUp]
    public void SetUp()
    {
        _outbox = new Mock<IChannelOutboxRepository>();
        _outbox.Setup(r => r.EnqueueAsync(It.IsAny<ChannelOutboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ChannelOutboxMessage m, CancellationToken _) => m);
        _events = new Mock<IEventRepository>();
        _events.Setup(e => e.AppendAsync(It.IsAny<DomainEvent>()))
            .ReturnsAsync((DomainEvent e) => e);
        _audience = new FakeAudienceResolver();

        _orchestratorClient = new Mock<IOrchestratorChannelClient>();
        var orchClients = new Mock<IHubClients<IOrchestratorChannelClient>>();
        orchClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_orchestratorClient.Object);
        _orchestratorHub = new Mock<IHubContext<OrchestratorChannelHub, IOrchestratorChannelClient>>();
        _orchestratorHub.SetupGet(h => h.Clients).Returns(orchClients.Object);

        _userClient = new Mock<IUserChannelClient>();
        var userClients = new Mock<IHubClients<IUserChannelClient>>();
        userClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_userClient.Object);
        _userHub = new Mock<IHubContext<UserChannelHub, IUserChannelClient>>();
        _userHub.SetupGet(h => h.Clients).Returns(userClients.Object);
    }

    private ChannelOutboxService Build() => new(
        _outbox.Object, _events.Object, _audience,
        _orchestratorHub.Object, _userHub.Object, NullLogger<ChannelOutboxService>.Instance);

    private static ChannelEnvelope Envelope(ChannelMessage message, ChannelAudience audience, Guid? tenant = null) =>
        new(UuidV7.NewGuid(), tenant ?? Guid.NewGuid(), audience, null, message, DateTimeOffset.UtcNow);

    [Test]
    public async Task Enqueue_PersistsBeforePublish_HubThrowLeavesRowPending_NoExceptionToCaller()
    {
        // Publish throws — persist must already have happened, and the caller must not see it.
        _orchestratorClient.Setup(c => c.Receive(It.IsAny<ChannelEnvelope>()))
            .ThrowsAsync(new InvalidOperationException("hub down"));

        var svc = Build();
        var envelope = Envelope(new EscalationRaised("esc-1", "rounds-exhausted", "{}", "issue-1", null), ChannelAudience.Orchestrator);

        var act = async () => await svc.EnqueueAsync(envelope);

        await act.Should().NotThrowAsync("a hub publish failure never throws into the caller");
        _outbox.Verify(r => r.EnqueueAsync(It.IsAny<ChannelOutboxMessage>(), It.IsAny<CancellationToken>()), Times.Once, "persist happens BEFORE publish");
        _outbox.Verify(r => r.MarkDeliveredAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never, "publish threw before delivered was marked — the row stays pending");
    }

    [Test]
    public async Task Enqueue_GuidanceQuery_AppendsGuidanceRequested()
    {
        var svc = Build();
        var envelope = Envelope(new GuidanceQuery(Guid.NewGuid(), "corr-1", "what?", null), ChannelAudience.Orchestrator);

        await svc.EnqueueAsync(envelope);

        _events.Verify(e => e.AppendAsync(It.Is<DomainEvent>(d => d.Type == "GUIDANCE.REQUESTED")), Times.Once);
    }

    [Test]
    public async Task Enqueue_GuidanceQuery_EventAppendFailure_IsFailLoud()
    {
        _events.Setup(e => e.AppendAsync(It.Is<DomainEvent>(d => d.Type == "GUIDANCE.REQUESTED")))
            .ThrowsAsync(new InvalidOperationException("event store down"));
        var svc = Build();
        var envelope = Envelope(new GuidanceQuery(Guid.NewGuid(), "corr-1", "what?", null), ChannelAudience.Orchestrator);

        var act = async () => await svc.EnqueueAsync(envelope);

        await act.Should().ThrowAsync<InvalidOperationException>("the GUIDANCE event IS part of the operation (fail-loud)");
    }

    [Test]
    public async Task Enqueue_DirectConversationKind_IsRefused()
    {
        var svc = Build();
        var envelope = Envelope(new AgentConversationMessage(Guid.NewGuid(), Guid.NewGuid(), "user->agent", "hi"), ChannelAudience.User);

        var act = async () => await svc.EnqueueAsync(envelope);

        var ex = await act.Should().ThrowAsync<TammaError>();
        ex.Which.Code.Should().Be("CHANNEL.MESSAGE.INVALID");
        _outbox.Verify(r => r.EnqueueAsync(It.IsAny<ChannelOutboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Enqueue_KindAudienceMismatch_Throws()
    {
        var svc = Build();
        // acceptance-request's canonical audience is orchestrator; sending it as User is a mismatch.
        var envelope = Envelope(new GuidanceQuery(Guid.NewGuid(), "c", "q", null), ChannelAudience.User);

        var act = async () => await svc.EnqueueAsync(envelope);

        var ex = await act.Should().ThrowAsync<TammaError>();
        ex.Which.Code.Should().Be("CHANNEL.MESSAGE.INVALID");
    }

    [Test]
    public async Task Enqueue_TaskAssigned_FansOutOneRowPerApprovedRecipient()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        _audience.Members = new[] { new AudienceMember(userA, "senior_developer"), new AudienceMember(userB, "senior_developer") };

        var svc = Build();
        var task = new TaskAssigned(Guid.NewGuid(), Guid.NewGuid(), "senior_developer", "repo-access", "decomposition", Guid.NewGuid(), "issue-1", 70, null);
        var envelope = Envelope(task, ChannelAudience.User);

        await svc.EnqueueAsync(envelope);

        _outbox.Verify(r => r.EnqueueAsync(
            It.Is<ChannelOutboxMessage>(m => m.RecipientUserId == userA), It.IsAny<CancellationToken>()), Times.Once);
        _outbox.Verify(r => r.EnqueueAsync(
            It.Is<ChannelOutboxMessage>(m => m.RecipientUserId == userB), It.IsAny<CancellationToken>()), Times.Once);
        _outbox.Verify(r => r.EnqueueAsync(It.IsAny<ChannelOutboxMessage>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Test]
    public async Task Enqueue_TaskAssigned_NoApprovedRecipients_WritesNoRow()
    {
        _audience.Members = Array.Empty<AudienceMember>();
        var svc = Build();
        var task = new TaskAssigned(Guid.NewGuid(), Guid.NewGuid(), "senior_developer", "initiator", "decomposition", Guid.NewGuid(), "issue-1", 70, null);
        var envelope = Envelope(task, ChannelAudience.User);

        await svc.EnqueueAsync(envelope);

        _outbox.Verify(r => r.EnqueueAsync(It.IsAny<ChannelOutboxMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class FakeAudienceResolver : ITaskAudienceResolver
    {
        public IReadOnlyList<AudienceMember> Members { get; set; } = Array.Empty<AudienceMember>();
        public Task<bool> CanSeeAsync(Guid userId, TaskRef task) => Task.FromResult(Members.Any(m => m.UserId == userId));
        public Task<IReadOnlyList<AudienceMember>> EligibleAudienceAsync(TaskRef task, string roleWire) => Task.FromResult(Members);
    }
}
