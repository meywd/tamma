# Completeness Audit — TaskReviewWorkflow

**Date:** 2026-06-22
**Workflow file:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TaskReviewWorkflow.cs`
**DefinitionId:** `task-review`
**Owning epic/story:** Epic 32 (First-class Agents & Benchmarking) — **Story 32-7 "Multi-Agent Design/Review Panels in Elsa (strategy-driven)"** (`docs/stories/epic-32/story-32-7/32-7-multi-agent-design-review-panels-in-elsa.md`). Design of record: `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md` (§ "Multi-agent design/review steps"). Pivot context: `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md`.

---

## Purpose & owner

The **task-review** step of the autonomous loop: after `task-creation` decomposes a plan into implementation tasks, a **4-role panel** (architect, senior_developer, developer, tester) reviews the `tasksJson` against the `planJson` and emits a `decision` (`approved` / `needsChanges`) plus the (pass-through) `tasksJson` and free-text `reviewNotes`. It is the task-level analogue of `plan-review` (which reviews the plan), positioned **before** branch creation / TDD so bad task breakdowns are caught cheaply.

Consumed by `SingleIssueCycleWorkflow` (`ReviewTasks` DispatchWorkflow → `task-review`, `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs:307-348`). The caller switches on `approved` / `needsChanges` / **`needsHuman`** (the `NeedsHuman` switch case is a `ctx => true` catch-all at line 345), and on `needsChanges` increments a task-revision counter and loops.

## Maturity

**THIN** (happy-path skeleton — the user's complaint applies).

Structurally it is the simplest of the three sibling review workflows. It is a strictly-linear 4-role fan with no persistence, no audit events, no round/iteration loop, no escalation path, and a real caller-contract mismatch. Compare to its siblings on disk:

| | TaskReview (this) | PlanReview | TriagePanelReview |
|---|---|---|---|
| Roles | 4 sequential | 7 sequential | 4 sequential |
| Rounds / loop | **none** (single pass) | bounded `maxRounds` loop w/ rebuttal phase | n/a (single) |
| Per-role persistence | **none** | `StoreRoleFindingActivity` per role | (per 32-7 target) |
| `needsHuman` escalation | **never emitted** (only approved/needsChanges) | `forceNeedsHuman` + PO `needsHuman` | n/a |
| Outputs | decision, tasksJson, reviewNotes | decision, planJson, reviewNotes, deferred, split, discussionLog, suggestions | panelResultJson |
| Discussion log / audit artifact | **none** | `discussionLog` | none |
| DCB lifecycle events | **none** | none (also a gap there) | none |

The mediation rule is honored — every model touch is a `DispatchWorkflow("llm-call")`, never a direct provider call — so the 32-5 seam is respected. But this workflow is **not** built out to the same level as its plan-level sibling, and it predates the 32-7 panel-primitive pivot that is the canonical target architecture.

## Current capabilities

- **Init** — reads `repository`, `issueNumber`, `tasksJson` (default `"[]"`), `planJson` from workflow input. **Drops** `conventions` and `tenantId` (both are passed by the caller — `SingleIssueCycleWorkflow.cs:318-319` — but never read here).
- **4 sequential role reviews.** For each role (architect, senior_developer, developer, tester): a `RoleReviewDispatch` → `DispatchWorkflow("llm-call")` with `role` + `action` (via `RolePhaseMap.GetReviewActionForRole`: architect/sr-dev → `PlanReview`, developer → `ReviewFeasibility`, tester → `ReviewTestability`), `variables = {tasksJson, planJson, previousReviews}`, `enableTools=true`. After each call, an `ExtractReview` `SetVariable` pulls the JSON object out of `llmResult["llmResponse"]` (first `{`…last `}`); on parse failure it wraps the raw text as a `{verdict:"concerns", comments:<raw>, suggestedChanges:""}` object.
- **Aggregate Verdicts** — parses each role's `{verdict, comments, suggestedChanges}`, sets `AllApproved = (every role verdict == "approve")`, builds `allReviewsJson` `[{role,verdict,comments,suggestedChanges}]`, and joins non-approving roles' comments into `reviewNotes`.
- **Branch on `AllApproved`** — `True` → `SetApproved` (`decision="approved"`, notes = "All 4 reviewers approved the tasks."); `False` → `SetNeedsChanges` (`decision="needsChanges"`).
- **Outputs** — `decision`, `tasksJson` (unchanged pass-through), `reviewNotes`.
- **Mediation-correct:** all model calls route through `llm-call` (prompt resolution, provider chain, circuit breaker, budget live there). No direct provider/SDK use in the workflow.

## Intended full scope (with citations)

1. **Caller contract — must be able to emit `needsHuman`.** `SingleIssueCycleWorkflow` has a `NeedsHuman` switch case (`SingleIssueCycleWorkflow.cs:345`, catch-all `ctx => true`) routed to `notifyNeedsHuman` + `reportNeedsHuman` (lines 822-823), but `task-review` **only ever returns `approved` or `needsChanges`** — so the human-escalation branch is reachable only by the caller's own fallback when `subResult` is null/missing `decision`, never as a deliberate verdict. A complete review must escalate to `needsHuman` on (a) unparseable/empty member output and (b) repeated/irreconcilable disagreement, matching `PlanReviewWorkflow.forceNeedsHuman`.
2. **Caller passes `conventions` + `tenantId` that are dropped.** `SingleIssueCycleWorkflow.cs:318-319` passes `conventions` and `tenantId` into `task-review`; `Init` reads neither. Conventions are the project's review yardstick (CLAUDE.md "Convention Templates" → `{{conventions}}` injected into every prompt) and `tenantId` is required for tenant→system→error prompt resolution, BYOK→platform credentials, per-tenant budget, and the tenant event store (CLAUDE.md "Universal rule for any tenant-aware feature"). Both must thread into every member `llm-call`.
3. **Strategy-driven, benchmarkable panel — the canonical target (32-7).** Story 32-7 extracts the hardcoded per-role fan into `RunAgentPanelActivity` + `AggregatePanelActivity` and refactors the existing review workflows onto them; the **review-step default strategy is `consensus`** (32-7 AC3, design spec §61 "Defaults: `lead+critics` (design), `consensus` (review)"). TaskReview's "all 4 must approve" is exactly an unweighted `consensus`/unanimity check and is a natural refactor candidate. 32-7's "Wiring into existing workflows" lists `TriagePanelReviewWorkflow` + `PlanReviewWorkflow` explicitly but **omits `TaskReviewWorkflow`** — this audit flags TaskReview as a third hardcoded review panel that the same refactor must cover (32-7 should name it). Every member becomes a first-class **Agent** (32-1/32-2) executed through the managed-agent layer (`IManagedAgent`, 32-5).
4. **First-class DCB audit events.** Per CLAUDE.md ("Every operation must emit events for audit trail", pattern `AGGREGATE.ACTION.STATUS`) and the design spec §67 event families: the review's own lifecycle (`TASK.REVIEW.STARTED` / `.COMPLETED` / `.ESCALATED`), one **`AGENT.PANEL.AGGREGATED`** per aggregation (strategy, verdict tally, member count, token/cost basis — 32-7 AC6), **`AGENT.ITERATION.COMPLETED`** if a revision loop is added (32-7 AC5), and per-member action-trail entries tagged `panelId`+`memberPosition`+`agentId`+`iteration` (32-6, 32-7 AC6). Today the workflow's `SetVariable`/`FlowDecision` nodes emit **nothing** — only the dispatched `llm-call` emits anything.
5. **Per-role persistence.** PlanReview persists each role's review via `StoreRoleFindingActivity` (`apps/tamma-elsa/src/Tamma.Activities/Context/StoreRoleFindingActivity.cs`, emits `CONTEXT.STORE_ROLE.*`) so partial results survive a later failure and feed RAG/learning. TaskReview persists nothing — a mid-panel crash loses all completed reviews.
6. **Outcome & bug taxonomy.** Story 32-8 + design spec §67 expect review findings classified (`REVIEW.BUG.RECORDED`, `bugType: visual|functional|regression|security|perf|style`) and fed to the learning loop / leaderboards. The current review captures free-text `comments`/`suggestedChanges` only.
7. **Tenant-scoped credentials, budget clamps, SaaS gating per member.** 32-7 AC8/AC9 + design spec §96 ("panels multiply token spend — budget clamps + max-iteration caps required"): members resolve credentials BYOK→platform (32-3), cost is attributed to the executing tenant, panel stops on budget breach and aggregates over completed members, and in SaaS CLI/token-backed or not-visible members are gated (`AGENT.PANEL.MEMBER_GATED`), failing loud only on lost quorum (32-4).
8. **No false-success on malformed output.** Per `feedback_resolution_no_empty_fallback` (tenant→system→error, never empty/plain) and "no silent-failure / false-success": the current `ExtractReview` coerces unparseable output to a `concerns` verdict — pessimistic (good, it won't fabricate an "approve"), but **invisible** (no event, no flag), and there is no path that turns persistent un-parseability into a visible `needsHuman` escalation.
9. **Revision loop.** A complete task-review should optionally re-review revised tasks within a bounded round count (mirroring `plan-review`'s `maxRounds`) rather than returning to the caller every time and relying on the caller's revision counter alone — or, at minimum, accept a `roundNumber` input and emit it, so the caller's loop is observable. (Lower priority — the caller does own the revision loop today.)

## Missing capabilities

| # | Capability | Priority | dependsOn |
|---|------------|----------|-----------|
| 1 | **Thread `tenantId` into every member `llm-call`.** Caller passes `tenantId`; `Init` drops it; no dispatch forwards it. Without it, prompt resolution can't apply tenant→system→error, credentials can't resolve BYOK→platform, budget/event-store aren't tenant-correct. | P0 | 32-3 |
| 2 | **Thread `conventions` into every member `llm-call`.** Caller passes `conventions` (the review yardstick); `Init` drops it. Reviewers currently judge tasks with no project conventions injected. | P0 | none |
| 3 | **Be able to emit `needsHuman`.** Today only `approved`/`needsChanges` are emitted; the caller's `NeedsHuman` branch is only reached via its own null-fallback, never as a deliberate verdict. Escalate to `needsHuman` on unparseable/empty member output and on irreconcilable disagreement (mirrors `forceNeedsHuman`). | P0 | none |
| 4 | **First-class DCB lifecycle events** — `TASK.REVIEW.STARTED` (tags: issueId, repository, tenantId, mode), `.COMPLETED` (decision, approvedCount/total, token/cost basis), `.ESCALATED` (on needsHuman). The workflow's own decision lifecycle currently has **no audit event**. | P0 | none |
| 5 | **Per-role persistence via `StoreRoleFindingActivity`** so partial results survive a later failure and feed RAG/learning (parity with PlanReview). | P1 | none |
| 6 | **Observable malformed-output handling** — emit a `TASK.REVIEW.PARSE_DEGRADED` event (role, raw) before applying the pessimistic `concerns` default; never silently coerce to a benign verdict. | P1 | none |
| 7 | **`AGENT.PANEL.AGGREGATED` event** per aggregation (strategy=`consensus`, verdict tally, member count, token/cost basis). | P1 | 32-7 |
| 8 | **Refactor onto `RunAgentPanelActivity` + `AggregatePanelActivity` with `consensus` strategy** — replaces the 4 hand-rolled dispatch+extract triples + the `Aggregate` `SetVariable`. (32-7 currently omits TaskReview from its named refactor targets; it should include it.) | P1 | 32-7, 32-5 |
| 9 | **Per-member benchmarkable agents** — members are anonymous role strings; nothing is attributable to a first-class `agentId`/config-version, so no leaderboard/learning input. | P1 | 32-7, 32-1, 32-2, 32-6 |
| 10 | **Per-tenant budget clamp + SaaS member gating** (API-key-only; drop CLI/token-backed or not-visible members with `AGENT.PANEL.MEMBER_GATED`; fail loud on lost quorum; stop on budget breach). | P1 | 32-7 (AC8/AC9), 32-3, 32-4 |
| 11 | **Classified findings / bug taxonomy** — emit `REVIEW.BUG.RECORDED` with `bugType` per finding instead of free-text comments only. | P2 | 32-8 |
| 12 | **Parallel member execution.** The 4 independent reviews run strictly sequentially with no inter-dependency — `Task.WhenAll` (via `RunAgentPanelActivity`) is a latency win. | P2 | 32-7 |
| 13 | **`previousReviews` is always empty.** Each member is passed `["previousReviews"] = allReviewsJson.Get(ctx)`, but `allReviewsJson` is only populated in `Aggregate` (after all members run), so every member sees `"[]"`. Either populate it progressively (sequential members can see prior reviews) or drop the parameter — currently dead input. | P2 | none |
| 14 | **Bounded in-workflow revision loop** accepting/echoing a `roundNumber`, mirroring `plan-review`'s `maxRounds`, instead of relying solely on the caller's revision counter. | P3 | none |
| 15 | **Golden-output parity test** capturing current `decision`+`allReviewsJson` for fixed mocked member outputs, to protect the 32-7 refactor (32-7 AC7/Testing §5). | P2 | 32-7 |

## Build-out spec (ordered)

The ordering delivers the **contract + tenant + audit correctness fixes first** (all shippable without the 32-7 primitives), then converges on the panel-primitive refactor.

1. **Thread `tenantId` + `conventions` through Init and every member dispatch (P0, #1/#2).** In `Init`, read `tenantId` and `conventions` from input into new workflow variables. In `RoleReviewDispatch`, add `["tenantId"] = tenantId.Get(ctx)` to the dispatch input and `["conventions"] = conventions.Get(ctx)` into the `variables` map, so each member's `llm-call` resolves prompts (tenant→system→error), credentials (BYOK→platform), budget, and event store correctly and judges against project conventions.
2. **Add the review's own DCB lifecycle events (P0, #4).** Introduce/reuse a small emit activity (e.g. a `TammaAsyncActivity` with `EventType`) to emit `TASK.REVIEW.STARTED` right after `Init` (tags: `issueId`, `repository`, `tenantId`, `mode`), `TASK.REVIEW.COMPLETED` in `SetOutputs` (tags: `decision`, `approvedCount`/`total`, token/cost basis), and `TASK.REVIEW.ESCALATED` on the new needsHuman path. Flush through the existing `tamma:events` transient-property path to the tenant event store.
3. **Add a real `needsHuman` escalation path (P0, #3).** Track per-role parse success in `Aggregate`. If any member's output was unparseable (degraded) OR the panel is irreconcilably split, route a third branch to a `SetNeedsHuman` (`decision="needsHuman"`, notes explaining why) instead of forcing `needsChanges`. Wire it so the caller's existing `NeedsHuman` switch case (`SingleIssueCycleWorkflow.cs:345`) becomes a deliberately-reachable verdict, not just a null-fallback. Emit `TASK.REVIEW.ESCALATED` here.
4. **Persist each role's review (P1, #5).** After each `ExtractReview`, insert a `StoreRoleFindingActivity` (`Repository`, `IssueNumber`, `Role`, `FindingsJson = <role review>`), mirroring PlanReview's `StoreReviewRole`, so partial results survive a later crash and feed RAG/learning (emits `CONTEXT.STORE_ROLE.*`).
5. **Make malformed output observable (P1, #6).** In `ExtractReview`, when the JSON parse fails, emit `TASK.REVIEW.PARSE_DEGRADED` (role, raw snippet) before applying the pessimistic `concerns` default — keep the pessimistic default (never fabricate "approve"), but make the degradation visible and feed it into the needsHuman decision from step 3.
6. **Fix/retire `previousReviews` (P2, #13).** Either populate `allReviewsJson` progressively so sequential members can actually see prior reviews, or remove the `previousReviews` parameter from `RoleReviewDispatch` (currently always `"[]"`).
7. **Refactor onto `RunAgentPanelActivity` + `AggregatePanelActivity`, `consensus` strategy (P1, #7/#8/#9, 32-7).** Replace the 4 hand-rolled `RoleReviewDispatch`+`ExtractReview` pairs and the `Aggregate` `SetVariable` with one `RunAgentPanelActivity` (members = architect/senior_developer/developer/tester, default agent per role, strategy `consensus`, `panelId` stable, `iteration` if looping) → `AggregatePanelActivity` (consensus = unanimity → `approved`, else `needsChanges`/`needsHuman`). Preserve `DefinitionId` `task-review` and the input/output contract (`decision`, `tasksJson`, `reviewNotes`). `AggregatePanelActivity` emits `AGENT.PANEL.AGGREGATED`; per-member runs emit action-trail entries tagged `panelId`+`memberPosition`+`agentId`+`iteration`. Also: **update Story 32-7 to name `TaskReviewWorkflow` as a refactor target** alongside `PlanReviewWorkflow`/`TriagePanelReviewWorkflow`.
8. **Per-tenant budget clamp + SaaS member gating (P1, #10, 32-7 AC8/AC9).** Inherited from `RunAgentPanelActivity`: gate ineligible members in SaaS (CLI/token-backed or not-visible) with `AGENT.PANEL.MEMBER_GATED`, proceed on quorum, fail loud on lost quorum; stop adding members on tenant budget breach and aggregate over completed members.
9. **Classify findings (P2, #11, 32-8).** Have `AggregatePanelActivity` / a follow-on step classify member findings into `bugType` and emit `REVIEW.BUG.RECORDED` per finding, feeding the learning loop and leaderboards.
10. **Parallel members (P2, #12).** Run the 4 independent reviews concurrently inside `RunAgentPanelActivity` with deterministic ordering by member index.
11. **Tests + golden parity (P2, #15).** Capture current `decision`+`allReviewsJson` for fixed mocked member outputs as a golden fixture before the refactor; assert equivalence after (32-7 AC7). Add unit coverage for the new tenant/conventions threading, the needsHuman escalation, and the parse-degraded path.

---

### Notes on project-rule conformance (current state)

- **Mediation: PASS.** All model calls go through `DispatchWorkflow("llm-call")`; no direct provider/SDK calls. The 32-5 seam is respected today.
- **tenant→system→error / no empty fallback: FAIL.** `tenantId` and `conventions` are passed by the caller but dropped in `Init`, so tenant-scoped prompt/credential/budget resolution cannot apply and conventions never reach the reviewers.
- **No false-success: PARTIAL.** Malformed member output defaults to `concerns` (not `approve`) — fails safe — but there is **no `needsHuman` escalation** and the degradation is invisible (no event), so a fully-garbled panel still returns a confident `needsChanges`.
- **DCB audit: GAP.** No `TASK.REVIEW.*`, no `AGENT.PANEL.AGGREGATED`, no per-role `StoreRoleFindingActivity` — the only events are whatever the dispatched `llm-call` emits. This workflow's own decision lifecycle is entirely unaudited.
