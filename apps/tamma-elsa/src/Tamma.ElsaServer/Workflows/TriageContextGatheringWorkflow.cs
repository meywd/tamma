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
using Tamma.ElsaServer.Workflows.Helpers;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Story 39-15 (D5) — Triage Context Gathering, re-implemented as a THIN BINDING over
/// <see cref="DocumentLifecycleWorkflow"/> (the 39-13 Research recipe), producing a typed
/// <see cref="Tamma.Core.Documents.Types.Findings"/> document through the shared
/// produce → validate → review → revise → accept loop. DefinitionId
/// <c>triage-context-gathering</c> is byte-stable; the legacy outputs
/// <c>contextJson</c> (= the accepted Findings document body) and <c>contextStatus</c>
/// (<c>"ok"</c> on accept / <c>"failed"</c> on any typed outcome — the fail-closed
/// contract the cycle already reads) are preserved, plus additive
/// <c>findingsDocumentId</c> / <c>outcome</c> / <c>documentId</c>.
///
/// <para>The produce cell is the SPLIT <c>(developer, triage-context-scan)</c> action —
/// distinct from the free-text <c>(developer, context-scan)</c> that
/// <c>ContextGatheringWorkflow</c> keeps unmigrated (D5). The old bespoke scan →
/// <c>ExtractContext</c> → fail-closed gate is DELETED (no parse, no success-flag gate,
/// ZERO <see cref="Finish"/>); <see cref="TriageContextHelper.DetectItemType"/> survives
/// to feed <c>{{workItemType}}</c>. <c>TRIAGE.CONTEXT.STARTED/COMPLETED/FAILED</c> mirror
/// the lifecycle exits (D6; <c>EMPTY</c> is unreachable — the type expresses "no context"
/// as an empty findings list — but the constant is retained).</para>
/// </summary>
[ResumeBehavior(ResumeMode.LatestStateReEntry)]
public class TriageContextGatheringWorkflow : WorkflowBase
{
    private const string FindingsDocumentType = "findings";
    private const string AmbiguityAssessmentDocumentType = "ambiguity-assessment";

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Triage Context Gathering";
        builder.DefinitionId = "triage-context-gathering";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Gather triage context and synthesize a typed Findings document via the generic document lifecycle";

        // ── Inputs ─────────────────────────────────────────────────────
        var repository = builder.WithVariable<string>("Repository", "");
        var itemJson = builder.WithVariable<string>("ItemJson", "");
        var tenantId = builder.WithVariable<string>("TenantId", "");
        var issueId = builder.WithVariable<string>("IssueId", "");
        var acceptanceRulesJson = builder.WithVariable<string>("AcceptanceRulesJson", "");
        var itemType = builder.WithVariable<string>("ItemType", TriageContextHelper.ItemTypeIssue);
        var itemNumber = builder.WithVariable<int>("ItemNumber", 0);

        // ── 39-10 re-entry position ────────────────────────────────────
        var reEntryPositionJson = builder.WithVariable<string>();
        var reEntryDocJson = builder.WithVariable<string>();
        var positionStage = builder.WithVariable<string>("PositionStage", "produce");

        // ── Story 39-25 — threaded ambiguity score (leg 1) ─────────────
        var assessmentFound = builder.WithVariable<bool>();
        var assessmentJson  = builder.WithVariable<string>("AssessmentJson", "{}");

        // ── Dispatched lifecycle result + typed exit ───────────────────
        var lifecycleResult = builder.WithVariable<IDictionary<string, object>?>();
        var lifecycleAccepted = builder.WithVariable<bool>();
        var exitOutcome = builder.WithVariable<string>("ExitOutcome", "");
        var findingsDocumentId = builder.WithVariable<string>("FindingsDocumentId", "");
        var contextJson = builder.WithVariable<string>("ContextJson", "{}");
        var contextStatus = builder.WithVariable<string>("ContextStatus", TriageBindingHelper.ContextStatusFailed);
        var failureDetail = builder.WithVariable<string>("FailureDetail", "");

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
                tenantId.Set(ctx, ctx.GetInput<string>("tenantId") ?? "");
                acceptanceRulesJson.Set(ctx, ctx.GetInput<string>("acceptanceRulesJson") ?? "");
                itemType.Set(ctx, TriageContextHelper.DetectItemType(item));
                itemNumber.Set(ctx, TriageBindingHelper.ParseItemNumber(item));

