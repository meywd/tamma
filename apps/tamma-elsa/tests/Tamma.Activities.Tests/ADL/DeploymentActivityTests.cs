using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.ADL;

namespace Tamma.Activities.Tests.ADL;

/// <summary>
/// Unit coverage for the deployment-pipeline gate/event pure logic (completeness
/// audit 2026-06-22):
///   - <c>WaitForDeploymentApprovalActivity.Normalize</c>: approve/reject map to
///     typed outcomes; unknown/empty → <c>Invalid</c> (NEVER a silent "approve" —
///     fail-closed before a production deploy);
///   - the prod-approval bookmark name folds tenant + repo + SHA so it can't
///     collide cross-tenant/cross-repo/cross-SHA;
///   - <c>EmitDeploymentEventActivity.BuildTammaEvent</c> maps onto the durable
///     drain event shape with the right tags, status and payload;
///   - <c>DeployEvents.IsFailureType</c>: failed/rejected/rollback-failed are loud
///     (error-status) rows, not false success.
/// </summary>
[TestFixture]
public class DeploymentActivityTests
{
    // ================================================================
    // WaitForDeploymentApprovalActivity.Normalize — fail-closed, no silent approve
    // ================================================================

    [TestCase("approve", "Approve", "approve")]
    [TestCase("APPROVE", "Approve", "approve")]
    [TestCase("  Approve  ", "Approve", "approve")]
    [TestCase("reject", "Reject", "reject")]
    [TestCase("REJECT", "Reject", "reject")]
    public void Normalize_KnownDecisions_MapToTypedOutcomes(string input, string outcome, string token)
    {
        var (o, n) = WaitForDeploymentApprovalActivity.Normalize(input);
        o.Should().Be(outcome);
        n.Should().Be(token);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("yes")]
    [TestCase("ok")]
    [TestCase("deploy")]
    [TestCase("approv")]   // typo
    public void Normalize_UnknownOrEmpty_IsInvalid_NotSilentApprove(string? input)
    {
        var (o, n) = WaitForDeploymentApprovalActivity.Normalize(input);
        o.Should().Be("Invalid",
            "an unknown/empty production-deploy decision must be an explicit Invalid outcome — never a silent approve");
        n.Should().Be("invalid");
        o.Should().NotBe("Approve");
    }

    // ================================================================
    // Bookmark name — tenant + repo + SHA scoping (no cross-tenant collision)
    // ================================================================

    [Test]
    public void BookmarkName_FoldsTenantRepoAndSha()
    {
        var name = WaitForDeploymentApprovalActivity.BookmarkName(
            "11111111-1111-1111-1111-111111111111", "acme/app", 42, "abcdef1234");
        name.Should().StartWith("adl-deploy-prod-approval-");
        name.Should().Contain("42");
        name.Should().Contain("abcdef1234");
    }

    [Test]
    public void BookmarkName_DifferentTenant_ProducesDistinctName()
    {
        var a = WaitForDeploymentApprovalActivity.BookmarkName("tenant-a", "acme/app", 42, "sha1");
        var b = WaitForDeploymentApprovalActivity.BookmarkName("tenant-b", "acme/app", 42, "sha1");
        a.Should().NotBe(b, "two tenants on the same issue/SHA must get distinct bookmarks (no cross-tenant resume)");
    }

    [Test]
    public void BookmarkName_DifferentSha_ProducesDistinctName()
    {
        var a = WaitForDeploymentApprovalActivity.BookmarkName("t", "acme/app", 42, "sha1");
        var b = WaitForDeploymentApprovalActivity.BookmarkName("t", "acme/app", 42, "sha2");
        a.Should().NotBe(b, "a re-deploy of a different SHA must not resume a stale gate");
    }

    [Test]
    public void BookmarkName_MatchesEngineEndpointBuilder()
    {
        // Suspend-side and resume-side must compute the SAME name byte-for-byte.
        var activitySide = WaitForDeploymentApprovalActivity.BookmarkName("t", "acme/app", 7, "deadbeef");
        var endpointSide = Tamma.ElsaServer.Endpoints.DeploymentApprovalResumeEndpoint.BookmarkName(
            "t", "acme/app", 7, "deadbeef");
        endpointSide.Should().Be(activitySide);
    }

    // ================================================================
    // DeployEvents.IsFailureType — loud audit rows
    // ================================================================

