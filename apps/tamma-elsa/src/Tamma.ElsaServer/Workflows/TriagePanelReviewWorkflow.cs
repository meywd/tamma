using System.Text.Json;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using Tamma.Activities.ADL;
using Tamma.Activities.Context;
using Tamma.ElsaServer.Workflows.Helpers;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;

using Tamma.Api.Services.Agents;
using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Triage Panel Review — 4-role LLM panel assesses a triage item.
/// Roles: Security Analyst, Developer, DevOps, Tester.
///
/// Each role dispatches llm-call with a role-specific triage action
/// (security=assess-vulnerability, developer/tester=triage-defect,
/// devops=diagnose-incident) — LLM mediation via the central <c>llm-call</c>
/// seam (the 32-5 boundary); no provider key ever enters the engine.
///
/// <para>Build-out (completeness audit 2026-06-22): the panel is now
/// <b>fail-closed</b>. A role whose <c>llm-call</c> yields no/garbage response is
/// recorded with <c>status="failed"</c> — NOT coalesced to a <c>{}</c>
/// participant — and each role's finding is persisted immediately so a later-role
/// failure does not lose earlier work. The aggregate exposes
/// <c>succeededCount</c> / <c>failedRoles</c> / <c>panelStatus</c>; a
/// <c>Panel Usable?</c> quorum gate routes a below-quorum panel to a
/// <c>FAILED</c> terminal (<c>panelStatus="failed"</c>) so the parent cycle can
/// skip label application — a failed panel is a LOUD failure, never a false
/// "successful review". Every lifecycle transition emits a <c>TRIAGE.PANEL.*</c>
/// DCB event via the durable drain (<see cref="EmitTriageEventActivity"/>).</para>
///
/// Flow:
///   Init → Emit STARTED → (per role: Review(llm-call) → Extract → Store)x4
///     → Aggregate → Panel Usable?
///         ├─ True  → Outputs(ok/partial) → Emit COMPLETED/PARTIAL → Finish
///         └─ False → Outputs(failed)      → Emit FAILED            → Finish
///
/// Inputs: repository, itemJson, contextJson, tenantId, quorum
/// Outputs: panelResultJson, panelStatus, succeededCount, failedRolesJson
/// </summary>
public class TriagePanelReviewWorkflow : WorkflowBase
{
    /// <summary>
    /// The single source of truth for the panel roster (DRY — the dispatch list
    /// and the aggregation iterate this one list, so they cannot drift).
    /// </summary>
    private static readonly AgentRole[] PanelRoles =
    [
        AgentRole.Security,
        AgentRole.Developer,
        AgentRole.Devops,
        AgentRole.Tester,
    ];

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Triage Panel Review";
        builder.DefinitionId = "triage-panel-review";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "4-role panel reviews item for triage (security/dev/devops/qa), fail-closed";

        // ================================================================
        // Variables
        // ================================================================
        var repository = builder.WithVariable<string>("Repository", "");
        var itemJson = builder.WithVariable<string>("ItemJson", "");
        var contextJson = builder.WithVariable<string>("ContextJson", "{}");
        var tenantId = builder.WithVariable<string>("TenantId", "");
        var quorum = builder.WithVariable<int>("Quorum", TriageEvents.DefaultQuorum);
        var itemNumber = builder.WithVariable<int>("ItemNumber", 0);

        // Per-role review results (JSON strings). Default "{}" = no usable review.
        var roleReviewVars = PanelRoles.ToDictionary(
            r => r,
            r => builder.WithVariable<string>($"{r}Review", "{}"));

        // Aggregated result + panel-health contract.
        var panelResultJson = builder.WithVariable<string>("PanelResultJson", "{}");
        var panelStatus = builder.WithVariable<string>("PanelStatus", TriagePanelAggregationHelper.StatusFailed);
        var succeededCount = builder.WithVariable<int>("SucceededCount", 0);
        var failedRolesJson = builder.WithVariable<string>("FailedRolesJson", "[]");

        // Shared LLM result.
        var llmResult = builder.WithVariable<IDictionary<string, object>?>();

