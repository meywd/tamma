using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Models;
using Tamma.Core.Interfaces;

namespace Tamma.Activities.Tests.Integration;

/// <summary>
/// Story 38-3b (Epic 38, Class D) — the 12 domain activities that used to send Slack
/// directly via the co-hosted <see cref="IIntegrationService"/> (a rule-1 violation:
/// the engine holds no Slack credential) now route through the
/// <see cref="MediatedSlack"/> seam → <see cref="TammaApiClient.QueueSlackNotificationAsync"/>
/// → <c>POST /api/v1/notifications/slack</c> (fire-and-forget outbox).
///
/// <para>These tests cover: (1) the seam's request mapping (target/message/messageType/
/// action) + tenant passthrough + fail-soft (a false enqueue or an unwired client never
/// throws and returns false); (2) the per-activity cutover proof — the seven activities
/// whose ONLY external use was Slack no longer inject any credential-holding integration
/// service, while the five that also use <see cref="IIntegrationService"/> for non-Slack
/// (GitHub / CI / JIRA / email) legitimately retain it; and (3) the drift guard — ZERO
/// direct Slack calls remain anywhere under <c>Tamma.Activities</c> (mirrors the 38-1/38-3
/// grep gates), which the 38-4 guardrail depends on.</para>
/// </summary>
[TestFixture]
public class SlackCallerCutoverTests
{
    // A concrete TammaApiClient subclass capturing the enqueued intent. The seam made
    // QueueSlackNotificationAsync virtual (38-3), so no real HTTP leaves. The base ctor
    // gets a throwaway HttpClient + NullLogger.
    private sealed class CapturingSlackApiClient : TammaApiClient
    {
        public CapturingSlackApiClient()
            : base(new HttpClient(), NullLogger<TammaApiClient>.Instance, null, null) { }

        public SlackNotificationRequest? Captured { get; private set; }
        public string? CapturedTenant { get; private set; }
        public int Calls { get; private set; }
        public bool Result { get; set; } = true;

        public override Task<bool> QueueSlackNotificationAsync(
            SlackNotificationRequest request, string? tenantId = null, CancellationToken ct = default)
        {
            Calls++;
            Captured = request;
            CapturedTenant = tenantId;
            return Task.FromResult(Result);
        }
    }

    // ── Request mapping (AC5) ─────────────────────────────────────────────────

    [Test]
    public void BuildDirectMessage_RoutesToTargetUser_NoChannel()
    {
        var req = MediatedSlack.BuildDirectMessage("U9", "hi there", "Success", "SendDirect");

        req.UserId.Should().Be("U9");
        req.Channel.Should().BeNull("a DM has no channel target");
        req.Message.Should().Be("hi there", "the body is passed through unchanged (no re-formatting)");
        req.MessageType.Should().Be("Success");
        req.Action.Should().Be("SendDirect");
    }

    [Test]
    public void BuildChannelMessage_RoutesToChannel_NoUser()
    {
        var req = MediatedSlack.BuildChannelMessage("senior-review", "needs review", "Warning", "SendChannel");

        req.Channel.Should().Be("senior-review");
        req.UserId.Should().BeNull("a channel post has no DM target");
        req.Message.Should().Be("needs review");
        req.MessageType.Should().Be("Warning");
        req.Action.Should().Be("SendChannel");
    }

    [Test]
    public void Build_BlankMessageType_DefaultsToInfo()
    {
        MediatedSlack.BuildDirectMessage("U1", "m", "", "SendDirect").MessageType.Should().Be("Info");
        MediatedSlack.BuildChannelMessage("c", "m", "   ", "SendChannel").MessageType.Should().Be("Info");
    }

    // ── Enqueue + tenant passthrough (AC5) ────────────────────────────────────

    [Test]
    public async Task Enqueue_CallsQueueSlackNotification_WithRequestAndTenant()
    {
        var api = new CapturingSlackApiClient();
        var request = MediatedSlack.BuildDirectMessage("U9", "hi", "Info", "SendDirect");

        var ok = await MediatedSlack.EnqueueAsync(api, "tenant-42", request, CancellationToken.None);

        ok.Should().BeTrue();
        api.Calls.Should().Be(1);
        api.Captured.Should().BeSameAs(request);
        api.Captured!.UserId.Should().Be("U9");
        api.CapturedTenant.Should().Be("tenant-42", "the ambient tenant is forwarded as X-Tenant-Id scope");
    }

    // ── Fail-soft (AC10) ──────────────────────────────────────────────────────

