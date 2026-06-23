using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using Tamma.Activities.ADL;
using Tamma.ElsaServer.Workflows.Helpers;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;

using Tamma.Api.Services.Agents;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Triage Context Gathering — gathers triage-time context for a single untriaged
/// item (code usage of the affected package/module, dependency graph, CVE details
/// for security alerts, changelog / migration guides) by dispatching one
/// tool-enabled <c>llm-call</c> (<c>role=developer</c>, <c>action=context-scan</c>)
/// and returning a <c>contextJson</c> bundle that the panel-review and PO-decision
/// sub-workflows reason over.
///
/// <para>Build-out (completeness audit 2026-06-22, <c>TriageContextGathering.md</c>):
/// the stage is now contract-correct and fail-closed:
/// <list type="bullet">
///   <item><description><b>Variable contract fixed (P0 #1).</b> The dispatch passes
///     the <c>context-scan</c> template's <i>declared</i> variables
///     (<c>workItemJson</c> / <c>workItemType</c> / <c>previousFindings</c>) — the
///     prior <c>itemJson</c> / <c>itemType</c> / <c>scanFocus</c> rendered empty, so
///     the model scanned with no work item. The detected item type now feeds a real
///     <c>{{workItemType}}</c> so the scan is item-type-aware.</description></item>
///   <item><description><b>Fail-closed on the <c>llm-call</c> <c>success</c> flag
///     (P0 #2).</b> An all-providers-failed scan no longer coalesces to <c>"{}"</c>
///     presented as success — a <c>Context Gathered?</c> gate routes a failed scan
///     to a LOUD <c>TRIAGE.CONTEXT.FAILED</c> terminal and reports
///     <c>contextStatus="failed"</c> so the parent cycle can skip the panel.</description></item>
///   <item><description><b>Robust item-type detection (P1 #5)</b> — parses the item
///     JSON via <see cref="TriageContextHelper.DetectItemType"/> instead of
///     substring-sniffing the raw text.</description></item>
///   <item><description><b>DCB events (P1 #4)</b> — <c>TRIAGE.CONTEXT.STARTED</c>
///     after init and exactly one terminal (<c>COMPLETED</c> / <c>EMPTY</c> /
///     <c>FAILED</c>) via <see cref="EmitTriageContextEventActivity"/> on the durable
///     drain, so audit can see whether context was gathered, degraded, or failed.</description></item>
///   <item><description><b><c>tenantId</c> threading (P1 #6)</b> — a <c>TenantId</c>
///     variable is stamped (the drain scopes events to it) and forwarded into the
///     <c>llm-call</c> dispatch for SaaS prompt + BYOK resolution.</description></item>
/// </list>
/// (Deferred per the audit: a dedicated CVE/changelog action + structured advisory
/// engine-callback (#7), idempotency cache (#8), balanced-brace scanner (#9), and
/// the shared init→scan→extract helper shared with <c>ContextGatheringWorkflow</c>
/// (#10).)</para>
///
/// Flow:
///   Init → Emit STARTED → Gather Context (llm-call) → Extract + Status
///     → Context Gathered?
///         ├─ True  → Output(ok/empty) → Emit COMPLETED/EMPTY → Finish
///         └─ False → Output(failed)   → Emit FAILED          → Finish
///
/// Inputs: repository, itemJson, tenantId
/// Outputs: contextJson, contextStatus
/// </summary>
public class TriageContextGatheringWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Triage Context Gathering";
        builder.DefinitionId = "triage-context-gathering";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Gather context for triage: code usage, deps, CVE, changelog (fail-closed)";

        // ================================================================
        // Variables
        // ================================================================
        var repository = builder.WithVariable<string>("Repository", "");
        var itemJson = builder.WithVariable<string>("ItemJson", "");
        var tenantId = builder.WithVariable<string>("TenantId", "");
        var contextJson = builder.WithVariable<string>("ContextJson", "{}");
        // Detected item type — drives {{workItemType}} so the scan is item-aware.
        var itemType = builder.WithVariable<string>("ItemType", TriageContextHelper.ItemTypeIssue);
        // Item number for event tags / audit correlation (0 when unknown).
        var itemNumber = builder.WithVariable<int>("ItemNumber", 0);
        // Context-health signal from the (fail-closed) extraction:
        // "ok" / "empty" => usable; "failed" => no context gathered, do NOT run panel.
        var contextStatus = builder.WithVariable<string>(
            "ContextStatus", TriageContextEvents.StatusFailed);

        var llmResult = builder.WithVariable<IDictionary<string, object>?>();

        // ================================================================
        // 1. Init — read inputs; parse item type + number.
        // ================================================================
        var init = new SetVariable
        {
            Id = "Init", Name = "Initialize",
            Variable = repository,
            Value = new Input<object?>(ctx =>
            {
                var repo = ctx.GetInput<string>("repository") ?? "";
                var item = ctx.GetInput<string>("itemJson") ?? "";
                itemJson.Set(ctx, item);
                tenantId.Set(ctx, ctx.GetInput<string>("tenantId") ?? "");
                itemType.Set(ctx, TriageContextHelper.DetectItemType(item));
                itemNumber.Set(ctx, TriagePanelAggregationHelper.ParseItemNumber(item));
                return (object)repo;
            })
        };
        init.SetDisplayText("Initialize");

        // ================================================================
        // 2. Emit TRIAGE.CONTEXT.STARTED
        // ================================================================
        var emitStarted = EmitContextEvent("EmitStarted", "Emit TRIAGE.CONTEXT.STARTED",
            _ => TriageContextEvents.Started,
            repository, itemNumber, tenantId,
            ctx => itemType.Get(ctx), _ => "", _ => 0);

        // ================================================================
        // 3. Gather Context (via LlmCallWorkflow) — correct template variables.
        // ================================================================
        var gatherContext = new DispatchWorkflow
        {
            Id = "GatherContext", Name = "Gather Context",
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["role"] = AgentRole.Developer.ToWire(),
                ["action"] = AgentAction.ContextScan.ToWire(),
                ["tenantId"] = tenantId.Get(ctx),
                ["variables"] = new Dictionary<string, object>
                {
                    // The context-scan template (SystemPrompts.cs) declares
                    // workItemJson / workItemType / previousFindings — pass those
                    // names so the model actually sees the triage item.
                    ["workItemJson"] = itemJson.Get(ctx),
                    ["workItemType"] = itemType.Get(ctx),
                    ["previousFindings"] = "{}",
                    ["repository"] = repository.Get(ctx),
                },
                ["enableTools"] = true,
            }),
            WaitForCompletion = new(true),
            Result = new(llmResult),
        };
        gatherContext.SetDisplayText("Gather Context");

        // ================================================================
        // 4. Extract Result + Status — fail-closed (no false success).
        // ================================================================
        var extractResult = new SetVariable
        {
            Id = "ExtractResult", Name = "Extract Result",
            Variable = contextJson,
            Value = new Input<object?>(ctx =>
            {
                var (json, status) = TriageContextHelper.ExtractContext(llmResult.Get(ctx));
                contextStatus.Set(ctx, status);
                return (object)json;
            })
        };
        extractResult.SetDisplayText("Extract Result");

        // ================================================================
        // 4a. Context Gathered? — fail-closed gate. "failed" routes to the LOUD
        //     FAILED terminal; "ok"/"empty" proceed to the usable terminal. NO
        //     false success: a failed scan never reaches COMPLETED.
        // ================================================================
        var contextGathered = new FlowDecision(ctx =>
            contextStatus.Get(ctx) != TriageContextEvents.StatusFailed)
        { Id = "ContextGathered", Name = "Context Gathered?" };
        contextGathered.SetDisplayText("Context Gathered?");

        // ----- Usable path (ok / empty) -----
        var usableOutputs = BuildOutputs("UsableOutputs", "Usable Outputs",
            contextJson, contextStatus);

        // Emit COMPLETED (ok) or EMPTY (degraded) — driven by contextStatus.
        var emitUsable = EmitContextEvent("EmitUsable", "Emit TRIAGE.CONTEXT.COMPLETED/EMPTY",
            ctx => TriageContextEvents.EventTypeForStatus(contextStatus.Get(ctx)),
            repository, itemNumber, tenantId,
            ctx => itemType.Get(ctx), ctx => contextStatus.Get(ctx),
            ctx => contextJson.Get(ctx).Length);

        // ----- Failed path (no context gathered) -----
        // Force the output to the "{}" sentinel + failed status so a downstream
        // consumer that ignores contextStatus still gets no phantom context.
        var failedSetStatus = new SetVariable
        {
            Id = "FailedSetStatus", Name = "Mark Context Failed",
            Variable = contextJson,
            Value = new Input<object?>(ctx =>
            {
                contextStatus.Set(ctx, TriageContextEvents.StatusFailed);
                return (object)"{}";
            })
        };
        failedSetStatus.SetDisplayText("Mark Context Failed");

        var failedOutputs = BuildOutputs("FailedOutputs", "Failed Outputs",
            contextJson, contextStatus);

        var emitFailed = EmitContextEvent("EmitFailed", "Emit TRIAGE.CONTEXT.FAILED",
            _ => TriageContextEvents.Failed,
            repository, itemNumber, tenantId,
            ctx => itemType.Get(ctx), _ => TriageContextEvents.StatusFailed, _ => 0);

        var finish = new Finish { Id = "Finish", Name = "Complete" };
        finish.SetDisplayText("Complete");

        // ================================================================
        // Flowchart
        // ================================================================
        builder.Root = new Flowchart
        {
            Id = "TriageContextGatheringFlowchart",
            Start = init,
            Activities =
            {
                init, emitStarted, gatherContext, extractResult,
                contextGathered,
                usableOutputs, emitUsable,
                failedSetStatus, failedOutputs, emitFailed,
                finish,
            },
            Connections =
            {
                Connect(init, emitStarted),
                Connect(emitStarted, gatherContext),
                Connect(gatherContext, extractResult),
                Connect(extractResult, contextGathered),

                // Usable (ok/empty) → outputs → emit COMPLETED/EMPTY → finish.
                ConnectOutcome(contextGathered, "True", usableOutputs),
                Connect(usableOutputs, emitUsable),
                Connect(emitUsable, finish),

                // Failed → mark "{}"/failed → outputs → emit FAILED → finish.
                // The False edge NEVER falls through to the usable/COMPLETED path.
                ConnectOutcome(contextGathered, "False", failedSetStatus),
                Connect(failedSetStatus, failedOutputs),
                Connect(failedOutputs, emitFailed),
                Connect(emitFailed, finish),
            }
        };
    }

    // ================================================================
    // Helper: Set the two workflow outputs (contextJson + contextStatus).
    // ================================================================
    private static Sequence BuildOutputs(
        string id, string name,
        Variable<string> contextJson, Variable<string> contextStatus)
    {
        var seq = new Sequence
        {
            Id = id, Name = name,
            Activities =
            {
                WithLabel(new SetOutput
                    { Id = $"{id}_Context", OutputName = new("contextJson"), OutputValue = new(ctx => (object)contextJson.Get(ctx)) }, "Output contextJson"),
                WithLabel(new SetOutput
                    { Id = $"{id}_Status", OutputName = new("contextStatus"), OutputValue = new(ctx => (object)contextStatus.Get(ctx)) }, "Output contextStatus"),
            }
        };
        seq.SetDisplayText(name);
        return seq;
    }

    // ================================================================
    // Helper: Emit a TRIAGE.CONTEXT.* DCB event through the durable drain.
    // ================================================================
    private static EmitTriageContextEventActivity EmitContextEvent(
        string id, string label,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, string> eventType,
        Variable<string> repository, Variable<int> itemNumber, Variable<string> tenantId,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, string> itemType,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, string> contextStatus,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, int> contextJsonLength)
    {
        var emit = new EmitTriageContextEventActivity
        {
            Id = id, Name = label,
            EventType = new Input<string>(eventType),
            Repository = new Input<string>(ctx => repository.Get(ctx)),
            ItemNumber = new Input<int>(ctx => itemNumber.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantId.Get(ctx)),
            ItemType = new Input<string?>(ctx => itemType(ctx)),
            ContextStatus = new Input<string?>(ctx => contextStatus(ctx)),
            ContextJsonLength = new Input<int>(contextJsonLength),
        };
        emit.SetDisplayText(label);
        return emit;
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));
}
