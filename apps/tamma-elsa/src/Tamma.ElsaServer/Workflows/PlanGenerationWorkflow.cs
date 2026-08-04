using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Runtime.Activities;
using System.Text.Json;
using Tamma.Activities.Context;
using Tamma.Activities.Documents;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents.Resume;
using Tamma.ElsaServer.Workflows.Helpers;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Story 39-14 — Plan Generation, re-implemented as a THIN BINDING over
/// <see cref="DocumentLifecycleWorkflow"/> (<c>DefinitionId = "document-lifecycle"</c>),
/// producing a typed <see cref="Tamma.Core.Documents.Types.Plan"/> reviewed via the unified
/// <see cref="Tamma.Core.Documents.Types.Review"/> (39-7 panel). The public surface is
/// byte-stable (D1): same <c>DefinitionId = "plan-generation"</c>, same inputs
/// (<c>repository</c>/<c>issueNumber</c>/<c>poSummary</c>/<c>contextIds</c>/<c>workItemJson</c>/
/// <c>reviewNotes</c>/<c>revisionNumber</c>/<c>tenantId</c>, plus additive <c>issueId?</c> /
/// <c>acceptanceRulesJson?</c>), same <c>planJson</c>/<c>error</c> outputs plus additive
/// <c>status</c>/<c>outcome</c>/<c>documentId</c>/<c>decision</c>/<c>reviewNotes</c>. The
/// SingleIssueCycle dispatch site (by definition id) is untouched.
///
/// <para><b>What changed (the epic's charter).</b> The bespoke validation-retry loop — the
/// <c>ValidationErrors</c> loop-back, the <c>OutErr</c> terminal, the <c>maxRetries</c> counter,
/// the hand parser (<c>PlanValidationHelper</c>) — is DELETED. Validation failure now flows
/// through the generic validate → repair/revise → review → accept rings and, at worst, exits as
/// a typed escalation (<c>validation-exhausted</c> / <c>rounds-exhausted</c> /
/// <c>review-undecidable</c>) with full lineage — never a dead terminal. Plan review runs INSIDE
/// the lifecycle via 39-7's producers (a 7-role panel by default policy, D3); the panel's
/// discussion rounds ARE the lifecycle's revise rounds. The binding contributes NO parse, NO
/// verdict gate, and ZERO <see cref="Finish"/> activities.</para>
///
/// <para><b>Consumes the accepted decomposition (D4).</b> On a fresh run it fetches the latest
/// accepted <c>decomposition</c> for the issue (the 39-12 pilot's output) and folds its JSON into
/// the DECLARED <c>contextFindings</c> producer variable ahead of <c>poSummary</c> — NOT a new
/// <c>decompositionJson</c> key, which the shared Plan-family template does not declare and would
/// silently drop at render (the <see cref="ValidationFeedbackHelper"/> render-drop lesson).
/// Repair/revise notes land in that SAME declared carrier via <c>feedbackVariableName =
/// "contextFindings"</c> (39-6 D11).</para>
///
/// <para><b>Resumable per the standard (D9).</b> Declared <c>[ResumeBehavior(LatestStateReEntry)]</c>
/// with the generic <see cref="ComputeReEntryPositionActivity"/> gate — the accept-gate suspend
/// happens inside the dispatched child lifecycle, which the parent awaits via
/// <c>WaitForCompletion</c>. Removed from the legacy resume allowlist.</para>
/// </summary>
[ResumeBehavior(ResumeMode.LatestStateReEntry)]
public class PlanGenerationWorkflow : WorkflowBase
{
    private const string PlanDocumentType = "plan";
    private const string DecompositionDocumentType = "decomposition";
    private const string AmbiguityAssessmentDocumentType = "ambiguity-assessment";

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Plan Generation";
        builder.DefinitionId = "plan-generation";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Generate a reviewed implementation plan via the generic document lifecycle (produce → validate → review(panel) → revise → accept)";

        // ── Inputs (compat set + additive) ─────────────────────────────
        var repository      = builder.WithVariable<string>("Repository", "");
        var issueNumber     = builder.WithVariable<int>("IssueNumber", 0);
        var poSummary       = builder.WithVariable<string>("POSummary", "");
        var contextIds      = builder.WithVariable<string>("ContextIds", "[]");
        var workItemJson    = builder.WithVariable<string>("WorkItemJson", "");
        var reviewNotes     = builder.WithVariable<string>("ReviewNotes", "");
        var revisionNumber  = builder.WithVariable<int>("RevisionNumber", 0);
        var tenantId        = builder.WithVariable<string>("TenantId", "");
        var issueId         = builder.WithVariable<string>("IssueId", "");
        var acceptanceRulesJson = builder.WithVariable<string>("AcceptanceRulesJson", "");

