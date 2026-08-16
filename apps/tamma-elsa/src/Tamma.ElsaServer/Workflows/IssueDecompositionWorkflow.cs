using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Runtime.Activities;
using System.Text.Json;
using Tamma.Activities.Decomposition;
using Tamma.Activities.Documents;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents.Resume;
using Tamma.ElsaServer.Workflows.Helpers;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Story 39-12 — Issue Decomposition, re-implemented as a THIN BINDING over
/// <see cref="DocumentLifecycleWorkflow"/> (<c>DefinitionId = "document-lifecycle"</c>).
/// The public surface is byte-stable (D1): same <c>DefinitionId = "issue-decomposition"</c>,
/// same inputs (<c>sessionId</c>/<c>issueId</c>/<c>issueTitle</c>/<c>repository</c>/
/// <c>issueNumber</c>/<c>workItemJson</c>/<c>tenantId</c>, plus an optional passthrough
/// <c>acceptanceRulesJson</c>), same outputs (<c>sessionId</c>/<c>status</c>/
/// <c>decomposition</c>/<c>subtaskCount</c>/<c>contextIds</c>) plus additive
/// <c>outcome</c>/<c>documentId</c>. Dispatch call sites (orchestrator / triage routing,
/// by definition id) are untouched.
///
/// <para><b>What changed (the epic's charter).</b> The old bespoke pipeline —
/// <c>llm-call</c> → hand parser (<c>DecompositionParsing</c>) → success-flag gate →
/// <c>DecompositionError</c> Finish terminal — is DELETED. The binding contributes NO
/// parse, NO success-flag gate, and ZERO <see cref="Finish"/> activities (D2). It
/// assembles the issue context (the <c>consumes</c> side), dispatches
/// <c>document-lifecycle</c> with <c>documentType = "decomposition"</c> and the
/// <c>(senior_developer, decompose-issue)</c> producer cell, and lets the generic
/// produce → validate → review → revise → accept rings own ALL quality routing.
/// Validation failure now flows through those rings and, at worst, exits as a typed
/// escalation (<c>validation-exhausted</c> / <c>rounds-exhausted</c> /
/// <c>review-undecidable</c>) with full lineage — never a dead terminal.</para>
///
/// <para><b>The only routing here (D2).</b> Exactly three <see cref="FlowDecision"/>s,
/// each on a TYPED value (never raw LLM output):
/// <list type="bullet">
///   <item><c>FreshRun</c> — the 39-10 re-entry position is <c>Produce</c>: run the
///     pre-produce region (STARTED + context scan + CONTEXT_GATHERED). A re-entry is not
///     a new decomposition, so those emissions are skipped (D7).</item>
///   <item><c>LifecycleAccepted</c> — the lifecycle exit status is <c>accepted</c>.</item>
///   <item><c>WasCompleteReEntry</c> — the re-entry short-circuited an already-accepted
///     document (<c>Complete</c>): suppress a duplicate <c>DECOMPOSITION.COMPLETED</c>
///     (D3).</item>
/// </list></para>
///
/// <para><b>Event compatibility (D3, AC4).</b> The legacy <c>DECOMPOSITION.*</c> events
/// are mirrored at the equivalent transitions ALONGSIDE the lifecycle's generic
/// <c>DOCUMENT.*</c> events: <c>DECOMPOSITION.STARTED</c> + <c>DECOMPOSITION.CONTEXT_GATHERED</c>
/// before dispatch (fresh runs only); <c>DECOMPOSITION.COMPLETED</c> (with the sub-task
/// count sourced from the accepted <c>Decomposition</c> payload) on an <c>accepted</c>
/// exit; <c>DECOMPOSITION.FAILED</c> on a <c>rejected</c>/<c>escalated</c> exit, its
/// detail naming the typed outcome. <see cref="EmitDecompositionEventActivity"/> and its
/// event catalogue are UNCHANGED.</para>
///
/// <para><b>Resumable per the standard (D7, AC6).</b> Declared
/// <c>[ResumeBehavior(LatestStateReEntry)]</c> — the binding itself never suspends on a
/// bookmark (the accept-gate suspend happens inside the dispatched child lifecycle, which
/// the parent awaits via <c>WaitForCompletion</c>), and it carries the generic
/// <see cref="ComputeReEntryPositionActivity"/> node the 39-10 structural gate requires
/// for this mode. It is NOT on the legacy resume allowlist (the first burn-down).</para>
/// </summary>
[ResumeBehavior(ResumeMode.LatestStateReEntry)]
public class IssueDecompositionWorkflow : WorkflowBase
{
    private const string DecompositionDocumentType = "decomposition";
    private const string AmbiguityAssessmentDocumentType = "ambiguity-assessment";

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "IssueDecomposition";
        builder.DefinitionId = "issue-decomposition";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Decompose a complex issue into an ordered set of implementable sub-tasks via the generic document lifecycle (produce → validate → review → revise → accept)";

