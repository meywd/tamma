using System.Reflection;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using Tamma.Activities.Integration;
using Tamma.Activities.LlmCall.Models;
using Tamma.Core.Entities;
using Tamma.Core.Interfaces;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Tests.Integration;

/// <summary>
/// Story 38-3 (AC5/AC6/AC10) — <c>SlackActivity</c> is now a THIN
/// <c>TammaApiClient</c> client. These tests cover the engine-side formatting +
/// plan mapping, the enqueue → <see cref="SlackOperationResult"/> projection
/// (outputs preserved, <c>Success</c> = enqueued), the kept <c>MentorshipEvent</c>
/// session-log write, the fail-soft null-enqueue path, and the cutover proof:
/// the activity injects no Slack-credential-holding integration service.
/// </summary>
[TestFixture]
public class SlackActivityThinClientTests
{
    private static SlackNotificationRequest? _captured;

    private static Func<SlackNotificationRequest, string?, CancellationToken, Task<bool>> Enqueue(bool result)
        => (req, _, _) => { _captured = req; return Task.FromResult(result); };

    [SetUp]
    public void Reset() => _captured = null;

    // ── BuildPlan formatting + routing (AC5) ──────────────────────────────────

    [Test]
    public void BuildPlan_SendChannel_EmojiPrefixesBody_RoutesToChannel()
    {
        var plan = SlackActivity.BuildPlan(SlackAction.SendChannel, "eng", null, "green", MessageType.Success)!;
        plan.Body.Should().Be(":white_check_mark: green");
        plan.Channel.Should().Be("eng");
        plan.TargetUserId.Should().BeNull();
        plan.Destination.Should().Be("eng");
        plan.WaitingForResponse.Should().BeFalse();
    }

    [Test]
    public void BuildPlan_SendDirect_RoutesToTargetUser()
    {
        var plan = SlackActivity.BuildPlan(SlackAction.SendDirect, null, "U9", "hi", MessageType.Info)!;
        plan.Body.Should().Be(":information_source: hi");
        plan.TargetUserId.Should().Be("U9");
        plan.Channel.Should().BeNull();
        plan.Destination.Should().Be("U9");
    }

    [Test]
    public void BuildPlan_SendAssessment_UsesAssessmentTemplate_And_WaitsForResponse()
    {
        var plan = SlackActivity.BuildPlan(SlackAction.SendAssessment, null, "U9", "Q1?", MessageType.Info)!;
        plan.Body.Should().Contain("**Tamma Assessment Request**").And.Contain("Q1?");
        plan.TargetUserId.Should().Be("U9");
        plan.WaitingForResponse.Should().BeTrue("the assessment action awaits the junior's reply");
    }

    [Test]
    public void BuildPlan_SendGuidance_UsesGuidanceTemplate()
    {
        var plan = SlackActivity.BuildPlan(SlackAction.SendGuidance, null, "U9", "do X", MessageType.Info)!;
        plan.Body.Should().Contain("**Tamma Guidance**").And.Contain("do X");
        plan.WaitingForResponse.Should().BeFalse();
    }

    [Test]
    public void BuildPlan_SendNotification_CarriesBothTargets()
    {
        var plan = SlackActivity.BuildPlan(SlackAction.SendNotification, "eng", "U9", "note", MessageType.Warning)!;
        plan.Channel.Should().Be("eng");
        plan.TargetUserId.Should().Be("U9");
        plan.Destination.Should().Be("U9");
    }

    // ── Slack control-char escaping (FIX 4 — mention/broadcast injection) ──────

    [Test]
    public void FormatMessage_EscapesBroadcastAndMentionTokens_LeavesOurPrefix()
    {
        var body = SlackActivity.FormatMessage("<!channel> ping <@U123> & <b>", MessageType.Info);

        body.Should().StartWith(":information_source: ", "our own emoji prefix is not escaped");
        body.Should().NotContain("<!channel>", "the broadcast token must be neutralized");
        body.Should().NotContain("<@U123>", "the mention token must be neutralized");
        body.Should().Contain("&lt;!channel&gt;");
        body.Should().Contain("&lt;@U123&gt;");
        body.Should().Contain("&amp;");
    }

    [Test]
    public void FormatAssessment_EscapesBroadcastToken_KeepsTemplateLabel()
    {
        var body = SlackActivity.FormatAssessment("please review <!here> now");

        body.Should().Contain("**Tamma Assessment Request**", "the template label is our own, not escaped");
        body.Should().NotContain("<!here>");
        body.Should().Contain("&lt;!here&gt;");
    }

    [Test]
    public void FormatGuidance_EscapesMentionToken()
    {
        var body = SlackActivity.FormatGuidance("ask <@U777> for help");

        body.Should().Contain("**Tamma Guidance**");
        body.Should().NotContain("<@U777>");
        body.Should().Contain("&lt;@U777&gt;");
    }

