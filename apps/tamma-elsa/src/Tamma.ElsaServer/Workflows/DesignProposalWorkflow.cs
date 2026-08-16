using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Runtime.Activities;
using System.Text.Json;
using Tamma.Activities.Design;
using Tamma.Activities.Documents;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents.Resume;
using Tamma.ElsaServer.Workflows.Helpers;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Story 39-13 — Design Proposal, re-implemented as a THIN BINDING over
/// <see cref="DocumentLifecycleWorkflow"/>, producing a typed
/// <see cref="Tamma.Core.Documents.Types.Design"/> document. The bespoke approval gate
/// (<c>WaitForDesignApprovalActivity</c>) is RETIRED (D4): design acceptance rides 39-8's
/// generic decision gate on the canonical tenant-folded bookmark. The binding threads its
/// <c>sessionId</c> as the lifecycle's decision-session id so <c>DesignResumeEndpoint</c>
/// (now a thin adapter) resolves the very gate the lifecycle suspended on.
///
/// <para>Delivery-to-issue survives via the filed-back pre-ACCEPT delivery hook (D5): the
/// binding passes <c>deliveryWorkflowDefinitionId = "design-proposal-delivery"</c>, so the
/// lifecycle dispatches the tiny <see cref="DesignDeliveryWorkflow"/> (which emits
/// <c>DESIGN.PROPOSAL.GENERATED</c>/<c>DELIVERED</c>) BEFORE the human decides.</para>
///
/// <para>The public surface is byte-stable (D1): same <c>DefinitionId = "design-proposal"</c>,
/// same outputs (<c>sessionId</c>/<c>status</c>/<c>designProposal</c>/<c>approved</c>) plus
/// additive <c>outcome</c>/<c>documentId</c>. NO parse, NO success-flag gate, ZERO
/// <see cref="Finish"/>. <c>DESIGN.REVIEW.TIMED_OUT</c> becomes unreachable (39-8 arms no SLA
/// on decisions); the constant stays drift-safe.</para>
/// </summary>
[ResumeBehavior(ResumeMode.LatestStateReEntry)]
public class DesignProposalWorkflow : WorkflowBase
{
    private const string DesignDocumentType = "design";
    private const string DesignDeliveryDefinitionId = "design-proposal-delivery";
    private const string AmbiguityAssessmentDocumentType = "ambiguity-assessment";

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "DesignProposal";
        builder.DefinitionId = "design-proposal";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Generate a reviewed technical design proposal via the generic document lifecycle (produce → validate → review → deliver → accept gate)";

        // ── Inputs ─────────────────────────────────────────────────────
        var sessionId    = builder.WithVariable<Guid>().Persisted();
        var issueId      = builder.WithVariable<string>().Persisted();
        var requirement  = builder.WithVariable<string>().Persisted();
        var repository   = builder.WithVariable<string>().Persisted();
        var issueNumber  = builder.WithVariable<int>().Persisted();
        var constraints  = builder.WithVariable<string>().Persisted();
        var conventions  = builder.WithVariable<string>().Persisted();
        var tenantId     = builder.WithVariable<string>("TenantId", "").Persisted();
        var acceptanceRulesJson = builder.WithVariable<string>("AcceptanceRulesJson", "").Persisted();

        // ── Story 39-25 — threaded ambiguity score (leg 1) ─────────────
        var assessmentFound = builder.WithVariable<bool>().Persisted();
        var assessmentJson  = builder.WithVariable<string>("AssessmentJson", "{}").Persisted();

        // ── 39-10 re-entry position ────────────────────────────────────
        var reEntryPositionJson = builder.WithVariable<string>().Persisted();
        var reEntryDocJson  = builder.WithVariable<string>().Persisted();
        var positionStage   = builder.WithVariable<string>("PositionStage", "produce").Persisted();

        // ── Dispatched-workflow result + typed exit ────────────────────
        var lifecycleResult = builder.WithVariable<IDictionary<string, object>?>().Persisted();
        var lifecycleAccepted = builder.WithVariable<bool>().Persisted();
        var lifecycleRejected = builder.WithVariable<bool>().Persisted();
        var exitStatus      = builder.WithVariable<string>("ExitStatus", "").Persisted();
        var exitOutcome     = builder.WithVariable<string>("ExitOutcome", "").Persisted();
        var exitDocId       = builder.WithVariable<string>("ExitDocId", "").Persisted();
        var proposalJson    = builder.WithVariable<string>("ProposalJson", "{}").Persisted();
        var alternativeCount = builder.WithVariable<int>().Persisted();
        var decisionNotes   = builder.WithVariable<string>("DecisionNotes", "").Persisted();
        var failureDetail   = builder.WithVariable<string>("FailureDetail", "").Persisted();
        var approved        = builder.WithVariable<bool>().Persisted();
        var outputStatus    = builder.WithVariable<string>().Persisted();

