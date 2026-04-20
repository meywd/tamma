using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.AgentDispatch.Models;
using Tamma.Activities.Core;

namespace Tamma.Activities.AgentDispatch;

/// <summary>
/// Story 19-2 — dispatches a <c>workflow_dispatch</c> event on the user's
/// repository via the GitHub App. The activity itself is a thin wrapper
/// around <see cref="IAgentDispatchService"/>; the logic lives in the
/// service so <see cref="GitHubActionsExecutor"/> can reuse it.
///
/// <para>Outcomes:
/// <list type="bullet">
///   <item><c>Dispatched</c> — workflow_dispatch succeeded (HTTP 204).</item>
///   <item><c>Failed</c> — any error (workflow missing, permission denied, rate limit exceeded after retries, ...).</item>
/// </list>
/// </para>
///
/// <para>Acceptance criteria coverage — 19-2 AC 1–8. Events emitted via
/// <see cref="TammaEventEmitter"/> surface on the workflow's transient
/// event bag as <c>AGENT.DISPATCH.STARTED/COMPLETED/FAILED</c>.</para>
/// </summary>
[Activity(
    "Tamma.AgentDispatch",
    "Dispatch Agent Workflow",
    "Dispatch a workflow_dispatch event to run an agent on the user's GitHub Actions runner",
    Kind = ActivityKind.Task)]
[FlowNode("Dispatched", "Failed")]
public class DispatchAgentWorkflowActivity : Activity, ITammaActivity
{
    private readonly ILogger<DispatchAgentWorkflowActivity>? _logger;
    private readonly IAgentDispatchService? _dispatchService;

    public string? EventType => "AGENT.DISPATCH";

    public Dictionary<string, object?> BuildStartData(ActivityExecutionContext context) => new()
    {
        ["repository"] = Repository.Get(context),
        ["branchName"] = BranchName.Get(context),
        ["issueNumber"] = IssueNumber.Get(context),
        ["sessionId"] = SessionId.Get(context),
        ["agentProvider"] = AgentProvider.Get(context),
        ["workflowFileName"] = WorkflowFileName.Get(context),
        ["timeoutMinutes"] = TimeoutMinutes.Get(context)
    };

    public Dictionary<string, object?> BuildEndData(ActivityExecutionContext context)
    {
        // Pull the most recent dispatch outcome from the workflow-level
        // transient bag — outputs aren't directly readable via Output<T>.
        var data = new Dictionary<string, object?>
        {
            ["repository"] = Repository.Get(context),
            ["branchName"] = BranchName.Get(context),
            ["sessionId"] = SessionId.Get(context)
        };
        if (context.GetVariable<object?>("LastDispatchResult") is AgentDispatchResult last)
        {
            data["dispatchSuccess"] = last.Success;
            data["dispatchedAt"] = last.DispatchedAt;
            data["errorMessage"] = last.ErrorMessage;
        }
        return data;
    }

