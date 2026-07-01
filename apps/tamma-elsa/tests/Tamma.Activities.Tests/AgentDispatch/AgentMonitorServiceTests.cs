using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.AgentDispatch;
using Tamma.Activities.AgentDispatch.Models;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.Tests.AgentDispatch;

/// <summary>
/// Story 38-2 — the <see cref="AgentMonitorService"/> KEEPS its discover→poll loop
/// (timeout / backoff / consecutive-error cap / webhook modes) engine-side; each
/// read is now MEDIATED through <see cref="FakeTammaApiClient"/> instead of the
/// deleted <c>IGitHubActionsClient</c>. These tests are the pre-cutover coverage
/// re-pointed at the mediated seam — the loop semantics are byte-for-byte the same.
/// </summary>
[TestFixture]
public class AgentMonitorServiceTests
{
    private static AgentExecutionRequest MakeRequest(string repo = "acme/widgets", Guid? tenantId = null) =>
        new(
            Repository: repo,
            BranchName: "tamma/issue-42",
            IssueNumber: 42,
            IssueTitle: string.Empty,
            Task: "implement",
            PlanJson: "{}",
            SessionId: "sess_abc",
            AgentProvider: "claude-code",
            AgentConfigJson: null,
            WorkflowFileName: null,
            TimeoutMinutes: 35,
            TenantId: tenantId);

    private static AgentRunStatusApiResponse Status(long id, string status, string conclusion = "") =>
        new()
        {
            Success = true,
            Found = true,
            RunId = id,
            Status = status,
            Conclusion = conclusion,
            WorkflowRunUrl = $"https://github.com/acme/widgets/actions/runs/{id}",
            HeadBranch = "tamma/issue-42",
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTime.UtcNow,
            ArtifactsUrl = $"https://api.github.com/repos/acme/widgets/actions/runs/{id}/artifacts",
            CredentialSource = "installation",
        };

    private static AgentRunStatusApiResponse NoRunYet() =>
        new() { Success = true, Found = false, CredentialSource = "installation" };

    [Test]
    public async Task MonitorAsync_ReturnsSuccess_WhenRunAlreadyComplete()
    {
        var api = new FakeTammaApiClient { DefaultDiscover = Status(12345, "completed", "success") };
        var svc = new AgentMonitorService(api, logger: null, new ImmediateDelayProvider());

        var result = await svc.MonitorAsync(MakeRequest(), DateTime.UtcNow.AddMinutes(-1), AgentMonitorOptions.Default);

        result.WorkflowRunId.Should().Be(12345);
        result.Status.Should().Be("completed");
        result.Conclusion.Should().Be("success");
    }

    [Test]
    public async Task MonitorAsync_PollsUntilCompletion()
    {
        var api = new FakeTammaApiClient { DefaultDiscover = Status(100, "in_progress") };
        api.GetRunQueue.Enqueue(Status(100, "in_progress"));
        api.GetRunQueue.Enqueue(Status(100, "in_progress"));
        api.GetRunQueue.Enqueue(Status(100, "completed", "success"));
        var svc = new AgentMonitorService(api, logger: null, new ImmediateDelayProvider());

        var result = await svc.MonitorAsync(
            MakeRequest(), DateTime.UtcNow.AddMinutes(-1),
            new AgentMonitorOptions(PollIntervalSeconds: 30, TimeoutMinutes: 5));

        result.Conclusion.Should().Be("success");
        api.GetRunCalls.Should().Be(3);
    }

