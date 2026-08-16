using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.IncidentStrategies;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using Tamma.Activities.ADL;
using Tamma.Activities.Documents;
using Tamma.Core.Documents.Resume;
using Tamma.ElsaServer.Workflows.Helpers;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Story 39-15 (D5/D8) — Triage Item Cycle, still a NON-binding ORCHESTRATOR over the
/// migrated triage bindings. It dispatches the <c>triage-context-gathering</c> Findings
/// binding and the <c>triage-po-decision</c> TriageDecision binding (whose REVIEW is now
/// the 39-7 panel INSIDE the lifecycle — the 4-role panel is no longer a separate input
/// stage), routes on the TYPED decision, and applies validated labels + a rendered comment.
///
/// <para>What changed (Story 39-15): the <c>triage-panel-review</c> dispatch +
/// <c>extractPanelResult</c> + <c>panelUsable</c> nodes are DELETED (the panel is the
/// lifecycle REVIEW stage now). The decision gate deserializes the TYPED
/// <see cref="Tamma.Core.Documents.Types.TriageDecision"/>
/// (<see cref="TriageItemCycleHelper.ReadTypedDecision"/>) instead of parsing a bare JSON
/// blob; <c>findingsDocumentId</c> threads from the context binding into the po-decision
/// dispatch as the lineage anchor. The cycle declares
/// <c>[ResumeBehavior(LatestStateReEntry)]</c> with a
/// <see cref="ComputeReEntryPositionActivity"/> gate on the item's <c>triage-decision</c>
/// document: a crash re-entry AFTER the decision was already accepted short-circuits to a
/// single idempotent <c>TRIAGE.ISSUE.COMPLETED</c> terminal (no re-produce, no duplicate
/// apply — D8). <c>ContinueWithIncidentsStrategy</c>, the seeded fail-closed
/// <c>itemResult</c>, <c>ValidateLabels</c>/<c>RenderComment</c>, and the
/// <see cref="ApplyTriageResultActivity"/> Success/Failure routing are all preserved.</para>
/// </summary>
[ResumeBehavior(ResumeMode.LatestStateReEntry)]
public class TriageItemCycleWorkflow : WorkflowBase
{
    private const string TriageDecisionDocumentType = "triage-decision";

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Triage Item Cycle";
        builder.DefinitionId = "triage-item-cycle";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Process one untriaged item: context → PO decision (panel inside) → labels (fail-closed, audited, resumable)";

        // Continue-with-incidents so an unexpected fault does not halt the instance with no
        // output; the apply step routes its failure as an explicit Failure OUTCOME.
        builder.WorkflowOptions.IncidentStrategyType = typeof(ContinueWithIncidentsStrategy);

        // ================================================================
        // Variables
        // ================================================================
        var repository = builder.WithVariable<string>("Repository", "").Persisted();
        var itemJson = builder.WithVariable<string>("ItemJson", "").Persisted();
        var tenantId = builder.WithVariable<string>("TenantId", "").Persisted();
        var itemKey = builder.WithVariable<string>("ItemKey", "").Persisted();
        var itemNumber = builder.WithVariable<int>("ItemNumber", 0).Persisted();
        var itemSource = builder.WithVariable<string>("ItemSource", "").Persisted();

        // 39-10 re-entry position (on the item's triage-decision document).
        var reEntryPositionJson = builder.WithVariable<string>().Persisted();
        var reEntryDocJson = builder.WithVariable<string>().Persisted();
        var positionStage = builder.WithVariable<string>("PositionStage", "produce").Persisted();

        var contextJson = builder.WithVariable<string>("ContextJson", "").Persisted();
        var contextStatus = builder.WithVariable<string>("ContextStatus", TriageContextEvents.StatusFailed).Persisted();
        var findingsDocumentId = builder.WithVariable<string>("FindingsDocumentId", "").Persisted();