        // ── Workflow variables (inputs) ────────────────────────────────
        var sessionId       = builder.WithVariable<Guid>().Persisted();
        var issueId         = builder.WithVariable<string>().Persisted();
        var issueTitle      = builder.WithVariable<string>().Persisted();
        var repository      = builder.WithVariable<string>().Persisted();
        var issueNumber     = builder.WithVariable<int>().Persisted();
        var workItemJson    = builder.WithVariable<string>().Persisted();
        var tenantId        = builder.WithVariable<string>("TenantId", "").Persisted();
        var acceptanceRulesJson = builder.WithVariable<string>("AcceptanceRulesJson", "").Persisted();

        // ── Context (consumes side) ────────────────────────────────────
        var decompositionContext = builder.WithVariable<string>().Persisted();
        var contextIds      = builder.WithVariable<string>("[]").Persisted();

        // ── 39-10 re-entry position (D7) ───────────────────────────────
        var reEntryPositionJson = builder.WithVariable<string>().Persisted();
        var reEntryDocJson  = builder.WithVariable<string>().Persisted();
        var positionStage   = builder.WithVariable<string>("PositionStage", "produce").Persisted();

        // ── Story 39-25 — threaded ambiguity score (leg 1) ─────────────
        var assessmentFound = builder.WithVariable<bool>().Persisted();
        var assessmentJson  = builder.WithVariable<string>("AssessmentJson", "{}").Persisted();

        // ── Dispatched-workflow result containers ──────────────────────
        var contextGatherResult = builder.WithVariable<IDictionary<string, object>?>().Persisted();
        var lifecycleResult = builder.WithVariable<IDictionary<string, object>?>().Persisted();

        // ── Typed lifecycle exit (D2 — routed values, never raw output) ─
        var lifecycleAccepted = builder.WithVariable<bool>().Persisted();
        var exitStatus      = builder.WithVariable<string>("ExitStatus", "").Persisted();
        var exitOutcome     = builder.WithVariable<string>("ExitOutcome", "").Persisted();
        var exitDocId       = builder.WithVariable<string>("ExitDocId", "").Persisted();
        var decompositionJson = builder.WithVariable<string>("DecompositionJson", "{}").Persisted();
        var subtaskCount    = builder.WithVariable<int>().Persisted();
        var failureDetail   = builder.WithVariable<string>("FailureDetail", "").Persisted();

        // ── Output ─────────────────────────────────────────────────────
        var outputStatus    = builder.WithVariable<string>().Persisted();

        // ── Step 1: Read inputs (BuildWorkItem kept as the internal composer) ──
        var readInputs = new SetVariable
        {
            Id = "ReadInputs",
            Name = "Read Inputs",
            Variable = sessionId,
            Value = new(context =>
            {
                var sid = context.GetInput<Guid>("sessionId");
                if (sid == Guid.Empty) sid = Guid.NewGuid();

                issueId.Set(context, context.GetInput<string>("issueId") ?? string.Empty);
                issueTitle.Set(context, context.GetInput<string>("issueTitle") ?? string.Empty);
                repository.Set(context, context.GetInput<string>("repository") ?? string.Empty);
                issueNumber.Set(context, context.GetInput<int>("issueNumber"));
                workItemJson.Set(context, context.GetInput<string>("workItemJson") ?? string.Empty);
                tenantId.Set(context, context.GetInput<string>("tenantId") ?? string.Empty);
                acceptanceRulesJson.Set(context, context.GetInput<string>("acceptanceRulesJson") ?? string.Empty);
                return sid;
            })
        };
        readInputs.SetDisplayText("Read Inputs");

