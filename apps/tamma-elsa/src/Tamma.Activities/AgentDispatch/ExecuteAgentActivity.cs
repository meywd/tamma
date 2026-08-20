using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.ADL;
using Tamma.Activities.AgentDispatch.Models;
using Tamma.Activities.Core;
using Tamma.Activities.LlmCall;

namespace Tamma.Activities.AgentDispatch;

/// <summary>
/// Story 19-5 AC-5 — Elsa activity wrapper around
/// <see cref="IAgentExecutor"/>. Selects Local vs GitHubActions via
/// <see cref="AgentExecutorFactory"/>, then runs the request through
/// whichever executor was chosen.
///
/// <para>The workflow sees a single activity with a single output
/// (<see cref="AgentExecutionResult"/>) regardless of mode. This is the
/// integration point that makes the same workflow behave identically in
/// CLI, self-hosted, and SaaS deployments.</para>
///
/// <para>Outcomes:
/// <list type="bullet">
///   <item><c>Completed</c> — agent succeeded.</item>
///   <item><c>Failed</c> — agent (or its dispatch/monitor/collect cycle)
///     failed.</item>
/// </list>
/// </para>
/// </summary>
[Activity(
    "Tamma.AgentDispatch",
    "Execute Agent",
    "Execute an AI agent via the configured execution mode (local or GitHub Actions)",
    Kind = ActivityKind.Task)]
[FlowNode("Completed", "Failed")]
public class ExecuteAgentActivity : Activity, ITammaActivity
{
    private readonly ILogger<ExecuteAgentActivity>? _logger;
    private readonly AgentExecutorFactory? _factory;

    public string? EventType => "AGENT.EXECUTION";

    public Dictionary<string, object?> BuildStartData(ActivityExecutionContext context) => new()
    {
        ["repository"] = Repository.Get(context),
        ["branchName"] = BranchName.Get(context),
        ["issueNumber"] = IssueNumber.Get(context),
        ["sessionId"] = SessionId.Get(context),
        ["task"] = Task.Get(context),
        ["agentProvider"] = AgentProvider.Get(context),
        ["timeoutMinutes"] = TimeoutMinutes.Get(context),
        ["mode"] = context.GetVariable<string>("AgentExecutionMode") ?? "auto"
    };

    public Dictionary<string, object?> BuildEndData(ActivityExecutionContext context)
    {
        var data = new Dictionary<string, object?>
        {
            ["repository"] = Repository.Get(context),
            ["branchName"] = BranchName.Get(context),
            ["sessionId"] = SessionId.Get(context)
        };
        if (context.GetVariable<object?>("LastAgentExecutionResult") is AgentExecutionResult r)
        {
            data["success"] = r.Success;
            data["prNumber"] = r.PrNumber;
            data["filesChanged"] = r.FilesChanged.Length;
            data["commitsCount"] = r.CommitsCount;
            data["tokensUsed"] = r.TokensUsed;
            data["durationSeconds"] = r.DurationSeconds;
            data["agentProvider"] = r.AgentProvider;
            data["checksPassed"] = r.ChecksPassed;
            data["mode"] = r.ExecutionMode;
        }
        return data;
    }

