using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.AgentDispatch;
using Tamma.Api.Services.Git;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Api.Tests.AgentDispatch;

/// <summary>
/// Story 38-2 / Epic 31 P3 — <see cref="AgentDispatchMediationService"/>
/// composition (guard → driver resolution → platform-through-the-abstraction →
/// one DCB event).
///
/// <para><b>P3 note.</b> The pre-swap fixture faked the GitHub-only
/// <c>IGitHubActionsClient</c>. The SETUP moved onto <see cref="IPlatformResolver"/>
/// + <see cref="IGitPlatformActionsClient"/>; every BEHAVIORAL assertion (the
/// guard-first fail-closed order, the typed key-free failure taxonomy with
/// preserved platformStatusCode, only-429-retries dispatch, found=false
/// keep-waiting polls, AC7 terminal-only poll events, the exactly-one-event
/// invariant + tag shapes, credential safety, the exception-guard
/// PLATFORM_ERROR path) is unchanged from the pre-swap fixture — that is the
/// parity claim this file pins. New pins: the typed
/// <c>capability_unsupported</c> dispatch code and the "dispatch accepted but
/// uncorrelated" success arm.</para>
/// </summary>
[TestFixture]
public class AgentDispatchMediationServiceTests
{
    private const string Repo = "acme/widgets";
    private readonly Guid _tenant = Guid.NewGuid();

    private Mock<IGitRepoAuthorizer> _authorizer = null!;
    private Mock<IPlatformResolver> _resolver = null!;
    private Mock<IInstallationRepository> _installations = null!;
    private Mock<IGitPlatformClient> _client = null!;
    private FakePlatformActionsClient _actions = null!;
    private Mock<IActionsResultAggregator> _aggregator = null!;
    private RecordingEventRepository _events = null!;
    private AgentDispatchMediationService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _authorizer = new Mock<IGitRepoAuthorizer>(MockBehavior.Strict);
        _resolver = new Mock<IPlatformResolver>(MockBehavior.Loose);
        _installations = new Mock<IInstallationRepository>(MockBehavior.Loose);
        _client = new Mock<IGitPlatformClient>(MockBehavior.Loose);
        _actions = new FakePlatformActionsClient();
        _aggregator = new Mock<IActionsResultAggregator>(MockBehavior.Loose);
        _events = new RecordingEventRepository();

        // Default: the workflow-file probe finds the file.
        _client
            .Setup(c => c.GetFileContentAsync(It.IsAny<GetFileContentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PlatformResult<byte[]>.FromOk(new byte[] { 1 }));

        ResolveDriver();

        _sut = new AgentDispatchMediationService(
            _authorizer.Object, _resolver.Object, _installations.Object, _aggregator.Object, _events,
            NullLogger<AgentDispatchMediationService>.Instance);
    }