        // ── Step 2: Compute 39-10 re-entry position (D7) ───────────────
        var computeReEntry = new ComputeReEntryPositionActivity
        {
            Id = "ComputeReEntryPosition",
            Name = "Compute Re-Entry Position",
            IssueId = new(ctx => issueId.Get(ctx)),
            DocumentType = new(DecompositionDocumentType),
            TenantId = new(ctx => tenantId.Get(ctx)),
            CorrelationId = new(ctx => issueId.Get(ctx)),
            PositionJson = new(reEntryPositionJson),
            ExistingDocumentJson = new(reEntryDocJson),
        };
        computeReEntry.SetDisplayText("Compute Re-Entry Position");

        var readPositionStage = new SetVariable
        {
            Id = "ReadPositionStage",
            Name = "Read Position Stage",
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

        // ── Step 3: FreshRun gate — pre-produce region only on a fresh run (D7) ──
        var freshRun = new FlowDecision(ctx => positionStage.Get(ctx) == "produce")
        { Id = "FreshRun", Name = "Fresh Run?" };
        freshRun.SetDisplayText("Fresh Run?");

        var emitStarted = new EmitDecompositionEventActivity
        {
            Id = "EmitDecompositionStarted",
            Name = "Emit Decomposition Started",
            EventType = new(DecompositionEvents.Started),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Detail = new("Issue decomposition started"),
        };
        emitStarted.SetDisplayText("Emit Decomposition Started");

        var gatherContext = new DispatchWorkflow
        {
            Id = "GatherContext",
            Name = "Gather Context",
            WorkflowDefinitionId = new("context-gathering"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"]   = repository.Get(ctx) ?? "",
                ["issueNumber"]  = issueNumber.Get(ctx),
                ["workItemJson"] = BuildWorkItem(issueTitle.Get(ctx), workItemJson.Get(ctx), issueId.Get(ctx)),
                ["tenantId"]     = tenantId.Get(ctx) ?? "",
            }),
            WaitForCompletion = new(true),
            Result = new(contextGatherResult),
        };
        gatherContext.SetDisplayText("Gather Context");

