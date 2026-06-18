# Story 32-7: Multi-Agent Design/Review Panels in Elsa (strategy-driven)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

## User Story

As a **workflow author building design and review steps**,
I want reusable, strategy-driven multi-agent panel primitives (`RunAgentPanelActivity` + `AggregatePanelActivity`) that fan a step out to N agents of a role and combine their outputs under a selectable strategy (`single`, `consensus`, `lead+critics`, `llm-judge`),
So that design/review steps are no longer hardcoded per-role sequences, every panel member is a first-class benchmarkable agent, and the panel result plus per-member action-trail entries are captured for audit and learning.

## Priority

P1 - Generalizes the hardcoded triage/plan review panels into the reusable primitive the rest of Epic 32's benchmarking, leaderboards, and learning loop depend on.

## Context

Today two workflows hardcode their panels:

- **`TriagePanelReviewWorkflow`** (`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TriagePanelReviewWorkflow.cs`) — a fixed 4-role **sequential** panel (security → developer → devops → tester). Each role is a `DispatchWorkflow` to the `llm-call` workflow with a role-specific triage action (`RolePhaseMap.GetTriageActionForRole`), then a `SetVariable` extracts the role's JSON from `llmResult["llmResponse"]`, and a final `Aggregate` `SetVariable` concatenates the four reviews into `{ reviews[], reviewCount }`. There is **no strategy** — aggregation is a dumb merge — and **no per-member agent identity**: the "panel members" are anonymous role strings, so nothing can be benchmarked.
- **`PlanReviewWorkflow`** (`.../Workflows/PlanReviewWorkflow.cs`) — a richer 7-role sequential debate (independent review → anonymized rebuttal round → PO decision, looping to `maxRounds`). Same shape: per-role `DispatchWorkflow("llm-call")` + extract + `StoreRoleFindingActivity`, with a bespoke early-termination consensus check (`allRebuttalApproved`) baked into the flowchart.

Both are sequential, role-hardcoded, and re-implement aggregation by hand. This story extracts the panel-fan-out and the aggregation into two reusable Elsa activities under a new `AggregatePanelStrategy` interface, makes every panel member a resolved first-class **Agent** (32-1/32-2) executed through the managed-agent layer (`IManagedAgent`, 32-5), emits per-member action-trail entries (32-6) and a single `AGENT.PANEL.AGGREGATED` event, and refactors both existing workflows onto the primitives with golden-output parity.

The design spec for the epic is `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md` (§ "Multi-agent design/review steps"). It pins the defaults: **`lead+critics` for design steps, `consensus` for review steps**, with specialized **security / performance / visual** reviewers, and a workflow `iteration_count` loop until gates pass or max iterations.

## Acceptance Criteria

1. **`RunAgentPanelActivity` fans out to N agents of a role.** A new Elsa activity at `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/RunAgentPanelActivity.cs` accepts a panel definition — an ordered list of members `{ agentId? , role, weight, position: lead|critic|member }` plus a `PanelStrategy` and panel-level inputs (`variables`, `enableTools`) — resolves each member to an `IManagedAgent` (32-5) and executes the members, **parallel where the strategy permits** (single/consensus/llm-judge run members concurrently; `lead+critics` runs the lead first, then critics in parallel). It collects per-member `AgentRunResult`s into an ordered `PanelMemberResult[]` (member position, agentId, role, raw output, parsed verdict/score, token + cost basis, latency, success flag).

2. **`AggregatePanelActivity` implements four strategies.** A new Elsa activity at `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/AggregatePanelActivity.cs` takes the `PanelMemberResult[]` and a `PanelStrategy` and produces a single `PanelAggregateResult` via the `IAggregatePanelStrategy` seam:
   - **`single`** — passthrough of the sole (or first/lead) member's output; no combination.
   - **`consensus`** — majority/weighted vote over member verdicts with a deterministic tie-break (highest summed weight wins; on a weight tie, the earliest member position wins). Records the vote tally and the winning verdict.
   - **`lead+critics`** — the lead's proposal is fed back with the critics' annotations to the lead for one revision pass; the revised lead output is the aggregate. (This is the **design** default.)
   - **`llm-judge`** — a designated **judge agent** (a configured member with `position: judge`, or a panel-level `judgeAgentId`) scores/selects among member outputs and emits a winner + rationale. The judge's rationale is **sanitized** before being recorded to the trail.
   Each strategy is a separate `IAggregatePanelStrategy` implementation, unit-tested with **deterministic fixtures** (no live LLM).

