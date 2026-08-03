using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Runtime.Activities;
using System.Text.Json;
using Tamma.Activities.Adr;
using Tamma.Activities.Documents;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents.Resume;
using Tamma.ElsaServer.Workflows.Helpers;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Story 41-9 — ADR Authoring: a THIN BINDING over <see cref="DocumentLifecycleWorkflow"/>
/// producing a <see cref="Tamma.Core.Documents.Types.Prose"/> document with
/// <c>kind = adr</c> and <c>audience = engineering</c> from the <c>(architect, write-adr)</c>
/// produce cell. An Architecture Decision Record becomes a document the platform produces,
/// reviews, accepts, persists and can query by issue — not a markdown file someone remembers to
/// commit. The <c>.dev/decisions/</c> repo convention is untouched; migrating those files into
/// the store is explicitly out of scope.
///
/// <para><b>This is the designated REFERENCE IMPLEMENTATION of the prose-on-lifecycle path</b>
/// for 41-4, 41-5, 41-8's narrative, 41-22, 41-24, 41-25 and 41-26. What those seven inherit:
/// this binding's graph shape, D3's producer-scoped issue id, D4's ENVELOPE-only contract
/// posture, and D6's event-family convention.</para>
///
/// <para><b>Prose stays prose (41-1c D1).</b> The dispatched type is <c>prose</c>, whose
/// validator checks ENVELOPE facts only — <c>kind</c> and <c>audience</c> in their closed
/// vocabularies, a non-empty <c>title</c>, a non-whitespace <c>body</c>. The ADR shape convention
/// (context / decision / consequences / alternatives-considered) is guidance inside the prompt
/// body, deliberately NOT a validated schema and NOT a contract token (D4).</para>
///
/// <para><b>D3 — producer-scoped lifecycle issue id.</b> Seven prose producers can write
/// <c>prose</c> for the SAME issue, and the 39-11 latest-accepted read scopes by
/// <c>(issueId, documentType)</c> with NO producer filter (the same collision 39-15 D2 solved for
/// the two plan producers). This binding therefore keys its lifecycle on
/// <c>{issueId}#adr</c>. The general fix — a producer or <c>kind</c> filter on the 39-11 read —
/// is FILED against 39-11 rather than solved locally seven times.</para>
///
/// <para><b>D7 — acceptance policy is passed through, never hardcoded.</b> 41-1c set the prose
/// default to a single <c>tech_writer</c> reviewer; an ADR wants architect eyes, but that is a
/// per-KIND preference and <c>AcceptanceDefaults.For</c> is per-TYPE (41-1c's file). The binding
/// therefore forwards a caller-supplied <c>acceptanceRulesJson</c> and defaults to nothing of its
/// own. The always-escalate mechanism already accepts <c>AgentAction.WriteAdr</c> as an
/// <c>EscalationClass</c> — no new machinery.</para>
///
/// <para><b>Resumable by design.</b> <c>[ResumeBehavior(LatestStateReEntry)]</c> with a
/// <see cref="ComputeReEntryPositionActivity"/> node and NO allowlist entry: a thin binding owns
/// no bookmark — the accept gate suspends inside the dispatched <c>document-lifecycle</c> child,
/// which this parent awaits with <c>WaitForCompletion = true</c>. Zero <see cref="Finish"/>, zero
/// <c>llm-call</c> dispatch, zero parsing, zero validate/retry plumbing.</para>
/// </summary>
[ResumeBehavior(ResumeMode.LatestStateReEntry)]
public class AdrAuthoringWorkflow : WorkflowBase
{
    private const string ProseDocumentType = "prose";
    private const string DesignDocumentType = "design";
    private const string FindingsDocumentType = "findings";
    private const string AmbiguityAssessmentDocumentType = "ambiguity-assessment";

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "ADR Authoring";
        builder.DefinitionId = "adr-authoring";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Capture a significant technical decision as an audience-tagged prose ADR via the generic document lifecycle (produce → validate → review → revise → accept)";

        // ── Inputs ─────────────────────────────────────────────────────
        var sessionId    = builder.WithVariable<Guid>();
        var issueId      = builder.WithVariable<string>("IssueId", "");
        var scopedIssueId = builder.WithVariable<string>("ScopedIssueId", "");
        var repository   = builder.WithVariable<string>("Repository", "");
        var issueNumber  = builder.WithVariable<int>("IssueNumber", 0);
        var workItemJson = builder.WithVariable<string>("WorkItemJson", "");
        var decisionContext = builder.WithVariable<string>("DecisionContext", "");
        var audience     = builder.WithVariable<string>("Audience", "");
        var tenantId     = builder.WithVariable<string>("TenantId", "");
        var acceptanceRulesJson = builder.WithVariable<string>("AcceptanceRulesJson", "");

