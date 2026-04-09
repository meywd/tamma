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
/// Triage PO Decision — Product Owner makes final triage decision based on panel review.
///
/// Dispatches llm-call with role=product_owner, action=triage.
/// Parses decision: priority, type, complexity, automation level, labels, comment.
///
/// Flow:
///   Init → PO Decision (llm-call) → Extract Decision → Output → Finish
///
/// Expected LLM response JSON:
/// {
///   "priority": "urgent|high|normal|low",
///   "type": "bug|feature|chore|security|docs",
///   "complexity": "trivial|simple|medium|complex|epic",
///   "automation": "tamma-auto|tamma-assist|needs-human",
///   "labels": ["label1", "label2"],
///   "comment": "Triage summary..."
/// }
///
/// Inputs: repository, itemJson, panelResultJson
/// Outputs: decisionJson
/// </summary>
public class TriagePODecisionWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Triage PO Decision";
        builder.DefinitionId = "triage-po-decision";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "PO makes final triage decision based on panel review";

        // ================================================================
        // Variables
        // ================================================================
        var repository = builder.WithVariable<string>("Repository", "");
        var itemJson = builder.WithVariable<string>("ItemJson", "");
        var panelResultJson = builder.WithVariable<string>("PanelResultJson", "{}");
        var decisionJson = builder.WithVariable<string>("DecisionJson", "{}");

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
                itemJson.Set(ctx, ctx.GetInput<string>("itemJson") ?? "");
                panelResultJson.Set(ctx, ctx.GetInput<string>("panelResultJson") ?? "{}");
                return (object)repo;
            })
        };
        init.SetDisplayText("Initialize");

        // ================================================================
        // 2. PO Decision (via LlmCallWorkflow)
        // ================================================================
        var poDecisionCall = new DispatchWorkflow
        {
            Id = "PODecisionCall", Name = "PO Decision",
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["role"] = "product_owner",
                ["action"] = "triage",
                ["variables"] = new Dictionary<string, object>
                {
                    ["itemJson"] = itemJson.Get(ctx),
                    ["panelResultJson"] = panelResultJson.Get(ctx),
                    ["repository"] = repository.Get(ctx),
                },
                ["enableTools"] = false,
            }),
            WaitForCompletion = new(true),
            Result = new(llmResult),
        };
        poDecisionCall.SetDisplayText("PO Decision");

        // ================================================================
        // 3. Extract Decision
        // ================================================================
        var extractDecision = new SetVariable
        {
            Id = "ExtractDecision", Name = "Extract Decision",
            Variable = decisionJson,
            Value = new Input<object?>(ctx =>
            {
                var result = llmResult.Get(ctx);
                if (result != null && result.TryGetValue("llmResponse", out var r))
                {
                    var output = r?.ToString() ?? "{}";

                    // Extract JSON block
                    var jsonStart = output.IndexOf('{');
                    var jsonEnd = output.LastIndexOf('}');
                    if (jsonStart >= 0 && jsonEnd > jsonStart)
                    {
                        var candidate = output[jsonStart..(jsonEnd + 1)];
                        try
                        {
                            var doc = JsonDocument.Parse(candidate);
                            var root = doc.RootElement;

                            // Ensure required fields have defaults
                            var decision = new Dictionary<string, object>();

                            decision["priority"] = root.TryGetProperty("priority", out var p)
                                ? p.GetString() ?? "normal" : "normal";
                            decision["type"] = root.TryGetProperty("type", out var t)
                                ? t.GetString() ?? "feature" : "feature";
                            decision["complexity"] = root.TryGetProperty("complexity", out var c)
                                ? c.GetString() ?? "medium" : "medium";
                            decision["automation"] = root.TryGetProperty("automation", out var a)
                                ? a.GetString() ?? "needs-human" : "needs-human";

                            if (root.TryGetProperty("labels", out var l))
                                decision["labels"] = l.GetRawText();
                            else
                                decision["labels"] = "[]";

                            if (root.TryGetProperty("comment", out var cm))
                                decision["comment"] = cm.GetString() ?? "";
                            else
                                decision["comment"] = "";

                            return (object)JsonSerializer.Serialize(decision);
                        }
                        catch
                        {
                            // Fall through to default
                        }
                    }

                    // If raw text, wrap as comment
                    return (object)JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["priority"] = "normal",
                        ["type"] = "feature",
                        ["complexity"] = "medium",
                        ["automation"] = "needs-human",
                        ["labels"] = "[]",
                        ["comment"] = output,
                    });
                }

                // No result — return defaults
                return (object)JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["priority"] = "normal",
                    ["type"] = "feature",
                    ["complexity"] = "medium",
                    ["automation"] = "needs-human",
                    ["labels"] = "[]",
                    ["comment"] = "No PO decision received.",
                });
            })
        };
        extractDecision.SetDisplayText("Extract Decision");

        // ================================================================
        // 4. Set Outputs
        // ================================================================
        var setOutputs = new SetOutput
        { Id = "OutDecision", OutputName = new("decisionJson"), OutputValue = new(ctx => (object)decisionJson.Get(ctx)) };
        setOutputs.SetDisplayText("Output Decision");

        var finish = new Finish { Id = "Finish", Name = "Complete" };
        finish.SetDisplayText("Complete");

        // ================================================================
        // Flowchart
        // ================================================================
        builder.Root = new Flowchart
        {
            Id = "TriagePODecisionFlowchart",
            Start = init,
            Activities =
            {
                init, poDecisionCall, extractDecision,
                setOutputs, finish,
            },
            Connections =
            {
                Connect(init, poDecisionCall),
                Connect(poDecisionCall, extractDecision),
                Connect(extractDecision, setOutputs),
                Connect(setOutputs, finish),
            }
        };
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));
}
