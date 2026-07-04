using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Tamma.Activities.Review;
using Tamma.Activities.Review.Models;

namespace Tamma.Activities.Tests.Review;

/// <summary>
/// Completeness build-out 2026-06-22 (<c>CodeReview.md</c>, Story 7-1D) — coverage for the
/// code-review correctness + observability fixes:
///   - the <c>CODE_REVIEW.*</c> DCB event catalogue + status convention (no false success, #8),
///   - <see cref="EmitCodeReviewEventActivity.BuildTammaEvent"/> tag/data mapping (#8),
///   - <see cref="BindCodeReviewConfigActivity.Resolve"/> config precedence + the
///     FixTimeoutMinutes→hours drift fix (#9),
///   - <see cref="ValidateCodeReviewInputsActivity.Validate"/> specific-failure validation (#3).
/// </summary>
[TestFixture]
public class CodeReviewEventTests
{
    // ================================================================
    // CodeReviewEvents — type catalogue + status convention (#8)
    // ================================================================

    [Test]
    public void EventTypes_FollowAggregateActionStatusConvention()
    {
        CodeReviewEvents.PrCreatedSuccess.Should().Be("CODE_REVIEW.PR_CREATED.SUCCESS");
        CodeReviewEvents.PrCreatedFailed.Should().Be("CODE_REVIEW.PR_CREATED.FAILED");
        CodeReviewEvents.GuidanceDeliveredSuccess.Should().Be("CODE_REVIEW.GUIDANCE_DELIVERED.SUCCESS");
        CodeReviewEvents.GuidanceDeliveredFailed.Should().Be("CODE_REVIEW.GUIDANCE_DELIVERED.FAILED");
        CodeReviewEvents.IterationStarted.Should().Be("CODE_REVIEW.ITERATION.STARTED");
        CodeReviewEvents.MergedSuccess.Should().Be("CODE_REVIEW.MERGED.SUCCESS");
        CodeReviewEvents.MergedFailed.Should().Be("CODE_REVIEW.MERGED.FAILED");
        CodeReviewEvents.Escalated.Should().Be("CODE_REVIEW.ESCALATED");
        // Story 4-6 — escalation resolution companion to the raise-time ESCALATED.
        CodeReviewEvents.EscalationResolved.Should().Be("CODE_REVIEW.ESCALATION_RESOLVED");
        CodeReviewEvents.Failed.Should().Be("CODE_REVIEW.FAILED");
    }

    [Test]
    public void StatusForEvent_FailuresAreError_RestAreSuccess()
    {
        CodeReviewEvents.StatusForEvent(CodeReviewEvents.PrCreatedFailed).Should().Be("error");
        CodeReviewEvents.StatusForEvent(CodeReviewEvents.GuidanceDeliveredFailed).Should().Be("error");
        CodeReviewEvents.StatusForEvent(CodeReviewEvents.MergedFailed).Should().Be("error");
        CodeReviewEvents.StatusForEvent(CodeReviewEvents.Failed).Should().Be("error");

        CodeReviewEvents.StatusForEvent(CodeReviewEvents.PrCreatedSuccess).Should().Be("success");
        CodeReviewEvents.StatusForEvent(CodeReviewEvents.GuidanceDeliveredSuccess).Should().Be("success");
        CodeReviewEvents.StatusForEvent(CodeReviewEvents.IterationStarted).Should().Be("success");
        CodeReviewEvents.StatusForEvent(CodeReviewEvents.MergedSuccess).Should().Be("success");
        // An escalation is an expected, auditable hand-off — success-status (the rejection
        // that may follow is recorded as CODE_REVIEW.FAILED).
        CodeReviewEvents.StatusForEvent(CodeReviewEvents.Escalated).Should().Be("success");
        // Story 4-6 — an escalation RESOLVED by the senior is a normal (success) audit row.
        CodeReviewEvents.StatusForEvent(CodeReviewEvents.EscalationResolved).Should().Be("success");
    }

    // ================================================================
    // Story 4-6 — escalation RAISE / RESOLVE DCB event mapping
    // ================================================================

