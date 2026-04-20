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
/// Story 19-4 — collects the outputs of a completed agent workflow run
/// (result artifact, PR metadata, changed files, CI status) and returns
/// a unified <see cref="AgentExecutionResult"/>.
///
/// <para>Outcomes:
/// <list type="bullet">
///   <item><c>Collected</c> — collection succeeded (even if the agent
///     itself reported a failure; the activity's job is to read state,
///     not interpret it).</item>
///   <item><c>Partial</c> — collection ran but some data (artifact, PR,
///     check runs) was unavailable.</item>
///   <item><c>Failed</c> — collection itself threw / couldn't reach
///     GitHub at all.</item>
/// </list>
/// </para>
/// </summary>
[Activity(
    "Tamma.AgentDispatch",
    "Collect Agent Results",
    "Collect results from a completed agent workflow run",
    Kind = ActivityKind.Task)]
[FlowNode("Collected", "Partial", "Failed")]
public class CollectAgentResultsActivity : Activity, ITammaActivity
{
    private readonly ILogger<CollectAgentResultsActivity>? _logger;
    private readonly IAgentResultCollectorService? _collector;

    public string? EventType => "AGENT.RESULTS";

    public Dictionary<string, object?> BuildStartData(ActivityExecutionContext context) => new()
    {
        ["repository"] = Repository.Get(context),
        ["workflowRunId"] = WorkflowRunId.Get(context),
        ["sessionId"] = SessionId.Get(context),
        ["conclusion"] = Conclusion.Get(context)
    };

    public Dictionary<string, object?> BuildEndData(ActivityExecutionContext context)
    {
        var data = new Dictionary<string, object?>
        {
            ["repository"] = Repository.Get(context),
            ["sessionId"] = SessionId.Get(context),
            ["workflowRunId"] = WorkflowRunId.Get(context)
        };
        if (context.GetVariable<object?>("LastAgentResult") is AgentExecutionResult r)
        {
            data["success"] = r.Success;
            data["prNumber"] = r.PrNumber;
            data["filesChanged"] = r.FilesChanged.Length;
            data["commitsCount"] = r.CommitsCount;
            data["tokensUsed"] = r.TokensUsed;
            data["durationSeconds"] = r.DurationSeconds;
            data["agentProvider"] = r.AgentProvider;
            data["checksPassed"] = r.ChecksPassed;
        }
        return data;
    }

