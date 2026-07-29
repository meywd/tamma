using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Runtime.Activities;
using System.Text.Json;
using Tamma.Activities.Documents;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents.Resume;
using Tamma.ElsaServer.Workflows.Helpers;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Story 41-2 — Acceptance-Criteria Authoring: a THIN BINDING over
/// <see cref="DocumentLifecycleWorkflow"/> (<c>DefinitionId = "document-lifecycle"</c>) producing
/// a typed <see cref="Tamma.Core.Documents.Types.AcceptanceCriteria"/> document from the
/// <c>(product_owner, define-acceptance-criteria)</c> produce cell. "Done" is defined once,
/// reviewed, accepted and then consumed by 41-15's acceptance verification and the merge gate —
/// instead of being implicit in a plan or a reviewer's head.
///
/// <para><b>Greenfield, not a migration (D1 / Correction 3).</b> The cell exists in the taxonomy
/// (<c>AgentAction.DefineAcceptanceCriteria</c>, <c>RolePhaseMap</c>) with a prompt file, but NO
/// workflow dispatched it before this one — so there is no legacy event family to preserve, no
/// parser to delete, and no byte-stability obligation. The definition id is deliberately
/// <c>acceptance-criteria-authoring</c>, not <c>acceptance-criteria</c>, so it never reads as the
/// document-type wire.</para>
///
/// <para><b>Consumed context rides the DECLARED carrier (D3).</b>
/// <c>define-acceptance-criteria.md</c> declares
/// <c>role, workItemJson, contextFindings, conventions</c>; a producer variable the front matter
/// does not declare is silently dropped at render (the 39-15 render-drop lesson). The accepted
/// <c>clarification</c> and <c>findings</c> bodies are therefore composed into the ONE declared
/// <c>contextFindings</c> carrier, and <c>feedbackVariableName = "contextFindings"</c> routes
/// repair/revise notes into that same carrier. Both fetches are fail-closed
/// (<see cref="FetchLatestAcceptedDocumentActivity"/>: absent upstream ⇒ empty carrier, never a
/// hard fail) — acceptance criteria are authorable from the issue alone.</para>
///
/// <para><b>Single-parent lineage (D4).</b> <c>DocumentInstance</c> carries one
/// <c>ParentDocumentId</c>, so the parent is the accepted Clarification when one exists, else the
/// Findings, else none; the full consumes-set rides the <c>ACCEPTANCE_CRITERIA.DRAFTED</c> event
/// payload.</para>
///
/// <para><b>Resumable by design (D5).</b> <c>[ResumeBehavior(LatestStateReEntry)]</c> with a
/// <see cref="ComputeReEntryPositionActivity"/> node and NO allowlist entry: a thin binding owns
/// no bookmark — the accept gate suspends inside the dispatched <c>document-lifecycle</c> child,
/// which this parent awaits with <c>WaitForCompletion = true</c>. Zero <see cref="Finish"/>, zero
/// <c>llm-call</c> dispatch, zero validate/retry plumbing.</para>
/// </summary>
[ResumeBehavior(ResumeMode.LatestStateReEntry)]
public class AcceptanceCriteriaAuthoringWorkflow : WorkflowBase
{
    private const string AcceptanceCriteriaDocumentType = "acceptance-criteria";
    private const string ClarificationDocumentType = "clarification";
    private const string FindingsDocumentType = "findings";

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Acceptance Criteria Authoring";
        builder.DefinitionId = "acceptance-criteria-authoring";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Author the testable definition-of-done for an issue as a typed AcceptanceCriteria document via the generic document lifecycle (produce → validate → review → revise → accept)";

