using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Runtime.Activities;
using System.Text.Json;
using Tamma.Activities.ADL;
using Tamma.Activities.Documents;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents.Resume;
using Tamma.Core.Documents.Types;
using Tamma.ElsaServer.Workflows.Helpers;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Story 39-15 (D5/D6) — Triage PO Decision, re-implemented as a THIN BINDING over
/// <see cref="DocumentLifecycleWorkflow"/>, producing a typed
/// <see cref="Tamma.Core.Documents.Types.TriageDecision"/> through the shared
/// produce → validate → review(panel) → revise → accept loop. DefinitionId
/// <c>triage-po-decision</c> is byte-stable. The PRODUCE cell is
/// <c>(product_owner, triage-intake)</c> (the DRAFT decision); VALIDATE is
/// <see cref="TriageDecisionDocumentType"/> (closed enums + reasoning — enum invalidity
/// is a validator failure, not a parse branch); REVIEW is the 39-7 panel over the draft
/// with the doc-type-aware TRIAGE roster (the retired <c>TriagePanelReviewWorkflow</c>'s
/// semantics, now lifecycle config); ACCEPT is the orchestrator gate.
///
/// <para>Legacy outputs are preserved: <c>decisionJson</c> (the accepted TriageDecision
/// projected to the wire <see cref="TriagePoDecisionHelper.ParseDecision"/> round-trips
/// clean), <c>callSucceeded</c> (accept → true; typed outcome → false),
/// <c>providerUsed</c>/<c>costUsd</c>/<c>rawResponse</c> (audit-only, empty here). The
/// empty-input short-circuit (SKIPPED, no dispatch, emitted before any dispatch) is kept.
/// <c>TRIAGE.PO_DECISION.*</c> + <c>TRIAGE.PANEL.*</c> mirror the lifecycle exits (D6).</para>
/// </summary>
[ResumeBehavior(ResumeMode.LatestStateReEntry)]
public class TriagePODecisionWorkflow : WorkflowBase
{
    private const string TriageDecisionDocumentType = "triage-decision";
    private const int TriageRosterSize = 4;
    private const string AmbiguityAssessmentDocumentType = "ambiguity-assessment";

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Triage PO Decision";
        builder.DefinitionId = "triage-po-decision";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Produce a reviewed, typed TriageDecision via the generic document lifecycle (produce → validate → review(panel) → accept)";

        // ── Inputs ─────────────────────────────────────────────────────
        var repository = builder.WithVariable<string>("Repository", "");
        var itemJson = builder.WithVariable<string>("ItemJson", "");
        var contextJson = builder.WithVariable<string>("ContextJson", "{}");
        var tenantId = builder.WithVariable<string>("TenantId", "");
        var issueId = builder.WithVariable<string>("IssueId", "");
        var findingsDocumentId = builder.WithVariable<string>("FindingsDocumentId", "");
        var acceptanceRulesJson = builder.WithVariable<string>("AcceptanceRulesJson", "");
        var itemNumber = builder.WithVariable<int>("ItemNumber", 0);

        // ── Story 39-25 — threaded ambiguity score (leg 1) ─────────────
        var assessmentFound = builder.WithVariable<bool>();
        var assessmentJson  = builder.WithVariable<string>("AssessmentJson", "{}");

        // ── 39-10 re-entry position ────────────────────────────────────
        var reEntryPositionJson = builder.WithVariable<string>();
        var reEntryDocJson = builder.WithVariable<string>();
        var positionStage = builder.WithVariable<string>("PositionStage", "produce");

        // ── Dispatched lifecycle result + typed exit ───────────────────
        var lifecycleResult = builder.WithVariable<IDictionary<string, object>?>();
        var lifecycleAccepted = builder.WithVariable<bool>();
        var exitOutcome = builder.WithVariable<string>("ExitOutcome", "");
        var exitDocId = builder.WithVariable<string>("ExitDocId", "");
        var documentJson = builder.WithVariable<string>("DocumentJson", "");
        var decisionJson = builder.WithVariable<string>("DecisionJson", "{}");
        var callSucceeded = builder.WithVariable<bool>("CallSucceeded", false);
        var failureDetail = builder.WithVariable<string>("FailureDetail", "");

        // Decision fields surfaced for the COMPLETED event payload.
        var decisionStatus = builder.WithVariable<string>("DecisionStatus", "");
        var priority = builder.WithVariable<string>("Priority", "");
        var type = builder.WithVariable<string>("Type", "");
        var complexity = builder.WithVariable<string>("Complexity", "");
        var automation = builder.WithVariable<string>("Automation", "");

