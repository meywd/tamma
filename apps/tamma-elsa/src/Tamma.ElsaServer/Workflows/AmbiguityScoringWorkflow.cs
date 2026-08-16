using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Runtime.Activities;
using System.Text.Json;
using Tamma.Activities.Ambiguity;
using Tamma.Activities.Documents;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents.Resume;
using Tamma.ElsaServer.Workflows.Helpers;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Story 39-13 — Ambiguity Scoring, re-implemented as a THIN BINDING over
/// <see cref="DocumentLifecycleWorkflow"/>, producing a typed
/// <see cref="Tamma.Core.Documents.Types.AmbiguityAssessment"/> document. The public surface
/// is byte-stable (D1): same <c>DefinitionId = "ambiguity-scoring"</c>, same outputs
/// (<c>sessionId</c>/<c>status</c>/<c>score</c>/<c>ambiguityCount</c>/<c>confidence</c>/
/// <c>threshold</c>/<c>decision</c>/<c>assessment</c>) plus additive <c>outcome</c>/
/// <c>documentId</c>.
///
/// <para><b>The threshold branch is RETIRED (D7).</b> "Ambiguity above threshold" is no
/// longer an inline <c>ComputeDecision</c>/<c>ShouldClarify</c> branch; it is the typed
/// <c>ambiguity-above-threshold</c> lifecycle outcome raised by the 39-5/39-6 policy
/// machinery (threshold = acceptance-rules config, NOT a workflow constant), which the
/// orchestrator routes to the Clarification lifecycle. This binding contains NO dispatch of
/// <c>clarifying-questions</c> (AC4's no-edge pin) and no threshold constant. The legacy
/// <c>threshold</c>/<c>decision</c> outputs are compat-only projections of the typed exit.</para>
/// </summary>
[ResumeBehavior(ResumeMode.LatestStateReEntry)]
public class AmbiguityScoringWorkflow : WorkflowBase
{
    private const string AmbiguityAssessmentDocumentType = "ambiguity-assessment";

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "AmbiguityScoring";
        builder.DefinitionId = "ambiguity-scoring";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Score how ambiguous/underspecified a requirement is via the generic document lifecycle; above-threshold routes to clarification as a typed outcome";

        // ── Inputs (legacy `threshold` input retired — the threshold is acceptance-rules config, 39-5) ──
        var sessionId        = builder.WithVariable<Guid>().Persisted();
        var issueId          = builder.WithVariable<string>().Persisted();
        var requirement      = builder.WithVariable<string>().Persisted();
        var ambiguityContext = builder.WithVariable<string>().Persisted();
        var tenantId         = builder.WithVariable<string>("TenantId", "").Persisted();
        var acceptanceRulesJson = builder.WithVariable<string>("AcceptanceRulesJson", "").Persisted();

        // ── 39-10 re-entry position ────────────────────────────────────
        var reEntryPositionJson = builder.WithVariable<string>().Persisted();
        var reEntryDocJson  = builder.WithVariable<string>().Persisted();
        var positionStage   = builder.WithVariable<string>("PositionStage", "produce").Persisted();

        // ── Dispatched-workflow result + typed exit ────────────────────
        var lifecycleResult = builder.WithVariable<IDictionary<string, object>?>().Persisted();
        var lifecycleAccepted = builder.WithVariable<bool>().Persisted();
        var isAmbiguity     = builder.WithVariable<bool>().Persisted();
        var hasAssessment   = builder.WithVariable<bool>().Persisted();
        var exitStatus      = builder.WithVariable<string>("ExitStatus", "").Persisted();
        var exitOutcome     = builder.WithVariable<string>("ExitOutcome", "").Persisted();
        var exitDocId       = builder.WithVariable<string>("ExitDocId", "").Persisted();
        var assessmentJson  = builder.WithVariable<string>("AssessmentJson", "{}").Persisted();
        var score           = builder.WithVariable<double>().Persisted();
        var ambiguityCount  = builder.WithVariable<int>().Persisted();
        var confidence      = builder.WithVariable<double>().Persisted();
        var threshold       = builder.WithVariable<double>().Persisted();
        var decision        = builder.WithVariable<string>("Decision", "").Persisted();
        var failureDetail   = builder.WithVariable<string>("FailureDetail", "").Persisted();
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
                ambiguityContext.Set(context, context.GetInput<string>("context") ?? string.Empty);
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
            DocumentType = new(AmbiguityAssessmentDocumentType),
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

        // ── Step 3: FreshRun gate — STARTED only on a fresh run ────────
        var freshRun = new FlowDecision(ctx => positionStage.Get(ctx) == "produce")
        { Id = "FreshRun", Name = "Fresh Run?" };
        freshRun.SetDisplayText("Fresh Run?");

        var emitStarted = new EmitAmbiguityEventActivity
        {
            Id = "EmitAmbiguityStarted", Name = "Emit Ambiguity Started",
            EventType = new(AmbiguityEvents.Started),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Detail = new("Ambiguity scoring started"),
        };
        emitStarted.SetDisplayText("Emit Ambiguity Started");