3. **Strategy defaults match the design spec.** When a panel definition omits a strategy, **design panels default to `lead+critics`** and **review panels default to `consensus`**, per `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md`.

4. **Design step + review step are expressible on the primitives.** A design step fans architect agents out under `lead+critics` and aggregates to a chosen/synthesized design. A review step fans reviewer agents — including specialized **security**, **performance**, and **visual** reviewers — out under `consensus` and aggregates to a verdict plus classified findings. Both are demonstrated by a `PanelStepWorkflow` (or equivalent composable sub-workflow) wired from the primitives.

5. **Iteration loop until gates pass or max.** The panel-bearing workflow tracks an `iterationCount` and loops the design→review cycle until review gates pass (consensus verdict `approve` / no blocking findings) or `maxIterations` is reached; every iteration emits `AGENT.ITERATION.COMPLETED` (consumed downstream by 32-8/32-10). On max-iterations-without-pass the workflow escalates (mirrors `PlanReviewWorkflow`'s `forceNeedsHuman`).

6. **Per-member action-trail entries.** Each panel member run produces its own action-trail entry (32-6) tagged with `panelId` + `memberPosition` (+ `agentId`, config version, role, provider, model, prompt key/version, `issueId`, `iteration`). The aggregate produces a single **`AGENT.PANEL.AGGREGATED`** DCB event carrying the chosen strategy, the winner/consensus metadata (vote tally or judge winner + sanitized rationale), member count, and aggregate token/cost basis.

7. **Existing workflows refactored without regression.** `TriagePanelReviewWorkflow` and `PlanReviewWorkflow` are refactored to use `RunAgentPanelActivity` + `AggregatePanelActivity` instead of their hardcoded per-role `DispatchWorkflow` sequences. A **golden-output test** asserts the refactored workflows produce byte-equivalent `panelResultJson` / `decision`+`discussionLog` output for fixed (mocked) member outputs — no behavior regression. The legacy hardcoded per-role helpers are removed once parity is proven; the workflow `DefinitionId`s (`triage-panel-review`, `plan-review`) and their input/output contracts are unchanged so callers (`TriageItemCycleWorkflow`, etc.) need no edits.

8. **Visibility + SaaS provider gating per member.** Panel definitions are persisted as part of an agent or workflow config and respect public/private agent visibility (32-1/32-2). When resolving members, an **ineligible member is gated**: in SaaS, members backed by `ICLIAgentProvider`/token providers are rejected (API-key-only path, per spec §"Provider credential & auth model"), and a member whose agent is not visible to the executing tenant is excluded. Gating an ineligible member never crashes the panel — it is dropped with a recorded `AGENT.PANEL.MEMBER_GATED` trail note, and the panel proceeds if a quorum remains (else fails loud).

9. **Tenant-scoped execution + per-tenant credentials/budgets.** Panels execute tenant-scoped; each member resolves credentials via the per-tenant resolution order (BYOK → platform, 32-3) and is subject to per-tenant budget clamps. Member token/cost is attributed to the executing tenant. A panel that would breach the tenant budget stops adding members and aggregates over what completed (recorded in the aggregate event).

10. **Tests.** Unit tests cover: each strategy's aggregation correctness with deterministic fixtures (`single` passthrough, `consensus` majority + weighted + tie-break, `lead+critics` revision, `llm-judge` selection + sanitized rationale); parallel member execution (members invoked concurrently; results ordered deterministically by position regardless of completion order); SaaS gating of an ineligible member (CLI-backed / not-visible) with quorum behavior; budget-clamp early stop; and refactor parity (golden output) for both `TriagePanelReviewWorkflow` and `PlanReviewWorkflow`.

## Technical Design

### New components

```
apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/
  RunAgentPanelActivity.cs            # Elsa activity: fan out members → IManagedAgent
  AggregatePanelActivity.cs           # Elsa activity: combine results under a strategy
  Panels/
    PanelStrategy.cs                  # enum: Single | Consensus | LeadCritics | LlmJudge
    PanelMember.cs                    # record: AgentId?, Role, Weight, Position
    PanelMemberPosition.cs            # enum: Lead | Critic | Member | Judge
    PanelDefinition.cs               # record: Members[], Strategy?, JudgeAgentId?, MaxIterations
    PanelMemberResult.cs              # per-member run result (output, verdict, tokens, cost, latency)
    PanelAggregateResult.cs           # aggregate: Strategy, WinnerVerdict, Tally, Rationale, Members
    IAggregatePanelStrategy.cs        # strategy seam
    SinglePanelStrategy.cs
    ConsensusPanelStrategy.cs
    LeadCriticsPanelStrategy.cs
    LlmJudgePanelStrategy.cs
    PanelStrategyFactory.cs           # PanelStrategy → IAggregatePanelStrategy
    PanelEventTypes.cs                # AGENT.PANEL.AGGREGATED, AGENT.PANEL.MEMBER_GATED

apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/
  PanelStepWorkflow.cs               # composable design/review step over the primitives (AC4/AC5)
```

### Strategy interface (C#)

```csharp
// IAggregatePanelStrategy.cs — pure, deterministic, no live LLM in the aggregation step
// (LeadCritics / LlmJudge perform their LLM call BEFORE aggregation, via RunAgentPanelActivity's
//  revision/judge member, so this seam stays unit-testable with fixtures).
public interface IAggregatePanelStrategy
{
    PanelStrategy Strategy { get; }

    /// <summary>Combine ordered per-member results into a single aggregate verdict/output.</summary>
    PanelAggregateResult Aggregate(PanelDefinition definition, IReadOnlyList<PanelMemberResult> members);
}

public sealed record PanelMember(
    string? AgentId,                  // null ⇒ resolve the tenant's default agent for Role
    AgentRole Role,
    double Weight = 1.0,
    PanelMemberPosition Position = PanelMemberPosition.Member);

public sealed record PanelMemberResult(
    int Index,
    PanelMemberPosition Position,
    string? AgentId,
    AgentRole Role,
    string RawOutput,
    string? ParsedVerdict,            // e.g. "approve" | "concerns" | "reject"
    double? Score,                    // for llm-judge / weighted strategies
    int PromptTokens,
    int CompletionTokens,
    long LatencyMs,
    bool Success);

public sealed record PanelAggregateResult(
    PanelStrategy Strategy,
    string WinnerVerdict,
    IReadOnlyDictionary<string, double> Tally,   // verdict → summed weight (consensus)
    string? Rationale,                            // sanitized judge rationale (llm-judge)
    int? WinnerIndex,                             // selected member (single / llm-judge)
    IReadOnlyList<PanelMemberResult> Members,
    int TotalPromptTokens,
    int TotalCompletionTokens);
```

### `RunAgentPanelActivity` (Elsa activity signature)

Follows the existing `CallLlmActivity` pattern (`[Activity(...)]` + `[FlowNode(...)]`, `Input<T>` properties, `[JsonConstructor]` + DI ctor, `CompleteActivityWithOutcomesAsync`). Extends the `Tamma.Activities.AgentDispatch` namespace where the existing dispatch activities live.

```csharp
[Activity("Tamma.RunAgentPanel", "Run Agent Panel",
    "Fan a step out to N agents of a role and collect per-member results", Kind = ActivityKind.Task)]
[FlowNode("Done", "Gated", "Failed")]
public class RunAgentPanelActivity : Activity   // outcomes ⇒ Activity, not CodeActivity
{
    [Input(Description = "Panel definition (members, strategy, judge, max iterations) as JSON", UIHint = "json-editor")]
    public Input<string> PanelDefinitionJson { get; set; } = default!;

    [Input(Description = "Panel-level variables passed to each member's prompt")]
    public Input<IDictionary<string, object>> Variables { get; set; } = default!;

    [Input(Description = "Enable tools for members", DefaultValue = true)]
    public Input<bool> EnableTools { get; set; } = new(true);

    [Input(Description = "Panel id (stable across iterations for trail correlation)")]
    public Input<string> PanelId { get; set; } = default!;

    [Input(Description = "Iteration number (1-based)", DefaultValue = 1)]
    public Input<int> Iteration { get; set; } = new(1);

    [Output(Description = "Ordered per-member results (JSON: PanelMemberResult[])")]
    public Output<string> MemberResultsJson { get; set; } = default!;

    // ctor: ILogger, IManagedAgentResolver (32-5), IAgentRegistry (32-2),
    //       ITenantContextAccessor, ICredentialResolver (32-3), IContentSanitizer, IBudgetGuard
}
```

Execution: parse definition → for each member, **gate** (visibility + SaaS provider class + budget) → resolve `IManagedAgent` → run concurrently (`Task.WhenAll`, except `lead+critics` which sequences lead before critics) → order results by member index → emit a per-member action-trail entry (32-6) tagged `panelId`+`memberPosition`+`iteration` → set `MemberResultsJson`. Outcome `Gated` if quorum lost, `Failed` on hard error, else `Done`.

### `AggregatePanelActivity` (Elsa activity signature)

```csharp
[Activity("Tamma.AggregatePanel", "Aggregate Panel",
    "Combine per-member results under a selectable strategy", Kind = ActivityKind.Task)]
[FlowNode("Aggregated")]
public class AggregatePanelActivity : Activity
{
    [Input] public Input<string> MemberResultsJson { get; set; } = default!;
    [Input] public Input<string> PanelDefinitionJson { get; set; } = default!;
    [Input] public Input<string> PanelId { get; set; } = default!;
    [Input(DefaultValue = 1)] public Input<int> Iteration { get; set; } = new(1);

    [Output(Description = "Aggregate result (JSON: PanelAggregateResult)")]
    public Output<string> AggregateJson { get; set; } = default!;

    // ctor: ILogger, PanelStrategyFactory, IContentSanitizer, IEventEmitter
}
```

Execution: parse member results + definition → resolve strategy default (design ⇒ `lead+critics`, review ⇒ `consensus`) → `factory.Get(strategy).Aggregate(def, members)` → sanitize judge rationale → emit `AGENT.PANEL.AGGREGATED` with strategy + winner/tally/rationale + token totals → set `AggregateJson`.

### DCB events

Event-type pattern is `AGGREGATE.ACTION.STATUS` (CLAUDE.md). Events are composed via the existing `TammaEvent` model and flushed through the same path the activities already use (`TammaEventEmitter` → `tamma:events` transient property; per the action-trail story 32-6 these are durably appended to the **tenant** event store).

| Event | When | Tags |
|---|---|---|
| `AGENT.PANEL.AGGREGATED` | aggregation completes | `panelId`, `strategy`, `winnerVerdict`, `iteration`, `tenantId`, `mode`, `memberCount` |
| `AGENT.PANEL.MEMBER_GATED` | a member is dropped (visibility / SaaS provider class / budget) | `panelId`, `agentId`, `role`, `reason`, `tenantId` |
| `AGENT.ITERATION.COMPLETED` | each design→review loop iteration (AC5) | `panelId`, `iteration`, `gatesPassed` |

Per-member `AGENT.TASK.*` action-trail entries are emitted by the managed-agent layer (32-5) / action trail (32-6); this story adds the `panelId` + `memberPosition` tags to them.

### Wiring into existing workflows (refactor)

- **`TriagePanelReviewWorkflow`**: replace the four `RoleTriageDispatch` + `ExtractTriageReview` pairs and the `Aggregate` `SetVariable` with one `RunAgentPanelActivity` (members = security/developer/devops/tester, strategy `consensus`) → `AggregatePanelActivity` → map `AggregateJson` into the unchanged `panelResultJson` output shape (`{ reviews[], reviewCount }`) via a thin adapter `SetVariable`. `DefinitionId` `triage-panel-review` and inputs (`repository`, `itemJson`, `contextJson`) / output (`panelResultJson`) stay identical.
- **`PlanReviewWorkflow`**: replace the Phase-1 7-role review fan and the Phase-2 rebuttal fan with `RunAgentPanelActivity`; the early-termination `allRebuttalApproved` check becomes the `consensus` strategy verdict; Phase-3 PO decision stays as-is (it is a single agent, not a panel). `StoreRoleFindingActivity` per-role persistence is preserved by emitting the per-member trail entries. `DefinitionId` `plan-review` + all inputs/outputs (`decision`, `planJson`, `reviewNotes`, `discussionLog`, ...) unchanged.
- Both workflows auto-register via the existing assembly scan (`elsa.AddWorkflowsFrom<LlmCallWorkflow>()` in `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs:118`); no registration edits needed for the new `PanelStepWorkflow` beyond it living in the same assembly.

### Role→action helpers

`RolePhaseMap` already exposes `GetReviewActionForRole` and `GetTriageActionForRole`. A small new `GetDesignActionForRole` helper (architect ⇒ `plan-system-design`, etc.) is added to `apps/tamma-elsa/src/Tamma.Core/Agents/RolePhaseMap.cs` for the design-step panel (the design actions `PlanSystemDesign`/`DesignApiContract`/… already exist in `AgentAction.cs`).

## Dependencies

**Internal:**

- **Story 32-1** (Agent entity model & versioned saved config) — panel members reference first-class `agentId`s; member results tag `agentId` + config version. *(Story dir `docs/stories/epic-32/story-32-1/` is a placeholder at drafting time.)*
- **Story 32-2** (Agent registry, resolution & RBAC API) — `IAgentRegistry` resolves a member's `agentId` (or the tenant's default agent for a role) and enforces public/private visibility.
- **Story 32-5** (Managed agent execution layer, `IManagedAgent` over `ILLMProvider`) — the execution seam each member runs through; `IManagedAgentResolver` returns an `IManagedAgent`. **Hard prerequisite.**
- **Story 32-6** (Agent action trail, DCB events tagged `agent_id`) — per-member trail entries; this story adds `panelId` + `memberPosition` tags. **Hard prerequisite.**
- **Story 32-3** (Per-tenant provider credential resolution, BYOK → platform) — member credential/budget resolution at execution (AC9).
- **Story 32-4** (SaaS provider auth gating — API-key only) — the SaaS member-gating rule in AC8.
- **Epic 9** (unified agent API) — the agent-resolution contract the panel primitives consume to treat LLM-backed and CLI-backed agents identically (SaaS exposes LLM-API path only).