        // ── Consumed decomposition (D4) ────────────────────────────────
        var decompositionJson = builder.WithVariable<string>("DecompositionJson", "");
        var decompositionFound = builder.WithVariable<bool>();
        var decompositionDocId = builder.WithVariable<string>();
        var decompositionLineage = builder.WithVariable<string>();

        // ── Story 39-25 — threaded ambiguity score (leg 1) ─────────────
        var assessmentFound = builder.WithVariable<bool>();
        var assessmentJson  = builder.WithVariable<string>("AssessmentJson", "{}");

        // ── 39-10 re-entry position (D9) ───────────────────────────────
        var reEntryPositionJson = builder.WithVariable<string>();
        var reEntryDocJson  = builder.WithVariable<string>();
        var positionStage   = builder.WithVariable<string>("PositionStage", "produce");

        // ── Dispatched-workflow result + typed exit ────────────────────
        var lifecycleResult = builder.WithVariable<IDictionary<string, object>?>();
        var lifecycleAccepted = builder.WithVariable<bool>();
        var exitStatus      = builder.WithVariable<string>("ExitStatus", "");
        var exitOutcome     = builder.WithVariable<string>("ExitOutcome", "");
        var exitDocId       = builder.WithVariable<string>("ExitDocId", "");
        var planJson        = builder.WithVariable<string>("PlanJson", "");
        var decisionNotes   = builder.WithVariable<string>("DecisionNotes", "");
        var legacyDecision  = builder.WithVariable<string>("LegacyDecision", "needsHuman");
        var failureDetail   = builder.WithVariable<string>("FailureDetail", "");
        var outputStatus    = builder.WithVariable<string>();
        var outputError     = builder.WithVariable<string>("OutputError", "");

        // ── Step 1: Read inputs ────────────────────────────────────────
        var readInputs = new SetVariable
        {
            Id = "ReadInputs", Name = "Read Inputs",
            Variable = repository,
            Value = new(ctx =>
            {
                var repo = ctx.GetInput<string>("repository") ?? "";
                issueNumber.Set(ctx, ctx.GetInput<int>("issueNumber"));
                poSummary.Set(ctx, ctx.GetInput<string>("poSummary") ?? "");
                contextIds.Set(ctx, ctx.GetInput<string>("contextIds") ?? "[]");
                workItemJson.Set(ctx, ctx.GetInput<string>("workItemJson") ?? "");
                reviewNotes.Set(ctx, ctx.GetInput<string>("reviewNotes") ?? "");
                revisionNumber.Set(ctx, ctx.GetInput<int>("revisionNumber"));
                tenantId.Set(ctx, ctx.GetInput<string>("tenantId") ?? "");
                acceptanceRulesJson.Set(ctx, ctx.GetInput<string>("acceptanceRulesJson") ?? "");

                // Issue identity: explicit input else derived "{repo}#{n}" (D4 — SingleIssueCycle passes none).
                var explicitIssueId = ctx.GetInput<string>("issueId") ?? "";
                issueId.Set(ctx, string.IsNullOrWhiteSpace(explicitIssueId)
                    ? PlanBindingHelper.DeriveIssueId(repo, ctx.GetInput<int>("issueNumber"))
                    : explicitIssueId);
                return (object)repo;
            })
        };
        readInputs.SetDisplayText("Read Inputs");

