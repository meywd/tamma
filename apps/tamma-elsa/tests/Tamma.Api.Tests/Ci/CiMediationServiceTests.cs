using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Ci;
using Tamma.Api.Services.Git;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Api.Tests.Ci;

/// <summary>
/// Story 38 (Phase 1) / Epic 31 P3 — <see cref="CiMediationService"/> composition
/// (guard → driver resolution → Actions-through-the-abstraction → one DCB event).
///
/// <para><b>P3 note.</b> The pre-swap fixture mocked <c>ICiClientFactory</c> +
/// the GitHub-only <c>ICIIntegrationService</c>. The SETUP moved onto
/// <see cref="IPlatformResolver"/> + <see cref="IGitPlatformActionsClient"/>
/// (mirroring the P2 git-mediation swap); every BEHAVIORAL assertion (status
/// codes, failure codes, outcomes, event types, credential-source labels, the
/// no-leak invariant) is unchanged from the pre-swap fixture — that is the
/// parity claim this file pins. New pins: the PlatformErrorText wire-string
/// projection (status-prefixed reasons → platformStatusCode) and the
/// first-class <c>capability_unsupported</c> taxonomy (plan §4).</para>
/// </summary>
[TestFixture]
public class CiMediationServiceTests
{
    private const string Repo = "acme/widgets";

