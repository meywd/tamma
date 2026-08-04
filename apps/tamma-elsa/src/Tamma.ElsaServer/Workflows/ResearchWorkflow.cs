using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Runtime.Activities;
using System.Text.Json;
using Tamma.Activities.Documents;
using Tamma.Activities.Research;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents.Resume;
using Tamma.ElsaServer.Workflows.Helpers;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Story 39-13 — Research, re-implemented as a THIN BINDING over
/// <see cref="DocumentLifecycleWorkflow"/> (<c>DefinitionId = "document-lifecycle"</c>),
/// producing a typed <see cref="Tamma.Core.Documents.Types.Findings"/> document through the
/// shared produce → validate → review → revise → accept loop. The public surface is
/// byte-stable (D1): same <c>DefinitionId = "research"</c>, same inputs, same outputs
/// (<c>sessionId</c>/<c>status</c>/<c>report</c>/<c>findingCount</c>/<c>confidence</c>/
/// <c>contextIds</c>) plus additive <c>outcome</c>/<c>documentId</c>.
///
/// <para>The old bespoke pipeline (<c>llm-call</c> → <c>ResearchParsing</c> → success-flag
/// gate → <c>ResearchError</c> Finish) is DELETED: NO parse, NO success-flag gate, ZERO
/// <see cref="Finish"/>. The binding gathers issue context (the <c>consumes</c> side),
/// dispatches the lifecycle with the canonical <c>(product_owner, research)</c> producer
/// cell, and lets the generic rings own all quality routing. Validation failure now exits
/// as a typed escalation with full lineage — never a dead terminal.</para>
/// </summary>
[ResumeBehavior(ResumeMode.LatestStateReEntry)]
public class ResearchWorkflow : WorkflowBase
{
    private const string FindingsDocumentType = "findings";
    private const string AmbiguityAssessmentDocumentType = "ambiguity-assessment";

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Research";
        builder.DefinitionId = "research";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Investigate an issue/topic and synthesize a ranked, confidence-scored findings document via the generic document lifecycle";

        // ── Inputs ─────────────────────────────────────────────────────
        var sessionId       = builder.WithVariable<Guid>();
        var issueId         = builder.WithVariable<string>();
        var topic           = builder.WithVariable<string>();
        var repository      = builder.WithVariable<string>();
        var issueNumber     = builder.WithVariable<int>();
        var workItemJson    = builder.WithVariable<string>();
        var tenantId        = builder.WithVariable<string>("TenantId", "");
        var acceptanceRulesJson = builder.WithVariable<string>("AcceptanceRulesJson", "");

        // ── Context (consumes side) ────────────────────────────────────
        var researchContext = builder.WithVariable<string>();
        var contextIds      = builder.WithVariable<string>("[]");

        // ── Story 39-25 — threaded ambiguity score (leg 1) ─────────────
        var assessmentFound = builder.WithVariable<bool>();
        var assessmentJson  = builder.WithVariable<string>("AssessmentJson", "{}");

        // ── 39-10 re-entry position ────────────────────────────────────
        var reEntryPositionJson = builder.WithVariable<string>();
        var reEntryDocJson  = builder.WithVariable<string>();
        var positionStage   = builder.WithVariable<string>("PositionStage", "produce");

        // ── Dispatched-workflow result containers ──────────────────────
        var contextGatherResult = builder.WithVariable<IDictionary<string, object>?>();
        var lifecycleResult = builder.WithVariable<IDictionary<string, object>?>();

        // ── Typed lifecycle exit ───────────────────────────────────────
        var lifecycleAccepted = builder.WithVariable<bool>();
        var exitStatus      = builder.WithVariable<string>("ExitStatus", "");
        var exitOutcome     = builder.WithVariable<string>("ExitOutcome", "");
        var exitDocId       = builder.WithVariable<string>("ExitDocId", "");
        var reportJson      = builder.WithVariable<string>("ReportJson", "{}");
        var findingCount    = builder.WithVariable<int>();
        var confidence      = builder.WithVariable<double>();
        var failureDetail   = builder.WithVariable<string>("FailureDetail", "");
        var outputStatus    = builder.WithVariable<string>();

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
                topic.Set(context, context.GetInput<string>("topic") ?? string.Empty);
                repository.Set(context, context.GetInput<string>("repository") ?? string.Empty);
                issueNumber.Set(context, context.GetInput<int>("issueNumber"));
                workItemJson.Set(context, context.GetInput<string>("workItemJson") ?? string.Empty);
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

        // ── Step 3: FreshRun gate — pre-produce region only on a fresh run ──
        var freshRun = new FlowDecision(ctx => positionStage.Get(ctx) == "produce")
        { Id = "FreshRun", Name = "Fresh Run?" };
        freshRun.SetDisplayText("Fresh Run?");

        var emitStarted = new EmitResearchEventActivity
        {
            Id = "EmitResearchStarted", Name = "Emit Research Started",
            EventType = new(ResearchEvents.Started),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Detail = new("Research investigation started"),
        };
        emitStarted.SetDisplayText("Emit Research Started");