    [TestCase("DEPLOY.STAGE.FAILED", true)]
    [TestCase("DEPLOY.PIPELINE.FAILED", true)]
    [TestCase("DEPLOY.PRODUCTION.REJECTED", true)]
    [TestCase("DEPLOY.ROLLBACK.FAILED", true)]
    [TestCase("DEPLOY.STAGE.STARTED", false)]
    [TestCase("DEPLOY.STAGE.SUCCESS", false)]
    [TestCase("DEPLOY.PIPELINE.SUCCESS", false)]
    [TestCase("DEPLOY.PRODUCTION.APPROVED", false)]
    [TestCase("DEPLOY.PRODUCTION.APPROVAL_REQUESTED", false)]
    [TestCase("DEPLOY.ROLLBACK.STARTED", false)]
    [TestCase("DEPLOY.ROLLBACK.SUCCESS", false)]
    public void IsFailureType_FlagsFailedAndRejectedRowsLoud(string type, bool isFailure)
    {
        DeployEvents.IsFailureType(type).Should().Be(isFailure);
    }

    // ================================================================
    // EmitDeploymentEventActivity.BuildTammaEvent — DCB mapping
    // ================================================================

    [Test]
    public void BuildTammaEvent_StageStarted_SetsSuccessStatusTagsAndData()
    {
        var data = new Dictionary<string, object?> { ["status"] = "started" };
        var evt = EmitDeploymentEventActivity.BuildTammaEvent(
            DeployEvents.StageStarted, issueNumber: 12, repository: "acme/app",
            mergeSha: "abc1234", stage: "qa", mode: "business", tenantId: null, data: data);

        evt.EventType.Should().Be("DEPLOY.STAGE.STARTED");
        evt.Status.Should().Be("success");
        evt.Tags.Should().NotBeNull();
        evt.Tags!["issueId"].Should().Be("12");
        evt.Tags["issueNumber"].Should().Be("12");
        evt.Tags["repository"].Should().Be("acme/app");
        evt.Tags["mergeSha"].Should().Be("abc1234");
        evt.Tags["stage"].Should().Be("qa");
        evt.Tags["mode"].Should().Be("business");
        evt.Tags.Should().NotContainKey("tenantId");
        evt.Data["status"].Should().Be("started");
    }

    [Test]
    public void BuildTammaEvent_StageFailed_SetsErrorStatus()
    {
        var evt = EmitDeploymentEventActivity.BuildTammaEvent(
            DeployEvents.StageFailed, issueNumber: 7, repository: "acme/app",
            mergeSha: "", stage: "production", mode: "", tenantId: null, data: null);

        evt.EventType.Should().Be("DEPLOY.STAGE.FAILED");
        evt.Status.Should().Be("error", "a failed deploy stage is a loud audit row, not a false success");
        // Empty repo/sha/mode segments are not stamped as tags.
        evt.Tags!.Should().NotContainKey("mergeSha");
        evt.Tags.Should().NotContainKey("mode");
    }

    [Test]
    public void BuildTammaEvent_ProductionRejected_SetsErrorStatus()
    {
        var evt = EmitDeploymentEventActivity.BuildTammaEvent(
            DeployEvents.ProductionRejected, issueNumber: 9, repository: "acme/app",
            mergeSha: "sha", stage: "production", mode: "business", tenantId: null, data: null);
        evt.Status.Should().Be("error", "a rejected production deploy is a loud row, never a silent promote");
    }

    [Test]
    public void BuildTammaEvent_WithTenant_StampsTenantTag()
    {
        var tenant = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var evt = EmitDeploymentEventActivity.BuildTammaEvent(
            DeployEvents.PipelineSuccess, issueNumber: 1, repository: "acme/app",
            mergeSha: "sha", stage: "", mode: "", tenantId: tenant, data: null);
        evt.Tags!["tenantId"].Should().Be(tenant.ToString("D"));
        // pipeline-level events carry no stage tag.
        evt.Tags.Should().NotContainKey("stage");
    }

    [Test]
    public void ParseData_RoundTripsJson_AndIsNullOnGarbage()
    {
        var parsed = EmitDeploymentEventActivity.ParseData("{\"status\":\"success\",\"reason\":\"\"}");
        parsed.Should().NotBeNull();
        parsed!["status"]!.ToString().Should().Be("success");

        EmitDeploymentEventActivity.ParseData(null).Should().BeNull();
        EmitDeploymentEventActivity.ParseData("   ").Should().BeNull();
        EmitDeploymentEventActivity.ParseData("not json").Should().BeNull();
    }

    [Test]
    public void ParseTenantId_ParsesGuid_NullOtherwise()
    {
        DeployEvents.ParseTenantId("33333333-3333-3333-3333-333333333333").Should().NotBeNull();
        DeployEvents.ParseTenantId("").Should().BeNull();
        DeployEvents.ParseTenantId("not-a-guid").Should().BeNull();
        DeployEvents.ParseTenantId(null).Should().BeNull();
    }
}