        // ── Step 2: Compute 39-10 re-entry position (D9) ───────────────
        var computeReEntry = new ComputeReEntryPositionActivity
        {
            Id = "ComputeReEntryPosition", Name = "Compute Re-Entry Position",
            IssueId = new(ctx => issueId.Get(ctx)),
            DocumentType = new(PlanDocumentType),
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

        // ── Step 3: FreshRun gate — fetch the consumed decomposition only on a fresh run (D4/D9) ──
        var freshRun = new FlowDecision(ctx => positionStage.Get(ctx) == "produce")
        { Id = "FreshRun", Name = "Fresh Run?" };
        freshRun.SetDisplayText("Fresh Run?");

        var fetchDecomposition = new FetchLatestAcceptedDocumentActivity
        {
            Id = "FetchDecomposition", Name = "Fetch Accepted Decomposition",
            IssueId = new(ctx => issueId.Get(ctx)),
            DocumentTypeKey = new(DecompositionDocumentType),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Found = new(decompositionFound),
            DocumentId = new(decompositionDocId),
            DocumentJson = new(decompositionJson),
            LineageJson = new(decompositionLineage),
        };
        fetchDecomposition.SetDisplayText("Fetch Accepted Decomposition");

        // ── Step 3b (39-25 leg 1): fetch the latest ACCEPTED ambiguity-assessment ──
        // Fail-closed: no accepted assessment for this issue ⇒ Found=false ⇒ the
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

        // ── Step 4: Dispatch the generic document lifecycle ────────────
        var dispatchLifecycle = new DispatchWorkflow
        {
            Id = "DispatchLifecycle", Name = "Dispatch Document Lifecycle",
            WorkflowDefinitionId = new("document-lifecycle"),
            Input = new(ctx =>
            {
                var input = new Dictionary<string, object>
                {
                    ["documentType"]          = PlanDocumentType,
                    ["producerRole"]          = AgentRole.Architect.ToWire(),
                    ["producerAction"]        = AgentAction.PlanSystemDesign.ToWire(),
                    ["producerVariablesJson"] = JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["workItemJson"] = workItemJson.Get(ctx) ?? "",
                        // D4 — the consumed decomposition is folded into the DECLARED contextFindings
                        // carrier ahead of poSummary; NOT a new (render-dropped) decompositionJson key.
                        ["contextFindings"] = PlanBindingHelper.MergeDecompositionIntoCarrier(
                            poSummary.Get(ctx) ?? "", decompositionJson.Get(ctx) ?? ""),
                        ["poSummary"]      = poSummary.Get(ctx) ?? "",
                        ["contextIds"]     = contextIds.Get(ctx) ?? "[]",
                        ["repository"]     = repository.Get(ctx) ?? "",
                        ["reviewNotes"]    = reviewNotes.Get(ctx) ?? "",
                        ["revisionNumber"] = revisionNumber.Get(ctx),
                    }),
                    // 39-6 D11 — repair/revise notes land in the DECLARED carrier, not a dropped key.
                    ["feedbackVariableName"] = "contextFindings",
                    ["issueId"]             = issueId.Get(ctx) ?? "",
                    ["correlationId"]       = issueId.Get(ctx) ?? "",
                    ["tenantId"]            = tenantId.Get(ctx) ?? "",
                    // D3 — behavior-preserving default rules (rounds 3 / repair 2 / 7-role panel) unless
                    // the caller/store passes an explicit override.
                    ["acceptanceRulesJson"] = string.IsNullOrWhiteSpace(acceptanceRulesJson.Get(ctx))
                        ? PlanBindingHelper.DefaultPlanRulesJson()
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

        // ── Step 5: Read the typed lifecycle exit (fail-closed) ────────
        var readLifecycleExit = new SetVariable
        {
            Id = "ReadLifecycleExit", Name = "Read Lifecycle Exit",
            Variable = planJson,
            Value = new(ctx =>
            {
                var exit = LifecycleBindingHelper.ReadLifecycleResult(lifecycleResult.Get(ctx));
                var accepted = LifecycleBindingHelper.IsAccepted(exit);

                lifecycleAccepted.Set(ctx, accepted);
                exitStatus.Set(ctx, exit.Status);
                exitOutcome.Set(ctx, exit.Outcome ?? "");
                exitDocId.Set(ctx, exit.DocumentId ?? "");
                decisionNotes.Set(ctx, exit.DecisionNotes);
                legacyDecision.Set(ctx, PlanBindingHelper.MapDecisionForLegacyOutput(exit));
                failureDetail.Set(ctx, PlanBindingHelper.BuildFailureDetail(exit));
                // status: "completed" on acceptance (compat, D1); else the typed exit status.
                outputStatus.Set(ctx, accepted ? "completed" : exit.Status);
                // planJson is the accepted body, "" otherwise (parent's empty-plan edge fires, D1).
                var body = accepted ? exit.DocumentJson : "";
                // error/reviewNotes compat: the decider's notes on acceptance, the failure detail otherwise.
                outputError.Set(ctx, accepted ? "" : PlanBindingHelper.BuildFailureDetail(exit));
                return body;
            })
        };
        readLifecycleExit.SetDisplayText("Read Lifecycle Exit");

        // ── Step 6: Accepted? (typed) ──────────────────────────────────
        var lifecycleAcceptedGate = new FlowDecision(ctx => lifecycleAccepted.Get(ctx))
        { Id = "LifecycleAccepted", Name = "Lifecycle Accepted?" };
        lifecycleAcceptedGate.SetDisplayText("Lifecycle Accepted?");

        // D5 — keep ONE StoreRoleFindingActivity persisting the accepted review to the vector
        // store so the CONTEXT.STORE_ROLE.* family continues at its equivalent transition.
        var storeAggregateReview = new StoreRoleFindingActivity
        {
            Id = "StoreAggregateReview", Name = "Store Aggregate Plan Review",
            Repository = new(ctx => repository.Get(ctx)),
            IssueNumber = new(ctx => issueNumber.Get(ctx)),
            Role = new("plan-review"),
            FindingsJson = new(ctx => JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["decision"] = legacyDecision.Get(ctx),
                ["documentId"] = exitDocId.Get(ctx),
                ["notes"] = decisionNotes.Get(ctx),
            })),
            ContextId = new(new Elsa.Workflows.Memory.Variable<string>()),
        };
        storeAggregateReview.SetDisplayText("Store Aggregate Plan Review");

        // ── Step 7: Expose output — the single terminal region (D2, AC3) ──
        var exposeOutput = new Sequence
        {
            Id = "ExposeOutput", Name = "Expose Output",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutputPlan", Name = "Output Plan", OutputName = new("planJson"), OutputValue = new(ctx => (object)(planJson.Get(ctx) ?? "")) }, "Output Plan"),
                WithLabel(new SetOutput { Id = "OutputError", Name = "Output Error", OutputName = new("error"), OutputValue = new(ctx => (object)(outputError.Get(ctx) ?? "")) }, "Output Error"),
                WithLabel(new SetOutput { Id = "OutputStatus", Name = "Output Status", OutputName = new("status"), OutputValue = new(ctx => (object)(outputStatus.Get(ctx) ?? "")) }, "Output Status"),
                WithLabel(new SetOutput { Id = "OutputOutcome", Name = "Output Outcome", OutputName = new("outcome"), OutputValue = new(ctx => (object)(exitOutcome.Get(ctx) ?? "")) }, "Output Outcome"),
                WithLabel(new SetOutput { Id = "OutputDocumentId", Name = "Output Document Id", OutputName = new("documentId"), OutputValue = new(ctx => (object)(exitDocId.Get(ctx) ?? "")) }, "Output Document Id"),
                WithLabel(new SetOutput { Id = "OutputDecision", Name = "Output Decision", OutputName = new("decision"), OutputValue = new(ctx => (object)(legacyDecision.Get(ctx) ?? "")) }, "Output Decision"),
                WithLabel(new SetOutput { Id = "OutputReviewNotes", Name = "Output Review Notes", OutputName = new("reviewNotes"), OutputValue = new(ctx => (object)(decisionNotes.Get(ctx) ?? "")) }, "Output Review Notes"),
            }
        };
        exposeOutput.SetDisplayText("Expose Output");

