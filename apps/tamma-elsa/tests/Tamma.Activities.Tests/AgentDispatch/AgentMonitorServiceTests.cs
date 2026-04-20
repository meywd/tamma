using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.AgentDispatch;
using Tamma.Activities.AgentDispatch.Models;

namespace Tamma.Activities.Tests.AgentDispatch;

[TestFixture]
public class AgentMonitorServiceTests
{
    private static AgentExecutionRequest MakeRequest(string repo = "acme/widgets") =>
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
            TimeoutMinutes: 35);

    private static WorkflowRunSummary Run(long id, string status, string conclusion = "") =>
        new(
            Id: id,
            Status: status,
            Conclusion: conclusion,
            HtmlUrl: $"https://github.com/acme/widgets/actions/runs/{id}",
            CreatedAt: DateTime.UtcNow.AddMinutes(-5),
            UpdatedAt: DateTime.UtcNow,
            HeadBranch: "tamma/issue-42",
            Event: "workflow_dispatch",
            ArtifactsUrl: $"https://api.github.com/repos/acme/widgets/actions/runs/{id}/artifacts");

    [Test]
    public async Task MonitorAsync_ReturnsSuccess_WhenRunAlreadyComplete()
    {
        var fake = new FakeGitHubActionsClient
        {
            DefaultListRuns = new[] { Run(12345, "completed", "success") }
        };
        var delay = new ImmediateDelayProvider();
        var svc = new AgentMonitorService(fake, logger: null, delay);

        var result = await svc.MonitorAsync(
            MakeRequest(), DateTime.UtcNow.AddMinutes(-1), AgentMonitorOptions.Default);

        result.WorkflowRunId.Should().Be(12345);
        result.Status.Should().Be("completed");
        result.Conclusion.Should().Be("success");
    }

    [Test]
    public async Task MonitorAsync_PollsUntilCompletion()
    {
        var fake = new FakeGitHubActionsClient
        {
            DefaultListRuns = new[] { Run(100, "in_progress") }
        };
        fake.GetRunQueue = new Queue<WorkflowRunSummary?>(new WorkflowRunSummary?[]
        {
            Run(100, "in_progress"),
            Run(100, "in_progress"),
            Run(100, "completed", "success")
        });
        var delay = new ImmediateDelayProvider();
        var svc = new AgentMonitorService(fake, logger: null, delay);

        var result = await svc.MonitorAsync(
            MakeRequest(),
            DateTime.UtcNow.AddMinutes(-1),
            new AgentMonitorOptions(PollIntervalSeconds: 30, TimeoutMinutes: 5));

        result.Conclusion.Should().Be("success");
        fake.GetRunCalls.Should().Be(3);
    }

    [Test]
    public async Task MonitorAsync_ReturnsNotFound_WhenDiscoveryTimesOut()
    {
        var fake = new FakeGitHubActionsClient
        {
            DefaultListRuns = System.Array.Empty<WorkflowRunSummary>()
        };
        var delay = new ImmediateDelayProvider();
        var svc = new AgentMonitorService(fake, logger: null, delay);

        var result = await svc.MonitorAsync(
            MakeRequest(),
            DateTime.UtcNow,
            new AgentMonitorOptions(PollIntervalSeconds: 30, TimeoutMinutes: 1, DiscoveryTimeoutSeconds: 1));

        result.Status.Should().Be("error");
        result.Conclusion.Should().Be("not_found");
        fake.ListRunsCalls.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task MonitorAsync_ReturnsFailure_ForFailedRun()
    {
        var fake = new FakeGitHubActionsClient
        {
            DefaultListRuns = new[] { Run(777, "completed", "failure") }
        };
        var svc = new AgentMonitorService(fake, logger: null, new ImmediateDelayProvider());

        var result = await svc.MonitorAsync(
            MakeRequest(), DateTime.UtcNow, AgentMonitorOptions.Default);

        result.Conclusion.Should().Be("failure");
    }

    [Test]
    public async Task MonitorAsync_BailsAfterConsecutivePollErrors()
    {
        var fake = new FakeGitHubActionsClient
        {
            DefaultListRuns = new[] { Run(500, "in_progress") }
        };
        // Queue nothing; GetWorkflowRunAsync will return null each call,
        // which the service treats as a transient error.
        fake.GetRunQueue = new Queue<WorkflowRunSummary?>(new WorkflowRunSummary?[]
        {
            null, null, null, null, null, null
        });
        var svc = new AgentMonitorService(fake, logger: null, new ImmediateDelayProvider());

        var result = await svc.MonitorAsync(
            MakeRequest(),
            DateTime.UtcNow,
            new AgentMonitorOptions(
                PollIntervalSeconds: 30,
                TimeoutMinutes: 60,
                MaxConsecutiveErrors: 3));

        result.Conclusion.Should().Be("monitor_failed");
    }

    [Test]
    public async Task MonitorAsync_InvalidRepoReturnsNotFound()
    {
        var fake = new FakeGitHubActionsClient();
        var svc = new AgentMonitorService(fake, logger: null, new ImmediateDelayProvider());

        var result = await svc.MonitorAsync(
            MakeRequest(repo: "bad"),
            DateTime.UtcNow,
            AgentMonitorOptions.Default);

        result.Conclusion.Should().Be("not_found");
    }

    // ================================================================
    // Story 19-3 AC-7 — webhook mode
    // ================================================================

    [Test]
    public async Task MonitorAsync_WebhookMode_ResumesOnSignal()
    {
        var registry = new WebhookSignalRegistry();
        var fake = new FakeGitHubActionsClient();
        var svc = new AgentMonitorService(fake, logger: null, new ImmediateDelayProvider(), registry);

        var options = new AgentMonitorOptions(
            PollIntervalSeconds: 30,
            TimeoutMinutes: 5,
            Mode: AgentMonitorMode.Webhook);

        var monitorTask = svc.MonitorAsync(MakeRequest(), DateTime.UtcNow.AddMinutes(-1), options);

        // Wait for the monitor to park on the signal before we publish.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (registry.PendingWaiterCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        registry.PendingWaiterCount.Should().BeGreaterThan(0,
            "the monitor must register its wait before we publish");

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
            WorkflowRunId: 99_999);
        registry.PublishSignal(key, signal);

        var result = await monitorTask;

        result.WorkflowRunId.Should().Be(99_999);
        result.Conclusion.Should().Be("success");
        result.Status.Should().Be("completed");
        result.DurationSeconds.Should().BeGreaterThan(0);
        fake.ListRunsCalls.Should().Be(0, "webhook mode must never poll GitHub");
        fake.GetRunCalls.Should().Be(0);
    }

    [Test]
    public async Task MonitorAsync_WebhookMode_ReturnsMonitorFailed_OnSafetyWindowExpiry()
    {
        var registry = new WebhookSignalRegistry();
        var fake = new FakeGitHubActionsClient();
        var svc = new AgentMonitorService(fake, logger: null, new ImmediateDelayProvider(), registry);

        // 0.01-minute timeout * 0.01 multiplier = 0.0001 min → clamped to
        // 1 second floor in AgentMonitorService (we just need the safety
        // window to elapse, not the full 35m production default).
        var options = new AgentMonitorOptions(
            PollIntervalSeconds: 30,
            TimeoutMinutes: 0,
            Mode: AgentMonitorMode.Webhook,
            WebhookSafetyWindowMultiplier: 0);

        var result = await svc.MonitorAsync(
            MakeRequest(), DateTime.UtcNow.AddMinutes(-1), options);

        result.Conclusion.Should().Be("monitor_failed",
            "explicit Webhook mode has no poll fallback");
        fake.ListRunsCalls.Should().Be(0,
            "Webhook mode must not fall back to polling");
    }

    [Test]
    public async Task MonitorAsync_AutoMode_FallsBackToPoll_WhenSafetyWindowExpires()
    {
        var registry = new WebhookSignalRegistry();
        var fake = new FakeGitHubActionsClient
        {
            DefaultListRuns = new[] { Run(424242, "completed", "success") }
        };
        var svc = new AgentMonitorService(fake, logger: null, new ImmediateDelayProvider(), registry);

        var options = new AgentMonitorOptions(
            PollIntervalSeconds: 30,
            TimeoutMinutes: 1,
            Mode: AgentMonitorMode.Auto,
            WebhookSafetyWindowMultiplier: 0);

        var result = await svc.MonitorAsync(
            MakeRequest(), DateTime.UtcNow.AddMinutes(-1), options);

        result.Conclusion.Should().Be("success",
            "Auto mode should fall back to polling when the webhook never arrives");
        result.WorkflowRunId.Should().Be(424242);
        fake.ListRunsCalls.Should().BeGreaterThan(0,
            "fallback path runs the discovery+poll cycle");
    }

    [Test]
    public async Task MonitorAsync_AutoMode_FallsBackToPoll_WhenRegistryNotWired()
    {
        var fake = new FakeGitHubActionsClient
        {
            DefaultListRuns = new[] { Run(10_001, "completed", "success") }
        };
        // No registry passed in — mirrors ElsaServer composition root.
        var svc = new AgentMonitorService(fake, logger: null, new ImmediateDelayProvider(), signals: null);

        var options = new AgentMonitorOptions(
            PollIntervalSeconds: 30,
            TimeoutMinutes: 5,
            Mode: AgentMonitorMode.Auto);

        var result = await svc.MonitorAsync(
            MakeRequest(), DateTime.UtcNow.AddMinutes(-1), options);

        result.Conclusion.Should().Be("success");
        result.WorkflowRunId.Should().Be(10_001);
        fake.ListRunsCalls.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task MonitorAsync_WebhookMode_ReturnsMonitorFailed_WhenRegistryNotWired()
    {
        var fake = new FakeGitHubActionsClient();
        var svc = new AgentMonitorService(fake, logger: null, new ImmediateDelayProvider(), signals: null);

        var options = new AgentMonitorOptions(
            PollIntervalSeconds: 30,
            TimeoutMinutes: 5,
            Mode: AgentMonitorMode.Webhook);

        var result = await svc.MonitorAsync(
            MakeRequest(), DateTime.UtcNow.AddMinutes(-1), options);

        result.Conclusion.Should().Be("monitor_failed",
            "explicit Webhook without registry is a misconfiguration");
        fake.ListRunsCalls.Should().Be(0);
    }
}