    [Test]
    public void BuildTammaEvent_EscalationRaised_HasSuccessStatusTagsAndReasonData()
    {
        // The event EscalateReviewActivity pushes into tamma:events at the RAISE point
        // (before the suspending senior-wait). Success-status: the raise is an auditable
        // hand-off, not a failure.
        var tenant = Guid.NewGuid();
        var evt = EmitCodeReviewEventActivity.BuildTammaEvent(
            CodeReviewEvents.Escalated,
            sessionId: "sess-9", storyId: "story-9", juniorId: "junior-9", tenantId: tenant,
            prNumber: 42, prUrl: null, iteration: 3,
            mergeSha: null, reason: "MaxIterationsReached",
            detail: "Maximum fix iterations reached during code review.");

        evt.EventType.Should().Be("CODE_REVIEW.ESCALATED");
        evt.Status.Should().Be("success");
        evt.Tags!["sessionId"].Should().Be("sess-9");
        evt.Tags["storyId"].Should().Be("story-9");
        evt.Tags["juniorId"].Should().Be("junior-9");
        evt.Tags["prId"].Should().Be("42");
        evt.Tags["tenantId"].Should().Be(tenant.ToString("D"));
        evt.Data["reason"].Should().Be("MaxIterationsReached");
    }

    [Test]
    public void BuildTammaEvent_EscalationResolved_IsSuccess_DistinctFromEscalated()
    {
        // The RESOLVE companion emitted on the Resolved→merge edge.
        var evt = EmitCodeReviewEventActivity.BuildTammaEvent(
            CodeReviewEvents.EscalationResolved,
            sessionId: "sess-9", storyId: "story-9", juniorId: "junior-9", tenantId: null,
            prNumber: 42, prUrl: null, iteration: 3,
            mergeSha: null, reason: null,
            detail: "Escalation resolved by senior developer.");

        evt.EventType.Should().Be("CODE_REVIEW.ESCALATION_RESOLVED");
        evt.EventType.Should().NotBe(CodeReviewEvents.Escalated);
        evt.Status.Should().Be("success");
    }

    [Test]
    public void ParseTenantId_ValidGuid_Parses_InvalidOrEmpty_IsNull()
    {
        var g = Guid.NewGuid();
        CodeReviewEvents.ParseTenantId(g.ToString()).Should().Be(g);
        CodeReviewEvents.ParseTenantId("").Should().BeNull();
        CodeReviewEvents.ParseTenantId(null).Should().BeNull();
        CodeReviewEvents.ParseTenantId("not-a-guid").Should().BeNull();
    }

    // ================================================================
    // EmitCodeReviewEventActivity.BuildTammaEvent — tags + data + status
    // ================================================================

    [Test]
    public void BuildTammaEvent_PrCreated_StampsTagsAndPrData_SuccessStatus()
    {
        var evt = EmitCodeReviewEventActivity.BuildTammaEvent(
            CodeReviewEvents.PrCreatedSuccess,
            sessionId: "sess-1", storyId: "story-7", juniorId: "junior-9", tenantId: null,
            prNumber: 42, prUrl: "https://github.com/o/r/pull/42", iteration: 0,
            mergeSha: null, reason: null, detail: null);

        evt.EventType.Should().Be("CODE_REVIEW.PR_CREATED.SUCCESS");
        evt.Status.Should().Be("success");
        evt.Tags!["sessionId"].Should().Be("sess-1");
        evt.Tags!["storyId"].Should().Be("story-7");
        evt.Tags!["juniorId"].Should().Be("junior-9");
        evt.Tags!["prId"].Should().Be("42");
        evt.Tags.Should().NotContainKey("tenantId", "single-user / platform-scope event");
        evt.Data["prNumber"].Should().Be(42);
        evt.Data["prUrl"].Should().Be("https://github.com/o/r/pull/42");
    }

    [Test]
    public void BuildTammaEvent_WithTenant_StampsTenantTag_AndIteration()
    {
        var tenant = Guid.NewGuid();
        var evt = EmitCodeReviewEventActivity.BuildTammaEvent(
            CodeReviewEvents.IterationStarted,
            "sess-1", "story-1", "junior-1", tenant,
            prNumber: 7, prUrl: null, iteration: 2,
            mergeSha: null, reason: null, detail: null);

        evt.Tags!["tenantId"].Should().Be(tenant.ToString("D"));
        evt.Tags!["iteration"].Should().Be("2");
        evt.Data["iteration"].Should().Be(2);
    }

