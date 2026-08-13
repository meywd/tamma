using FluentAssertions;
using Moq;
using NUnit.Framework;
using Tamma.Activities.ADL;
using Tamma.ElsaServer.Workflows;
using Tamma.Platforms.Abstractions;
using PModels = Tamma.Platforms.Abstractions.Models;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Epic 31 P5 M2 — the remaining §4 degradation pairs (DG-2 / DG-3 / DG-4),
/// mirroring the DG-1/DG-7 exemplars (<see cref="PrReadyBeforeMergeGateTests"/>):
///
/// <list type="bullet">
///   <item><b>DG-3 reviewer request</b> — the cycle's only reviewer use is the
///   PR step (pull-request workflow → CreatePullRequestActivity's post-create
///   metadata). The workflow carries the §4 check step
///   (CheckReviewersSupported) before the PR step whenever reviewers are
///   actually requested; the alternative step emits GIT.PR_REVIEWERS.SKIPPED
///   and clears the reviewers input. The mediation core is the §4.3 safety
///   net: a runtime reviewer refusal is captured, the PR labeled
///   needs-reviewer, and the SAME audit event emitted — the PR step never
///   fails on it.</item>
///   <item><b>DG-4 merge method</b> — the merge core consumes EXACTLY the
///   typed <c>merge_method_unsupported</c> code (GitLab's rebase answers it)
///   and falls back along the fixed order rebase→squash→merge, audited via
///   GIT.PR_MERGE.METHOD_FALLBACK at the mediation layer. There is no
///   per-method capability flag to probe, so the typed code IS the check;
///   fail loud only when no method works.</item>
///   <item><b>DG-2 review-comment anchoring</b> — mediation-level (see
///   GitMediationServiceTests): capability check before the anchored post +
///   downgrade-to-plain-comment on the typed refusal / anchor rejection.</item>
/// </list>
/// </summary>
[TestFixture]
public class DegradationPairsTests
{
    // ================================================================
    // DG-3 — workflow structure (the check step + alternative step)
    // ================================================================

