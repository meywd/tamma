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
/// <para>Build-out (completeness audit 2026-06-22, <c>TriageContextGathering.md</c>):
/// the context-gathering sub-workflow is likewise fail-closed and now reports a
/// <c>contextStatus</c> (<c>ok</c>/<c>empty</c>/<c>failed</c>) and accepts a
/// <c>tenantId</c>. This cycle forwards <c>tenantId</c> into it and <b>honours</b>
/// the <c>failed</c> signal: when NO context could be gathered (the LLM scan
/// failed), the panel is NOT run over phantom context — the cycle routes to the
/// same loud non-applying terminal. (<c>empty</c>/<c>ok</c> still run the panel;
/// an empty-but-successful scan is a degraded, not failed, result.)</para>
///
/// Flow:
///   Init → Gather Context (llm-call) → Extract Context + Status → Context Gathered?
///       ├─ True (ok/empty)    → Panel Review (llm-call x4)
///       │     → Extract Panel Result + Status → Panel Usable?
///       │         ├─ True (ok/partial)  → PO Decision → Apply Labels → Finish
///       │         └─ False (failed)     → Mark Skipped (no labels)    → Finish
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
        // Context health signal from the (fail-closed) context sub-workflow:
        // "ok" / "empty" => usable; "failed" => no context gathered, skip the panel.
        var contextStatus = builder.WithVariable<string>(
            "ContextStatus", TriageContextEvents.StatusFailed);
        var panelResultJson = builder.WithVariable<string>("PanelResultJson", "");
        // Panel health signal from the (fail-closed) panel sub-workflow:
        // "ok" / "partial" => usable; "failed" => below quorum, do NOT apply labels.
        var panelStatus = builder.WithVariable<string>(
            "PanelStatus", TriagePanelAggregationHelper.StatusFailed);
        var poDecisionJson = builder.WithVariable<string>("PODecisionJson", "");
        // Why the cycle skipped label application — "context-failed" (no context
        // gathered) or "panel-failed" (panel below quorum). Set by whichever gate
        // tripped, surfaced on the workflow output for the caller / audit.
        var skipReason = builder.WithVariable<string>("SkipReason", "");
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
                ["tenantId"] = tenantId.Get(ctx),
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

                // Read the context-health signal first. Absence is treated as a
                // FAILED scan (fail-closed): if the sub-workflow did not report a
                // status we must NOT assume context was gathered and run the panel.
                var status = TriageContextEvents.StatusFailed;
                if (result != null && result.TryGetValue("contextStatus", out var st))
                {
                    var s = st?.ToString();
                    if (!string.IsNullOrWhiteSpace(s)) status = s!;
                }
                contextStatus.Set(ctx, status);

                if (result != null && result.TryGetValue("contextJson", out var c))
                    return (object)(c?.ToString() ?? "");
                return (object)"";
            })
        };
        extractContext.SetDisplayText("Extract Context");

        // ================================================================
        // 2a. Context Gathered? — honour the context stage's fail-closed signal.
        //     A "failed" scan (no context gathered) routes to the same non-applying
        //     terminal as a failed panel; "ok"/"empty" proceed to the panel review.
        // ================================================================
        var contextGathered = new FlowDecision(ctx =>
            contextStatus.Get(ctx) != TriageContextEvents.StatusFailed)
        { Id = "ContextGathered", Name = "Context Gathered?" };
        contextGathered.SetDisplayText("Context Gathered?");

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

        // Shared non-applying terminal — record the skip on the workflow outputs so
        // the caller/audit sees a LOUD non-applying outcome (no labels applied off a
        // failed context scan or a wholly-failed panel). The relevant sub-workflow
        // already emitted TRIAGE.CONTEXT.FAILED / TRIAGE.PANEL.FAILED to the durable
        // drain; the skipReason variable says which stage tripped.
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
            OutputValue = new(ctx => (object)skipReason.Get(ctx)),
        };
        outSkipReason.SetDisplayText("Output Skip Reason");

        // Set the skip reason on the context-failed branch (before the shared
        // terminal). A dedicated SetVariable keeps the reason explicit + testable.
        var setContextFailedReason = new SetVariable
        {
            Id = "SetContextFailedReason",
            Name = "Set Context-Failed Reason",
            Variable = skipReason,
            Value = new Input<object?>(_ => (object)"context-failed"),
        };
        setContextFailedReason.SetDisplayText("Set Context-Failed Reason");

        // Set the skip reason on the panel-failed branch.
        var setPanelFailedReason = new SetVariable
        {
            Id = "SetPanelFailedReason",
            Name = "Set Panel-Failed Reason",
            Variable = skipReason,
            Value = new Input<object?>(_ => (object)"panel-failed"),
        };
        setPanelFailedReason.SetDisplayText("Set Panel-Failed Reason");

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
                contextGathered,
                panelReview, extractPanelResult,
                panelUsable,
                poDecision, extractDecision,
                applyLabels,
                setContextFailedReason, setPanelFailedReason,
                markSkipped, outSkipReason,
                finish,
            },
            Connections =
            {
                Connect(init, gatherContext),
                Connect(gatherContext, extractContext),
                Connect(extractContext, contextGathered),

                // Context gathered (ok/empty) → run the panel.
                ConnectOutcome(contextGathered, "True", panelReview),
                Connect(panelReview, extractPanelResult),
                Connect(extractPanelResult, panelUsable),

                // Context failed (no context gathered) → skip the panel + labels.
                // The False edge never falls through to the panel-review path.
                ConnectOutcome(contextGathered, "False", setContextFailedReason),
                Connect(setContextFailedReason, markSkipped),

                // Usable (ok/partial) → PO decision → apply labels → finish.
                ConnectOutcome(panelUsable, "True", poDecision),
                Connect(poDecision, extractDecision),
                Connect(extractDecision, applyLabels),
                Connect(applyLabels, finish),

                // Failed (below quorum) → mark skipped (NO labels) → finish.
                // The False edge never falls through to the apply-labels path.
                ConnectOutcome(panelUsable, "False", setPanelFailedReason),
                Connect(setPanelFailedReason, markSkipped),
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
