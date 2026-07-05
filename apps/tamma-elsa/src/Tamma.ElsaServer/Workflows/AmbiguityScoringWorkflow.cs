using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using System.Text.Json;
using Tamma.Activities;
using Tamma.Activities.Ambiguity;
using Tamma.Activities.Ambiguity.Models;
using Tamma.Api.Services.Agents;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Story 3.6 — Ambiguity Scoring sub-workflow. Given an issue / requirement it uses the LLM
/// (via the MEDIATED <c>llm-call</c> path — the engine holds no LLM credential, TAMMA001) to
/// score how ambiguous / underspecified the requirement is (a 0..1 score + a typed, itemised
/// breakdown with specific recommendations), then compares the score to a caller-supplied
/// threshold to DECIDE whether to trigger clarification before proceeding. Every transition is
/// emitted as an <c>AMBIGUITY.*</c> DCB event.
///
/// Flow:
///   1. Read inputs (issue/requirement + optional context + tenantId + threshold; mint a
///      session id if none)
///   2. Emit AMBIGUITY.STARTED
///   3. Score the requirement via DispatchWorkflow("llm-call")
///      role=product_owner / action=score-ambiguity
///   4. Parse the response fail-closed (empty/unparseable/out-of-range score → error terminal)
///   5a. On success: emit AMBIGUITY.SCORED, then apply the threshold policy:
///        - score ≥ threshold → emit AMBIGUITY.CLARIFICATION_TRIGGERED (decision="clarify")
///        - score &lt; threshold → emit AMBIGUITY.BELOW_THRESHOLD (decision="proceed")
///       and set outputs (score, breakdown, decision).
///   5b. On failure: emit AMBIGUITY.FAILED (LOUD) and route to the AmbiguityError terminal.
///
/// Reuses the <see cref="ResearchWorkflow"/> / <see cref="AssessmentWorkflow"/> skeleton
/// (llm-call → parse → fail-closed gate + error terminal). The scoring is AUTONOMOUS — there is
/// no human gate / bookmark; the clarification it can trigger is handled by the sibling
/// <see cref="ClarifyingQuestionsWorkflow"/> (Story 3.5), which a parent flow dispatches on the
/// <c>decision="clarify"</c> output.
///
/// Fail-closed: if the scoring <c>llm-call</c> returns success=false, or the response cannot be
/// parsed into a valid in-range score with a rationale, the workflow emits a LOUD
/// <c>AMBIGUITY.FAILED</c> event and routes to the AmbiguityError terminal — it NEVER proceeds
/// with a fabricated score. Prompt resolution is tenant→system→error (the <c>llm-call</c>
/// registry never falls back to an empty/plain prompt).
///
/// NOTE (taxonomy): the scoring dispatches the dedicated <c>(product_owner, score-ambiguity)</c>
/// pair (Story 3.6). The <c>score-ambiguity</c> action is a first-class member of the
/// <see cref="AgentAction"/> taxonomy and is eligible for <c>product_owner</c> in
/// <c>RolePhaseMap</c> (requirement clarity is a product_owner concern, consistent with
/// <c>clarify-requirements</c> and <c>research</c>). Its system-default prompt template
/// (<c>SystemPrompts.ScoreAmbiguityBody</c>) emits the structured JSON
/// <see cref="AmbiguityParsing"/> parses, so the happy path produces a real
/// <c>AMBIGUITY.SCORED</c> assessment rather than failing closed.
/// </summary>
public class AmbiguityScoringWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "AmbiguityScoring";
        builder.DefinitionId = "ambiguity-scoring";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Score how ambiguous/underspecified a requirement is via the mediated LLM and decide whether to trigger clarification";

        // ── Workflow variables ──────────────────────────────────────────
        var sessionId        = builder.WithVariable<Guid>();
        var issueId          = builder.WithVariable<string>();
        var requirement      = builder.WithVariable<string>();
        var ambiguityContext = builder.WithVariable<string>();
        var tenantId         = builder.WithVariable<string>("TenantId", "");
        var threshold        = builder.WithVariable<double>();

        var assessmentJson   = builder.WithVariable<string>();
        var score            = builder.WithVariable<double>();
        var ambiguityCount   = builder.WithVariable<int>();
        var confidence       = builder.WithVariable<double>();
        var decision         = builder.WithVariable<string>();

        // llm-call result container
        var scoreLlm         = builder.WithVariable<IDictionary<string, object>?>();

        // Success flag (fail-closed guard) + threshold decision
        var ambiguityLlmOk   = builder.WithVariable<bool>();
        var shouldClarify    = builder.WithVariable<bool>();

        // Captured parse output
        var assessment       = builder.WithVariable<AmbiguityAssessment>();

        // Output variable (readable by a parent workflow)
        var outputStatus     = builder.WithVariable<string>();

        // ── Step 1: Read inputs ────────────────────────────────────────
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
                requirement.Set(context, context.GetInput<string>("requirement") ?? string.Empty);
                ambiguityContext.Set(context, context.GetInput<string>("context") ?? string.Empty);
                tenantId.Set(context, context.GetInput<string>("tenantId") ?? string.Empty);

                // Resolve the effective clarify threshold (caller value clamped to [0,1];
                // ≤ 0 / unset → default). Kept in decimal by the pure policy, exposed as double
                // to the workflow variables.
                var requested = (decimal)context.GetInput<double>("threshold");
                threshold.Set(context, (double)AmbiguityThresholds.Resolve(requested));
                return sid;
            })
        };
        readInputs.SetDisplayText("Read Inputs");

        // ── Step 2: Emit AMBIGUITY.STARTED ─────────────────────────────
        var emitStarted = new EmitAmbiguityEventActivity
        {
            Id = "EmitAmbiguityStarted",
            Name = "Emit Ambiguity Started",
            EventType = new(AmbiguityEvents.Started),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Detail = new("Ambiguity scoring started"),
        };
        emitStarted.SetDisplayText("Emit Ambiguity Started");

        // ── Step 3: Score the requirement via llm-call ─────────────────
        var scoreAmbiguityLlm = new DispatchWorkflow
        {
            Id = "ScoreAmbiguityLlm",
            Name = "Score Ambiguity (LLM)",
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                // Dedicated scoring action (Story 3.6): (product_owner, score-ambiguity) resolves
                // the structured-score prompt template that yields the JSON AmbiguityParsing
                // recovers. Prompt resolution is tenant→system→error.
                ["role"]     = AgentRole.ProductOwner.ToWire(),
                ["action"]   = AgentAction.ScoreAmbiguity.ToWire(),
                ["tenantId"] = tenantId.Get(ctx),
                ["variables"] = new Dictionary<string, object>
                {
                    ["workItemJson"]    = requirement.Get(ctx) ?? "",
                    ["contextFindings"] = ambiguityContext.Get(ctx) ?? "",
                    ["conventions"]     = "",
                },
                ["enableTools"] = false,
            }),
            WaitForCompletion = new(true),
            Result = new(scoreLlm),
        };
        scoreAmbiguityLlm.SetDisplayText("Score Ambiguity (LLM)");

        // ── Step 4: Parse the response (fail-closed) ───────────────────
        var parseAmbiguity = new SetVariable
        {
            Id = "ParseAmbiguity",
            Name = "Parse Ambiguity",
            Variable = assessmentJson,
            Value = new(ctx =>
            {
                var result = scoreLlm.Get(ctx);
                if (!ReadSuccessFlag(result))
                {
                    ambiguityLlmOk.Set(ctx, false);
                    return "{}";
                }

                var text = result!.TryGetValue("llmResponse", out var r) ? r?.ToString() ?? "" : "";
                var parsed = AmbiguityParsing.ParseAssessment(text);
                if (parsed is null)
                {
                    // Fail-closed — no fabricated score.
                    ambiguityLlmOk.Set(ctx, false);
                    return "{}";
                }

                ambiguityLlmOk.Set(ctx, true);
                assessment.Set(ctx, parsed);
                score.Set(ctx, (double)parsed.Score);
                ambiguityCount.Set(ctx, parsed.Ambiguities.Count);
                confidence.Set(ctx, (double)parsed.Confidence);
                return JsonSerializer.Serialize(parsed);
            })
        };
        parseAmbiguity.SetDisplayText("Parse Ambiguity");

        // Fail-closed gate: route to error terminal if scoring failed / unparseable.
        var ambiguitySuccessCheck = new FlowDecision(ctx => ambiguityLlmOk.Get(ctx))
        { Id = "AmbiguityLlmOk", Name = "Ambiguity LLM OK?" };
        ambiguitySuccessCheck.SetDisplayText("Ambiguity LLM OK?");

        // ── Step 5a: Success path — emit SCORED, then apply threshold ──
        var emitScored = new EmitAmbiguityEventActivity
        {
            Id = "EmitAmbiguityScored",
            Name = "Emit Ambiguity Scored",
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

        var computeDecision = new SetVariable
        {
            Id = "ComputeDecision",
            Name = "Compute Decision",
            Variable = shouldClarify,
            Value = new(ctx =>
            {
                var clarify = AmbiguityThresholds.ShouldClarify(
                    (decimal)score.Get(ctx), (decimal)threshold.Get(ctx));
                decision.Set(ctx, clarify ? "clarify" : "proceed");
                return clarify;
            })
        };
        computeDecision.SetDisplayText("Compute Decision");

        var shouldClarifyCheck = new FlowDecision(ctx => shouldClarify.Get(ctx))
        { Id = "ShouldClarify", Name = "Should Clarify?" };
        shouldClarifyCheck.SetDisplayText("Should Clarify?");

        // ── Step 5a-i: above threshold → trigger clarification ─────────
        var emitClarificationTriggered = new EmitAmbiguityEventActivity
        {
            Id = "EmitClarificationTriggered",
            Name = "Emit Clarification Triggered",
            EventType = new(AmbiguityEvents.ClarificationTriggered),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Score = new(ctx => (double?)score.Get(ctx)),
            Threshold = new(ctx => threshold.Get(ctx)),
            Detail = new("Score met/exceeded the clarify threshold — routing to clarifying questions"),
        };
        emitClarificationTriggered.SetDisplayText("Emit Clarification Triggered");

        // ── Step 5a-ii: below threshold → proceed as-is ────────────────
        var emitBelowThreshold = new EmitAmbiguityEventActivity
        {
            Id = "EmitBelowThreshold",
            Name = "Emit Below Threshold",
            EventType = new(AmbiguityEvents.BelowThreshold),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Score = new(ctx => (double?)score.Get(ctx)),
            Threshold = new(ctx => threshold.Get(ctx)),
            Detail = new("Score below the clarify threshold — proceeding without clarification"),
        };
        emitBelowThreshold.SetDisplayText("Emit Below Threshold");

        var setOutputResult = new SetVariable
        {
            Id = "SetOutputResult",
            Name = "Set Output Result",
            Variable = outputStatus,
            Value = new(_ => "scored")
        };
        setOutputResult.SetDisplayText("Set Output Result");

        var exposeOutput = new Sequence
        {
            Id = "ExposeOutput",
            Name = "Expose Output",
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
            }
        };
        exposeOutput.SetDisplayText("Expose Output");

        // ── Step 5b: Fail-closed error terminal (LOUD event + Finish) ──
        var emitAmbiguityFailed = new EmitAmbiguityEventActivity
        {
            Id = "EmitAmbiguityFailed",
            Name = "Emit Ambiguity Failed",
            EventType = new(AmbiguityEvents.Failed),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Detail = new("llm-call for ambiguity scoring failed or returned unparseable/out-of-range output"),
        };
        emitAmbiguityFailed.SetDisplayText("Emit Ambiguity Failed");

        var ambiguityError = new Finish
        {
            Id = "AmbiguityError",
            Name = "Ambiguity Error"
        };
        ambiguityError.SetDisplayText("Ambiguity Error");

        // ── Build the flowchart ────────────────────────────────────────
        builder.Root = new Flowchart
        {
            Id = "AmbiguityScoringFlowchart",
            Name = "Ambiguity Scoring Flowchart",
            Start = readInputs,
            Activities =
            {
                readInputs,
                emitStarted,
                scoreAmbiguityLlm,
                parseAmbiguity,
                ambiguitySuccessCheck,

                // Success path
                emitScored,
                computeDecision,
                shouldClarifyCheck,
                emitClarificationTriggered,
                emitBelowThreshold,
                setOutputResult,
                exposeOutput,

                // Fail-closed error terminal
                emitAmbiguityFailed,
                ambiguityError,
            },
            Connections =
            {
                new(readInputs, emitStarted),
                new(emitStarted, scoreAmbiguityLlm),
                new(scoreAmbiguityLlm, parseAmbiguity),
                new(parseAmbiguity, ambiguitySuccessCheck),

                // Success path
                new(new FlowEndpoint(ambiguitySuccessCheck, "True"),  new FlowEndpoint(emitScored)),
                new(emitScored, computeDecision),
                new(computeDecision, shouldClarifyCheck),
                new(new FlowEndpoint(shouldClarifyCheck, "True"),  new FlowEndpoint(emitClarificationTriggered)),
                new(new FlowEndpoint(shouldClarifyCheck, "False"), new FlowEndpoint(emitBelowThreshold)),
                new(emitClarificationTriggered, setOutputResult),
                new(emitBelowThreshold, setOutputResult),
                new(setOutputResult, exposeOutput),

                // Fail-closed error path
                new(new FlowEndpoint(ambiguitySuccessCheck, "False"), new FlowEndpoint(emitAmbiguityFailed)),
                new(emitAmbiguityFailed, ambiguityError),
            }
        };
    }

    /// <summary>
    /// Read the <c>success</c> flag from a dispatched workflow's Result dictionary. Returns
    /// <c>false</c> if the dictionary is null, the key is absent, or the value is falsy —
    /// fail-closed by design. Uses the tolerant <see cref="ResumeInput.AsBool"/> read (boxed
    /// bool / string / JsonElement).
    /// </summary>
    internal static bool ReadSuccessFlag(IDictionary<string, object>? result)
    {
        if (result == null) return false;
        if (!result.TryGetValue("success", out var s)) return false;
        return ResumeInput.AsBool(s);
    }
}
