using System.Globalization;
using System.Text.Json;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using Tamma.Activities;
using Tamma.Activities.Documents;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;
using Tamma.Core.Documents.Resume;
using Tamma.Core.Documents.Types;
using Tamma.ElsaServer.Workflows.Helpers;
using ExprContext = Elsa.Expressions.Models.ExpressionExecutionContext;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Story 39-6 — the generic document lifecycle sub-workflow
/// (<c>DefinitionId = "document-lifecycle"</c>). Runs
/// <c>PRODUCE → VALIDATE (bounded repair) → REVIEW → REVISE (bounded) → ACCEPT</c>
/// for ANY registered document type, driven purely by inputs: a producer dispatch
/// spec (<c>producerRole</c> + <c>producerAction</c> + <c>producerVariablesJson</c>),
/// a document type key, lineage anchors, and the resolved acceptance rules.
///
/// <para>All decision logic lives in the pure <see cref="DocumentLifecycleHelper"/>
/// (D1) — this graph only ROUTES. Every transition emits a <c>DOCUMENT.*</c> DCB
/// event. The ACCEPT stage builds an <see cref="AcceptanceRequest"/>, publishes it
/// through <see cref="PublishAcceptanceRequestActivity"/>, and suspends on 39-8's
/// <see cref="WaitForDocumentDecisionActivity"/> — it contains NO accept-decision
/// <c>llm-call</c> and NO branch that skips the decision (D5). The workflow exits as
/// <c>accepted</c>, <c>rejected</c>, or one of the four typed <c>escalated</c>
/// outcomes, each carrying full lineage (D7); parents switch on <c>status</c> first.</para>
///
/// <para><b>Story 39-10 (D6).</b> Resumable by construction: it SUSPENDS on the
/// canonical <see cref="WaitForDocumentDecisionActivity"/> bookmark AND re-enters from
/// the latest accepted state after a crash — declared <c>[ResumeBehavior(Both)]</c> and
/// enforced by <c>ResumableStandardStructuralTests</c>. Init consults
/// <see cref="ComputeReEntryPositionActivity"/> and routes idempotent guards:
/// <c>Complete</c> short-circuits to the accepted terminal (emitting
/// <c>DOCUMENT.REENTERED</c>, never a second <c>DOCUMENT.ACCEPTED</c>); <c>Review</c>
/// skips produce/validate; <c>Accept</c> re-suspends on the recovered session; <c>Produce</c>
/// runs fresh.</para>
/// </summary>
[ResumeBehavior(ResumeMode.Both, SuspendActivities = new[] { typeof(WaitForDocumentDecisionActivity) })]
public class DocumentLifecycleWorkflow : WorkflowBase
{
    /// <summary>The stable review-producer definition-id contract 39-7 adopts (D10).</summary>
    private const string DefaultReviewDefinitionId = "document-review";

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Document Lifecycle";
        builder.DefinitionId = "document-lifecycle";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description =
            "Generic produce → validate (bounded repair) → review → revise (bounded) → accept " +
            "lifecycle for any registered document type";

        // ── Loop state (D1 — ONE variable) ─────────────────────────────
        var stateJson = builder.WithVariable<string>("LifecycleState", "");

        // ── Plain producer-dispatch vars (default "" so the drift scanner
        //    materialises the three llm-call dispatches as DATA-DRIVEN, D3) ──
        var producerRole = builder.WithVariable<string>("ProducerRole", "");
        var producerAction = builder.WithVariable<string>("ProducerAction", "");
        var producerVariablesJson = builder.WithVariable<string>("ProducerVariablesJson", "{}");
        var documentType = builder.WithVariable<string>("DocumentType", "");
        var issueId = builder.WithVariable<string>("IssueId", "");
        var correlationId = builder.WithVariable<string>("CorrelationId", "");
        var reviewDefId = builder.WithVariable<string>("ReviewDefinitionId", DefaultReviewDefinitionId);
        var tenantId = builder.WithVariable<string>("TenantId", "");

        // ── Denormalised scalars for emit tags / gate inputs ───────────
        var sessionId = builder.WithVariable<string>("SessionId", "");
        var currentDocId = builder.WithVariable<string>("CurrentDocumentId", "");
        var currentDocJson = builder.WithVariable<string>("CurrentDocumentJson", "");
        var currentRound = builder.WithVariable<int>("CurrentRound", 0);
        var rulesReference = builder.WithVariable<string>("RulesReference", "");
        var acceptRequestedAtUtc = builder.WithVariable<string>("AcceptRequestedAtUtc", "");
        var acceptanceRequestJson = builder.WithVariable<string>("AcceptanceRequestJson", "");

        // ── Dispatch result containers ─────────────────────────────────
        var produceResult = builder.WithVariable<IDictionary<string, object>?>();
        var repairResult = builder.WithVariable<IDictionary<string, object>?>();
        var reviseResult = builder.WithVariable<IDictionary<string, object>?>();
        var reviewResult = builder.WithVariable<IDictionary<string, object>?>();

        // ── Routing flags ──────────────────────────────────────────────
        var producedOk = builder.WithVariable<bool>("ProducedOk", false);
        var lastDispatchOk = builder.WithVariable<bool>("LastDispatchOk", false);
        var validationOk = builder.WithVariable<bool>("ValidationOk", false);
        var ambiguityOver = builder.WithVariable<bool>("AmbiguityOver", false);
        var reviewRoute = builder.WithVariable<string>("ReviewRoute", "");
        var clampedRoute = builder.WithVariable<string>("ClampedRoute", "");
        var escalateOutcome = builder.WithVariable<string>("EscalateOutcome", "");
        var reviewDecisionWire = builder.WithVariable<string>("ReviewDecisionWire", "approve");
        var reviewHasBlocking = builder.WithVariable<bool>("ReviewHasBlocking", false);

        // ── Gate outputs ───────────────────────────────────────────────
        var decisionJson = builder.WithVariable<string>("DecisionJson", "");
        var decisionChannel = builder.WithVariable<string>("DecisionChannel", "orchestrator");

        // ── Story 39-10 re-entry (D6) ──────────────────────────────────
        var reEntryPositionJson = builder.WithVariable<string>("ReEntryPositionJson", "");
        var reEntryDocJson = builder.WithVariable<string>("ReEntryDocumentJson", "");
        var reEntryStage = builder.WithVariable<string>("ReEntryStage", "produce");

