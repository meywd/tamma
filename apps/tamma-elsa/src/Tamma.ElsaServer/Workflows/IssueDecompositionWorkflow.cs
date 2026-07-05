using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using System.Text.Json;
using Tamma.Activities;
using Tamma.Activities.Decomposition;
using Tamma.Activities.Decomposition.Models;
using Tamma.Api.Services.Agents;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Story 2.14 — Issue Decomposition sub-workflow. Given a complex issue / requirement it
/// investigates the codebase / prior art and uses the LLM (via the MEDIATED <c>llm-call</c> path —
/// the engine holds no LLM credential, TAMMA001) to break the issue into an ORDERED set of smaller,
/// implementable sub-tasks — each with a rationale, a definition of done, a rough sizing, a
/// complexity, and its declared prerequisite dependencies — then emits the results as
/// <c>DECOMPOSITION.*</c> DCB events.
///
/// Flow:
///   1. Read inputs (issue/requirement + repository + issueNumber + workItemJson + tenantId;
///      mint a session id if none)
///   2. Emit DECOMPOSITION.STARTED
///   3. Gather codebase / prior-art context by REUSING DispatchWorkflow("context-gathering")
///      (Story 7-1F multi-role scan) — same reuse as <see cref="ResearchWorkflow"/> /
///      <see cref="AssessmentWorkflow"/>; the scope/dependency signal it surfaces informs the
///      complexity assessment (Story 2.14 AC1)
///   4. Emit DECOMPOSITION.CONTEXT_GATHERED
///   5. Decompose the issue via DispatchWorkflow("llm-call")
///      role=senior_developer / action=decompose-issue
///   6. Parse the decomposition fail-closed (empty/unparseable/no-subtasks → error terminal)
///   7a. On success: emit DECOMPOSITION.COMPLETED (with the sub-task count), set outputs
///       (decomposition JSON, sub-task count)
///   7b. On failure: emit DECOMPOSITION.FAILED (LOUD) and route to the DecompositionError terminal
///
/// Reuses the <see cref="ResearchWorkflow"/> / <see cref="AmbiguityScoringWorkflow"/> skeleton
/// (gather-context → llm-call → parse → fail-closed gate + error terminal). Decomposition is
/// AUTONOMOUS — there is no in-workflow human gate / bookmark. The Story 2.14 AC7 "human approval
/// before executing decomposed tasks" is a downstream orchestration concern (a parent flow presents
/// the emitted sub-task set for approval before dispatching implementation); this workflow's job is
/// to PRODUCE the auditable, structured breakdown, not to execute it.
///
/// Fail-closed: if the decomposition <c>llm-call</c> returns success=false, or the response cannot
/// be parsed into a non-empty, valid sub-task set with a rationale, the workflow emits a LOUD
/// <c>DECOMPOSITION.FAILED</c> event and routes to the DecompositionError terminal — it NEVER
/// proceeds with a fabricated breakdown. Prompt resolution is tenant→system→error (the
/// <c>llm-call</c> registry never falls back to an empty/plain prompt).
///
/// NOTE (taxonomy): the decomposition dispatches the dedicated <c>(senior_developer,
/// decompose-issue)</c> pair (Story 2.14). The <c>decompose-issue</c> action is a first-class
/// member of the <see cref="AgentAction"/> taxonomy and is eligible for <c>senior_developer</c> in
/// <c>RolePhaseMap</c> — decomposition is the tech-lead's charter (the senior_developer identity
/// prompt is literally "decompose complex tasks", alongside <c>create-tasks</c> and
/// <c>plan-implementation</c>). Its system-default prompt template
/// (<c>SystemPrompts.DecomposeIssueBody</c>) emits the structured sub-task JSON
/// <see cref="DecompositionParsing"/> parses, so the happy path produces a real
/// <c>DECOMPOSITION.COMPLETED</c> breakdown rather than failing closed. The sub-task output shape
/// (<see cref="IssueDecomposition"/> — ordered sub-tasks with ids + <c>dependsOn</c> edges) is the
/// input contract for Story 2.15 (#138 dependency mapping) and Story 2.16 (#139 sequencing).
/// </summary>
public class IssueDecompositionWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "IssueDecomposition";
        builder.DefinitionId = "issue-decomposition";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Decompose a complex issue into an ordered set of implementable sub-tasks (with rationale, sizing and dependencies) via the mediated LLM";

        // ── Workflow variables ──────────────────────────────────────────
        var sessionId       = builder.WithVariable<Guid>();
        var issueId         = builder.WithVariable<string>();
        var issueTitle      = builder.WithVariable<string>();
        var repository      = builder.WithVariable<string>();
        var issueNumber     = builder.WithVariable<int>();
        var workItemJson    = builder.WithVariable<string>();
        var tenantId        = builder.WithVariable<string>("TenantId", "");

        var decompositionContext = builder.WithVariable<string>();
        var contextIds      = builder.WithVariable<string>("[]");
        var decompositionJson = builder.WithVariable<string>();
        var subtaskCount    = builder.WithVariable<int>();

        // Dispatched-workflow result containers
        var contextGatherResult = builder.WithVariable<IDictionary<string, object>?>();
        var decomposeLlm        = builder.WithVariable<IDictionary<string, object>?>();

        // Success flag (fail-closed guard)
        var decompositionLlmOk = builder.WithVariable<bool>();

        // Captured parse output
        var decomposition   = builder.WithVariable<IssueDecomposition>();

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
                issueTitle.Set(context, context.GetInput<string>("issueTitle") ?? string.Empty);
                repository.Set(context, context.GetInput<string>("repository") ?? string.Empty);
                issueNumber.Set(context, context.GetInput<int>("issueNumber"));
                workItemJson.Set(context, context.GetInput<string>("workItemJson") ?? string.Empty);
                tenantId.Set(context, context.GetInput<string>("tenantId") ?? string.Empty);
                return sid;
            })
        };
        readInputs.SetDisplayText("Read Inputs");

        // ── Step 2: Emit DECOMPOSITION.STARTED ─────────────────────────
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

        // ── Step 4: Emit DECOMPOSITION.CONTEXT_GATHERED ────────────────
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

        // ── Step 5: Decompose the issue via llm-call ───────────────────
        var decomposeIssueLlm = new DispatchWorkflow
        {
            Id = "DecomposeIssueLlm",
            Name = "Decompose Issue (LLM)",
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                // Dedicated decomposition action (Story 2.14): (senior_developer, decompose-issue)
                // resolves the structured-subtask prompt template that yields the JSON
                // DecompositionParsing recovers. Prompt resolution is tenant→system→error.
                ["role"]     = AgentRole.SeniorDeveloper.ToWire(),
                ["action"]   = AgentAction.DecomposeIssue.ToWire(),
                ["tenantId"] = tenantId.Get(ctx),
                ["variables"] = new Dictionary<string, object>
                {
                    ["workItemJson"] = BuildWorkItem(issueTitle.Get(ctx), workItemJson.Get(ctx), issueId.Get(ctx)),
                    ["findings"]     = decompositionContext.Get(ctx) ?? "",
                    ["conventions"]  = "",
                },
                ["enableTools"] = false,
            }),
            WaitForCompletion = new(true),
            Result = new(decomposeLlm),
        };
        decomposeIssueLlm.SetDisplayText("Decompose Issue (LLM)");

        // ── Step 6: Parse the decomposition (fail-closed) ──────────────
        var parseDecomposition = new SetVariable
        {
            Id = "ParseDecomposition",
            Name = "Parse Decomposition",
            Variable = decompositionJson,
            Value = new(ctx =>
            {
                var result = decomposeLlm.Get(ctx);
                if (!ReadSuccessFlag(result))
                {
                    decompositionLlmOk.Set(ctx, false);
                    return "{}";
                }

                var text = result!.TryGetValue("llmResponse", out var r) ? r?.ToString() ?? "" : "";
                var parsed = DecompositionParsing.ParseDecomposition(text);
                if (parsed is null)
                {
                    // Fail-closed — no fabricated breakdown.
                    decompositionLlmOk.Set(ctx, false);
                    return "{}";
                }

                decompositionLlmOk.Set(ctx, true);
                decomposition.Set(ctx, parsed);
                subtaskCount.Set(ctx, parsed.Subtasks.Count);
                return JsonSerializer.Serialize(parsed);
            })
        };
        parseDecomposition.SetDisplayText("Parse Decomposition");

        // Fail-closed gate: route to error terminal if decomposition failed / unparseable.
        var decompositionSuccessCheck = new FlowDecision(ctx => decompositionLlmOk.Get(ctx))
        { Id = "DecompositionLlmOk", Name = "Decomposition LLM OK?" };
        decompositionSuccessCheck.SetDisplayText("Decomposition LLM OK?");

        // ── Step 7a: Success path ──────────────────────────────────────
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
                WithLabel(new SetOutput { Id = "OutputDecomposition", Name = "Output Decomposition", OutputName = new("decomposition"), OutputValue = new(ctx => (object)(decompositionJson.Get(ctx) ?? "{}")) }, "Output Decomposition"),
                WithLabel(new SetOutput { Id = "OutputSubtaskCount", Name = "Output Subtask Count", OutputName = new("subtaskCount"), OutputValue = new(ctx => (object)subtaskCount.Get(ctx)) }, "Output Subtask Count"),
                WithLabel(new SetOutput { Id = "OutputContextIds", Name = "Output Context Ids", OutputName = new("contextIds"), OutputValue = new(ctx => (object)(contextIds.Get(ctx) ?? "[]")) }, "Output Context Ids"),
            }
        };
        exposeOutput.SetDisplayText("Expose Output");

        // ── Step 7b: Fail-closed error terminal (LOUD event + Finish) ──
        var emitDecompositionFailed = new EmitDecompositionEventActivity
        {
            Id = "EmitDecompositionFailed",
            Name = "Emit Decomposition Failed",
            EventType = new(DecompositionEvents.Failed),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Detail = new("llm-call for issue decomposition failed or returned unparseable/empty output"),
        };
        emitDecompositionFailed.SetDisplayText("Emit Decomposition Failed");

        var decompositionError = new Finish
        {
            Id = "DecompositionError",
            Name = "Decomposition Error"
        };
        decompositionError.SetDisplayText("Decomposition Error");

        // ── Build the flowchart ────────────────────────────────────────
        builder.Root = new Flowchart
        {
            Id = "IssueDecompositionFlowchart",
            Name = "Issue Decomposition Flowchart",
            Start = readInputs,
            Activities =
            {
                readInputs,
                emitStarted,
                gatherContext,
                storeContextResult,
                emitContextGathered,
                decomposeIssueLlm,
                parseDecomposition,
                decompositionSuccessCheck,

                // Success path
                emitDecompositionCompleted,
                setOutputResult,
                exposeOutput,

                // Fail-closed error terminal
                emitDecompositionFailed,
                decompositionError,
            },
            Connections =
            {
                new(readInputs, emitStarted),
                new(emitStarted, gatherContext),
                new(gatherContext, storeContextResult),
                new(storeContextResult, emitContextGathered),
                new(emitContextGathered, decomposeIssueLlm),
                new(decomposeIssueLlm, parseDecomposition),
                new(parseDecomposition, decompositionSuccessCheck),

                // Success path
                new(new FlowEndpoint(decompositionSuccessCheck, "True"),  new FlowEndpoint(emitDecompositionCompleted)),
                new(emitDecompositionCompleted, setOutputResult),
                new(setOutputResult, exposeOutput),

                // Fail-closed error path
                new(new FlowEndpoint(decompositionSuccessCheck, "False"), new FlowEndpoint(emitDecompositionFailed)),
                new(emitDecompositionFailed, decompositionError),
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

    /// <summary>
    /// Compose the work-item JSON handed to the context-gathering scan and the decomposition
    /// prompt. Prefers an explicit <paramref name="workItemJson"/>; otherwise wraps the free-text
    /// <paramref name="issueTitle"/> (plus the issue id) into a minimal JSON object so the
    /// downstream template has a stable shape. Pure; exposed for unit testing.
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
