using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using Tamma.Activities.ADL;
using Tamma.ElsaServer.Workflows.Helpers;
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
/// <para>Build-out (completeness audit 2026-06-22): the panel sub-workflow is now
/// fail-closed and reports a <c>panelStatus</c> (<c>ok</c>/<c>partial</c>/
/// <c>failed</c>). This cycle <b>honours</b> that signal: a <c>failed</c> panel
/// (too few panellists produced a usable assessment) routes to a loud
/// non-applying terminal — the PO decision is skipped and NO labels are applied
/// off a wholly-failed panel. Previously the cycle marched straight from the
/// panel to PO + label application regardless of panel health, so a fully-failed
/// panel still labelled the item (silent false success downstream).</para>
///
/// Flow:
///   Init → Gather Context (llm-call) → Panel Review (llm-call x4)
///   → Extract Panel Result + Status → Panel Usable?
///       ├─ True (ok/partial)  → PO Decision (llm-call) → Apply Labels → Finish
///       └─ False (failed)     → Mark Skipped (no labels)              → Finish
///
/// Inputs: repository, itemJson, tenantId
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
        var tenantId = builder.WithVariable<string>("TenantId", "");
        var contextJson = builder.WithVariable<string>("ContextJson", "");
        var panelResultJson = builder.WithVariable<string>("PanelResultJson", "");
        // Panel health signal from the (fail-closed) panel sub-workflow:
        // "ok" / "partial" => usable; "failed" => below quorum, do NOT apply labels.
        var panelStatus = builder.WithVariable<string>(
            "PanelStatus", TriagePanelAggregationHelper.StatusFailed);
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
                tenantId.Set(ctx, ctx.GetInput<string>("tenantId") ?? "");
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
                ["tenantId"] = tenantId.Get(ctx),
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

                // Read the panel-health signal first. Absence is treated as a
                // FAILED panel (fail-closed): if the sub-workflow did not report
                // a status we must NOT assume success and apply labels.
                var status = TriagePanelAggregationHelper.StatusFailed;
                if (result != null && result.TryGetValue("panelStatus", out var st))
                {
                    var s = st?.ToString();
                    if (!string.IsNullOrWhiteSpace(s)) status = s!;
                }
                panelStatus.Set(ctx, status);

                if (result != null && result.TryGetValue("panelResultJson", out var p))
                    return (object)(p?.ToString() ?? "");
                return (object)"";
            })
        };
        extractPanelResult.SetDisplayText("Extract Panel Result");

        // ================================================================
        // 3a. Panel Usable? — honour the panel's fail-closed signal. A "failed"
        //     panel (below quorum) routes to a non-applying terminal; "ok" /
        //     "partial" proceed to the PO decision + label application.
        // ================================================================
        var panelUsable = new FlowDecision(ctx =>
            panelStatus.Get(ctx) != TriagePanelAggregationHelper.StatusFailed)
        { Id = "PanelUsable", Name = "Panel Usable?" };
        panelUsable.SetDisplayText("Panel Usable?");

        // Failed-panel terminal — record the skip on the workflow outputs so the
        // caller/audit sees a LOUD non-applying outcome (no labels applied off a
        // wholly-failed panel). The panel sub-workflow already emitted
        // TRIAGE.PANEL.FAILED to the durable drain.
        var markSkipped = new SetOutput
        {
            Id = "MarkSkipped",
            Name = "Mark Triage Skipped",
            OutputName = new("triageSkipped"),
            OutputValue = new(_ => (object)true),
        };
        markSkipped.SetDisplayText("Mark Triage Skipped");

        var outSkipReason = new SetOutput
        {
            Id = "OutSkipReason",
            Name = "Output Skip Reason",
            OutputName = new("skipReason"),
            OutputValue = new(_ => (object)"panel-failed"),
        };
        outSkipReason.SetDisplayText("Output Skip Reason");

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
                panelUsable,
                poDecision, extractDecision,
                applyLabels,
                markSkipped, outSkipReason,
                finish,
            },
            Connections =
            {
                Connect(init, gatherContext),
                Connect(gatherContext, extractContext),
                Connect(extractContext, panelReview),
                Connect(panelReview, extractPanelResult),
                Connect(extractPanelResult, panelUsable),

                // Usable (ok/partial) → PO decision → apply labels → finish.
                ConnectOutcome(panelUsable, "True", poDecision),
                Connect(poDecision, extractDecision),
                Connect(extractDecision, applyLabels),
                Connect(applyLabels, finish),

                // Failed (below quorum) → mark skipped (NO labels) → finish.
                // The False edge never falls through to the apply-labels path.
                ConnectOutcome(panelUsable, "False", markSkipped),
                Connect(markSkipped, outSkipReason),
                Connect(outSkipReason, finish),
            }
        };
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));
}