    [Test]
    public void BuildTammaEvent_Merged_CarriesShaAndStrategy()
    {
        var evt = EmitCodeReviewEventActivity.BuildTammaEvent(
            CodeReviewEvents.MergedSuccess,
            "sess-1", "story-1", "junior-1", null,
            prNumber: 7, prUrl: null, iteration: 3,
            mergeSha: "abc123", reason: null, detail: null);

        evt.Status.Should().Be("success");
        evt.Data["mergeSha"].Should().Be("abc123");
    }

    [Test]
    public void BuildTammaEvent_Failed_IsErrorStatus_WithDetailOnError_NotFalseSuccess()
    {
        var evt = EmitCodeReviewEventActivity.BuildTammaEvent(
            CodeReviewEvents.Failed,
            "sess-1", "story-1", "junior-1", null,
            prNumber: 0, prUrl: null, iteration: 0,
            mergeSha: null, reason: null, detail: "missing storyId");

        evt.EventType.Should().Be("CODE_REVIEW.FAILED");
        evt.Status.Should().Be("error");
        evt.Error.Should().Be("missing storyId");
        evt.Data["detail"].Should().Be("missing storyId");
    }

    [Test]
    public void BuildTammaEvent_OmitsEmptyTagsAndZeroData()
    {
        var evt = EmitCodeReviewEventActivity.BuildTammaEvent(
            CodeReviewEvents.Escalated,
            sessionId: "", storyId: "", juniorId: "", tenantId: null,
            prNumber: 0, prUrl: "", iteration: 0,
            mergeSha: "", reason: "", detail: "");

        evt.Tags!.Should().NotContainKey("sessionId");
        evt.Tags!.Should().NotContainKey("prId");
        evt.Tags!.Should().NotContainKey("iteration");
        evt.Data.Should().NotContainKey("prNumber");
        evt.Data.Should().NotContainKey("mergeSha");
    }

    // ================================================================
    // BindCodeReviewConfigActivity.Resolve — config precedence + drift fix (#9)
    // ================================================================

    private static IConfiguration Config(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Test]
    public void Resolve_Defaults_WhenNoInputNoConfig()
    {
        var r = BindCodeReviewConfigActivity.Resolve(configuration: null, maxIterationsInput: 0, mergeStrategyInput: null);

        r.MaxIterations.Should().Be(5);
        r.MergeStrategy.Should().Be(MergeStrategy.Squash);
        r.ReviewTimeoutHours.Should().Be(24);
        // FixTimeoutMinutes default 60 → 1h (NOT the 24h review timeout — the drift fix).
        r.FixTimeoutHours.Should().Be(1);
        r.VerifyCIBeforeMerge.Should().BeTrue();
        r.DeleteBranchAfterMerge.Should().BeTrue();
    }

    [Test]
    public void Resolve_InputOverridesConfig_ForMaxIterAndStrategy()
    {
        var cfg = Config(new()
        {
            ["CodeReview:MaxReviewIterations"] = "3",
            ["CodeReview:MergeStrategy"] = "Merge",
        });

        var r = BindCodeReviewConfigActivity.Resolve(cfg, maxIterationsInput: 8, mergeStrategyInput: "Rebase");

        r.MaxIterations.Should().Be(8, "explicit input wins over config");
        r.MergeStrategy.Should().Be(MergeStrategy.Rebase, "explicit input wins over config");
    }

    [Test]
    public void Resolve_ConfigUsedWhenNoInput()
    {
        var cfg = Config(new()
        {
            ["CodeReview:MaxReviewIterations"] = "7",
            ["CodeReview:MergeStrategy"] = "merge",
            ["CodeReview:ReviewTimeoutHours"] = "48",
            ["CodeReview:FixTimeoutMinutes"] = "120",
            ["CodeReview:VerifyCIBeforeMerge"] = "false",
            ["CodeReview:DeleteBranchAfterMerge"] = "false",
        });

        var r = BindCodeReviewConfigActivity.Resolve(cfg, maxIterationsInput: 0, mergeStrategyInput: null);

        r.MaxIterations.Should().Be(7);
        r.MergeStrategy.Should().Be(MergeStrategy.Merge);
        r.ReviewTimeoutHours.Should().Be(48);
        r.FixTimeoutHours.Should().Be(2, "120 minutes / 60 = 2 hours");
        r.VerifyCIBeforeMerge.Should().BeFalse();
        r.DeleteBranchAfterMerge.Should().BeFalse();
    }