        // Panel mirror counts.
        var panelMemberCount = builder.WithVariable<int>("PanelMemberCount", TriageRosterSize);
        var panelSucceededCount = builder.WithVariable<int>("PanelSucceededCount", 0);
        var panelFailedRolesJson = builder.WithVariable<string>("PanelFailedRolesJson", "[]");

        // ── Step 1: Read inputs ────────────────────────────────────────
        var readInputs = new SetVariable
        {
            Id = "ReadInputs", Name = "Read Inputs",
            Variable = repository,
            Value = new(ctx =>
            {
                var repo = ctx.GetInput<string>("repository") ?? "";
                var item = ctx.GetInput<string>("itemJson") ?? "";
                itemJson.Set(ctx, item);
                contextJson.Set(ctx, ctx.GetInput<string>("contextJson") ?? "{}");
                tenantId.Set(ctx, ctx.GetInput<string>("tenantId") ?? "");
                findingsDocumentId.Set(ctx, ctx.GetInput<string>("findingsDocumentId") ?? "");
                acceptanceRulesJson.Set(ctx, ctx.GetInput<string>("acceptanceRulesJson") ?? "");
                itemNumber.Set(ctx, TriageBindingHelper.ParseItemNumber(item));

                var explicitIssueId = ctx.GetInput<string>("issueId") ?? "";
                issueId.Set(ctx, string.IsNullOrWhiteSpace(explicitIssueId)
                    ? CreationBindingHelper.DeriveIssueId(repo, TriageBindingHelper.ParseItemNumber(item))
                    : explicitIssueId);
                return (object)repo;
            })
        };
        readInputs.SetDisplayText("Read Inputs");