    [Test]
    public async Task Enqueue_FalseResult_IsReturned_NotThrown()
    {
        var api = new CapturingSlackApiClient { Result = false };

        var ok = await MediatedSlack.EnqueueAsync(
            api, null, MediatedSlack.BuildChannelMessage("c", "m", "Info", "SendChannel"), CancellationToken.None);

        ok.Should().BeFalse("a failed enqueue is fail-soft — the caller stays fire-and-forget, never throws");
        api.Calls.Should().Be(1);
    }

    [Test]
    public async Task Enqueue_NullClient_ReturnsFalse_NeverThrows()
    {
        var ok = await MediatedSlack.EnqueueAsync(
            null, "t", MediatedSlack.BuildDirectMessage("U", "m", "Info", "SendDirect"), CancellationToken.None);

        ok.Should().BeFalse("an unwired engine client must not break a mentorship/review run");
    }

    // ── Per-activity cutover proof (AC6) ──────────────────────────────────────

    // The seven activities whose ONLY external call was Slack — they must inject NO
    // credential-holding integration service after the cutover.
    private static readonly Type[] SlackOnlyActivities =
    {
        typeof(Tamma.Activities.Mentorship.ProvideGuidanceActivity),
        typeof(Tamma.Activities.Mentorship.AssessJuniorCapabilityActivity),
        typeof(Tamma.Activities.Review.DeliverGuidanceActivity),
        typeof(Tamma.Activities.Review.ReRequestReviewActivity),
        typeof(Tamma.Activities.Review.RequestReviewActivity),
        typeof(Tamma.Activities.Review.EscalateReviewActivity),
        typeof(Tamma.Activities.Blocker.EscalateToSeniorActivity),
    };

    // NOTE: the four "non-Slack retainer" activities (MergeCompleteActivity,
    // DiagnoseBlockerActivity, MergeAndCompleteReviewActivity, DeliverQuestionsActivity) that
    // still held IIntegrationService for GitHub / CI / JIRA / email after 38-3b were fully cut
    // over to the thin TammaApiClient in Story 38 Phase 2 (Batch C). They now inject NO
    // IIntegrationService and are covered by IntegrationServiceCutoverTests instead — so the
    // former NonSlackRetainer_KeepsIntegrationService assertion was removed (no engine activity
    // retains the composite after Batch C).

    [Test]
    [TestCaseSource(nameof(SlackOnlyActivities))]
    public void SlackOnlyActivity_InjectsNoIntegrationService(Type activityType)
    {
        foreach (var ctor in activityType.GetConstructors())
        {
            ctor.GetParameters()
                .Any(p => IsIntegrationServiceType(p.ParameterType))
                .Should().BeFalse(
                    $"{activityType.Name} must not inject a Slack-credential integration service (ctor)");
        }

        activityType
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Any(f => IsIntegrationServiceType(f.FieldType))
            .Should().BeFalse(
                $"{activityType.Name} must hold no Slack-credential integration-service field");
    }

    private static bool IsIntegrationServiceType(Type t) =>
        typeof(IIntegrationService).IsAssignableFrom(t)
        || typeof(ISlackIntegrationService).IsAssignableFrom(t);

    // ── Drift guard: ZERO direct Slack calls remain under Tamma.Activities ─────

    [Test]
    public void TammaActivities_MakeNoDirectSlackCall()
    {
        var root = FindActivitiesRoot();
        root.Should().NotBeNull("the drift gate needs the Tamma.Activities source tree");

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root!, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root!, file).Replace('\\', '/');
            if (rel.StartsWith("obj/") || rel.StartsWith("bin/"))
                continue;

            var text = File.ReadAllText(file);
            if (text.Contains("SendSlackDirectMessageAsync", StringComparison.Ordinal)
                || text.Contains("SendSlackMessageAsync", StringComparison.Ordinal)
                || text.Contains("ISlackIntegrationService", StringComparison.Ordinal))
            {
                offenders.Add(rel);
            }
        }

        offenders.Should().BeEmpty(
            "no in-engine activity may send Slack directly — every Slack post routes through "
            + "MediatedSlack → TammaApiClient.QueueSlackNotificationAsync → POST /api/v1/notifications/slack. "
            + "Offending files: " + string.Join(", ", offenders));
    }

    private static string? FindActivitiesRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Tamma.Activities");
            if (Directory.Exists(candidate)) return candidate;

            var nested = Path.Combine(dir.FullName, "apps", "tamma-elsa", "src", "Tamma.Activities");
            if (Directory.Exists(nested)) return nested;

            dir = dir.Parent;
        }
        return null;
    }
}