        // ── Step 1: Read inputs ────────────────────────────────────────
        var readInputs = new SetVariable
        {
            Id = "ReadInputs", Name = "Read Inputs",
            Variable = sessionId,
            Value = new(context =>
            {
                var sid = context.GetInput<Guid>("sessionId");
                if (sid == Guid.Empty) sid = Guid.NewGuid();

                issueId.Set(context, context.GetInput<string>("issueId") ?? string.Empty);
                requirement.Set(context, context.GetInput<string>("requirement") ?? string.Empty);
                repository.Set(context, context.GetInput<string>("repository") ?? string.Empty);
                issueNumber.Set(context, context.GetInput<int>("issueNumber"));
                constraints.Set(context, context.GetInput<string>("constraints") ?? string.Empty);
                conventions.Set(context, context.GetInput<string>("conventions") ?? string.Empty);
                tenantId.Set(context, context.GetInput<string>("tenantId") ?? string.Empty);
                acceptanceRulesJson.Set(context, context.GetInput<string>("acceptanceRulesJson") ?? string.Empty);
                return sid;
            })
        };
        readInputs.SetDisplayText("Read Inputs");

        // ── Step 2: Compute 39-10 re-entry position ────────────────────
        var computeReEntry = new ComputeReEntryPositionActivity
        {
            Id = "ComputeReEntryPosition", Name = "Compute Re-Entry Position",
            IssueId = new(ctx => issueId.Get(ctx)),
            DocumentType = new(DesignDocumentType),
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
                    ["documentType"]          = DesignDocumentType,
                    ["producerRole"]          = AgentRole.Architect.ToWire(),
                    ["producerAction"]        = AgentAction.ProposeDesign.ToWire(),
                    ["producerVariablesJson"] = JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["workItemJson"]    = requirement.Get(ctx) ?? "",
                        ["contextFindings"] = constraints.Get(ctx) ?? "",
                        ["repository"]      = repository.Get(ctx) ?? "",
                        ["conventions"]     = conventions.Get(ctx) ?? "",
                    }),
                    ["issueId"]             = issueId.Get(ctx) ?? "",
                    ["correlationId"]       = issueId.Get(ctx) ?? "",
                    // Thread the binding's sessionId as the lifecycle decision-session id so the
                    // DesignResumeEndpoint adapter resolves the same accept-gate bookmark (D4).
                    ["sessionId"]           = sessionId.Get(ctx),
                    ["tenantId"]            = tenantId.Get(ctx) ?? "",
                    ["acceptanceRulesJson"] = acceptanceRulesJson.Get(ctx) ?? "",
                    // Pre-ACCEPT delivery hook (D5): post the proposal to the issue before the human
                    // decides, emitting DESIGN.PROPOSAL.GENERATED/DELIVERED via the delivery workflow.
                    ["deliveryWorkflowDefinitionId"] = DesignDeliveryDefinitionId,
                    ["repository"]          = repository.Get(ctx) ?? "",
                    ["issueNumber"]         = issueNumber.Get(ctx),
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
            Variable = proposalJson,
            Value = new(ctx =>
            {
                var exit = LifecycleBindingHelper.ReadLifecycleResult(lifecycleResult.Get(ctx));
                var isAccepted = LifecycleBindingHelper.IsAccepted(exit);
                var isRejected = string.Equals(exit.Status, "rejected", StringComparison.Ordinal);

                lifecycleAccepted.Set(ctx, isAccepted);
                lifecycleRejected.Set(ctx, isRejected);
                exitStatus.Set(ctx, exit.Status);
                exitOutcome.Set(ctx, exit.Outcome ?? "");
                exitDocId.Set(ctx, exit.DocumentId ?? "");
                alternativeCount.Set(ctx, AssessmentBindingHelper.CountAlternatives(exit.DocumentJson));
                decisionNotes.Set(ctx, exit.DecisionNotes);
                failureDetail.Set(ctx, AssessmentBindingHelper.BuildFailureDetail(exit));
                approved.Set(ctx, isAccepted);
                outputStatus.Set(ctx, isAccepted ? "approved" : isRejected ? "rejected" : exit.Status);
                return exit.DocumentJson;
            })
        };
        readLifecycleExit.SetDisplayText("Read Lifecycle Exit");

        // ── Step 5: routing (typed values only) ────────────────────────
        var acceptedGate = new FlowDecision(ctx => lifecycleAccepted.Get(ctx))
        { Id = "LifecycleAccepted", Name = "Approved?" };
        acceptedGate.SetDisplayText("Approved?");

        var rejectedGate = new FlowDecision(ctx => lifecycleRejected.Get(ctx))
        { Id = "LifecycleRejected", Name = "Rejected?" };
        rejectedGate.SetDisplayText("Rejected?");

        var emitApproved = new EmitDesignEventActivity
        {
            Id = "EmitProposalApproved", Name = "Emit Proposal Approved",
            EventType = new(DesignEvents.ProposalApproved),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            AlternativeCount = new(ctx => alternativeCount.Get(ctx)),
            Detail = new(ctx => decisionNotes.Get(ctx)),
        };
        emitApproved.SetDisplayText("Emit Proposal Approved");

        var emitRejected = new EmitDesignEventActivity
        {
            Id = "EmitProposalRejected", Name = "Emit Proposal Rejected",
            EventType = new(DesignEvents.ProposalRejected),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            AlternativeCount = new(ctx => alternativeCount.Get(ctx)),
            Detail = new(ctx => decisionNotes.Get(ctx)),
        };
        emitRejected.SetDisplayText("Emit Proposal Rejected");

        var emitFailed = new EmitDesignEventActivity
        {
            Id = "EmitProposalFailed", Name = "Emit Proposal Failed",
            EventType = new(DesignEvents.ProposalFailed),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Detail = new(ctx => failureDetail.Get(ctx)),
        };
        emitFailed.SetDisplayText("Emit Proposal Failed");

        // ── Step 6: Expose output — the single terminal region ─────────
        var exposeOutput = new Sequence
        {
            Id = "ExposeOutput", Name = "Expose Output",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutputSessionId", Name = "Output Session Id", OutputName = new("sessionId"), OutputValue = new(ctx => (object)sessionId.Get(ctx).ToString()) }, "Output Session Id"),
                WithLabel(new SetOutput { Id = "OutputStatus", Name = "Output Status", OutputName = new("status"), OutputValue = new(ctx => (object)(outputStatus.Get(ctx) ?? "")) }, "Output Status"),
                WithLabel(new SetOutput { Id = "OutputProposal", Name = "Output Proposal", OutputName = new("designProposal"), OutputValue = new(ctx => (object)(proposalJson.Get(ctx) ?? "{}")) }, "Output Proposal"),
                WithLabel(new SetOutput { Id = "OutputApproved", Name = "Output Approved", OutputName = new("approved"), OutputValue = new(ctx => (object)approved.Get(ctx)) }, "Output Approved"),
                WithLabel(new SetOutput { Id = "OutputOutcome", Name = "Output Outcome", OutputName = new("outcome"), OutputValue = new(ctx => (object)(exitOutcome.Get(ctx) ?? "")) }, "Output Outcome"),
                WithLabel(new SetOutput { Id = "OutputDocumentId", Name = "Output Document Id", OutputName = new("documentId"), OutputValue = new(ctx => (object)(exitDocId.Get(ctx) ?? "")) }, "Output Document Id"),
            }
        };
        exposeOutput.SetDisplayText("Expose Output");

        // ── Build the flowchart ────────────────────────────────────────
        builder.Root = new Flowchart
        {
            Id = "DesignProposalFlowchart",
            Name = "Design Proposal Flowchart",
            Start = readInputs,
            Activities =
            {
                readInputs, computeReEntry, readPositionStage,
                fetchAmbiguityAssessment, dispatchLifecycle, readLifecycleExit,
                acceptedGate, rejectedGate, emitApproved, emitRejected, emitFailed,
                exposeOutput,
            },
            Connections =
            {
                new(readInputs, computeReEntry),
                new(computeReEntry, readPositionStage),
                // 39-25 — the ambiguity fetch is the single predecessor of the dispatch.
                new(readPositionStage, fetchAmbiguityAssessment),
                new(fetchAmbiguityAssessment, dispatchLifecycle),

                new(dispatchLifecycle, readLifecycleExit),
                new(readLifecycleExit, acceptedGate),

                new(new FlowEndpoint(acceptedGate, "True"),  new FlowEndpoint(emitApproved)),
                new(emitApproved, exposeOutput),
                new(new FlowEndpoint(acceptedGate, "False"), new FlowEndpoint(rejectedGate)),
                new(new FlowEndpoint(rejectedGate, "True"),  new FlowEndpoint(emitRejected)),
                new(emitRejected, exposeOutput),
                new(new FlowEndpoint(rejectedGate, "False"), new FlowEndpoint(emitFailed)),
                new(emitFailed, exposeOutput),
            }
        };
    }
}