        // ── Build the flowchart ────────────────────────────────────────
        builder.Root = new Flowchart
        {
            Id = "PlanGenerationFlowchart",
            Name = "Plan Generation Flowchart",
            Start = readInputs,
            Activities =
            {
                readInputs, computeReEntry, readPositionStage, freshRun,
                fetchDecomposition, fetchAmbiguityAssessment, dispatchLifecycle, readLifecycleExit,
                lifecycleAcceptedGate, storeAggregateReview, exposeOutput,
            },
            Connections =
            {
                new(readInputs, computeReEntry),
                new(computeReEntry, readPositionStage),
                new(readPositionStage, freshRun),

                // Fresh run → fetch the consumed decomposition → ambiguity fetch → dispatch.
                new(new FlowEndpoint(freshRun, "True"),  new FlowEndpoint(fetchDecomposition)),
                new(fetchDecomposition, fetchAmbiguityAssessment),
                // Re-entry → ambiguity fetch → dispatch (the consumed-decomposition fetch is
                // still skipped on re-entry, D9; the 39-25 score fetch runs on EVERY path that
                // dispatches, as the single predecessor of the dispatch).
                new(new FlowEndpoint(freshRun, "False"), new FlowEndpoint(fetchAmbiguityAssessment)),
                new(fetchAmbiguityAssessment, dispatchLifecycle),

                new(dispatchLifecycle, readLifecycleExit),
                new(readLifecycleExit, lifecycleAcceptedGate),

                // Accepted → persist the aggregate review → expose outputs.
                new(new FlowEndpoint(lifecycleAcceptedGate, "True"),  new FlowEndpoint(storeAggregateReview)),
                new(storeAggregateReview, exposeOutput),
                // Not accepted → planJson="" so the parent's empty-plan edge fires (D1).
                new(new FlowEndpoint(lifecycleAcceptedGate, "False"), new FlowEndpoint(exposeOutput)),
            }
        };
    }
}