    // ─── Inputs (mirror AgentExecutionRequest) ───────────────────────────
    [Input(Description = "Repository in owner/repo format")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Branch for the agent to work on")]
    public Input<string> BranchName { get; set; } = default!;

    [Input(Description = "Issue number")]
    public Input<int> IssueNumber { get; set; } = default!;

    [Input(Description = "Issue title")]
    public Input<string> IssueTitle { get; set; } = new(string.Empty);

    [Input(Description = "Task type: implement, fix, debug, review, test")]
    public Input<string> Task { get; set; } = new("implement");

    [Input(Description = "Serialized development plan")]
    public Input<string> PlanJson { get; set; } = new("{}");

    [Input(Description = "Tamma session ID")]
    public Input<string> SessionId { get; set; } = default!;

    [Input(Description = "Agent provider")]
    public Input<string> AgentProvider { get; set; } = new("claude-code");

    [Input(Description = "Agent config JSON")]
    public Input<string?> AgentConfigJson { get; set; } = default!;

    [Input(Description = "Timeout in minutes")]
    public Input<int> TimeoutMinutes { get; set; } = new(30);

    [Input(Description = "Override the execution mode (local, github_actions)")]
    public Input<string?> ModeOverride { get; set; } = default!;

    // ─── Outputs (mirror AgentExecutionResult) ──────────────────────────
    [Output(Description = "Whether the agent completed successfully")]
    public Output<bool> Success { get; set; } = default!;

    [Output(Description = "Execution mode used (local / github_actions)")]
    public Output<string> ExecutionMode { get; set; } = default!;

    [Output(Description = "PR number if created")]
    public Output<int?> PrNumber { get; set; } = default!;

    [Output(Description = "PR HTML URL")]
    public Output<string?> PrUrl { get; set; } = default!;

    [Output(Description = "HEAD commit SHA")]
    public Output<string> CommitSha { get; set; } = default!;

    [Output(Description = "JSON-serialized array of changed file paths")]
    public Output<string> FilesChangedJson { get; set; } = default!;

    [Output(Description = "Number of commits made by the agent")]
    public Output<int> CommitsCount { get; set; } = default!;

    [Output(Description = "Whether CI checks passed")]
    public Output<bool?> ChecksPassed { get; set; } = default!;

    [Output(Description = "Total tokens consumed")]
    public Output<int> TokensUsed { get; set; } = default!;

    [Output(Description = "Execution duration in seconds")]
    public Output<int> DurationSeconds { get; set; } = default!;

    [Output(Description = "Error details if the agent failed")]
    public Output<string?> ErrorMessage { get; set; } = default!;

    [Output(Description = "Agent log summary")]
    public Output<string?> AgentLogSummary { get; set; } = default!;

    [JsonConstructor]
    public ExecuteAgentActivity() { }

    public ExecuteAgentActivity(
        ILogger<ExecuteAgentActivity> logger,
        AgentExecutorFactory factory)
    {
        _logger = logger;
        _factory = factory;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var startedAt = DateTime.UtcNow;
        TammaEventEmitter.EmitStart(context, this, this, _logger);

        // 2026-08-13 (engine-driven E2E run 38): store-rehydrated activities are
        // built by the [JsonConstructor] with NULL ctor-injected members — the
        // ctor-or-GetService idiom, or EVERY agent execution in a real engine
        // fails instantly ("AgentExecutorFactory not registered") and the task
        // loop silently degrades to its debug-retry leg.
        var factory = _factory ?? context.GetService<AgentExecutorFactory>();
        if (factory is null)
        {
            const string msg = "AgentExecutorFactory not registered — ExecuteAgentActivity requires DI.";
            _logger?.LogError(msg);
            SetOutputs(context, AgentExecutionResult.Failed(msg, AgentProvider.Get(context), "unknown"));
            TammaEventEmitter.EmitFailure(context, this, this, _logger, DateTime.UtcNow - startedAt, msg);
            await context.CompleteActivityWithOutcomesAsync("Failed");
            return;
        }

        var request = new AgentExecutionRequest(
            Repository: Repository.Get(context),
            BranchName: BranchName.Get(context),
            IssueNumber: IssueNumber.Get(context),
            IssueTitle: IssueTitle.Get(context) ?? string.Empty,
            Task: Task.Get(context),
            PlanJson: PlanJson.Get(context),
            SessionId: SessionId.Get(context),
            AgentProvider: AgentProvider.Get(context),
            AgentConfigJson: AgentConfigJson.GetOrDefault(context),
            WorkflowFileName: "tamma-agent.yml",
            TimeoutMinutes: TimeoutMinutes.Get(context));

        try
        {
            var executor = factory.Create(ModeOverride.GetOrDefault(context));
            context.SetVariable("AgentExecutionMode", executor.Mode);

            _logger?.LogInformation(
                "Executing agent via {Mode} for {Repository}/{Branch} (session={SessionId})",
                executor.Mode, request.Repository, request.BranchName, request.SessionId);

            // CRASH VISIBILITY — the await below is the cycle's longest non-durable
            // stretch (dispatch → discover → poll to terminal, up to ~35 minutes) with no
            // bookmark and nothing written to the workflow store until it returns. A
            // deploy or crash inside it leaves the instance Running/Executing forever;
            // OrphanedCycleRecoveryService detects and clears that, but without this
            // marker there is no record of WHICH agent run was in flight, so nobody can
            // tell whether the platform-side run kept going. Posted straight to the event
            // store (not through the per-activity drain, which only flushes AFTER the
            // activity returns — i.e. never, in the case this exists for). Best-effort:
            // it must never fail the run. A durable, resumable wait is story 40-2.
            await EmitInFlightMarkerAsync(context, request, executor.Mode).ConfigureAwait(false);

            var result = await executor.ExecuteAsync(request, context.CancellationToken);
            SetOutputs(context, result);
            context.SetVariable("LastAgentExecutionResult", result);

            if (result.Success)
            {
                TammaEventEmitter.EmitSuccess(context, this, this, _logger, DateTime.UtcNow - startedAt);
                await context.CompleteActivityWithOutcomesAsync("Completed");
            }
            else
            {
                TammaEventEmitter.EmitFailure(
                    context, this, this, _logger,
                    DateTime.UtcNow - startedAt,
                    result.ErrorMessage ?? "unknown");
                await context.CompleteActivityWithOutcomesAsync("Failed");
            }
        }
        catch (OperationCanceledException)
        {
            SetOutputs(context, AgentExecutionResult.Failed(
                "Agent execution cancelled", request.AgentProvider, "unknown"));
            TammaEventEmitter.EmitFailure(
                context, this, this, _logger, DateTime.UtcNow - startedAt, "cancelled");
            await context.CompleteActivityWithOutcomesAsync("Failed");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "ExecuteAgentActivity failed for {Repository}/{Branch}",
                request.Repository, request.BranchName);
            SetOutputs(context, AgentExecutionResult.Failed(
                $"Execute error: {ex.Message}", request.AgentProvider, "unknown"));
            TammaEventEmitter.EmitFailure(
                context, this, this, _logger, DateTime.UtcNow - startedAt, ex.Message);
            await context.CompleteActivityWithOutcomesAsync("Failed");
        }
    }