        var gatherContext = new DispatchWorkflow
        {
            Id = "GatherContext", Name = "Gather Context",
            WorkflowDefinitionId = new("context-gathering"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"]   = repository.Get(ctx) ?? "",
                ["issueNumber"]  = issueNumber.Get(ctx),
                ["workItemJson"] = BuildWorkItem(topic.Get(ctx), workItemJson.Get(ctx), issueId.Get(ctx)),
                ["tenantId"]     = tenantId.Get(ctx) ?? "",
            }),
            WaitForCompletion = new(true),
            Result = new(contextGatherResult),
        };
        gatherContext.SetDisplayText("Gather Context");

        var storeContextResult = new SetVariable
        {
            Id = "StoreContextResult", Name = "Store Context Result",
            Variable = researchContext,
            Value = new(ctx =>
            {
                var result = contextGatherResult.Get(ctx);
                if (result != null && result.TryGetValue("contextIds", out var ids) && ids != null)
                    contextIds.Set(ctx, ids.ToString() ?? "[]");
                if (result != null && result.TryGetValue("summary", out var s) && s != null)
                    return s.ToString() ?? string.Empty;
                return string.Empty;
            })
        };
        storeContextResult.SetDisplayText("Store Context Result");

        var emitContextGathered = new EmitResearchEventActivity
        {
            Id = "EmitContextGathered", Name = "Emit Context Gathered",
            EventType = new(ResearchEvents.ContextGathered),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Detail = new("Codebase / prior-art context gathered via context-gathering"),
        };
        emitContextGathered.SetDisplayText("Emit Context Gathered");

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

        // ── Step 4: Dispatch the generic document lifecycle ────────────
        var dispatchLifecycle = new DispatchWorkflow
        {
            Id = "DispatchLifecycle", Name = "Dispatch Document Lifecycle",
            WorkflowDefinitionId = new("document-lifecycle"),
            Input = new(ctx =>
            {
                var input = new Dictionary<string, object>
                {
                    ["documentType"]          = FindingsDocumentType,
                    ["producerRole"]          = AgentRole.ProductOwner.ToWire(),
                    ["producerAction"]        = AgentAction.Research.ToWire(),
                    ["producerVariablesJson"] = JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["workItemJson"] = BuildWorkItem(topic.Get(ctx), workItemJson.Get(ctx), issueId.Get(ctx)),
                        ["findings"]     = researchContext.Get(ctx) ?? "",
                        ["conventions"]  = "",
                    }),
                    ["issueId"]             = issueId.Get(ctx) ?? "",
                    ["correlationId"]       = issueId.Get(ctx) ?? "",
                    ["tenantId"]            = tenantId.Get(ctx) ?? "",
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
            Variable = reportJson,
            Value = new(ctx =>
            {
                var exit = LifecycleBindingHelper.ReadLifecycleResult(lifecycleResult.Get(ctx));
                var accepted = LifecycleBindingHelper.IsAccepted(exit);
                var (fc, conf) = AssessmentBindingHelper.ReadFindings(exit.DocumentJson);

                lifecycleAccepted.Set(ctx, accepted);
                exitStatus.Set(ctx, exit.Status);
                exitOutcome.Set(ctx, exit.Outcome ?? "");
                exitDocId.Set(ctx, exit.DocumentId ?? "");
                findingCount.Set(ctx, fc);
                confidence.Set(ctx, conf);
                failureDetail.Set(ctx, AssessmentBindingHelper.BuildFailureDetail(exit));
                outputStatus.Set(ctx, accepted ? "completed" : exit.Status);
                return exit.DocumentJson;
            })
        };
        readLifecycleExit.SetDisplayText("Read Lifecycle Exit");

        // ── Step 6: Accepted? (typed) ──────────────────────────────────
        var lifecycleAcceptedGate = new FlowDecision(ctx => lifecycleAccepted.Get(ctx))
        { Id = "LifecycleAccepted", Name = "Lifecycle Accepted?" };
        lifecycleAcceptedGate.SetDisplayText("Lifecycle Accepted?");

        var wasCompleteReEntry = new FlowDecision(ctx => positionStage.Get(ctx) == "complete")
        { Id = "WasCompleteReEntry", Name = "Was Complete Re-Entry?" };
        wasCompleteReEntry.SetDisplayText("Was Complete Re-Entry?");

        var emitCompleted = new EmitResearchEventActivity
        {
            Id = "EmitResearchCompleted", Name = "Emit Research Completed",
            EventType = new(ResearchEvents.Completed),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            FindingCount = new(ctx => findingCount.Get(ctx)),
            Confidence = new(ctx => confidence.Get(ctx)),
            Detail = new("Ranked, confidence-scored findings document accepted"),
        };
        emitCompleted.SetDisplayText("Emit Research Completed");

        var emitFailed = new EmitResearchEventActivity
        {
            Id = "EmitResearchFailed", Name = "Emit Research Failed",
            EventType = new(ResearchEvents.Failed),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Detail = new(ctx => failureDetail.Get(ctx)),
        };
        emitFailed.SetDisplayText("Emit Research Failed");

        // ── Step 7: Expose output — the single terminal region ─────────
        var exposeOutput = new Sequence
        {
            Id = "ExposeOutput", Name = "Expose Output",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutputSessionId", Name = "Output Session Id", OutputName = new("sessionId"), OutputValue = new(ctx => (object)sessionId.Get(ctx).ToString()) }, "Output Session Id"),
                WithLabel(new SetOutput { Id = "OutputStatus", Name = "Output Status", OutputName = new("status"), OutputValue = new(ctx => (object)(outputStatus.Get(ctx) ?? "")) }, "Output Status"),
                WithLabel(new SetOutput { Id = "OutputReport", Name = "Output Report", OutputName = new("report"), OutputValue = new(ctx => (object)(reportJson.Get(ctx) ?? "{}")) }, "Output Report"),
                WithLabel(new SetOutput { Id = "OutputFindingCount", Name = "Output Finding Count", OutputName = new("findingCount"), OutputValue = new(ctx => (object)findingCount.Get(ctx)) }, "Output Finding Count"),
                WithLabel(new SetOutput { Id = "OutputConfidence", Name = "Output Confidence", OutputName = new("confidence"), OutputValue = new(ctx => (object)confidence.Get(ctx)) }, "Output Confidence"),
                WithLabel(new SetOutput { Id = "OutputContextIds", Name = "Output Context Ids", OutputName = new("contextIds"), OutputValue = new(ctx => (object)(contextIds.Get(ctx) ?? "[]")) }, "Output Context Ids"),
                WithLabel(new SetOutput { Id = "OutputOutcome", Name = "Output Outcome", OutputName = new("outcome"), OutputValue = new(ctx => (object)(exitOutcome.Get(ctx) ?? "")) }, "Output Outcome"),
                WithLabel(new SetOutput { Id = "OutputDocumentId", Name = "Output Document Id", OutputName = new("documentId"), OutputValue = new(ctx => (object)(exitDocId.Get(ctx) ?? "")) }, "Output Document Id"),
            }
        };
        exposeOutput.SetDisplayText("Expose Output");

        // ── Build the flowchart ────────────────────────────────────────
        builder.Root = new Flowchart
        {
            Id = "ResearchFlowchart",
            Name = "Research Flowchart",
            Start = readInputs,
            Activities =
            {
                readInputs, computeReEntry, readPositionStage, freshRun,
                emitStarted, gatherContext, storeContextResult, emitContextGathered,
                fetchAmbiguityAssessment, dispatchLifecycle, readLifecycleExit,
                lifecycleAcceptedGate, wasCompleteReEntry, emitCompleted, emitFailed,
                exposeOutput,
            },
            Connections =
            {
                new(readInputs, computeReEntry),
                new(computeReEntry, readPositionStage),
                new(readPositionStage, freshRun),

                new(new FlowEndpoint(freshRun, "True"),  new FlowEndpoint(emitStarted)),
                new(emitStarted, gatherContext),
                new(gatherContext, storeContextResult),
                new(storeContextResult, emitContextGathered),
                // 39-25 — the ambiguity fetch is the single predecessor of the dispatch,
                // so it runs on every path that actually dispatches (fresh + re-entry).
                new(emitContextGathered, fetchAmbiguityAssessment),
                new(new FlowEndpoint(freshRun, "False"), new FlowEndpoint(fetchAmbiguityAssessment)),
                new(fetchAmbiguityAssessment, dispatchLifecycle),

                new(dispatchLifecycle, readLifecycleExit),
                new(readLifecycleExit, lifecycleAcceptedGate),

                new(new FlowEndpoint(lifecycleAcceptedGate, "True"),  new FlowEndpoint(wasCompleteReEntry)),
                new(new FlowEndpoint(wasCompleteReEntry, "False"), new FlowEndpoint(emitCompleted)),
                new(emitCompleted, exposeOutput),
                new(new FlowEndpoint(wasCompleteReEntry, "True"),  new FlowEndpoint(exposeOutput)),

                new(new FlowEndpoint(lifecycleAcceptedGate, "False"), new FlowEndpoint(emitFailed)),
                new(emitFailed, exposeOutput),
            }
        };
    }

    /// <summary>
    /// Compose the work-item JSON handed to the context-gathering scan and the findings
    /// producer. Prefers an explicit <paramref name="workItemJson"/>; otherwise wraps the
    /// free-text <paramref name="topic"/> (plus the issue id) into a minimal JSON object.
    /// Pure; exposed for unit testing.
    /// </summary>
    internal static string BuildWorkItem(string? topic, string? workItemJson, string? issueId)
    {
        if (!string.IsNullOrWhiteSpace(workItemJson))
            return workItemJson!;

        return JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["type"] = "research",
            ["issueId"] = issueId ?? "",
            ["topic"] = topic ?? "",
        });
    }
}