                // findings is a shared type (ResearchWorkflow also produces it) — scope the
                // triage-context findings so its accepted-doc + re-entry slice never collides
                // with a research findings for the same issue (CreationBindingHelper D2 pattern).
                var explicitIssueId = ctx.GetInput<string>("issueId") ?? "";
                var baseId = string.IsNullOrWhiteSpace(explicitIssueId)
                    ? CreationBindingHelper.DeriveIssueId(repo, TriageBindingHelper.ParseItemNumber(item))
                    : explicitIssueId;
                issueId.Set(ctx, CreationBindingHelper.ScopeIssueId(baseId, "triage-context"));
                return (object)repo;
            })
        };
        readInputs.SetDisplayText("Read Inputs");

        // ── Step 2: Compute 39-10 re-entry position ────────────────────
        var computeReEntry = new ComputeReEntryPositionActivity
        {
            Id = "ComputeReEntryPosition", Name = "Compute Re-Entry Position",
            IssueId = new(ctx => issueId.Get(ctx)),
            DocumentType = new(FindingsDocumentType),
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

        var emitStarted = ContextEvent("EmitContextStarted", "Emit TRIAGE.CONTEXT.STARTED",
            _ => TriageContextEvents.Started, repository, itemNumber, tenantId, itemType,
            _ => "", _ => 0);

        // ── Story 39-25 (leg 1): fetch the latest ACCEPTED ambiguity-assessment ──
        // Fail-closed: no accepted assessment for this run's anchor ⇒ Found=false ⇒ the
        // ambiguityScore dispatch key below is OMITTED (never a fabricated 0.0).
        // This binding holds only the triage-context SCOPED anchor (its issueId variable);
        // an assessment would have to be persisted under that scope to thread. Honest null
        // in practice; the read stays fail-closed.
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
                    ["documentType"] = FindingsDocumentType,
                    ["producerRole"] = AgentRole.Developer.ToWire(),
                    ["producerAction"] = AgentAction.TriageContextScan.ToWire(),
                    ["producerVariablesJson"] = JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["workItemJson"] = itemJson.Get(ctx) ?? "",
                        ["workItemType"] = itemType.Get(ctx) ?? "",
                        ["previousFindings"] = "{}",
                        ["repository"] = repository.Get(ctx) ?? "",
                    }),
                    ["feedbackVariableName"] = "previousFindings",
                    ["issueId"] = issueId.Get(ctx) ?? "",
                    ["correlationId"] = issueId.Get(ctx) ?? "",
                    ["tenantId"] = tenantId.Get(ctx) ?? "",
                    ["acceptanceRulesJson"] = acceptanceRulesJson.Get(ctx) ?? "",
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
            Variable = contextJson,
            Value = new(ctx =>
            {
                var exit = LifecycleBindingHelper.ReadLifecycleResult(lifecycleResult.Get(ctx));
                var accepted = LifecycleBindingHelper.IsAccepted(exit);

                lifecycleAccepted.Set(ctx, accepted);
                exitOutcome.Set(ctx, exit.Outcome ?? "");
                findingsDocumentId.Set(ctx, exit.DocumentId ?? "");
                contextStatus.Set(ctx, accepted ? TriageBindingHelper.ContextStatusOk : TriageBindingHelper.ContextStatusFailed);
                failureDetail.Set(ctx, TriageBindingHelper.BuildFailureDetail(exit));
                // Legacy contextJson = the accepted Findings body; "{}" on non-accept so a
                // downstream consumer that ignores contextStatus still gets no phantom context.
                return accepted ? exit.DocumentJson : "{}";
            })
        };
        readLifecycleExit.SetDisplayText("Read Lifecycle Exit");

        var lifecycleAcceptedGate = new FlowDecision(ctx => lifecycleAccepted.Get(ctx))
        { Id = "LifecycleAccepted", Name = "Lifecycle Accepted?" };
        lifecycleAcceptedGate.SetDisplayText("Lifecycle Accepted?");

        var wasCompleteReEntry = new FlowDecision(ctx => positionStage.Get(ctx) == "complete")
        { Id = "WasCompleteReEntry", Name = "Was Complete Re-Entry?" };
        wasCompleteReEntry.SetDisplayText("Was Complete Re-Entry?");

        var emitCompleted = ContextEvent("EmitContextCompleted", "Emit TRIAGE.CONTEXT.COMPLETED",
            _ => TriageContextEvents.Completed, repository, itemNumber, tenantId, itemType,
            _ => TriageContextEvents.StatusOk, ctx => contextJson.Get(ctx).Length);

        var emitFailed = ContextEvent("EmitContextFailed", "Emit TRIAGE.CONTEXT.FAILED",
            _ => TriageContextEvents.Failed, repository, itemNumber, tenantId, itemType,
            _ => TriageContextEvents.StatusFailed, _ => 0);

        // ── Step 5: Expose output — the single terminal region ─────────
        var exposeOutput = new Sequence
        {
            Id = "ExposeOutput", Name = "Expose Output",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutputContext", OutputName = new("contextJson"), OutputValue = new(ctx => (object)(contextJson.Get(ctx) ?? "{}")) }, "Output contextJson"),
                WithLabel(new SetOutput { Id = "OutputContextStatus", OutputName = new("contextStatus"), OutputValue = new(ctx => (object)(contextStatus.Get(ctx) ?? TriageBindingHelper.ContextStatusFailed)) }, "Output contextStatus"),
                WithLabel(new SetOutput { Id = "OutputFindingsDocId", OutputName = new("findingsDocumentId"), OutputValue = new(ctx => (object)(findingsDocumentId.Get(ctx) ?? "")) }, "Output findingsDocumentId"),
                WithLabel(new SetOutput { Id = "OutputOutcome", OutputName = new("outcome"), OutputValue = new(ctx => (object)(exitOutcome.Get(ctx) ?? "")) }, "Output outcome"),
            }
        };
        exposeOutput.SetDisplayText("Expose Output");

        builder.Root = new Flowchart
        {
            Id = "TriageContextGatheringFlowchart",
            Start = readInputs,
            Activities =
            {
                readInputs, computeReEntry, readPositionStage, freshRun,
                emitStarted, fetchAmbiguityAssessment, dispatchLifecycle, readLifecycleExit,
                lifecycleAcceptedGate, wasCompleteReEntry, emitCompleted, emitFailed,
                exposeOutput,
            },
            Connections =
            {
                new(readInputs, computeReEntry),
                new(computeReEntry, readPositionStage),
                new(readPositionStage, freshRun),

                new(new FlowEndpoint(freshRun, "True"), new FlowEndpoint(emitStarted)),
                // 39-25 — the ambiguity fetch is the single predecessor of the dispatch,
                // so it runs on every path that actually dispatches (fresh + re-entry).
                new(emitStarted, fetchAmbiguityAssessment),
                new(new FlowEndpoint(freshRun, "False"), new FlowEndpoint(fetchAmbiguityAssessment)),
                new(fetchAmbiguityAssessment, dispatchLifecycle),

                new(dispatchLifecycle, readLifecycleExit),
                new(readLifecycleExit, lifecycleAcceptedGate),

                new(new FlowEndpoint(lifecycleAcceptedGate, "True"), new FlowEndpoint(wasCompleteReEntry)),
                new(new FlowEndpoint(wasCompleteReEntry, "False"), new FlowEndpoint(emitCompleted)),
                new(emitCompleted, exposeOutput),
                new(new FlowEndpoint(wasCompleteReEntry, "True"), new FlowEndpoint(exposeOutput)),

                new(new FlowEndpoint(lifecycleAcceptedGate, "False"), new FlowEndpoint(emitFailed)),
                new(emitFailed, exposeOutput),
            }
        };
    }

    private static EmitTriageContextEventActivity ContextEvent(
        string id, string label,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, string> eventType,
        Elsa.Workflows.Memory.Variable<string> repository,
        Elsa.Workflows.Memory.Variable<int> itemNumber,
        Elsa.Workflows.Memory.Variable<string> tenantId,
        Elsa.Workflows.Memory.Variable<string> itemType,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, string> contextStatus,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, int> contextJsonLength)
    {
        var emit = new EmitTriageContextEventActivity
        {
            Id = id, Name = label,
            EventType = new(eventType),
            Repository = new(ctx => repository.Get(ctx)),
            ItemNumber = new(ctx => itemNumber.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            ItemType = new(ctx => itemType.Get(ctx)),
            ContextStatus = new(ctx => contextStatus(ctx)),
            ContextJsonLength = new(contextJsonLength),
        };
        emit.SetDisplayText(label);
        return emit;
    }
}