        // ================================================================
        // 1. Init — copy inputs; parse the item number for store key + event tags.
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
                contextJson.Set(ctx, ctx.GetInput<string>("contextJson") ?? "{}");
                tenantId.Set(ctx, ctx.GetInput<string>("tenantId") ?? "");
                var inputQuorum = ctx.GetInput<int?>("quorum");
                if (inputQuorum.HasValue && inputQuorum.Value >= 1)
                    quorum.Set(ctx, inputQuorum.Value);
                itemNumber.Set(ctx, TriagePanelAggregationHelper.ParseItemNumber(item));
                return (object)repo;
            })
        };
        init.SetDisplayText("Initialize");

        // ================================================================
        // 2. Emit TRIAGE.PANEL.STARTED
        // ================================================================
        var emitStarted = EmitPanelEvent("EmitStarted", "Emit TRIAGE.PANEL.STARTED",
            _ => TriageEvents.PanelStarted,
            repository, itemNumber, tenantId,
            _ => PanelRoles.Length, _ => 0, _ => "[]");

        // ================================================================
        // 3. Role Reviews — 4 sequential llm-call dispatches, each followed by
        //    extraction and immediate persistence (so partial work survives).
        // ================================================================
        var roleNodes = new List<(DispatchWorkflow call, SetVariable extract, StoreRoleFindingActivity store)>();
        foreach (var role in PanelRoles)
        {
            var reviewVar = roleReviewVars[role];
            var idBase = role.ToString(); // Security / Developer / Devops / Tester

            var call = RoleTriageDispatch($"{idBase}Review", $"{idBase} Review", role,
                repository, itemJson, contextJson, llmResult);
            var extract = ExtractTriageReview(reviewVar, llmResult,
                $"Extract{idBase}Review", $"Extract {idBase} Review");
            var store = StoreReviewRole($"Store{idBase}Review", $"Store {idBase} Review",
                $"triage-{role.ToWire()}", repository, itemNumber, reviewVar);

            roleNodes.Add((call, extract, store));
        }

        // ================================================================
        // 4. Aggregate Results — fail-closed. Failed/empty role reviews are
        //    recorded as status="failed", NOT as {} participants.
        // ================================================================
        var aggregate = new SetVariable
        {
            Id = "Aggregate", Name = "Aggregate Results",
            Variable = panelResultJson,
            Value = new Input<object?>(ctx =>
            {
                var reviews = PanelRoles.ToDictionary(
                    r => r.ToWire(),
                    r => (string?)roleReviewVars[r].Get(ctx));

                var roster = PanelRoles.Select(r => r.ToWire()).ToList();
                var result = TriagePanelAggregationHelper.Aggregate(roster, reviews, quorum.Get(ctx));

                // Surface the panel-health signal as separate workflow variables so
                // the gate + the emit nodes + the outputs all read the same source.
                panelStatus.Set(ctx, result.PanelStatus);
                succeededCount.Set(ctx, result.SucceededCount);
                failedRolesJson.Set(ctx, JsonSerializer.Serialize(result.FailedRoles));

                return (object)TriagePanelAggregationHelper.Serialize(result);
            })
        };
        aggregate.SetDisplayText("Aggregate Results");

        // ================================================================
        // 5. Panel Usable? — quorum gate. Below quorum (panelStatus="failed")
        //    routes to the FAILED terminal; ok/partial route to the usable
        //    terminal. NO false success: a failed panel never reaches COMPLETED.
        // ================================================================
        var panelUsable = new FlowDecision(ctx =>
            panelStatus.Get(ctx) != TriagePanelAggregationHelper.StatusFailed)
        { Id = "PanelUsable", Name = "Panel Usable?" };
        panelUsable.SetDisplayText("Panel Usable?");

        // ----- Usable path (ok / partial) -----
        var usableOutputs = BuildOutputs("UsableOutputs", "Usable Outputs",
            panelResultJson, panelStatus, succeededCount, failedRolesJson);

        // Emit COMPLETED (status=ok) or PARTIAL (some failed) — driven by panelStatus.
        var emitUsable = EmitPanelEvent("EmitUsable", "Emit TRIAGE.PANEL.COMPLETED/PARTIAL",
            ctx => TriagePanelAggregationHelper.EventTypeForStatus(panelStatus.Get(ctx)),
            repository, itemNumber, tenantId,
            _ => PanelRoles.Length, ctx => succeededCount.Get(ctx), ctx => failedRolesJson.Get(ctx));

        // ----- Failed path (below quorum) -----
        var failedOutputs = BuildOutputs("FailedOutputs", "Failed Outputs",
            panelResultJson, panelStatus, succeededCount, failedRolesJson);

        var emitFailed = EmitPanelEvent("EmitFailed", "Emit TRIAGE.PANEL.FAILED",
            _ => TriageEvents.PanelFailed,
            repository, itemNumber, tenantId,
            _ => PanelRoles.Length, ctx => succeededCount.Get(ctx), ctx => failedRolesJson.Get(ctx));

        var finish = new Finish { Id = "Finish", Name = "Complete" };
        finish.SetDisplayText("Complete");

        // ================================================================
        // Flowchart
        // ================================================================
        var activities = new List<IActivity> { init, emitStarted };
        foreach (var (call, extract, store) in roleNodes)
        {
            activities.Add(call);
            activities.Add(extract);
            activities.Add(store);
        }
        activities.Add(aggregate);
        activities.Add(panelUsable);
        activities.Add(usableOutputs);
        activities.Add(emitUsable);
        activities.Add(failedOutputs);
        activities.Add(emitFailed);
        activities.Add(finish);

        var connections = new List<FlowConnection>
        {
            Connect(init, emitStarted),
            Connect(emitStarted, roleNodes[0].call),
        };

        // Per-role chain: call → extract → store → next role's call.
        for (var i = 0; i < roleNodes.Count; i++)
        {
            var (call, extract, store) = roleNodes[i];
            connections.Add(Connect(call, extract));
            connections.Add(Connect(extract, store));
            if (i + 1 < roleNodes.Count)
                connections.Add(Connect(store, roleNodes[i + 1].call));
            else
                connections.Add(Connect(store, aggregate));
        }

        // Aggregate → quorum gate → (usable | failed) terminals.
        connections.Add(Connect(aggregate, panelUsable));

        // Usable: outputs → emit COMPLETED/PARTIAL → finish.
        connections.Add(ConnectOutcome(panelUsable, "True", usableOutputs));
        connections.Add(Connect(usableOutputs, emitUsable));
        connections.Add(Connect(emitUsable, finish));

        // Failed: outputs (panelStatus=failed) → emit FAILED → finish.
        // The Error/False edge NEVER falls through to the usable/COMPLETED path.
        connections.Add(ConnectOutcome(panelUsable, "False", failedOutputs));
        connections.Add(Connect(failedOutputs, emitFailed));
        connections.Add(Connect(emitFailed, finish));

        builder.Root = new Flowchart
        {
            Id = "TriagePanelReviewFlowchart",
            Start = init,
            Activities = activities,
            Connections = connections,
        };
    }

    // ================================================================
    // Helper: Set the four workflow outputs (identical on both terminals; the
    // panelStatus value differs by which terminal set it).
    // ================================================================
    private static Sequence BuildOutputs(
        string id, string name,
        Variable<string> panelResultJson, Variable<string> panelStatus,
        Variable<int> succeededCount, Variable<string> failedRolesJson)
    {
        var seq = new Sequence
        {
            Id = id, Name = name,
            Activities =
            {
                WithLabel(new SetOutput
                    { Id = $"{id}_PanelResult", OutputName = new("panelResultJson"), OutputValue = new(ctx => (object)panelResultJson.Get(ctx)) }, "Output panelResultJson"),
                WithLabel(new SetOutput
                    { Id = $"{id}_PanelStatus", OutputName = new("panelStatus"), OutputValue = new(ctx => (object)panelStatus.Get(ctx)) }, "Output panelStatus"),
                WithLabel(new SetOutput
                    { Id = $"{id}_Succeeded", OutputName = new("succeededCount"), OutputValue = new(ctx => (object)succeededCount.Get(ctx)) }, "Output succeededCount"),
                WithLabel(new SetOutput
                    { Id = $"{id}_FailedRoles", OutputName = new("failedRolesJson"), OutputValue = new(ctx => (object)failedRolesJson.Get(ctx)) }, "Output failedRolesJson"),
            }
        };
        seq.SetDisplayText(name);
        return seq;
    }

    // ================================================================
    // Helper: Emit a TRIAGE.PANEL.* DCB event through the durable drain.
    // ================================================================
    private static EmitTriageEventActivity EmitPanelEvent(
        string id, string label,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, string> eventType,
        Variable<string> repository, Variable<int> itemNumber, Variable<string> tenantId,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, int> roleCount,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, int> succeededCount,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, string> failedRolesJson)
    {
        var emit = new EmitTriageEventActivity
        {
            Id = id, Name = label,
            EventType = new Input<string>(eventType),
            Repository = new Input<string>(ctx => repository.Get(ctx)),
            ItemNumber = new Input<int>(ctx => itemNumber.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantId.Get(ctx)),
            RoleCount = new Input<int>(roleCount),
            SucceededCount = new Input<int>(succeededCount),
            FailedRolesJson = new Input<string?>(ctx => failedRolesJson(ctx)),
        };
        emit.SetDisplayText(label);
        return emit;
    }

    // ================================================================
    // Helper: Create a DispatchWorkflow for a triage role review (llm-call).
    // ================================================================
    private static DispatchWorkflow RoleTriageDispatch(
        string id, string displayName, AgentRole role,
        Variable<string> repository, Variable<string> itemJson,
        Variable<string> contextJson,
        Variable<IDictionary<string, object>?> result)
    {
        var dispatch = new DispatchWorkflow
        {
            Id = id, Name = displayName,
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["role"] = role.ToWire(),
                ["action"] = RolePhaseMap.GetTriageActionForRole(role).ToWire(),
                ["variables"] = new Dictionary<string, object>
                {
                    ["itemJson"] = itemJson.Get(ctx),
                    ["contextJson"] = contextJson.Get(ctx),
                    ["repository"] = repository.Get(ctx),
                },
                ["enableTools"] = true,
            }),
            WaitForCompletion = new(true),
            Result = new(result),
        };
        dispatch.SetDisplayText(displayName);
        return dispatch;
    }

    // ================================================================
    // Helper: Extract a role's triage review from llmResult.
    //
    // Fail-closed: when llmResult is null / has no llmResponse / yields no
    // parseable JSON object, the target is left as the "{}" sentinel — which the
    // aggregation classifies as a FAILED role (NOT a participant). A real JSON
    // object is stored verbatim; free-form prose is wrapped as a usable
    // {"rawAssessment": ...} object (the role still produced an assessment).
    // ================================================================
    private static SetVariable ExtractTriageReview(
        Variable<string> target,
        Variable<IDictionary<string, object>?> llmResult,
        string id, string displayName)
    {
        var sv = new SetVariable
        {
            Id = id, Name = displayName,
            Variable = target,
            Value = new Input<object?>(ctx =>
            {
                var result = llmResult.Get(ctx);

                // No result, or the mediated call reported failure → fail-closed "{}".
                if (result == null) return (object)"{}";
                var success = !result.TryGetValue("success", out var s) || s is true;
                if (!success) return (object)"{}";
                if (!result.TryGetValue("llmResponse", out var r)) return (object)"{}";

                var output = r?.ToString();
                if (string.IsNullOrWhiteSpace(output)) return (object)"{}";

                var jsonStart = output.IndexOf('{');
                var jsonEnd = output.LastIndexOf('}');
                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    var jsonCandidate = output[jsonStart..(jsonEnd + 1)];
                    try
                    {
                        using var doc = JsonDocument.Parse(jsonCandidate);
                        // An empty {} object is not a usable assessment → fall through.
                        var hasAny = false;
                        if (doc.RootElement.ValueKind == JsonValueKind.Object)
                            foreach (var _ in doc.RootElement.EnumerateObject()) { hasAny = true; break; }
                        if (hasAny) return (object)jsonCandidate;
                    }
                    catch { /* not valid JSON */ }
                }

                // Free-form prose is still a usable assessment — wrap it.
                return (object)JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["rawAssessment"] = output,
                });
            })
        };
        sv.SetDisplayText(displayName);
        return sv;
    }

    // ================================================================
    // Helper: Persist a role's finding immediately (partial-result durability).
    // ================================================================
    private static StoreRoleFindingActivity StoreReviewRole(
        string id, string name, string role,
        Variable<string> repository, Variable<int> itemNumber,
        Variable<string> reviewVar)
    {
        var store = new StoreRoleFindingActivity
        {
            Id = id, Name = name,
            Repository = new Input<string>(ctx => repository.Get(ctx)),
            IssueNumber = new Input<int>(ctx => itemNumber.Get(ctx)),
            Role = new Input<string>(role),
            FindingsJson = new Input<string>(ctx => reviewVar.Get(ctx)),
            ContextId = new Output<string>(new Variable<string>()),
        };
        store.SetDisplayText(name);
        return store;
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));
}