        // ── Consumed upstream documents (D2 — both optional, fail-closed) ──
        var designFound   = builder.WithVariable<bool>();
        var designDocId   = builder.WithVariable<string>("DesignDocId", "");
        var designJson    = builder.WithVariable<string>("DesignJson", "");
        var designLineage = builder.WithVariable<string>();
        var findingsFound   = builder.WithVariable<bool>();
        var findingsDocId   = builder.WithVariable<string>("FindingsDocId", "");
        var findingsJson    = builder.WithVariable<string>("FindingsJson", "");
        var findingsLineage = builder.WithVariable<string>();

        // ── Story 39-25 — threaded ambiguity score (leg 1) ─────────────
        var assessmentFound = builder.WithVariable<bool>();
        var assessmentJson  = builder.WithVariable<string>("AssessmentJson", "{}");

        // ── 39-10 re-entry position ────────────────────────────────────
        var reEntryPositionJson = builder.WithVariable<string>();
        var reEntryDocJson  = builder.WithVariable<string>();
        var positionStage   = builder.WithVariable<string>("PositionStage", "produce");

        // ── Dispatched-workflow result + typed exit ────────────────────
        var lifecycleResult   = builder.WithVariable<IDictionary<string, object>?>();
        var lifecycleAccepted = builder.WithVariable<bool>();
        var lifecycleDrafted  = builder.WithVariable<bool>();
        var exitOutcome       = builder.WithVariable<string>("ExitOutcome", "");
        var exitDocId         = builder.WithVariable<string>("ExitDocId", "");
        var adrJson           = builder.WithVariable<string>("AdrJson", "");
        var acceptedAudience  = builder.WithVariable<string>("AcceptedAudience", "");
        var failureDetail     = builder.WithVariable<string>("FailureDetail", "");
        var outputStatus      = builder.WithVariable<string>();

        // ── Step 1: Read inputs ────────────────────────────────────────
        var readInputs = new SetVariable
        {
            Id = "ReadInputs", Name = "Read Inputs",
            Variable = sessionId,
            Value = new(ctx =>
            {
                var sid = ctx.GetInput<Guid>("sessionId");
                if (sid == Guid.Empty) sid = Guid.NewGuid();

                var repo = ctx.GetInput<string>("repository") ?? "";
                repository.Set(ctx, repo);
                issueNumber.Set(ctx, ctx.GetInput<int>("issueNumber"));
                workItemJson.Set(ctx, ctx.GetInput<string>("workItemJson") ?? "");
                decisionContext.Set(ctx, ctx.GetInput<string>("decisionContext") ?? "");
                tenantId.Set(ctx, ctx.GetInput<string>("tenantId") ?? "");
                acceptanceRulesJson.Set(ctx, ctx.GetInput<string>("acceptanceRulesJson") ?? "");
                // 41-1c's audience vocabulary is CLOSED — a caller typo falls back to the ADR
                // default rather than burning a repair round on PROSE_AUDIENCE_OUT_OF_VOCABULARY.
                audience.Set(ctx, AdrBindingHelper.ResolveAudience(ctx.GetInput<string>("audience")));

                var explicitIssueId = ctx.GetInput<string>("issueId") ?? "";
                var baseIssueId = string.IsNullOrWhiteSpace(explicitIssueId)
                    ? CreationBindingHelper.DeriveIssueId(repo, ctx.GetInput<int>("issueNumber"))
                    : explicitIssueId;
                issueId.Set(ctx, baseIssueId);
                // D3 — the prose-family producer scope, inherited by the other seven prose stories.
                scopedIssueId.Set(ctx, CreationBindingHelper.ScopeIssueId(baseIssueId, AdrBindingHelper.ProducerScope));
                return sid;
            })
        };
        readInputs.SetDisplayText("Read Inputs");

