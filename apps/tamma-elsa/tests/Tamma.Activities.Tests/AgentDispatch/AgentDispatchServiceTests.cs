using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.AgentDispatch;
using Tamma.Activities.AgentDispatch.Models;

namespace Tamma.Activities.Tests.AgentDispatch;

[TestFixture]
public class AgentDispatchServiceTests
{
    private static AgentExecutionRequest MakeRequest(
        string repo = "acme/widgets",
        string branch = "tamma/issue-42",
        string? workflowFile = "tamma-agent.yml") =>
        new(
            Repository: repo,
            BranchName: branch,
            IssueNumber: 42,
            IssueTitle: "Fix it",
            Task: "implement",
            PlanJson: "{\"step\":1}",
            SessionId: "sess_abc123",
            AgentProvider: "claude-code",
            AgentConfigJson: null,
            WorkflowFileName: workflowFile,
            TimeoutMinutes: 30);

    [Test]
    public async Task DispatchAsync_ReturnsSuccess_OnHappyPath()
    {
        var fake = new FakeGitHubActionsClient();
        var svc = new AgentDispatchService(fake);

        var result = await svc.DispatchAsync(MakeRequest());

        result.Success.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        fake.DispatchCalls.Should().HaveCount(1);

        var call = fake.DispatchCalls[0];
        call.Owner.Should().Be("acme");
        call.Repo.Should().Be("widgets");
        call.WorkflowFile.Should().Be("tamma-agent.yml");
        call.Ref.Should().Be("tamma/issue-42");
        call.Inputs.Should().ContainKey("issue_number").WhoseValue.Should().Be("42");
        call.Inputs.Should().ContainKey("tamma_session_id").WhoseValue.Should().Be("sess_abc123");
        call.Inputs.Should().ContainKey("agent_provider").WhoseValue.Should().Be("claude-code");
    }

    [Test]
    public async Task DispatchAsync_Fails_WhenRepositoryFormatInvalid()
    {
        var fake = new FakeGitHubActionsClient();
        var svc = new AgentDispatchService(fake);

        var result = await svc.DispatchAsync(MakeRequest(repo: "not-a-repo"));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("owner/repo");
        fake.CheckWorkflowCalls.Should().Be(0);
    }

    [Test]
    public async Task DispatchAsync_Fails_WhenWorkflowFileMissing()
    {
        var fake = new FakeGitHubActionsClient
        {
            CheckWorkflow = (_, _, _) => new WorkflowFileCheck(false, false, "workflow_not_found")
        };
        var svc = new AgentDispatchService(fake);

        var result = await svc.DispatchAsync(MakeRequest());

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("tamma-agent.yml");
        fake.DispatchCalls.Should().BeEmpty();
    }

    [Test]
    public async Task DispatchAsync_Fails_WhenClientNotConfigured()
    {
        var fake = new FakeGitHubActionsClient
        {
            CheckWorkflow = (_, _, _) => new WorkflowFileCheck(false, true, "github_client_not_configured")
        };
        var svc = new AgentDispatchService(fake);

        var result = await svc.DispatchAsync(MakeRequest());

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("GitHub App not configured");
    }

    [Test]
    public async Task DispatchAsync_ReturnsPermissionError_On403()
    {
        var fake = new FakeGitHubActionsClient
        {
            OnDispatch = (_, _, _, _, _) => new DispatchApiResult(403, "forbidden")
        };
        var svc = new AgentDispatchService(fake);

        var result = await svc.DispatchAsync(MakeRequest());

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("403");
        result.ErrorMessage.Should().Contain("actions: write");
    }

    [Test]
    public async Task DispatchAsync_ReturnsBranchError_On404()
    {
        var fake = new FakeGitHubActionsClient
        {
            OnDispatch = (_, _, _, _, _) => new DispatchApiResult(404, "not found")
        };
        var svc = new AgentDispatchService(fake);

        var result = await svc.DispatchAsync(MakeRequest());

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("404");
    }

    [Test]
    public async Task DispatchAsync_RetriesOn429_ThenSucceeds()
    {
        var attempts = 0;
        var fake = new FakeGitHubActionsClient
        {
            OnDispatch = (_, _, _, _, _) =>
            {
                attempts++;
                return attempts < 2
                    ? new DispatchApiResult(429, "rate limited")
                    : new DispatchApiResult(204, null);
            }
        };
        // Use a cancellation token we can cancel to avoid real delays.
        // Service uses Task.Delay; the test runs quickly because
        // first delay is only 1s.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var svc = new AgentDispatchService(fake);

        var result = await svc.DispatchAsync(MakeRequest(), cts.Token);

        result.Success.Should().BeTrue();
        attempts.Should().Be(2);
    }

    [Test]
    public async Task DispatchAsync_FailsAfterRetryBudget_On429()
    {
        var fake = new FakeGitHubActionsClient
        {
            OnDispatch = (_, _, _, _, _) => new DispatchApiResult(429, "still rate-limited")
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var svc = new AgentDispatchService(fake);

        var result = await svc.DispatchAsync(MakeRequest(), cts.Token);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("429");
        // MaxRetries = 3 → 4 attempts total (initial + 3 retries).
        fake.DispatchCalls.Should().HaveCount(4);
    }

    [Test]
    public async Task DispatchAsync_FallsBackToDefaultWorkflowFileName()
    {
        var fake = new FakeGitHubActionsClient();
        var svc = new AgentDispatchService(fake);

        var result = await svc.DispatchAsync(MakeRequest(workflowFile: null));

        result.Success.Should().BeTrue();
        fake.DispatchCalls[0].WorkflowFile.Should().Be("tamma-agent.yml");
    }
}
