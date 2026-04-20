using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.AgentDispatch;
using Tamma.Activities.AgentDispatch.Models;

namespace Tamma.Activities.Tests.AgentDispatch;

[TestFixture]
public class GitHubActionsExecutorTests
{
    private static AgentExecutionRequest MakeRequest() =>
        new(
            Repository: "acme/widgets",
            BranchName: "tamma/issue-42",
            IssueNumber: 42,
            IssueTitle: "t",
            Task: "implement",
            PlanJson: "{}",
            SessionId: "sess",
            AgentProvider: "claude-code",
            AgentConfigJson: null,
            WorkflowFileName: null,
            TimeoutMinutes: 30);

    [Test]
    public async Task ExecuteAsync_RunsDispatchMonitorCollect_OnHappyPath()
    {
        var dispatch = new StubDispatch(success: true);
        var monitor = new StubMonitor("success");
        var collector = new StubCollector(AgentExecutionResult.Failed("never", "claude-code", ExecutionModeNames.GitHubActions)
            with
        {
            Success = true,
            PrNumber = 7,
            CommitSha = "abc",
            ExecutionMode = ExecutionModeNames.GitHubActions
        });

        var exec = new GitHubActionsExecutor(dispatch, monitor, collector);
        var result = await exec.ExecuteAsync(MakeRequest());

        result.Success.Should().BeTrue();
        result.PrNumber.Should().Be(7);
        dispatch.Calls.Should().Be(1);
        monitor.Calls.Should().Be(1);
        collector.Calls.Should().Be(1);
    }

    [Test]
    public async Task ExecuteAsync_ShortCircuits_WhenDispatchFails()
    {
        var dispatch = new StubDispatch(success: false, error: "workflow missing");
        var monitor = new StubMonitor("success");
        var collector = new StubCollector(AgentExecutionResult.Failed("x", "p", "m"));

        var exec = new GitHubActionsExecutor(dispatch, monitor, collector);
        var result = await exec.ExecuteAsync(MakeRequest());

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("workflow missing");
        monitor.Calls.Should().Be(0);
        collector.Calls.Should().Be(0);
    }

    [Test]
    public async Task ExecuteAsync_StillCollects_EvenOnFailureConclusion()
    {
        var dispatch = new StubDispatch(true);
        var monitor = new StubMonitor("failure");
        var collector = new StubCollector(AgentExecutionResult.Failed(
            "conclusion=failure", "claude-code", ExecutionModeNames.GitHubActions));

        var exec = new GitHubActionsExecutor(dispatch, monitor, collector);
        var result = await exec.ExecuteAsync(MakeRequest());

        result.Success.Should().BeFalse();
        collector.Calls.Should().Be(1);
    }

    [Test]
    public void Mode_IsGitHubActions()
    {
        var exec = new GitHubActionsExecutor(
            new StubDispatch(true),
            new StubMonitor("success"),
            new StubCollector(AgentExecutionResult.Failed("x", "p", "m")));

        exec.Mode.Should().Be(ExecutionModeNames.GitHubActions);
    }

    private sealed class StubDispatch : IAgentDispatchService
    {
        public int Calls { get; private set; }
        private readonly bool _success;
        private readonly string? _error;

        public StubDispatch(bool success, string? error = null)
        {
            _success = success;
            _error = error;
        }

        public Task<AgentDispatchResult> DispatchAsync(AgentExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new AgentDispatchResult(
                Success: _success,
                WorkflowRunUrl: null,
                ErrorMessage: _error,
                DispatchedAt: DateTime.UtcNow));
        }
    }

    private sealed class StubMonitor : IAgentMonitorService
    {
        public int Calls { get; private set; }
        private readonly string _conclusion;

        public StubMonitor(string conclusion) { _conclusion = conclusion; }

        public Task<AgentMonitorResult> MonitorAsync(
            AgentExecutionRequest request,
            DateTime dispatchedAfter,
            AgentMonitorOptions options,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new AgentMonitorResult(
                WorkflowRunId: 42,
                Status: "completed",
                Conclusion: _conclusion,
                WorkflowRunUrl: "u",
                DurationSeconds: 1,
                ArtifactsUrl: "a"));
        }
    }

    private sealed class StubCollector : IAgentResultCollectorService
    {
        public int Calls { get; private set; }
        private readonly AgentExecutionResult _result;

        public StubCollector(AgentExecutionResult r) { _result = r; }

        public Task<AgentExecutionResult> CollectAsync(
            AgentExecutionRequest request,
            AgentMonitorResult monitorResult,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(_result);
        }
    }
}
