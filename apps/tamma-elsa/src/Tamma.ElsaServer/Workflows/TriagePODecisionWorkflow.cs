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
/// Triage PO Decision — Product Owner makes the final triage decision based on the
/// panel review.
///
/// Dispatches <c>llm-call</c> with role=<c>product_owner</c>, action=<c>triage-intake</c>.
/// Parses the decision: priority, type, complexity, automation level, labels, comment.
///
/// <para>Build-out (completeness audit 2026-06-22, <c>TriagePODecision.md</c>): the
/// step is now <b>fail-closed / no-false-success</b>:
/// <list type="bullet">
///   <item><description>#1 — it branches on the <c>llm-call</c> <c>success</c> bool.
///     A total LLM failure (providers down / budget / allowlist reject) routes to a
///     loud FAILED terminal that emits an explicit <c>llm-failed</c> marker
///     (<c>triage-failed</c>/<c>needs-human</c>) — it NEVER fabricates a clean
///     <c>needs-human</c>/<c>priority-normal</c> applied decision.</description></item>
///   <item><description>#2 — prose / unparseable LLM output is marked
///     <c>unparsed</c> (needs-human-review), not presented as a clean classified
///     decision.</description></item>
///   <item><description>#3 — every lifecycle transition emits a
///     <c>TRIAGE.PO_DECISION.*</c> DCB event via the durable drain
///     (<see cref="EmitTriagePoDecisionEventActivity"/>).</description></item>
///   <item><description>#4 — classification fields are validated against the Story
///     26-1 vocabulary; out-of-vocab values are clamped + flagged in the comment.</description></item>
///   <item><description>#7 — empty input short-circuits with a SKIPPED event (no
///     LLM spend).</description></item>
/// </list></para>
///
/// Flow:
///   Init → [Inputs Present?] ──No──► BuildSkipped → (emit SKIPPED) → SetOutputs → Finish
///        └─Yes─► Emit STARTED → PO Decision (llm-call) → Capture Result
///                  → [Call Succeeded?] ──False──► BuildFailure → (emit FAILED) → SetOutputs → Finish
///                       └─True─► Extract Decision (validate vocab, mark unparsed)
///                                 → (emit COMPLETED) → SetOutputs → Finish
///
/// Inputs: repository, itemJson, panelResultJson, tenantId
/// Outputs: decisionJson (contract preserved, additively carries status/reasoning);
///          plus callSucceeded, providerUsed, costUsd, rawResponse for audit.
/// </summary>
public class TriagePODecisionWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Triage PO Decision";
        builder.DefinitionId = "triage-po-decision";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "PO makes final triage decision based on panel review (fail-closed)";

        // ================================================================
        // Variables
        // ================================================================
        var repository = builder.WithVariable<string>("Repository", "");
        var itemJson = builder.WithVariable<string>("ItemJson", "");
        var panelResultJson = builder.WithVariable<string>("PanelResultJson", "{}");
        var tenantId = builder.WithVariable<string>("TenantId", "");
        var itemNumber = builder.WithVariable<int>("ItemNumber", 0);
        var decisionJson = builder.WithVariable<string>("DecisionJson", "{}");

        // Captured from the llm-call result (#1/#3/#6).
        var callSucceeded = builder.WithVariable<bool>("CallSucceeded", false);
        var providerUsed = builder.WithVariable<string>("ProviderUsed", "");
        var costUsd = builder.WithVariable<decimal>("CostUsd", 0m);
        var rawResponse = builder.WithVariable<string>("RawResponse", "");
        var failureSummary = builder.WithVariable<string>("FailureSummary", "");

        // Decision fields surfaced for the COMPLETED event payload.
        var decisionStatus = builder.WithVariable<string>("DecisionStatus", "");
        var priority = builder.WithVariable<string>("Priority", "");
        var type = builder.WithVariable<string>("Type", "");
        var complexity = builder.WithVariable<string>("Complexity", "");
        var automation = builder.WithVariable<string>("Automation", "");

        var llmResult = builder.WithVariable<IDictionary<string, object>?>();

        // ================================================================
        // 1. Init — copy inputs; parse the item number for event tags.
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
                panelResultJson.Set(ctx, ctx.GetInput<string>("panelResultJson") ?? "{}");
                tenantId.Set(ctx, ctx.GetInput<string>("tenantId") ?? "");
                itemNumber.Set(ctx, TriagePoDecisionHelper.ParseItemNumber(item));
                return (object)repo;
            })
        };
        init.SetDisplayText("Initialize");

        // ================================================================
        // 1a. Inputs Present? — #7 empty-input guard. Blank / {} item → skip the
        //     LLM call entirely (no spend on garbage).
        // ================================================================
        var inputsPresent = new FlowDecision(ctx =>
            TriagePoDecisionHelper.IsUsableInput(itemJson.Get(ctx)))
        { Id = "InputsPresent", Name = "Inputs Present?" };
        inputsPresent.SetDisplayText("Inputs Present?");

        // ----- Skip path (#7) -----
        var buildSkipped = new SetVariable
        {
            Id = "BuildSkipped", Name = "Build Skipped Decision",
            Variable = decisionJson,
            Value = new Input<object?>(ctx =>
            {
                var d = TriagePoDecisionHelper.BuildSkippedDecision();
                decisionStatus.Set(ctx, d.Status);
                return (object)TriagePoDecisionHelper.Serialize(d);
            })
        };
        buildSkipped.SetDisplayText("Build Skipped Decision");

        var emitSkipped = EmitEvent("EmitSkipped", "Emit TRIAGE.PO_DECISION.SKIPPED",
            _ => TriagePoDecisionEvents.Skipped,
            repository, itemNumber, tenantId,
            ctx => decisionStatus.Get(ctx), _ => "", _ => "", _ => "", _ => "",
            _ => "", _ => 0m, _ => "");

        // ================================================================
        // 2. Emit TRIAGE.PO_DECISION.STARTED
        // ================================================================
        var emitStarted = EmitEvent("EmitStarted", "Emit TRIAGE.PO_DECISION.STARTED",
            _ => TriagePoDecisionEvents.Started,
            repository, itemNumber, tenantId,
            _ => "", _ => "", _ => "", _ => "", _ => "",
            _ => "", _ => 0m, _ => "");

        // ================================================================
        // 3. PO Decision (via LlmCallWorkflow) — unchanged dispatch.
        // ================================================================
        var poDecisionCall = new DispatchWorkflow
        {
            Id = "PODecisionCall", Name = "PO Decision",
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["role"] = AgentRole.ProductOwner.ToWire(),
                ["action"] = AgentAction.TriageIntake.ToWire(),
                ["variables"] = new Dictionary<string, object>
                {
                    ["itemJson"] = itemJson.Get(ctx),
                    ["panelResultJson"] = panelResultJson.Get(ctx),
                    ["repository"] = repository.Get(ctx),
                },
                ["enableTools"] = false,
                ["tenantId"] = tenantId.Get(ctx),
            }),
            WaitForCompletion = new(true),
            Result = new(llmResult),
        };
        poDecisionCall.SetDisplayText("PO Decision");

        // ================================================================
        // 3a. Capture Result — #1/#6. Read success/providerUsed/costUsd/rawResponse
        //     + the failure diagnostics summary, NOT just llmResponse. Fail-closed:
        //     a missing `success` key reads as FAILED (we never assume success).
        // ================================================================
        var captureResult = new SetVariable
        {
            Id = "CaptureResult", Name = "Capture Result",
            Variable = callSucceeded,
            Value = new Input<object?>(ctx =>
            {
                var result = llmResult.Get(ctx);
                if (result == null)
                {
                    failureSummary.Set(ctx, "no result from llm-call");
                    return (object)false;
                }

                // Fail-closed: only an explicit success==true counts as success.
                var succeeded = result.TryGetValue("success", out var s) && s is true;

                providerUsed.Set(ctx, result.TryGetValue("providerUsed", out var pv)
                    ? pv?.ToString() ?? "" : "");
                costUsd.Set(ctx, result.TryGetValue("costUsd", out var c)
                    ? EmitTriagePoDecisionEventActivity.ParseCost(c) : 0m);
                rawResponse.Set(ctx, result.TryGetValue("llmResponse", out var r)
                    ? r?.ToString() ?? "" : "");

                if (!succeeded)
                {
                    var wfOut = result.TryGetValue("workflowOutput", out var wo)
                        ? wo?.ToString() : null;
                    failureSummary.Set(ctx, TriagePoDecisionHelper.SummarizeFailure(wfOut));
                }

                return (object)succeeded;
            })
        };
        captureResult.SetDisplayText("Capture Result");

        // ================================================================
        // 3b. Call Succeeded? — #1 FlowDecision. False → FAILED terminal (no
        //     fabricated decision); True → ExtractDecision.
        // ================================================================
        var callSucceededGate = new FlowDecision(ctx => callSucceeded.Get(ctx))
        { Id = "CallSucceeded", Name = "PO Call Succeeded?" };
        callSucceededGate.SetDisplayText("PO Call Succeeded?");

        // ----- Failure path (#1) -----
        var buildFailure = new SetVariable
        {
            Id = "BuildFailure", Name = "Build Failure Decision",
            Variable = decisionJson,
            Value = new Input<object?>(ctx =>
            {
                var d = TriagePoDecisionHelper.BuildFailureDecision(failureSummary.Get(ctx));
                decisionStatus.Set(ctx, d.Status);
                return (object)TriagePoDecisionHelper.Serialize(d);
            })
        };
        buildFailure.SetDisplayText("Build Failure Decision");

        var emitFailed = EmitEvent("EmitFailed", "Emit TRIAGE.PO_DECISION.FAILED",
            _ => TriagePoDecisionEvents.Failed,
            repository, itemNumber, tenantId,
            ctx => decisionStatus.Get(ctx), _ => "", _ => "", _ => "", _ => "",
            ctx => providerUsed.Get(ctx), ctx => costUsd.Get(ctx),
            ctx => failureSummary.Get(ctx));

        // ================================================================
        // 4. Extract Decision (success branch) — #2/#4/#5 via the pure helper.
        // ================================================================
        var extractDecision = new SetVariable
        {
            Id = "ExtractDecision", Name = "Extract Decision",
            Variable = decisionJson,
            Value = new Input<object?>(ctx =>
            {
                var d = TriagePoDecisionHelper.ParseDecision(rawResponse.Get(ctx));
                decisionStatus.Set(ctx, d.Status);
                priority.Set(ctx, d.Priority);
                type.Set(ctx, d.Type);
                complexity.Set(ctx, d.Complexity);
                automation.Set(ctx, d.Automation);
                return (object)TriagePoDecisionHelper.Serialize(d);
            })
        };
        extractDecision.SetDisplayText("Extract Decision");

        var emitCompleted = EmitEvent("EmitCompleted", "Emit TRIAGE.PO_DECISION.COMPLETED",
            _ => TriagePoDecisionEvents.Completed,
            repository, itemNumber, tenantId,
            ctx => decisionStatus.Get(ctx),
            ctx => priority.Get(ctx), ctx => type.Get(ctx),
            ctx => complexity.Get(ctx), ctx => automation.Get(ctx),
            ctx => providerUsed.Get(ctx), ctx => costUsd.Get(ctx), _ => "");

        // ================================================================
        // 5. Set Outputs — decisionJson (contract preserved) + audit outputs (#6).
        // ================================================================
        var setOutputs = new Sequence
        {
            Id = "SetOutputs", Name = "Set Outputs",
            Activities =
            {
                WithLabel(new SetOutput
                    { Id = "OutDecision", OutputName = new("decisionJson"), OutputValue = new(ctx => (object)decisionJson.Get(ctx)) }, "Output decisionJson"),
                WithLabel(new SetOutput
                    { Id = "OutCallSucceeded", OutputName = new("callSucceeded"), OutputValue = new(ctx => (object)callSucceeded.Get(ctx)) }, "Output callSucceeded"),
                WithLabel(new SetOutput
                    { Id = "OutProviderUsed", OutputName = new("providerUsed"), OutputValue = new(ctx => (object)providerUsed.Get(ctx)) }, "Output providerUsed"),
                WithLabel(new SetOutput
                    { Id = "OutCostUsd", OutputName = new("costUsd"), OutputValue = new(ctx => (object)costUsd.Get(ctx)) }, "Output costUsd"),
                WithLabel(new SetOutput
                    { Id = "OutRawResponse", OutputName = new("rawResponse"), OutputValue = new(ctx => (object)rawResponse.Get(ctx)) }, "Output rawResponse"),
            }
        };
        setOutputs.SetDisplayText("Set Outputs");

        var finish = new Finish { Id = "Finish", Name = "Complete" };
        finish.SetDisplayText("Complete");

        // ================================================================
        // Flowchart
        // ================================================================
        builder.Root = new Flowchart
        {
            Id = "TriagePODecisionFlowchart",
            Start = init,
            Activities =
            {
                init, inputsPresent,
                buildSkipped, emitSkipped,
                emitStarted, poDecisionCall, captureResult, callSucceededGate,
                buildFailure, emitFailed,
                extractDecision, emitCompleted,
                setOutputs, finish,
            },
            Connections =
            {
                Connect(init, inputsPresent),

                // #7 — empty input → skip (no LLM spend).
                ConnectOutcome(inputsPresent, "False", buildSkipped),
                Connect(buildSkipped, emitSkipped),
                Connect(emitSkipped, setOutputs),

                // Inputs present → STARTED → dispatch → capture → success gate.
                ConnectOutcome(inputsPresent, "True", emitStarted),
                Connect(emitStarted, poDecisionCall),
                Connect(poDecisionCall, captureResult),
                Connect(captureResult, callSucceededGate),

                // #1 — LLM failed → FAILED terminal (no fabricated decision).
                ConnectOutcome(callSucceededGate, "False", buildFailure),
                Connect(buildFailure, emitFailed),
                Connect(emitFailed, setOutputs),

                // Success → extract (validate/clamp/unparsed) → COMPLETED.
                ConnectOutcome(callSucceededGate, "True", extractDecision),
                Connect(extractDecision, emitCompleted),
                Connect(emitCompleted, setOutputs),

                Connect(setOutputs, finish),
            }
        };
    }

    // ================================================================
    // Helper: Emit a TRIAGE.PO_DECISION.* DCB event through the durable drain.
    // ================================================================
    private static EmitTriagePoDecisionEventActivity EmitEvent(
        string id, string label,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, string> eventType,
        Variable<string> repository, Variable<int> itemNumber, Variable<string> tenantId,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, string> decisionStatus,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, string> priority,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, string> type,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, string> complexity,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, string> automation,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, string> providerUsed,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, decimal> costUsd,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, string> error)
    {
        var emit = new EmitTriagePoDecisionEventActivity
        {
            Id = id, Name = label,
            EventType = new Input<string>(eventType),
            Repository = new Input<string>(ctx => repository.Get(ctx)),
            ItemNumber = new Input<int>(ctx => itemNumber.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantId.Get(ctx)),
            DecisionStatus = new Input<string?>(ctx => decisionStatus(ctx)),
            Priority = new Input<string?>(ctx => priority(ctx)),
            Type = new Input<string?>(ctx => type(ctx)),
            Complexity = new Input<string?>(ctx => complexity(ctx)),
            Automation = new Input<string?>(ctx => automation(ctx)),
            ProviderUsed = new Input<string?>(ctx => providerUsed(ctx)),
            CostUsd = new Input<decimal>(costUsd),
            Error = new Input<string?>(ctx => error(ctx)),
        };
        emit.SetDisplayText(label);
        return emit;
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));
}