    /// <summary>
    /// Persist <c>AGENT.EXECUTION.INFLIGHT</c> immediately, BEFORE the long inline wait,
    /// so a crash inside that window leaves a durable record of the run that was in
    /// flight (repository, branch, session, mode, timeout). Swallows every failure — the
    /// marker is diagnostics, and losing it must not cost the agent run.
    /// </summary>
    private async Task EmitInFlightMarkerAsync(
        ActivityExecutionContext context, AgentExecutionRequest request, string mode)
    {
        try
        {
            var api = context.GetService<TammaApiClient>();
            if (api is null) return;

            var evt = new TammaEvent
            {
                EventType = AdlLoopEvents.AgentInFlight,
                Status = "started",
                ActivityId = Id,
                ActivityName = Name ?? nameof(ExecuteAgentActivity),
                WorkflowInstanceId = context.WorkflowExecutionContext.Id,
                Data = new Dictionary<string, object?>
                {
                    ["repository"] = request.Repository,
                    ["branchName"] = request.BranchName,
                    ["issueNumber"] = request.IssueNumber,
                    ["sessionId"] = request.SessionId,
                    ["agentProvider"] = request.AgentProvider,
                    ["timeoutMinutes"] = request.TimeoutMinutes,
                    ["mode"] = mode,
                },
                Tags = new Dictionary<string, object?>
                {
                    ["issueNumber"] = request.IssueNumber.ToString(),
                    ["sessionId"] = request.SessionId,
                },
            };

            await api.AppendEventsAsync(
                new[] { EventPersistenceMiddleware.ToWireRecord(evt) },
                request.TenantId,
                context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Could not persist the agent in-flight marker (continuing).");
        }
    }

    private void SetOutputs(ActivityExecutionContext context, AgentExecutionResult r)
    {
        Success.Set(context, r.Success);
        ExecutionMode.Set(context, r.ExecutionMode);
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
