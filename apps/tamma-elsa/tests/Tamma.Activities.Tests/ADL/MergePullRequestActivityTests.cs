using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Tamma.Activities.ADL;
using Tamma.Core.Interfaces;

namespace Tamma.Activities.Tests.ADL;

/// <summary>
/// Story 2-10 build-out — unit coverage for the merge activity's pure helpers
/// (strategy normalization / failure classification), the <c>ExecuteCoreAsync</c>
/// orchestration (pre-merge read → idempotency → conflict gate → merge → verified
/// post-merge close/delete, NEVER a false success / never a silent failure), and
/// <c>EmitMergeEventActivity</c>'s DCB mapping (<c>BuildTammaEvent</c> — the
/// <c>TammaEvent</c> pushed into the workflow's <c>tamma:events</c> list, which
/// the engine event drain persists durably to <c>domain_events</c>). Mirrors the
/// merged branch/PR exemplars: test the testable static logic + a mocked
/// <see cref="IGitHubIntegrationService"/> rather than a full Elsa runtime.
/// </summary>
[TestFixture]
public class MergePullRequestActivityTests
{
    // ================================================================
    // Constructors
    // ================================================================

    [Test]
    public void MergePullRequestActivity_JsonConstructor_ShouldNotThrow()
    {
        Action act = () => new MergePullRequestActivity();
        act.Should().NotThrow();
    }

    [Test]
    public void EmitMergeEventActivity_JsonConstructor_ShouldNotThrow()
    {
        Action act = () => new EmitMergeEventActivity();
        act.Should().NotThrow();
    }

    [Test]
    public void EmitMergeEventActivity_WithLogger_ShouldNotThrow()
    {
        var logger = new Mock<ILogger<EmitMergeEventActivity>>();
        Action act = () => new EmitMergeEventActivity(logger.Object);
        act.Should().NotThrow();
    }

    // ================================================================
    // NormalizeStrategy
    // ================================================================

    [Test]
    public void NormalizeStrategy_MapsKnownStrategies_DefaultsToSquash()
    {
        MergePullRequestActivity.NormalizeStrategy("merge").Should().Be("merge");
        MergePullRequestActivity.NormalizeStrategy("REBASE").Should().Be("rebase");
        MergePullRequestActivity.NormalizeStrategy("squash").Should().Be("squash");
        MergePullRequestActivity.NormalizeStrategy("nonsense").Should().Be("squash");
        MergePullRequestActivity.NormalizeStrategy("").Should().Be("squash");
        MergePullRequestActivity.NormalizeStrategy(null).Should().Be("squash");
    }

    // ================================================================
    // ClassifyError — conflict / permission / protected / not-mergeable / transient
    // ================================================================

    [Test]
    public void ClassifyError_MapsKnownCodes()
    {
        MergePullRequestActivity.ClassifyError("409: merge conflict").Should().Be("merge_conflict");
        MergePullRequestActivity.ClassifyError("403 Forbidden").Should().Be("permission_denied");
        MergePullRequestActivity.ClassifyError("422 required status check failed").Should().Be("branch_protected");
        MergePullRequestActivity.ClassifyError("405: Pull Request is not mergeable").Should().Be("not_mergeable");
        MergePullRequestActivity.ClassifyError("503 service unavailable").Should().Be("api_error");
        MergePullRequestActivity.ClassifyError(null).Should().Be("api_error");
    }

    // ================================================================
    // ExecuteCoreAsync helpers
    // ================================================================

    private static Mock<IGitHubIntegrationService> OpenMergeablePr()
    {
        var gh = new Mock<IGitHubIntegrationService>();
        gh.Setup(g => g.GetGitHubPullRequestAsync("o/r", 7))
            .ReturnsAsync(IntegrationResult<GitHubPullRequestDetail>.Ok(new GitHubPullRequestDetail
            { Number = 7, State = "open", Merged = false, Mergeable = true, MergeableState = "clean" }));
        return gh;
    }

    // ================================================================
    // Happy path — merge → close issue → delete branch → clean success
    // ================================================================