    [Test]
    public async Task MonitorAsync_ReturnsNotFound_WhenDiscoveryTimesOut()
    {
        var api = new FakeTammaApiClient { DefaultDiscover = NoRunYet() };
        var svc = new AgentMonitorService(api, logger: null, new ImmediateDelayProvider());

        var result = await svc.MonitorAsync(
            MakeRequest(), DateTime.UtcNow,
            new AgentMonitorOptions(PollIntervalSeconds: 30, TimeoutMinutes: 1, DiscoveryTimeoutSeconds: 1));

        result.Status.Should().Be("error");
        result.Conclusion.Should().Be("not_found");
        api.DiscoverCalls.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task MonitorAsync_ReturnsFailure_ForFailedRun()
    {
        var api = new FakeTammaApiClient { DefaultDiscover = Status(777, "completed", "failure") };
        var svc = new AgentMonitorService(api, logger: null, new ImmediateDelayProvider());

        var result = await svc.MonitorAsync(MakeRequest(), DateTime.UtcNow, AgentMonitorOptions.Default);

        result.Conclusion.Should().Be("failure");
    }

    [Test]
    public async Task MonitorAsync_PollDeadlineExpires_ReturnsTimedOut()
    {
        // Review finding 7 — the poll phase discovers an in-progress run but the
        // per-run deadline (TimeoutMinutes=0) elapses before it reaches a terminal
        // status ⇒ the monitor concludes "timed_out" (status "completed"), which the
        // activity routes to Failed. Distinct from discovery-timeout ("not_found").
        var api = new FakeTammaApiClient { DefaultDiscover = Status(4242, "in_progress") };
        var svc = new AgentMonitorService(api, logger: null, new ImmediateDelayProvider());

        var result = await svc.MonitorAsync(
            MakeRequest(), DateTime.UtcNow.AddMinutes(-1),
            new AgentMonitorOptions(PollIntervalSeconds: 30, TimeoutMinutes: 0, DiscoveryTimeoutSeconds: 5));

        result.Status.Should().Be("completed");
        result.Conclusion.Should().Be("timed_out");
        result.WorkflowRunId.Should().Be(4242);
        api.DiscoverCalls.Should().BeGreaterThan(0, "the run was discovered before the poll deadline expired");
    }

    [Test]
    public async Task MonitorAsync_BailsAfterConsecutivePollErrors()
    {
        // Discovery finds an in-progress run; every subsequent poll returns null
        // (mediation transient) → consecutive-error cap trips → monitor_failed.
        var api = new FakeTammaApiClient { DefaultDiscover = Status(500, "in_progress"), DefaultGetRun = null };
        var svc = new AgentMonitorService(api, logger: null, new ImmediateDelayProvider());

        var result = await svc.MonitorAsync(
            MakeRequest(), DateTime.UtcNow,
            new AgentMonitorOptions(PollIntervalSeconds: 30, TimeoutMinutes: 60, MaxConsecutiveErrors: 3));

        result.Conclusion.Should().Be("monitor_failed");
    }

    [Test]
    public async Task MonitorAsync_InvalidRepoReturnsNotFound()
    {
        var api = new FakeTammaApiClient();
        var svc = new AgentMonitorService(api, logger: null, new ImmediateDelayProvider());

        var result = await svc.MonitorAsync(MakeRequest(repo: "bad"), DateTime.UtcNow, AgentMonitorOptions.Default);

        result.Conclusion.Should().Be("not_found");
        api.DiscoverCalls.Should().Be(0, "a malformed repo never reaches the API");
    }

    // ================================================================
    // Story 19-3 AC-7 — webhook mode (inbound path unchanged; only the
    // installation-id lookup that scopes the wait key is now mediated)
    // ================================================================

    [Test]
    public async Task MonitorAsync_WebhookMode_ResumesOnSignal()
    {
        var registry = new WebhookSignalRegistry();
        var api = new FakeTammaApiClient();
        var svc = new AgentMonitorService(api, logger: null, new ImmediateDelayProvider(), registry);

        var options = new AgentMonitorOptions(PollIntervalSeconds: 30, TimeoutMinutes: 5, Mode: AgentMonitorMode.Webhook);

        var monitorTask = svc.MonitorAsync(MakeRequest(), DateTime.UtcNow.AddMinutes(-1), options);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (registry.PendingWaiterCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        registry.PendingWaiterCount.Should().BeGreaterThan(0, "the monitor must register its wait before we publish");

        var now = DateTime.UtcNow;
        var signal = new AgentWebhookSignal(
            WorkflowRunId: 99_999,
            Status: "completed",
            Conclusion: "success",
            WorkflowRunUrl: "https://github.com/acme/widgets/actions/runs/99999",
            CreatedAt: now.AddMinutes(-3),
            UpdatedAt: now,
            ArtifactsUrl: "https://api.github.com/repos/acme/widgets/actions/runs/99999/artifacts");
        var key = new AgentWebhookSignalKey(
            Repository: "acme/widgets",
            HeadBranch: "tamma/issue-42",
            SessionId: null,
            WorkflowRunId: 99_999,
            // The monitor scopes its wait key with the MEDIATED installation id.
            InstallationId: api.InstallationId);
        registry.PublishSignal(key, signal);

        var result = await monitorTask;

        result.WorkflowRunId.Should().Be(99_999);
        result.Conclusion.Should().Be("success");
        result.Status.Should().Be("completed");
        result.DurationSeconds.Should().BeGreaterThan(0);
        api.InstallationCalls.Should().BeGreaterThan(0, "the wait key is scoped with the mediated installation id");
        api.DiscoverCalls.Should().Be(0, "webhook mode must never poll");
        api.GetRunCalls.Should().Be(0);
    }

    [Test]
    public async Task MonitorAsync_WebhookMode_ReturnsMonitorFailed_OnSafetyWindowExpiry()
    {
        var registry = new WebhookSignalRegistry();
        var api = new FakeTammaApiClient();
        var svc = new AgentMonitorService(api, logger: null, new ImmediateDelayProvider(), registry);

        var options = new AgentMonitorOptions(
            PollIntervalSeconds: 30, TimeoutMinutes: 0, Mode: AgentMonitorMode.Webhook, WebhookSafetyWindowMultiplier: 0);

        var result = await svc.MonitorAsync(MakeRequest(), DateTime.UtcNow.AddMinutes(-1), options);

        result.Conclusion.Should().Be("monitor_failed", "explicit Webhook mode has no poll fallback");
        api.DiscoverCalls.Should().Be(0, "Webhook mode must not fall back to polling");
    }

    [Test]
    public async Task MonitorAsync_AutoMode_FallsBackToPoll_WhenSafetyWindowExpires()
    {
        var registry = new WebhookSignalRegistry();
        var api = new FakeTammaApiClient { DefaultDiscover = Status(424242, "completed", "success") };
        var svc = new AgentMonitorService(api, logger: null, new ImmediateDelayProvider(), registry);

        var options = new AgentMonitorOptions(
            PollIntervalSeconds: 30, TimeoutMinutes: 1, Mode: AgentMonitorMode.Auto, WebhookSafetyWindowMultiplier: 0);

        var result = await svc.MonitorAsync(MakeRequest(), DateTime.UtcNow.AddMinutes(-1), options);

        result.Conclusion.Should().Be("success", "Auto mode should fall back to polling when the webhook never arrives");
        result.WorkflowRunId.Should().Be(424242);
        api.DiscoverCalls.Should().BeGreaterThan(0, "fallback path runs the discovery+poll cycle");
    }

    [Test]
    public async Task MonitorAsync_AutoMode_FallsBackToPoll_WhenRegistryNotWired()
    {
        var api = new FakeTammaApiClient { DefaultDiscover = Status(10_001, "completed", "success") };
        // No registry passed in — mirrors ElsaServer composition root.
        var svc = new AgentMonitorService(api, logger: null, new ImmediateDelayProvider(), signals: null);

        var options = new AgentMonitorOptions(PollIntervalSeconds: 30, TimeoutMinutes: 5, Mode: AgentMonitorMode.Auto);

        var result = await svc.MonitorAsync(MakeRequest(), DateTime.UtcNow.AddMinutes(-1), options);

        result.Conclusion.Should().Be("success");
        result.WorkflowRunId.Should().Be(10_001);
        api.DiscoverCalls.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task MonitorAsync_WebhookMode_ReturnsMonitorFailed_WhenRegistryNotWired()
    {
        var api = new FakeTammaApiClient();
        var svc = new AgentMonitorService(api, logger: null, new ImmediateDelayProvider(), signals: null);

        var options = new AgentMonitorOptions(PollIntervalSeconds: 30, TimeoutMinutes: 5, Mode: AgentMonitorMode.Webhook);

        var result = await svc.MonitorAsync(MakeRequest(), DateTime.UtcNow.AddMinutes(-1), options);

        result.Conclusion.Should().Be("monitor_failed", "explicit Webhook without registry is a misconfiguration");
        api.DiscoverCalls.Should().Be(0);
    }

    // ── Pure wire→summary mapping (AC5) ────────────────────────────────────

    [Test]
    public void ToSummary_NullOrNotFoundOrFailed_ReturnsNull()
    {
        AgentMonitorService.ToSummary(null, "b").Should().BeNull();
        AgentMonitorService.ToSummary(new AgentRunStatusApiResponse { Success = true, Found = false }, "b").Should().BeNull();
        AgentMonitorService.ToSummary(new AgentRunStatusApiResponse { Success = false, Found = true, RunId = 1 }, "b").Should().BeNull();
    }

    [Test]
    public void ToSummary_FoundRun_ProjectsAllFields()
    {
        var s = AgentMonitorService.ToSummary(Status(55, "completed", "success"), "fallback");
        s.Should().NotBeNull();
        s!.Id.Should().Be(55);
        s.Status.Should().Be("completed");
        s.Conclusion.Should().Be("success");
        s.HeadBranch.Should().Be("tamma/issue-42");
    }
}