        // ── Step 4: Dispatch the generic document lifecycle ────────────
        var dispatchLifecycle = new DispatchWorkflow
        {
            Id = "DispatchLifecycle", Name = "Dispatch Document Lifecycle",
            WorkflowDefinitionId = new("document-lifecycle"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["documentType"]          = AmbiguityAssessmentDocumentType,
                ["producerRole"]          = AgentRole.ProductOwner.ToWire(),
                ["producerAction"]        = AgentAction.ScoreAmbiguity.ToWire(),
                ["producerVariablesJson"] = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["workItemJson"]    = requirement.Get(ctx) ?? "",
                    ["contextFindings"] = ambiguityContext.Get(ctx) ?? "",
                    ["conventions"]     = "",
                }),
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
            Variable = assessmentJson,
            Value = new(ctx =>
            {
                var exit = LifecycleBindingHelper.ReadLifecycleResult(lifecycleResult.Get(ctx));
                var accepted = LifecycleBindingHelper.IsAccepted(exit);
                var ambiguity = AssessmentBindingHelper.IsAmbiguityOutcome(exit);
                var (sc, count, conf) = AssessmentBindingHelper.ReadAssessment(exit.DocumentJson);

                lifecycleAccepted.Set(ctx, accepted);
                isAmbiguity.Set(ctx, ambiguity);
                hasAssessment.Set(ctx, accepted || ambiguity);
                exitStatus.Set(ctx, exit.Status);
                exitOutcome.Set(ctx, exit.Outcome ?? "");
                exitDocId.Set(ctx, exit.DocumentId ?? "");
                score.Set(ctx, sc);
                ambiguityCount.Set(ctx, count);
                confidence.Set(ctx, conf);
                threshold.Set(ctx, AssessmentBindingHelper.EffectiveAmbiguityThreshold(acceptanceRulesJson.Get(ctx)));
                decision.Set(ctx, ambiguity ? "clarify" : accepted ? "proceed" : "");
                failureDetail.Set(ctx, AssessmentBindingHelper.BuildFailureDetail(exit));
                outputStatus.Set(ctx, (accepted || ambiguity) ? "scored" : exit.Status);
                return exit.DocumentJson;
            })
        };
        readLifecycleExit.SetDisplayText("Read Lifecycle Exit");

        // ── Step 6: routing (typed values only) ────────────────────────
        var hasAssessmentGate = new FlowDecision(ctx => hasAssessment.Get(ctx))
        { Id = "HasAssessment", Name = "Has Assessment?" };
        hasAssessmentGate.SetDisplayText("Has Assessment?");

        var wasCompleteReEntry = new FlowDecision(ctx => positionStage.Get(ctx) == "complete")
        { Id = "WasCompleteReEntry", Name = "Was Complete Re-Entry?" };
        wasCompleteReEntry.SetDisplayText("Was Complete Re-Entry?");

        var emitScored = new EmitAmbiguityEventActivity
        {
            Id = "EmitAmbiguityScored", Name = "Emit Ambiguity Scored",
            EventType = new(AmbiguityEvents.Scored),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Score = new(ctx => (double?)score.Get(ctx)),
            AmbiguityCount = new(ctx => ambiguityCount.Get(ctx)),
            Confidence = new(ctx => confidence.Get(ctx)),
            Detail = new("Ambiguity score computed"),
        };
        emitScored.SetDisplayText("Emit Ambiguity Scored");

        var isAmbiguityGate = new FlowDecision(ctx => isAmbiguity.Get(ctx))
        { Id = "IsAmbiguity", Name = "Ambiguity Above Threshold?" };
        isAmbiguityGate.SetDisplayText("Ambiguity Above Threshold?");

        var emitClarificationTriggered = new EmitAmbiguityEventActivity
        {
            Id = "EmitClarificationTriggered", Name = "Emit Clarification Triggered",
            EventType = new(AmbiguityEvents.ClarificationTriggered),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Score = new(ctx => (double?)score.Get(ctx)),
            Threshold = new(ctx => threshold.Get(ctx)),
            Detail = new("Assessment exceeded the effective clarify threshold — the lifecycle exited ambiguity-above-threshold for orchestrator routing to clarification"),
        };
        emitClarificationTriggered.SetDisplayText("Emit Clarification Triggered");

        var emitBelowThreshold = new EmitAmbiguityEventActivity
        {
            Id = "EmitBelowThreshold", Name = "Emit Below Threshold",
            EventType = new(AmbiguityEvents.BelowThreshold),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Score = new(ctx => (double?)score.Get(ctx)),
            Threshold = new(ctx => threshold.Get(ctx)),
            Detail = new("Assessment accepted below the clarify threshold — proceeding without clarification"),
        };
        emitBelowThreshold.SetDisplayText("Emit Below Threshold");

        var emitFailed = new EmitAmbiguityEventActivity
        {
            Id = "EmitAmbiguityFailed", Name = "Emit Ambiguity Failed",
            EventType = new(AmbiguityEvents.Failed),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Detail = new(ctx => failureDetail.Get(ctx)),
        };
        emitFailed.SetDisplayText("Emit Ambiguity Failed");

        // ── Step 7: Expose output — the single terminal region ─────────
        var exposeOutput = new Sequence
        {
            Id = "ExposeOutput", Name = "Expose Output",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutputSessionId", Name = "Output Session Id", OutputName = new("sessionId"), OutputValue = new(ctx => (object)sessionId.Get(ctx).ToString()) }, "Output Session Id"),
                WithLabel(new SetOutput { Id = "OutputStatus", Name = "Output Status", OutputName = new("status"), OutputValue = new(ctx => (object)(outputStatus.Get(ctx) ?? "")) }, "Output Status"),
                WithLabel(new SetOutput { Id = "OutputScore", Name = "Output Score", OutputName = new("score"), OutputValue = new(ctx => (object)score.Get(ctx)) }, "Output Score"),
                WithLabel(new SetOutput { Id = "OutputAmbiguityCount", Name = "Output Ambiguity Count", OutputName = new("ambiguityCount"), OutputValue = new(ctx => (object)ambiguityCount.Get(ctx)) }, "Output Ambiguity Count"),
                WithLabel(new SetOutput { Id = "OutputConfidence", Name = "Output Confidence", OutputName = new("confidence"), OutputValue = new(ctx => (object)confidence.Get(ctx)) }, "Output Confidence"),
                WithLabel(new SetOutput { Id = "OutputThreshold", Name = "Output Threshold", OutputName = new("threshold"), OutputValue = new(ctx => (object)threshold.Get(ctx)) }, "Output Threshold"),
                WithLabel(new SetOutput { Id = "OutputDecision", Name = "Output Decision", OutputName = new("decision"), OutputValue = new(ctx => (object)(decision.Get(ctx) ?? "")) }, "Output Decision"),
                WithLabel(new SetOutput { Id = "OutputAssessment", Name = "Output Assessment", OutputName = new("assessment"), OutputValue = new(ctx => (object)(assessmentJson.Get(ctx) ?? "{}")) }, "Output Assessment"),
                WithLabel(new SetOutput { Id = "OutputOutcome", Name = "Output Outcome", OutputName = new("outcome"), OutputValue = new(ctx => (object)(exitOutcome.Get(ctx) ?? "")) }, "Output Outcome"),
                WithLabel(new SetOutput { Id = "OutputDocumentId", Name = "Output Document Id", OutputName = new("documentId"), OutputValue = new(ctx => (object)(exitDocId.Get(ctx) ?? "")) }, "Output Document Id"),
            }
        };
        exposeOutput.SetDisplayText("Expose Output");

        // ── Build the flowchart ────────────────────────────────────────
        builder.Root = new Flowchart
        {
            Id = "AmbiguityScoringFlowchart",
            Name = "Ambiguity Scoring Flowchart",
            Start = readInputs,
            Activities =
            {
                readInputs, computeReEntry, readPositionStage, freshRun, emitStarted,
                dispatchLifecycle, readLifecycleExit,
                hasAssessmentGate, wasCompleteReEntry, emitScored, isAmbiguityGate,
                emitClarificationTriggered, emitBelowThreshold, emitFailed,
                exposeOutput,
            },
            Connections =
            {
                new(readInputs, computeReEntry),
                new(computeReEntry, readPositionStage),
                new(readPositionStage, freshRun),

                new(new FlowEndpoint(freshRun, "True"),  new FlowEndpoint(emitStarted)),
                new(emitStarted, dispatchLifecycle),
                new(new FlowEndpoint(freshRun, "False"), new FlowEndpoint(dispatchLifecycle)),

                new(dispatchLifecycle, readLifecycleExit),
                new(readLifecycleExit, hasAssessmentGate),

                new(new FlowEndpoint(hasAssessmentGate, "True"),  new FlowEndpoint(wasCompleteReEntry)),
                new(new FlowEndpoint(wasCompleteReEntry, "True"),  new FlowEndpoint(exposeOutput)),
                new(new FlowEndpoint(wasCompleteReEntry, "False"), new FlowEndpoint(emitScored)),
                new(emitScored, isAmbiguityGate),
                new(new FlowEndpoint(isAmbiguityGate, "True"),  new FlowEndpoint(emitClarificationTriggered)),
                new(new FlowEndpoint(isAmbiguityGate, "False"), new FlowEndpoint(emitBelowThreshold)),
                new(emitClarificationTriggered, exposeOutput),
                new(emitBelowThreshold, exposeOutput),

                new(new FlowEndpoint(hasAssessmentGate, "False"), new FlowEndpoint(emitFailed)),
                new(emitFailed, exposeOutput),
            }
        };
    }
}
