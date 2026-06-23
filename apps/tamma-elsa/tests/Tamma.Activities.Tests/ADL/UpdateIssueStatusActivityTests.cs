using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Tamma.Activities.ADL;

namespace Tamma.Activities.Tests.ADL;

/// <summary>
/// Story 2.10 build-out — unit coverage for the update-issue-status activity.
///
/// <para>The headline regression: a failed status update must surface a loud
/// <c>Failed</c> outcome (which the workflow routes to an
/// <c>ISSUE_STATUS.UPDATED.FAILED</c> emit), NOT be swallowed into a silent
/// success. Tests cover <see cref="UpdateIssueStatusActivity.ExecuteCoreAsync"/>
/// (happy / total-failure / partial-retry de-dup / exception), the PR-link body
/// composition, error classification, and <see cref="EmitIssueStatusEventActivity"/>'s
/// DCB mapping onto the durable drain. Follows the codebase pattern of testing the
/// testable static logic (no full Elsa ActivityExecutionContext).</para>
/// </summary>
[TestFixture]
public class UpdateIssueStatusActivityTests
{
    // ================================================================
    // Constructors
    // ================================================================

    [Test]
    public void UpdateIssueStatusActivity_JsonConstructor_ShouldNotThrow()
    {
        Action act = () => new UpdateIssueStatusActivity();
        act.Should().NotThrow();
    }

    [Test]
    public void EmitIssueStatusEventActivity_JsonConstructor_ShouldNotThrow()
    {
        Action act = () => new EmitIssueStatusEventActivity();
        act.Should().NotThrow();
    }

    [Test]
    public void EmitIssueStatusEventActivity_WithLogger_ShouldNotThrow()
    {
        var logger = new Mock<ILogger<EmitIssueStatusEventActivity>>();
        Action act = () => new EmitIssueStatusEventActivity(logger.Object);
        act.Should().NotThrow();
    }

    // ================================================================
    // ExecuteCoreAsync — happy path
    // ================================================================

