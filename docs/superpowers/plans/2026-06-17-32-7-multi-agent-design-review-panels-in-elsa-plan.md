# Story 32-7 — Multi-Agent Design/Review Panels in Elsa (strategy-driven)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes failing
> tests before implementation. Run C# tests in `apps/tamma-elsa` as `sg docker -c "dotnet test ..."`
> for docker-bound suites (the build itself needs no wrapper).

**Goal:** Generalize the two hardcoded panels (`TriagePanelReviewWorkflow`, `PlanReviewWorkflow`)
into reusable, strategy-driven Elsa primitives — `RunAgentPanelActivity` (fan a step out to N
agents of a role, each via `IManagedAgent`) and `AggregatePanelActivity` (combine member outputs
under a selectable strategy: `single`, `consensus`, `lead+critics`, `llm-judge`). Panels back both
**design** steps (default `lead+critics`) and **review** steps (default `consensus`, incl.
specialized security/performance/visual reviewers), loop on `iterationCount` until gates pass or
max, emit per-member action-trail entries (32-6) tagged `panelId`+`memberPosition` plus a single
`AGENT.PANEL.AGGREGATED` event, and respect public/private visibility + SaaS provider gating +
per-tenant credentials/budgets for every member.

**Story file:** `docs/stories/epic-32/story-32-7/32-7-multi-agent-design-review-panels-in-elsa.md`
**Design spec:** `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md`
(§"Multi-agent design/review steps")