        // ── Terminal outputs ───────────────────────────────────────────
        var outStatus = builder.WithVariable<string>("OutStatus", "");
        var outOutcome = builder.WithVariable<string>("OutOutcome", "");
        var outDocId = builder.WithVariable<string>("OutDocId", "");
        var outLifecycleResult = builder.WithVariable<string>("OutLifecycleResult", "{}");
        // Story 39-12 (D4) — the accepted revision's PAYLOAD body (not the envelope, not the
        // lineage). A lifecycle binding (e.g. IssueDecompositionWorkflow) needs the typed body to
        // project its own domain output (subtask count, decomposition JSON); the lineage on
        // lifecycleResult only carries id+state. Empty when no draft was ever produced.
        var outDocJson = builder.WithVariable<string>("OutDocJson", "");

        // ================================================================
        // Init — read + validate inputs (D2/D4), mint session, seed state
        // ================================================================
        var init = new SetVariable
        {
            Id = "Init", Name = "Init",
            Variable = stateJson,
            Value = new(ctx =>
            {
                var role = FirstNonEmpty(ctx.GetInput<string>("producerRole"), ctx.GetInput<string>("agentRole"));
                var action = ctx.GetInput<string>("producerAction") ?? ctx.GetInput<string>("action") ?? "";
                var variablesJson = ctx.GetInput<string>("producerVariablesJson") ?? "{}";
                var typeKey = ctx.GetInput<string>("documentType") ?? "";
                var issue = ctx.GetInput<string>("issueId") ?? "";
                var corr = ctx.GetInput<string>("correlationId") ?? "";
                var feedbackVar = ctx.GetInput<string>("feedbackVariableName") ?? DocumentLifecycleHelper.DefaultFeedbackVariable;
                var rulesInput = ctx.GetInput<string>("acceptanceRulesJson") ?? "";
                var reviewDef = FirstNonEmpty(ctx.GetInput<string>("reviewWorkflowDefinitionId"), DefaultReviewDefinitionId);
                var tenant = ctx.GetInput<string>("tenantId") ?? "";
                double? ambiguityScore = TryGetDouble(ctx.GetInput<object>("ambiguityScore"));

                var sid = ctx.GetInput<Guid>("sessionId");
                if (sid == Guid.Empty) sid = UuidV7.NewGuid();

                var rules = DocumentLifecycleHelper.ResolveRules(rulesInput, typeKey, DateTimeOffset.UtcNow);

                // Fail-loud producer spec + type key validation (D2/AC1).
                var state = DocumentLifecycleHelper.Init(
                    role, action, variablesJson, typeKey, issue, corr, sid, feedbackVar, ambiguityScore, rules);

                producerRole.Set(ctx, role);
                producerAction.Set(ctx, action);
                producerVariablesJson.Set(ctx, string.IsNullOrWhiteSpace(variablesJson) ? "{}" : variablesJson);
                documentType.Set(ctx, typeKey);
                issueId.Set(ctx, issue);
                correlationId.Set(ctx, corr);
                reviewDefId.Set(ctx, reviewDef);
                tenantId.Set(ctx, tenant);
                sessionId.Set(ctx, sid.ToString());
                currentRound.Set(ctx, 0);
                rulesReference.Set(ctx, state.RulesReference);

                return DocumentLifecycleHelper.Serialize(state);
            })
        };
        init.SetDisplayText("Init");

