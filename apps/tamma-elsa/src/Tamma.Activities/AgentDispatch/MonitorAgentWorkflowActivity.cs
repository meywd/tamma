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
/// Story 19-3 — monitors a dispatched GitHub Actions workflow run until
/// it reaches a terminal state. Thin wrapper around
/// <see cref="IAgentMonitorService"/>.
///
/// <para>Outcomes:
/// <list type="bullet">
///   <item><c>Completed</c> — conclusion is <c>success</c>.</item>
///   <item><c>Failed</c> — conclusion is <c>failure</c> / <c>cancelled</c> / <c>timed_out</c> / <c>not_found</c>.</item>
/// </list>
/// </para>
///
/// <para>AC-7: webhook mode is supported via the <see cref="Mode"/> input
/// (<c>Auto</c> / <c>Poll</c> / <c>Webhook</c>). Default remains
/// <c>Poll</c> for back-compat. See <see cref="AgentMonitorMode"/> for
/// the resolution rules; <c>Auto</c> is the production-recommended
/// setting when the webhook receiver is wired.</para>
/// </summary>
[Activity(
    "Tamma.AgentDispatch",
    "Monitor Agent Workflow",
    "Monitor a dispatched GitHub Actions workflow run until completion",
    Kind = ActivityKind.Task)]
[FlowNode("Completed", "Failed")]
public class MonitorAgentWorkflowActivity : Activity, ITammaActivity
{
    private readonly ILogger<MonitorAgentWorkflowActivity>? _logger;
    private readonly IAgentMonitorService? _monitorService;

    public string? EventType => "AGENT.MONITOR";

    public Dictionary<string, object?> BuildStartData(ActivityExecutionContext context) => new()
    {
        ["repository"] = Repository.Get(context),
        ["branchName"] = BranchName.Get(context),
        ["sessionId"] = SessionId.Get(context),
        ["pollIntervalSeconds"] = PollIntervalSeconds.Get(context),
        ["timeoutMinutes"] = TimeoutMinutes.Get(context),
        ["mode"] = ResolveModeString(context)
    };

    public Dictionary<string, object?> BuildEndData(ActivityExecutionContext context)
    {
        var data = new Dictionary<string, object?>
        {
            ["repository"] = Repository.Get(context),
            ["sessionId"] = SessionId.Get(context)
        };
        if (context.GetVariable<object?>("LastMonitorResult") is AgentMonitorResult r)
        {
            data["workflowRunId"] = r.WorkflowRunId;
            data["conclusion"] = r.Conclusion;
            data["durationSeconds"] = r.DurationSeconds;
            data["workflowRunUrl"] = r.WorkflowRunUrl;
        }
        return data;
    }