    [Test]
    public async Task ExecuteCore_HappyPath_Merges_ClosesIssue_DeletesBranch()
    {
        var gh = OpenMergeablePr();
        gh.Setup(g => g.MergeGitHubPullRequestAsync("o/r", 7, "squash"))
            .ReturnsAsync(IntegrationResult<GitHubMergeResult>.Ok(new GitHubMergeResult
            { Success = true, MergeSha = "merge-sha-1" }));
        gh.Setup(g => g.CloseGitHubIssueAsync("o/r", 12, It.IsAny<string>()))
            .ReturnsAsync(IntegrationResult<bool>.Ok(true));
        gh.Setup(g => g.DeleteGitHubBranchAsync("o/r", "adl/12-x"))
            .ReturnsAsync(IntegrationResult<bool>.Ok(true));

        var outcome = await MergePullRequestActivity.ExecuteCoreAsync(
            gh.Object, "o/r", 7, 12, "adl/12-x", "squash", autoDeleteBranch: true, closeIssue: true);

        outcome.Outcome.Should().Be("Merged");
        outcome.MergeSucceeded.Should().BeTrue();
        outcome.MergeSha.Should().Be("merge-sha-1");
        outcome.IssueClosed.Should().BeTrue();
        outcome.BranchDeleted.Should().BeTrue();
        outcome.Partial.Should().BeFalse();
        outcome.AlreadyMerged.Should().BeFalse();
    }