    [Test]
    public void PullRequestWorkflow_CarriesTheReviewerCheckStep_BeforeThePrStep()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new PullRequestWorkflow());
        var flowchart = WorkflowTestHelper.GetFlowchart(builder);

        var hasReviewers = flowchart.Activities.Single(a => a.Id == "HasReviewers");
        var check = flowchart.Activities.OfType<CheckPlatformCapabilityActivity>()
            .Single(a => a.Id == "CheckReviewersSupported");
        var createPr = flowchart.Activities.OfType<CreatePullRequestActivity>()
            .Single(a => a.Id == "CreatePR");

        // Reviewers requested → the §4 check step; nothing requested → the PR
        // step directly (no capability-gated action is being asked for).
        flowchart.Connections.Should().Contain(
            c => c.Source.Activity == hasReviewers && c.Source.Port == "True" && c.Target.Activity == check,
            "a reviewer request is capability-gated and must pass the check step first");
        flowchart.Connections.Should().Contain(
            c => c.Source.Activity == hasReviewers && c.Source.Port == "False" && c.Target.Activity == createPr);

        // Supported → the PR step runs with reviewers exactly as today.
        flowchart.Connections.Should().Contain(
            c => c.Source.Activity == check && c.Source.Port == "Supported" && c.Target.Activity == createPr);
    }

    [Test]
    public void PullRequestWorkflow_ReviewerCheckUnsupported_RoutesToAuditedSkip_ThenThePrStep()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new PullRequestWorkflow());
        var flowchart = WorkflowTestHelper.GetFlowchart(builder);

        var check = flowchart.Activities.OfType<CheckPlatformCapabilityActivity>()
            .Single(a => a.Id == "CheckReviewersSupported");
        var skipped = flowchart.Activities.OfType<EmitPrEventActivity>()
            .Single(a => a.Id == "MarkReviewersSkipped");
        var clear = flowchart.Activities.Single(a => a.Id == "ClearReviewers");
        var createPr = flowchart.Activities.Single(a => a.Id == "CreatePR");

        flowchart.Connections.Should().Contain(
            c => c.Source.Activity == check && c.Source.Port == "Unsupported" && c.Target.Activity == skipped,
            "the Unsupported edge routes to the defined alternative step — an audited skip, never a failure");
        flowchart.Connections.Should().Contain(
            c => c.Source.Activity == skipped && c.Target.Activity == clear,
            "after the audit event the reviewers input is cleared");
        flowchart.Connections.Should().Contain(
            c => c.Source.Activity == clear && c.Target.Activity == createPr,
            "the PR step still runs — DG-3 never fails the PR step");

        // Unsupported must not route to the failure path.
        flowchart.Connections.Should().NotContain(
            c => c.Source.Activity == check && c.Source.Port == "Unsupported" && c.Target.Activity.Id == "FailureOutputs");
    }

    [Test]
    public void PullRequestWorkflow_ReviewerCount_GatesTheCheckStep()
    {
        PullRequestWorkflow.ParseReviewerCount(null).Should().Be(0);
        PullRequestWorkflow.ParseReviewerCount("").Should().Be(0);
        PullRequestWorkflow.ParseReviewerCount("[]").Should().Be(0);
        PullRequestWorkflow.ParseReviewerCount("not json").Should().Be(0);
        PullRequestWorkflow.ParseReviewerCount("""["alice","bob"]""").Should().Be(2);
    }

    // ================================================================
    // DG-3 — the safety net in the PR core (mediation-side capture)
    // ================================================================

    private static PModels.PullRequest Pr(string number = "15") =>
        new(number, "t", null, "feature", "main", PModels.PullRequestState.Open,
            false, "https://x/pr/15", "bot", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static PlatformResult<T> Ok<T>(T v) => PlatformResult<T>.FromOk(v);
    private static PlatformResult<T> Fail<T>(PlatformError e) => PlatformResult<T>.FromError(e);

    [Test]
    public async Task PrCore_ReviewerRefusal_IsCaptured_Labeled_AndNeverFailsThePrStep()
    {
        var client = new Mock<IGitPlatformClient>();
        client.Setup(c => c.ListOpenPullRequestsForBranchAsync("o", "r", "feature", "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok<IReadOnlyList<PModels.PullRequest>>(Array.Empty<PModels.PullRequest>()));
        client.Setup(c => c.OpenPullRequestAsync(It.IsAny<PModels.OpenPullRequestRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Pr()));
        client.Setup(c => c.RequestReviewersAsync(It.IsAny<PModels.RequestReviewersRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<PModels.PullRequest>(new PlatformError.InvalidRequest(
                "capability_unsupported", "no reviewer API")));
        var labelCalls = new List<PModels.AddPullRequestLabelsRequest>();
        client.Setup(c => c.AddPullRequestLabelsAsync(It.IsAny<PModels.AddPullRequestLabelsRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PModels.AddPullRequestLabelsRequest, CancellationToken>((r, _) => labelCalls.Add(r))
            .ReturnsAsync(Ok(Pr()));

        var outcome = await CreatePullRequestActivity.ExecuteCoreAsync(
            client.Object, "o/r", "feature", "main", draft: false,
            new Tamma.Core.Interfaces.CreatePullRequestRequest
            {
                Title = "t", Body = "b", Head = "feature", Base = "main",
                Reviewers = new List<string> { "alice" },
            });

        outcome.Outcome.Should().Be("Created", "a reviewer refusal must never fail the PR step (DG-3)");
        outcome.ReviewersSkipped.Should().BeTrue();
        outcome.ReviewersSkipReason.Should().Be("capability_unsupported", "§4.5 — the exact code is preserved");
        labelCalls.Should().ContainSingle()
            .Which.Labels.Should().Contain(CreatePullRequestActivity.ReviewersSkippedLabel);
    }

    [Test]
    public async Task PrCore_NoReviewersRequested_RecordsNoSkip()
    {
        var client = new Mock<IGitPlatformClient>();
        client.Setup(c => c.ListOpenPullRequestsForBranchAsync("o", "r", "feature", "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok<IReadOnlyList<PModels.PullRequest>>(Array.Empty<PModels.PullRequest>()));
        client.Setup(c => c.OpenPullRequestAsync(It.IsAny<PModels.OpenPullRequestRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Pr()));

        var outcome = await CreatePullRequestActivity.ExecuteCoreAsync(
            client.Object, "o/r", "feature", "main", draft: false,
            new Tamma.Core.Interfaces.CreatePullRequestRequest
            {
                Title = "t", Body = "b", Head = "feature", Base = "main",
            });

        outcome.Outcome.Should().Be("Created");
        outcome.ReviewersSkipped.Should().BeFalse();
        client.Verify(c => c.RequestReviewersAsync(
            It.IsAny<PModels.RequestReviewersRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public void ClassifyReviewerSkip_PreservesExactCode_AndClassifiesTheRest()
    {
        CreatePullRequestActivity.ClassifyReviewerSkip(
                Fail<PModels.PullRequest>(new PlatformError.InvalidRequest("capability_unsupported", "x")))
            .Should().Be("capability_unsupported");
        CreatePullRequestActivity.ClassifyReviewerSkip(
                Fail<PModels.PullRequest>(new PlatformError.InvalidRequest("invalid_request", "ghost is not a collaborator")))
            .Should().Be("reviewer_unresolvable");
        CreatePullRequestActivity.ClassifyReviewerSkip(
                Fail<PModels.PullRequest>(new PlatformError.ServiceUnavailable()))
            .Should().NotBe("capability_unsupported",
                "a transport failure must never read as a capability refusal (§4.5)");
    }

    // ================================================================
    // DG-4 — merge-method fallback core
    // ================================================================

    [Test]
    public void MethodFallbackOrder_IsRequestedFirst_ThenTheFixedOrder()
    {
        MergePullRequestActivity.BuildMethodFallbackOrder("rebase")
            .Should().Equal("rebase", "squash", "merge");
        MergePullRequestActivity.BuildMethodFallbackOrder("squash")
            .Should().Equal("squash", "rebase", "merge");
        MergePullRequestActivity.BuildMethodFallbackOrder("merge")
            .Should().Equal("merge", "rebase", "squash");
        // Unknown strategies normalize to squash first.
        MergePullRequestActivity.BuildMethodFallbackOrder("garbage")
            .Should().Equal("squash", "rebase", "merge");
    }

    [Test]
    public void IsMergeMethodUnsupported_IsExactCodeMatchOnly()
    {
        MergePullRequestActivity.IsMergeMethodUnsupported(
                Fail<PModels.PullRequest>(new PlatformError.InvalidRequest("merge_method_unsupported", "x")))
            .Should().BeTrue();
        MergePullRequestActivity.IsMergeMethodUnsupported(
                Fail<PModels.PullRequest>(new PlatformError.InvalidRequest("MERGE_METHOD_UNSUPPORTED", "x")))
            .Should().BeFalse("ordinal exact-code only");
        MergePullRequestActivity.IsMergeMethodUnsupported(
                Fail<PModels.PullRequest>(new PlatformError.InvalidRequest("capability_unsupported", "x")))
            .Should().BeFalse("the whole merge verb missing cannot be fixed by another method");
        MergePullRequestActivity.IsMergeMethodUnsupported(
                Fail<PModels.PullRequest>(new PlatformError.InvalidRequest("merge_conflict", "x")))
            .Should().BeFalse("a real failure must never be consumed as 'try another method'");
        MergePullRequestActivity.IsMergeMethodUnsupported(
                PlatformResult<PModels.PullRequest>.FromServiceUnavailable())
            .Should().BeFalse();
    }

    private static PModels.PullRequest MergedPr(string sha) =>
        new("15", "t", null, "feature", "main", PModels.PullRequestState.Merged,
            false, "u", "bot", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        { MergeCommitSha = sha };

    private static Mock<IGitPlatformClient> OpenPrClient()
    {
        var client = new Mock<IGitPlatformClient>();
        client.Setup(c => c.GetPullRequestAsync("o", "r", "15", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Pr() with { Mergeable = true }));
        return client;
    }

    [Test]
    public async Task MergeCore_TypedMethodRefusal_FallsBack_AndRecordsAppliedStrategy()
    {
        var client = OpenPrClient();
        var attempted = new List<PModels.MergeMethod>();
        client.Setup(c => c.MergePullRequestAsync(It.IsAny<PModels.MergePullRequestRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PModels.MergePullRequestRequest, CancellationToken>((r, _) => attempted.Add(r.Method))
            .ReturnsAsync((PModels.MergePullRequestRequest r, CancellationToken _) =>
                r.Method == PModels.MergeMethod.Rebase
                    ? Fail<PModels.PullRequest>(new PlatformError.InvalidRequest("merge_method_unsupported", "x"))
                    : Ok(MergedPr("sha-1")));

        var outcome = await MergePullRequestActivity.ExecuteCoreAsync(
            client.Object, "o/r", 15, 0, "feature", "rebase",
            autoDeleteBranch: false, closeIssue: false);

        outcome.MergeSucceeded.Should().BeTrue();
        outcome.MergeSha.Should().Be("sha-1");
        outcome.AppliedStrategy.Should().Be("squash");
        outcome.MethodFallbackFrom.Should().Be("rebase");
        attempted.Should().Equal(PModels.MergeMethod.Rebase, PModels.MergeMethod.Squash);
    }

    [Test]
    public async Task MergeCore_NoFallbackNeeded_RecordsNoFallback()
    {
        var client = OpenPrClient();
        client.Setup(c => c.MergePullRequestAsync(It.IsAny<PModels.MergePullRequestRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(MergedPr("sha-2")));

        var outcome = await MergePullRequestActivity.ExecuteCoreAsync(
            client.Object, "o/r", 15, 0, "feature", "squash",
            autoDeleteBranch: false, closeIssue: false);

        outcome.MergeSucceeded.Should().BeTrue();
        outcome.AppliedStrategy.Should().Be("squash");
        outcome.MethodFallbackFrom.Should().BeNull();
    }

    [Test]
    public async Task MergeCore_EveryMethodRefused_FailsLoud_WithTheTypedCode()
    {
        var client = OpenPrClient();
        client.Setup(c => c.MergePullRequestAsync(It.IsAny<PModels.MergePullRequestRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<PModels.PullRequest>(new PlatformError.InvalidRequest("merge_method_unsupported", "x")));

        var outcome = await MergePullRequestActivity.ExecuteCoreAsync(
            client.Object, "o/r", 15, 0, "feature", "rebase",
            autoDeleteBranch: false, closeIssue: false);

        outcome.MergeSucceeded.Should().BeFalse();
        outcome.FailureCode.Should().Be("merge_method_unsupported");
        client.Verify(c => c.MergePullRequestAsync(It.IsAny<PModels.MergePullRequestRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [Test]
    public async Task MergeCore_RealFailure_FailsImmediately_NoMethodRoulette()
    {
        var client = OpenPrClient();
        client.Setup(c => c.MergePullRequestAsync(It.IsAny<PModels.MergePullRequestRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<PModels.PullRequest>(new PlatformError.InvalidRequest("merge_conflict", "409: conflict")));

        var outcome = await MergePullRequestActivity.ExecuteCoreAsync(
            client.Object, "o/r", 15, 0, "feature", "rebase",
            autoDeleteBranch: false, closeIssue: false);

        outcome.MergeSucceeded.Should().BeFalse();
        outcome.FailureCode.Should().Be("merge_conflict");
        client.Verify(c => c.MergePullRequestAsync(It.IsAny<PModels.MergePullRequestRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ================================================================
    // DG-2 — the downgraded body helper (the mediation flow itself is
    // pinned in GitMediationServiceTests)
    // ================================================================

    [Test]
    public void DowngradedReviewCommentBody_CarriesFileLineAndTheOriginalFeedback()
    {
        var body = Tamma.Api.Services.Git.GitMediationService
            .BuildDowngradedReviewCommentBody("src/a.cs", 42, "rename this");

        body.Should().Contain("src/a.cs:42");
        body.Should().Contain("rename this");
    }
}