        var poDecisionJson = builder.WithVariable<string>("PODecisionJson", "").Persisted();
        var poDocumentJson = builder.WithVariable<string>("PODocumentJson", "").Persisted();
        var poCallSucceeded = builder.WithVariable<bool>("PoCallSucceeded", false).Persisted();
        var decisionStatus = builder.WithVariable<string>("DecisionStatus", "").Persisted();
        var decisionType = builder.WithVariable<string>("DecisionType", "").Persisted();
        var decisionPriority = builder.WithVariable<string>("DecisionPriority", "").Persisted();
        var decisionAutomation = builder.WithVariable<string>("DecisionAutomation", "").Persisted();
        var appliedLabels = builder.WithVariable<string[]>("AppliedLabels", System.Array.Empty<string>()).Persisted();
        var appliedComment = builder.WithVariable<string>("AppliedComment", "").Persisted();
        var droppedLabels = builder.WithVariable<string[]>("DroppedLabels", System.Array.Empty<string>()).Persisted();

        var skipReason = builder.WithVariable<string>("SkipReason", "").Persisted();
        var subResult = builder.WithVariable<IDictionary<string, object>?>().Persisted();

        // ================================================================
        // 1. Init — read inputs; derive itemKey/itemNumber/itemSource.
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
                tenantId.Set(ctx, ctx.GetInput<string>("tenantId") ?? "");
                itemKey.Set(ctx, TriageItemCycleHelper.DeriveItemKey(repo, item));
                itemNumber.Set(ctx, TriageBindingHelper.ParseItemNumber(item));
                itemSource.Set(ctx, TriageItemCycleHelper.ReadItemSource(item));
                return (object)repo;
            })
        };
        init.SetDisplayText("Initialize");

        // ================================================================
        // 1a. 39-10 re-entry position on the item's triage-decision document (D8).
        // ================================================================
        var computeReEntry = new ComputeReEntryPositionActivity
        {
            Id = "ComputeReEntryPosition", Name = "Compute Re-Entry Position",
            IssueId = new(ctx => itemKey.Get(ctx)),
            DocumentType = new(TriageDecisionDocumentType),
            TenantId = new(ctx => tenantId.Get(ctx)),
            CorrelationId = new(ctx => itemKey.Get(ctx)),
            PositionJson = new(reEntryPositionJson),
            ExistingDocumentJson = new(reEntryDocJson),
        };
        computeReEntry.SetDisplayText("Compute Re-Entry Position");

        var readPositionStage = new SetVariable
        {
            Id = "ReadPositionStage", Name = "Read Position Stage",
            Variable = positionStage,
            Value = new(ctx =>
            {
                var position = DocumentLifecycleHelper.DeserializeReEntryPosition(reEntryPositionJson.Get(ctx));
                return position?.ResumeAt switch
                {
                    LifecycleResumeStage.Complete => "complete",
                    LifecycleResumeStage.Accept => "accept",
                    LifecycleResumeStage.Review => "review",
                    _ => "produce",
                };
            })
        };
        readPositionStage.SetDisplayText("Read Position Stage");

        // Apply-idempotence gate (D8): a crash re-entry AFTER the decision was already
        // accepted (position "complete") short-circuits to a single idempotent COMPLETED
        // terminal — no re-dispatch of context/po, no duplicate ApplyTriageResultActivity.
        var alreadyComplete = new FlowDecision(ctx => positionStage.Get(ctx) == "complete")
        { Id = "AlreadyComplete", Name = "Already Complete?" };
        alreadyComplete.SetDisplayText("Already Complete?");

        // ================================================================
        // 1b. Emit TRIAGE.ISSUE.STARTED.
        // ================================================================
        var emitStarted = CycleEvent(
            "EmitCycleStarted", "Emit TRIAGE.ISSUE.STARTED",
            _ => TriageCycleEvents.Started,
            repository, itemKey, itemNumber, tenantId, itemSource,
            _ => "", _ => "", _ => "", _ => "", _ => "");

        // ================================================================
        // 2. Gather Context (Findings binding).
        // ================================================================
        var gatherContext = new DispatchWorkflow
        {
            Id = "GatherTriageContext", Name = "Gather Triage Context",
            WorkflowDefinitionId = new("triage-context-gathering"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["itemJson"] = itemJson.Get(ctx),
                ["tenantId"] = tenantId.Get(ctx),
                ["issueId"] = itemKey.Get(ctx),
            }),
            WaitForCompletion = new(true),
            Result = new(subResult),
        };
        gatherContext.SetDisplayText("Gather Triage Context");

        var extractContext = new SetVariable
        {
            Id = "ExtractContext", Name = "Extract Context",
            Variable = contextJson,
            Value = new Input<object?>(ctx =>
            {
                var result = subResult.Get(ctx);

                var status = TriageContextEvents.StatusFailed;
                if (result != null && result.TryGetValue("contextStatus", out var st))
                {
                    var s = st?.ToString();
                    if (!string.IsNullOrWhiteSpace(s)) status = s!;
                }
                contextStatus.Set(ctx, status);

                if (result != null && result.TryGetValue("findingsDocumentId", out var fd))
                    findingsDocumentId.Set(ctx, fd?.ToString() ?? "");

                if (result != null && result.TryGetValue("contextJson", out var c))
                    return (object)(c?.ToString() ?? "");
                return (object)"";
            })
        };
        extractContext.SetDisplayText("Extract Context");

        var contextGathered = new FlowDecision(ctx =>
            contextStatus.Get(ctx) != TriageContextEvents.StatusFailed)
        { Id = "ContextGathered", Name = "Context Gathered?" };
        contextGathered.SetDisplayText("Context Gathered?");

        // ================================================================
        // 3. PO Decision (TriageDecision binding — panel is now INSIDE the lifecycle).
        // ================================================================
        var poDecision = new DispatchWorkflow
        {
            Id = "PODecision", Name = "PO Decision",
            WorkflowDefinitionId = new("triage-po-decision"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["itemJson"] = itemJson.Get(ctx),
                ["contextJson"] = contextJson.Get(ctx),
                ["findingsDocumentId"] = findingsDocumentId.Get(ctx),
                ["issueId"] = itemKey.Get(ctx),
                ["tenantId"] = tenantId.Get(ctx),
            }),
            WaitForCompletion = new(true),
            Result = new(subResult),
        };
        poDecision.SetDisplayText("PO Decision");

        var extractDecision = new SetVariable
        {
            Id = "ExtractDecision", Name = "Extract Decision",
            Variable = poDecisionJson,
            Value = new Input<object?>(ctx =>
            {
                var result = subResult.Get(ctx);

                var succeeded = result != null
                    && result.TryGetValue("callSucceeded", out var cs) && cs is true;
                poCallSucceeded.Set(ctx, succeeded);

                var json = "";
                if (result != null && result.TryGetValue("decisionJson", out var d))
                    json = d?.ToString() ?? "";
                var docJson = "";
                if (result != null && result.TryGetValue("documentJson", out var dj))
                    docJson = dj?.ToString() ?? "";
                poDocumentJson.Set(ctx, docJson);

                var parsed = TriageItemCycleHelper.ReadTypedDecision(docJson);
                decisionStatus.Set(ctx, parsed.Status);
                decisionType.Set(ctx, parsed.Type);
                decisionPriority.Set(ctx, parsed.Priority);
                decisionAutomation.Set(ctx, parsed.Automation);

                return (object)json;
            })
        };
        extractDecision.SetDisplayText("Extract Decision");

        // Typed-exit gate — applicable ONLY when the po binding accepted AND the typed
        // TriageDecision is complete. A non-accept exit (callSucceeded=false / empty
        // documentJson) → NOT applicable → fail the item, never label off a fabricated decision.
        var decisionOk = new FlowDecision(ctx =>
            TriageItemCycleHelper.IsDecisionApplicable(
                poCallSucceeded.Get(ctx),
                TriageItemCycleHelper.ReadTypedDecision(poDocumentJson.Get(ctx))))
        { Id = "DecisionOK", Name = "Decision OK?" };
        decisionOk.SetDisplayText("Decision OK?");

        var buildApplyInputs = new SetVariable
        {
            Id = "BuildApplyInputs", Name = "Build Apply Inputs",
            Variable = appliedLabels,
            Value = new Input<object?>(ctx =>
            {
                var decision = TriageItemCycleHelper.ReadTypedDecision(poDocumentJson.Get(ctx));
                var validated = TriageItemCycleHelper.ValidateLabels(decision.Labels, out var dropped);
                droppedLabels.Set(ctx, dropped.ToArray());
                appliedComment.Set(ctx, TriageItemCycleHelper.RenderComment(decision));
                return (object)validated.ToArray();
            })
        };
        buildApplyInputs.SetDisplayText("Build Apply Inputs");

        var emitLabelsInvalid = CycleEvent(
            "EmitLabelsInvalid", "Emit TRIAGE.LABELS.INVALID",
            _ => TriageCycleEvents.LabelsInvalid,
            repository, itemKey, itemNumber, tenantId, itemSource,
            _ => "", _ => "", _ => "", ctx => decisionStatus.Get(ctx),
            ctx => string.Join(",", droppedLabels.Get(ctx) ?? System.Array.Empty<string>()));

        var seedFailedReason = new SetVariable
        {
            Id = "SeedFailedReason", Name = "Seed Fail-Closed Reason",
            Variable = skipReason,
            Value = new Input<object?>(_ => (object)"applyIncomplete"),
        };
        seedFailedReason.SetDisplayText("Seed Fail-Closed Reason");

        var seedFailedResult = new SetOutput
        {
            Id = "SeedFailedResult", Name = "Seed Item Result (failed)",
            OutputName = new("itemResult"),
            OutputValue = new(ctx => (object)TriageItemCycleHelper.BuildItemResult(
                itemKey.Get(ctx) ?? "", TriageCycleEvents.OutcomeFailed, decisionStatus.Get(ctx),
                "applyIncomplete")),
        };
        seedFailedResult.SetDisplayText("Seed Item Result (failed)");

        // ================================================================
        // 4. Apply Labels + Post Comment.
        // ================================================================
        var applyLabels = new ApplyTriageResultActivity
        {
            Id = "ApplyLabels", Name = "Apply Labels & Comment",
            Repository = new Input<string>(ctx => repository.Get(ctx)),
            ItemJson = new Input<string>(ctx => itemJson.Get(ctx)),
            DecisionJson = new Input<string>(ctx => poDecisionJson.Get(ctx)),
            LabelsOverride = new Input<ICollection<string>?>(ctx => appliedLabels.Get(ctx)),
            CommentOverride = new Input<string?>(ctx => appliedComment.Get(ctx)),
        };
        applyLabels.SetDisplayText("Apply Labels & Comment");

        var emitCompleted = CycleEvent(
            "EmitCycleCompleted", "Emit TRIAGE.ISSUE.COMPLETED",
            _ => TriageCycleEvents.Completed,
            repository, itemKey, itemNumber, tenantId, itemSource,
            ctx => decisionType.Get(ctx), ctx => decisionPriority.Get(ctx),
            ctx => decisionAutomation.Get(ctx), ctx => decisionStatus.Get(ctx), _ => "");

        var outCompletedResult = new SetOutput
        {
            Id = "OutCompletedResult", Name = "Output Item Result (triaged)",
            OutputName = new("itemResult"),
            OutputValue = new(ctx => (object)TriageItemCycleHelper.BuildItemResult(
                itemKey.Get(ctx) ?? "", TriageCycleEvents.OutcomeTriaged, decisionStatus.Get(ctx), null)),
        };
        outCompletedResult.SetDisplayText("Output Item Result (triaged)");

        // Idempotent re-entry COMPLETED terminal (D8) — reached only from the
        // already-complete gate; emits exactly one TRIAGE.ISSUE.COMPLETED with no re-apply.
        var emitCompletedReentry = CycleEvent(
            "EmitCycleCompletedReentry", "Emit TRIAGE.ISSUE.COMPLETED (re-entry)",
            _ => TriageCycleEvents.Completed,
            repository, itemKey, itemNumber, tenantId, itemSource,
            _ => "", _ => "", _ => "", _ => TriagePoDecisionHelper.StatusOk, _ => "");

        var outReentryResult = new SetOutput
        {
            Id = "OutReentryResult", Name = "Output Item Result (re-entry)",
            OutputName = new("itemResult"),
            OutputValue = new(ctx => (object)TriageItemCycleHelper.BuildItemResult(
                itemKey.Get(ctx) ?? "", TriageCycleEvents.OutcomeTriaged, TriagePoDecisionHelper.StatusOk, null)),
        };
        outReentryResult.SetDisplayText("Output Item Result (re-entry)");

        var setApplyFailedReason = new SetVariable
        {
            Id = "SetApplyFailedReason", Name = "Set Apply-Failed Reason",
            Variable = skipReason,
            Value = new Input<object?>(_ => (object)"applyFailed"),
        };
        setApplyFailedReason.SetDisplayText("Set Apply-Failed Reason");

        var emitApplyFailed = CycleEvent(
            "EmitCycleApplyFailed", "Emit TRIAGE.ISSUE.FAILED (apply)",
            _ => TriageCycleEvents.Failed,
            repository, itemKey, itemNumber, tenantId, itemSource,
            _ => "", _ => "", _ => "", ctx => decisionStatus.Get(ctx),
            ctx => skipReason.Get(ctx));

        var outApplyFailedResult = new SetOutput
        {
            Id = "OutApplyFailedResult", Name = "Output Item Result (apply failed)",
            OutputName = new("itemResult"),
            OutputValue = new(ctx => (object)TriageItemCycleHelper.BuildItemResult(
                itemKey.Get(ctx) ?? "", TriageCycleEvents.OutcomeFailed, decisionStatus.Get(ctx),
                skipReason.Get(ctx))),
        };
        outApplyFailedResult.SetDisplayText("Output Item Result (apply failed)");

        // ================================================================
        // Skip / fail terminals.
        // ================================================================
        var setContextFailedReason = new SetVariable
        {
            Id = "SetContextFailedReason", Name = "Set Context-Failed Reason",
            Variable = skipReason,
            Value = new Input<object?>(_ => (object)"context-failed"),
        };
        setContextFailedReason.SetDisplayText("Set Context-Failed Reason");

        var markSkipped = new SetOutput
        {
            Id = "MarkSkipped", Name = "Mark Triage Skipped",
            OutputName = new("triageSkipped"),
            OutputValue = new(_ => (object)true),
        };
        markSkipped.SetDisplayText("Mark Triage Skipped");

        var outSkipReason = new SetOutput
        {
            Id = "OutSkipReason", Name = "Output Skip Reason",
            OutputName = new("skipReason"),
            OutputValue = new(ctx => (object)skipReason.Get(ctx)),
        };
        outSkipReason.SetDisplayText("Output Skip Reason");

        var emitSkipped = CycleEvent(
            "EmitCycleSkipped", "Emit TRIAGE.ISSUE.SKIPPED",
            _ => TriageCycleEvents.Skipped,
            repository, itemKey, itemNumber, tenantId, itemSource,
            _ => "", _ => "", _ => "", ctx => decisionStatus.Get(ctx),
            ctx => skipReason.Get(ctx));

        var outSkippedResult = new SetOutput
        {
            Id = "OutSkippedResult", Name = "Output Item Result (skipped)",
            OutputName = new("itemResult"),
            OutputValue = new(ctx => (object)TriageItemCycleHelper.BuildItemResult(
                itemKey.Get(ctx) ?? "", TriageCycleEvents.OutcomeSkipped, decisionStatus.Get(ctx),
                skipReason.Get(ctx))),
        };
        outSkippedResult.SetDisplayText("Output Item Result (skipped)");

        var setDecisionFailedReason = new SetVariable
        {
            Id = "SetDecisionFailedReason", Name = "Set Decision-Failed Reason",
            Variable = skipReason,
            Value = new Input<object?>(ctx =>
            {
                var status = decisionStatus.Get(ctx);
                return (object)(string.IsNullOrWhiteSpace(status)
                    ? "decisionUnusable"
                    : $"decisionUnusable:{status}");
            }),
        };
        setDecisionFailedReason.SetDisplayText("Set Decision-Failed Reason");

        var emitFailed = CycleEvent(
            "EmitCycleFailed", "Emit TRIAGE.ISSUE.FAILED",
            _ => TriageCycleEvents.Failed,
            repository, itemKey, itemNumber, tenantId, itemSource,
            _ => "", _ => "", _ => "", ctx => decisionStatus.Get(ctx),
            ctx => skipReason.Get(ctx));

        var outFailedResult = new SetOutput
        {
            Id = "OutFailedResult", Name = "Output Item Result (failed)",
            OutputName = new("itemResult"),
            OutputValue = new(ctx => (object)TriageItemCycleHelper.BuildItemResult(
                itemKey.Get(ctx) ?? "", TriageCycleEvents.OutcomeFailed, decisionStatus.Get(ctx),
                skipReason.Get(ctx))),
        };
        outFailedResult.SetDisplayText("Output Item Result (failed)");

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
                init, computeReEntry, readPositionStage, alreadyComplete,
                emitCompletedReentry, outReentryResult,
                emitStarted,
                gatherContext, extractContext, contextGathered,
                poDecision, extractDecision, decisionOk, buildApplyInputs,
                emitLabelsInvalid, seedFailedReason, seedFailedResult,
                applyLabels, emitCompleted, outCompletedResult,
                setApplyFailedReason, emitApplyFailed, outApplyFailedResult,
                setContextFailedReason,
                markSkipped, outSkipReason, emitSkipped, outSkippedResult,
                setDecisionFailedReason, emitFailed, outFailedResult,
                finish,
            },
            Connections =
            {
                Connect(init, computeReEntry),
                Connect(computeReEntry, readPositionStage),
                Connect(readPositionStage, alreadyComplete),

                // Re-entry after accept → single idempotent COMPLETED, no re-apply (D8).
                ConnectOutcome(alreadyComplete, "True", emitCompletedReentry),
                Connect(emitCompletedReentry, outReentryResult),
                Connect(outReentryResult, finish),

                // Fresh / mid-flow re-entry → the normal cycle.
                ConnectOutcome(alreadyComplete, "False", emitStarted),
                Connect(emitStarted, gatherContext),
                Connect(gatherContext, extractContext),
                Connect(extractContext, contextGathered),

                // Context gathered (ok) → PO decision (panel inside).
                ConnectOutcome(contextGathered, "True", poDecision),
                Connect(poDecision, extractDecision),
                Connect(extractDecision, decisionOk),

                // Context failed → SKIPPED.
                ConnectOutcome(contextGathered, "False", setContextFailedReason),
                Connect(setContextFailedReason, markSkipped),

                // Decision OK → validate labels + render comment → seed → apply.
                ConnectOutcome(decisionOk, "True", buildApplyInputs),
                Connect(buildApplyInputs, emitLabelsInvalid),
                Connect(emitLabelsInvalid, seedFailedReason),
                Connect(seedFailedReason, seedFailedResult),
                Connect(seedFailedResult, applyLabels),

                ConnectOutcome(applyLabels, "Success", emitCompleted),
                Connect(emitCompleted, outCompletedResult),
                Connect(outCompletedResult, finish),

                ConnectOutcome(applyLabels, "Failure", setApplyFailedReason),
                Connect(setApplyFailedReason, emitApplyFailed),
                Connect(emitApplyFailed, outApplyFailedResult),
                Connect(outApplyFailedResult, finish),

                // Decision NOT OK → FAILED.
                ConnectOutcome(decisionOk, "False", setDecisionFailedReason),
                Connect(setDecisionFailedReason, emitFailed),
                Connect(emitFailed, outFailedResult),
                Connect(outFailedResult, finish),

                // Shared SKIPPED terminal.
                Connect(markSkipped, outSkipReason),
                Connect(outSkipReason, emitSkipped),
                Connect(emitSkipped, outSkippedResult),
                Connect(outSkippedResult, finish),
            }
        };
    }

    private static EmitTriageCycleEventActivity CycleEvent(
        string id, string label,
        System.Func<Elsa.Expressions.Models.ExpressionExecutionContext, string> eventType,
        Variable<string> repository, Variable<string> itemKey, Variable<int> itemNumber,
        Variable<string> tenantId, Variable<string> itemSource,
        System.Func<Elsa.Expressions.Models.ExpressionExecutionContext, string> type,
        System.Func<Elsa.Expressions.Models.ExpressionExecutionContext, string> priority,
        System.Func<Elsa.Expressions.Models.ExpressionExecutionContext, string> automation,
        System.Func<Elsa.Expressions.Models.ExpressionExecutionContext, string> decisionStatus,
        System.Func<Elsa.Expressions.Models.ExpressionExecutionContext, string> reason)
    {
        var emit = new EmitTriageCycleEventActivity
        {
            Id = id, Name = label,
            EventType = new Input<string>(eventType),
            Repository = new Input<string>(ctx => repository.Get(ctx)),
            ItemKey = new Input<string?>(ctx => itemKey.Get(ctx)),
            ItemNumber = new Input<int>(ctx => itemNumber.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantId.Get(ctx)),
            ItemSource = new Input<string?>(ctx => itemSource.Get(ctx)),
            Type = new Input<string?>(ctx => type(ctx)),
            Priority = new Input<string?>(ctx => priority(ctx)),
            Automation = new Input<string?>(ctx => automation(ctx)),
            DecisionStatus = new Input<string?>(ctx => decisionStatus(ctx)),
            Reason = new Input<string?>(ctx => reason(ctx)),
        };
        emit.SetDisplayText(label);
        return emit;
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));
}
