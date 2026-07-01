using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.AgentDispatch;
using Tamma.Activities.AgentDispatch.Models;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.Tests.AgentDispatch;

/// <summary>
/// Story 38-2 — the thin <see cref="AgentDispatchService"/>. It composes the
/// dispatch inputs engine-side (pure, token-free) and delegates the actual
/// workflow_dispatch (check + retry) to <c>Tamma.Api</c> via
/// <c>TammaApiClient.DispatchAgentRunAsync</c>. Tests cover: engine-side
/// repo-format validation (no API call on bad input), input composition, tenant
/// threading, and the pure wire→result mapping (including the fail-closed
/// null-response path). The check/retry/403/404 platform semantics now live in
/// <c>AgentDispatchMediationServiceTests</c> (Tamma.Api).
/// </summary>
[TestFixture]
public class AgentDispatchServiceTests
{
    private static AgentExecutionRequest MakeRequest(
        string repo = "acme/widgets",
        string branch = "tamma/issue-42",
        string? workflowFile = "tamma-agent.yml",
        Guid? tenantId = null) =>
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
            TimeoutMinutes: 30,
            TenantId: tenantId);

    [Test]
    public async Task DispatchAsync_ComposesInputs_AndCalls_Mediation_OnHappyPath()
    {
        var tenant = Guid.NewGuid();
        var api = new FakeTammaApiClient
        {
            OnDispatch = (_, _, _) => new AgentDispatchRunApiResponse
            { Success = true, DispatchedAt = DateTime.UtcNow, CredentialSource = "installation" }
        };
        var svc = new AgentDispatchService(api);

        var result = await svc.DispatchAsync(MakeRequest(tenantId: tenant));

        result.Success.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();

        api.DispatchCalls.Should().HaveCount(1);
        var call = api.DispatchCalls[0];
        call.Repo.Should().Be("acme/widgets");
        call.TenantId.Should().Be(tenant.ToString(), "the acting tenant is sent as X-Tenant-Id");
        call.Request.WorkflowFileName.Should().Be("tamma-agent.yml");
        call.Request.Ref.Should().Be("tamma/issue-42");
        call.Request.CorrelationId.Should().Be("sess_abc123");
        call.Request.Inputs.Should().ContainKey("issue_number").WhoseValue.Should().Be("42");
        call.Request.Inputs.Should().ContainKey("tamma_session_id").WhoseValue.Should().Be("sess_abc123");
        call.Request.Inputs.Should().ContainKey("agent_provider").WhoseValue.Should().Be("claude-code");
    }

    [Test]
    public async Task DispatchAsync_Fails_WhenRepositoryFormatInvalid_WithoutCallingApi()
    {
        var api = new FakeTammaApiClient();
        var svc = new AgentDispatchService(api);

        var result = await svc.DispatchAsync(MakeRequest(repo: "not-a-repo"));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("owner/repo");
        api.DispatchCalls.Should().BeEmpty("a malformed repo never reaches the API");
    }

    [Test]
    public async Task DispatchAsync_FallsBackToDefaultWorkflowFileName()
    {
        var api = new FakeTammaApiClient
        {
            OnDispatch = (_, _, _) => new AgentDispatchRunApiResponse { Success = true, DispatchedAt = DateTime.UtcNow }
        };
        var svc = new AgentDispatchService(api);

        await svc.DispatchAsync(MakeRequest(workflowFile: null));

        api.DispatchCalls[0].Request.WorkflowFileName.Should().Be("tamma-agent.yml");
    }

    [Test]
    public async Task DispatchAsync_MapsFailureReason_ToErrorMessage()
    {
        var api = new FakeTammaApiClient
        {
            OnDispatch = (_, _, _) => new AgentDispatchRunApiResponse
            {
                Success = false,
                FailureCode = "DISPATCH_REJECTED",
                FailureReason = "GitHub returned 403 for dispatch — Tamma App installation may be missing the 'actions: write' permission.",
                DispatchedAt = DateTime.UtcNow,
            }
        };
        var svc = new AgentDispatchService(api);

        var result = await svc.DispatchAsync(MakeRequest());

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("403");
        result.ErrorMessage.Should().Contain("actions: write");
    }

    // ── Pure wire→result mapping (AC5) ────────────────────────────────────

    [Test]
    public void MapResponse_Success_ProjectsRunUrlAndDispatchedAt()
    {
        var at = new DateTime(2026, 6, 30, 12, 0, 0, DateTimeKind.Utc);
        var result = AgentDispatchService.MapResponse(new AgentDispatchRunApiResponse
        { Success = true, WorkflowRunUrl = "https://gh/run/1", DispatchedAt = at });

        result.Success.Should().BeTrue();
        result.WorkflowRunUrl.Should().Be("https://gh/run/1");
        result.DispatchedAt.Should().Be(at);
        result.ErrorMessage.Should().BeNull();
    }

    [Test]
    public void MapResponse_NullResponse_FailsClosed()
    {
        var result = AgentDispatchService.MapResponse(null);
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("mediation unavailable");
    }
}
