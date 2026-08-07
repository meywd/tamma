using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Tamma.Activities.ADL;
using Tamma.Platforms.Abstractions;

namespace Tamma.Activities.Tests.ADL;

/// <summary>
/// Story 2.4 build-out — unit coverage for the branch-creation activity's pure
/// helpers (name generation / sanitize / ref-validation / error classification),
/// the <c>ExecuteCoreAsync</c> orchestration (base validation → idempotent
/// conflict resolution → create → post-create validation, NEVER a false success),
/// and <c>EmitBranchEventActivity</c>'s DCB mapping (<c>BuildTammaEvent</c> — the
/// <c>TammaEvent</c> pushed into the workflow's <c>tamma:events</c> list, which
/// the engine event drain persists durably to <c>domain_events</c>). Mirrors the
/// merged PR exemplar (<see cref="PullRequestActivityTests"/>): test the testable
/// static logic + a mocked <see cref="IGitHubIntegrationService"/> rather than a
/// full Elsa runtime.
/// </summary>
[TestFixture]
public class BranchCreationActivityTests
{
    // ================================================================
    // Constructors
    // ================================================================

    [Test]
    public void CreateBranchActivity_JsonConstructor_ShouldNotThrow()
    {
        Action act = () => new CreateBranchActivity();
        act.Should().NotThrow();
    }

    [Test]
    public void CreateBranchActivity_WithDependencies_ShouldNotThrow()
    {
        var logger = new Mock<ILogger<CreateBranchActivity>>();
        Action act = () => new CreateBranchActivity(logger.Object, (Tamma.Activities.LlmCall.TammaApiClient?)null);
        act.Should().NotThrow();
    }

    [Test]
    public void EmitBranchEventActivity_JsonConstructor_ShouldNotThrow()
    {
        Action act = () => new EmitBranchEventActivity();
        act.Should().NotThrow();
    }

    [Test]
    public void EmitBranchEventActivity_WithLogger_ShouldNotThrow()
    {
        var logger = new Mock<ILogger<EmitBranchEventActivity>>();
        Action act = () => new EmitBranchEventActivity(logger.Object);
        act.Should().NotThrow();
    }

    // ================================================================
    // GenerateBranchName / sanitize
    // ================================================================

    [Test]
    public void GenerateBranchName_BuildsAdlPrefixedSlug()
    {
        CreateBranchActivity.GenerateBranchName(42, "Add OAuth login!")
            .Should().Be("adl/42-add-oauth-login");
    }

    [Test]
    public void GenerateBranchName_TruncatesLongTitle()
    {
        var name = CreateBranchActivity.GenerateBranchName(7, new string('a', 200));
        // adl/7- prefix + at most 40 title chars.
        name.Should().StartWith("adl/7-");
        name[(name.IndexOf('-') + 1)..].Length.Should().BeLessThanOrEqualTo(40);
    }

    [Test]
    public void GenerateBranchName_EmptyTitle_StillValid()
    {
        var name = CreateBranchActivity.GenerateBranchName(9, "");
        name.Should().Be("adl/9");
        CreateBranchActivity.IsValidRefName(name).Should().BeTrue();
    }

    [Test]
    public void GenerateBranchName_StripsSlashesAndDots()
    {
        var name = CreateBranchActivity.GenerateBranchName(3, "feature/../etc");
        name.Should().NotContain("..");
        CreateBranchActivity.IsValidRefName(name).Should().BeTrue();
    }

    // ================================================================
    // IsValidRefName — injection hardening
    // ================================================================

    [Test]
    public void IsValidRefName_RejectsDangerousPatterns()
    {
        CreateBranchActivity.IsValidRefName("").Should().BeFalse();
        CreateBranchActivity.IsValidRefName("-leading-hyphen").Should().BeFalse();
        CreateBranchActivity.IsValidRefName("has..dotdot").Should().BeFalse();
        CreateBranchActivity.IsValidRefName("has space").Should().BeFalse();
        CreateBranchActivity.IsValidRefName("has~tilde").Should().BeFalse();
        CreateBranchActivity.IsValidRefName("trailing/").Should().BeFalse();
        CreateBranchActivity.IsValidRefName("double//slash").Should().BeFalse();
    }

    [Test]
    public void IsValidRefName_AcceptsNormalBranchNames()
    {
        CreateBranchActivity.IsValidRefName("adl/42-add-auth").Should().BeTrue();
        CreateBranchActivity.IsValidRefName("main").Should().BeTrue();
        CreateBranchActivity.IsValidRefName("feature/x").Should().BeTrue();
    }