    private Mock<IGitRepoAuthorizer> _authorizer = null!;
    private Mock<IPlatformResolver> _resolver = null!;
    private Mock<IGitPlatformActionsClient> _actions = null!;
    private RecordingEventRepository _events = null!;
    private CiMediationService _sut = null!;
    private readonly Guid _tenant = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _authorizer = new Mock<IGitRepoAuthorizer>(MockBehavior.Strict);
        _resolver = new Mock<IPlatformResolver>(MockBehavior.Strict);
        _actions = new Mock<IGitPlatformActionsClient>(MockBehavior.Loose);
        _events = new RecordingEventRepository();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CI:PollIntervalMs"] = "1",
                ["CI:PollMaxAttempts"] = "3",
            })
            .Build();

        _sut = new CiMediationService(
            _authorizer.Object, _resolver.Object, _events, config,
            NullLogger<CiMediationService>.Instance);
    }

    private void Allow() => _authorizer
        .Setup(a => a.AuthorizeAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(GitRepoAuthorization.Allow());

    private void Deny() => _authorizer
        .Setup(a => a.AuthorizeAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(GitRepoAuthorization.Deny("not authorized"));

    private sealed class FakeDriver : IGitPlatformDriver
    {
        public FakeDriver(IGitPlatformActionsClient? actions) => Actions = actions;
        public PlatformKind Kind => PlatformKind.GitHub;
        public IGitPlatformClient Client { get; } = Mock.Of<IGitPlatformClient>();
        public IGitPlatformActionsClient? Actions { get; }
        public IReadOnlySet<PlatformCapability> Capabilities { get; } =
            new HashSet<PlatformCapability> { PlatformCapability.Actions };
    }

    private void ResolveDriver(string source, IGitPlatformActionsClient? actions) => _resolver
        .Setup(r => r.ResolveForMediationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(new MediationDriverResolution(
            new FakeDriver(actions),
            source == GitCredentialSources.Byok
                ? MediationCredentialSource.TenantInstallation
                : MediationCredentialSource.PlatformDefault));

    private void NoDriver() => _resolver
        .Setup(r => r.ResolveForMediationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync((MediationDriverResolution?)null);

    private static TriggerTestsRequest TriggerBody() => new() { Branch = "feature", CorrelationId = "corr-ci" };

    private static WorkflowRun Run(string runId = "42", string status = "completed", string? conclusion = "success") =>
        new(runId, status, conclusion, "https://gh/run/42", DateTimeOffset.UtcNow, conclusion is null ? null : DateTimeOffset.UtcNow, null);

    private static PlatformResult<T> Ok<T>(T value) => PlatformResult<T>.FromOk(value);
    private static PlatformResult<T> Fail<T>(PlatformError error) => PlatformResult<T>.FromError(error);

    private static PlatformResult<IReadOnlyList<WorkflowRun>> Runs(params WorkflowRun[] runs) =>
        PlatformResult<IReadOnlyList<WorkflowRun>>.FromOk(runs.ToList());

    // ================================================================
    // Guard-first / fail-closed order (pre-swap parity)
    // ================================================================

    [Test]
    public async Task TriggerTests_GuardDenied_403_NoDriverResolved_PlatformNeverCalled()
    {
        Deny();

        var result = await _sut.TriggerTestsAsync(_tenant, Repo, TriggerBody());

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(CiFailureCodes.RepoNotAuthorized);
        result.CredentialSource.Should().BeNull();

        _resolver.Verify(r => r.ResolveForMediationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        _actions.VerifyNoOtherCalls();

        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(CiEventTypes.TestsTriggeredFailed);
    }

    [Test]
    public async Task TriggerTests_Success_Byok_ReturnsPollableRunId_OneSuccessEvent()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Byok, _actions.Object);
        _actions.Setup(a => a.DispatchWorkflowAsync(
                "acme", "widgets",
                It.Is<WorkflowDispatchRequest>(r => r.Ref == "feature" && r.WorkflowFileName == CiMediationService.DefaultWorkflowFile),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Run(runId: "42", status: "completed", conclusion: "success")));

        var result = await _sut.TriggerTestsAsync(_tenant, Repo, TriggerBody());

        result.Success.Should().BeTrue();
        result.Outcome.Should().Be("Triggered");
        result.TestRun!.RunId.Should().Be("42");
        result.TestRun.Status.Should().Be("success",
            "a terminal run surfaces the platform CONCLUSION as Status (pre-swap contract)");
        result.CredentialSource.Should().Be(GitCredentialSources.Byok);

        var evt = _events.Appended.Should().ContainSingle().Subject;
        evt.Type.Should().Be(CiEventTypes.TestsTriggeredSuccess);
        evt.TenantId.Should().Be(_tenant);
    }

    [Test]
    public async Task TriggerTests_PollsRunToTerminal_ThenReturnsConclusion()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Platform, _actions.Object);
        _actions.Setup(a => a.DispatchWorkflowAsync(
                "acme", "widgets", It.IsAny<WorkflowDispatchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Run(runId: "77", status: "in_progress", conclusion: null)));
        var polls = 0;
        _actions.Setup(a => a.GetRunStatusAsync("acme", "widgets", "77", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++polls < 2
                ? Ok(Run(runId: "77", status: "in_progress", conclusion: null))
                : Ok(Run(runId: "77", status: "completed", conclusion: "failure")));

        var result = await _sut.TriggerTestsAsync(_tenant, Repo, TriggerBody());

        result.Success.Should().BeTrue();
        result.TestRun!.RunId.Should().Be("77");
        result.TestRun.Status.Should().Be("failure");
        polls.Should().Be(2, "polling stops at the first terminal status");
    }

    [Test]
    public async Task TriggerTests_StillRunningAfterPollBudget_ReturnsInProgressStatus()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Platform, _actions.Object);
        _actions.Setup(a => a.DispatchWorkflowAsync(
                "acme", "widgets", It.IsAny<WorkflowDispatchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Run(runId: "9", status: "queued", conclusion: null)));
        _actions.Setup(a => a.GetRunStatusAsync("acme", "widgets", "9", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Run(runId: "9", status: "in_progress", conclusion: null)));

        var result = await _sut.TriggerTestsAsync(_tenant, Repo, TriggerBody());

        result.Success.Should().BeTrue(
            "an in-progress run after the poll budget is the pre-swap 'last-observed state' success");
        result.TestRun!.Status.Should().Be("in_progress");
        _actions.Verify(a => a.GetRunStatusAsync("acme", "widgets", "9", It.IsAny<CancellationToken>()),
            Times.Exactly(3), "CI:PollMaxAttempts bounds the poll loop");
    }

    [Test]
    public async Task TriggerTests_ExplicitWorkflowFile_OverridesConfiguredDefault()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Platform, _actions.Object);
        WorkflowDispatchRequest? seen = null;
        _actions.Setup(a => a.DispatchWorkflowAsync(
                "acme", "widgets", It.IsAny<WorkflowDispatchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, WorkflowDispatchRequest, CancellationToken>((_, _, r, _) => seen = r)
            .ReturnsAsync(Ok(Run()));

        await _sut.TriggerTestsAsync(_tenant, Repo, new TriggerTestsRequest
        {
            Branch = "feature",
            CorrelationId = "corr-wf",
            WorkflowFile = "agent.yml",
            Inputs = new Dictionary<string, string> { ["issue"] = "7" },
        });

        seen.Should().NotBeNull();
        seen!.WorkflowFileName.Should().Be("agent.yml");
        seen.Inputs.Should().ContainKey("issue");
    }

    // ================================================================
    // Token / driver unavailable (503 contract preserved)
    // ================================================================

    [Test]
    public async Task TriggerTests_NoDriver_TokenUnavailable_FailClosed_PlatformNeverCalled()
    {
        Allow();
        NoDriver();

        var result = await _sut.TriggerTestsAsync(_tenant, Repo, TriggerBody());

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(CiFailureCodes.TokenUnavailable);
        _actions.VerifyNoOtherCalls();
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(CiEventTypes.TestsTriggeredFailed);
    }

    // ================================================================
    // capability_unsupported — FIRST-CLASS (plan §4)
    // ================================================================

    [Test]
    public async Task TriggerTests_DriverWithoutActionsSurface_CapabilityUnsupported_NotPlatformError()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Byok, actions: null);

        var result = await _sut.TriggerTestsAsync(_tenant, Repo, TriggerBody());

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(CiFailureCodes.CapabilityUnsupported,
            "a driver without an Actions surface is a typed capability refusal, never a coarse PLATFORM_ERROR");
        result.CredentialSource.Should().Be(GitCredentialSources.Byok);

        var evt = _events.Appended.Should().ContainSingle().Subject;
        evt.Type.Should().Be(CiEventTypes.TestsTriggeredFailed);
        evt.Tags.Should().Contain("capability_unsupported");
    }

    [Test]
    public async Task TriggerTests_TypedCapabilityRefusalFromPlatform_SurfacesExactCode()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Platform, _actions.Object);
        _actions.Setup(a => a.DispatchWorkflowAsync(
                "acme", "widgets", It.IsAny<WorkflowDispatchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<WorkflowRun>(new PlatformError.InvalidRequest(
                PlatformErrorText.CapabilityUnsupportedCode, "CI dispatch is not supported here")));

        var result = await _sut.TriggerTestsAsync(_tenant, Repo, TriggerBody());

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(CiFailureCodes.CapabilityUnsupported);
    }

    // ================================================================
    // Wire error strings — PlatformErrorText parity (pinned BEFORE the swap)
    // ================================================================

    [Test]
    public async Task GetBuildStatus_PermissionDenied_ProjectsLegacy403String_PreservesStatus()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Platform, _actions.Object);
        _actions.Setup(a => a.ListRunsAsync(
                "acme", "widgets", It.IsAny<ListWorkflowRunsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fail<IReadOnlyList<WorkflowRun>>(new PlatformError.PermissionDenied()));

        var result = await _sut.GetBuildStatusAsync(_tenant, Repo, "feature", "corr-b");

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(CiFailureCodes.PlatformError);
        result.FailureReason.Should().Be("403: permission denied",
            "the driver's typed error projects into the SAME status-prefixed wire string family the live path produced");
        result.PlatformStatusCode.Should().Be(403);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(CiEventTypes.BuildStatusReadFailed);
    }

    [Test]
    public async Task TriggerTests_ServiceUnavailable_ProjectsLegacy503String()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Platform, _actions.Object);
        _actions.Setup(a => a.DispatchWorkflowAsync(
                "acme", "widgets", It.IsAny<WorkflowDispatchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PlatformResult<WorkflowRun>.FromServiceUnavailable());

        var result = await _sut.TriggerTestsAsync(_tenant, Repo, TriggerBody());

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(CiFailureCodes.PlatformError);
        result.FailureReason.Should().Be("503: platform unavailable");
        result.PlatformStatusCode.Should().Be(503);
    }

    // ================================================================
    // Build status (latest run on branch via ListRunsAsync)
    // ================================================================

    [Test]
    public async Task GetBuildStatus_Success_Platform_MapsLatestRun_StampsPlatformSource()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Platform, _actions.Object);
        _actions.Setup(a => a.ListRunsAsync(
                "acme", "widgets",
                It.Is<ListWorkflowRunsRequest>(r => r.Branch == "feature" && r.PerPage == 1),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Runs(Run(runId: "42", status: "completed", conclusion: "success")));

        var result = await _sut.GetBuildStatusAsync(_tenant, Repo, "feature", "corr-b");

        result.Success.Should().BeTrue();
        result.Outcome.Should().Be("Read");
        result.BuildStatus!.Status.Should().Be("success");
        result.BuildStatus.BuildUrl.Should().Be("https://gh/run/42");
        result.CredentialSource.Should().Be(GitCredentialSources.Platform);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(CiEventTypes.BuildStatusReadSuccess);
    }

    [Test]
    public async Task GetBuildStatus_NoRuns_IsSuccessfulNoRunsRead()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Platform, _actions.Object);
        _actions.Setup(a => a.ListRunsAsync(
                "acme", "widgets", It.IsAny<ListWorkflowRunsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Runs());

        var result = await _sut.GetBuildStatusAsync(_tenant, Repo, "feature", "corr-b");

        result.Success.Should().BeTrue();
        result.BuildStatus!.Status.Should().Be("NoRuns",
            "an empty branch history is a successful read (pre-swap contract), not an error");
    }

    [Test]
    public async Task GetBuildStatus_InProgressRun_SurfacesPlatformStatus()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Platform, _actions.Object);
        _actions.Setup(a => a.ListRunsAsync(
                "acme", "widgets", It.IsAny<ListWorkflowRunsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Runs(Run(runId: "8", status: "in_progress", conclusion: null)));

        var result = await _sut.GetBuildStatusAsync(_tenant, Repo, "feature", "corr-b");

        result.Success.Should().BeTrue();
        result.BuildStatus!.Status.Should().Be("in_progress");
        result.BuildStatus.FinishedAt.Should().BeNull();
    }

    // ================================================================
    // No-throw + one-event invariants (pre-swap parity)
    // ================================================================

    [Test]
    public async Task PlatformThrows_TypedPlatformError_OneFailedEvent()
    {
        Allow();
        ResolveDriver(GitCredentialSources.Byok, _actions.Object);
        _actions.Setup(a => a.DispatchWorkflowAsync(
                "acme", "widgets", It.IsAny<WorkflowDispatchRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await _sut.TriggerTestsAsync(_tenant, Repo, TriggerBody());

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(CiFailureCodes.PlatformError);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(CiEventTypes.TestsTriggeredFailed);
    }

    [Test]
    public async Task CredentialSafety_NoCredentialEverEntersTheMediationLayer()
    {
        // Post-swap the credential lives INSIDE the resolved driver — the
        // mediation result and audit events can only ever carry the LABEL.
        Allow();
        ResolveDriver(GitCredentialSources.Byok, _actions.Object);
        _actions.Setup(a => a.DispatchWorkflowAsync(
                "acme", "widgets", It.IsAny<WorkflowDispatchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(Run()));

        var result = await _sut.TriggerTestsAsync(_tenant, Repo, TriggerBody());

        var wire = JsonSerializer.Serialize(result);
        wire.Should().NotContain("ghp-", "no token-shaped material may appear on the wire");
        result.CredentialSource.Should().Be(GitCredentialSources.Byok);
        foreach (var evt in _events.Appended)
        {
            (evt.Tags + evt.Data + evt.Metadata).Should().NotContain("ghp-");
        }
    }

    private sealed class RecordingEventRepository : IEventRepository
    {
        public ConcurrentBag<DomainEvent> Appended { get; } = new();
        public Task<DomainEvent> AppendAsync(DomainEvent evt) { Appended.Add(evt); return Task.FromResult(evt); }
        public Task<DomainEvent?> GetByIdAsync(Guid id) => Task.FromResult<DomainEvent?>(null);
        public Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit) => Task.FromResult(new List<DomainEvent>());
        public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type) => Task.FromResult<DomainEvent?>(null);
        public Task ClearAsync(Guid tenantId) => Task.CompletedTask;
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(Guid? tenantId, string? type, int? issueNumber, int limit, int offset)
            => Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(Guid tenantId, string? typePrefix, int limit, int offset)
            => Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
    }
}
