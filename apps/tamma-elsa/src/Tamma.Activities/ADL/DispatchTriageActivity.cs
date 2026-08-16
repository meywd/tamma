using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Contracts;
using Elsa.Workflows.Runtime.Requests;
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
        // Context fallback: Elsa rehydrates a persisted definition through the
        // [JsonConstructor], on which path the DI-injected field is null.
        var dispatcher = _dispatcher ?? context.GetService<IWorkflowDispatcher>();
        if (dispatcher == null)
        {
            Logger?.LogWarning("No IWorkflowDispatcher available, skipping triage dispatch");
            return;
        }

        var input = new Dictionary<string, object>
        {
            ["repository"] = Repository.Get(context),
        };

        // FIRE & FORGET, and non-fatal by design. This activity sits upstream of the
        // orchestrator's cooldown → restart edge, so an exception here faults the
        // instance BEFORE it can dispatch its successor and the autonomous loop stops
        // permanently. Failing to triage one batch must cost one tick, never the loop.
        // The version-id resolve lives INSIDE the try for the same reason.
        try
        {
            // 2026-08-13 — the request ctor takes the VERSION id, not the definition
            // id (see PublishedWorkflowDispatch: every background dispatch failed
            // WorkflowGraphNotFound before this resolve step existed).
            var definitionVersionId = await Tamma.Activities.Core.PublishedWorkflowDispatch
                .ResolvePublishedVersionIdAsync(
                    context.GetRequiredService<Elsa.Workflows.Management.IWorkflowDefinitionService>(),
                    "issue-triage");
            var request = new DispatchWorkflowDefinitionRequest(definitionVersionId)
            {
                Input = input,
            };

            await dispatcher.DispatchAsync(request, default);

            Logger?.LogInformation(
                "Dispatched issue-triage for {Count} untriaged issues",
                UntriagedCount.Get(context));
        }
        catch (Exception ex)
        {
            Logger?.LogError(
                ex,
                "Failed to dispatch issue-triage for {Repository}; continuing so the ADL loop restarts",
                Repository.Get(context));
        }
    }

    public override Dictionary<string, object?> BuildStartData(ActivityExecutionContext context) => new()
    {
        ["repository"] = Repository.Get(context),
        ["untriagedCount"] = UntriagedCount.Get(context),
    };
}