    // ── ExecuteCoreAsync enqueue + output projection (AC5) ─────────────────────

    [Test]
    public async Task ExecuteCore_Enqueues_MapsRequest_And_ProjectsSuccessOutputs()
    {
        var result = await SlackActivity.ExecuteCoreAsync(
            SlackAction.SendChannel, "eng", null, "green", MessageType.Success,
            sessionId: null, tenantId: "t-1", Enqueue(true), repository: null, CancellationToken.None);

        result.Success.Should().BeTrue("Success now means enqueued");
        result.Destination.Should().Be("eng");
        result.WaitingForResponse.Should().BeFalse();

        _captured.Should().NotBeNull();
        _captured!.Action.Should().Be("SendChannel");
        _captured.Channel.Should().Be("eng");
        _captured.Message.Should().Be(":white_check_mark: green");
        _captured.MessageType.Should().Be("Success");
    }

    [Test]
    public async Task ExecuteCore_Assessment_WaitingForResponse_TrueOnQueued()
    {
        var result = await SlackActivity.ExecuteCoreAsync(
            SlackAction.SendAssessment, null, "U9", "Q1?", MessageType.Info,
            sessionId: null, tenantId: null, Enqueue(true), repository: null, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.WaitingForResponse.Should().BeTrue();
        _captured!.Message.Should().Contain("Q1?");
    }

    [Test]
    public async Task ExecuteCore_WritesMentorshipEvent_OnQueuedSuccess_WithSession()
    {
        var sid = Guid.NewGuid();
        var repo = new Mock<IMentorshipSessionRepository>();
        repo.Setup(r => r.LogEventAsync(It.IsAny<MentorshipEvent>()))
            .ReturnsAsync((MentorshipEvent e) => e);

        var result = await SlackActivity.ExecuteCoreAsync(
            SlackAction.SendDirect, null, "U9", "hi", MessageType.Info,
            sessionId: sid, tenantId: null, Enqueue(true), repo.Object, CancellationToken.None);

        result.Success.Should().BeTrue();
        repo.Verify(r => r.LogEventAsync(
            It.Is<MentorshipEvent>(e => e.SessionId == sid && e.EventType == EventTypes.Info)),
            Times.Once, "the local session log is kept and fires on enqueue-success");
    }

    // ── Fail-soft (AC10) ──────────────────────────────────────────────────────

    [Test]
    public async Task ExecuteCore_FailSoft_WhenEnqueueReturnsFalse()
    {
        var sid = Guid.NewGuid();
        var repo = new Mock<IMentorshipSessionRepository>();

        var result = await SlackActivity.ExecuteCoreAsync(
            SlackAction.SendAssessment, null, "U9", "Q1?", MessageType.Info,
            sessionId: sid, tenantId: null, Enqueue(false), repo.Object, CancellationToken.None);

        result.Success.Should().BeFalse("a failed enqueue is fail-soft, not a throw");
        result.Message.Should().Be("Notification queue failed");
        result.WaitingForResponse.Should().BeFalse("do not wait when the enqueue failed");
        result.Destination.Should().Be("U9");

        repo.Verify(r => r.LogEventAsync(It.IsAny<MentorshipEvent>()), Times.Never,
            "no session log write when the notification wasn't queued");
    }

    // ── Cutover proof (AC6) ───────────────────────────────────────────────────

    [Test]
    public void SlackActivity_HasNoIntegrationServiceConstructorParameter()
    {
        foreach (var ctor in typeof(SlackActivity).GetConstructors())
        {
            ctor.GetParameters()
                .Any(p => p.ParameterType.Name == "IIntegrationService"
                       || typeof(ISlackIntegrationService).IsAssignableFrom(p.ParameterType))
                .Should().BeFalse("SlackActivity must not inject a Slack-credential integration service");
        }
    }

    [Test]
    public void SlackActivity_HasNoIntegrationServiceField()
    {
        typeof(SlackActivity)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Any(f => f.FieldType.Name == "IIntegrationService"
                   || typeof(ISlackIntegrationService).IsAssignableFrom(f.FieldType))
            .Should().BeFalse("SlackActivity must hold no Slack-credential integration-service field");
    }

    [Test]
    public void SlackActivitySource_MakesNoDirectSlackCall()
    {
        var root = FindActivitiesRoot();
        root.Should().NotBeNull();
        var text = File.ReadAllText(Path.Combine(root!, "Integration", "SlackActivity.cs"));

        text.Should().NotContain("GetService<IIntegrationService>");
        text.Should().NotContain("GetRequiredService<IIntegrationService>");
        text.Should().NotContain("SendSlackMessageAsync");
        text.Should().NotContain("SendSlackDirectMessageAsync");
        text.Should().NotContain("_integrationService");
    }

    private static string? FindActivitiesRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Tamma.Activities");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
