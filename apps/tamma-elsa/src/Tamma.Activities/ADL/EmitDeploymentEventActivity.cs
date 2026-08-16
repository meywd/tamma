using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.ADL;

/// <summary>
/// Emits a <c>DEPLOY.*</c> DCB event (completeness audit item 2) for the
/// built-out <c>deployment-pipeline</c> workflow by appending a
/// <see cref="TammaEvent"/> to the workflow's <c>tamma:events</c> transient list
/// via <see cref="TammaEventEmitter.Emit"/>. The engine event drain
/// (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) flushes that list
/// <i>durably</i> to the tenant <c>domain_events</c> store after this activity
/// runs — the drain resolves the tenant from the workflow scope (the pipeline
/// stamps a <c>TenantId</c> variable). The event therefore persists without this
/// activity holding any DB / repository dependency of its own (none is registered
/// in the Elsa engine — a directly-injected <c>IEventRepository</c> would be inert
/// and silently drop the event, the same trap the PR / merge / branch exemplars
/// avoid).
///
/// <para>This is the per-edge deploy emitter: the workflow fires it before each
/// stage dispatch (<c>DEPLOY.STAGE.STARTED</c>), after each stage extract
/// (<c>DEPLOY.STAGE.SUCCESS</c> / <c>DEPLOY.STAGE.FAILED</c>), around the prod
/// approval gate (<c>DEPLOY.PRODUCTION.APPROVAL_REQUESTED/APPROVED/REJECTED</c>),
/// in the rollback branch (<c>DEPLOY.ROLLBACK.*</c>), and at the terminals
/// (<c>DEPLOY.PIPELINE.SUCCESS/FAILED</c>) — so every meaningful edge lands a
/// queryable audit row for time-travel debugging + SOC2 compliance.</para>
/// </summary>
[Activity(
    "Tamma.ADL",
    "Emit Deployment Event",
    "Emit a DEPLOY.* DCB event for the deployment-pipeline audit trail",
    Kind = ActivityKind.Task
)]
public class EmitDeploymentEventActivity : Activity
{
    private readonly ILogger<EmitDeploymentEventActivity>? _logger;

    [Input(Description = "Event type — e.g. DEPLOY.STAGE.STARTED / DEPLOY.PRODUCTION.APPROVED")]
    public Input<string> EventType { get; set; } = default!;

    [Input(Description = "Issue number this deployment resolves")]
    public Input<int> IssueNumber { get; set; } = new(0);

    [Input(Description = "Repository in owner/repo format")]
    public Input<string?> Repository { get; set; } = new((string?)null);

    [Input(Description = "Merged commit SHA being deployed")]
    public Input<string?> MergeSha { get; set; } = new((string?)null);

    [Input(Description = "Deploy stage — qa | uat | production (empty for pipeline-level events)")]
    public Input<string?> Stage { get; set; } = new((string?)null);

    [Input(Description = "Deployment mode (dev | business) driving the prod approval gate")]
    public Input<string?> Mode { get; set; } = new((string?)null);

    [Input(Description = "Tenant id (empty / single-user → platform-scope event)")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [Input(Description = "Event data payload as JSON (status, reason, completedStages, rollbackStatus, durationMs, approver)")]
    public Input<string?> DataJson { get; set; } = new((string?)null);

    [JsonConstructor]
    public EmitDeploymentEventActivity() { }

    public EmitDeploymentEventActivity(ILogger<EmitDeploymentEventActivity> logger)
    {
        _logger = logger;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var type = EventType.GetOrDefault(context) ?? DeployEvents.PipelineFailed;
        var issueNumber = IssueNumber.GetOrDefault(context);
        var repository = Repository.GetOrDefault(context) ?? "";
        var mergeSha = MergeSha.GetOrDefault(context) ?? "";
        var stage = Stage.GetOrDefault(context) ?? "";
        var mode = Mode.GetOrDefault(context) ?? "";
        var tenantId = DeployEvents.ParseTenantId(TenantId.GetOrDefault(context));
        var data = ParseData(DataJson.GetOrDefault(context));

        var evt = BuildTammaEvent(type, issueNumber, repository, mergeSha, stage, mode, tenantId, data);
        TammaEventEmitter.Emit(context, this, _logger, evt);

        _logger?.LogInformation(
            "Emitted {Type} for issue #{Issue} stage {Stage} in {Repo}",
            type, issueNumber, string.IsNullOrEmpty(stage) ? "<pipeline>" : stage, repository);

        await context.CompleteActivityAsync(); // 2026-08-13 — bare Activity does NOT auto-complete (see EmitEscalationEventActivity precedent); without this the workflow hangs here forever
        return;
    }

    /// <summary>
    /// Map the deploy event inputs onto a <see cref="TammaEvent"/> expressed as the
    /// engine's transient-list event so the merged drain persists it. Tags carry
    /// the queryable DCB index keys (<c>issueId</c>/<c>issueNumber</c>/
    /// <c>repository</c>/<c>mergeSha</c>/<c>stage</c>/<c>mode</c>/<c>tenantId</c>);
    /// <c>Data</c> carries the operation payload (status / reason / completedStages
    /// / rollbackStatus). Pure (no Elsa context) — exposed for unit testing.
    /// </summary>
    public static TammaEvent BuildTammaEvent(
        string type,
        int issueNumber,
        string repository,
        string mergeSha,
        string stage,
        string mode,
        Guid? tenantId,
        IReadOnlyDictionary<string, object?>? data)
    {
        var tags = new Dictionary<string, object?>
        {
            ["issueId"] = issueNumber.ToString(),
            ["issueNumber"] = issueNumber.ToString(),
        };
        if (!string.IsNullOrWhiteSpace(repository)) tags["repository"] = repository;
        if (!string.IsNullOrWhiteSpace(mergeSha)) tags["mergeSha"] = mergeSha;
        if (!string.IsNullOrWhiteSpace(stage)) tags["stage"] = stage;
        if (!string.IsNullOrWhiteSpace(mode)) tags["mode"] = mode;
        if (tenantId is not null) tags["tenantId"] = tenantId.Value.ToString("D");

        return new TammaEvent
        {
            EventType = type,
            Status = DeployEvents.IsFailureType(type) ? "error" : "success",
            Tags = tags,
            Data = data is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?>(data),
        };
    }

    /// <summary>
    /// Deserialize the JSON data payload into a dictionary for the event builder.
    /// Returns null on empty/malformed input (the event still emits with "{}").
    /// Exposed for unit testing the parse path.
    /// </summary>
    public static IReadOnlyDictionary<string, object?>? ParseData(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return System.Text.Json.JsonSerializer
                .Deserialize<Dictionary<string, object?>>(json);
        }
        catch
        {
            return null;
        }
    }
}