    // ─── Inputs ─────────────────────────────────────────────────────────
    [Input(Description = "Repository in owner/repo format")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Branch for the agent to work on")]
    public Input<string> BranchName { get; set; } = default!;

    [Input(Description = "Issue number")]
    public Input<int> IssueNumber { get; set; } = default!;

    [Input(Description = "Task type: implement, fix, debug, review, test")]
    public Input<string> Task { get; set; } = new("implement");

    [Input(Description = "Serialized development plan")]
    public Input<string> PlanJson { get; set; } = new("{}");

    [Input(Description = "Tamma session ID for correlation")]
    public Input<string> SessionId { get; set; } = default!;

    [Input(Description = "Agent provider (claude-code, aider, etc.)")]
    public Input<string> AgentProvider { get; set; } = new("claude-code");

    [Input(Description = "Additional agent config JSON")]
    public Input<string> AgentConfigJson { get; set; } = new("{}");

    [Input(Description = "Workflow file name in the repo")]
    public Input<string> WorkflowFileName { get; set; } = new("tamma-agent.yml");

    [Input(Description = "Timeout in minutes for the agent workflow")]
    public Input<int> TimeoutMinutes { get; set; } = new(30);

    // ─── Outputs ────────────────────────────────────────────────────────
    [Output(Description = "Whether the dispatch API call succeeded")]
    public Output<bool> DispatchSuccess { get; set; } = default!;

    [Output(Description = "URL to the workflow run (story 19-3 resolves)")]
    public Output<string?> WorkflowRunUrl { get; set; } = default!;

    [Output(Description = "Timestamp of the dispatch API call")]
    public Output<DateTime> DispatchedAt { get; set; } = default!;

    [Output(Description = "Error details if dispatch failed")]
    public Output<string?> ErrorMessage { get; set; } = default!;

    [JsonConstructor]
    public DispatchAgentWorkflowActivity() { }

    public DispatchAgentWorkflowActivity(
        ILogger<DispatchAgentWorkflowActivity> logger,
        IAgentDispatchService dispatchService)
    {
        _logger = logger;
        _dispatchService = dispatchService;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var startedAt = DateTime.UtcNow;
        TammaEventEmitter.EmitStart(context, this, this, _logger);

        if (_dispatchService is null)
        {
            const string msg = "IAgentDispatchService not registered — DispatchAgentWorkflowActivity requires DI.";
            _logger?.LogError(msg);
            SetFailure(context, msg, DateTime.UtcNow);
            TammaEventEmitter.EmitFailure(context, this, this, _logger, DateTime.UtcNow - startedAt, msg);
            await context.CompleteActivityWithOutcomesAsync("Failed");
            return;
        }

        var request = new AgentExecutionRequest(
            Repository: Repository.Get(context),
            BranchName: BranchName.Get(context),
            IssueNumber: IssueNumber.Get(context),
            IssueTitle: string.Empty,
            Task: Task.Get(context),
            PlanJson: PlanJson.Get(context),
            SessionId: SessionId.Get(context),
            AgentProvider: AgentProvider.Get(context),
            AgentConfigJson: AgentConfigJson.Get(context),
            WorkflowFileName: WorkflowFileName.Get(context),
            TimeoutMinutes: TimeoutMinutes.Get(context));

        try
        {
            var result = await _dispatchService.DispatchAsync(request, context.CancellationToken);
            DispatchSuccess.Set(context, result.Success);
            WorkflowRunUrl.Set(context, result.WorkflowRunUrl);
            DispatchedAt.Set(context, result.DispatchedAt);
            ErrorMessage.Set(context, result.ErrorMessage);
            context.SetVariable("LastDispatchResult", result);

            if (result.Success)
            {
                TammaEventEmitter.EmitSuccess(context, this, this, _logger, DateTime.UtcNow - startedAt);
                await context.CompleteActivityWithOutcomesAsync("Dispatched");
            }
            else
            {
                _logger?.LogWarning(
                    "Agent dispatch failed for {Repository}/{Branch}: {Error}",
                    request.Repository, request.BranchName, result.ErrorMessage);
                TammaEventEmitter.EmitFailure(
                    context, this, this, _logger,
                    DateTime.UtcNow - startedAt, result.ErrorMessage ?? "unknown");
                await context.CompleteActivityWithOutcomesAsync("Failed");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "Agent dispatch threw an unexpected exception for {Repository}/{Branch}",
                request.Repository, request.BranchName);
            SetFailure(context, $"Unexpected error: {ex.Message}", DateTime.UtcNow);
            TammaEventEmitter.EmitFailure(
                context, this, this, _logger, DateTime.UtcNow - startedAt, ex.Message);
            await context.CompleteActivityWithOutcomesAsync("Failed");
        }
    }

    private void SetFailure(ActivityExecutionContext context, string message, DateTime dispatchedAt)
    {
        DispatchSuccess.Set(context, false);
        WorkflowRunUrl.Set(context, null);
        DispatchedAt.Set(context, dispatchedAt);
        ErrorMessage.Set(context, message);
    }
}