        // ── Step 2: Compute 39-10 re-entry position ────────────────────
        var computeReEntry = new ComputeReEntryPositionActivity
        {
            Id = "ComputeReEntryPosition", Name = "Compute Re-Entry Position",
            IssueId = new(ctx => scopedIssueId.Get(ctx)),
            DocumentType = new(ProseDocumentType),
            TenantId = new(ctx => tenantId.Get(ctx)),
            CorrelationId = new(ctx => scopedIssueId.Get(ctx)),
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

        // ── Step 3: FreshRun gate — a re-entry is not a new ADR ─────────
        var freshRun = new FlowDecision(ctx => positionStage.Get(ctx) == "produce")
        { Id = "FreshRun", Name = "Fresh Run?" };
        freshRun.SetDisplayText("Fresh Run?");

        var emitStarted = new EmitDomainLifecycleEventActivity
        {
            Id = "EmitAdrStarted", Name = "Emit ADR Started",
            EventType = new(AdrEvents.Started),
            IssueId = new(ctx => issueId.Get(ctx)),
            Repository = new(ctx => repository.Get(ctx)),
            CorrelationId = new(ctx => scopedIssueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Detail = new(ctx => $"audience={audience.Get(ctx)}"),
        };
        emitStarted.SetDisplayText("Emit ADR Started");

        // The design seed: 41-10's output, which `design-proposal` already produces today, so
        // this node is useful before 41-10 lands. Read on the BASE issue id (design's own scope).
        var fetchDesign = new FetchLatestAcceptedDocumentActivity
        {
            Id = "FetchConsumedDesign", Name = "Fetch Accepted Design",
            IssueId = new(ctx => issueId.Get(ctx)),
            DocumentTypeKey = new(DesignDocumentType),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Found = new(designFound),
            DocumentId = new(designDocId),
            DocumentJson = new(designJson),
            LineageJson = new(designLineage),
        };
        fetchDesign.SetDisplayText("Fetch Accepted Design");

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

        // ── Story 39-25 (leg 1): fetch the latest ACCEPTED ambiguity-assessment ──
        // Fail-closed: no accepted assessment for this run's anchor ⇒ Found=false ⇒ the
        // ambiguityScore dispatch key below is OMITTED (never a fabricated 0.0).
        // Read on the BASE issue id (the run identity the assessment is persisted under),
        // like FetchConsumedDesign/Findings — NOT the prose-family producer scope.
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
                    ["documentType"]          = ProseDocumentType,
                    ["producerRole"]          = AgentRole.Architect.ToWire(),
                    ["producerAction"]        = AgentAction.WriteAdr.ToWire(),
                    ["producerVariablesJson"] = JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["workItemJson"] = workItemJson.Get(ctx) ?? "",
                        // D5 — the seed context rides the DECLARED `findings` carrier
                        // (write-adr.md declares role, workItemJson, findings, audience).
                        ["findings"] = AdrBindingHelper.BuildDecisionContext(
                            designJson.Get(ctx), findingsJson.Get(ctx), decisionContext.Get(ctx)),
                        ["audience"] = audience.Get(ctx) ?? AdrBindingHelper.DefaultAudience,
                    }),
                    // 39-6 D11 / 39-15 render-drop lesson — repair/revise notes land in a DECLARED key.
                    ["feedbackVariableName"] = "findings",
                    // D3 — producer-scoped so a sibling prose producer's document for the same issue
                    // is not mistaken for this binding's latest-accepted.
                    ["issueId"]             = scopedIssueId.Get(ctx) ?? "",
                    ["correlationId"]       = scopedIssueId.Get(ctx) ?? "",
                    ["sessionId"]           = sessionId.Get(ctx),
                    ["tenantId"]            = tenantId.Get(ctx) ?? "",
                    // D7 — acceptance posture is the caller's; the binding hardcodes none.
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

        // ── Step 5: Read the typed lifecycle exit (fail-closed) ────────
        var readLifecycleExit = new SetVariable
        {
            Id = "ReadLifecycleExit", Name = "Read Lifecycle Exit",
            Variable = adrJson,
            Value = new(ctx =>
            {
                var exit = LifecycleBindingHelper.ReadLifecycleResult(lifecycleResult.Get(ctx));
                var accepted = LifecycleBindingHelper.IsAccepted(exit);

                lifecycleAccepted.Set(ctx, accepted);
                lifecycleDrafted.Set(ctx, !string.IsNullOrWhiteSpace(exit.DocumentId));
                exitOutcome.Set(ctx, exit.Outcome ?? "");
                exitDocId.Set(ctx, exit.DocumentId ?? "");
                outputStatus.Set(ctx, accepted ? "completed" : exit.Status);
                failureDetail.Set(ctx, AdrBindingHelper.BuildFailureDetail(exit));
                acceptedAudience.Set(ctx, AdrBindingHelper.ReadAudience(exit.DocumentJson));

                return AdrBindingHelper.ProjectAdrBody(exit.DocumentJson);
            })
        };
        readLifecycleExit.SetDisplayText("Read Lifecycle Exit");

        // ── Step 6: routing (typed values only) ────────────────────────
        var draftedGate = new FlowDecision(ctx => lifecycleDrafted.Get(ctx))
        { Id = "DocumentDrafted", Name = "Drafted?" };
        draftedGate.SetDisplayText("Drafted?");

