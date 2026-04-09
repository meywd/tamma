using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using Tamma.Activities.ADL;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Triage Item Cycle — processes a single untriaged item through
/// context gathering, 4-role panel review, PO decision, and label application.
///
/// Runs as a singleton workflow: only one instance at a time.
/// IssueTriageWorkflow dispatches one per item (fire & forget);
/// Elsa queues subsequent dispatches until the current one finishes.
///
/// Flow:
///   Init → Gather Context (llm-call) → Panel Review (llm-call x4)
///   → PO Decision (llm-call) → Apply Labels & Comment → Finish
///
/// Inputs: repository, itemJson
/// </summary>
public class TriageItemCycleWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Triage Item Cycle";
        builder.DefinitionId = "triage-item-cycle";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Process one untriaged item: context → panel → PO → labels (singleton)";

        // ================================================================
        // Variables
        // ================================================================
        var repository = builder.WithVariable<string>("Repository", "");
        var itemJson = builder.WithVariable<string>("ItemJson", "");
        var contextJson = builder.WithVariable<string>("ContextJson", "");
        var panelResultJson = builder.WithVariable<string>("PanelResultJson", "");
        var poDecisionJson = builder.WithVariable<string>("PODecisionJson", "");
        var subResult = builder.WithVariable<IDictionary<string, object>?>();

        // ================================================================
        // 1. Init — read inputs
        // ================================================================
        var init = new SetVariable
        {
            Id = "Init", Name = "Initialize",
            Variable = repository,
            Value = new Input<object?>(ctx =>
            {
                var repo = ctx.GetInput<string>("repository") ?? "";
                itemJson.Set(ctx, ctx.GetInput<string>("itemJson") ?? "");
                return (object)repo;
            })
        };
        init.SetDisplayText("Initialize");

        // ================================================================
        // 2. Gather Context (code usage, deps, CVE details)
        // ================================================================
        var gatherContext = new DispatchWorkflow
        {
            Id = "GatherTriageContext",
            Name = "Gather Triage Context",
            WorkflowDefinitionId = new("triage-context-gathering"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["itemJson"] = itemJson.Get(ctx),
            }),
            WaitForCompletion = new(true),
            Result = new(subResult),
        };
        gatherContext.SetDisplayText("Gather Triage Context");

        var extractContext = new SetVariable
        {
            Id = "ExtractContext",
            Name = "Extract Context",
            Variable = contextJson,
            Value = new Input<object?>(ctx =>
            {
                var result = subResult.Get(ctx);
                if (result != null && result.TryGetValue("contextJson", out var c))
                    return (object)(c?.ToString() ?? "");
                return (object)"";
            })
        };
        extractContext.SetDisplayText("Extract Context");

        // ================================================================
        // 3. Panel Review (security analyst, dev, devops, qa)
        // ================================================================
        var panelReview = new DispatchWorkflow
        {
            Id = "PanelReview",
            Name = "Panel Review",
            WorkflowDefinitionId = new("triage-panel-review"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["itemJson"] = itemJson.Get(ctx),
                ["contextJson"] = contextJson.Get(ctx),
            }),
            WaitForCompletion = new(true),
            Result = new(subResult),
        };
        panelReview.SetDisplayText("Panel Review");

        var extractPanelResult = new SetVariable
        {
            Id = "ExtractPanelResult",
            Name = "Extract Panel Result",
            Variable = panelResultJson,
            Value = new Input<object?>(ctx =>
            {
                var result = subResult.Get(ctx);
                if (result != null && result.TryGetValue("panelResultJson", out var p))
                    return (object)(p?.ToString() ?? "");
                return (object)"";
            })
        };
        extractPanelResult.SetDisplayText("Extract Panel Result");

        // ================================================================
        // 4. PO Decision (priority, labels, automation level)
        // ================================================================
        var poDecision = new DispatchWorkflow
        {
            Id = "PODecision",
            Name = "PO Decision",
            WorkflowDefinitionId = new("triage-po-decision"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["itemJson"] = itemJson.Get(ctx),
                ["panelResultJson"] = panelResultJson.Get(ctx),
            }),
            WaitForCompletion = new(true),
            Result = new(subResult),
        };
        poDecision.SetDisplayText("PO Decision");

        var extractDecision = new SetVariable
        {
            Id = "ExtractDecision",
            Name = "Extract Decision",
            Variable = poDecisionJson,
            Value = new Input<object?>(ctx =>
            {
                var result = subResult.Get(ctx);
                if (result != null && result.TryGetValue("decisionJson", out var d))
                    return (object)(d?.ToString() ?? "");
                return (object)"";
            })
        };
        extractDecision.SetDisplayText("Extract Decision");

        // ================================================================
        // 5. Apply Labels + Post Comment
        // ================================================================
        var applyLabels = new ApplyTriageResultActivity
        {
            Id = "ApplyLabels",
            Name = "Apply Labels & Comment",
            Repository = new Input<string>(ctx => repository.Get(ctx)),
            ItemJson = new Input<string>(ctx => itemJson.Get(ctx)),
            DecisionJson = new Input<string>(ctx => poDecisionJson.Get(ctx)),
        };
        applyLabels.SetDisplayText("Apply Labels & Comment");

        // ================================================================
        // 6. Finish
        // ================================================================
        var finish = new Finish { Id = "Finish", Name = "Complete" };
        finish.SetDisplayText("Complete");

        // ================================================================
        // Flowchart
        // ================================================================
        builder.Root = new Flowchart
        {
            Id = "TriageItemCycleFlowchart",
            Start = init,
            Activities =
            {
                init,
                gatherContext, extractContext,
                panelReview, extractPanelResult,
                poDecision, extractDecision,
                applyLabels, finish,
            },
            Connections =
            {
                Connect(init, gatherContext),
                Connect(gatherContext, extractContext),
                Connect(extractContext, panelReview),
                Connect(panelReview, extractPanelResult),
                Connect(extractPanelResult, poDecision),
                Connect(poDecision, extractDecision),
                Connect(extractDecision, applyLabels),
                Connect(applyLabels, finish),
            }
        };
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));
}