    // ─── Inputs ─────────────────────────────────────────────────────────
    [Input(Description = "Repository in owner/repo format")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Workflow run ID from the monitoring step")]
    public Input<long> WorkflowRunId { get; set; } = default!;

    [Input(Description = "Branch the agent worked on")]
    public Input<string> BranchName { get; set; } = default!;

    [Input(Description = "Issue number")]
    public Input<int> IssueNumber { get; set; } = default!;

    [Input(Description = "Tamma session ID")]
    public Input<string> SessionId { get; set; } = default!;

    [Input(Description = "Workflow conclusion from the monitor step")]
    public Input<string> Conclusion { get; set; } = default!;

    [Input(Description = "Artifacts URL (from the monitor step)")]
    public Input<string?> ArtifactsUrl { get; set; } = default!;

    [Input(Description = "Agent provider used")]
    public Input<string> AgentProvider { get; set; } = new("claude-code");

    // ─── Outputs ────────────────────────────────────────────────────────
    [Output(Description = "Whether the agent completed its task successfully")]
    public Output<bool> Success { get; set; } = default!;

    [Output(Description = "PR number if created")]
    public Output<int?> PrNumber { get; set; } = default!;

    [Output(Description = "PR HTML URL")]
    public Output<string?> PrUrl { get; set; } = default!;

    [Output(Description = "HEAD commit SHA on the branch")]
    public Output<string> CommitSha { get; set; } = default!;

    [Output(Description = "List of changed file paths (JSON array)")]
    public Output<string> FilesChangedJson { get; set; } = default!;

    [Output(Description = "Number of commits made by the agent")]
    public Output<int> CommitsCount { get; set; } = default!;

    [Output(Description = "Whether CI checks passed (null if not yet run)")]
    public Output<bool?> ChecksPassed { get; set; } = default!;

    [Output(Description = "Total tokens consumed by the agent")]
    public Output<int> TokensUsed { get; set; } = default!;

    [Output(Description = "Agent execution time in seconds")]
    public Output<int> DurationSeconds { get; set; } = default!;

    [Output(Description = "Error details if the agent failed")]
    public Output<string?> ErrorMessage { get; set; } = default!;

    [Output(Description = "Summary from the agent's logs")]
    public Output<string?> AgentLogSummary { get; set; } = default!;

    [JsonConstructor]
    public CollectAgentResultsActivity() { }

    public CollectAgentResultsActivity(
        ILogger<CollectAgentResultsActivity> logger,
        IAgentResultCollectorService collector)
    {
        _logger = logger;
        _collector = collector;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var startedAt = DateTime.UtcNow;
        TammaEventEmitter.EmitStart(context, this, this, _logger);

        if (_collector is null)
        {
            const string msg = "IAgentResultCollectorService not registered — CollectAgentResultsActivity requires DI.";
            _logger?.LogError(msg);
            SetOutputs(context, AgentExecutionResult.Failed(msg, AgentProvider.Get(context), ExecutionModeNames.GitHubActions));
            TammaEventEmitter.EmitFailure(context, this, this, _logger, DateTime.UtcNow - startedAt, msg);
            await context.CompleteActivityWithOutcomesAsync("Failed");
            return;
        }

        var request = new AgentExecutionRequest(
            Repository: Repository.Get(context),
            BranchName: BranchName.Get(context),
            IssueNumber: IssueNumber.Get(context),
            IssueTitle: string.Empty,
            Task: string.Empty,
            PlanJson: string.Empty,
            SessionId: SessionId.Get(context),
            AgentProvider: AgentProvider.Get(context),
            AgentConfigJson: null,
            WorkflowFileName: null,
            TimeoutMinutes: 0);

        var monitor = new AgentMonitorResult(
            WorkflowRunId: WorkflowRunId.Get(context),
            Status: "completed",
            Conclusion: Conclusion.Get(context) ?? "unknown",
            WorkflowRunUrl: string.Empty,
            DurationSeconds: 0,
            ArtifactsUrl: ArtifactsUrl.Get(context) ?? string.Empty);

        try
        {
            var result = await _collector.CollectAsync(request, monitor, context.CancellationToken);
            SetOutputs(context, result);
            context.SetVariable("LastAgentResult", result);

            var partial = IsPartial(result);
            if (partial)
            {
                _logger?.LogInformation(
                    "Partial result for {Repository}/{Branch}: artifact or PR missing",
                    request.Repository, request.BranchName);
                TammaEventEmitter.Emit(context, this, _logger, new TammaEvent
                {
                    EventType = "AGENT.RESULTS.PARTIAL",
                    Status = "partial",
                    Data = BuildEndData(context)
                });
                await context.CompleteActivityWithOutcomesAsync("Partial");
            }
            else
            {
                TammaEventEmitter.EmitSuccess(context, this, this, _logger, DateTime.UtcNow - startedAt);
                await context.CompleteActivityWithOutcomesAsync("Collected");
            }
        }
        catch (OperationCanceledException)
        {
            SetOutputs(context, AgentExecutionResult.Failed(
                "Result collection cancelled", request.AgentProvider, ExecutionModeNames.GitHubActions));
            TammaEventEmitter.EmitFailure(
                context, this, this, _logger, DateTime.UtcNow - startedAt, "cancelled");
            await context.CompleteActivityWithOutcomesAsync("Failed");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "Unexpected error collecting results for run {RunId}",
                monitor.WorkflowRunId);
            SetOutputs(context, AgentExecutionResult.Failed(
                $"Collection error: {ex.Message}", request.AgentProvider, ExecutionModeNames.GitHubActions));
            TammaEventEmitter.EmitFailure(
                context, this, this, _logger, DateTime.UtcNow - startedAt, ex.Message);
            await context.CompleteActivityWithOutcomesAsync("Failed");
        }
    }

    private static bool IsPartial(AgentExecutionResult r)
    {
        // AGENT.RESULTS.PARTIAL when the artifact wasn't available OR the
        // conclusion was non-success but we still collected something.
        if (string.IsNullOrEmpty(r.CommitSha) && r.FilesChanged.Length == 0) return true;
        if (!r.Success && !string.IsNullOrEmpty(r.ErrorMessage)
            && r.ErrorMessage.Contains("no result artifact", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return false;
    }

    private void SetOutputs(ActivityExecutionContext context, AgentExecutionResult r)
    {
        Success.Set(context, r.Success);
        PrNumber.Set(context, r.PrNumber);
        PrUrl.Set(context, r.PrUrl);
        CommitSha.Set(context, r.CommitSha);
        FilesChangedJson.Set(context, System.Text.Json.JsonSerializer.Serialize(r.FilesChanged));
        CommitsCount.Set(context, r.CommitsCount);
        ChecksPassed.Set(context, r.ChecksPassed);
        TokensUsed.Set(context, r.TokensUsed);
        DurationSeconds.Set(context, r.DurationSeconds);
        ErrorMessage.Set(context, r.ErrorMessage);
        AgentLogSummary.Set(context, r.AgentLogSummary);
    }
}