**External / platform:**

- Elsa Workflows activity model (`Activity`, `[Activity]`, `[FlowNode]`, `CompleteActivityWithOutcomesAsync`) — existing in `apps/tamma-elsa`.
- No new NuGet packages anticipated.

**Supersedes:** the hardcoded `TriagePanelReviewWorkflow` panel logic (and the per-role fan in `PlanReviewWorkflow`).

## Testing Strategy

1. **Strategy unit tests** (`tests/Tamma.Activities.Tests/AgentDispatch/Panels/`): one fixture file per strategy.
   - `single` — N=1 passthrough; N>1 returns the lead/first member; verdict + winnerIndex correct.
   - `consensus` — majority over `{approve, approve, concerns}` ⇒ approve; weighted vote where a high-weight member flips the result; **tie-break** (`{approve:1.0, reject:1.0}` ⇒ earliest position wins); tally recorded.
   - `lead+critics` — lead proposal + critic annotations ⇒ revised lead output is the aggregate (revision member's output is fed in as a fixture).
   - `llm-judge` — judge fixture selects member 2 with a rationale; rationale is **sanitized** (HTML/zero-width stripped) before it lands in `PanelAggregateResult.Rationale`.
2. **Parallel execution test**: stub `IManagedAgent`s with staggered delays; assert members run concurrently (wall-clock < sum of delays) and that `PanelMemberResult[]` is ordered by member index regardless of completion order.
3. **Gating tests** (SaaS mode via `ITammaModeProvider`): a CLI/token-backed member ⇒ gated with `AGENT.PANEL.MEMBER_GATED`; a not-visible-to-tenant agent ⇒ gated; quorum-lost ⇒ outcome `Gated`; quorum-retained ⇒ panel proceeds.
4. **Budget-clamp test**: budget exhausted mid-fan ⇒ remaining members skipped, aggregate computed over completed members, recorded in the event.
5. **Refactor parity (golden output)**: drive `TriagePanelReviewWorkflow` and `PlanReviewWorkflow` with mocked member outputs; assert `panelResultJson` and `decision`+`discussionLog` match the captured pre-refactor golden fixtures byte-for-byte.
6. **Tenancy isolation**: member runs and trail entries carry the executing tenant id; no cross-tenant leakage (assert against the per-tenant event store).

Run C# tests in `apps/tamma-elsa` per the repo convention: `sg docker -c "dotnet test ..."` for docker-bound suites (the build itself needs no wrapper).

## Estimated Effort

5-6 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/RunAgentPanelActivity.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/AggregatePanelActivity.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/Panels/PanelStrategy.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/Panels/PanelMember.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/Panels/PanelMemberPosition.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/Panels/PanelDefinition.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/Panels/PanelMemberResult.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/Panels/PanelAggregateResult.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/Panels/IAggregatePanelStrategy.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/Panels/SinglePanelStrategy.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/Panels/ConsensusPanelStrategy.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/Panels/LeadCriticsPanelStrategy.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/Panels/LlmJudgePanelStrategy.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/Panels/PanelStrategyFactory.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/Panels/PanelEventTypes.cs` | Create |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/PanelStepWorkflow.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Core/Agents/RolePhaseMap.cs` | Modify (add `GetDesignActionForRole`) |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TriagePanelReviewWorkflow.cs` | Modify (refactor onto primitives) |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/PlanReviewWorkflow.cs` | Modify (refactor onto primitives) |
| `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/AgentDispatchServiceCollectionExtensions.cs` | Modify or Create (register strategies + factory in DI) |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/AgentDispatch/Panels/SinglePanelStrategyTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/AgentDispatch/Panels/ConsensusPanelStrategyTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/AgentDispatch/Panels/LeadCriticsPanelStrategyTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/AgentDispatch/Panels/LlmJudgePanelStrategyTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/AgentDispatch/RunAgentPanelActivityTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/AgentDispatch/AggregatePanelActivityTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.ElsaServer.Tests/Workflows/TriagePanelReviewParityTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.ElsaServer.Tests/Workflows/PlanReviewParityTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. Searched `.dev/` directory for related spikes, bugs, findings, and decisions
3. Read the verified current panels: `TriagePanelReviewWorkflow.cs` (hardcoded 4-role sequential) and `PlanReviewWorkflow.cs` (7-role debate) — the refactor must preserve their input/output contracts
4. Read the epic design spec `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md` (defaults: design ⇒ `lead+critics`, review ⇒ `consensus`)
5. Confirmed 32-5 (`IManagedAgent`) and 32-6 (action trail) are landed — they are hard prerequisites; if stubbed, build against their interfaces and gate execution behind a feature flag

### Key Design Decisions

- **Aggregation is pure; LLM happens in the fan-out.** `lead+critics` revision and `llm-judge` selection both require an LLM call, but that call is performed by `RunAgentPanelActivity` as a designated member (the lead's revision pass, or the judge member), so `IAggregatePanelStrategy.Aggregate` stays a deterministic, fixture-testable function. This is what makes AC2's "deterministic fixtures" achievable.
- **Two activities, not one.** Splitting fan-out from aggregation mirrors the existing `Dispatch* → Collect*` pattern in `AgentDispatch/` and keeps each unit-testable; it also lets a workflow re-aggregate captured member results under a different strategy without re-running the (expensive) members.
- **Members are agents, not roles.** A member references an `agentId` (32-1) so its run is benchmarkable per-tenant (one definition → many per-tenant datasets, per the spec). A null `agentId` resolves the tenant's default agent for the role — this is the bridge that lets the refactored triage/plan workflows keep their role-only definitions.
- **`tenant → system → error` resolution stays.** Prompt/convention resolution inside each member is unchanged (members run through 32-5 which uses the existing prompt store); panels add no fallback layer (`feedback_resolution_no_empty_fallback`).
- **Gating is soft per member, hard per quorum.** A single ineligible member is dropped (trail note), not fatal; the panel only fails when quorum can't be met — this keeps a public agent that a tenant can't run from breaking the whole step.

### Per-mode ownership (two-scoping-model answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who owns a **panel definition**? | The sole user (private) or it's a shipped/system definition. | Public definitions: platform owner. Private: tenant owner/admin (32-2 RBAC). |
| Which members can a principal run? | All shipped + the user's private agents; CLI/token providers allowed. | Public ∪ tenant-private agents; **LLM-API path only** — CLI/token-backed members gated (AC8). |
| Whose credentials/budget does a member use? | The user's. | The tenant's, BYOK → platform (32-3); cost attributed to the tenant. |
| Where do panel events land? | The user's (only) feed/store. | The executing tenant's event store (tenant-scoped); `AGENT.PANEL.AGGREGATED` tagged `tenantId`. |

### Risks and Mitigations

| Risk | Severity | Mitigation |
|---|---|---|
| Panel multiplies token spend (N members × iterations) | High | Per-tenant budget clamps (AC9) + `maxIterations` cap (AC5); budget-exhaustion aggregates over completed members |
| Refactor changes triage/plan output shape → breaks callers | High | Golden-output parity tests (AC7); preserve `DefinitionId`s and input/output contracts; thin adapter `SetVariable` maps aggregate → legacy shape |
| `lead+critics`/`llm-judge` non-determinism leaks into tests | Medium | Aggregation seam is pure; LLM-bearing steps are fixtures; live behavior covered only by parity tests with mocked members |
| SaaS gating drops too many members → empty panel | Medium | Quorum check; fail-loud with a clear error rather than silently producing an empty verdict |
| 32-5/32-6 not landed at implementation time | Medium | Build against their interfaces; feature-flag panel execution until both are merged |

### Success Metrics

- [ ] Both `TriagePanelReviewWorkflow` and `PlanReviewWorkflow` run on the shared primitives with golden-output parity (zero diff on fixed member outputs).
- [ ] Each of the four strategies has deterministic-fixture unit coverage ≥ 90% branch on the strategy class.
- [ ] A SaaS ineligible member is gated without aborting the panel (quorum retained).

## Logging Requirements

- **INFO**: Panel started (`panelId`, member count, strategy, iteration); panel aggregated (`panelId`, strategy, winnerVerdict, totalTokens); iteration completed (`panelId`, iteration, gatesPassed).
- **DEBUG**: Each member dispatched (`panelId`, index, position, agentId, role); each member result (verdict, tokens, latencyMs); vote tally / judge selection detail.
- **WARN**: Member gated (`panelId`, agentId, reason); budget clamp triggered mid-fan (`panelId`, completedMembers); quorum at risk.
- **ERROR**: Panel failed (no quorum / all members errored); aggregation strategy threw; managed-agent resolution failed for a required member.
- **Structured context**: include `{ panelId, iteration, strategy, memberIndex, agentId, role, tenantId, mode }` where applicable.
- **Credential safety**: NEVER log provider API keys; the judge rationale is **sanitized** before it is logged or stored.

## Related

- Epic design spec: `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md`
- Implementation plan: `docs/superpowers/plans/2026-06-17-32-7-multi-agent-design-review-panels-in-elsa-plan.md`
- Superseded panel: `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TriagePanelReviewWorkflow.cs`
- Refactor target: `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/PlanReviewWorkflow.cs`
- Sibling stories: `docs/stories/epic-32/story-32-1/`, `story-32-2/`, `story-32-5/`, `story-32-6/`

## References

- **MANDATORY PROCESS:** [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
- **Knowledge Base:** [.dev/README.md](../../.dev/README.md)
- Elsa Workflows activity model (`Activity`, `[Activity]`, `[FlowNode]`, `CompleteActivityWithOutcomesAsync`)
- CLAUDE.md — Operating Modes; event-type pattern `AGGREGATE.ACTION.STATUS`

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