        var storeContextResult = new SetVariable
        {
            Id = "StoreContextResult",
            Name = "Store Context Result",
            Variable = decompositionContext,
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

        var emitContextGathered = new EmitDecompositionEventActivity
        {
            Id = "EmitContextGathered",
            Name = "Emit Context Gathered",
            EventType = new(DecompositionEvents.ContextGathered),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Detail = new("Codebase / prior-art context gathered via context-gathering"),
        };
        emitContextGathered.SetDisplayText("Emit Context Gathered");

        // ── Step 3b (39-25 leg 1): fetch the latest ACCEPTED ambiguity-assessment ──
        // Fail-closed: no accepted assessment for this issue ⇒ Found=false ⇒ the
        // ambiguityScore dispatch key below is OMITTED (never a fabricated 0.0).
        var fetchAmbiguityAssessment = new FetchLatestAcceptedDocumentActivity
        {
            Id = "FetchAmbiguityAssessment",
            Name = "Fetch Accepted Ambiguity Assessment",
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
            Id = "DispatchLifecycle",
            Name = "Dispatch Document Lifecycle",
            WorkflowDefinitionId = new("document-lifecycle"),
            Input = new(ctx =>
            {
                var input = new Dictionary<string, object>
                {
                    // The (senior_developer, decompose-issue) producer cell is bound as the
                    // produce step; the drift enumeration reads producerRole/producerAction here.
                    ["documentType"]          = DecompositionDocumentType,
                    ["producerRole"]          = AgentRole.SeniorDeveloper.ToWire(),
                    ["producerAction"]        = AgentAction.DecomposeIssue.ToWire(),
                    ["producerVariablesJson"] = JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["workItemJson"] = BuildWorkItem(issueTitle.Get(ctx), workItemJson.Get(ctx), issueId.Get(ctx)),
                        ["findings"]     = decompositionContext.Get(ctx) ?? "",
                        ["conventions"]  = "",
                    }),
                    ["issueId"]             = issueId.Get(ctx) ?? "",
                    ["correlationId"]       = issueId.Get(ctx) ?? "",
                    ["tenantId"]            = tenantId.Get(ctx) ?? "",
                    ["acceptanceRulesJson"] = acceptanceRulesJson.Get(ctx) ?? "",
                };
                // 39-25 — thread the accepted assessment's score into the lifecycle's
                // existing ambiguityScore input; ABSENT when none (null stays null).
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
            Id = "ReadLifecycleExit",
            Name = "Read Lifecycle Exit",
            Variable = decompositionJson,
            Value = new(ctx =>
            {
                var exit = DecompositionBindingHelper.ReadLifecycleResult(lifecycleResult.Get(ctx));
                var accepted = DecompositionBindingHelper.IsAccepted(exit);

                lifecycleAccepted.Set(ctx, accepted);
                exitStatus.Set(ctx, exit.Status);
                exitOutcome.Set(ctx, exit.Outcome ?? "");
                exitDocId.Set(ctx, exit.DocumentId ?? "");
                subtaskCount.Set(ctx, DecompositionBindingHelper.CountSubtasks(exit.DocumentJson));
                failureDetail.Set(ctx, DecompositionBindingHelper.BuildFailureDetail(exit));
                // status output: "completed" on acceptance (compat, D1); else the typed exit
                // status ("rejected"/"escalated") — strictly additive over the old failure path.
                outputStatus.Set(ctx, accepted ? "completed" : exit.Status);
                return exit.DocumentJson;
            })
        };
        readLifecycleExit.SetDisplayText("Read Lifecycle Exit");

        // ── Step 6: Accepted? (typed) ──────────────────────────────────
        var lifecycleAcceptedGate = new FlowDecision(ctx => lifecycleAccepted.Get(ctx))
        { Id = "LifecycleAccepted", Name = "Lifecycle Accepted?" };
        lifecycleAcceptedGate.SetDisplayText("Lifecycle Accepted?");

        // Suppress a duplicate DECOMPOSITION.COMPLETED when a re-entry short-circuited an
        // already-accepted document (position == Complete, D3).
        var wasCompleteReEntry = new FlowDecision(ctx => positionStage.Get(ctx) == "complete")
        { Id = "WasCompleteReEntry", Name = "Was Complete Re-Entry?" };
        wasCompleteReEntry.SetDisplayText("Was Complete Re-Entry?");

        var emitDecompositionCompleted = new EmitDecompositionEventActivity
        {
            Id = "EmitDecompositionCompleted",
            Name = "Emit Decomposition Completed",
            EventType = new(DecompositionEvents.Completed),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            SubtaskCount = new(ctx => subtaskCount.Get(ctx)),
            Detail = new("Issue decomposed into ordered, implementable sub-tasks"),
        };
        emitDecompositionCompleted.SetDisplayText("Emit Decomposition Completed");

        var emitDecompositionFailed = new EmitDecompositionEventActivity
        {
            Id = "EmitDecompositionFailed",
            Name = "Emit Decomposition Failed",
            EventType = new(DecompositionEvents.Failed),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Detail = new(ctx => failureDetail.Get(ctx)),
        };
        emitDecompositionFailed.SetDisplayText("Emit Decomposition Failed");

        // ── Step 7: Expose output — the single terminal region (D2, AC3) ──
        var exposeOutput = new Sequence
        {
            Id = "ExposeOutput",
            Name = "Expose Output",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutputSessionId", Name = "Output Session Id", OutputName = new("sessionId"), OutputValue = new(ctx => (object)sessionId.Get(ctx).ToString()) }, "Output Session Id"),
                WithLabel(new SetOutput { Id = "OutputStatus", Name = "Output Status", OutputName = new("status"), OutputValue = new(ctx => (object)(outputStatus.Get(ctx) ?? "")) }, "Output Status"),
                WithLabel(new SetOutput { Id = "OutputDecomposition", Name = "Output Decomposition", OutputName = new("decomposition"), OutputValue = new(ctx => (object)(decompositionJson.Get(ctx) ?? "{}")) }, "Output Decomposition"),
                WithLabel(new SetOutput { Id = "OutputSubtaskCount", Name = "Output Subtask Count", OutputName = new("subtaskCount"), OutputValue = new(ctx => (object)subtaskCount.Get(ctx)) }, "Output Subtask Count"),
                WithLabel(new SetOutput { Id = "OutputContextIds", Name = "Output Context Ids", OutputName = new("contextIds"), OutputValue = new(ctx => (object)(contextIds.Get(ctx) ?? "[]")) }, "Output Context Ids"),
                WithLabel(new SetOutput { Id = "OutputOutcome", Name = "Output Outcome", OutputName = new("outcome"), OutputValue = new(ctx => (object)(exitOutcome.Get(ctx) ?? "")) }, "Output Outcome"),
                WithLabel(new SetOutput { Id = "OutputDocumentId", Name = "Output Document Id", OutputName = new("documentId"), OutputValue = new(ctx => (object)(exitDocId.Get(ctx) ?? "")) }, "Output Document Id"),
            }
        };
        exposeOutput.SetDisplayText("Expose Output");