        // ================================================================
        // RE-ENTRY (39-10, D6) — reconstruct resume position + idempotent guards
        // ================================================================
        var computeReEntry = new ComputeReEntryPositionActivity
        {
            Id = "ComputeReEntryPosition", Name = "Compute Re-Entry Position",
            IssueId = new(ctx => issueId.Get(ctx)),
            DocumentType = new(ctx => documentType.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            CorrelationId = new(ctx => correlationId.Get(ctx)),
            PositionJson = new(reEntryPositionJson),
            ExistingDocumentJson = new(reEntryDocJson),
        };
        computeReEntry.SetDisplayText("Compute Re-Entry Position");

        var applyReEntry = new SetVariable
        {
            Id = "ApplyReEntry", Name = "Apply Re-Entry",
            Variable = reEntryStage,
            Value = new(ctx =>
            {
                var position = DocumentLifecycleHelper.DeserializeReEntryPosition(reEntryPositionJson.Get(ctx));
                if (position is null)
                    return "produce";

                var existingJson = reEntryDocJson.Get(ctx);
                DocumentEnvelope? existing = null;
                if (!string.IsNullOrWhiteSpace(existingJson))
                {
                    try { existing = DocumentJson.Deserialize(existingJson); }
                    catch (JsonException) { existing = null; }
                }

                var state = DocumentLifecycleHelper.Deserialize(stateJson.Get(ctx));
                state = DocumentLifecycleHelper.ApplyReEntry(state, position, existing);
                stateJson.Set(ctx, DocumentLifecycleHelper.Serialize(state));
                UpdateCurrent(ctx, state, currentDocId, currentDocJson, currentRound);

                // Accept re-entry re-suspends on the SAME recovered decision session (D6).
                if (position.ResumeAt == LifecycleResumeStage.Accept &&
                    position.PendingDecisionSessionId is Guid recovered && recovered != Guid.Empty)
                    sessionId.Set(ctx, recovered.ToString());

                return position.ResumeAt switch
                {
                    LifecycleResumeStage.Complete => "complete",
                    LifecycleResumeStage.Accept => "accept",
                    LifecycleResumeStage.Review => "review",
                    _ => "produce",
                };
            })
        };
        applyReEntry.SetDisplayText("Apply Re-Entry");

        var reEntryCompleteGate = new FlowDecision(ctx => reEntryStage.Get(ctx) == "complete")
        { Id = "ReEntryCompleteGate", Name = "Already Accepted?" };
        reEntryCompleteGate.SetDisplayText("Already Accepted?");
        var reEntryReviewGate = new FlowDecision(ctx => reEntryStage.Get(ctx) == "review")
        { Id = "ReEntryReviewGate", Name = "Re-enter At Review?" };
        reEntryReviewGate.SetDisplayText("Re-enter At Review?");
        var reEntryAcceptGate = new FlowDecision(ctx => reEntryStage.Get(ctx) == "accept")
        { Id = "ReEntryAcceptGate", Name = "Re-enter At Accept?" };
        reEntryAcceptGate.SetDisplayText("Re-enter At Accept?");

        // ================================================================
        // PRODUCE — dispatch llm-call (agentRole/action/variables + documentType/issueId)
        // ================================================================
        var dispatchProduce = LlmDispatch(
            "DispatchProduce", "Dispatch Produce",
            producerRole, producerAction, producerVariablesJson, documentType, issueId, tenantId, produceResult);

        var ingestProduce = new SetVariable
        {
            Id = "IngestProduce", Name = "Ingest Produce",
            Variable = stateJson,
            Value = new(ctx =>
            {
                var state = DocumentLifecycleHelper.Deserialize(stateJson.Get(ctx));
                var (newState, ok, _) = IngestDraft(produceResult.Get(ctx), state, isRevise: false);
                producedOk.Set(ctx, ok);
                lastDispatchOk.Set(ctx, ok);
                UpdateCurrent(ctx, newState, currentDocId, currentDocJson, currentRound);
                return DocumentLifecycleHelper.Serialize(newState);
            })
        };
        ingestProduce.SetDisplayText("Ingest Produce");

        var emitProduced = new EmitDocumentEventActivity
        {
            Id = "EmitProduced", Name = "Emit Produced",
            EventType = new(ctx => producedOk.Get(ctx) ? DocumentEvents.ProducedSuccess : DocumentEvents.ProducedFailed),
            DocumentId = new(ctx => currentDocId.Get(ctx)),
            DocumentType = new(ctx => documentType.Get(ctx)),
            Round = new(ctx => currentRound.Get(ctx)),
            IssueId = new(ctx => issueId.Get(ctx)),
            CorrelationId = new(ctx => correlationId.Get(ctx)),
            SessionId = new(ctx => sessionId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Detail = new(ctx => producedOk.Get(ctx) ? "Draft produced" : "Producer llm-call failed or unparseable"),
        };
        emitProduced.SetDisplayText("Emit Produced");

        // ================================================================
        // VALIDATE — the type's deterministic validator (shared loop target)
        // ================================================================
        var validateDraft = new SetVariable
        {
            Id = "ValidateDraft", Name = "Validate Draft",
            Variable = validationOk,
            Value = new(ctx =>
            {
                var state = DocumentLifecycleHelper.Deserialize(stateJson.Get(ctx));
                bool ok;
                if (!lastDispatchOk.Get(ctx) || state.Current is null)
                {
                    state = DocumentLifecycleHelper.WithViolations(state, new[]
                    {
                        new DocumentViolation("PRODUCE_FAILED",
                            "The producer did not return a parseable document payload for this turn."),
                    });
                    ok = false;
                }
                else
                {
                    var result = DocumentTypeRegistry.Resolve(state.TypeKey).Validate(state.Current.Payload);
                    state = DocumentLifecycleHelper.WithViolations(state, result.Violations);
                    ok = result.IsValid;
                    if (ok)
                        state = DocumentLifecycleHelper.TransitionCurrent(state, DocumentState.Validated, DateTimeOffset.UtcNow);
                }
                UpdateCurrent(ctx, state, currentDocId, currentDocJson, currentRound);
                stateJson.Set(ctx, DocumentLifecycleHelper.Serialize(state));
                return ok;
            })
        };
        validateDraft.SetDisplayText("Validate Draft");

        var emitValidated = new EmitDocumentEventActivity
        {
            Id = "EmitValidated", Name = "Emit Validated",
            EventType = new(ctx => validationOk.Get(ctx) ? DocumentEvents.ValidatedSuccess : DocumentEvents.ValidatedFailed),
            DocumentId = new(ctx => currentDocId.Get(ctx)),
            DocumentType = new(ctx => documentType.Get(ctx)),
            Round = new(ctx => currentRound.Get(ctx)),
            IssueId = new(ctx => issueId.Get(ctx)),
            CorrelationId = new(ctx => correlationId.Get(ctx)),
            SessionId = new(ctx => sessionId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Detail = new(ctx => validationOk.Get(ctx) ? "Draft validated" : "Draft failed deterministic validation"),
        };
        emitValidated.SetDisplayText("Emit Validated");

        var validationGate = new FlowDecision(ctx => validationOk.Get(ctx))
        { Id = "ValidationGate", Name = "Valid?" };
        validationGate.SetDisplayText("Valid?");

        // ── Repair ring (OUTER) ────────────────────────────────────────
        var repairCheck = new FlowDecision(ctx =>
            DocumentLifecycleHelper.ShouldRepair(DocumentLifecycleHelper.Deserialize(stateJson.Get(ctx))))
        { Id = "RepairCheck", Name = "Can Repair?" };
        repairCheck.SetDisplayText("Can Repair?");

        var prepareRepair = new SetVariable
        {
            Id = "PrepareRepair", Name = "Prepare Repair",
            Variable = producerVariablesJson,
            Value = new(ctx =>
            {
                var state = DocumentLifecycleHelper.Deserialize(stateJson.Get(ctx));
                state = DocumentLifecycleHelper.IncrementRepairAttempts(state);
                var vars = DocumentLifecycleHelper.BuildRepairVariables(
                    state.ProducerVariablesJson, state.LastViolations, state.FeedbackVariableName);
                stateJson.Set(ctx, DocumentLifecycleHelper.Serialize(state));
                currentRound.Set(ctx, state.Round);
                return vars;
            })
        };
        prepareRepair.SetDisplayText("Prepare Repair");

        var dispatchRepair = LlmDispatch(
            "DispatchRepair", "Dispatch Repair",
            producerRole, producerAction, producerVariablesJson, documentType, issueId, tenantId, repairResult);

        var ingestRepair = new SetVariable
        {
            Id = "IngestRepair", Name = "Ingest Repair",
            Variable = stateJson,
            Value = new(ctx =>
            {
                var state = DocumentLifecycleHelper.Deserialize(stateJson.Get(ctx));
                var (newState, ok, _) = IngestDraft(repairResult.Get(ctx), state, isRevise: false);
                lastDispatchOk.Set(ctx, ok);
                UpdateCurrent(ctx, newState, currentDocId, currentDocJson, currentRound);
                return DocumentLifecycleHelper.Serialize(newState);
            })
        };
        ingestRepair.SetDisplayText("Ingest Repair");

        // ================================================================
        // AMBIGUITY (D8) — post-validate escalation short-circuit
        // ================================================================
        var ambiguityCheck = new SetVariable
        {
            Id = "AmbiguityCheck", Name = "Ambiguity Check",
            Variable = ambiguityOver,
            Value = new(ctx =>
            {
                var state = DocumentLifecycleHelper.Deserialize(stateJson.Get(ctx));
                var payloadJson = state.Current?.Payload.GetRawText();
                return DocumentLifecycleHelper.IsAmbiguityAboveThreshold(
                    state.TypeKey, payloadJson, state.Rules.Rules, state.AmbiguityScore);
            })
        };
        ambiguityCheck.SetDisplayText("Ambiguity Check");

        var ambiguityGate = new FlowDecision(ctx => ambiguityOver.Get(ctx))
        { Id = "AmbiguityGate", Name = "Ambiguity Over Threshold?" };
        ambiguityGate.SetDisplayText("Ambiguity Over Threshold?");

        // ================================================================
        // REVIEW — dispatch the 39-7 producer (variable-backed definition id, D10)
        // ================================================================
        var emitReviewRequested = DocEvent(
            "EmitReviewRequested", "Emit Review Requested", DocumentEvents.ReviewRequested,
            currentDocId, documentType, currentRound, issueId, correlationId, sessionId, tenantId,
            "Review requested");

        var dispatchReview = new DispatchWorkflow
        {
            Id = "DispatchReview", Name = "Dispatch Review",
            WorkflowDefinitionId = new(ctx => reviewDefId.Get(ctx)),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["documentJson"] = currentDocJson.Get(ctx) ?? "",
                ["documentType"] = documentType.Get(ctx) ?? "",
                ["issueId"] = issueId.Get(ctx) ?? "",
                ["correlationId"] = correlationId.Get(ctx) ?? "",
                ["tenantId"] = tenantId.Get(ctx) ?? "",
                ["acceptanceRulesJson"] = AcceptanceRulesJson.Serialize(
                    DocumentLifecycleHelper.Deserialize(stateJson.Get(ctx)).Rules.Rules),
            }),
            WaitForCompletion = new(true),
            Result = new(reviewResult),
        };
        dispatchReview.SetDisplayText("Dispatch Review");

        var ingestReview = new SetVariable
        {
            Id = "IngestReview", Name = "Ingest Review",
            Variable = reviewRoute,
            Value = new(ctx =>
            {
                var state = DocumentLifecycleHelper.Deserialize(stateJson.Get(ctx));
                var reviewJson = ReadReviewJson(reviewResult.Get(ctx));

                var facts = DocumentLifecycleHelper.ExtractReviewFacts(reviewJson);
                reviewDecisionWire.Set(ctx, facts.Decision.ToWire());
                reviewHasBlocking.Set(ctx, facts.HasBlockingIssues);

                // Build + append the review envelope, transition current → Reviewed.
                if (facts.Usable && !string.IsNullOrWhiteSpace(reviewJson) && state.Current is not null)
                {
                    var reviewEnvelope = BuildReviewEnvelope(state, reviewJson!, reviewDefId.Get(ctx));
                    state = DocumentLifecycleHelper.AppendReview(state, reviewEnvelope);
                    state = DocumentLifecycleHelper.TransitionCurrent(state, DocumentState.Reviewed, DateTimeOffset.UtcNow);
                }

                var route = DocumentLifecycleHelper.ComputeReviewRoute(state, reviewJson);
                UpdateCurrent(ctx, state, currentDocId, currentDocJson, currentRound);
                stateJson.Set(ctx, DocumentLifecycleHelper.Serialize(state));
                return route switch
                {
                    DocumentLifecycleHelper.ReviewRoute.Accept => "accept",
                    DocumentLifecycleHelper.ReviewRoute.Revise => "revise",
                    DocumentLifecycleHelper.ReviewRoute.RoundsExhausted => "rounds-exhausted",
                    _ => "undecidable",
                };
            })
        };
        ingestReview.SetDisplayText("Ingest Review");

        var emitReviewed = DocEvent(
            "EmitReviewed", "Emit Reviewed", DocumentEvents.Reviewed,
            currentDocId, documentType, currentRound, issueId, correlationId, sessionId, tenantId,
            "Review landed");

        var routeAccept = new FlowDecision(ctx => reviewRoute.Get(ctx) == "accept")
        { Id = "RouteAccept", Name = "Approved?" };
        routeAccept.SetDisplayText("Approved?");
        var routeRevise = new FlowDecision(ctx => reviewRoute.Get(ctx) == "revise")
        { Id = "RouteRevise", Name = "Revise?" };
        routeRevise.SetDisplayText("Revise?");
        var routeRounds = new FlowDecision(ctx => reviewRoute.Get(ctx) == "rounds-exhausted")
        { Id = "RouteRounds", Name = "Rounds Exhausted?" };
        routeRounds.SetDisplayText("Rounds Exhausted?");

        // ================================================================
        // REVISE — mint a new superseding draft (D9), bounded
        // ================================================================
        var emitRevisionStarted = DocEvent(
            "EmitRevisionStarted", "Emit Revision Started", DocumentEvents.RevisionStarted,
            currentDocId, documentType, currentRound, issueId, correlationId, sessionId, tenantId,
            "Revision started");

        var prepareRevision = new SetVariable
        {
            Id = "PrepareRevision", Name = "Prepare Revision",
            Variable = producerVariablesJson,
            Value = new(ctx =>
            {
                var state = DocumentLifecycleHelper.Deserialize(stateJson.Get(ctx));
                state = DocumentLifecycleHelper.IncrementRound(state);
                var reviewJson = state.Reviews.Count == 0 ? null : state.Reviews[^1].Payload.GetRawText();
                var vars = DocumentLifecycleHelper.BuildRevisionVariables(
                    state.ProducerVariablesJson, reviewJson, state.FeedbackVariableName);
                // On a repair-then-revise the producer var base is the original spec.
                stateJson.Set(ctx, DocumentLifecycleHelper.Serialize(state));
                currentRound.Set(ctx, state.Round);
                return vars;
            })
        };
        prepareRevision.SetDisplayText("Prepare Revision");

        var dispatchRevise = LlmDispatch(
            "DispatchRevise", "Dispatch Revise",
            producerRole, producerAction, producerVariablesJson, documentType, issueId, tenantId, reviseResult);

        var ingestRevise = new SetVariable
        {
            Id = "IngestRevise", Name = "Ingest Revise",
            Variable = stateJson,
            Value = new(ctx =>
            {
                var state = DocumentLifecycleHelper.Deserialize(stateJson.Get(ctx));
                var (newState, ok, _) = IngestDraft(reviseResult.Get(ctx), state, isRevise: true);
                lastDispatchOk.Set(ctx, ok);
                UpdateCurrent(ctx, newState, currentDocId, currentDocJson, currentRound);
                return DocumentLifecycleHelper.Serialize(newState);
            })
        };
        ingestRevise.SetDisplayText("Ingest Revise");

        // ================================================================
        // ACCEPT — publish + ONE gate (D5). No FlowDecision between them.
        // ================================================================
        var buildAcceptanceRequest = new SetVariable
        {
            Id = "BuildAcceptanceRequest", Name = "Build Acceptance Request",
            Variable = acceptanceRequestJson,
            Value = new(ctx =>
            {
                var state = DocumentLifecycleHelper.Deserialize(stateJson.Get(ctx));
                // D4/39-8 — stamp the request time (durationMs basis; fail-loud if missing).
                acceptRequestedAtUtc.Set(ctx, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));

                var request = new AcceptanceRequest
                {
                    DecisionSessionId = state.SessionId,
                    Document = state.Current!,
                    Review = state.Reviews[^1],
                    Lineage = state.Drafts,
                    RoundsUsed = state.Round,
                    Rules = state.Rules,
                    IssueId = state.IssueId,
                };
                return JsonSerializer.Serialize(request, DocumentJson.Options);
            })
        };
        buildAcceptanceRequest.SetDisplayText("Build Acceptance Request");

