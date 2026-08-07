using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Tamma.Activities.ADL;
using Tamma.Platforms.Abstractions;

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
    // ExecuteCoreAsync helpers — Epic 31 P2: the core is retyped onto
    // IGitPlatformClient (pre-merge read = GetPullRequestAsync with the new
    // Mergeable/MergeCommitSha read-backs; merge = MergePullRequestAsync;
    // close = CloseIssueAsync; delete = DeleteBranchAsync). Coarse error
    // classes are preserved via the legacy-string projection.
    // ================================================================

    private static Tamma.Platforms.Abstractions.Models.PullRequest MPr(
        Tamma.Platforms.Abstractions.Models.PullRequestState state,
        bool? mergeable = null,
        string? mergeableState = null,
        string? mergeSha = null) =>
        new("7", "t", null, "b", "main", state, false, "u", "bot",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        { Mergeable = mergeable, MergeableState = mergeableState, MergeCommitSha = mergeSha };

    private static PlatformResult<Tamma.Platforms.Abstractions.Models.PullRequest> PrOk(
        Tamma.Platforms.Abstractions.Models.PullRequest pr) =>
        PlatformResult<Tamma.Platforms.Abstractions.Models.PullRequest>.FromOk(pr);

    private static Tamma.Platforms.Abstractions.Models.Issue ClosedIssue() =>
        new("12", "t", null, Tamma.Platforms.Abstractions.Models.IssueState.Closed, "u", Array.Empty<string>());

    private static Mock<IGitPlatformClient> OpenMergeablePr()
    {
        var client = new Mock<IGitPlatformClient>();
        client.Setup(c => c.GetPullRequestAsync("o", "r", "7", It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrOk(MPr(Tamma.Platforms.Abstractions.Models.PullRequestState.Open,
                mergeable: true, mergeableState: "clean")));
        return client;
    }

    // ================================================================
    // Happy path — merge → close issue → delete branch → clean success
    // ================================================================

    [Test]
    public async Task ExecuteCore_HappyPath_Merges_ClosesIssue_DeletesBranch()
    {
        var client = OpenMergeablePr();
        client.Setup(c => c.MergePullRequestAsync(
                It.Is<Tamma.Platforms.Abstractions.Models.MergePullRequestRequest>(r =>
                    r.Method == Tamma.Platforms.Abstractions.Models.MergeMethod.Squash && r.PrNumber == "7"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrOk(MPr(Tamma.Platforms.Abstractions.Models.PullRequestState.Merged, mergeSha: "merge-sha-1")));
        client.Setup(c => c.CloseIssueAsync("o", "r", "12", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PlatformResult<Tamma.Platforms.Abstractions.Models.Issue>.FromOk(ClosedIssue()));
        client.Setup(c => c.DeleteBranchAsync("o", "r", "adl/12-x", It.IsAny<CancellationToken>()))
            .ReturnsAsync(PlatformResult<bool>.FromOk(true));

        var outcome = await MergePullRequestActivity.ExecuteCoreAsync(
            client.Object, "o/r", 7, 12, "adl/12-x", "squash", autoDeleteBranch: true, closeIssue: true);

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
        var client = OpenMergeablePr();
        client.Setup(c => c.MergePullRequestAsync(
                It.Is<Tamma.Platforms.Abstractions.Models.MergePullRequestRequest>(r =>
                    r.Method == Tamma.Platforms.Abstractions.Models.MergeMethod.Rebase),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrOk(MPr(Tamma.Platforms.Abstractions.Models.PullRequestState.Merged, mergeSha: "sha")));
        client.Setup(c => c.CloseIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PlatformResult<Tamma.Platforms.Abstractions.Models.Issue>.FromOk(ClosedIssue()));
        client.Setup(c => c.DeleteBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PlatformResult<bool>.FromOk(true));

        var outcome = await MergePullRequestActivity.ExecuteCoreAsync(
            client.Object, "o/r", 7, 12, "b", "rebase", true, true);

        outcome.Outcome.Should().Be("Merged");
        // The configured strategy is honoured: exactly one merge call, rebase.
        client.Verify(c => c.MergePullRequestAsync(
            It.Is<Tamma.Platforms.Abstractions.Models.MergePullRequestRequest>(r =>
                r.Method == Tamma.Platforms.Abstractions.Models.MergeMethod.Rebase),
            It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.MergePullRequestAsync(
            It.IsAny<Tamma.Platforms.Abstractions.Models.MergePullRequestRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ================================================================
    // FAILURE PATH — the core regression: merge did NOT happen → Error,
    // success=false, NEVER a silent success.
    // ================================================================

    [Test]
    public async Task ExecuteCore_MergeReturnsFailure_ReturnsError_NoFalseSuccess()
    {
        var client = OpenMergeablePr();
        client.Setup(c => c.MergePullRequestAsync(
                It.IsAny<Tamma.Platforms.Abstractions.Models.MergePullRequestRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PlatformResult<Tamma.Platforms.Abstractions.Models.PullRequest>.FromError(
                new PlatformError.InvalidRequest("merge_conflict", "merge conflict")));

        var outcome = await MergePullRequestActivity.ExecuteCoreAsync(
            client.Object, "o/r", 7, 12, "b", "squash", true, true);

        outcome.Outcome.Should().Be("Error");
        outcome.MergeSucceeded.Should().BeFalse();
        outcome.FailureCode.Should().Be("merge_conflict");
        outcome.MergeSha.Should().BeNullOrEmpty();
        // A failed merge must NOT close the issue or delete the branch.
        client.Verify(c => c.CloseIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        client.Verify(c => c.DeleteBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ExecuteCore_ConfirmedConflict_FailsBeforeMerge_NoBlindMerge()
    {
        var client = new Mock<IGitPlatformClient>();
        client.Setup(c => c.GetPullRequestAsync("o", "r", "7", It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrOk(MPr(Tamma.Platforms.Abstractions.Models.PullRequestState.Open,
                mergeable: false, mergeableState: "dirty")));

        var outcome = await MergePullRequestActivity.ExecuteCoreAsync(
            client.Object, "o/r", 7, 12, "b", "squash", true, true);

        outcome.Outcome.Should().Be("Error");
        outcome.FailureCode.Should().Be("merge_conflict");
        // mergeable==false must short-circuit — never attempt a doomed merge.
        client.Verify(c => c.MergePullRequestAsync(
            It.IsAny<Tamma.Platforms.Abstractions.Models.MergePullRequestRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ExecuteCore_PermissionDenied_ReturnsError()
    {
        var client = OpenMergeablePr();
        client.Setup(c => c.MergePullRequestAsync(
                It.IsAny<Tamma.Platforms.Abstractions.Models.MergePullRequestRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PlatformResult<Tamma.Platforms.Abstractions.Models.PullRequest>.FromError(
                new PlatformError.PermissionDenied()));

        var outcome = await MergePullRequestActivity.ExecuteCoreAsync(
            client.Object, "o/r", 7, 12, "b", "squash", true, true);

        outcome.Outcome.Should().Be("Error");
        outcome.FailureCode.Should().Be("permission_denied");
    }

    [Test]
    public async Task ExecuteCore_ClosedUnmergedPr_ReturnsError_NotMergeable()
    {
        var client = new Mock<IGitPlatformClient>();
        client.Setup(c => c.GetPullRequestAsync("o", "r", "7", It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrOk(MPr(Tamma.Platforms.Abstractions.Models.PullRequestState.Closed)));

        var outcome = await MergePullRequestActivity.ExecuteCoreAsync(
            client.Object, "o/r", 7, 12, "b", "squash", true, true);

        outcome.Outcome.Should().Be("Error");
        outcome.FailureCode.Should().Be("not_mergeable");
        client.Verify(c => c.MergePullRequestAsync(
            It.IsAny<Tamma.Platforms.Abstractions.Models.MergePullRequestRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ExecuteCore_PreMergeReadFails_ReturnsError_NoBlindMerge()
    {
        var client = new Mock<IGitPlatformClient>();
        client.Setup(c => c.GetPullRequestAsync("o", "r", "7", It.IsAny<CancellationToken>()))
            .ReturnsAsync(PlatformResult<Tamma.Platforms.Abstractions.Models.PullRequest>.FromError(
                new PlatformError.ServiceUnavailable()));

        var outcome = await MergePullRequestActivity.ExecuteCoreAsync(
            client.Object, "o/r", 7, 12, "b", "squash", true, true);

        outcome.Outcome.Should().Be("Error");
        outcome.FailureCode.Should().Be("api_error");
        client.Verify(c => c.MergePullRequestAsync(
            It.IsAny<Tamma.Platforms.Abstractions.Models.MergePullRequestRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ExecuteCore_MergeSuccessButNoSha_TreatedAsFailure()
    {
        var client = OpenMergeablePr();
        client.Setup(c => c.MergePullRequestAsync(
                It.IsAny<Tamma.Platforms.Abstractions.Models.MergePullRequestRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrOk(MPr(Tamma.Platforms.Abstractions.Models.PullRequestState.Merged, mergeSha: "")));

        var outcome = await MergePullRequestActivity.ExecuteCoreAsync(
            client.Object, "o/r", 7, 12, "b", "squash", true, true);

        outcome.Outcome.Should().Be("Error");
        outcome.FailureCode.Should().Be("api_error");
    }

    [Test]
    public async Task ExecuteCore_NeverThrows_OnUnexpectedException()
    {
        var client = new Mock<IGitPlatformClient>();
        client.Setup(c => c.GetPullRequestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var outcome = await MergePullRequestActivity.ExecuteCoreAsync(
            client.Object, "o/r", 7, 12, "b", "squash", true, true);

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
        var client = new Mock<IGitPlatformClient>();
        client.Setup(c => c.GetPullRequestAsync("o", "r", "7", It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrOk(MPr(Tamma.Platforms.Abstractions.Models.PullRequestState.Merged, mergeSha: "existing-sha")));
        client.Setup(c => c.CloseIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PlatformResult<Tamma.Platforms.Abstractions.Models.Issue>.FromOk(ClosedIssue()));
        client.Setup(c => c.DeleteBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PlatformResult<bool>.FromOk(true));

        var outcome = await MergePullRequestActivity.ExecuteCoreAsync(
            client.Object, "o/r", 7, 12, "b", "squash", true, true);

        outcome.Outcome.Should().Be("Merged");
        outcome.AlreadyMerged.Should().BeTrue();
        outcome.MergeSha.Should().Be("existing-sha");
        // Must NOT re-attempt the merge (that 405s on a merged PR).
        client.Verify(c => c.MergePullRequestAsync(
            It.IsAny<Tamma.Platforms.Abstractions.Models.MergePullRequestRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ================================================================
    // Partial — merge OK but issue-close failed → MergedWithWarnings (success=true)
    // ================================================================

    [Test]
    public async Task ExecuteCore_MergeOk_IssueCloseFails_ReturnsMergedWithWarnings()
    {
        var client = OpenMergeablePr();
        client.Setup(c => c.MergePullRequestAsync(
                It.IsAny<Tamma.Platforms.Abstractions.Models.MergePullRequestRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrOk(MPr(Tamma.Platforms.Abstractions.Models.PullRequestState.Merged, mergeSha: "sha")));
        client.Setup(c => c.CloseIssueAsync("o", "r", "12", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PlatformResult<Tamma.Platforms.Abstractions.Models.Issue>.FromError(
                new PlatformError.NotFound()));
        client.Setup(c => c.DeleteBranchAsync("o", "r", "b", It.IsAny<CancellationToken>()))
            .ReturnsAsync(PlatformResult<bool>.FromOk(true));

        var outcome = await MergePullRequestActivity.ExecuteCoreAsync(
            client.Object, "o/r", 7, 12, "b", "squash", true, true);

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
        var client = OpenMergeablePr();
        client.Setup(c => c.MergePullRequestAsync(
                It.IsAny<Tamma.Platforms.Abstractions.Models.MergePullRequestRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrOk(MPr(Tamma.Platforms.Abstractions.Models.PullRequestState.Merged, mergeSha: "sha")));
        client.Setup(c => c.CloseIssueAsync("o", "r", "12", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PlatformResult<Tamma.Platforms.Abstractions.Models.Issue>.FromOk(ClosedIssue()));
        client.Setup(c => c.DeleteBranchAsync("o", "r", "b", It.IsAny<CancellationToken>()))
            .ReturnsAsync(PlatformResult<bool>.FromError(
                new PlatformError.InvalidRequest("validation_failed", "protected")));

        var outcome = await MergePullRequestActivity.ExecuteCoreAsync(
            client.Object, "o/r", 7, 12, "b", "squash", true, true);

        outcome.Outcome.Should().Be("MergedWithWarnings");
        outcome.MergeSucceeded.Should().BeTrue("a failed branch-delete is a warning, not a merge failure");
        outcome.IssueClosed.Should().BeTrue();
        outcome.BranchDeleted.Should().BeFalse();
        outcome.FailureReason.Should().Contain("branch-delete-failed");
    }

    [Test]
    public async Task ExecuteCore_CloseDisabled_DoesNotCallClose_StillClean()
    {
        var client = OpenMergeablePr();
        client.Setup(c => c.MergePullRequestAsync(
                It.IsAny<Tamma.Platforms.Abstractions.Models.MergePullRequestRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrOk(MPr(Tamma.Platforms.Abstractions.Models.PullRequestState.Merged, mergeSha: "sha")));
        client.Setup(c => c.DeleteBranchAsync("o", "r", "b", It.IsAny<CancellationToken>()))
            .ReturnsAsync(PlatformResult<bool>.FromOk(true));

        var outcome = await MergePullRequestActivity.ExecuteCoreAsync(
            client.Object, "o/r", 7, 12, "b", "squash", autoDeleteBranch: true, closeIssue: false);

        outcome.Outcome.Should().Be("Merged", "skipping a disabled close is not a warning");
        outcome.IssueClosed.Should().BeFalse();
        client.Verify(c => c.CloseIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
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