    [Test]
    public void Resolve_FixTimeoutHasOneHourFloor()
    {
        var cfg = Config(new() { ["CodeReview:FixTimeoutMinutes"] = "30" });
        var r = BindCodeReviewConfigActivity.Resolve(cfg, 0, null);
        r.FixTimeoutHours.Should().Be(1, "sub-hour fix timeouts floor at 1h for the hour-granular durable wait");
    }

    // ================================================================
    // ValidateCodeReviewInputsActivity.Validate — specific failures (#3)
    // ================================================================

    [Test]
    public void Validate_AllPresentWithExplicitReviewers_IsValid()
    {
        var r = ValidateCodeReviewInputsActivity.Validate(
            storyId: "S-1", repositoryUrl: "https://github.com/o/r", juniorId: "J-1",
            reviewerIdsJson: "[\"alice\",\"bob\"]", reviewerPool: Array.Empty<string>());

        r.IsValid.Should().BeTrue();
        r.Reviewers.Should().BeEquivalentTo(new[] { "alice", "bob" });
        r.ErrorMessage.Should().BeEmpty();
    }

    [Test]
    public void Validate_FallsBackToPool_WhenNoExplicitReviewers()
    {
        var r = ValidateCodeReviewInputsActivity.Validate(
            "S-1", "https://github.com/o/r", "J-1",
            reviewerIdsJson: null, reviewerPool: new[] { "carol" });

        r.IsValid.Should().BeTrue();
        r.Reviewers.Should().ContainSingle().Which.Should().Be("carol");
    }

    [TestCase(null, "repo", "j", "storyId")]
    [TestCase("", "repo", "j", "storyId")]
    [TestCase("s", null, "j", "repositoryUrl")]
    [TestCase("s", "repo", null, "juniorId")]
    public void Validate_MissingRequiredInput_IsInvalid_WithSpecificMessage(
        string? story, string? repo, string? junior, string expectedToken)
    {
        var r = ValidateCodeReviewInputsActivity.Validate(
            story, repo, junior, "[\"alice\"]", Array.Empty<string>());

        r.IsValid.Should().BeFalse();
        r.ErrorMessage.Should().NotBeEmpty();
        r.ErrorMessage.Should().Contain(expectedToken);
        r.ErrorMessage.Should().NotContain("Code review failed", "no generic message — must be specific (#3)");
    }

    [Test]
    public void Validate_NoResolvableReviewer_IsInvalid_WithSpecificMessage()
    {
        var r = ValidateCodeReviewInputsActivity.Validate(
            "S-1", "https://github.com/o/r", "J-1",
            reviewerIdsJson: "[]", reviewerPool: Array.Empty<string>());

        r.IsValid.Should().BeFalse();
        r.ErrorMessage.Should().Contain("no reviewer resolvable");
        r.ErrorMessage.Should().Contain("ReviewerPool");
    }

    [Test]
    public void Validate_SingleIssueCyclePayloadShape_IsInvalid_NotSilentlyDropped()
    {
        // The autonomous-loop dispatch sends {repository, prNumber, branchName, tenantId} with
        // no storyId/juniorId. It must FAIL with a clear reason here (deferred #2) — never a
        // silent no-op that proceeds to create a PR for an empty story.
        var r = ValidateCodeReviewInputsActivity.Validate(
            storyId: null, repositoryUrl: "https://github.com/o/r", juniorId: null,
            reviewerIdsJson: null, reviewerPool: Array.Empty<string>());

        r.IsValid.Should().BeFalse();
        r.ErrorMessage.Should().Contain("storyId");
        r.ErrorMessage.Should().Contain("juniorId");
        r.ErrorMessage.Should().Contain("autonomous-loop", "the message points the misrouted payload at the right workflow");
    }
}