        // ── Build the flowchart ────────────────────────────────────────
        builder.Root = new Flowchart
        {
            Id = "IssueDecompositionFlowchart",
            Name = "Issue Decomposition Flowchart",
            Start = readInputs,
            Activities =
            {
                readInputs,
                computeReEntry,
                readPositionStage,
                freshRun,
                emitStarted,
                gatherContext,
                storeContextResult,
                emitContextGathered,
                fetchAmbiguityAssessment,
                dispatchLifecycle,
                readLifecycleExit,
                lifecycleAcceptedGate,
                wasCompleteReEntry,
                emitDecompositionCompleted,
                emitDecompositionFailed,
                exposeOutput,
            },
            Connections =
            {
                new(readInputs, computeReEntry),
                new(computeReEntry, readPositionStage),
                new(readPositionStage, freshRun),

                // Fresh run → pre-produce region (STARTED + context scan + CONTEXT_GATHERED).
                new(new FlowEndpoint(freshRun, "True"),  new FlowEndpoint(emitStarted)),
                new(emitStarted, gatherContext),
                new(gatherContext, storeContextResult),
                new(storeContextResult, emitContextGathered),
                // 39-25 — the ambiguity-assessment fetch is the SINGLE predecessor of the
                // dispatch, so it runs on every path that actually dispatches (fresh + re-entry).
                new(emitContextGathered, fetchAmbiguityAssessment),
                // Re-entry → fetch → dispatch (a re-entry is not a new decomposition, D7).
                new(new FlowEndpoint(freshRun, "False"), new FlowEndpoint(fetchAmbiguityAssessment)),
                new(fetchAmbiguityAssessment, dispatchLifecycle),

                new(dispatchLifecycle, readLifecycleExit),
                new(readLifecycleExit, lifecycleAcceptedGate),

                // Accepted → suppress duplicate COMPLETED on a complete re-entry (D3).
                new(new FlowEndpoint(lifecycleAcceptedGate, "True"),  new FlowEndpoint(wasCompleteReEntry)),
                new(new FlowEndpoint(wasCompleteReEntry, "False"), new FlowEndpoint(emitDecompositionCompleted)),
                new(emitDecompositionCompleted, exposeOutput),
                new(new FlowEndpoint(wasCompleteReEntry, "True"),  new FlowEndpoint(exposeOutput)),

                // Not accepted → LOUD DECOMPOSITION.FAILED naming the typed outcome (D3, AC4).
                new(new FlowEndpoint(lifecycleAcceptedGate, "False"), new FlowEndpoint(emitDecompositionFailed)),
                new(emitDecompositionFailed, exposeOutput),
            }
        };
    }

    /// <summary>
    /// Compose the work-item JSON handed to the context-gathering scan and the decomposition
    /// producer. Prefers an explicit <paramref name="workItemJson"/>; otherwise wraps the
    /// free-text <paramref name="issueTitle"/> (plus the issue id) into a minimal JSON object
    /// so the downstream template has a stable shape. Pure; exposed for unit testing.
    /// </summary>
    internal static string BuildWorkItem(string? issueTitle, string? workItemJson, string? issueId)
    {
        if (!string.IsNullOrWhiteSpace(workItemJson))
            return workItemJson!;

        return JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["type"] = "issue",
            ["issueId"] = issueId ?? "",
            ["title"] = issueTitle ?? "",
        });
    }
}