    [Test]
    public async Task ExecuteCore_HappyPath_PassesConfiguredStrategy()
    {
        var gh = new Mock<IGitHubIntegrationService>();
        gh.Setup(g => g.GetGitHubPullRequestAsync("o/r", 7))
            .ReturnsAsync(IntegrationResult<GitHubPullRequestDetail>.Ok(new GitHubPullRequestDetail
            { Number = 7, State = "open", Merged = false, Mergeable = true }));
        gh.Setup(g => g.MergeGitHubPullRequestAsync("o/r", 7, "rebase"))
            .ReturnsAsync(IntegrationResult<GitHubMergeResult>.Ok(new GitHubMergeResult
            { Success = true, MergeSha = "sha" }));
        gh.Setup(g => g.CloseGitHubIssueAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(IntegrationResult<bool>.Ok(true));
        gh.Setup(g => g.DeleteGitHubBranchAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(IntegrationResult<bool>.Ok(true));

        var outcome = await MergePullRequestActivity.ExecuteCoreAsync(
            gh.Object, "o/r", 7, 12, "b", "rebase", true, true);

        outcome.Outcome.Should().Be("Merged");
        gh.Verify(g => g.MergeGitHubPullRequestAsync("o/r", 7, "rebase"), Times.Once);
        // The squash-only overload must NOT be used (strategy was honoured).
        gh.Verify(g => g.MergeGitHubPullRequestAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    // ================================================================
    // FAILURE PATH — the core regression: merge did NOT happen → Error,
    // success=false, NEVER a silent success.
    // ================================================================

    [Test]
    public async Task ExecuteCore_MergeReturnsFailure_ReturnsError_NoFalseSuccess()
    {
        var gh = OpenMergeablePr();
        gh.Setup(g => g.MergeGitHubPullRequestAsync("o/r", 7, "squash"))
            .ReturnsAsync(IntegrationResult<GitHubMergeResult>.Fail("409: merge conflict"));

        var outcome = await MergePullRequestActivity.ExecuteCoreAsync(
            gh.Object, "o/r", 7, 12, "b", "squash", true, true);

        outcome.Outcome.Should().Be("Error");
        outcome.MergeSucceeded.Should().BeFalse();
        outcome.FailureCode.Should().Be("merge_conflict");
        outcome.MergeSha.Should().BeNullOrEmpty();
        // A failed merge must NOT close the issue or delete the branch.
        gh.Verify(g => g.CloseGitHubIssueAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);
        gh.Verify(g => g.DeleteGitHubBranchAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task ExecuteCore_ConfirmedConflict_FailsBeforeMerge_NoBlindMerge()
    {
        var gh = new Mock<IGitHubIntegrationService>();
        gh.Setup(g => g.GetGitHubPullRequestAsync("o/r", 7))
            .ReturnsAsync(IntegrationResult<GitHubPullRequestDetail>.Ok(new GitHubPullRequestDetail
            { Number = 7, State = "open", Merged = false, Mergeable = false, MergeableState = "dirty" }));

        var outcome = await MergePullRequestActivity.ExecuteCoreAsync(
            gh.Object, "o/r", 7, 12, "b", "squash", true, true);

        outcome.Outcome.Should().Be("Error");
        outcome.FailureCode.Should().Be("merge_conflict");
        // mergeable==false must short-circuit — never attempt a doomed merge.
        gh.Verify(g => g.MergeGitHubPullRequestAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task ExecuteCore_PermissionDenied_ReturnsError()
    {
        var gh = OpenMergeablePr();
        gh.Setup(g => g.MergeGitHubPullRequestAsync("o/r", 7, "squash"))
            .ReturnsAsync(IntegrationResult<GitHubMergeResult>.Fail("403 Forbidden"));

        var outcome = await MergePullRequestActivity.ExecuteCoreAsync(
            gh.Object, "o/r", 7, 12, "b", "squash", true, true);

        outcome.Outcome.Should().Be("Error");
        outcome.FailureCode.Should().Be("permission_denied");
    }

    [Test]
    public async Task ExecuteCore_ClosedUnmergedPr_ReturnsError_NotMergeable()
    {
        var gh = new Mock<IGitHubIntegrationService>();
        gh.Setup(g => g.GetGitHubPullRequestAsync("o/r", 7))
            .ReturnsAsync(IntegrationResult<GitHubPullRequestDetail>.Ok(new GitHubPullRequestDetail
            { Number = 7, State = "closed", Merged = false }));

        var outcome = await MergePullRequestActivity.ExecuteCoreAsync(
            gh.Object, "o/r", 7, 12, "b", "squash", true, true);

        outcome.Outcome.Should().Be("Error");
        outcome.FailureCode.Should().Be("not_mergeable");
        gh.Verify(g => g.MergeGitHubPullRequestAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task ExecuteCore_PreMergeReadFails_ReturnsError_NoBlindMerge()
    {
        var gh = new Mock<IGitHubIntegrationService>();
        gh.Setup(g => g.GetGitHubPullRequestAsync("o/r", 7))
            .ReturnsAsync(IntegrationResult<GitHubPullRequestDetail>.Fail("503 service unavailable"));

        var outcome = await MergePullRequestActivity.ExecuteCoreAsync(
            gh.Object, "o/r", 7, 12, "b", "squash", true, true);

        outcome.Outcome.Should().Be("Error");
        outcome.FailureCode.Should().Be("api_error");
        gh.Verify(g => g.MergeGitHubPullRequestAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task ExecuteCore_MergeSuccessButNoSha_TreatedAsFailure()
    {
        var gh = OpenMergeablePr();
        gh.Setup(g => g.MergeGitHubPullRequestAsync("o/r", 7, "squash"))
            .ReturnsAsync(IntegrationResult<GitHubMergeResult>.Ok(new GitHubMergeResult
            { Success = true, MergeSha = "" }));

        var outcome = await MergePullRequestActivity.ExecuteCoreAsync(
            gh.Object, "o/r", 7, 12, "b", "squash", true, true);

        outcome.Outcome.Should().Be("Error");
        outcome.FailureCode.Should().Be("api_error");
    }

    [Test]
    public async Task ExecuteCore_NeverThrows_OnUnexpectedException()
    {
        var gh = new Mock<IGitHubIntegrationService>();
        gh.Setup(g => g.GetGitHubPullRequestAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var outcome = await MergePullRequestActivity.ExecuteCoreAsync(
            gh.Object, "o/r", 7, 12, "b", "squash", true, true);

        outcome.Outcome.Should().Be("Error");
        outcome.FailureCode.Should().Be("api_error");
        outcome.FailureReason.Should().Contain("boom");
    }

    // ================================================================
    // Idempotency — already-merged PR → success, no re-merge (no 405)
    // ================================================================

    [Test]
    public async Task ExecuteCore_AlreadyMerged_ReturnsSuccess_NoReMerge()
    {
        var gh = new Mock<IGitHubIntegrationService>();
        gh.Setup(g => g.GetGitHubPullRequestAsync("o/r", 7))
            .ReturnsAsync(IntegrationResult<GitHubPullRequestDetail>.Ok(new GitHubPullRequestDetail
            { Number = 7, State = "closed", Merged = true, MergeCommitSha = "existing-sha" }));
        gh.Setup(g => g.CloseGitHubIssueAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(IntegrationResult<bool>.Ok(true));
        gh.Setup(g => g.DeleteGitHubBranchAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(IntegrationResult<bool>.Ok(true));

        var outcome = await MergePullRequestActivity.ExecuteCoreAsync(
            gh.Object, "o/r", 7, 12, "b", "squash", true, true);

        outcome.Outcome.Should().Be("Merged");
        outcome.AlreadyMerged.Should().BeTrue();
        outcome.MergeSha.Should().Be("existing-sha");
        // Must NOT re-attempt the merge (that 405s on a merged PR).
        gh.Verify(g => g.MergeGitHubPullRequestAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    // ================================================================
    // Partial — merge OK but issue-close failed → MergedWithWarnings (success=true)
    // ================================================================

    [Test]
    public async Task ExecuteCore_MergeOk_IssueCloseFails_ReturnsMergedWithWarnings()
    {
        var gh = OpenMergeablePr();
        gh.Setup(g => g.MergeGitHubPullRequestAsync("o/r", 7, "squash"))
            .ReturnsAsync(IntegrationResult<GitHubMergeResult>.Ok(new GitHubMergeResult
            { Success = true, MergeSha = "sha" }));
        gh.Setup(g => g.CloseGitHubIssueAsync("o/r", 12, It.IsAny<string>()))
            .ReturnsAsync(IntegrationResult<bool>.Fail("404 not found"));
        gh.Setup(g => g.DeleteGitHubBranchAsync("o/r", "b"))
            .ReturnsAsync(IntegrationResult<bool>.Ok(true));

        var outcome = await MergePullRequestActivity.ExecuteCoreAsync(
            gh.Object, "o/r", 7, 12, "b", "squash", true, true);

        outcome.Outcome.Should().Be("MergedWithWarnings");
        outcome.MergeSucceeded.Should().BeTrue("the merge stands even though a post-merge action failed");
        outcome.Partial.Should().BeTrue();
        outcome.IssueClosed.Should().BeFalse();
        outcome.BranchDeleted.Should().BeTrue();
        outcome.FailureReason.Should().Contain("issue-close-failed");
    }

    [Test]
    public async Task ExecuteCore_MergeOk_BranchDeleteFails_ReturnsMergedWithWarnings_StillSuccess()
    {
        var gh = OpenMergeablePr();
        gh.Setup(g => g.MergeGitHubPullRequestAsync("o/r", 7, "squash"))
            .ReturnsAsync(IntegrationResult<GitHubMergeResult>.Ok(new GitHubMergeResult
            { Success = true, MergeSha = "sha" }));
        gh.Setup(g => g.CloseGitHubIssueAsync("o/r", 12, It.IsAny<string>()))
            .ReturnsAsync(IntegrationResult<bool>.Ok(true));
        gh.Setup(g => g.DeleteGitHubBranchAsync("o/r", "b"))
            .ReturnsAsync(IntegrationResult<bool>.Fail("422 protected"));

        var outcome = await MergePullRequestActivity.ExecuteCoreAsync(
            gh.Object, "o/r", 7, 12, "b", "squash", true, true);

        outcome.Outcome.Should().Be("MergedWithWarnings");
        outcome.MergeSucceeded.Should().BeTrue("a failed branch-delete is a warning, not a merge failure");
        outcome.IssueClosed.Should().BeTrue();
        outcome.BranchDeleted.Should().BeFalse();
        outcome.FailureReason.Should().Contain("branch-delete-failed");
    }

    [Test]
    public async Task ExecuteCore_CloseDisabled_DoesNotCallClose_StillClean()
    {
        var gh = OpenMergeablePr();
        gh.Setup(g => g.MergeGitHubPullRequestAsync("o/r", 7, "squash"))
            .ReturnsAsync(IntegrationResult<GitHubMergeResult>.Ok(new GitHubMergeResult
            { Success = true, MergeSha = "sha" }));
        gh.Setup(g => g.DeleteGitHubBranchAsync("o/r", "b"))
            .ReturnsAsync(IntegrationResult<bool>.Ok(true));

        var outcome = await MergePullRequestActivity.ExecuteCoreAsync(
            gh.Object, "o/r", 7, 12, "b", "squash", autoDeleteBranch: true, closeIssue: false);

        outcome.Outcome.Should().Be("Merged", "skipping a disabled close is not a warning");
        outcome.IssueClosed.Should().BeFalse();
        gh.Verify(g => g.CloseGitHubIssueAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    // ================================================================
    // EmitMergeEventActivity.BuildTammaEvent — DCB mapping onto the drain
    // ================================================================

    [Test]
    public void BuildTammaEvent_SuccessType_SetsTypeStatusTagsAndData()
    {
        var evt = EmitMergeEventActivity.BuildTammaEvent(
            MergeEvents.Success, issueNumber: 12, prNumber: 7, repository: "o/r",
            tenantId: null,
            data: new Dictionary<string, object?> { ["mergeSha"] = "sha", ["mergeStrategy"] = "squash" });

        evt.EventType.Should().Be("MERGE.SUCCESS");
        evt.Status.Should().Be("success");
        evt.Tags.Should().NotBeNull();
        evt.Tags!["issueId"].Should().Be("12");
        evt.Tags["issueNumber"].Should().Be("12");
        evt.Tags["prNumber"].Should().Be("7");
        evt.Tags["repository"].Should().Be("o/r");
        evt.Tags.Should().NotContainKey("tenantId");
        evt.Data.Should().ContainKey("mergeSha");
        evt.Data.Should().ContainKey("mergeStrategy");
    }

    [Test]
    public void BuildTammaEvent_FailedType_SetsErrorStatus()
    {
        var evt = EmitMergeEventActivity.BuildTammaEvent(
            MergeEvents.Failed, issueNumber: 7, prNumber: 3, repository: "o/r",
            tenantId: null, data: null);

        evt.EventType.Should().Be("MERGE.FAILED");
        evt.Status.Should().Be("error");
        evt.Data.Should().BeEmpty();
    }

    [Test]
    public void BuildTammaEvent_IssueClosedFailed_IsErrorStatus()
    {
        var evt = EmitMergeEventActivity.BuildTammaEvent(
            MergeEvents.IssueClosedFailed, 7, 3, "o/r", tenantId: null, data: null);
        evt.Status.Should().Be("error");

        var ok = EmitMergeEventActivity.BuildTammaEvent(
            MergeEvents.IssueClosedSuccess, 7, 3, "o/r", tenantId: null, data: null);
        ok.Status.Should().Be("success");
    }

    [Test]
    public void BuildTammaEvent_WithTenant_SetsTenantIdTag()
    {
        var tenant = Guid.NewGuid();
        var evt = EmitMergeEventActivity.BuildTammaEvent(
            MergeEvents.Success, 1, 2, "o/r", tenantId: tenant, data: null);

        evt.Tags!["tenantId"].Should().Be(tenant.ToString("D"));
    }

    [Test]
    public void MergeEvents_ParseTenantId_HandlesEmptyAndValid()
    {
        MergeEvents.ParseTenantId(null).Should().BeNull();
        MergeEvents.ParseTenantId("").Should().BeNull();
        MergeEvents.ParseTenantId("not-a-guid").Should().BeNull();
        var g = Guid.NewGuid();
        MergeEvents.ParseTenantId(g.ToString()).Should().Be(g);
    }

    [Test]
    public void EmitMergeEvent_ParseData_HandlesEmptyAndMalformed()
    {
        EmitMergeEventActivity.ParseData(null).Should().BeNull();
        EmitMergeEventActivity.ParseData("").Should().BeNull();
        EmitMergeEventActivity.ParseData("{not json").Should().BeNull();
    }
}