        // ── Step 2: Compute 39-10 re-entry position ────────────────────
        var computeReEntry = new ComputeReEntryPositionActivity
        {
            Id = "ComputeReEntryPosition", Name = "Compute Re-Entry Position",
            IssueId = new(ctx => issueId.Get(ctx)),
            DocumentType = new(TriageDecisionDocumentType),
            TenantId = new(ctx => tenantId.Get(ctx)),
            CorrelationId = new(ctx => issueId.Get(ctx)),
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

        var freshRun = new FlowDecision(ctx => positionStage.Get(ctx) == "produce")
        { Id = "FreshRun", Name = "Fresh Run?" };
        freshRun.SetDisplayText("Fresh Run?");

        // ── Empty-input short-circuit (kept — the one pre-lifecycle guard that saves LLM spend) ──
        var inputsPresent = new FlowDecision(ctx => TriagePoDecisionHelper.IsUsableInput(itemJson.Get(ctx)))
        { Id = "InputsPresent", Name = "Inputs Present?" };
        inputsPresent.SetDisplayText("Inputs Present?");

        var buildSkipped = new SetVariable
        {
            Id = "BuildSkipped", Name = "Build Skipped Decision",
            Variable = decisionJson,
            Value = new(ctx =>
            {
                var d = TriagePoDecisionHelper.BuildSkippedDecision();
                decisionStatus.Set(ctx, d.Status);
                callSucceeded.Set(ctx, false);
                return (object)TriagePoDecisionHelper.Serialize(d);
            })
        };
        buildSkipped.SetDisplayText("Build Skipped Decision");

        var emitSkipped = PoEvent("EmitSkipped", "Emit TRIAGE.PO_DECISION.SKIPPED",
            _ => TriagePoDecisionEvents.Skipped, repository, itemNumber, tenantId,
            ctx => decisionStatus.Get(ctx), _ => "", _ => "", _ => "", _ => "", _ => "");

        var emitStarted = PoEvent("EmitStarted", "Emit TRIAGE.PO_DECISION.STARTED",
            _ => TriagePoDecisionEvents.Started, repository, itemNumber, tenantId,
            _ => "", _ => "", _ => "", _ => "", _ => "", _ => "");

        var emitPanelStarted = PanelEvent("EmitPanelStarted", "Emit TRIAGE.PANEL.STARTED",
            _ => TriageEvents.PanelStarted, repository, itemNumber, tenantId,
            _ => TriageRosterSize, _ => 0, _ => "[]");

        // ── Story 39-25 (leg 1): fetch the latest ACCEPTED ambiguity-assessment ──
        // Fail-closed: no accepted assessment for this run's anchor ⇒ Found=false ⇒ the
        // ambiguityScore dispatch key below is OMITTED (never a fabricated 0.0).
        var fetchAmbiguityAssessment = new FetchLatestAcceptedDocumentActivity
        {
            Id = "FetchAmbiguityAssessment", Name = "Fetch Accepted Ambiguity Assessment",
            IssueId = new(ctx => issueId.Get(ctx)),
            DocumentTypeKey = new(AmbiguityAssessmentDocumentType),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Found = new(assessmentFound),
            DocumentJson = new(assessmentJson),
        };
        fetchAmbiguityAssessment.SetDisplayText("Fetch Accepted Ambiguity Assessment");

        // ── Step 3: Dispatch the generic document lifecycle ────────────
        var dispatchLifecycle = new DispatchWorkflow
        {
            Id = "DispatchLifecycle", Name = "Dispatch Document Lifecycle",
            WorkflowDefinitionId = new("document-lifecycle"),
            Input = new(ctx =>
            {
                var input = new Dictionary<string, object>
                {
                    ["documentType"] = TriageDecisionDocumentType,
                    ["producerRole"] = AgentRole.ProductOwner.ToWire(),
                    ["producerAction"] = AgentAction.TriageIntake.ToWire(),
                    ["producerVariablesJson"] = JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["itemJson"] = itemJson.Get(ctx) ?? "",
                        // The gathered Findings context is folded into the DECLARED contextFindings
                        // carrier (render-drop lesson); repair/revise notes land in the same carrier.
                        ["contextFindings"] = contextJson.Get(ctx) ?? "{}",
                        ["repository"] = repository.Get(ctx) ?? "",
                    }),
                    ["feedbackVariableName"] = "contextFindings",
                    ["issueId"] = issueId.Get(ctx) ?? "",
                    ["correlationId"] = issueId.Get(ctx) ?? "",
                    ["tenantId"] = tenantId.Get(ctx) ?? "",
                    // Behavior-preserving triage default rules (panel roster + quorum 2 + needs-human
                    // always-escalate) unless the caller/store passes an explicit override.
                    ["acceptanceRulesJson"] = string.IsNullOrWhiteSpace(acceptanceRulesJson.Get(ctx))
                        ? TriageBindingHelper.DefaultTriageRulesJson()
                        : acceptanceRulesJson.Get(ctx)!,
                };
                // 39-25 — thread the accepted assessment's score; ABSENT when none (null stays null).
                if (LifecycleBindingHelper.TryReadAssessmentScore(
                        assessmentFound.Get(ctx), assessmentJson.Get(ctx)) is double ambiguityScore)
                    input["ambiguityScore"] = ambiguityScore;
                return input;
            }),
            WaitForCompletion = new(true),
            Result = new(lifecycleResult),
        };
        dispatchLifecycle.SetDisplayText("Dispatch Document Lifecycle");

        // ── Step 4: Read the typed lifecycle exit (fail-closed) ────────
        var readLifecycleExit = new SetVariable
        {
            Id = "ReadLifecycleExit", Name = "Read Lifecycle Exit",
            Variable = decisionJson,
            Value = new(ctx =>
            {
                var exit = LifecycleBindingHelper.ReadLifecycleResult(lifecycleResult.Get(ctx));
                var accepted = LifecycleBindingHelper.IsAccepted(exit);

                lifecycleAccepted.Set(ctx, accepted);
                callSucceeded.Set(ctx, accepted);
                exitOutcome.Set(ctx, exit.Outcome ?? "");
                exitDocId.Set(ctx, exit.DocumentId ?? "");
                failureDetail.Set(ctx, TriageBindingHelper.BuildFailureDetail(exit));

                var mirror = TriageBindingHelper.ReadPanelMirror(exit.DocumentJson, accepted, TriageRosterSize);
                panelMemberCount.Set(ctx, mirror.MemberCount);
                panelSucceededCount.Set(ctx, mirror.SucceededCount);
                panelFailedRolesJson.Set(ctx, mirror.FailedRolesJson);

                if (accepted)
                {
                    documentJson.Set(ctx, exit.DocumentJson);
                    var d = TryReadDecision(exit.DocumentJson);
                    decisionStatus.Set(ctx, TriagePoDecisionHelper.StatusOk);
                    priority.Set(ctx, d?.Priority ?? "");
                    type.Set(ctx, d?.Type ?? "");
                    complexity.Set(ctx, d?.Complexity ?? "");
                    automation.Set(ctx, d?.Automation ?? "");
                    return (object)TriageBindingHelper.ProjectLegacyDecisionJson(exit.DocumentJson);
                }

                // Non-accept — honest fallback labels (needs-human), NEVER a fabricated clean decision.
                documentJson.Set(ctx, "");
                var failure = TriagePoDecisionHelper.BuildFailureDecision(TriageBindingHelper.BuildFailureDetail(exit));
                decisionStatus.Set(ctx, failure.Status);
                priority.Set(ctx, failure.Priority);
                type.Set(ctx, failure.Type);
                complexity.Set(ctx, failure.Complexity);
                automation.Set(ctx, failure.Automation);
                return (object)TriagePoDecisionHelper.Serialize(failure);
            })
        };
        readLifecycleExit.SetDisplayText("Read Lifecycle Exit");

        var lifecycleAcceptedGate = new FlowDecision(ctx => lifecycleAccepted.Get(ctx))
        { Id = "LifecycleAccepted", Name = "Lifecycle Accepted?" };
        lifecycleAcceptedGate.SetDisplayText("Lifecycle Accepted?");

        var wasCompleteReEntry = new FlowDecision(ctx => positionStage.Get(ctx) == "complete")
        { Id = "WasCompleteReEntry", Name = "Was Complete Re-Entry?" };
        wasCompleteReEntry.SetDisplayText("Was Complete Re-Entry?");

        var emitPanelCompleted = PanelEvent("EmitPanelCompleted", "Emit TRIAGE.PANEL.COMPLETED",
            _ => TriageEvents.PanelCompleted, repository, itemNumber, tenantId,
            ctx => panelMemberCount.Get(ctx), ctx => panelSucceededCount.Get(ctx), ctx => panelFailedRolesJson.Get(ctx));

        var emitCompleted = PoEvent("EmitCompleted", "Emit TRIAGE.PO_DECISION.COMPLETED",
            _ => TriagePoDecisionEvents.Completed, repository, itemNumber, tenantId,
            ctx => decisionStatus.Get(ctx), ctx => priority.Get(ctx), ctx => type.Get(ctx),
            ctx => complexity.Get(ctx), ctx => automation.Get(ctx), _ => "");

        var emitPanelFailed = PanelEvent("EmitPanelFailed", "Emit TRIAGE.PANEL.FAILED",
            _ => TriageEvents.PanelFailed, repository, itemNumber, tenantId,
            _ => TriageRosterSize, _ => 0, _ => "[]");

        var emitFailed = PoEvent("EmitFailed", "Emit TRIAGE.PO_DECISION.FAILED",
            _ => TriagePoDecisionEvents.Failed, repository, itemNumber, tenantId,
            ctx => decisionStatus.Get(ctx), _ => "", _ => "", _ => "", _ => "",
            ctx => failureDetail.Get(ctx));

        // ── Step 5: Set Outputs — the single terminal region ───────────
        var setOutputs = new Sequence
        {
            Id = "SetOutputs", Name = "Set Outputs",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutDecision", OutputName = new("decisionJson"), OutputValue = new(ctx => (object)(decisionJson.Get(ctx) ?? "{}")) }, "Output decisionJson"),
                WithLabel(new SetOutput { Id = "OutCallSucceeded", OutputName = new("callSucceeded"), OutputValue = new(ctx => (object)callSucceeded.Get(ctx)) }, "Output callSucceeded"),
                WithLabel(new SetOutput { Id = "OutProviderUsed", OutputName = new("providerUsed"), OutputValue = new(_ => (object)"") }, "Output providerUsed"),
                WithLabel(new SetOutput { Id = "OutCostUsd", OutputName = new("costUsd"), OutputValue = new(_ => (object)0m) }, "Output costUsd"),
                WithLabel(new SetOutput { Id = "OutRawResponse", OutputName = new("rawResponse"), OutputValue = new(_ => (object)"") }, "Output rawResponse"),
                WithLabel(new SetOutput { Id = "OutDocumentJson", OutputName = new("documentJson"), OutputValue = new(ctx => (object)(documentJson.Get(ctx) ?? "")) }, "Output documentJson"),
                WithLabel(new SetOutput { Id = "OutDocumentId", OutputName = new("documentId"), OutputValue = new(ctx => (object)(exitDocId.Get(ctx) ?? "")) }, "Output documentId"),
                WithLabel(new SetOutput { Id = "OutOutcome", OutputName = new("outcome"), OutputValue = new(ctx => (object)(exitOutcome.Get(ctx) ?? "")) }, "Output outcome"),
                WithLabel(new SetOutput { Id = "OutStatus", OutputName = new("status"), OutputValue = new(ctx => (object)decisionStatus.Get(ctx)) }, "Output status"),
            }
        };
        setOutputs.SetDisplayText("Set Outputs");

        builder.Root = new Flowchart
        {
            Id = "TriagePODecisionFlowchart",
            Start = readInputs,
            Activities =
            {
                readInputs, computeReEntry, readPositionStage, freshRun,
                inputsPresent, buildSkipped, emitSkipped,
                emitStarted, emitPanelStarted, fetchAmbiguityAssessment, dispatchLifecycle, readLifecycleExit,
                lifecycleAcceptedGate, wasCompleteReEntry,
                emitPanelCompleted, emitCompleted, emitPanelFailed, emitFailed,
                setOutputs,
            },
            Connections =
            {
                new(readInputs, computeReEntry),
                new(computeReEntry, readPositionStage),
                new(readPositionStage, freshRun),

                // Fresh run → empty-input guard.
                new(new FlowEndpoint(freshRun, "True"), new FlowEndpoint(inputsPresent)),
                new(new FlowEndpoint(inputsPresent, "False"), new FlowEndpoint(buildSkipped)),
                new(buildSkipped, emitSkipped),
                new(emitSkipped, setOutputs),

                new(new FlowEndpoint(inputsPresent, "True"), new FlowEndpoint(emitStarted)),
                new(emitStarted, emitPanelStarted),
                // 39-25 — the ambiguity fetch is the single predecessor of the dispatch,
                // so it runs on every path that actually dispatches (fresh + re-entry).
                new(emitPanelStarted, fetchAmbiguityAssessment),

                // Re-entry → fetch → dispatch (no double STARTED/PANEL.STARTED emit).
                new(new FlowEndpoint(freshRun, "False"), new FlowEndpoint(fetchAmbiguityAssessment)),
                new(fetchAmbiguityAssessment, dispatchLifecycle),

                new(dispatchLifecycle, readLifecycleExit),
                new(readLifecycleExit, lifecycleAcceptedGate),

                new(new FlowEndpoint(lifecycleAcceptedGate, "True"), new FlowEndpoint(wasCompleteReEntry)),
                new(new FlowEndpoint(wasCompleteReEntry, "False"), new FlowEndpoint(emitPanelCompleted)),
                new(emitPanelCompleted, emitCompleted),
                new(emitCompleted, setOutputs),
                new(new FlowEndpoint(wasCompleteReEntry, "True"), new FlowEndpoint(setOutputs)),

                new(new FlowEndpoint(lifecycleAcceptedGate, "False"), new FlowEndpoint(emitPanelFailed)),
                new(emitPanelFailed, emitFailed),
                new(emitFailed, setOutputs),
            }
        };
    }

    private static Tamma.Core.Documents.Types.TriageDecision? TryReadDecision(string? documentJson)
    {
        if (string.IsNullOrWhiteSpace(documentJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<Tamma.Core.Documents.Types.TriageDecision>(documentJson!, Tamma.Core.Documents.DocumentJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static EmitTriagePoDecisionEventActivity PoEvent(
        string id, string label,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, string> eventType,
        Elsa.Workflows.Memory.Variable<string> repository,
        Elsa.Workflows.Memory.Variable<int> itemNumber,
        Elsa.Workflows.Memory.Variable<string> tenantId,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, string> decisionStatus,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, string> priority,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, string> type,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, string> complexity,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, string> automation,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, string> error)
    {
        var emit = new EmitTriagePoDecisionEventActivity
        {
            Id = id, Name = label,
            EventType = new(eventType),
            Repository = new(ctx => repository.Get(ctx)),
            ItemNumber = new(ctx => itemNumber.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            DecisionStatus = new(ctx => decisionStatus(ctx)),
            Priority = new(ctx => priority(ctx)),
            Type = new(ctx => type(ctx)),
            Complexity = new(ctx => complexity(ctx)),
            Automation = new(ctx => automation(ctx)),
            ProviderUsed = new(_ => ""),
            CostUsd = new(0m),
            Error = new(ctx => error(ctx)),
        };
        emit.SetDisplayText(label);
        return emit;
    }

    private static EmitTriageEventActivity PanelEvent(
        string id, string label,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, string> eventType,
        Elsa.Workflows.Memory.Variable<string> repository,
        Elsa.Workflows.Memory.Variable<int> itemNumber,
        Elsa.Workflows.Memory.Variable<string> tenantId,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, int> roleCount,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, int> succeededCount,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, string> failedRolesJson)
    {
        var emit = new EmitTriageEventActivity
        {
            Id = id, Name = label,
            EventType = new(eventType),
            Repository = new(ctx => repository.Get(ctx)),
            ItemNumber = new(ctx => itemNumber.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            RoleCount = new(roleCount),
            SucceededCount = new(succeededCount),
            FailedRolesJson = new(ctx => failedRolesJson(ctx)),
        };
        emit.SetDisplayText(label);
        return emit;
    }
}