**Tech stack:** .NET 9 / Elsa Workflows in `apps/tamma-elsa` (C#). Activities live in
`Tamma.Activities`, workflows in `Tamma.ElsaServer`, taxonomy in `Tamma.Core`. Tests in
`apps/tamma-elsa/tests/Tamma.Activities.Tests/` and `tests/Tamma.ElsaServer.Tests/` (xUnit).

---

## Non-goals (YAGNI guard)

- **NO new aggregation semantics beyond the four named strategies.** `single`/`consensus`/
  `lead+critics`/`llm-judge` only. A/B experiment cohorts are Story 32-14.
- **NO changes to the `llm-call` workflow or `CallLlmActivity`.** Members run through the 32-5
  managed-agent layer; the panel never talks HTTP to a provider directly.
- **NO change to prompt/convention resolution.** `tenant → system → error` stays exactly as-is
  (`feedback_resolution_no_empty_fallback`). Panels add no fallback layer.
- **NO behavior change to the triage/plan workflow contracts.** `DefinitionId`s
  (`triage-panel-review`, `plan-review`) and their inputs/outputs are frozen — callers
  (`TriageItemCycleWorkflow`, etc.) must not need edits. Parity is proven by golden-output tests.
- **NO per-member personalization layer.** Members reference agents (32-1); credentials resolve at
  execution (32-3). No new credential storage here.
- **NO leaderboard/benchmark projection.** This story *produces* per-member trail + aggregate
  events; consumption is 32-8/32-10.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

> `packages/api` is **deleted** — all server + engine code is the C# `apps/tamma-elsa` stack.

### The two panels being generalized

| File | Shape today |
|---|---|
| `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TriagePanelReviewWorkflow.cs` | Hardcoded **4-role sequential** panel (`security → developer → devops → tester`). Each role: a `DispatchWorkflow{ WorkflowDefinitionId = "llm-call" }` with `role`=`AgentRole.X.ToWire()`, `action`=`RolePhaseMap.GetTriageActionForRole(role).ToWire()`, `enableTools=true`, `WaitForCompletion=true`, `Result → llmResult`. Then a `SetVariable` (`ExtractTriageReview`) pulls JSON out of `llmResult["llmResponse"]`. Final `Aggregate` `SetVariable` concatenates the four into `{ reviews[], reviewCount }` → output `panelResultJson`. **No strategy; no agent identity.** `DefinitionId = "triage-panel-review"`; inputs `repository,itemJson,contextJson`; output `panelResultJson`. |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/PlanReviewWorkflow.cs` | **7-role sequential debate**: Phase 1 independent review (`RoleReviewDispatch` + `ExtractReview` + `StoreRoleFindingActivity`, per role), Phase 2 anonymized rebuttal (`RebuttalDispatch` + `ExtractRebuttal` + store), early-termination consensus check via `allRebuttalApproved`, Phase 3 PO decision (single agent), loop to `maxRounds` (default 3), `forceNeedsHuman` on max. `DefinitionId = "plan-review"`; outputs `decision,planJson,reviewNotes,deferred,split,discussionLog,suggestionsJson`. |

Both call the `llm-call` workflow whose output key is **`llmResponse`** (string)
(`LlmCallWorkflow.cs:599-607`). Both auto-register via `elsa.AddWorkflowsFrom<LlmCallWorkflow>()`
(`apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs:118`) — assembly scan, no per-workflow add.
`TriagePanelReviewWorkflow` is dispatched by `TriageItemCycleWorkflow.cs`.

### Activity + event seams to reuse

- **Existing dispatch home:** `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/` already hosts
  `DispatchAgentWorkflowActivity`, `CollectAgentResultsActivity`, `AgentDispatchService`,
  `AgentExecutorFactory`, etc. (these are the **CLI/process** dispatch path — GitHub Actions /
  local executors). The new panel activities are **NEW** in the same folder; the existing
  CLI-dispatch classes are unrelated and untouched.
- **Activity pattern:** `CallLlmActivity.cs` is the reference — `[Activity("Tamma.X", ...)]` +
  `[FlowNode("Success","Retryable","Fatal")]`, `Input<T>`/`Output<T>` props, `[JsonConstructor]`
  + DI ctor, `context.CompleteActivityWithOutcomesAsync(...)`. Outcome-bearing activities derive
  from `Activity` (not `CodeActivity`); see `TammaOutcomeActivity` in
  `Tamma.Activities/Core/TammaActivity.cs`.
- **Event emission:** `TammaEventEmitter.Emit(context, source, logger, new TammaEvent{ EventType,
  Status, Data })` writes to `WorkflowExecutionContext.TransientProperties["tamma:events"]`. Per
  story 32-6 the action trail durably appends these to the **tenant** event store; this story
  composes `AGENT.PANEL.AGGREGATED` / `AGENT.PANEL.MEMBER_GATED` / `AGENT.ITERATION.COMPLETED` the
  same way and adds `panelId`+`memberPosition` tags to per-member entries.
- **Taxonomy:** `Tamma.Core/Agents/AgentRole.cs` (enum, `.ToWire()`), `AgentAction.cs` (wire
  actions incl. design actions `plan-system-design`/`design-api-contract`/…), `RolePhaseMap.cs`
  (`GetReviewActionForRole`, `GetTriageActionForRole`, `EligibleActions`). **No
  `GetDesignActionForRole` yet** → add one (architect ⇒ `PlanSystemDesign`, etc.).

### Dependency status

- Sibling story dirs `docs/stories/epic-32/story-32-1..32-14/` exist but are **empty placeholders**
  at drafting time — 32-1/32-2/32-5/32-6 are referenced by contract, not by landed code.
  `IManagedAgent` / action-trail types do **not** yet exist in the tree
  (`grep IManagedAgent` → none). Build against their interfaces; feature-flag panel execution
  until 32-5 + 32-6 merge.

---

## Architecture

**Two activities + a pure strategy seam, wired into a composable step workflow, then retrofitted
onto the two legacy panels.**

```
PanelStepWorkflow (design or review step)
  └─ loop iterationCount ≤ maxIterations:
       RunAgentPanelActivity   ── gate each member (visibility / SaaS provider class / budget)
         │                        resolve IManagedAgent (32-5), run (parallel where safe),
         │                        emit per-member trail entry (panelId+memberPosition)
         ▼  MemberResultsJson
       AggregatePanelActivity  ── PanelStrategyFactory → IAggregatePanelStrategy.Aggregate(...)
         │                        (pure; LLM-bearing revision/judge already ran as a member)
         │                        sanitize judge rationale; emit AGENT.PANEL.AGGREGATED
         ▼  AggregateJson
       gates pass? → done : iterationCount++ → loop  (max → escalate)
```

- **Pure aggregation, LLM in the fan-out:** `lead+critics` revision and `llm-judge` selection need
  an LLM call — performed by `RunAgentPanelActivity` as a designated member (lead-revision member /
  judge member), so `IAggregatePanelStrategy.Aggregate` is a deterministic, fixture-testable
  function. This is the keystone that makes the AC2 "deterministic fixtures" requirement real.
- **Member = agent, not role:** a member carries an `agentId` (32-1); a null `agentId` resolves the
  tenant's default agent for the role — the bridge that lets the refactored role-only triage/plan
  definitions keep working.
- **Soft member gate, hard quorum:** one ineligible member is dropped with an
  `AGENT.PANEL.MEMBER_GATED` note; the panel only fails when quorum is unmet.

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Concern | single-user | SaaS |
|---|---|---|
| Panel definition ownership | sole user (private) or shipped/system | public: platform owner; private: tenant owner/admin (32-2) |
| Runnable members | shipped ∪ user-private; CLI/token allowed | public ∪ tenant-private; **LLM-API only** — CLI/token members gated |
| Member credentials/budget | the user's | the tenant's, BYOK → platform (32-3); cost attributed to tenant |
| Panel events | the user's feed/store | executing tenant's store; `AGENT.PANEL.AGGREGATED` tagged `tenantId` |

---

## Task breakdown

### T1: Panel domain types + pure strategy seam (no Elsa, no LLM)

**Scope:** All record/enum types and the four `IAggregatePanelStrategy` implementations + factory.
Pure C#, fully unit-testable with fixtures. **This is the only hard prerequisite for the rest.**

**Files (new):** under `Tamma.Activities/AgentDispatch/Panels/` — `PanelStrategy.cs`,
`PanelMemberPosition.cs`, `PanelMember.cs`, `PanelDefinition.cs`, `PanelMemberResult.cs`,
`PanelAggregateResult.cs`, `IAggregatePanelStrategy.cs`, `SinglePanelStrategy.cs`,
`ConsensusPanelStrategy.cs`, `LeadCriticsPanelStrategy.cs`, `LlmJudgePanelStrategy.cs`,
`PanelStrategyFactory.cs`, `PanelEventTypes.cs`.

**Tests first** (`tests/Tamma.Activities.Tests/AgentDispatch/Panels/`):
- `single` — N=1 passthrough; N>1 returns lead/first; `WinnerIndex` correct.
- `consensus` — majority (`{approve,approve,concerns}`→approve); weighted flip; **tie-break**
  (`{approve:1.0,reject:1.0}` → earliest position); `Tally` recorded.
- `lead+critics` — lead proposal + critics' annotations + lead-revision-member output ⇒ revised
  output is the aggregate.
- `llm-judge` — judge fixture selects member 2 + rationale; rationale **sanitized** (HTML/zero-width
  stripped) into `PanelAggregateResult.Rationale`.
- factory maps each `PanelStrategy` → its impl; unknown ⇒ throws.

**Acceptance:**
- [ ] Each strategy unit-tested with deterministic fixtures; ≥90% branch on each strategy class.
- [ ] Tie-break + weighted-vote behavior pinned by tests.
- [ ] No Elsa / no `IManagedAgent` dependency in this layer (compiles standalone).

### T2: `RunAgentPanelActivity` (fan-out + gating + per-member trail)

**Scope:** Elsa activity that parses a `PanelDefinition`, gates + resolves + runs each member via
`IManagedAgent` (parallel where the strategy allows; `lead+critics` sequences lead→critics),
orders results by member index, emits per-member trail entries, sets `MemberResultsJson`.

**Files (new):** `Tamma.Activities/AgentDispatch/RunAgentPanelActivity.cs`.
**DI ctor:** `ILogger`, `IManagedAgentResolver` (32-5), `IAgentRegistry` (32-2),
`ITenantContextAccessor`, `ICredentialResolver` (32-3), `IContentSanitizer`, `IBudgetGuard`.
Outcomes `[FlowNode("Done","Gated","Failed")]`.

**Gating rule (AC8):** in SaaS (`ITammaModeProvider`), drop members backed by
`ICLIAgentProvider`/token providers and members whose agent isn't visible to the tenant; record
`AGENT.PANEL.MEMBER_GATED`; proceed if quorum remains else `Gated`. Budget exhaustion mid-fan ⇒
skip remaining, aggregate over completed (AC9).

**Tests first** (`tests/.../AgentDispatch/RunAgentPanelActivityTests.cs`): stub `IManagedAgent`s with
staggered delays → assert concurrency (wall-clock < sum) + deterministic index ordering; SaaS gating
(CLI-backed / not-visible → gated, with quorum behaviors); budget clamp early-stop; per-member trail
entry carries `panelId`+`memberPosition`+`agentId`+`iteration`+`tenantId`.

**Acceptance:**
- [ ] Members run concurrently except `lead+critics`; results ordered by position regardless of
      completion order.
- [ ] Ineligible member gated without aborting the panel (quorum retained); quorum-lost → `Gated`.
- [ ] Each member emits its own action-trail entry tagged `panelId`+`memberPosition`.

### T3: `AggregatePanelActivity` (strategy default + aggregate event)

**Scope:** Elsa activity wrapping T1: parse member results + definition, resolve strategy default
(design ⇒ `lead+critics`, review ⇒ `consensus`), aggregate, sanitize judge rationale, emit
`AGENT.PANEL.AGGREGATED`, set `AggregateJson`.

**Files (new):** `Tamma.Activities/AgentDispatch/AggregatePanelActivity.cs`. DI ctor: `ILogger`,
`PanelStrategyFactory`, `IContentSanitizer`. Outcome `[FlowNode("Aggregated")]`.

**Tests first** (`tests/.../AgentDispatch/AggregatePanelActivityTests.cs`): default-strategy
resolution (omitted strategy → design/review defaults); `AGENT.PANEL.AGGREGATED` carries strategy +
winner/tally + token totals; judge rationale sanitized before emit.

**Acceptance:**
- [ ] Omitted strategy resolves to the spec default per step kind.
- [ ] Exactly one `AGENT.PANEL.AGGREGATED` per aggregation with winner/consensus metadata.

### T4: `PanelStepWorkflow` + `GetDesignActionForRole` + iteration loop (AC3/AC4/AC5)

**Scope:** A composable design/review step over the primitives, with the iteration loop until gates
pass / max, emitting `AGENT.ITERATION.COMPLETED`. Add `GetDesignActionForRole` to `RolePhaseMap`.

**Files:** new `Tamma.ElsaServer/Workflows/PanelStepWorkflow.cs`; modify
`Tamma.Core/Agents/RolePhaseMap.cs` (add `GetDesignActionForRole`); modify or create
`Tamma.Activities/AgentDispatch/AgentDispatchServiceCollectionExtensions.cs` (register strategies +
factory in DI). Auto-registers via the existing assembly scan — no `Program.cs` workflow-add edit.

**Tests first:** `RolePhaseMap` design-action mapping; a design-step run aggregates under
`lead+critics`; a review-step run with security/performance/visual reviewers aggregates under
`consensus`; loop stops on gate pass and on `maxIterations` (escalation), emitting one
`AGENT.ITERATION.COMPLETED` per iteration.

**Acceptance:**
- [ ] Design step defaults `lead+critics`; review step defaults `consensus`.
- [ ] Loop terminates on gates-pass or max; escalates on max-without-pass.

### T5: Refactor `TriagePanelReviewWorkflow` onto the primitives (parity)

**Scope:** Replace the four `RoleTriageDispatch`+`ExtractTriageReview` pairs and the `Aggregate`
`SetVariable` with `RunAgentPanelActivity` (members security/developer/devops/tester, strategy
`consensus`) → `AggregatePanelActivity` → a thin adapter `SetVariable` mapping `AggregateJson` into
the unchanged `{ reviews[], reviewCount }` `panelResultJson`. Keep `DefinitionId =
"triage-panel-review"` + inputs/output. Remove the now-dead per-role helpers.

**Tests first:** `tests/Tamma.ElsaServer.Tests/Workflows/TriagePanelReviewParityTests.cs` — capture
pre-refactor golden `panelResultJson` for fixed mocked member outputs; assert the refactored
workflow reproduces it byte-for-byte.

**Acceptance:**
- [ ] Golden-output parity (zero diff) on fixed member outputs.
- [ ] `DefinitionId` + inputs (`repository,itemJson,contextJson`) + output (`panelResultJson`)
      unchanged; `TriageItemCycleWorkflow` needs no edit.

### T6: Refactor `PlanReviewWorkflow` onto the primitives (parity)

**Scope:** Replace the Phase-1 review fan and Phase-2 rebuttal fan with `RunAgentPanelActivity`; the
`allRebuttalApproved` early-termination becomes the `consensus` strategy verdict; Phase-3 PO
decision stays a single agent; preserve per-role persistence via the per-member trail entries; keep
the `maxRounds`/`forceNeedsHuman` loop. `DefinitionId = "plan-review"` + all inputs/outputs frozen.

**Tests first:** `tests/Tamma.ElsaServer.Tests/Workflows/PlanReviewParityTests.cs` — golden parity on
`decision` + `discussionLog` for fixed mocked member outputs, including the early-termination path
and a `maxRounds` exhaustion path.

**Acceptance:**
- [ ] Golden-output parity on `decision`+`discussionLog` incl. early-termination + max-rounds paths.
- [ ] Outputs (`decision,planJson,reviewNotes,deferred,split,discussionLog,suggestionsJson`)
      unchanged.

### T7: Tenancy + budget integration + suite green

**Scope:** Confirm member runs + trail entries carry the executing tenant id (no cross-tenant
leakage), budget-clamp path is exercised end-to-end, and the full `apps/tamma-elsa` suite is green.

**Tests:** tenancy-isolation assertion against the per-tenant event store; budget-exhaustion panel
aggregates over completed members and records it in the event. Run docker-bound suites via
`sg docker -c "dotnet test ..."`.

**Acceptance:**
- [ ] Panel events/trail entries tenant-scoped; isolation test passes.
- [ ] Full suite green; no regressions in triage/plan or AgentDispatch CLI tests.

---

## Task order & dependencies

T1 → T2 → T3 → T4 → (T5 ∥ T6) → T7.
T1 is the only hard prerequisite for everything. T2 needs 32-5 (`IManagedAgent`) + 32-6 (trail)
interfaces; if those aren't landed, build against the interfaces and feature-flag execution. T5/T6
are parallel-safe (independent workflows). T3 needs T1; T4 needs T2+T3.

## Risks

- **Token-spend blowup:** N members × iterations multiplies cost — primary mitigation is per-tenant
  budget clamps (T2/AC9) + `maxIterations` cap (T4/AC5); budget exhaustion aggregates over completed
  members rather than failing.
- **Refactor regresses caller contracts:** the golden-output parity tests (T5/T6) are load-bearing —
  preserve `DefinitionId`s and input/output shapes; map the aggregate back into the legacy JSON via a
  thin adapter rather than changing the output schema.
- **Strategy non-determinism in tests:** keep `IAggregatePanelStrategy.Aggregate` pure; the only
  LLM-bearing steps (`lead+critics` revision, `llm-judge`) run as members and are fixtures in unit
  tests — live behavior is covered solely by parity tests with mocked members.
- **32-5/32-6 not yet in tree:** `IManagedAgent` and the action-trail types don't exist at drafting
  time (verified). Build against their interfaces and gate panel execution behind a feature flag
  until both merge; T1/T5/T6 (pure types + workflow wiring) can proceed regardless.
- **SaaS over-gating → empty panel:** quorum check + fail-loud (no silent empty verdict).
- **Event-store topology (Story 28-1 / Epic 30):** `AGENT.PANEL.*` and per-member trail events are
  tenant-scoped by design — route them through the tenant event store the action trail (32-6) owns,
  so a later per-tenant fan-out migration touches only routing, not this story's emission sites.
