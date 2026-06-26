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
using Tamma.ElsaServer.Workflows.Helpers;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Triage Item Cycle — processes a single untriaged item through
/// context gathering, 4-role panel review, PO decision, and label application.
///
/// <para>Build-out (completeness audit 2026-06-22, <c>TriageItemCycle.md</c>): the
/// orchestrator layer is now <b>fail-closed and audited</b> — it is no longer a
/// happy-path spine over robust sub-workflows. Specifically:</para>
///
/// <list type="bullet">
///   <item><description>#3 — cycle-scoped DCB events. <c>TRIAGE.ISSUE.STARTED</c> at
///     Init and exactly one terminal (<c>COMPLETED</c> / <c>SKIPPED</c> /
///     <c>FAILED</c>) at each exit, tagged itemKey/issueId/repository/itemSource/type/
///     priority/automation (via <see cref="EmitTriageCycleEventActivity"/>). The unit
///     of audit ("we triaged item X → outcome Y") is now a single loud row.</description></item>
///   <item><description>#1/#2 — a decision-OK gate before apply. The PO sub-workflow now
///     outputs <c>callSucceeded</c> + a <c>status</c> (#391). A faulted PO dispatch (no
///     <c>callSucceeded</c> output → fail-closed false), an <c>llm-failed</c> /
///     <c>unparsed</c> / <c>skipped</c> decision, or a missing/empty decision SKIPS
///     apply and FAILS the item — never labels off a fabricated/empty decision.</description></item>
///   <item><description>#2 — failure edges on the three <c>DispatchWorkflow</c> nodes.
///     <c>DispatchWorkflow</c> has NO <c>Faulted</c> outcome port; a faulted child
///     reaches <c>Finished</c> (so the parent bookmark resumes — never a hang) but its
///     output dict lacks the success-signal keys. The cycle therefore fail-closes on the
///     ABSENCE of those keys: context → <c>contextStatus</c> gate, panel →
///     <c>panelStatus</c> gate, PO → <c>callSucceeded</c>/<c>status</c> gate. A faulted
///     stage routes to a loud non-applying terminal, never proceeds with empty JSON.</description></item>
///   <item><description>#5 — a per-item <c>itemResult</c> output
///     (<c>{ itemKey, outcome, decisionStatus, error? }</c>) so the fire-and-forget
///     parent can report <c>{ triaged, failed, skipped }</c> rather than a blanket
///     success.</description></item>
///   <item><description>#7 — labels are validated against the canonical vocabulary and
///     the comment is rendered deterministically from the parsed decision (an AC5
///     markdown table) before apply — never arbitrary LLM prose/labels.</description></item>
///   <item><description>#8 — <see cref="ApplyTriageResultActivity"/> is fail-loud on a
///     non-success engine-callback POST. It emits a loud <c>TRIAGE.APPLY.RESULT.FAILED</c>
///     leaf event instead of a swallowed false <c>.COMPLETED</c>, and exposes a
///     <c>Success</c> / <c>Failure</c> outcome so the cycle routes an apply failure to a
///     LOUD <c>TRIAGE.ISSUE.FAILED</c> terminal (review fix below) rather than halting
///     with no terminal.</description></item>
/// </list>
///
/// <para>Review fix (CRITICAL — "stuck, no terminal" hang). Previously the apply node
/// <i>threw</i> on a 4xx/5xx and had only a success-only edge
/// (<c>applyLabels → emitCompleted</c>) with the DEFAULT incident strategy. A Flowchart
/// never schedules a <i>faulted</i> node's outbound edges (Elsa 3.5
/// <c>Flowchart.ProcessChildCompletedAsync</c> only routes a <c>Completed</c> child), and
/// the default <c>FaultStrategy</c> transitions the whole instance to <c>Faulted</c> — so
/// an apply HTTP failure halted the instance with NO <c>TRIAGE.ISSUE.*</c> terminal and
/// no <c>itemResult</c>. Mirroring the merge-approval / deployment-pipeline precedent:
/// (a) <c>WorkflowOptions.IncidentStrategyType = ContinueWithIncidentsStrategy</c> so an
/// unexpected fault does not hard-halt; (b) the <c>itemResult</c> output + fail-reason are
/// SEEDED to a fail-closed <c>failed</c> default BEFORE apply, so even a halt yields a
/// parseable failed outcome; and (c) the apply node's <c>Failure</c> outcome routes to a
/// loud <c>TRIAGE.ISSUE.FAILED</c> terminal that overwrites the <c>itemResult</c>. The
/// <c>Success</c> outcome routes to <c>COMPLETED</c>. Exactly ONE cycle terminal event
/// fires per run — including the apply-fault path.</para>
///
/// <para>#9 — singleton: the previous header claimed "singleton — Elsa queues
/// subsequent dispatches until the current one finishes". That is FALSE for Elsa's
/// <c>SingletonStrategy</c>, which <i>rejects</i> (does not queue) a new dispatch while
/// one is running — enforcing it would silently DROP items from the parent's per-item
/// fan-out. The claim is therefore dropped; correctness does not rely on serialization.
/// A real dedupe/triage-state gate (#4) is deferred to a follow-on 26-1 sub-story.</para>
///
/// Flow:
///   Init → Emit STARTED → Gather Context (llm-call) → Extract Context + Status → Context Gathered?
///       ├─ True (ok/empty)    → Panel Review (llm-call x4) → Extract Panel + Status → Panel Usable?
///       │     ├─ True (ok/partial)  → PO Decision → Capture (callSucceeded/status/fields)
///       │     │       → Decision OK?
///       │     │           ├─ True (callSucceeded && status=ok) → Seed failed itemResult →
///       │     │           │     Build Apply Inputs (validated labels + rendered comment;
///       │     │           │     emit LABELS.INVALID for any dropped) → Apply
///       │     │           │         ├─ Success → Emit COMPLETED → Finish
///       │     │           │         └─ Failure → Fail Item (reason=applyFailed) → Emit FAILED → Finish
///       │     │           └─ False → Fail Item (reason=decisionUnusable) → Emit FAILED → Finish
///       │     └─ False (failed)     → Mark Skipped (panel-failed)  → Emit SKIPPED → Finish
///       └─ False (failed)     → Mark Skipped (context-failed)      → Emit SKIPPED → Finish
///
/// Inputs: repository, itemJson, tenantId
/// Outputs: itemResult ({ itemKey, outcome, decisionStatus, error? }); triageSkipped/skipReason (back-compat)
/// </summary>
public class TriageItemCycleWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Triage Item Cycle";
        builder.DefinitionId = "triage-item-cycle";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Process one untriaged item: context → panel → PO → labels (fail-closed, audited)";

        // Review fix (CRITICAL — "stuck, no terminal" hang). Continue-with-incidents so
        // an unexpected fault (e.g. in an Extract/Build node) does NOT halt the instance
        // with no output. The apply step routes its failure as an explicit Failure
        // OUTCOME (not a throw), so the loud TRIAGE.ISSUE.FAILED terminal is always
        // reachable; the seeded fail-closed itemResult (below, before apply) covers any
        // other halt. Mirrors MergeApprovalWorkflow I1 / DeploymentPipelineWorkflow.
        builder.WorkflowOptions.IncidentStrategyType = typeof(ContinueWithIncidentsStrategy);

        // ================================================================
        // Variables
        // ================================================================
        var repository = builder.WithVariable<string>("Repository", "");
        var itemJson = builder.WithVariable<string>("ItemJson", "");
        var tenantId = builder.WithVariable<string>("TenantId", "");
        // Deterministic key + tags for events / outcome (#3/#5).
        var itemKey = builder.WithVariable<string>("ItemKey", "");
        var itemNumber = builder.WithVariable<int>("ItemNumber", 0);
        var itemSource = builder.WithVariable<string>("ItemSource", "");

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
        // PO call health (#1/#2): the PO sub-workflow's `callSucceeded` output. Default
        // false — a faulted PO dispatch never sets it, so the cycle fail-closes.
        var poCallSucceeded = builder.WithVariable<bool>("PoCallSucceeded", false);
        // Decision fields surfaced for the COMPLETED event + apply (#3/#7).
        var decisionStatus = builder.WithVariable<string>("DecisionStatus", "");
        var decisionType = builder.WithVariable<string>("DecisionType", "");
        var decisionPriority = builder.WithVariable<string>("DecisionPriority", "");
        var decisionAutomation = builder.WithVariable<string>("DecisionAutomation", "");
        // Validated labels + rendered comment for apply (#7).
        var appliedLabels = builder.WithVariable<string[]>("AppliedLabels", System.Array.Empty<string>());
        var appliedComment = builder.WithVariable<string>("AppliedComment", "");
        // Out-of-vocab labels the PO returned that were dropped by ValidateLabels (#7) —
        // captured (no longer discarded) so the cycle emits a TRIAGE.LABELS.INVALID
        // warning recording what was dropped, rather than silently swallowing it.
        var droppedLabels = builder.WithVariable<string[]>("DroppedLabels", System.Array.Empty<string>());

        // Why the cycle skipped/failed apply — surfaced on the outputs + events.
        var skipReason = builder.WithVariable<string>("SkipReason", "");
        var subResult = builder.WithVariable<IDictionary<string, object>?>();

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
                itemNumber.Set(ctx, TriagePanelAggregationHelper.ParseItemNumber(item));
                itemSource.Set(ctx, TriageItemCycleHelper.ReadItemSource(item));
                return (object)repo;
            })
        };
        init.SetDisplayText("Initialize");

        // ================================================================
        // 1a. Emit TRIAGE.ISSUE.STARTED (#3).
        // ================================================================
        var emitStarted = CycleEvent(
            "EmitCycleStarted", "Emit TRIAGE.ISSUE.STARTED",
            _ => TriageCycleEvents.Started,
            repository, itemKey, itemNumber, tenantId, itemSource,
            _ => "", _ => "", _ => "", _ => "", _ => "");

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
                // FAILED scan (fail-closed): a faulted context dispatch (#2) reaches
                // Finished with no output, so the status key is absent — we must NOT
                // assume context was gathered and run the panel.
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

                // Read the panel-health signal first. Absence is treated as a FAILED
                // panel (fail-closed): a faulted panel dispatch (#2) yields no status
                // key — we must NOT assume success and apply labels.
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
        // 3a. Panel Usable? — honour the panel's fail-closed signal.
        // ================================================================
        var panelUsable = new FlowDecision(ctx =>
            panelStatus.Get(ctx) != TriagePanelAggregationHelper.StatusFailed)
        { Id = "PanelUsable", Name = "Panel Usable?" };
        panelUsable.SetDisplayText("Panel Usable?");

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
                ["tenantId"] = tenantId.Get(ctx),
            }),
            WaitForCompletion = new(true),
            Result = new(subResult),
        };
        poDecision.SetDisplayText("PO Decision");

        // 4a. Capture decision — #1/#2. Read callSucceeded + decisionJson, parse the
        //     status + classification fields. Fail-closed: a faulted PO dispatch
        //     leaves callSucceeded absent (=> false) and decisionJson empty.
        var extractDecision = new SetVariable
        {
            Id = "ExtractDecision",
            Name = "Extract Decision",
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

                var parsed = TriageItemCycleHelper.ParseDecision(json);
                decisionStatus.Set(ctx, parsed.Status);
                decisionType.Set(ctx, parsed.Type);
                decisionPriority.Set(ctx, parsed.Priority);
                decisionAutomation.Set(ctx, parsed.Automation);

                return (object)json;
            })
        };
        extractDecision.SetDisplayText("Extract Decision");

        // 4b. Decision OK? — #1 the apply gate. Applicable ONLY when the PO call
        //     succeeded AND the decision status is "ok". A faulted PO, an llm-failed /
        //     unparsed / skipped decision, or empty JSON → False → fail the item.
        var decisionOk = new FlowDecision(ctx =>
            TriageItemCycleHelper.IsDecisionApplicable(
                poCallSucceeded.Get(ctx),
                TriageItemCycleHelper.ParseDecision(poDecisionJson.Get(ctx))))
        { Id = "DecisionOK", Name = "Decision OK?" };
        decisionOk.SetDisplayText("Decision OK?");

        // 4c. Build apply inputs — #7 validate labels against the canonical vocab and
        //     render the AC5 markdown-table comment deterministically from the decision.
        //     Capture the dropped (out-of-vocab) labels so the cycle can emit a
        //     TRIAGE.LABELS.INVALID warning rather than silently discarding them.
        var buildApplyInputs = new SetVariable
        {
            Id = "BuildApplyInputs",
            Name = "Build Apply Inputs",
            Variable = appliedLabels,
            Value = new Input<object?>(ctx =>
            {
                var decision = TriageItemCycleHelper.ParseDecision(poDecisionJson.Get(ctx));
                var validated = TriageItemCycleHelper.ValidateLabels(decision.Labels, out var dropped);
                droppedLabels.Set(ctx, dropped.ToArray());
                appliedComment.Set(ctx, TriageItemCycleHelper.RenderComment(decision));
                return (object)validated.ToArray();
            })
        };
        buildApplyInputs.SetDisplayText("Build Apply Inputs");

        // 4d. MINOR (#7) — record dropped out-of-vocab labels as a loud (warning) audit
        //     row. Non-terminal: the cycle still applies the validated subset and
        //     proceeds to COMPLETED. Reason carries the dropped set (comma-joined,
        //     secret-free — they are label strings). Emitted unconditionally; the
        //     downstream durable drain persists it, and an empty dropped set carries an
        //     empty reason (the cheap path — no extra branch in the flowchart).
        var emitLabelsInvalid = CycleEvent(
            "EmitLabelsInvalid", "Emit TRIAGE.LABELS.INVALID",
            _ => TriageCycleEvents.LabelsInvalid,
            repository, itemKey, itemNumber, tenantId, itemSource,
            _ => "", _ => "", _ => "", ctx => decisionStatus.Get(ctx),
            ctx => string.Join(",", droppedLabels.Get(ctx) ?? System.Array.Empty<string>()));

        // 4e. Seed a fail-closed itemResult + reason BEFORE apply (review fix). If apply
        //     (or anything after this) faults and continue-with-incidents stops the flow
        //     before a terminal, the workflow output still carries a parseable
        //     itemResult{outcome:"failed"} — never a stuck instance with no output. The
        //     Success / Failure terminals below overwrite this with the real outcome.
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
                itemKey.Get(ctx), TriageCycleEvents.OutcomeFailed, decisionStatus.Get(ctx),
                "applyIncomplete")),
        };
        seedFailedResult.SetDisplayText("Seed Item Result (failed)");

        // ================================================================
        // 5. Apply Labels + Post Comment (#7/#8 — validated labels, rendered
        //    comment, fail-loud on a non-success engine POST). The activity exposes a
        //    Success / Failure outcome so an apply HTTP failure routes to a loud
        //    TRIAGE.ISSUE.FAILED terminal (review fix) instead of faulting the cycle.
        // ================================================================
        var applyLabels = new ApplyTriageResultActivity
        {
            Id = "ApplyLabels",
            Name = "Apply Labels & Comment",
            Repository = new Input<string>(ctx => repository.Get(ctx)),
            ItemJson = new Input<string>(ctx => itemJson.Get(ctx)),
            DecisionJson = new Input<string>(ctx => poDecisionJson.Get(ctx)),
            LabelsOverride = new Input<ICollection<string>?>(ctx => appliedLabels.Get(ctx)),
            CommentOverride = new Input<string?>(ctx => appliedComment.Get(ctx)),
        };
        applyLabels.SetDisplayText("Apply Labels & Comment");

        // 5a. Emit TRIAGE.ISSUE.COMPLETED (#3) — reached ONLY via the apply Success
        //     outcome, so COMPLETED is never a false success (a failed apply takes the
        //     Failure edge to the FAILED terminal below).
        var emitCompleted = CycleEvent(
            "EmitCycleCompleted", "Emit TRIAGE.ISSUE.COMPLETED",
            _ => TriageCycleEvents.Completed,
            repository, itemKey, itemNumber, tenantId, itemSource,
            ctx => decisionType.Get(ctx), ctx => decisionPriority.Get(ctx),
            ctx => decisionAutomation.Get(ctx), ctx => decisionStatus.Get(ctx), _ => "");

        var outCompletedResult = new SetOutput
        {
            Id = "OutCompletedResult",
            Name = "Output Item Result (triaged)",
            OutputName = new("itemResult"),
            OutputValue = new(ctx => (object)TriageItemCycleHelper.BuildItemResult(
                itemKey.Get(ctx), TriageCycleEvents.OutcomeTriaged, decisionStatus.Get(ctx), null)),
        };
        outCompletedResult.SetDisplayText("Output Item Result (triaged)");

        // 5b. Apply Failure terminal (review fix) — the engine-callback POST failed
        //     (4xx/5xx or a network throw). Loud TRIAGE.ISSUE.FAILED, fail-closed
        //     itemResult{outcome:"failed"}. Exactly one cycle terminal on this path.
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
                itemKey.Get(ctx), TriageCycleEvents.OutcomeFailed, decisionStatus.Get(ctx),
                skipReason.Get(ctx))),
        };
        outApplyFailedResult.SetDisplayText("Output Item Result (apply failed)");

        // ================================================================
        // Skip / fail terminals
        // ================================================================
        // Skip reason setters (per branch) — explicit + testable.
        var setContextFailedReason = new SetVariable
        {
            Id = "SetContextFailedReason", Name = "Set Context-Failed Reason",
            Variable = skipReason,
            Value = new Input<object?>(_ => (object)"context-failed"),
        };
        setContextFailedReason.SetDisplayText("Set Context-Failed Reason");

        var setPanelFailedReason = new SetVariable
        {
            Id = "SetPanelFailedReason", Name = "Set Panel-Failed Reason",
            Variable = skipReason,
            Value = new Input<object?>(_ => (object)"panel-failed"),
        };
        setPanelFailedReason.SetDisplayText("Set Panel-Failed Reason");

        // Shared SKIPPED terminal — a stage reported a non-applying-but-not-faulted
        // signal (context unavailable / panel below quorum). Loud (warning) audit row.
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
                itemKey.Get(ctx), TriageCycleEvents.OutcomeSkipped, decisionStatus.Get(ctx),
                skipReason.Get(ctx))),
        };
        outSkippedResult.SetDisplayText("Output Item Result (skipped)");

        // FAILED terminal — the PO produced no usable decision (#1/#2). Loud (error).
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
                itemKey.Get(ctx), TriageCycleEvents.OutcomeFailed, decisionStatus.Get(ctx),
                skipReason.Get(ctx))),
        };
        outFailedResult.SetDisplayText("Output Item Result (failed)");

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
                init, emitStarted,
                gatherContext, extractContext, contextGathered,
                panelReview, extractPanelResult, panelUsable,
                poDecision, extractDecision, decisionOk, buildApplyInputs,
                emitLabelsInvalid, seedFailedReason, seedFailedResult,
                applyLabels, emitCompleted, outCompletedResult,
                setApplyFailedReason, emitApplyFailed, outApplyFailedResult,
                setContextFailedReason, setPanelFailedReason,
                markSkipped, outSkipReason, emitSkipped, outSkippedResult,
                setDecisionFailedReason, emitFailed, outFailedResult,
                finish,
            },
            Connections =
            {
                Connect(init, emitStarted),
                Connect(emitStarted, gatherContext),
                Connect(gatherContext, extractContext),
                Connect(extractContext, contextGathered),

                // Context gathered (ok/empty) → run the panel.
                ConnectOutcome(contextGathered, "True", panelReview),
                Connect(panelReview, extractPanelResult),
                Connect(extractPanelResult, panelUsable),

                // Context failed → SKIPPED (no panel over phantom context).
                ConnectOutcome(contextGathered, "False", setContextFailedReason),
                Connect(setContextFailedReason, markSkipped),

                // Panel usable (ok/partial) → PO decision → capture → decision gate.
                ConnectOutcome(panelUsable, "True", poDecision),
                Connect(poDecision, extractDecision),
                Connect(extractDecision, decisionOk),

                // Panel failed → SKIPPED (no labels off a wholly-failed panel).
                ConnectOutcome(panelUsable, "False", setPanelFailedReason),
                Connect(setPanelFailedReason, markSkipped),

                // Decision OK → validate labels + render comment → record any dropped
                // labels (LABELS.INVALID warning) → seed a fail-closed itemResult →
                // apply. The apply Success outcome → COMPLETED; Failure → loud FAILED.
                ConnectOutcome(decisionOk, "True", buildApplyInputs),
                Connect(buildApplyInputs, emitLabelsInvalid),
                Connect(emitLabelsInvalid, seedFailedReason),
                Connect(seedFailedReason, seedFailedResult),
                Connect(seedFailedResult, applyLabels),

                // Apply succeeded → COMPLETED (overwrites the seeded itemResult).
                ConnectOutcome(applyLabels, "Success", emitCompleted),
                Connect(emitCompleted, outCompletedResult),
                Connect(outCompletedResult, finish),

                // Apply failed (4xx/5xx / network) → loud TRIAGE.ISSUE.FAILED terminal —
                // exactly one cycle terminal on the apply-fault path (the review fix).
                ConnectOutcome(applyLabels, "Failure", setApplyFailedReason),
                Connect(setApplyFailedReason, emitApplyFailed),
                Connect(emitApplyFailed, outApplyFailedResult),
                Connect(outApplyFailedResult, finish),

                // Decision NOT OK (faulted PO / llm-failed / unparsed / empty) → FAILED.
                // The False edge NEVER reaches buildApplyInputs / applyLabels.
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

    // ================================================================
    // Helper: Emit a cycle-scoped TRIAGE.ISSUE.* DCB event via the durable drain.
    // ================================================================
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