        var publishRequest = new PublishAcceptanceRequestActivity
        {
            Id = "PublishAcceptanceRequest", Name = "Publish Acceptance Request",
            RequestJson = new(ctx => acceptanceRequestJson.Get(ctx)),
        };
        publishRequest.SetDisplayText("Publish Acceptance Request");

        var waitForDecision = new WaitForDocumentDecisionActivity
        {
            Id = "WaitForDocumentDecision", Name = "Wait For Document Decision",
            SessionId = new(ctx => Guid.TryParse(sessionId.Get(ctx), out var g) ? g : Guid.Empty),
            TenantId = new(ctx => tenantId.Get(ctx)),
            RequestedAtUtc = new(ctx => acceptRequestedAtUtc.Get(ctx)),
            RulesReference = new(ctx => rulesReference.Get(ctx)),
            IssueId = new(ctx => issueId.Get(ctx)),
            DocumentId = new(ctx => currentDocId.Get(ctx)),
            DocumentType = new(ctx => documentType.Get(ctx)),
            CorrelationId = new(ctx => correlationId.Get(ctx)),
            DecisionJson = new(decisionJson),
            Channel = new(decisionChannel),
        };
        waitForDecision.SetDisplayText("Wait For Document Decision");

        var applyGuardrails = new SetVariable
        {
            Id = "ApplyGuardrails", Name = "Apply Guardrails",
            Variable = clampedRoute,
            Value = new(ctx =>
            {
                var state = DocumentLifecycleHelper.Deserialize(stateJson.Get(ctx));
                var proposed = ParseDecision(decisionJson.Get(ctx));
                var channel = ParseChannel(decisionChannel.Get(ctx));
                var facts = new ReviewFacts(ParseReviewDecision(reviewDecisionWire.Get(ctx)), reviewHasBlocking.Get(ctx));

                var gateCtx = new AcceptanceGateContext(
                    DocumentType: DocumentTypeKeyExtensions.Parse(state.TypeKey),
                    AgentActionWire: producerAction.Get(ctx),
                    Review: facts,
                    RoundsUsed: state.Round,
                    Rules: state.Rules.Rules,
                    DeciderChannel: channel);

                var clamped = AcceptanceGuardrails.Clamp(proposed, gateCtx);
                switch (clamped)
                {
                    case AcceptanceDecision.Accept:
                        return "accept";
                    case AcceptanceDecision.RequestRevision:
                        return "revise";
                    case AcceptanceDecision.Reject:
                        return "reject";
                    case AcceptanceDecision.Escalate esc:
                        escalateOutcome.Set(ctx, DocumentLifecycleHelper.OutcomeForEscalationReason(esc.Reason).ToWire());
                        return "escalate";
                    default:
                        escalateOutcome.Set(ctx, DocumentLifecycleOutcome.ReviewUndecidable.ToWire());
                        return "escalate";
                }
            })
        };
        applyGuardrails.SetDisplayText("Apply Guardrails");