        // ── Inputs ─────────────────────────────────────────────────────
        // 41-2 follow-up F7 (2026-07-29): the decision-session handle. Every other
        // lifecycle binding threads one (AdrAuthoringWorkflow, DesignProposalWorkflow,
        // DocumentLifecycleWorkflow); this binding did not, so — although nothing
        // crashed, because DocumentLifecycleWorkflow mints a UUIDv7 when the input is
        // Guid.Empty — the caller had NO handle to correlate the accept decision with,
        // and the workflow exposed none on exit. That bites when 39-17/39-19 land.
        var sessionId    = builder.WithVariable<Guid>();
        var issueId      = builder.WithVariable<string>("IssueId", "");
        var issueTitle   = builder.WithVariable<string>("IssueTitle", "");
        var repository   = builder.WithVariable<string>("Repository", "");
        var issueNumber  = builder.WithVariable<int>("IssueNumber", 0);
        var workItemJson = builder.WithVariable<string>("WorkItemJson", "");
        var conventions  = builder.WithVariable<string>("Conventions", "");
        var tenantId     = builder.WithVariable<string>("TenantId", "");
        var acceptanceRulesJson = builder.WithVariable<string>("AcceptanceRulesJson", "");

        // ── Consumed upstream documents (D3 carrier / D4 lineage) ──────
        var clarificationFound   = builder.WithVariable<bool>();
        var clarificationDocId   = builder.WithVariable<string>("ClarificationDocId", "");
        var clarificationJson    = builder.WithVariable<string>("ClarificationJson", "");
        var clarificationLineage = builder.WithVariable<string>();
        var findingsFound   = builder.WithVariable<bool>();
        var findingsDocId   = builder.WithVariable<string>("FindingsDocId", "");
        var findingsJson    = builder.WithVariable<string>("FindingsJson", "");
        var findingsLineage = builder.WithVariable<string>();

        // ── 39-10 re-entry position (D5) ───────────────────────────────
        var reEntryPositionJson = builder.WithVariable<string>();
        var reEntryDocJson  = builder.WithVariable<string>();
        var positionStage   = builder.WithVariable<string>("PositionStage", "produce");

        // ── Dispatched-workflow result + typed exit ────────────────────
        var lifecycleResult   = builder.WithVariable<IDictionary<string, object>?>();
        var lifecycleAccepted = builder.WithVariable<bool>();
        var lifecycleDrafted  = builder.WithVariable<bool>();
        var exitOutcome       = builder.WithVariable<string>("ExitOutcome", "");
        var exitDocId         = builder.WithVariable<string>("ExitDocId", "");
        var criteriaJson      = builder.WithVariable<string>("CriteriaJson", "[]");
        var criteriaCount     = builder.WithVariable<int>();
        var parentDocumentId  = builder.WithVariable<string>("ParentDocumentId", "");
        var failureDetail     = builder.WithVariable<string>("FailureDetail", "");
        var outputStatus      = builder.WithVariable<string>();

        // ── Step 1: Read inputs ────────────────────────────────────────
        var readInputs = new SetVariable
        {
            Id = "ReadInputs", Name = "Read Inputs",
            Variable = issueId,
            Value = new(ctx =>
            {
                // F7 — mint a session id when the caller supplies none, exactly as
                // AdrAuthoringWorkflow:118 does, so the handle this binding hands the
                // lifecycle is the SAME one it exposes as output.
                var sid = ctx.GetInput<Guid>("sessionId");
                if (sid == Guid.Empty) sid = Guid.NewGuid();
                sessionId.Set(ctx, sid);

                var repo = ctx.GetInput<string>("repository") ?? "";
                repository.Set(ctx, repo);
                issueNumber.Set(ctx, ctx.GetInput<int>("issueNumber"));
                issueTitle.Set(ctx, ctx.GetInput<string>("issueTitle") ?? "");
                workItemJson.Set(ctx, ctx.GetInput<string>("workItemJson") ?? "");
                conventions.Set(ctx, ctx.GetInput<string>("conventions") ?? "");
                tenantId.Set(ctx, ctx.GetInput<string>("tenantId") ?? "");
                acceptanceRulesJson.Set(ctx, ctx.GetInput<string>("acceptanceRulesJson") ?? "");

                // AcceptanceCriteria has exactly ONE producing cell per issue (41-1b D4), so —
                // unlike task-creation's two-plans collision (39-15 D2) — the lifecycle keys on
                // the bare issue id; no producer scope suffix is needed.
                var explicitIssueId = ctx.GetInput<string>("issueId") ?? "";
                return string.IsNullOrWhiteSpace(explicitIssueId)
                    ? CreationBindingHelper.DeriveIssueId(repo, ctx.GetInput<int>("issueNumber"))
                    : explicitIssueId;
            })
        };
        readInputs.SetDisplayText("Read Inputs");

