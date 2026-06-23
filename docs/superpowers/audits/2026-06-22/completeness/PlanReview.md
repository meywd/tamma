# Completeness Audit — PlanReviewWorkflow

**Date:** 2026-06-22
**Workflow file:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/PlanReviewWorkflow.cs`
**DefinitionId:** `plan-review`
**Owning epic/story:** Epic 32 (First-class Agents & Benchmarking) — **Story 32-7 "Multi-Agent Design/Review Panels in Elsa (strategy-driven)"** (`docs/stories/epic-32/story-32-7/32-7-multi-agent-design-review-panels-in-elsa.md`). Design of record: `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md` (§ "Multi-agent design/review steps"). Pivot context: `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md`.

---

## Purpose & owner

Structured multi-agent **plan review** step of the autonomous loop: after `plan-generation` produces a plan, this workflow runs a 7-role debate (architect, developer, tester, security, devops, product_owner, senior_developer) and produces a `decision` (`approved` / `needsModification` / `needsHuman`) plus the (possibly modified) plan, review notes, a discussion log, deferred/split lists, and suggestions. Consumed by `SingleIssueCycleWorkflow` (`ReviewPlan` DispatchWorkflow, `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs:189`).

## Maturity

**PARTIAL** (leaning to "partial, healthy").

This is emphatically **not** a thin/stub workflow. It is one of the more fully-realized workflows in the repo: a real three-phase debate with anonymized rebuttals, an early-consensus shortcut, a bounded round loop with human escalation, per-role persistence, and a discussion-log audit artifact. It correctly honors the mediation rule — every LLM touch is a `DispatchWorkflow("llm-call")`, never a direct provider call. The gaps that keep it from "complete" are (a) a real **caller-contract mismatch** (defer/split), (b) **no first-class DCB lifecycle events** for the review itself, (c) a **stale-review loop bug**, (d) **tenant-scoping not threaded into the PO step**, and (e) it predates the **32-7 panel-primitive pivot** that is the canonical target architecture (strategy-driven panels, per-member benchmarkable agents, `AGENT.PANEL.AGGREGATED` / `AGENT.ITERATION.COMPLETED`, SaaS provider gating, budget clamps).

## Current capabilities

- **Init** — reads inputs (`repository`, `issueNumber`, `planJson`, `contextIds`, `workItemJson`, `maxRetries`→`maxRounds` default 3), sets `roundCount=1`, `phase="review"`.
- **Phase 1 — Independent Review (7 sequential roles).** Each role: `DispatchWorkflow("llm-call")` with `role`+`action` (via `RolePhaseMap.GetReviewActionForRole`) + plan/context/workItem/`previousReviews`; `enableTools=true`. After each call, an `ExtractReview` `SetVariable` pulls the JSON object out of `llmResult["llmResponse"]` (first `{`…last `}`), and a `StoreRoleFindingActivity` persists the role's review to the vector DB (emits `CONTEXT.STORE_ROLE.*`).
- **Aggregate Phase 1** — parses each role verdict via `ReviewAggregationHelper.ParseRoleVerdict` into `allReviewsJson` `[{role,verdict,comments,suggestedChanges}]` and appends `phase1-review` entries to `discussionLog`.
- **Build Anonymized** — strips role labels, replaces with `reviewerIndex` for the rebuttal round.
- **Phase 2 — Rebuttal Round (7 sequential roles).** Each role sees ALL anonymized reviews + its own Phase-1 review + `roundNumber`; outputs `{responses, revisedVerdict}`. Persisted per role.
- **Aggregate Rebuttals + Early Termination** — if every role's `revisedVerdict == "approve"`, `allRebuttalApproved=true` → `SetApprovedEarly` → outputs (unanimous-consensus shortcut). Appends `phase2-rebuttal` entries to `discussionLog`.
- **Phase 3 — PO Decision.** `product_owner` (action `ReviewScope`) sees all reviews + rebuttals; `ExtractPODecision` parses `decision` / `suggestions` / `modifiedPlan` / `notes` / `deferred` / `split`. On `needsModification` with a `modifiedPlan`, updates `planJson`. Persists a `po-decision-round-N` finding.
- **Routing** — `approved` → outputs; `needsHuman` → outputs; `needsModification` → `IncrementRound` → `CanContinue` (`round <= maxRounds`): loop back to `BuildAnonymized` (re-runs Phase 2), else `ForceNeedsHuman`.
- **Outputs** — `decision`, `planJson`, `reviewNotes`, `deferred`, `split`, `discussionLog`, `suggestionsJson`.
- **Mediation-correct**: all model calls route through `llm-call` (prompt resolution, provider chain, circuit breaker, budget live there). No direct provider/SDK use in the workflow.

## Intended full scope (with citations)

1. **Strategy-driven, benchmarkable panels — the canonical target.** Story 32-7 explicitly lists `PlanReviewWorkflow` as a **refactor target**: replace the hardcoded Phase-1/Phase-2 per-role `DispatchWorkflow` fans with `RunAgentPanelActivity` + `AggregatePanelActivity`, where the early-termination `allRebuttalApproved` check becomes the **`consensus`** strategy verdict, every panel member is a first-class **Agent** (32-1/32-2) run through the managed-agent layer (`IManagedAgent`, 32-5), and the `DefinitionId`/input-output contract stays unchanged (32-7 AC7, "Wiring into existing workflows"). The design spec pins **`consensus` as the review-step default** and calls for **specialized security / performance / visual reviewers** (`2026-06-17-agent-entities-benchmarking-design.md` §59-62).
2. **First-class DCB audit events.** Per CLAUDE.md ("Every operation must emit events for audit trail", pattern `AGGREGATE.ACTION.STATUS`) and 32-7 AC6: a single **`AGENT.PANEL.AGGREGATED`** event per aggregation (strategy, winner/tally, member count, token/cost basis), **`AGENT.ITERATION.COMPLETED`** per design→review loop (32-7 AC5; design spec §63 "every iteration emits events"), per-member **action-trail** entries tagged `panelId`+`memberPosition`+`agentId`+`iteration` (32-7 AC6, 32-6). A plan-review step should also carry its own lifecycle (`PLAN.REVIEW.STARTED/COMPLETED/ESCALATED`).
3. **Outcome & bug taxonomy.** Story 32-8 ("Outcome capture & bug taxonomy at review/gate") + design spec §67 expect review findings classified (`REVIEW.BUG.RECORDED`, `bugType: visual|functional|regression|security|perf|style`) and fed to the learning loop / leaderboards. The current review captures free-text `comments`/`suggestedChanges` only.
4. **Tenant-scoped execution + per-tenant credentials & budget clamps.** 32-7 AC9 + design spec §96 ("panels multiply token spend — budget clamps + max-iteration caps required"): members resolve credentials BYOK→platform (32-3), cost attributed to the executing tenant, panel stops adding members on budget breach and aggregates over what completed.
5. **SaaS provider gating per member.** 32-7 AC8: in SaaS, CLI/token-backed members are gated (API-key-only path); a member not visible to the tenant is excluded; gating is soft (drop + `AGENT.PANEL.MEMBER_GATED` note) and fails loud only if quorum is lost.
6. **Caller-contract completeness.** `SingleIssueCycleWorkflow.ReviewOutcome` switches on `decision == "defer"` and `decision == "split"` (`SingleIssueCycleWorkflow.cs:233-234`) and reads `subResult["deferred"]` / `subResult["split"]` to create issues. The review must actually be able to **emit `defer` / `split` as a `decision`**, not only populate the arrays under a `needsModification` PO output.
7. **Robust LLM-output handling, no false-success.** Per `feedback_resolution_no_empty_fallback` (tenant→system→error, never empty/plain) and "no silent-failure / false-success": malformed/empty LLM output should not be silently coerced to a benign verdict; failures should be observable and the run should escalate rather than emit a fabricated "approve". Current `ExtractReview` falls back to a `concerns` verdict, and `ExtractRebuttal` to `revisedVerdict:"concerns"` — pessimistic (acceptable) but currently **invisible** (no event, no flag).

## Missing capabilities

| # | Capability | Priority | dependsOn |
|---|------------|----------|-----------|
| 1 | **Emit `defer`/`split` as real `decision` values** so the caller's defer/split routing isn't dead code. Today PO can only return `approved`/`needsModification`/`needsHuman`; `deferred`/`split` arrays are populated but `decision` never becomes `defer`/`split`, so `SingleIssueCycleWorkflow` defer/split branches are unreachable. | P0 | none (contract fix); aligns with 32-7 refactor |
| 2 | **First-class DCB lifecycle events for the review itself** — `PLAN.REVIEW.STARTED` / `.COMPLETED` (with final decision, round count, consensus state) / `.ESCALATED` (force-needs-human). The `SetVariable`/`FlowDecision` nodes emit nothing; only `StoreRoleFindingActivity` and the dispatched `llm-call` emit events, so the review's own decision lifecycle has **no audit event**. | P0 | none |
| 3 | **`AGENT.PANEL.AGGREGATED` event** per phase aggregation (strategy, verdict tally, member count, token/cost basis). | P1 | 32-7 (panel primitives) |
| 4 | **`AGENT.ITERATION.COMPLETED` event** per round loop (panelId, iteration, gatesPassed) — consumed by 32-8/32-10. | P1 | 32-7 |
| 5 | **Stale-review loop fix.** On `needsModification`, the workflow loops back to `BuildAnonymized` (Phase 2 only). Phase-1 independent reviews are **never re-run against the modified plan**, so rebuttals/PO in round 2+ debate against a plan the reviewers never saw, and `roleRebuttalVariables` are not reset between rounds (last round's rebuttals leak into the early-termination check). Loop should re-run Phase 1 on the modified plan (or explicitly re-review the diff). | P1 | none |
| 6 | **Per-member benchmarkable agents** — members are anonymous role strings; nothing is attributable to a first-class `agentId`/config-version, so no leaderboard/learning input (32-1/32-2/32-6). | P1 | 32-7, 32-1, 32-2, 32-6 |
| 7 | **Refactor onto `RunAgentPanelActivity`+`AggregatePanelActivity` with `consensus` strategy** — replaces the 14 hand-rolled per-role dispatch+extract+store triples; required convergence point for the pivot. | P1 | 32-7, 32-5 |
| 8 | **Tenant scoping into Phase 3 PO call.** Phase-1/Phase-2 `llm-call` dispatches do **not** pass `tenantId`, and the PO dispatch (`Phase3PODecision`) also omits it — yet the caller passes `tenantId` into `plan-review`. Tenant context must thread to every member + the PO step for per-tenant prompts/creds/budget/event-store. | P1 | 32-3, 32-7 |
| 9 | **Per-tenant budget clamps + max-iteration cost guard.** A 7-member × N-round debate multiplies token spend with no in-workflow budget stop; `maxRounds` caps rounds but not spend. | P1 | 32-7 (AC9), 32-3 |
| 10 | **SaaS provider gating per member** (API-key-only; drop CLI/token-backed or not-visible members with `AGENT.PANEL.MEMBER_GATED`, fail loud only on lost quorum). | P1 | 32-7 (AC8), 32-4, 32-2 |
| 11 | **Classified findings / bug taxonomy** — emit `REVIEW.BUG.RECORDED` with `bugType` per finding instead of free-text comments only; feeds learning loop. | P2 | 32-8 |
| 12 | **Specialized reviewers** (security / performance / visual) as distinct panel members beyond the single `security`/role set, per design spec §62. | P2 | 32-7 |
| 13 | **Observable malformed-output handling** — when an LLM response is unparseable, currently silently coerced to `concerns` (review) / `concerns` rebuttal with no event. Should emit a diagnostic event (e.g. `PLAN.REVIEW.PARSE_DEGRADED`) so a degraded run is visible, never a fabricated verdict. | P2 | none |
| 14 | **Parallel member execution.** Phase 1 and Phase 2 run all 7 roles strictly sequentially (independent reviews have no inter-dependency). 32-7 specifies parallel fan-out where the strategy permits — large latency win. | P2 | 32-7 |
| 15 | **`phase` variable is dead.** `phase` is set in Init and never read/branched on; either wire it into routing/events or remove. | P3 | none |
| 16 | **Golden-output parity test** capturing current decision+discussionLog for fixed mocked member outputs, to protect the refactor (32-7 AC7/Testing §5). | P2 | 32-7 |

## Build-out spec (ordered)

The ordering does the **contract + audit correctness fixes first** (deliverable without the 32-7 primitives), then converges on the panel-primitive refactor.

1. **Fix the defer/split decision contract (P0).** Extend `ExtractPODecision` to recognize `decision ∈ {approved, needsModification, needsHuman, defer, split}` from the PO output, and update the PO `ReviewScope` prompt (in the prompt store, not the workflow) to allow returning `defer`/`split` with the corresponding `deferred`/`split` arrays. Add `defer`/`split` to the early-consensus and routing logic so the caller's `ReviewOutcome` defer/split branches become reachable. Add a parity assertion that `decision` and the `deferred`/`split` outputs are mutually consistent.
2. **Add the review's own DCB lifecycle events (P0).** Introduce a small `TammaAsyncActivity` (or reuse an emit helper) to emit `PLAN.REVIEW.STARTED` at Init (tags: `issueId`, `repository`, `tenantId`, `mode`, `maxRounds`), `PLAN.REVIEW.COMPLETED` at `SetOutputs` (tags: `decision`, `roundCount`, `consensus=early|po`, token/cost basis), and `PLAN.REVIEW.ESCALATED` on `ForceNeedsHuman` (tags: `maxRounds`, last verdicts). These must flush through the existing `tamma:events` transient-property path to the tenant event store.
3. **Fix the stale-review loop + reset state (P1).** Change `ConnectOutcome(canContinue, "True", …)` to re-enter **Phase 1** on the modified plan (re-run the 7 independent reviews against `planJson` updated by the PO), and reset all `roleRebuttalVariables` (+ `allRebuttalApproved`) at the start of each round so a prior round's rebuttals can't satisfy the early-termination check. Emit `AGENT.ITERATION.COMPLETED` (`panelId`, `iteration=roundCount`, `gatesPassed`) at the end of each round.
4. **Thread tenant scope everywhere (P1).** Read `tenantId` in Init and add `["tenantId"] = tenantId.Get(ctx)` to every Phase-1, Phase-2, and the Phase-3 PO `llm-call` dispatch input dictionary, so prompt resolution (tenant→system→error), credentials (BYOK→platform), budget, and event store are all tenant-correct.
5. **Make malformed output observable (P2).** In `ExtractReview`/`ExtractRebuttal`/`ExtractPODecision`, when JSON parse fails, emit a `PLAN.REVIEW.PARSE_DEGRADED` event (role, phase, round) before applying the pessimistic `concerns` default — never silently produce a benign verdict; keep the pessimistic default (it correctly avoids false "approve").
6. **Refactor Phase 1 & Phase 2 onto `RunAgentPanelActivity` + `AggregatePanelActivity` (P1, 32-7).** Replace the 14 hand-rolled `DispatchWorkflow`+`ExtractReview/Rebuttal`+`StoreRoleFindingActivity` triples with: one `RunAgentPanelActivity` (members = the 7 roles, default agent per role, strategy `consensus`, `panelId` stable across rounds, `iteration=roundCount`) → `AggregatePanelActivity` (consensus). The `allRebuttalApproved` early-termination becomes the consensus aggregate verdict (`approve`). Keep Phase-3 PO as a single-agent dispatch (it is not a panel). Preserve `DefinitionId` `plan-review` and all input/output names. `AggregatePanelActivity` emits `AGENT.PANEL.AGGREGATED`; per-member runs emit action-trail entries (32-6) tagged `panelId`+`memberPosition`+`agentId`+`iteration`.
7. **Add per-tenant budget clamp + SaaS member gating (P1, 32-7 AC8/AC9).** Inherited from `RunAgentPanelActivity`: gate ineligible members in SaaS (CLI/token-backed or not-visible) with `AGENT.PANEL.MEMBER_GATED`, proceed on quorum, fail loud on lost quorum; stop adding members on tenant budget breach and aggregate over completed members (recorded in the aggregate event).
8. **Classify findings (P2, 32-8).** Have `AggregatePanelActivity`/a follow-on step classify member findings into `bugType` and emit `REVIEW.BUG.RECORDED` per finding, feeding the learning loop and leaderboards.
9. **Specialized + parallel reviewers (P2).** Add specialized `security`/`performance`/`visual` reviewer members per design spec §62; run independent members in parallel (`Task.WhenAll`) inside `RunAgentPanelActivity`, with deterministic ordering by member index.
10. **Tests + golden parity (P2).** Capture current `decision`+`discussionLog` for fixed mocked member outputs as a golden fixture before the refactor; assert byte-equivalence after (32-7 AC7). Add unit coverage for the new defer/split decision parsing and the loop-reset behavior.
11. **Cleanup (P3).** Remove or wire the dead `phase` variable.

---

### Notes on project-rule conformance (current state)

- **Mediation: PASS.** All model calls go through `DispatchWorkflow("llm-call")`; no direct provider/SDK calls. The 32-5 mediation seam is respected today.
- **tenant→system→error / no empty fallback: PARTIAL.** Prompt resolution lives in `llm-call` (correct), but `tenantId` is not threaded into the dispatches here, so tenant-scoped resolution can't apply; and malformed-output coercion, while pessimistic (good), is invisible (no event).
- **No false-success: MOSTLY PASS.** Malformed reviews default to `concerns` (not `approve`), and max-rounds escalates to `needsHuman` — both fail safe. The gap is observability (no event) + the unreachable defer/split decisions.
- **DCB audit: GAP.** No `PLAN.REVIEW.*`, no `AGENT.PANEL.AGGREGATED`, no `AGENT.ITERATION.COMPLETED`; only `CONTEXT.STORE_ROLE.*` (per-role persistence) and whatever `llm-call` emits.
