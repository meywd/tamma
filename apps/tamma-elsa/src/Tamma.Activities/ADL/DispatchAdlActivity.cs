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
/// Dispatches a new ADL Orchestrator workflow instance (fire & forget).
/// The current instance finishes; the new one picks up with fresh config.
/// This is how the ADL runs forever — each cycle is a fresh instance.
/// </summary>
[Activity(
    "Tamma.ADL",
    "Dispatch ADL",
    "Dispatch a new ADL Orchestrator cycle",
    Kind = ActivityKind.Task
)]
public class DispatchAdlActivity : TammaAsyncActivity
{
    public override string? EventType => "ADL.SELF.DISPATCH";

    private readonly IWorkflowDispatcher? _dispatcher;

    [Input(Description = "Config JSON to pass to the new instance")]
    public Input<string> ConfigJson { get; set; } = default!;

    [JsonConstructor]
    public DispatchAdlActivity() { }

    public DispatchAdlActivity(
        ILogger<DispatchAdlActivity> logger,
        IWorkflowDispatcher dispatcher)
    {
        Logger = logger;
        _dispatcher = dispatcher;
    }

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        if (_dispatcher == null)
        {
            Logger?.LogWarning("No IWorkflowDispatcher available, cannot restart ADL");
            return;
        }

        var configJson = ConfigJson.Get(context);

        var request = new DispatchWorkflowDefinitionRequest
        {
            DefinitionId = "adl-orchestrator",
            Input = new Dictionary<string, object>
            {
                ["configJson"] = configJson,
            },
        };

        await _dispatcher.DispatchAsync(request);
        Logger?.LogInformation("Dispatched new ADL Orchestrator cycle");
    }
}
