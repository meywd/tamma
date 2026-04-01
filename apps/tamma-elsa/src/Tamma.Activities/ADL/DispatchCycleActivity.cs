using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.ADL;

/// <summary>
/// Dispatches a SingleIssueCycle workflow (fire & forget) with event emission.
/// Wraps Elsa's DispatchWorkflow to add audit trail.
/// </summary>
[Activity(
    "Tamma.ADL",
    "Dispatch Issue Cycle",
    "Fire-and-forget dispatch of a single issue cycle workflow",
    Kind = ActivityKind.Task
)]
public class DispatchCycleActivity : TammaAsyncActivity
{
    public override string? EventType => "ADL.CYCLE.DISPATCH";

    private readonly IWorkflowDispatcher? _dispatcher;

    [Input(Description = "Repository (owner/repo)")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Work item JSON")]
    public Input<string> WorkItemJson { get; set; } = default!;

    [Input(Description = "Issue number")]
    public Input<int> IssueNumber { get; set; } = default!;

    [Input(Description = "Bot assignee")]
    public Input<string> BotAssignee { get; set; } = default!;

    [Input(Description = "Base branch")]
    public Input<string> BaseBranch { get; set; } = default!;

    [Output(Description = "Dispatched workflow instance ID")]
    public Output<string?> InstanceId { get; set; } = default!;

    [JsonConstructor]
    public DispatchCycleActivity() { }

    public DispatchCycleActivity(
        ILogger<DispatchCycleActivity> logger,
        IWorkflowDispatcher dispatcher)
    {
        Logger = logger;
        _dispatcher = dispatcher;
    }

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        if (_dispatcher == null)
        {
            Logger?.LogWarning("No IWorkflowDispatcher available, skipping dispatch");
            InstanceId.Set(context, null);
            return;
        }

        var input = new Dictionary<string, object>
        {
            ["repository"] = Repository.Get(context),
            ["workItemJson"] = WorkItemJson.Get(context),
            ["issueNumber"] = IssueNumber.Get(context),
            ["botAssignee"] = BotAssignee.Get(context),
            ["baseBranch"] = BaseBranch.Get(context),
        };

        var request = new DispatchWorkflowDefinitionRequest
        {
            DefinitionId = "single-issue-cycle",
            Input = input,
        };

        var result = await _dispatcher.DispatchAsync(request);

        InstanceId.Set(context, result.InstanceId);

        Logger?.LogInformation(
            "Dispatched single-issue-cycle for issue #{IssueNumber}, instance {InstanceId}",
            IssueNumber.Get(context), result.InstanceId);
    }

    public override Dictionary<string, object?> BuildStartData(ActivityExecutionContext context) => new()
    {
        ["issueNumber"] = IssueNumber.Get(context),
        ["repository"] = Repository.Get(context),
    };

    public override Dictionary<string, object?> BuildEndData(ActivityExecutionContext context) => new()
    {
        ["issueNumber"] = IssueNumber.Get(context),
        ["instanceId"] = InstanceId.Get(context),
    };
}