    // ─── Inputs ─────────────────────────────────────────────────────────
    [Input(Description = "Repository in owner/repo format")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Branch the workflow was dispatched on")]
    public Input<string> BranchName { get; set; } = default!;

    [Input(Description = "Tamma session ID for correlation")]
    public Input<string> SessionId { get; set; } = default!;

    [Input(Description = "Timestamp of the dispatch (to filter old runs)")]
    public Input<DateTime> DispatchedAfter { get; set; } = default!;

    [Input(Description = "Poll interval in seconds")]
    public Input<int> PollIntervalSeconds { get; set; } = new(30);

    [Input(Description = "Timeout in minutes")]
    public Input<int> TimeoutMinutes { get; set; } = new(35);

    [Input(Description = "Monitor mode: Auto (webhook with poll fallback), Poll (default), or Webhook (webhook-only, no fallback)")]
    public Input<string> Mode { get; set; } = new("Poll");

    // ─── Outputs ────────────────────────────────────────────────────────
    [Output(Description = "The GitHub workflow run ID")]
    public Output<long> WorkflowRunId { get; set; } = default!;

    [Output(Description = "Final status (typically 'completed')")]
    public Output<string> Status { get; set; } = default!;

    [Output(Description = "Conclusion: success, failure, cancelled, timed_out, not_found")]
    public Output<string> Conclusion { get; set; } = default!;

    [Output(Description = "HTML URL to the workflow run")]
    public Output<string> WorkflowRunUrl { get; set; } = default!;

    [Output(Description = "Total execution time in seconds")]
    public Output<int> DurationSeconds { get; set; } = default!;

    [Output(Description = "API URL to download artifacts")]
    public Output<string> ArtifactsUrl { get; set; } = default!;

    [JsonConstructor]
    public MonitorAgentWorkflowActivity() { }

    public MonitorAgentWorkflowActivity(
        ILogger<MonitorAgentWorkflowActivity> logger,
        IAgentMonitorService monitorService)
    {
        _logger = logger;
        _monitorService = monitorService;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var startedAt = DateTime.UtcNow;
        TammaEventEmitter.EmitStart(context, this, this, _logger);

        if (_monitorService is null)
        {
            const string msg = "IAgentMonitorService not registered — MonitorAgentWorkflowActivity requires DI.";
            _logger?.LogError(msg);
            SetOutputs(context, new AgentMonitorResult(0, "error", "monitor_not_configured", string.Empty, 0, string.Empty));
            TammaEventEmitter.EmitFailure(context, this, this, _logger, DateTime.UtcNow - startedAt, msg);
            await context.CompleteActivityWithOutcomesAsync("Failed");
            return;
        }

        var request = new AgentExecutionRequest(
            Repository: Repository.Get(context),
            BranchName: BranchName.Get(context),
            IssueNumber: 0,
            IssueTitle: string.Empty,
            Task: string.Empty,
            PlanJson: string.Empty,
            SessionId: SessionId.Get(context),
            AgentProvider: string.Empty,
            AgentConfigJson: null,
            WorkflowFileName: null,
            TimeoutMinutes: TimeoutMinutes.Get(context));

        var options = new AgentMonitorOptions(
            PollIntervalSeconds: Math.Max(5, PollIntervalSeconds.Get(context)),
            TimeoutMinutes: Math.Max(1, TimeoutMinutes.Get(context)),
            Mode: ResolveMode(context));

        try
        {
            var result = await _monitorService.MonitorAsync(
                request, DispatchedAfter.Get(context), options, context.CancellationToken);

            SetOutputs(context, result);
            context.SetVariable("LastMonitorResult", result);

            if (string.Equals(result.Conclusion, "success", StringComparison.OrdinalIgnoreCase))
            {
                TammaEventEmitter.EmitSuccess(context, this, this, _logger, DateTime.UtcNow - startedAt);
                await context.CompleteActivityWithOutcomesAsync("Completed");
            }
            else
            {
                _logger?.LogWarning(
                    "Monitor completed with non-success conclusion for {Repository}/{Branch}: {Conclusion}",
                    request.Repository, request.BranchName, result.Conclusion);
                TammaEventEmitter.EmitFailure(
                    context, this, this, _logger,
                    DateTime.UtcNow - startedAt,
                    $"conclusion={result.Conclusion}");
                await context.CompleteActivityWithOutcomesAsync("Failed");
            }
        }
        catch (OperationCanceledException)
        {
            _logger?.LogInformation(
                "Monitor cancelled for {Repository}/{Branch}",
                request.Repository, request.BranchName);
            SetOutputs(context, new AgentMonitorResult(0, "cancelled", "cancelled", string.Empty, 0, string.Empty));
            TammaEventEmitter.EmitFailure(
                context, this, this, _logger, DateTime.UtcNow - startedAt, "cancelled");
            await context.CompleteActivityWithOutcomesAsync("Failed");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "Unexpected error monitoring workflow run for {Repository}/{Branch}",
                request.Repository, request.BranchName);
            SetOutputs(context, new AgentMonitorResult(0, "error", "exception", string.Empty, 0, string.Empty));
            TammaEventEmitter.EmitFailure(
                context, this, this, _logger, DateTime.UtcNow - startedAt, ex.Message);
            await context.CompleteActivityWithOutcomesAsync("Failed");
        }
    }

    private void SetOutputs(ActivityExecutionContext context, AgentMonitorResult r)
    {
        WorkflowRunId.Set(context, r.WorkflowRunId);
        Status.Set(context, r.Status);
        Conclusion.Set(context, r.Conclusion);
        WorkflowRunUrl.Set(context, r.WorkflowRunUrl);
        DurationSeconds.Set(context, r.DurationSeconds);
        ArtifactsUrl.Set(context, r.ArtifactsUrl);
    }

    /// <summary>
    /// Parse the <see cref="Mode"/> input into an <see cref="AgentMonitorMode"/>.
    /// Unknown values fall through to <see cref="AgentMonitorMode.Poll"/> so
    /// a typo never disables poll mode silently.
    /// </summary>
    private AgentMonitorMode ResolveMode(ActivityExecutionContext context)
    {
        var raw = Mode.Get(context);
        if (string.IsNullOrWhiteSpace(raw)) return AgentMonitorMode.Poll;
        return raw.Trim().ToLowerInvariant() switch
        {
            "auto" => AgentMonitorMode.Auto,
            "webhook" => AgentMonitorMode.Webhook,
            "poll" => AgentMonitorMode.Poll,
            _ => AgentMonitorMode.Poll
        };
    }

    private string ResolveModeString(ActivityExecutionContext context) =>
        ResolveMode(context) switch
        {
            AgentMonitorMode.Auto => "auto",
            AgentMonitorMode.Webhook => "webhook",
            _ => "poll"
        };
}