        var acceptGate = new FlowDecision(ctx => clampedRoute.Get(ctx) == "accept")
        { Id = "AcceptGate", Name = "Accept?" };
        acceptGate.SetDisplayText("Accept?");
        var rejectGate = new FlowDecision(ctx => clampedRoute.Get(ctx) == "reject")
        { Id = "RejectGate", Name = "Reject?" };
        rejectGate.SetDisplayText("Reject?");
        var reviseGate = new FlowDecision(ctx => clampedRoute.Get(ctx) == "revise")
        { Id = "ReviseGate", Name = "Request Revision?" };
        reviseGate.SetDisplayText("Request Revision?");

        // ================================================================
        // Terminal builders
        // ================================================================
        // Escalate-outcome seeds (each sets the wire outcome then → EmitEscalated).
        var seedValidationExhausted = SeedEscalate("SeedValidationExhausted", "Seed Validation Exhausted",
            escalateOutcome, DocumentLifecycleOutcome.ValidationExhausted);
        var seedAmbiguity = SeedEscalate("SeedAmbiguity", "Seed Ambiguity",
            escalateOutcome, DocumentLifecycleOutcome.AmbiguityAboveThreshold);
        var seedRounds = SeedEscalate("SeedRoundsExhausted", "Seed Rounds Exhausted",
            escalateOutcome, DocumentLifecycleOutcome.RoundsExhausted);
        var seedUndecidable = SeedEscalate("SeedReviewUndecidable", "Seed Review Undecidable",
            escalateOutcome, DocumentLifecycleOutcome.ReviewUndecidable);

        var emitAccepted = TerminalEmit("EmitAccepted", "Emit Accepted", DocumentEvents.Accepted,
            currentDocId, documentType, currentRound, issueId, correlationId, sessionId, tenantId, escalateOutcome, "Document accepted");
        var emitRejected = TerminalEmit("EmitRejected", "Emit Rejected", DocumentEvents.Rejected,
            currentDocId, documentType, currentRound, issueId, correlationId, sessionId, tenantId, escalateOutcome, "Document rejected (human decision)");
        var emitEscalated = TerminalEmit("EmitEscalated", "Emit Escalated", DocumentEvents.Escalated,
            currentDocId, documentType, currentRound, issueId, correlationId, sessionId, tenantId, escalateOutcome, "Document escalated");

        var finalizeAccepted = Finalize("FinalizeAccepted", "Finalize Accepted",
            stateJson, DocumentState.Accepted, outStatus, outOutcome, outDocId, outLifecycleResult, outDocJson,
            currentDocId, currentDocJson, currentRound, TerminalKind.Accepted, escalateOutcome);
        var finalizeRejected = Finalize("FinalizeRejected", "Finalize Rejected",
            stateJson, DocumentState.Rejected, outStatus, outOutcome, outDocId, outLifecycleResult, outDocJson,
            currentDocId, currentDocJson, currentRound, TerminalKind.Rejected, escalateOutcome);
        var finalizeEscalated = Finalize("FinalizeEscalated", "Finalize Escalated",
            stateJson, DocumentState.Escalated, outStatus, outOutcome, outDocId, outLifecycleResult, outDocJson,
            currentDocId, currentDocJson, currentRound, TerminalKind.Escalated, escalateOutcome);

