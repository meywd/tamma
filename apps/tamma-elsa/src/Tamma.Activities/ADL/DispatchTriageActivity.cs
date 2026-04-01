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
/// Dispatches the Issue Triage workflow with event emission.
/// Waits for completion so the ADL can re-select after triage.
/// </summary>
[Activity(
    "Tamma.ADL",
    "Dispatch Triage",
    "Dispatch issue triage workflow for untriaged issues",
    Kind = ActivityKind.Task
)]
public class DispatchTriageActivity : TammaAsyncActivity
{
    public override string? EventType => "ADL.TRIAGE.DISPATCH";

    private readonly IWorkflowDispatcher? _dispatcher;

    [Input(Description = "Repository (owner/repo)")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Number of untriaged issues")]
    public Input<int> UntriagedCount { get; set; } = new(0);

    [JsonConstructor]
    public DispatchTriageActivity() { }

    public DispatchTriageActivity(
        ILogger<DispatchTriageActivity> logger,
        IWorkflowDispatcher dispatcher)
    {
        Logger = logger;
        _dispatcher = dispatcher;
    }

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        if (_dispatcher == null)
        {
            Logger?.LogWarning("No IWorkflowDispatcher available, skipping triage dispatch");
            return;
        }

        var input = new Dictionary<string, object>
        {
            ["repository"] = Repository.Get(context),
        };

        var request = new DispatchWorkflowDefinitionRequest
        {
            DefinitionId = "issue-triage",
            Input = input,
        };

        await _dispatcher.DispatchAsync(request);

        Logger?.LogInformation(
            "Dispatched issue-triage for {Count} untriaged issues",
            UntriagedCount.Get(context));
    }

    public override Dictionary<string, object?> BuildStartData(ActivityExecutionContext context) => new()
    {
        ["repository"] = Repository.Get(context),
        ["untriagedCount"] = UntriagedCount.Get(context),
    };
}