    // ================================================================
    // ClassifyError — permission / not-found / protected / transient
    // ================================================================

    [Test]
    public void ClassifyError_MapsKnownCodes()
    {
        CreateBranchActivity.ClassifyError("403 Forbidden").Should().Be("permission_denied");
        CreateBranchActivity.ClassifyError("base_branch_not_found: develop").Should().Be("base_branch_not_found");
        CreateBranchActivity.ClassifyError("422 protected branch").Should().Be("base_branch_protected");
        CreateBranchActivity.ClassifyError("503 service unavailable").Should().Be("transient");
        CreateBranchActivity.ClassifyError("some weird error").Should().Be("unknown");
        CreateBranchActivity.ClassifyError(null).Should().Be("unknown");
    }

    // ================================================================
    // ExecuteCoreAsync — behavioral (happy / idempotency / failure / validation).
    // Epic 31 P2: the core is retyped onto IGitPlatformClient — existence is
    // GetBranchAsync (Ok = exists, NotFound = absent), the base branch is
    // resolved to a SHA before CreateBranchAsync, and errors classify through
    // the same coarse legacy-string classes as before.
    // ================================================================

    private static Tamma.Platforms.Abstractions.Models.Branch B(string name, string sha = "sha") =>
        new(name, sha, false);

    private static PlatformResult<Tamma.Platforms.Abstractions.Models.Branch> BranchOk(string name, string sha = "sha") =>
        PlatformResult<Tamma.Platforms.Abstractions.Models.Branch>.FromOk(B(name, sha));

    private static PlatformResult<Tamma.Platforms.Abstractions.Models.Branch> BranchAbsent() =>
        PlatformResult<Tamma.Platforms.Abstractions.Models.Branch>.FromError(new PlatformError.NotFound());