        // 39-10 (D6) — Complete short-circuit terminal: the document of this type is ALREADY
        // accepted. Emits NO second DOCUMENT.ACCEPTED (DOCUMENT.REENTERED was emitted by
        // ComputeReEntryPosition) and does NOT re-transition the already-accepted envelope.
        var finalizeReenteredComplete = new SetVariable
        {
            Id = "FinalizeReenteredComplete", Name = "Finalize Reentered Complete",
            Variable = outStatus,
            Value = new(ctx =>
            {
                var state = DocumentLifecycleHelper.Deserialize(stateJson.Get(ctx));
                var docId = state.Current?.Id ?? Guid.Empty;
                var result = DocumentLifecycleHelper.BuildAccepted(state, docId);
                outOutcome.Set(ctx, result.Outcome?.ToWire() ?? "");
                outDocId.Set(ctx, result.DocumentId?.ToString() ?? "");
                outLifecycleResult.Set(ctx, JsonSerializer.Serialize(result, DocumentJson.Options));
                // D4 — surface the already-accepted revision's payload on the short-circuit path too.
                outDocJson.Set(ctx, state.Current is null ? "" : state.Current.Payload.GetRawText());
                return result.Status;
            })
        };
        finalizeReenteredComplete.SetDisplayText("Finalize Reentered Complete");