        // ── Step 2: Compute 39-10 re-entry position (D5) ───────────────
        var computeReEntry = new ComputeReEntryPositionActivity
        {
            Id = "ComputeReEntryPosition", Name = "Compute Re-Entry Position",
            IssueId = new(ctx => issueId.Get(ctx)),
            DocumentType = new(AcceptanceCriteriaDocumentType),
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

        // ── Step 3: FreshRun gate — a re-entry is not a new authoring run (D5) ──
        var freshRun = new FlowDecision(ctx => positionStage.Get(ctx) == "produce")
        { Id = "FreshRun", Name = "Fresh Run?" };
        freshRun.SetDisplayText("Fresh Run?");

        var emitStarted = new EmitDomainLifecycleEventActivity
        {
            Id = "EmitAcceptanceCriteriaStarted", Name = "Emit Acceptance Criteria Started",
            EventType = new(AcceptanceCriteriaEvents.Started),
            IssueId = new(ctx => issueId.Get(ctx)),
            Repository = new(ctx => repository.Get(ctx)),
            CorrelationId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Detail = new(ctx => issueTitle.Get(ctx)),
        };
        emitStarted.SetDisplayText("Emit Acceptance Criteria Started");

        var fetchClarification = new FetchLatestAcceptedDocumentActivity
        {
            Id = "FetchConsumedClarification", Name = "Fetch Accepted Clarification",
            IssueId = new(ctx => issueId.Get(ctx)),
            DocumentTypeKey = new(ClarificationDocumentType),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Found = new(clarificationFound),
            DocumentId = new(clarificationDocId),
            DocumentJson = new(clarificationJson),
            LineageJson = new(clarificationLineage),
        };
        fetchClarification.SetDisplayText("Fetch Accepted Clarification");

        var fetchFindings = new FetchLatestAcceptedDocumentActivity
        {
            Id = "FetchConsumedFindings", Name = "Fetch Accepted Findings",
            IssueId = new(ctx => issueId.Get(ctx)),
            DocumentTypeKey = new(FindingsDocumentType),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Found = new(findingsFound),
            DocumentId = new(findingsDocId),
            DocumentJson = new(findingsJson),
            LineageJson = new(findingsLineage),
        };
        fetchFindings.SetDisplayText("Fetch Accepted Findings");

        // ── Step 4: Dispatch the generic document lifecycle ────────────
        var dispatchLifecycle = new DispatchWorkflow
        {
            Id = "DispatchLifecycle", Name = "Dispatch Document Lifecycle",
            WorkflowDefinitionId = new("document-lifecycle"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["documentType"]          = AcceptanceCriteriaDocumentType,
                ["producerRole"]          = AgentRole.ProductOwner.ToWire(),
                ["producerAction"]        = AgentAction.DefineAcceptanceCriteria.ToWire(),
                ["producerVariablesJson"] = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["workItemJson"] = workItemJson.Get(ctx) ?? "",
                    // D3 — the consumed Clarification + Findings ride the DECLARED carrier.
                    ["contextFindings"] = AcceptanceCriteriaBindingHelper.BuildContextFindings(
                        clarificationJson.Get(ctx), findingsJson.Get(ctx)),
                    ["conventions"] = conventions.Get(ctx) ?? "",
                }),
                // 39-6 D11 — repair/revise notes land in the DECLARED carrier (D3).
                ["feedbackVariableName"] = "contextFindings",
                // F7 — thread the binding's sessionId as the lifecycle's decision-session
                // id (AdrAuthoringWorkflow:245, DesignProposalWorkflow:157) so the accept
                // decision is correlatable to this run rather than to a UUID the child
                // minted and nobody upstream ever sees.
                ["sessionId"]           = sessionId.Get(ctx),
                ["issueId"]             = issueId.Get(ctx) ?? "",
                ["correlationId"]       = issueId.Get(ctx) ?? "",
                ["tenantId"]            = tenantId.Get(ctx) ?? "",
                ["acceptanceRulesJson"] = acceptanceRulesJson.Get(ctx) ?? "",
            }),
            WaitForCompletion = new(true),
            Result = new(lifecycleResult),
        };
        dispatchLifecycle.SetDisplayText("Dispatch Document Lifecycle");

        // ── Step 5: Read the typed lifecycle exit (fail-closed) ────────
        var readLifecycleExit = new SetVariable
        {
            Id = "ReadLifecycleExit", Name = "Read Lifecycle Exit",
            Variable = criteriaJson,
            Value = new(ctx =>
            {
                var exit = LifecycleBindingHelper.ReadLifecycleResult(lifecycleResult.Get(ctx));
                var accepted = LifecycleBindingHelper.IsAccepted(exit);

                lifecycleAccepted.Set(ctx, accepted);
                lifecycleDrafted.Set(ctx, !string.IsNullOrWhiteSpace(exit.DocumentId));
                exitOutcome.Set(ctx, exit.Outcome ?? "");
                exitDocId.Set(ctx, exit.DocumentId ?? "");
                outputStatus.Set(ctx, accepted ? "completed" : exit.Status);
                failureDetail.Set(ctx, CreationBindingHelper.BuildFailureDetail(exit));
                criteriaCount.Set(ctx, AcceptanceCriteriaBindingHelper.CountCriteria(exit.DocumentJson));
                // D4 — one parent slot: the accepted Clarification, else the Findings, else none.
                parentDocumentId.Set(ctx, AcceptanceCriteriaBindingHelper.ChooseParentDocumentId(
                    clarificationDocId.Get(ctx), findingsDocId.Get(ctx)));

                return AcceptanceCriteriaBindingHelper.ProjectCriteria(exit.DocumentJson);
            })
        };
        readLifecycleExit.SetDisplayText("Read Lifecycle Exit");

        // ── Step 6: routing (typed values only) ────────────────────────
        var draftedGate = new FlowDecision(ctx => lifecycleDrafted.Get(ctx))
        { Id = "DocumentDrafted", Name = "Drafted?" };
        draftedGate.SetDisplayText("Drafted?");

        var acceptedGate = new FlowDecision(ctx => lifecycleAccepted.Get(ctx))
        { Id = "LifecycleAccepted", Name = "Accepted?" };
        acceptedGate.SetDisplayText("Accepted?");

        var emitDrafted = new EmitDomainLifecycleEventActivity
        {
            Id = "EmitAcceptanceCriteriaDrafted", Name = "Emit Acceptance Criteria Drafted",
            EventType = new(AcceptanceCriteriaEvents.Drafted),
            IssueId = new(ctx => issueId.Get(ctx)),
            Repository = new(ctx => repository.Get(ctx)),
            CorrelationId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            DocumentId = new(ctx => exitDocId.Get(ctx)),
            Detail = new(ctx => $"{criteriaCount.Get(ctx)} criteria drafted"),
            DataJson = new(ctx => AcceptanceCriteriaBindingHelper.BuildConsumedIdsJson(
                clarificationDocId.Get(ctx), findingsDocId.Get(ctx))),
        };
        emitDrafted.SetDisplayText("Emit Acceptance Criteria Drafted");

        var emitAccepted = new EmitDomainLifecycleEventActivity
        {
            Id = "EmitAcceptanceCriteriaAccepted", Name = "Emit Acceptance Criteria Accepted",
            EventType = new(AcceptanceCriteriaEvents.Accepted),
            IssueId = new(ctx => issueId.Get(ctx)),
            Repository = new(ctx => repository.Get(ctx)),
            CorrelationId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            DocumentId = new(ctx => exitDocId.Get(ctx)),
            Detail = new(ctx => $"{criteriaCount.Get(ctx)} criteria accepted"),
        };
        emitAccepted.SetDisplayText("Emit Acceptance Criteria Accepted");

        var emitFailed = new EmitDomainLifecycleEventActivity
        {
            Id = "EmitAcceptanceCriteriaFailed", Name = "Emit Acceptance Criteria Failed",
            EventType = new(AcceptanceCriteriaEvents.Failed),
            IssueId = new(ctx => issueId.Get(ctx)),
            Repository = new(ctx => repository.Get(ctx)),
            CorrelationId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            DocumentId = new(ctx => exitDocId.Get(ctx)),
            Detail = new(ctx => failureDetail.Get(ctx)),
        };
        emitFailed.SetDisplayText("Emit Acceptance Criteria Failed");

        // ── Step 7: Expose output — the single terminal region ─────────
        var exposeOutput = new Sequence
        {
            Id = "ExposeOutput", Name = "Expose Output",
            Activities =
            {
                // F7 — expose the session handle, matching AdrAuthoringWorkflow:332.
                WithLabel(new SetOutput { Id = "OutputSessionId", Name = "Output Session Id", OutputName = new("sessionId"), OutputValue = new(ctx => (object)sessionId.Get(ctx).ToString()) }, "Output Session Id"),
                WithLabel(new SetOutput { Id = "OutputStatus", Name = "Output Status", OutputName = new("status"), OutputValue = new(ctx => (object)(outputStatus.Get(ctx) ?? "")) }, "Output Status"),
                WithLabel(new SetOutput { Id = "OutputOutcome", Name = "Output Outcome", OutputName = new("outcome"), OutputValue = new(ctx => (object)(exitOutcome.Get(ctx) ?? "")) }, "Output Outcome"),
                WithLabel(new SetOutput { Id = "OutputDocumentId", Name = "Output Document Id", OutputName = new("documentId"), OutputValue = new(ctx => (object)(exitDocId.Get(ctx) ?? "")) }, "Output Document Id"),
                WithLabel(new SetOutput { Id = "OutputParentDocumentId", Name = "Output Parent Document Id", OutputName = new("parentDocumentId"), OutputValue = new(ctx => (object)(parentDocumentId.Get(ctx) ?? "")) }, "Output Parent Document Id"),
                WithLabel(new SetOutput { Id = "OutputAcceptanceCriteria", Name = "Output Acceptance Criteria", OutputName = new("acceptanceCriteriaJson"), OutputValue = new(ctx => (object)(criteriaJson.Get(ctx) ?? "[]")) }, "Output Acceptance Criteria"),
                WithLabel(new SetOutput { Id = "OutputError", Name = "Output Error", OutputName = new("error"), OutputValue = new(ctx => (object)(lifecycleAccepted.Get(ctx) ? "" : failureDetail.Get(ctx) ?? "")) }, "Output Error"),
            }
        };
        exposeOutput.SetDisplayText("Expose Output");

        // ── Build the flowchart ────────────────────────────────────────
        builder.Root = new Flowchart
        {
            Id = "AcceptanceCriteriaAuthoringFlowchart",
            Name = "Acceptance Criteria Authoring Flowchart",
            Start = readInputs,
            Activities =
            {
                readInputs, computeReEntry, readPositionStage, freshRun,
                emitStarted, fetchClarification, fetchFindings,
                dispatchLifecycle, readLifecycleExit,
                draftedGate, emitDrafted, acceptedGate, emitAccepted, emitFailed,
                exposeOutput,
            },
            Connections =
            {
                new(readInputs, computeReEntry),
                new(computeReEntry, readPositionStage),
                new(readPositionStage, freshRun),

                new(new FlowEndpoint(freshRun, "True"),  new FlowEndpoint(emitStarted)),
                new(emitStarted, fetchClarification),
                new(fetchClarification, fetchFindings),
                new(fetchFindings, dispatchLifecycle),
                new(new FlowEndpoint(freshRun, "False"), new FlowEndpoint(dispatchLifecycle)),

                new(dispatchLifecycle, readLifecycleExit),
                new(readLifecycleExit, draftedGate),

                new(new FlowEndpoint(draftedGate, "True"),  new FlowEndpoint(emitDrafted)),
                new(emitDrafted, acceptedGate),
                new(new FlowEndpoint(draftedGate, "False"), new FlowEndpoint(acceptedGate)),

                new(new FlowEndpoint(acceptedGate, "True"),  new FlowEndpoint(emitAccepted)),
                new(emitAccepted, exposeOutput),
                new(new FlowEndpoint(acceptedGate, "False"), new FlowEndpoint(emitFailed)),
                new(emitFailed, exposeOutput),
            }
        };
    }
}