        var acceptedGate = new FlowDecision(ctx => lifecycleAccepted.Get(ctx))
        { Id = "AdrAccepted", Name = "Accepted?" };
        acceptedGate.SetDisplayText("Accepted?");

        var emitDrafted = new EmitDomainLifecycleEventActivity
        {
            Id = "EmitAdrDrafted", Name = "Emit ADR Drafted",
            EventType = new(AdrEvents.Drafted),
            IssueId = new(ctx => issueId.Get(ctx)),
            Repository = new(ctx => repository.Get(ctx)),
            CorrelationId = new(ctx => scopedIssueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            DocumentId = new(ctx => exitDocId.Get(ctx)),
            Detail = new(ctx => $"kind={AdrBindingHelper.Kind} audience={acceptedAudience.Get(ctx)}"),
        };
        emitDrafted.SetDisplayText("Emit ADR Drafted");

        var emitAccepted = new EmitDomainLifecycleEventActivity
        {
            Id = "EmitAdrAccepted", Name = "Emit ADR Accepted",
            EventType = new(AdrEvents.Accepted),
            IssueId = new(ctx => issueId.Get(ctx)),
            Repository = new(ctx => repository.Get(ctx)),
            CorrelationId = new(ctx => scopedIssueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            DocumentId = new(ctx => exitDocId.Get(ctx)),
            Detail = new(ctx => $"kind={AdrBindingHelper.Kind} audience={acceptedAudience.Get(ctx)}"),
        };
        emitAccepted.SetDisplayText("Emit ADR Accepted");

        var emitFailed = new EmitDomainLifecycleEventActivity
        {
            Id = "EmitAdrFailed", Name = "Emit ADR Failed",
            EventType = new(AdrEvents.Failed),
            IssueId = new(ctx => issueId.Get(ctx)),
            Repository = new(ctx => repository.Get(ctx)),
            CorrelationId = new(ctx => scopedIssueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            DocumentId = new(ctx => exitDocId.Get(ctx)),
            Detail = new(ctx => failureDetail.Get(ctx)),
        };
        emitFailed.SetDisplayText("Emit ADR Failed");

        // ── Step 7: Expose output — the single terminal region ─────────
        var exposeOutput = new Sequence
        {
            Id = "ExposeOutput", Name = "Expose Output",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutputSessionId", Name = "Output Session Id", OutputName = new("sessionId"), OutputValue = new(ctx => (object)sessionId.Get(ctx).ToString()) }, "Output Session Id"),
                WithLabel(new SetOutput { Id = "OutputStatus", Name = "Output Status", OutputName = new("status"), OutputValue = new(ctx => (object)(outputStatus.Get(ctx) ?? "")) }, "Output Status"),
                WithLabel(new SetOutput { Id = "OutputOutcome", Name = "Output Outcome", OutputName = new("outcome"), OutputValue = new(ctx => (object)(exitOutcome.Get(ctx) ?? "")) }, "Output Outcome"),
                WithLabel(new SetOutput { Id = "OutputDocumentId", Name = "Output Document Id", OutputName = new("documentId"), OutputValue = new(ctx => (object)(exitDocId.Get(ctx) ?? "")) }, "Output Document Id"),
                WithLabel(new SetOutput { Id = "OutputAdr", Name = "Output ADR", OutputName = new("adrJson"), OutputValue = new(ctx => (object)(adrJson.Get(ctx) ?? "")) }, "Output ADR"),
                WithLabel(new SetOutput { Id = "OutputError", Name = "Output Error", OutputName = new("error"), OutputValue = new(ctx => (object)(lifecycleAccepted.Get(ctx) ? "" : failureDetail.Get(ctx) ?? "")) }, "Output Error"),
            }
        };
        exposeOutput.SetDisplayText("Expose Output");

        // ── Build the flowchart ────────────────────────────────────────
        builder.Root = new Flowchart
        {
            Id = "AdrAuthoringFlowchart",
            Name = "ADR Authoring Flowchart",
            Start = readInputs,
            Activities =
            {
                readInputs, computeReEntry, readPositionStage, freshRun,
                emitStarted, fetchDesign, fetchFindings, fetchAmbiguityAssessment,
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
                new(emitStarted, fetchDesign),
                new(fetchDesign, fetchFindings),
                // 39-25 — the ambiguity fetch is the single predecessor of the dispatch,
                // so it runs on every path that actually dispatches (fresh + re-entry).
                new(fetchFindings, fetchAmbiguityAssessment),
                new(new FlowEndpoint(freshRun, "False"), new FlowEndpoint(fetchAmbiguityAssessment)),
                new(fetchAmbiguityAssessment, dispatchLifecycle),

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
