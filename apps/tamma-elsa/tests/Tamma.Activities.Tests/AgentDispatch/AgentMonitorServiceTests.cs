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
}