    private void ResolveDriver() => _resolver
        .Setup(r => r.ResolveForMediationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(new MediationDriverResolution(
            new FakePlatformDriver(_client.Object, _actions),
            MediationCredentialSource.TenantInstallation));

    private void NoDriver() => _resolver
        .Setup(r => r.ResolveForMediationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync((MediationDriverResolution?)null);

    private void Allow() => _authorizer
        .Setup(a => a.AuthorizeAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(GitRepoAuthorization.Allow());

    private void Deny() => _authorizer
        .Setup(a => a.AuthorizeAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(GitRepoAuthorization.Deny("not authorized"));

    private static DispatchAgentRunRequest DispatchBody() => new()
    { WorkflowFileName = "tamma-agent.yml", Ref = "tamma/issue-7", CorrelationId = "wf-1" };

    // ===================================================================
    // Cross-tenant guard (AC2) — FIRST, fail-closed
    // ===================================================================

    [Test]
    public async Task Trigger_GuardDenied_403Code_PlatformNeverCalled_OneFailedEvent()
    {
        Deny();

        var result = await _sut.TriggerRunAsync(_tenant, Repo, DispatchBody());

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(AgentDispatchFailureCodes.RepoNotAuthorized);
        result.CredentialSource.Should().BeNull("no driver is resolved on a guard denial");
        _actions.DispatchCalls.Should().BeEmpty("the guard runs BEFORE any platform call");
        _resolver.Verify(r => r.ResolveForMediationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);

        _events.Appended.Should().ContainSingle();
        var evt = _events.Appended.Single();
        evt.Type.Should().Be(AgentDispatchEventTypes.RunTriggeredFailed);
        evt.Tags.Should().Contain(AgentDispatchFailureCodes.RepoNotAuthorized);
        result.ToHttpResult().Let(StatusOf).Should().Be(403);
    }

    [Test]
    public async Task GetRun_GuardDenied_403_PlatformNeverCalled()
    {
        Deny();

        var result = await _sut.GetRunAsync(_tenant, Repo, 55, "wf-1");

        result.FailureCode.Should().Be(AgentDispatchFailureCodes.RepoNotAuthorized);
        _actions.GetRunCalls.Should().Be(0);
        result.ToHttpResult().Let(StatusOf).Should().Be(403);
    }

    [Test]
    public async Task Collect_GuardDenied_403_AggregatorNeverCalled()
    {
        Deny();

        var result = await _sut.CollectResultsAsync(_tenant, Repo, 55, new CollectAgentRunRequest { CorrelationId = "wf-1" });

        result.FailureCode.Should().Be(AgentDispatchFailureCodes.RepoNotAuthorized);
        _aggregator.Verify(a => a.AggregateAsync(
            It.IsAny<IGitPlatformDriver>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<CollectAgentRunRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        result.ToHttpResult().Let(StatusOf).Should().Be(403);
    }

    // ===================================================================
    // Dispatch (AC4/AC6) — happy + typed failures
    // ===================================================================

    [Test]
    public async Task Trigger_Success_EmitsOneSuccessEvent_InstallationLabel()
    {
        Allow();

        var result = await _sut.TriggerRunAsync(_tenant, Repo, DispatchBody());

        result.Success.Should().BeTrue();
        result.CredentialSource.Should().Be(AgentDispatchCredentialSources.Installation);
        _actions.DispatchCalls.Should().HaveCount(1);
        _actions.DispatchCalls[0].Owner.Should().Be("acme");
        _actions.DispatchCalls[0].Repo.Should().Be("widgets");
        result.WorkflowRunUrl.Should().Be("https://ci/run/1",
            "P1's pollable-run dispatch lets mediation surface the run URL (pre-swap: always null)");

        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(AgentDispatchEventTypes.RunTriggeredSuccess);
        result.ToHttpResult().Let(StatusOf).Should().Be(200);
        AssertTags(_events.Appended.Single(), AgentDispatchEventTypes.RunTriggerOperation, "installation", "wf-1");
    }

    [Test]
    public async Task Trigger_DispatchAcceptedButUncorrelated_IsSuccess_NoRunUrl()
    {
        // The GitHub driver's typed "dispatch accepted (204) but the created
        // run could not be correlated" — the dispatch HAPPENED; pre-swap this
        // was the plain 204 success with no run URL (monitor discovers it).
        Allow();
        _actions.OnDispatch = (_, _, _) => PlatformResult<WorkflowRun>.FromError(
            new PlatformError.Unknown("dispatch accepted (204) but the created run could not be correlated"));

        var result = await _sut.TriggerRunAsync(_tenant, Repo, DispatchBody());

        result.Success.Should().BeTrue();
        result.WorkflowRunUrl.Should().BeNull();
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(AgentDispatchEventTypes.RunTriggeredSuccess);
    }

    [Test]
    public async Task Trigger_WorkflowMissing_200SuccessFalse_WorkflowNotFound()
    {
        Allow();
        _client
            .Setup(c => c.GetFileContentAsync(It.IsAny<GetFileContentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PlatformResult<byte[]>.FromError(new PlatformError.NotFound()));

        var result = await _sut.TriggerRunAsync(_tenant, Repo, DispatchBody());

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(AgentDispatchFailureCodes.WorkflowNotFound);
        _actions.DispatchCalls.Should().BeEmpty();
        result.ToHttpResult().Let(StatusOf).Should().Be(200, "expected platform failures ride inside 200 success:false");
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(AgentDispatchEventTypes.RunTriggeredFailed);
    }

    [Test]
    public async Task Trigger_NoDriverResolved_200SuccessFalse_ActionsNotConfigured()
    {
        Allow();
        NoDriver();

        var result = await _sut.TriggerRunAsync(_tenant, Repo, DispatchBody());

        result.FailureCode.Should().Be(AgentDispatchFailureCodes.ActionsNotConfigured);
        result.ToHttpResult().Let(StatusOf).Should().Be(200, "no 503 token path — the credential is internal to the driver");
    }

    [Test]
    public async Task Trigger_DriverWithoutActionsSurface_ActionsNotConfigured()
    {
        Allow();
        _resolver
            .Setup(r => r.ResolveForMediationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediationDriverResolution(
                new FakePlatformDriver(_client.Object, actions: null),
                MediationCredentialSource.TenantInstallation));

        var result = await _sut.TriggerRunAsync(_tenant, Repo, DispatchBody());

        result.FailureCode.Should().Be(AgentDispatchFailureCodes.ActionsNotConfigured);
    }

    [Test]
    public async Task Trigger_403_DispatchRejected_PreservesPlatformStatusCode()
    {
        Allow();
        _actions.OnDispatch = (_, _, _) => PlatformResult<WorkflowRun>.FromError(new PlatformError.PermissionDenied());

        var result = await _sut.TriggerRunAsync(_tenant, Repo, DispatchBody());

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(AgentDispatchFailureCodes.DispatchRejected);
        result.PlatformStatusCode.Should().Be(403);
        ((int)StatusOf(result.ToHttpResult())).Should().BeLessThan(500, "a raw 5xx must NEVER leak");
    }

    [Test]
    public async Task Trigger_CapabilityUnsupported_SurfacesExactTypedCode()
    {
        Allow();
        _actions.OnDispatch = (_, _, _) => PlatformResult<WorkflowRun>.FromError(
            new PlatformError.InvalidRequest(PlatformErrorText.CapabilityUnsupportedCode, "no CI dispatch here"));

        var result = await _sut.TriggerRunAsync(_tenant, Repo, DispatchBody());

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(AgentDispatchFailureCodes.CapabilityUnsupported,
            "the typed refusal must never be coarsened into PLATFORM_ERROR (plan §4)");
    }

    [Test]
    public async Task Trigger_RetriesOnRateLimit_ThenSucceeds()
    {
        Allow();
        var attempts = 0;
        _actions.OnDispatch = (_, _, _) =>
        {
            attempts++;
            return attempts < 2
                ? PlatformResult<WorkflowRun>.FromError(new PlatformError.RateLimited(null))
                : PlatformResult<WorkflowRun>.FromOk(FakePlatformActionsClient.Run(id: "9", status: "queued", conclusion: null));
        };

        var result = await _sut.TriggerRunAsync(_tenant, Repo, DispatchBody());

        result.Success.Should().BeTrue();
        attempts.Should().Be(2);
    }

    [Test]
    public async Task Trigger_503_DoesNotRetry_SingleAttempt_TypedFailure()
    {
        // Review finding 4 — a 503 may arrive AFTER the platform already queued
        // the run, so the NON-idempotent dispatch must NOT auto-retry it (only
        // a rate-limit is retried). One attempt ⇒ one typed PLATFORM_ERROR.
        Allow();
        var attempts = 0;
        _actions.OnDispatch = (_, _, _) => { attempts++; return PlatformResult<WorkflowRun>.FromServiceUnavailable(); };

        var result = await _sut.TriggerRunAsync(_tenant, Repo, DispatchBody());

        attempts.Should().Be(1, "an ambiguous 5xx dispatch must be tried exactly once (may already be queued)");
        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(AgentDispatchFailureCodes.PlatformError);
        result.PlatformStatusCode.Should().Be(503);
        ((int)StatusOf(result.ToHttpResult())).Should().BeLessThan(500, "a raw 5xx must NEVER leak");
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(AgentDispatchEventTypes.RunTriggeredFailed);
    }

    // ===================================================================
    // Discover / poll (AC4)
    // ===================================================================

    [Test]
    public async Task GetRun_Found_Returns200_Found_OnePolledEvent()
    {
        Allow();
        _actions.RunsById["55"] = FakePlatformActionsClient.Run(id: "55", status: "completed", conclusion: "success", url: "https://gh/run/55");

        var result = await _sut.GetRunAsync(_tenant, Repo, 55, "wf-1");

        result.Success.Should().BeTrue();
        result.Found.Should().BeTrue();
        result.RunId.Should().Be(55);
        result.Status.Should().Be("completed");
        result.Conclusion.Should().Be("success");
        result.ToHttpResult().Let(StatusOf).Should().Be(200);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(AgentDispatchEventTypes.RunPolledSuccess);
    }

    [Test]
    public async Task GetRun_NotVisibleYet_Returns200_FoundFalse_NoEvent()
    {
        Allow(); // RunsById empty ⇒ GetRunStatusAsync answers NotFound.

        var result = await _sut.GetRunAsync(_tenant, Repo, 55, "wf-1");

        result.Success.Should().BeTrue("a not-yet-visible run is a successful poll the monitor keeps waiting on");
        result.Found.Should().BeFalse();
        result.ToHttpResult().Let(StatusOf).Should().Be(200);
        _events.Appended.Should().BeEmpty("AC7 — a non-terminal (not-found) poll emits no DCB event");
    }

    [Test]
    public async Task GetRun_InProgress_Returns200_Found_NoEvent()
    {
        Allow();
        _actions.RunsById["55"] = FakePlatformActionsClient.Run(id: "55", status: "in_progress", conclusion: null, url: "https://gh/run/55");

        var result = await _sut.GetRunAsync(_tenant, Repo, 55, "wf-1");

        result.Success.Should().BeTrue();
        result.Found.Should().BeTrue();
        result.Status.Should().Be("in_progress");
        _events.Appended.Should().BeEmpty("AC7 — a routine in-progress poll emits no DCB event");
    }

    [Test]
    public async Task Discover_InProgressRun_ReturnsLatest_NoEvent()
    {
        Allow();
        _actions.DefaultListRuns = new[]
        {
            FakePlatformActionsClient.Run(id: "77", status: "in_progress", conclusion: null),
        };

        var result = await _sut.DiscoverRunAsync(_tenant, Repo, "tamma/issue-7", DateTime.UtcNow.AddMinutes(-1), "wf-1");

        result.Found.Should().BeTrue();
        result.RunId.Should().Be(77);
        _events.Appended.Should().BeEmpty("AC7 — discovering an in-progress run is a non-terminal poll (no event)");
    }

    [Test]
    public async Task Discover_TerminalRun_EmitsOnePolledEvent()
    {
        Allow();
        _actions.DefaultListRuns = new[]
        {
            FakePlatformActionsClient.Run(id: "88", status: "completed", conclusion: "success"),
        };

        var result = await _sut.DiscoverRunAsync(_tenant, Repo, "tamma/issue-7", DateTime.UtcNow.AddMinutes(-1), "wf-1");

        result.Found.Should().BeTrue();
        result.RunId.Should().Be(88);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(AgentDispatchEventTypes.RunPolledSuccess);
    }

    [Test]
    public async Task Discover_RunOlderThanDispatchWindow_FoundFalse()
    {
        Allow();
        _actions.DefaultListRuns = new[]
        {
            FakePlatformActionsClient.Run(id: "12", status: "completed", conclusion: "success",
                started: DateTimeOffset.UtcNow.AddHours(-2)),
        };

        var result = await _sut.DiscoverRunAsync(_tenant, Repo, "tamma/issue-7", DateTime.UtcNow.AddMinutes(-1), "wf-1");

        result.Found.Should().BeFalse("a stale run from before the dispatch window is not OUR run");
    }

    [Test]
    public async Task Discover_GuardDenied_403_PlatformNeverCalled_OneFailedEvent()
    {
        Deny();

        var result = await _sut.DiscoverRunAsync(_tenant, Repo, "tamma/issue-7", DateTime.UtcNow.AddMinutes(-1), "wf-1");

        result.Success.Should().BeFalse();
        result.Found.Should().BeFalse();
        result.FailureCode.Should().Be(AgentDispatchFailureCodes.RepoNotAuthorized);
        _actions.ListRunsCalls.Should().Be(0, "the guard runs BEFORE any platform read");
        result.ToHttpResult().Let(StatusOf).Should().Be(403);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(AgentDispatchEventTypes.RunPolledFailed);
    }

    // ===================================================================
    // Collect (AC4)
    // ===================================================================

    [Test]
    public async Task Collect_DelegatesToAggregator_WithTheResolvedDriver_EmitsOneCollectedEvent()
    {
        Allow();
        _aggregator
            .Setup(a => a.AggregateAsync(
                It.IsAny<IGitPlatformDriver>(), It.IsAny<Guid?>(), "acme", "widgets", 99,
                It.IsAny<CollectAgentRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResultsResult { Success = true, AgentSuccess = true, PrNumber = 7, CredentialSource = "installation" });

        var result = await _sut.CollectResultsAsync(_tenant, Repo, 99, new CollectAgentRunRequest { CorrelationId = "wf-1" });

        result.Success.Should().BeTrue();
        result.AgentSuccess.Should().BeTrue();
        result.PrNumber.Should().Be(7);
        _events.Appended.Should().ContainSingle().Which.Type.Should().Be(AgentDispatchEventTypes.ResultsCollectedSuccess);
        result.ToHttpResult().Let(StatusOf).Should().Be(200);
    }

    // ===================================================================
    // Installation resolution (webhook wait-key scoping) — NO DCB event
    // ===================================================================

    [Test]
    public async Task ResolveInstallation_Allowed_ReturnsId_NoEvent()
    {
        Allow();
        _installations
            .Setup(i => i.GetByRepoFullNameAsync(Repo))
            .ReturnsAsync(new GitHubInstallation { InstallationId = 4242 });

        var result = await _sut.ResolveInstallationAsync(_tenant, Repo, "wf-1");

        result.Success.Should().BeTrue();
        result.InstallationId.Should().Be(4242);
        _events.Appended.Should().BeEmpty("an installation lookup is not a run-lifecycle audit event");
    }

    [Test]
    public async Task ResolveInstallation_GuardDenied_403()
    {
        Deny();

        var result = await _sut.ResolveInstallationAsync(_tenant, Repo, "wf-1");

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(AgentDispatchFailureCodes.RepoNotAuthorized);
    }

    // ===================================================================
    // Exception guard (AC6) — PLATFORM_ERROR, never a raw 5xx, one event
    // ===================================================================

    [Test]
    public async Task GuardThrows_TypedPlatformError_OneFailedEvent_No5xx()
    {
        _authorizer
            .Setup(a => a.AuthorizeAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("installation lookup DB down"));

        Func<Task> act = async () => await _sut.TriggerRunAsync(_tenant, Repo, DispatchBody());
        await act.Should().NotThrowAsync("a guard exception must never surface as a raw 5xx");

        var result = await _sut.TriggerRunAsync(_tenant, Repo, DispatchBody());
        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(AgentDispatchFailureCodes.PlatformError);
        result.ToHttpResult().Let(StatusOf).Should().Be(200);
        _events.Appended.Should().OnlyContain(e => e.Type == AgentDispatchEventTypes.RunTriggeredFailed);
        _events.Appended.Should().HaveCount(2);
    }

    [Test]
    public async Task Trigger_TaskCanceled_NotCallerCancellation_TypedPlatformError_OneFailedEvent_No5xx()
    {
        // Review finding 5 — an HttpClient/dispatch TIMEOUT surfaces as a
        // TaskCanceledException whose token is NOT the caller's ct. It must NOT rethrow
        // (raw 500 + skipped FAILED event); it is a typed PLATFORM_ERROR with one event.
        _authorizer
            .Setup(a => a.AuthorizeAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("HttpClient timeout"));

        using var uncanceled = new CancellationTokenSource(); // NOT cancelled → not a caller cancellation

        Func<Task> act = async () => await _sut.TriggerRunAsync(_tenant, Repo, DispatchBody(), uncanceled.Token);
        await act.Should().NotThrowAsync("a non-caller TaskCanceledException must not surface as a raw 5xx");

        var result = await _sut.TriggerRunAsync(_tenant, Repo, DispatchBody(), uncanceled.Token);
        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(AgentDispatchFailureCodes.PlatformError);
        result.ToHttpResult().Let(StatusOf).Should().Be(200);
        _events.Appended.Should().OnlyContain(e => e.Type == AgentDispatchEventTypes.RunTriggeredFailed);
        _events.Appended.Should().HaveCount(2);
    }

    [Test]
    public async Task Trigger_CallerCancellation_Propagates()
    {
        // The counterpart to finding 5 — a genuine CALLER cancellation still propagates.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _authorizer
            .Setup(a => a.AuthorizeAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        Func<Task> act = async () => await _sut.TriggerRunAsync(_tenant, Repo, DispatchBody(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _events.Appended.Should().BeEmpty("a caller cancellation is not a platform failure — no FAILED event");
    }

    // ===================================================================
    // Credential safety (AC3/AC7) — only the "installation" label surfaces
    // ===================================================================

    [Test]
    public async Task CredentialSafety_OnlyInstallationLabel_NoTokenInEventsOrResult()
    {
        Allow();

        var result = await _sut.TriggerRunAsync(_tenant, Repo, DispatchBody());

        result.CredentialSource.Should().Be("installation");
        var serialized = JsonSerializer.Serialize(result);
        serialized.Should().NotContain("ghs_").And.NotContain("ghp_");
        foreach (var evt in _events.Appended)
        {
            (evt.Tags + evt.Data + evt.Metadata).Should().NotContain("ghs_").And.NotContain("ghp_");
        }
    }

    // ===================================================================
    // Helpers
    // ===================================================================

    private static void AssertTags(DomainEvent evt, string operation, string credentialSource, string correlationId)
    {
        using var doc = JsonDocument.Parse(evt.Tags);
        var root = doc.RootElement;
        root.GetProperty("repo").GetString().Should().Be(Repo);
        root.GetProperty("operation").GetString().Should().Be(operation);
        root.GetProperty("credentialSource").GetString().Should().Be(credentialSource);
        root.GetProperty("correlationId").GetString().Should().Be(correlationId);
        root.TryGetProperty("tenantId", out _).Should().BeTrue();
    }

    private static int StatusOf(Microsoft.AspNetCore.Http.IResult result)
    {
        var prop = result.GetType().GetProperty("StatusCode");
        var value = prop?.GetValue(result);
        return value is int code ? code : 200;
    }

    private sealed class RecordingEventRepository : IEventRepository
    {
        public ConcurrentBag<DomainEvent> Appended { get; } = new();

        public Task<DomainEvent> AppendAsync(DomainEvent evt)
        {
            Appended.Add(evt);
            return Task.FromResult(evt);
        }

        public Task<DomainEvent?> GetByIdAsync(Guid id) => Task.FromResult<DomainEvent?>(null);
        public Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit)
            => Task.FromResult(new List<DomainEvent>());
        public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type) => Task.FromResult<DomainEvent?>(null);
        public Task ClearAsync(Guid tenantId) => Task.CompletedTask;
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(
            Guid? tenantId, string? type, int? issueNumber, int limit, int offset)
            => Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(
            Guid tenantId, string? typePrefix, int limit, int offset)
            => Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
    }
}

/// <summary>Fluent one-liner helper to pipe an IResult through StatusOf.</summary>
internal static class AgentDispatchTestPipe
{
    public static TOut Let<TIn, TOut>(this TIn value, Func<TIn, TOut> f) => f(value);
}