        var setOutputs = new Sequence
        {
            Id = "SetOutputs", Name = "Set Outputs",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutputStatus", OutputName = new("status"), OutputValue = new(ctx => (object)outStatus.Get(ctx)) }, "Output status"),
                WithLabel(new SetOutput { Id = "OutputOutcome", OutputName = new("outcome"), OutputValue = new(ctx => (object)(outOutcome.Get(ctx) ?? "")) }, "Output outcome"),
                WithLabel(new SetOutput { Id = "OutputDocumentId", OutputName = new("documentId"), OutputValue = new(ctx => (object)(outDocId.Get(ctx) ?? "")) }, "Output documentId"),
                WithLabel(new SetOutput { Id = "OutputLifecycleResult", OutputName = new("lifecycleResult"), OutputValue = new(ctx => (object)(outLifecycleResult.Get(ctx) ?? "{}")) }, "Output lifecycleResult"),
                // Story 39-12 (D4) — the accepted revision's payload body, for lifecycle bindings.
                WithLabel(new SetOutput { Id = "OutputDocumentJson", OutputName = new("documentJson"), OutputValue = new(ctx => (object)(outDocJson.Get(ctx) ?? "")) }, "Output documentJson"),
                WithLabel(new SetOutput { Id = "OutputSessionId", OutputName = new("sessionId"), OutputValue = new(ctx => (object)(sessionId.Get(ctx) ?? "")) }, "Output sessionId"),
            }
        };
        setOutputs.SetDisplayText("Set Outputs");

        var finish = new Finish { Id = "Finish", Name = "Finish" };
        finish.SetDisplayText("Finish");

        // ================================================================
        // Flowchart wiring
        // ================================================================
        builder.Root = new Flowchart
        {
            Id = "DocumentLifecycleFlowchart",
            Start = init,
            Activities =
            {
                init,
                computeReEntry, applyReEntry,
                reEntryCompleteGate, reEntryReviewGate, reEntryAcceptGate, finalizeReenteredComplete,
                dispatchProduce, ingestProduce, emitProduced,
                validateDraft, emitValidated, validationGate,
                repairCheck, prepareRepair, dispatchRepair, ingestRepair,
                ambiguityCheck, ambiguityGate,
                emitReviewRequested, dispatchReview, ingestReview, emitReviewed,
                routeAccept, routeRevise, routeRounds,
                emitRevisionStarted, prepareRevision, dispatchRevise, ingestRevise,
                buildAcceptanceRequest, publishRequest, waitForDecision, applyGuardrails,
                acceptGate, rejectGate, reviseGate,
                seedValidationExhausted, seedAmbiguity, seedRounds, seedUndecidable,
                emitAccepted, emitRejected, emitEscalated,
                finalizeAccepted, finalizeRejected, finalizeEscalated,
                setOutputs, finish,
            },
            Connections =
            {
                // 39-10 re-entry gate chain (D6): Init → compute → apply → guards.
                Connect(init, computeReEntry),
                Connect(computeReEntry, applyReEntry),
                Connect(applyReEntry, reEntryCompleteGate),
                ConnectOutcome(reEntryCompleteGate, "True", finalizeReenteredComplete),
                ConnectOutcome(reEntryCompleteGate, "False", reEntryReviewGate),
                ConnectOutcome(reEntryReviewGate, "True", emitReviewRequested),
                ConnectOutcome(reEntryReviewGate, "False", reEntryAcceptGate),
                ConnectOutcome(reEntryAcceptGate, "True", buildAcceptanceRequest),
                ConnectOutcome(reEntryAcceptGate, "False", dispatchProduce),
                Connect(finalizeReenteredComplete, setOutputs),

                Connect(dispatchProduce, ingestProduce),
                Connect(ingestProduce, emitProduced),
                Connect(emitProduced, validateDraft),
                Connect(validateDraft, emitValidated),
                Connect(emitValidated, validationGate),

                ConnectOutcome(validationGate, "True", ambiguityCheck),
                ConnectOutcome(validationGate, "False", repairCheck),

                ConnectOutcome(repairCheck, "True", prepareRepair),
                Connect(prepareRepair, dispatchRepair),
                Connect(dispatchRepair, ingestRepair),
                Connect(ingestRepair, validateDraft),
                ConnectOutcome(repairCheck, "False", seedValidationExhausted),

                Connect(ambiguityCheck, ambiguityGate),
                ConnectOutcome(ambiguityGate, "True", seedAmbiguity),
                ConnectOutcome(ambiguityGate, "False", emitReviewRequested),

                Connect(emitReviewRequested, dispatchReview),
                Connect(dispatchReview, ingestReview),
                Connect(ingestReview, emitReviewed),
                Connect(emitReviewed, routeAccept),

                ConnectOutcome(routeAccept, "True", buildAcceptanceRequest),
                ConnectOutcome(routeAccept, "False", routeRevise),
                ConnectOutcome(routeRevise, "True", emitRevisionStarted),
                ConnectOutcome(routeRevise, "False", routeRounds),
                ConnectOutcome(routeRounds, "True", seedRounds),
                ConnectOutcome(routeRounds, "False", seedUndecidable),

                Connect(emitRevisionStarted, prepareRevision),
                Connect(prepareRevision, dispatchRevise),
                Connect(dispatchRevise, ingestRevise),
                Connect(ingestRevise, validateDraft),

                // ACCEPT — publish → gate (NO decision between them) → guardrails → route
                Connect(buildAcceptanceRequest, publishRequest),
                Connect(publishRequest, waitForDecision),
                ConnectOutcome(waitForDecision, "Accept", applyGuardrails),
                ConnectOutcome(waitForDecision, "RequestRevision", applyGuardrails),
                ConnectOutcome(waitForDecision, "Reject", applyGuardrails),
                ConnectOutcome(waitForDecision, "Escalate", applyGuardrails),
                Connect(applyGuardrails, acceptGate),
                ConnectOutcome(acceptGate, "True", emitAccepted),
                ConnectOutcome(acceptGate, "False", rejectGate),
                ConnectOutcome(rejectGate, "True", emitRejected),
                ConnectOutcome(rejectGate, "False", reviseGate),
                ConnectOutcome(reviseGate, "True", emitRevisionStarted),
                ConnectOutcome(reviseGate, "False", emitEscalated),

                // Escalate seeds → shared escalated terminal
                Connect(seedValidationExhausted, emitEscalated),
                Connect(seedAmbiguity, emitEscalated),
                Connect(seedRounds, emitEscalated),
                Connect(seedUndecidable, emitEscalated),

                // Terminals
                Connect(emitAccepted, finalizeAccepted),
                Connect(finalizeAccepted, setOutputs),
                Connect(emitRejected, finalizeRejected),
                Connect(finalizeRejected, setOutputs),
                Connect(emitEscalated, finalizeEscalated),
                Connect(finalizeEscalated, setOutputs),

                Connect(setOutputs, finish),
            }
        };
    }

    // ====================================================================
    // Node factories
    // ====================================================================

    private static DispatchWorkflow LlmDispatch(
        string id, string name,
        Variable<string> role, Variable<string> action, Variable<string> variablesJson,
        Variable<string> documentType, Variable<string> issueId, Variable<string> tenantId,
        Variable<IDictionary<string, object>?> result)
    {
        var dispatch = new DispatchWorkflow
        {
            Id = id, Name = name,
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                // agentRole is the REAL llm-call input key (LlmCallWorkflow), NOT role.
                ["agentRole"] = role.Get(ctx) ?? "",
                ["action"] = action.Get(ctx) ?? "",
                ["tenantId"] = tenantId.Get(ctx) ?? "",
                // Thread documentType + issueId onto the wire request so 39-9's inner
                // repair ring (which selects the validator from documentType) is reachable.
                ["documentType"] = documentType.Get(ctx) ?? "",
                ["issueId"] = issueId.Get(ctx) ?? "",
                ["variables"] = ParseVarsDict(variablesJson.Get(ctx)),
                ["enableTools"] = false,
            }),
            WaitForCompletion = new(true),
            Result = new(result),
        };
        dispatch.SetDisplayText(name);
        return dispatch;
    }

    private static EmitDocumentEventActivity DocEvent(
        string id, string name, string eventType,
        Variable<string> docId, Variable<string> docType, Variable<int> round,
        Variable<string> issueId, Variable<string> corr, Variable<string> session, Variable<string> tenant,
        string detail)
    {
        var e = new EmitDocumentEventActivity
        {
            Id = id, Name = name,
            EventType = new(eventType),
            DocumentId = new(ctx => docId.Get(ctx)),
            DocumentType = new(ctx => docType.Get(ctx)),
            Round = new(ctx => round.Get(ctx)),
            IssueId = new(ctx => issueId.Get(ctx)),
            CorrelationId = new(ctx => corr.Get(ctx)),
            SessionId = new(ctx => session.Get(ctx)),
            TenantId = new(ctx => tenant.Get(ctx)),
            Detail = new(detail),
        };
        e.SetDisplayText(name);
        return e;
    }

    private static EmitDocumentEventActivity TerminalEmit(
        string id, string name, string eventType,
        Variable<string> docId, Variable<string> docType, Variable<int> round,
        Variable<string> issueId, Variable<string> corr, Variable<string> session, Variable<string> tenant,
        Variable<string> outcome, string detail)
    {
        var e = new EmitDocumentEventActivity
        {
            Id = id, Name = name,
            EventType = new(eventType),
            DocumentId = new(ctx => docId.Get(ctx)),
            DocumentType = new(ctx => docType.Get(ctx)),
            Round = new(ctx => round.Get(ctx)),
            IssueId = new(ctx => issueId.Get(ctx)),
            CorrelationId = new(ctx => corr.Get(ctx)),
            SessionId = new(ctx => session.Get(ctx)),
            TenantId = new(ctx => tenant.Get(ctx)),
            Detail = new(detail),
            DataJson = new(ctx => { var o = outcome.Get(ctx); return string.IsNullOrWhiteSpace(o) ? null : $"{{\"outcome\":\"{o}\"}}"; }),
        };
        e.SetDisplayText(name);
        return e;
    }

    private static SetVariable SeedEscalate(string id, string name, Variable<string> outcome, DocumentLifecycleOutcome value)
    {
        var sv = new SetVariable
        {
            Id = id, Name = name,
            Variable = outcome,
            Value = new(_ => (object)value.ToWire()),
        };
        sv.SetDisplayText(name);
        return sv;
    }

    private enum TerminalKind { Accepted, Rejected, Escalated }

    private static SetVariable Finalize(
        string id, string name,
        Variable<string> stateJson, DocumentState finalState,
        Variable<string> outStatus, Variable<string> outOutcome, Variable<string> outDocId, Variable<string> outResult,
        Variable<string> outDocJson,
        Variable<string> currentDocId, Variable<string> currentDocJson, Variable<int> currentRound,
        TerminalKind kind, Variable<string> escalateOutcome)
    {
        var sv = new SetVariable
        {
            Id = id, Name = name,
            Variable = outStatus,
            Value = new(ctx =>
            {
                var state = DocumentLifecycleHelper.Deserialize(stateJson.Get(ctx));
                if (state.Current is not null)
                    state = DocumentLifecycleHelper.TransitionCurrent(state, finalState, DateTimeOffset.UtcNow);

                var docId = state.Current?.Id;
                DocumentLifecycleResult result = kind switch
                {
                    TerminalKind.Accepted => DocumentLifecycleHelper.BuildAccepted(state, docId ?? Guid.Empty),
                    TerminalKind.Rejected => DocumentLifecycleHelper.BuildRejected(state, docId ?? Guid.Empty),
                    _ => DocumentLifecycleHelper.BuildOutcome(state,
                        DocumentLifecycleOutcomeExtensions.Parse(
                            string.IsNullOrWhiteSpace(escalateOutcome.Get(ctx))
                                ? DocumentLifecycleOutcome.ReviewUndecidable.ToWire()
                                : escalateOutcome.Get(ctx))),
                };

                outOutcome.Set(ctx, result.Outcome?.ToWire() ?? "");
                outDocId.Set(ctx, result.DocumentId?.ToString() ?? "");
                outResult.Set(ctx, JsonSerializer.Serialize(result, DocumentJson.Options));
                // D4 — the terminal revision's payload body (empty when nothing was produced).
                outDocJson.Set(ctx, state.Current is null ? "" : state.Current.Payload.GetRawText());
                UpdateCurrent(ctx, state, currentDocId, currentDocJson, currentRound);
                stateJson.Set(ctx, DocumentLifecycleHelper.Serialize(state));
                return result.Status;
            })
        };
        sv.SetDisplayText(name);
        return sv;
    }

    // ====================================================================
    // Pure helpers (exposed static, no Elsa context)
    // ====================================================================

    private static void UpdateCurrent(
        ExprContext ctx, DocumentLifecycleHelper.LifecycleState state,
        Variable<string> currentDocId, Variable<string> currentDocJson, Variable<int> currentRound)
    {
        var current = state.Current;
        currentDocId.Set(ctx, current?.Id.ToString() ?? "");
        currentDocJson.Set(ctx, current is null ? "" : DocumentJson.Serialize(current));
        currentRound.Set(ctx, state.Round);
    }

    /// <summary>Ingest a produce/repair/revise result into a fresh draft envelope.</summary>
    private static (DocumentLifecycleHelper.LifecycleState State, bool Ok, string PayloadJson) IngestDraft(
        IDictionary<string, object>? result, DocumentLifecycleHelper.LifecycleState state, bool isRevise)
    {
        if (!ReadSuccessFlag(result))
            return (state, false, "{}");

        var text = result!.TryGetValue("llmResponse", out var r) ? r?.ToString() ?? "" : "";
        var payloadJson = ExtractJsonObject(text);
        if (payloadJson is null)
            return (state, false, "{}");

        JsonElement payload;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            payload = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return (state, false, "{}");
        }

        var producer = DocumentProducer.Create(
            state.ProducerRole, state.ProducerAction, DocumentLifecycleHelper.ProducerWorkflowDefinitionId);
        var supersedes = isRevise ? state.Current?.Id : null;
        var envelope = DocumentLifecycleHelper.MintDraft(state, payload, producer, supersedes, DateTimeOffset.UtcNow);
        return (DocumentLifecycleHelper.AppendDraft(state, envelope), true, payloadJson);
    }

    private static DocumentEnvelope BuildReviewEnvelope(
        DocumentLifecycleHelper.LifecycleState state, string reviewJson, string reviewDefId)
    {
        JsonElement payload;
        using (var doc = JsonDocument.Parse(reviewJson))
            payload = doc.RootElement.Clone();

        var reviewerRoleWire = FirstNonEmpty(
            state.Rules.Rules.ReviewerSelection.ReviewerRole,
            state.Rules.Rules.ReviewerSelection.PanelRoles is { Count: > 0 } panel ? panel[0] : null,
            AgentRole.Architect.ToWire());
        var reviewerRole = AgentRoleExtensions.Parse(reviewerRoleWire);
        var reviewAction = RolePhaseMap.GetReviewActionForRole(reviewerRole).ToWire();
        var producer = DocumentProducer.Create(reviewerRoleWire, reviewAction, reviewDefId);

        return DocumentEnvelope.CreateDraft(
            DocumentTypeKey.Review, 1, state.IssueId, state.CorrelationId, producer, payload,
            now: DateTimeOffset.UtcNow);
    }

    private static string? ReadReviewJson(IDictionary<string, object>? result)
    {
        if (!ReadSuccessFlag(result)) return null;
        if (result!.TryGetValue("reviewJson", out var rj) && rj is not null)
        {
            var s = rj.ToString();
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }
        // Fallback: the review producer may surface the review as llmResponse text.
        if (result.TryGetValue("llmResponse", out var lr) && lr is not null)
            return ExtractJsonObject(lr.ToString() ?? "");
        return null;
    }

    private static Dictionary<string, object> ParseVarsDict(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, object>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new Dictionary<string, object>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, object>();
        }
    }

    private static readonly JsonSerializerOptions s_decisionOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static AcceptanceDecision ParseDecision(string? decisionJson)
    {
        if (!string.IsNullOrWhiteSpace(decisionJson))
        {
            try
            {
                var d = JsonSerializer.Deserialize<AcceptanceDecision>(decisionJson, s_decisionOptions);
                if (d is not null) return d;
            }
            catch (JsonException) { /* fall through */ }
        }
        return new AcceptanceDecision.Escalate(AcceptanceEscalationReason.AcceptorJudgment, "unreadable decision payload");
    }

    private static ApprovalChannel ParseChannel(string? channel)
        => EnumWire<ApprovalChannel>.TryParse(channel ?? "", out var c) ? c : ApprovalChannel.Orchestrator;

    private static Tamma.Core.Documents.Types.ReviewDecision ParseReviewDecision(string? wire)
        => EnumWire<Tamma.Core.Documents.Types.ReviewDecision>.TryParse(wire ?? "", out var d)
            ? d
            : Tamma.Core.Documents.Types.ReviewDecision.NeedsDiscussion;

    /// <summary>Fail-closed <c>success</c> flag read from a dispatched workflow result.</summary>
    internal static bool ReadSuccessFlag(IDictionary<string, object>? result)
    {
        if (result == null) return false;
        if (!result.TryGetValue("success", out var s)) return false;
        return ResumeInput.AsBool(s);
    }

    /// <summary>Carve the first <c>{</c> … last <c>}</c> JSON object out of a response.</summary>
    internal static string? ExtractJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        var candidate = text[start..(end + 1)];
        try
        {
            using var _ = JsonDocument.Parse(candidate);
            return candidate;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    private static double? TryGetDouble(object? value)
    {
        switch (value)
        {
            case null: return null;
            case double d: return d;
            case float f: return f;
            case int i: return i;
            case long l: return l;
            case decimal m: return (double)m;
            case string s when double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed): return parsed;
            default: return null;
        }
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));
}