    private static Mock<IIssueCallbackClient> AllOk()
    {
        var c = new Mock<IIssueCallbackClient>();
        c.Setup(x => x.PostCommentAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(IssueCallbackResult.Ok());
        c.Setup(x => x.AddLabelsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(IssueCallbackResult.Ok());
        c.Setup(x => x.RemoveLabelAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(IssueCallbackResult.Ok());
        return c;
    }

    [Test]
    public async Task ExecuteCore_HappyPath_PostsCommentAddsAndRemovesLabels_ReturnsUpdated()
    {
        var c = AllOk();

        var outcome = await UpdateIssueStatusActivity.ExecuteCoreAsync(
            c.Object, "o/r", 5, "Working on it",
            addLabels: new[] { "tamma-processing" },
            removeLabels: new[] { "tamma-queued" });

        outcome.Success.Should().BeTrue();
        outcome.ErrorCode.Should().BeNull();
        c.Verify(x => x.PostCommentAsync("o/r", 5, "Working on it", It.IsAny<CancellationToken>()), Times.Once);
        c.Verify(x => x.AddLabelsAsync("o/r", 5, It.Is<string[]>(l => l.Contains("tamma-processing")), It.IsAny<CancellationToken>()), Times.Once);
        c.Verify(x => x.RemoveLabelAsync("o/r", 5, "tamma-queued", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ExecuteCore_NoLabels_OnlyPostsComment()
    {
        var c = AllOk();

        var outcome = await UpdateIssueStatusActivity.ExecuteCoreAsync(
            c.Object, "o/r", 9, "Status only", addLabels: null, removeLabels: null);

        outcome.Success.Should().BeTrue();
        c.Verify(x => x.PostCommentAsync("o/r", 9, "Status only", It.IsAny<CancellationToken>()), Times.Once);
        c.Verify(x => x.AddLabelsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string[]>(), It.IsAny<CancellationToken>()), Times.Never);
        c.Verify(x => x.RemoveLabelAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ================================================================
    // ExecuteCoreAsync — the headline bug: no silent failure / no false success
    // ================================================================

    [Test]
    public async Task ExecuteCore_CommentFailsEveryAttempt_ReturnsFailed_NotSilentSuccess()
    {
        // The core regression test: a status update that fails on every attempt
        // must NOT be swallowed into a success — it must surface a Failed outcome.
        var c = new Mock<IIssueCallbackClient>();
        c.Setup(x => x.PostCommentAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(IssueCallbackResult.Fail("issue-comment 403"));

        var outcome = await UpdateIssueStatusActivity.ExecuteCoreAsync(
            c.Object, "o/r", 5, "Closing", addLabels: null, removeLabels: null);

        outcome.Success.Should().BeFalse("a failed status update must NOT report success (no false success)");
        outcome.ErrorCode.Should().Be("permission-denied");
        outcome.Error.Should().Contain("403");
    }

    [Test]
    public async Task ExecuteCore_CallbackThrowsEveryAttempt_ReturnsFailed_NeverThrows()
    {
        var c = new Mock<IIssueCallbackClient>();
        c.Setup(x => x.PostCommentAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("network down"));

        var outcome = await UpdateIssueStatusActivity.ExecuteCoreAsync(
            c.Object, "o/r", 5, "msg", addLabels: null, removeLabels: null);

        outcome.Success.Should().BeFalse("an exception must become a Failed outcome, never a throw or false success");
        outcome.ErrorCode.Should().Be("issue-update-failed");
    }

    [Test]
    public async Task ExecuteCore_AddLabelsFailEveryAttempt_ReturnsFailed()
    {
        var c = AllOk();
        c.Setup(x => x.AddLabelsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(IssueCallbackResult.Fail("issue-labels 503"));

        var outcome = await UpdateIssueStatusActivity.ExecuteCoreAsync(
            c.Object, "o/r", 5, "msg", addLabels: new[] { "x" }, removeLabels: null);

        outcome.Success.Should().BeFalse();
        outcome.ErrorCode.Should().Be("callback-unavailable");
    }

    // ================================================================
    // ExecuteCoreAsync — idempotency / de-dup (no duplicate comment on retry)
    // ================================================================

    [Test]
    public async Task ExecuteCore_CommentSucceedsThenLabelFailsOnce_DoesNotRepostComment()
    {
        // Comment succeeds attempt 1; add-labels fails attempt 1 then succeeds
        // attempt 2. The comment must be posted exactly ONCE (no duplicate-comment
        // hazard on the label-only retry).
        var c = new Mock<IIssueCallbackClient>();
        c.Setup(x => x.PostCommentAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(IssueCallbackResult.Ok());

        var addCalls = 0;
        c.Setup(x => x.AddLabelsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++addCalls == 1 ? IssueCallbackResult.Fail("issue-labels 500") : IssueCallbackResult.Ok());

        var outcome = await UpdateIssueStatusActivity.ExecuteCoreAsync(
            c.Object, "o/r", 5, "msg", addLabels: new[] { "x" }, removeLabels: null);

        outcome.Success.Should().BeTrue();
        c.Verify(x => x.PostCommentAsync("o/r", 5, "msg", It.IsAny<CancellationToken>()), Times.Once,
            "the comment must not be re-posted on a label-only retry (de-dup)");
        addCalls.Should().Be(2);
    }

    [Test]
    public async Task ExecuteCore_PartialRemovals_DoesNotRepeatCompletedRemovals()
    {
        // Two removals: first succeeds, second fails on attempt 1 then succeeds on
        // attempt 2. The first (completed) removal must not be repeated.
        var c = new Mock<IIssueCallbackClient>();
        c.Setup(x => x.PostCommentAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(IssueCallbackResult.Ok());

        var bCalls = 0;
        c.Setup(x => x.RemoveLabelAsync("o/r", 5, "a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(IssueCallbackResult.Ok());
        c.Setup(x => x.RemoveLabelAsync("o/r", 5, "b", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++bCalls == 1 ? IssueCallbackResult.Fail("issue-labels-delete 500") : IssueCallbackResult.Ok());

        var outcome = await UpdateIssueStatusActivity.ExecuteCoreAsync(
            c.Object, "o/r", 5, "msg", addLabels: null, removeLabels: new[] { "a", "b" });

        outcome.Success.Should().BeTrue();
        c.Verify(x => x.RemoveLabelAsync("o/r", 5, "a", It.IsAny<CancellationToken>()), Times.Once,
            "a completed removal must not be repeated on retry");
        bCalls.Should().Be(2);
    }

    // ================================================================
    // ComposeBody — PR-link composition (Story 2.10 AC5)
    // ================================================================

    [Test]
    public void ComposeBody_NoPr_ReturnsMessageOnly()
    {
        UpdateIssueStatusActivity.ComposeBody("Done!", prNumber: 0, prUrl: null)
            .Should().Be("Done!");
    }

    [Test]
    public void ComposeBody_WithPrNumberAndUrl_LinksMergedPr()
    {
        var body = UpdateIssueStatusActivity.ComposeBody(
            "🎉 PR merged! Issue resolved.", prNumber: 42, prUrl: "https://x/pull/42");

        body.Should().Contain("🎉 PR merged! Issue resolved.");
        body.Should().Contain("Resolved by #42");
        body.Should().Contain("https://x/pull/42");
    }

    [Test]
    public void ComposeBody_WithPrNumberOnly_LinksNumber()
    {
        UpdateIssueStatusActivity.ComposeBody("Closed", prNumber: 7, prUrl: null)
            .Should().Contain("Resolved by #7");
    }

    [Test]
    public void ComposeBody_EmptyMessageWithPr_StillLinks_NeverEmpty()
    {
        var body = UpdateIssueStatusActivity.ComposeBody("", prNumber: 3, prUrl: "u");
        body.Should().NotBeNullOrWhiteSpace();
        body.Should().Contain("Resolved by #3");
    }

    // ================================================================
    // ClassifyError — drives the failure-edge errorCode
    // ================================================================

    [Test]
    public void ClassifyError_MapsKnownCodes()
    {
        UpdateIssueStatusActivity.ClassifyError("issue-comment 404").Should().Be("issue-not-found");
        UpdateIssueStatusActivity.ClassifyError("403 Forbidden").Should().Be("permission-denied");
        UpdateIssueStatusActivity.ClassifyError("401 Unauthorized").Should().Be("unauthorized");
        UpdateIssueStatusActivity.ClassifyError("429 rate limit").Should().Be("rate-limited");
        UpdateIssueStatusActivity.ClassifyError("503 not_configured").Should().Be("callback-unavailable");
    }

    [Test]
    public void ClassifyError_GenericFallback()
    {
        UpdateIssueStatusActivity.ClassifyError(null).Should().Be("issue-update-failed");
        UpdateIssueStatusActivity.ClassifyError("boom").Should().Be("issue-update-failed");
    }

    // ================================================================
    // EmitIssueStatusEventActivity.BuildTammaEvent — DCB mapping onto the drain
    // ================================================================

    [Test]
    public void BuildTammaEvent_SuccessType_SetsTypeStatusTagsAndData()
    {
        var evt = EmitIssueStatusEventActivity.BuildTammaEvent(
            IssueStatusEvents.UpdatedSuccess, issueNumber: 12, repository: "o/r",
            tenantId: null,
            data: new Dictionary<string, object?> { ["message"] = "ok", ["degraded"] = false });

        evt.EventType.Should().Be("ISSUE_STATUS.UPDATED.SUCCESS");
        evt.Status.Should().Be("success");
        evt.Tags.Should().NotBeNull();
        evt.Tags!["issueId"].Should().Be("12");
        evt.Tags["issueNumber"].Should().Be("12");
        evt.Tags["repository"].Should().Be("o/r");
        evt.Tags.Should().NotContainKey("tenantId");
        evt.Data.Should().ContainKey("message");
    }

    [Test]
    public void BuildTammaEvent_FailedType_SetsErrorStatus()
    {
        var evt = EmitIssueStatusEventActivity.BuildTammaEvent(
            IssueStatusEvents.UpdatedFailed, issueNumber: 7, repository: "o/r",
            tenantId: null,
            data: new Dictionary<string, object?> { ["errorCode"] = "permission-denied" });

        evt.EventType.Should().Be("ISSUE_STATUS.UPDATED.FAILED");
        evt.Status.Should().Be("error", "a failed update must emit a loud error-status event");
        evt.Data.Should().ContainKey("errorCode");
    }

    [Test]
    public void BuildTammaEvent_WithTenant_SetsTenantIdTag()
    {
        var tenant = Guid.NewGuid();
        var evt = EmitIssueStatusEventActivity.BuildTammaEvent(
            IssueStatusEvents.UpdatedSuccess, 1, "o/r", tenantId: tenant, data: null);

        evt.Tags!["tenantId"].Should().Be(tenant.ToString("D"));
        evt.Data.Should().BeEmpty();
    }

    [Test]
    public void BuildTammaEvent_DegradedSuccess_AddsDegradedTag_NotJustData()
    {
        // A degraded (callback-unavailable) no-op still emits a SUCCESS event. A
        // consumer filtering on event TYPE would miscount it as a real success
        // unless the degraded flag is a QUERYABLE tag — not buried in Data only.
        var evt = EmitIssueStatusEventActivity.BuildTammaEvent(
            IssueStatusEvents.UpdatedSuccess, issueNumber: 8, repository: "o/r",
            tenantId: null,
            data: new Dictionary<string, object?> { ["message"] = "noop", ["degraded"] = true });

        evt.Tags.Should().NotBeNull();
        evt.Tags!.Should().ContainKey("degraded");
        evt.Tags["degraded"].Should().Be("true", "the degraded flag must be indexed/queryable as a tag");
        // still present in Data (non-indexed payload retained).
        evt.Data.Should().ContainKey("degraded");
    }

    [Test]
    public void BuildTammaEvent_NonDegradedSuccess_DoesNotAddDegradedTag()
    {
        var evt = EmitIssueStatusEventActivity.BuildTammaEvent(
            IssueStatusEvents.UpdatedSuccess, issueNumber: 8, repository: "o/r",
            tenantId: null,
            data: new Dictionary<string, object?> { ["message"] = "ok", ["degraded"] = false });

        evt.Tags!.Should().NotContainKey("degraded",
            "a genuine (non-degraded) success must NOT carry the degraded tag");
    }

    [Test]
    public void IssueStatusEvents_ParseTenantId_HandlesEmptyAndValid()
    {
        IssueStatusEvents.ParseTenantId(null).Should().BeNull();
        IssueStatusEvents.ParseTenantId("").Should().BeNull();
        IssueStatusEvents.ParseTenantId("not-a-guid").Should().BeNull();
        var g = Guid.NewGuid();
        IssueStatusEvents.ParseTenantId(g.ToString()).Should().Be(g);
    }

    [Test]
    public void EmitIssueStatusEvent_ParseData_HandlesEmptyMalformedAndValid()
    {
        EmitIssueStatusEventActivity.ParseData(null).Should().BeNull();
        EmitIssueStatusEventActivity.ParseData("").Should().BeNull();
        EmitIssueStatusEventActivity.ParseData("{not json").Should().BeNull();

        var data = EmitIssueStatusEventActivity.ParseData("{\"errorCode\":\"x\",\"durationMs\":3}");
        data.Should().NotBeNull();
        data!.Should().ContainKey("errorCode");
        data.Should().ContainKey("durationMs");
    }

    // ================================================================
    // IssueCallbackResult
    // ================================================================

    [Test]
    public void IssueCallbackResult_OkAndFail_AreDistinct()
    {
        IssueCallbackResult.Ok().Success.Should().BeTrue();
        var fail = IssueCallbackResult.Fail("boom");
        fail.Success.Should().BeFalse();
        fail.Error.Should().Be("boom");
    }
}