    private static void SetupBranch(
        Mock<IGitPlatformClient> client, string branch,
        PlatformResult<Tamma.Platforms.Abstractions.Models.Branch> result) =>
        client.Setup(c => c.GetBranchAsync("o", "r", branch, It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    private static Mock<IGitPlatformClient> NoExistingBranch()
    {
        var client = new Mock<IGitPlatformClient>();
        // candidate does not exist; base branch resolves.
        client.Setup(c => c.GetBranchAsync("o", "r", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BranchAbsent());
        SetupBranch(client, "main", BranchOk("main", "deadbeef"));
        return client;
    }

    [Test]
    public async Task ExecuteCore_HappyPath_CreatesBranch_AndCapturesBaseSha()
    {
        var client = new Mock<IGitPlatformClient>();
        client.SetupSequence(c => c.GetBranchAsync("o", "r", "adl/42-add-auth", It.IsAny<CancellationToken>()))
            .ReturnsAsync(BranchAbsent())                        // pre-create conflict check
            .ReturnsAsync(BranchOk("adl/42-add-auth"));          // post-create validation
        SetupBranch(client, "main", BranchOk("main", "deadbeef"));
        client.Setup(c => c.CreateBranchAsync(
                It.Is<Tamma.Platforms.Abstractions.Models.CreateBranchRequest>(r =>
                    r.Owner == "o" && r.RepoName == "r" && r.NewBranchName == "adl/42-add-auth" && r.FromSha == "deadbeef"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(BranchOk("adl/42-add-auth", "deadbeef"));

        var outcome = await CreateBranchActivity.ExecuteCoreAsync(
            client.Object, "o/r", 42, "adl/42-add-auth", "main", "suffix");

        outcome.Outcome.Should().Be("Created");
        outcome.BranchName.Should().Be("adl/42-add-auth");
        outcome.BaseSha.Should().Be("deadbeef");
        outcome.ConflictResolved.Should().BeFalse();
        outcome.ErrorCode.Should().BeNull();
    }

    [Test]
    public async Task ExecuteCore_BranchAlreadyExists_SuffixStrategy_CreatesSuffixed_NoDoubleCreate()
    {
        var client = new Mock<IGitPlatformClient>();
        // base candidate exists, -2 free
        SetupBranch(client, "adl/42-add-auth", BranchOk("adl/42-add-auth"));
        client.SetupSequence(c => c.GetBranchAsync("o", "r", "adl/42-add-auth-2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(BranchAbsent())                        // pre-create
            .ReturnsAsync(BranchOk("adl/42-add-auth-2"));        // post-create validation
        SetupBranch(client, "main", BranchOk("main", "sha2"));
        client.Setup(c => c.CreateBranchAsync(
                It.Is<Tamma.Platforms.Abstractions.Models.CreateBranchRequest>(r => r.NewBranchName == "adl/42-add-auth-2"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(BranchOk("adl/42-add-auth-2", "sha2"));

        var outcome = await CreateBranchActivity.ExecuteCoreAsync(
            client.Object, "o/r", 42, "adl/42-add-auth", "main", "suffix");

        outcome.Outcome.Should().Be("Created");
        outcome.BranchName.Should().Be("adl/42-add-auth-2");
        outcome.ConflictResolved.Should().BeTrue();
        // The original (existing) name must NOT have been (re)created.
        client.Verify(c => c.CreateBranchAsync(
            It.Is<Tamma.Platforms.Abstractions.Models.CreateBranchRequest>(r => r.NewBranchName == "adl/42-add-auth"),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ExecuteCore_BranchAlreadyExists_AbortStrategy_ReturnsExistsError_NoCreate()
    {
        var client = new Mock<IGitPlatformClient>();
        SetupBranch(client, "adl/42-add-auth", BranchOk("adl/42-add-auth"));

        var outcome = await CreateBranchActivity.ExecuteCoreAsync(
            client.Object, "o/r", 42, "adl/42-add-auth", "main", "abort");

        outcome.Outcome.Should().Be("Error");
        outcome.ErrorCode.Should().Be("branch_exists");
        client.Verify(c => c.CreateBranchAsync(
            It.IsAny<Tamma.Platforms.Abstractions.Models.CreateBranchRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ExecuteCore_CreateFailure_ReturnsError_NoFalseSuccess()
    {
        var client = NoExistingBranch();
        client.Setup(c => c.CreateBranchAsync(
                It.IsAny<Tamma.Platforms.Abstractions.Models.CreateBranchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PlatformResult<Tamma.Platforms.Abstractions.Models.Branch>.FromError(
                new PlatformError.PermissionDenied()));

        var outcome = await CreateBranchActivity.ExecuteCoreAsync(
            client.Object, "o/r", 42, "adl/42-add-auth", "main", "suffix");

        outcome.Outcome.Should().Be("Error");
        outcome.ErrorCode.Should().Be("permission_denied");
        outcome.BranchName.Should().BeNullOrEmpty();
    }

    [Test]
    public async Task ExecuteCore_BaseBranchMissing_ReturnsError_NoCreate()
    {
        var client = new Mock<IGitPlatformClient>();
        SetupBranch(client, "adl/42-add-auth", BranchAbsent());
        SetupBranch(client, "develop", BranchAbsent()); // base branch missing

        var outcome = await CreateBranchActivity.ExecuteCoreAsync(
            client.Object, "o/r", 42, "adl/42-add-auth", "develop", "suffix");

        outcome.Outcome.Should().Be("Error");
        outcome.ErrorCode.Should().Be("base_branch_not_found");
        client.Verify(c => c.CreateBranchAsync(
            It.IsAny<Tamma.Platforms.Abstractions.Models.CreateBranchRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ExecuteCore_PostCreateValidationFails_ReturnsError_NoFalseSuccess()
    {
        var client = new Mock<IGitPlatformClient>();
        client.SetupSequence(c => c.GetBranchAsync("o", "r", "adl/42-add-auth", It.IsAny<CancellationToken>()))
            .ReturnsAsync(BranchAbsent())    // pre-create
            .ReturnsAsync(BranchAbsent());   // post-create: STILL absent
        SetupBranch(client, "main", BranchOk("main"));
        client.Setup(c => c.CreateBranchAsync(
                It.IsAny<Tamma.Platforms.Abstractions.Models.CreateBranchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BranchOk("adl/42-add-auth"));

        var outcome = await CreateBranchActivity.ExecuteCoreAsync(
            client.Object, "o/r", 42, "adl/42-add-auth", "main", "suffix");

        outcome.Outcome.Should().Be("Error");
        outcome.ErrorCode.Should().Be("validation_failed");
    }

    [Test]
    public async Task ExecuteCore_InvalidRefName_ReturnsError_NoCreate()
    {
        var client = new Mock<IGitPlatformClient>();

        var outcome = await CreateBranchActivity.ExecuteCoreAsync(
            client.Object, "o/r", 42, "-bad..name", "main", "suffix");

        outcome.Outcome.Should().Be("Error");
        outcome.ErrorCode.Should().Be("invalid_ref");
        client.Verify(c => c.CreateBranchAsync(
            It.IsAny<Tamma.Platforms.Abstractions.Models.CreateBranchRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ExecuteCore_ConflictCheckApiError_ReturnsError_NotSilentCreate()
    {
        // A transient existence-lookup failure must NOT be treated as "absent → create".
        var client = new Mock<IGitPlatformClient>();
        SetupBranch(client, "adl/42-add-auth",
            PlatformResult<Tamma.Platforms.Abstractions.Models.Branch>.FromError(
                new PlatformError.ServiceUnavailable()));

        var outcome = await CreateBranchActivity.ExecuteCoreAsync(
            client.Object, "o/r", 42, "adl/42-add-auth", "main", "suffix");

        outcome.Outcome.Should().Be("Error");
        outcome.ErrorCode.Should().Be("transient");
        client.Verify(c => c.CreateBranchAsync(
            It.IsAny<Tamma.Platforms.Abstractions.Models.CreateBranchRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ExecuteCore_NeverThrows_OnUnexpectedException()
    {
        var client = new Mock<IGitPlatformClient>();
        client.Setup(c => c.GetBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var outcome = await CreateBranchActivity.ExecuteCoreAsync(
            client.Object, "o/r", 42, "adl/42-add-auth", "main", "suffix");

        outcome.Outcome.Should().Be("Error");
        outcome.ErrorCode.Should().NotBeNullOrEmpty();
    }

    // ================================================================
    // EmitBranchEventActivity.BuildTammaEvent — DCB mapping onto the drain
    // ================================================================

    [Test]
    public void BuildTammaEvent_SuccessType_SetsTypeStatusTagsAndData()
    {
        var evt = EmitBranchEventActivity.BuildTammaEvent(
            BranchEvents.CreatedSuccess, issueNumber: 12, repository: "o/r",
            tenantId: null,
            data: new Dictionary<string, object?> { ["baseSha"] = "sha", ["finalName"] = "adl/12-x" });

        evt.EventType.Should().Be("BRANCH.CREATED.SUCCESS");
        evt.Status.Should().Be("success");
        evt.Tags.Should().NotBeNull();
        evt.Tags!["issueId"].Should().Be("12");
        evt.Tags["issueNumber"].Should().Be("12");
        evt.Tags["repository"].Should().Be("o/r");
        evt.Tags.Should().NotContainKey("tenantId");
        evt.Data.Should().ContainKey("baseSha");
        evt.Data.Should().ContainKey("finalName");
    }

    [Test]
    public void BuildTammaEvent_FailedType_SetsErrorStatus()
    {
        var evt = EmitBranchEventActivity.BuildTammaEvent(
            BranchEvents.CreatedFailed, issueNumber: 7, repository: "o/r",
            tenantId: null, data: null);

        evt.EventType.Should().Be("BRANCH.CREATED.FAILED");
        evt.Status.Should().Be("error");
        evt.Data.Should().BeEmpty();
    }

    [Test]
    public void BuildTammaEvent_WithTenant_SetsTenantIdTag()
    {
        var tenant = Guid.NewGuid();
        var evt = EmitBranchEventActivity.BuildTammaEvent(
            BranchEvents.CreatedSuccess, 1, "o/r", tenantId: tenant, data: null);

        evt.Tags!["tenantId"].Should().Be(tenant.ToString("D"));
    }

    [Test]
    public void BranchEvents_ParseTenantId_HandlesEmptyAndValid()
    {
        BranchEvents.ParseTenantId(null).Should().BeNull();
        BranchEvents.ParseTenantId("").Should().BeNull();
        BranchEvents.ParseTenantId("not-a-guid").Should().BeNull();
        var g = Guid.NewGuid();
        BranchEvents.ParseTenantId(g.ToString()).Should().Be(g);
    }

    [Test]
    public void EmitBranchEvent_ParseData_HandlesEmptyAndMalformed()
    {
        EmitBranchEventActivity.ParseData(null).Should().BeNull();
        EmitBranchEventActivity.ParseData("").Should().BeNull();
        EmitBranchEventActivity.ParseData("{not json").Should().BeNull();
    }
}
