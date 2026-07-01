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
///   <item><c>Partial</c> — collection RAN but some data (artifact, PR,
///     check runs) was unavailable.</item>
///   <item><c>Failed</c> — the collection itself did not run: it threw, or the
///     mediated collect call returned a MEDIATION/authorization failure (guard 403 /
///     auth 401 / transport). This is checked BEFORE the Partial heuristic so a
///     revoked mid-run authorization never surfaces as a phantom Partial.</item>
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
            TimeoutMinutes: 0,
            TenantId: ReadTenantIdFromContext(context));

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

            switch (Route(result))
            {
                case CollectRoute.Failed:
                    // Review finding 2 — the collect call itself did not run (mediation
                    // outage / revoked mid-run authorization). Route to Failed, NOT the
                    // soft Partial, so a downstream branch never proceeds on a phantom.
                    _logger?.LogWarning(
                        "Agent result collection unavailable for {Repository}/{Branch}: {Error}",
                        request.Repository, request.BranchName, result.ErrorMessage);
                    TammaEventEmitter.EmitFailure(
                        context, this, this, _logger, DateTime.UtcNow - startedAt,
                        result.ErrorMessage ?? "agent result collection unavailable");
                    await context.CompleteActivityWithOutcomesAsync("Failed");
                    break;

                case CollectRoute.Partial:
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
                    break;

                default:
                    TammaEventEmitter.EmitSuccess(context, this, this, _logger, DateTime.UtcNow - startedAt);
                    await context.CompleteActivityWithOutcomesAsync("Collected");
                    break;
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

    /// <summary>The three terminal routes for a completed collect call.</summary>
    internal enum CollectRoute { Collected, Partial, Failed }

    /// <summary>
    /// Review finding 2 — decide the outcome for a mapped collect result. A
    /// MEDIATION/authorization failure (the collect call itself did not run) is a hard
    /// <see cref="CollectRoute.Failed"/>, evaluated BEFORE the Partial heuristic so an
    /// empty-git-state mediation outage never misroutes to the soft Partial. Only a
    /// collection that RAN but couldn't read full git state stays a Partial.
    /// </summary>
    internal static CollectRoute Route(AgentExecutionResult r)
    {
        if (IsCollectionUnavailable(r)) return CollectRoute.Failed;
        if (IsPartial(r)) return CollectRoute.Partial;
        return CollectRoute.Collected;
    }

    private static bool IsCollectionUnavailable(AgentExecutionResult r) =>
        !r.Success
        && r.ErrorMessage is not null
        && r.ErrorMessage.StartsWith(
            AgentResultCollectorService.CollectionUnavailableMarker, StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// Story 38-2 — best-effort tenant resolution so the thin collector can send
    /// X-Tenant-Id on the mediated collect call. Mirrors
    /// <c>DispatchAgentWorkflowActivity.ReadTenantIdFromContext</c>.
    /// </summary>
    private static Guid? ReadTenantIdFromContext(ActivityExecutionContext context)
    {
        string?[] candidates =
        {
            context.GetVariable<string>("TenantId"),
            context.GetVariable<string>("tenantId"),
            context.GetVariable<string>("Tamma:TenantId"),
        };
        foreach (var s in candidates)
        {
            if (!string.IsNullOrWhiteSpace(s) && Guid.TryParse(s, out var g))
                return g;
        }
        return null;
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
