using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using System.Text.Json;
using Tamma.Activities;
using Tamma.Activities.Research;
using Tamma.Activities.Research.Models;
using Tamma.Api.Services.Agents;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Story 3.4 — Research sub-workflow. Given an issue / topic (typically when ambiguity is
/// detected in a requirement) it investigates the codebase / prior art and uses the LLM
/// (via the MEDIATED <c>llm-call</c> path — the engine holds no LLM credential, TAMMA001)
/// to synthesize the gathered context into a ranked, confidence-scored research report,
/// then emits the results as <c>RESEARCH.*</c> DCB events.
///
/// Flow:
///   1. Read inputs (issue/topic + repository + tenantId; mint a session id if none)
///   2. Emit RESEARCH.STARTED
///   3. Gather codebase / prior-art context by REUSING DispatchWorkflow("context-gathering")
///      (Story 7-1F multi-role scan) — same reuse as <see cref="AssessmentWorkflow"/>
///   4. Emit RESEARCH.CONTEXT_GATHERED
///   5. Synthesize a ranked research report via DispatchWorkflow("llm-call")
///      role=product_owner / action=summarize-stakeholder
///   6. Parse the synthesis fail-closed (empty/unparseable → error terminal)
///   7a. On success: emit RESEARCH.COMPLETED, set outputs (report, confidence, findings)
///   7b. On failure: emit RESEARCH.FAILED (LOUD) and route to the ResearchError terminal
///
/// Reuses the <see cref="AssessmentWorkflow"/> skeleton (gather-context → llm-call → parse
/// → fail-closed gate + error terminal). The research is AUTONOMOUS — there is no human
/// gate / bookmark (the ambiguity that triggers research is resolved by the sibling
/// <see cref="ClarifyingQuestionsWorkflow"/>; research itself just investigates and reports).
///
/// Fail-closed: if the synthesis <c>llm-call</c> returns success=false, or the response
/// cannot be parsed into a non-empty ranked report, the workflow emits a LOUD
/// <c>RESEARCH.FAILED</c> event and routes to the ResearchError terminal — it NEVER
/// proceeds with a fabricated report. Prompt resolution is tenant→system→error (the
/// <c>llm-call</c> registry never falls back to an empty/plain prompt).
///
/// NOTE (taxonomy): Tamma's action taxonomy has no dedicated <c>research</c>/<c>investigate</c>
/// action (the legacy TS <c>researcher</c> role maps onto <c>product_owner</c> in
/// <c>RolePhaseMap.LegacyRoleAliases</c>). The synthesis therefore dispatches the closest
/// eligible pair, <c>product_owner</c> / <c>summarize-stakeholder</c>, so the workflow is
/// taxonomy-drift-clean and prompt resolution works. A future story that adds a dedicated
/// research action + a structured-findings prompt template (a cross-cutting taxonomy change)
/// only needs to swap the action constant below.
/// </summary>
public class ResearchWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Research";
        builder.DefinitionId = "research";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Investigate an issue/topic and synthesize a ranked, confidence-scored research report via the mediated LLM";

        // ── Workflow variables ──────────────────────────────────────────
        var sessionId       = builder.WithVariable<Guid>();
        var issueId         = builder.WithVariable<string>();
        var topic           = builder.WithVariable<string>();
        var repository      = builder.WithVariable<string>();
        var issueNumber     = builder.WithVariable<int>();
        var workItemJson    = builder.WithVariable<string>();
        var tenantId        = builder.WithVariable<string>("TenantId", "");

        var researchContext = builder.WithVariable<string>();
        var contextIds      = builder.WithVariable<string>("[]");
        var reportJson      = builder.WithVariable<string>();
        var findingCount    = builder.WithVariable<int>();
        var overallConfidence = builder.WithVariable<double>();

        // Dispatched-workflow result containers
        var contextGatherResult = builder.WithVariable<IDictionary<string, object>?>();
        var synthesizeLlm       = builder.WithVariable<IDictionary<string, object>?>();

        // Success flag (fail-closed guard)
        var researchLlmOk   = builder.WithVariable<bool>();

        // Captured parse output
        var researchReport  = builder.WithVariable<ResearchReport>();

        // Output variables (readable by a parent workflow)
        var outputStatus    = builder.WithVariable<string>();

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
                topic.Set(context, context.GetInput<string>("topic") ?? string.Empty);
                repository.Set(context, context.GetInput<string>("repository") ?? string.Empty);
                issueNumber.Set(context, context.GetInput<int>("issueNumber"));
                workItemJson.Set(context, context.GetInput<string>("workItemJson") ?? string.Empty);
                tenantId.Set(context, context.GetInput<string>("tenantId") ?? string.Empty);
                return sid;
            })
        };
        readInputs.SetDisplayText("Read Inputs");

        // ── Step 2: Emit RESEARCH.STARTED ──────────────────────────────
        var emitStarted = new EmitResearchEventActivity
        {
            Id = "EmitResearchStarted",
            Name = "Emit Research Started",
            EventType = new(ResearchEvents.Started),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Detail = new("Research investigation started"),
        };
        emitStarted.SetDisplayText("Emit Research Started");

        // ── Step 3: Gather context via ContextGathering workflow (7-1F) ─
        var gatherContext = new DispatchWorkflow
        {
            Id = "GatherContext",
            Name = "Gather Context",
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
            Id = "StoreContextResult",
            Name = "Store Context Result",
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

        // ── Step 4: Emit RESEARCH.CONTEXT_GATHERED ─────────────────────
        var emitContextGathered = new EmitResearchEventActivity
        {
            Id = "EmitContextGathered",
            Name = "Emit Context Gathered",
            EventType = new(ResearchEvents.ContextGathered),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Detail = new("Codebase / prior-art context gathered via context-gathering"),
        };
        emitContextGathered.SetDisplayText("Emit Context Gathered");

        // ── Step 5: Synthesize a ranked research report via llm-call ───
        var synthesizeResearchLlm = new DispatchWorkflow
        {
            Id = "SynthesizeResearchLlm",
            Name = "Synthesize Research (LLM)",
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                // No dedicated research action exists; product_owner/summarize-stakeholder
                // is the closest eligible pair (see class remarks). tenant→system→error.
                ["role"]     = AgentRole.ProductOwner.ToWire(),
                ["action"]   = AgentAction.SummarizeStakeholder.ToWire(),
                ["tenantId"] = tenantId.Get(ctx),
                ["variables"] = new Dictionary<string, object>
                {
                    ["workItemJson"] = BuildWorkItem(topic.Get(ctx), workItemJson.Get(ctx), issueId.Get(ctx)),
                    ["findings"]     = researchContext.Get(ctx) ?? "",
                    ["audience"]     = "engineering-team",
                },
                ["enableTools"] = false,
            }),
            WaitForCompletion = new(true),
            Result = new(synthesizeLlm),
        };
        synthesizeResearchLlm.SetDisplayText("Synthesize Research (LLM)");

        // ── Step 6: Parse the synthesis (fail-closed) ──────────────────
        var parseResearch = new SetVariable
        {
            Id = "ParseResearch",
            Name = "Parse Research",
            Variable = reportJson,
            Value = new(ctx =>
            {
                var result = synthesizeLlm.Get(ctx);
                if (!ReadSuccessFlag(result))
                {
                    researchLlmOk.Set(ctx, false);
                    return "{}";
                }

                var text = result!.TryGetValue("llmResponse", out var r) ? r?.ToString() ?? "" : "";
                var report = ResearchParsing.ParseReport(text, topic.Get(ctx));
                if (report is null)
                {
                    // Fail-closed — no fabricated report.
                    researchLlmOk.Set(ctx, false);
                    return "{}";
                }

                researchLlmOk.Set(ctx, true);
                researchReport.Set(ctx, report);
                findingCount.Set(ctx, report.Findings.Count);
                overallConfidence.Set(ctx, (double)report.OverallConfidence);
                return JsonSerializer.Serialize(report);
            })
        };
        parseResearch.SetDisplayText("Parse Research");

        // Fail-closed gate: route to error terminal if synthesis failed / unparseable.
        var researchSuccessCheck = new FlowDecision(ctx => researchLlmOk.Get(ctx))
        { Id = "ResearchLlmOk", Name = "Research LLM OK?" };
        researchSuccessCheck.SetDisplayText("Research LLM OK?");

        // ── Step 7a: Success path ──────────────────────────────────────
        var emitResearchCompleted = new EmitResearchEventActivity
        {
            Id = "EmitResearchCompleted",
            Name = "Emit Research Completed",
            EventType = new(ResearchEvents.Completed),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            FindingCount = new(ctx => findingCount.Get(ctx)),
            Confidence = new(ctx => overallConfidence.Get(ctx)),
            Detail = new("Ranked, confidence-scored research report synthesized"),
        };
        emitResearchCompleted.SetDisplayText("Emit Research Completed");

        var setOutputResult = new SetVariable
        {
            Id = "SetOutputResult",
            Name = "Set Output Result",
            Variable = outputStatus,
            Value = new(_ => "completed")
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
                WithLabel(new SetOutput { Id = "OutputReport", Name = "Output Report", OutputName = new("report"), OutputValue = new(ctx => (object)(reportJson.Get(ctx) ?? "{}")) }, "Output Report"),
                WithLabel(new SetOutput { Id = "OutputFindingCount", Name = "Output Finding Count", OutputName = new("findingCount"), OutputValue = new(ctx => (object)findingCount.Get(ctx)) }, "Output Finding Count"),
                WithLabel(new SetOutput { Id = "OutputConfidence", Name = "Output Confidence", OutputName = new("confidence"), OutputValue = new(ctx => (object)overallConfidence.Get(ctx)) }, "Output Confidence"),
                WithLabel(new SetOutput { Id = "OutputContextIds", Name = "Output Context Ids", OutputName = new("contextIds"), OutputValue = new(ctx => (object)(contextIds.Get(ctx) ?? "[]")) }, "Output Context Ids"),
            }
        };
        exposeOutput.SetDisplayText("Expose Output");

        // ── Step 7b: Fail-closed error terminal (LOUD event + Finish) ──
        var emitResearchFailed = new EmitResearchEventActivity
        {
            Id = "EmitResearchFailed",
            Name = "Emit Research Failed",
            EventType = new(ResearchEvents.Failed),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Detail = new("llm-call for research synthesis failed or returned unparseable output"),
        };
        emitResearchFailed.SetDisplayText("Emit Research Failed");

        var researchError = new Finish
        {
            Id = "ResearchError",
            Name = "Research Error"
        };
        researchError.SetDisplayText("Research Error");

        // ── Build the flowchart ────────────────────────────────────────
        builder.Root = new Flowchart
        {
            Id = "ResearchFlowchart",
            Name = "Research Flowchart",
            Start = readInputs,
            Activities =
            {
                readInputs,
                emitStarted,
                gatherContext,
                storeContextResult,
                emitContextGathered,
                synthesizeResearchLlm,
                parseResearch,
                researchSuccessCheck,

                // Success path
                emitResearchCompleted,
                setOutputResult,
                exposeOutput,

                // Fail-closed error terminal
                emitResearchFailed,
                researchError,
            },
            Connections =
            {
                new(readInputs, emitStarted),
                new(emitStarted, gatherContext),
                new(gatherContext, storeContextResult),
                new(storeContextResult, emitContextGathered),
                new(emitContextGathered, synthesizeResearchLlm),
                new(synthesizeResearchLlm, parseResearch),
                new(parseResearch, researchSuccessCheck),

                // Success path
                new(new FlowEndpoint(researchSuccessCheck, "True"),  new FlowEndpoint(emitResearchCompleted)),
                new(emitResearchCompleted, setOutputResult),
                new(setOutputResult, exposeOutput),

                // Fail-closed error path
                new(new FlowEndpoint(researchSuccessCheck, "False"), new FlowEndpoint(emitResearchFailed)),
                new(emitResearchFailed, researchError),
            }
        };
    }

    /// <summary>
    /// Read the <c>success</c> flag from a dispatched workflow's Result dictionary.
    /// Returns <c>false</c> if the dictionary is null, the key is absent, or the value is
    /// falsy — fail-closed by design. Uses the tolerant <see cref="ResumeInput.AsBool"/>
    /// read (boxed bool / string / JsonElement).
    /// </summary>
    internal static bool ReadSuccessFlag(IDictionary<string, object>? result)
    {
        if (result == null) return false;
        if (!result.TryGetValue("success", out var s)) return false;
        return ResumeInput.AsBool(s);
    }

    /// <summary>
    /// Compose the work-item JSON handed to the context-gathering scan and the synthesis
    /// prompt. Prefers an explicit <paramref name="workItemJson"/>; otherwise wraps the
    /// free-text <paramref name="topic"/> (plus the issue id) into a minimal JSON object
    /// so the downstream template has a stable shape. Pure; exposed for unit testing.
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
