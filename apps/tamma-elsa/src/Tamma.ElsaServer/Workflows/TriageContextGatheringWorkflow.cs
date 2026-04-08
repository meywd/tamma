using System.Text.Json;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Triage Context Gathering — gathers context for triage, focused on:
/// - Code usage of affected package/module
/// - Dependency graph
/// - CVE details (for security alerts)
/// - Changelog and migration guides
///
/// Dispatches llm-call with role=developer, action=context-scan with triage-specific variables.
///
/// Flow:
///   Init → Gather Context (llm-call) → Extract Result → Output → Finish
///
/// Inputs: repository, itemJson
/// Outputs: contextJson
/// </summary>
public class TriageContextGatheringWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Triage Context Gathering";
        builder.DefinitionId = "triage-context-gathering";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Gather context for triage: code usage, deps, CVE, changelog";

        // ================================================================
        // Variables
        // ================================================================
        var repository = builder.WithVariable<string>("Repository", "");
        var itemJson = builder.WithVariable<string>("ItemJson", "");
        var contextJson = builder.WithVariable<string>("ContextJson", "{}");
        var itemType = builder.WithVariable<string>("ItemType", "issue");

        var llmResult = builder.WithVariable<IDictionary<string, object>?>();

        // ================================================================
        // 1. Init
        // ================================================================
        var init = new SetVariable
        {
            Id = "Init", Name = "Initialize",
            Variable = repository,
            Value = new Input<object?>(ctx =>
            {
                var repo = ctx.GetInput<string>("repository") ?? "";
                var item = ctx.GetInput<string>("itemJson") ?? "";
                itemJson.Set(ctx, item);

                // Detect item type for context-scan focus
                var type = "issue";
                if (item.Contains("\"type\":\"security", StringComparison.OrdinalIgnoreCase)
                    || item.Contains("\"advisory\"", StringComparison.OrdinalIgnoreCase)
                    || item.Contains("\"cve\"", StringComparison.OrdinalIgnoreCase))
                    type = "security";
                else if (item.Contains("\"type\":\"dependabot", StringComparison.OrdinalIgnoreCase)
                    || item.Contains("\"dependency\"", StringComparison.OrdinalIgnoreCase))
                    type = "dependency";
                itemType.Set(ctx, type);

                return (object)repo;
            })
        };
        init.SetDisplayText("Initialize");

        // ================================================================
        // 2. Gather Context (via LlmCallWorkflow)
        // ================================================================
        var gatherContext = new DispatchWorkflow
        {
            Id = "GatherContext", Name = "Gather Context",
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["role"] = "developer",
                ["action"] = "context-scan",
                ["variables"] = new Dictionary<string, object>
                {
                    ["itemJson"] = itemJson.Get(ctx),
                    ["itemType"] = itemType.Get(ctx),
                    ["repository"] = repository.Get(ctx),
                    ["scanFocus"] = "triage",
                },
                ["enableTools"] = true,
            }),
            WaitForCompletion = new(true),
            Result = new(llmResult),
        };
        gatherContext.SetDisplayText("Gather Context");

        // ================================================================
        // 3. Extract Result
        // ================================================================
        var extractResult = new SetVariable
        {
            Id = "ExtractResult", Name = "Extract Result",
            Variable = contextJson,
            Value = new Input<object?>(ctx =>
            {
                var result = llmResult.Get(ctx);
                if (result != null && result.TryGetValue("llmResponse", out var r))
                {
                    var output = r?.ToString() ?? "{}";

                    // Try to extract JSON
                    var jsonStart = output.IndexOf('{');
                    var jsonEnd = output.LastIndexOf('}');
                    if (jsonStart >= 0 && jsonEnd > jsonStart)
                    {
                        var candidate = output[jsonStart..(jsonEnd + 1)];
                        try
                        {
                            JsonDocument.Parse(candidate);
                            return (object)candidate;
                        }
                        catch { /* not valid JSON, use raw */ }
                    }

                    // Wrap raw text as context
                    return (object)JsonSerializer.Serialize(new Dictionary<string, string>
                    {
                        ["rawContext"] = output,
                    });
                }
                return (object)"{}";
            })
        };
        extractResult.SetDisplayText("Extract Result");

        // ================================================================
        // 4. Set Outputs
        // ================================================================
        var setOutputs = new SetOutput
        { Id = "OutContext", OutputName = new("contextJson"), OutputValue = new(ctx => (object)contextJson.Get(ctx)) };
        setOutputs.SetDisplayText("Output Context");

        var finish = new Finish { Id = "Finish", Name = "Complete" };
        finish.SetDisplayText("Complete");

        // ================================================================
        // Flowchart
        // ================================================================
        builder.Root = new Flowchart
        {
            Id = "TriageContextGatheringFlowchart",
            Start = init,
            Activities =
            {
                init, gatherContext, extractResult,
                setOutputs, finish,
            },
            Connections =
            {
                Connect(init, gatherContext),
                Connect(gatherContext, extractResult),
                Connect(extractResult, setOutputs),
                Connect(setOutputs, finish),
            }
        };
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));
}
