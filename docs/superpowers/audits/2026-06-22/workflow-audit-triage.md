# Workflow Audit — Triage / Issue Workflows (2026-06-22)

Cluster of 7 workflows under `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/`, plus the
activities they compose under `apps/tamma-elsa/src/Tamma.Activities/ADL/`. Owning story:
**Epic 26 / Story 26-1 (Issue Triage Workflow)** — `docs/stories/epic-26/story-26-1/26-1-issue-triage-workflow.md`
(`sprint-status.yaml:327` = `in-progress`). Architecture references: the agent-pivot spec
(`docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md`) and Epic 38 (non-LLM
step mediation).

## Summary
- **IssueTriageWorkflow** — NEEDS-WORK — fan-out loop is sound; event names diverge from story AC, no de-dup/already-triaged guard — 0 P0 / 2 P1 / 1 P2
- **TriageContextGatheringWorkflow** — GOOD — clean `llm-call` mediation, item-type detection; minor no-output-on-empty-LLM polish — 0 P0 / 0 P1 / 2 P2
- **TriageItemCycleWorkflow** — NEEDS-WORK — correct linear pipeline but ZERO error handling (any sub-workflow soft-fail produces a "successful" triage), no item-level start/complete events — 0 P0 / 3 P1 / 1 P2
- **TriagePanelReviewWorkflow** — NEEDS-WORK — 4-role panel is structurally fine but failed/empty role reviews are aggregated as `{}` with no failure signal (soft-fail masking) — 0 P0 / 2 P1 / 1 P2
- **TriagePODecisionWorkflow** — NEEDS-WORK — silently substitutes a default `needs-human` decision when the LLM returns nothing/garbage, with no error event (violates fail-closed) — 0 P0 / 2 P1 / 1 P2
- **TestCaseCreationWorkflow** — GOOD — best-in-cluster: real validate→retry loop with bounded retries, error output path; minor event-emission gap — 0 P0 / 1 P1 / 1 P2
- **UpdateIssueStatusWorkflow** — NEEDS-WORK — thin wrapper over a retrying activity, but the activity swallows the final failure into a silent "success" and never emits a FAILED event — 0 P0 / 2 P1 / 1 P2

**Cluster totals: P0 = 0 · P1 = 12 · P2 = 8**

> **Architecture / pivot alignment — overall GOOD (no rule-1 violations in this cluster).**
> All LLM steps dispatch the centralized `llm-call` workflow with `role`/`action`/`variables`
> (the mediation path the brief says 32-5 preserves) — no engine-held LLM keys. All git-platform
> writes (`ApplyTriageResultActivity`, `UpdateIssueStatusActivity`, `FetchUntriagedItemsActivity`,
> `ReportCycleResultActivity`) POST to `Engine:CallbackUrl` `/api/engine/*`, which are **tamma-api**
> endpoints (`Tamma.Api/Endpoints/EngineEndpoints.cs`) that mediate GitHub via
> `IGitHubEngineCallbackService`. The Elsa engine holds no git/LLM credentials. This already
> satisfies Epic 38 rule-1 for both the LLM and git paths. The remaining findings are about
> error handling, event-trail fidelity, and missing story-required behavior — NOT rule-1.

---

## IssueTriageWorkflow  (`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/IssueTriageWorkflow.cs`)
- **Purpose / owner story:** Fetch untriaged items for a repo and dispatch one singleton
  `triage-item-cycle` per item (fire-and-forget). Top of the triage cluster; owned by Story 26-1.
  Still needed.
- **Health:** NEEDS-WORK
- **Findings:**
  - [P1] Event emission — Story 26-1 AC9 requires `TRIAGE.ISSUE.STARTED` / `TRIAGE.ISSUE.COMPLETED`
    per issue. The only event emitted on this path is `TRIAGE.FETCH.ITEMS` from
    `FetchUntriagedItemsActivity` (`FetchUntriagedItemsActivity.cs:31`); there is no per-item
    `TRIAGE.ISSUE.*` event and the `Report Triage Complete` step emits `CYCLE.RESULT.REPORT`
    (`ReportCycleResultActivity.cs:25`), not a triage-batch-completed event. — `IssueTriageWorkflow.cs:140-146`
    — **Fix:** emit a `TRIAGE.BATCH.STARTED`/`...COMPLETED` (with item count) on this workflow and a
    `TRIAGE.ISSUE.STARTED`/`...COMPLETED` per item in `TriageItemCycleWorkflow`, matching the AC's
    `TRIAGE.ISSUE.*` naming.
  - [P1] Structural / story gap — no "already triaged?" guard. Story 26-1 AC6/AC7 require triage
    results to be cached so unchanged issues are not re-triaged, and re-triage only on significant
    edits. `FetchUntriagedItemsActivity` filters only by *label presence*
    (`FetchUntriagedItemsActivity.cs:36-44,98`); once labels exist an item is silently never
    re-triaged, and there is no edit-delta / `/triage` re-triage trigger anywhere. — `IssueTriageWorkflow.cs:55-67`
    — **Fix:** add a triage-cache lookup (issue number + content hash) before dispatch, and a
    re-triage path keyed on body-change / explicit command, per AC6/AC7.
  - [P2] Naming — workflow doc-comment says triggered by "ADL Orchestrator (NeedsTriage outcome) /
    GitHub webhook (issues.opened) / Manual dispatch" but only `DispatchTriageActivity`
    (`ADL/DispatchTriageActivity.cs:60`) is a confirmed caller; the `issues.opened` webhook trigger
    in the story is not wired. — `IssueTriageWorkflow.cs:29-33` — **Fix:** either wire the webhook
    trigger or update the comment to reflect the actual ADL-only entry point.
- **Depends on:** Story 26-1 (caching/re-triage AC); no pivot dependency.

## TriageContextGatheringWorkflow  (`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TriageContextGatheringWorkflow.cs`)
- **Purpose / owner story:** Gather triage context (code usage, deps, CVE, changelog) by dispatching
  `llm-call` with `role=developer, action=context-scan` and a triage-specific variable bag. Sub-workflow
  of `TriageItemCycleWorkflow`. Still needed.
- **Health:** GOOD
- **Findings:**
  - [P2] Architecture alignment (positive) — correctly routes the LLM through `llm-call`
    (`TriageContextGatheringWorkflow.cs:85-104`) with `role`/`action`/`variables`/`enableTools`; no
    direct provider call. No change needed; recorded for completeness.
  - [P2] Resilience polish — when the dispatched `llm-call` returns no `llmResponse`, `Extract Result`
    silently yields `"{}"` (`TriageContextGatheringWorkflow.cs:141`). That is acceptable for context
    (empty context is degraded, not wrong), but there is no event distinguishing "LLM produced no
    context" from "context was genuinely empty". — `TriageContextGatheringWorkflow.cs:110-143` —
    **Fix:** emit a low-severity `TRIAGE.CONTEXT.EMPTY` (or set a `contextStatus` output) when the
    LLM response is missing, so downstream/audit can see degraded context.
- **Depends on:** none beyond the `llm-call` path (32-5 boundary preserved — do not change).

## TriageItemCycleWorkflow  (`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TriageItemCycleWorkflow.cs`)
- **Purpose / owner story:** Process ONE untriaged item end-to-end: context → 4-role panel → PO
  decision → apply labels/comment. Runs as a singleton so items are triaged sequentially. Owned by
  Story 26-1 (the per-item engine). Still needed.
- **Health:** NEEDS-WORK
- **Findings:**
  - [P1] Error handling — the flowchart is a single linear chain with **no failure branch and no
    fail-closed terminal** (`TriageItemCycleWorkflow.cs:200-210`). Every sub-workflow is dispatched
    `WaitForCompletion=true` but each one soft-fails internally (context → `{}`, panel → `{}` reviews,
    PO → default `needs-human`), so this workflow always reaches `ApplyLabels` and `Finish` reporting
    success even when context gathering and the entire panel failed. — **Fix:** after each
    `Extract*` step add a "did this stage produce a usable result?" `FlowDecision`; on hard failure,
    route to a `ReportCycleResultActivity{ Reason="error" }` terminal instead of applying a triage
    derived from empty inputs.
  - [P1] Event emission — no per-item `TRIAGE.ISSUE.STARTED` / `TRIAGE.ISSUE.COMPLETED` event is
    emitted (the `Init`/`Extract*` steps are bare `SetVariable`s with no `EventType`); the only audit
    events come from the dispatched `ApplyTriageResultActivity` (`TRIAGE.APPLY.RESULT`,
    `ApplyTriageResultActivity.cs:26`). The item-level triage lifecycle required by AC9 is invisible
    in the event store. — `TriageItemCycleWorkflow.cs:52-63,182-183` — **Fix:** wrap Init/Finish with
    `TRIAGE.ISSUE.STARTED`/`...COMPLETED` events carrying the issue number + final decision.
  - [P1] Structural — the dispatched decision (`poDecisionJson`) drives `ApplyLabels`
    (`TriageItemCycleWorkflow.cs:169-177`) but the workflow never inspects the decision's
    `automation`/`priority`; an `automation: "needs-human"` outcome and a `tamma-auto` outcome both
    flow to the same apply-and-finish. Story 26-1 expects `tamma-auto` to enable ADL pickup (AC8).
    — **Fix:** branch on `decisionJson.automation` so a `tamma-auto` decision also signals ADL
    (event/label) rather than treating all automation levels identically.
  - [P2] Naming — variable `subResult` is reused across the context, panel, and PO dispatches
    (`TriageItemCycleWorkflow.cs:47,79,113,147`). Functionally fine (each Extract runs before the
    next dispatch overwrites it) but fragile; rename to per-stage results or add a comment. —
    `TriageItemCycleWorkflow.cs:47`
- **Depends on:** Story 26-1; no pivot dependency.

## TriagePanelReviewWorkflow  (`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TriagePanelReviewWorkflow.cs`)
- **Purpose / owner story:** 4-role LLM panel (security / developer / devops / tester) assesses a
  triage item; per-role `llm-call` with role-specific triage action via
  `RolePhaseMap.GetTriageActionForRole`. Aggregates into `panelResultJson`. Owned by Story 26-1
  (cross-role panel, Story 27-19 role/action taxonomy). Still needed.
- **Health:** NEEDS-WORK
- **Findings:**
  - [P1] Error handling / fail-closed — each role's `Extract*Review` defaults to `"{}"` when its
    `llm-call` returns no `llmResponse` (`TriagePanelReviewWorkflow.cs:299`), and `Aggregate` records
    those `{}` reviews as if the role participated (`TriagePanelReviewWorkflow.cs:153-158`). A panel
    where 0–3 roles failed produces an indistinguishable `reviews[]` with `reviewCount=4`. This masks
    LLM failures from the PO decision and the audit trail. — **Fix:** track per-role success and emit
    a `TRIAGE.PANEL.PARTIAL`/`...FAILED` event (and include a `failedRoles`/`succeededCount` field in
    `panelResultJson`) so a degraded panel is visible.
  - [P1] Event emission — the workflow emits no `TRIAGE.PANEL.*` lifecycle event; the only events are
    the nested `llm-call` events. There is no single audit record that "the triage panel ran and these
    N roles responded". — `TriagePanelReviewWorkflow.cs:123-180` — **Fix:** emit
    `TRIAGE.PANEL.STARTED`/`...COMPLETED` with role count and success count.
  - [P2] Architecture alignment (positive) — role/action pairs come from the typed
    `GetTriageActionForRole` map (`RolePhaseMap.cs:395-403`), every `(role,action)` is eligibility-
    checked, and all four calls go via `llm-call`. The `ReviewRoles` string array
    (`TriagePanelReviewWorkflow.cs:41-47`) duplicates the role list also encoded in the four explicit
    dispatch calls — minor DRY/drift risk. — **Fix (optional):** derive the dispatch list from one
    source.
- **Depends on:** none beyond `llm-call` (32-5 boundary preserved).

## TriagePODecisionWorkflow  (`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TriagePODecisionWorkflow.cs`)
- **Purpose / owner story:** Product-Owner final triage decision (priority/type/complexity/automation/
  labels/comment) via `llm-call` `role=product_owner, action=triage-intake`, parsing the LLM JSON into
  a normalized decision. Owned by Story 26-1. Still needed.
- **Health:** NEEDS-WORK
- **Findings:**
  - [P1] Error handling / fail-closed — `Extract Decision` substitutes a hard-coded default decision
    (`priority=normal, type=feature, complexity=medium, automation=needs-human, comment="No PO
    decision received."`) when the LLM returns nothing or unparseable output
    (`TriagePODecisionWorkflow.cs:155-176`). This is a *silent false success*: the workflow finishes
    with a fabricated decision that then gets applied as real labels/comment on the issue. Per the
    brief's no-empty/plain-fallback rule, resolution should be tenant→system→error, not a synthesized
    default. — **Fix:** when no valid LLM JSON is produced, emit `TRIAGE.PO_DECISION.FAILED` and route
    the item to a non-applying terminal (or `needs-human` with an explicit "triage failed" comment),
    rather than presenting the default as a confident decision. At minimum, flag the decision with a
    `decisionConfidence: "fallback"` marker so `ApplyTriageResultActivity` can avoid auto-labeling.
  - [P1] Event emission — no `TRIAGE.PO_DECISION.*` event; the decision (the single most important
    triage output) is not independently recorded in the audit trail except as an `llm-call` event and
    the downstream `TRIAGE.APPLY.RESULT`. — `TriagePODecisionWorkflow.cs:79-179` — **Fix:** emit
    `TRIAGE.PO_DECISION.COMPLETED` carrying the parsed decision.
  - [P2] Architecture alignment (positive) — `enableTools=false` is the correct choice for a pure
    decision step; LLM goes via `llm-call`. No change. — `TriagePODecisionWorkflow.cs:79-97`
- **Depends on:** none beyond `llm-call` (32-5 boundary preserved).

## TestCaseCreationWorkflow  (`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TestCaseCreationWorkflow.cs`)
- **Purpose / owner story:** Generate test cases from task plans for the TDD red phase via `llm-call`
  `role=tester, action=write-tests`, validating output is a non-empty test array/object, retrying up
  to `maxRetries` (default 2) with validation errors fed back into the prompt. Consumed by
  `SingleIssueCycleWorkflow.cs:418` (issue/cycle cluster, not strictly triage). Still needed.
- **Health:** GOOD
- **Findings:**
  - [P1] Error handling (positive, with one gap) — this is the cluster's best error model: real
    validate→`Tests Valid?`→retry/give-up loop with a bounded counter and a distinct
    `SetErrorOutputs` path that sets `testCasesJson="[]"` AND an `error` output
    (`TestCaseCreationWorkflow.cs:198-218,245-250`). Gap: on give-up it reaches `Finish` with empty
    output but emits **no FAILED event** — the failure is only visible to the immediate caller via the
    `error` output, not in the audit trail. — **Fix:** emit `TEST.GENERATION.FAILED` on the give-up
    branch before `Finish`.
  - [P2] Architecture alignment (positive) — LLM via `llm-call`; retry loop is the workflow's own
    validation retry and does NOT duplicate `LlmCallWorkflow`'s provider-chain/circuit-breaker
    boundary (correct per the brief). — `TestCaseCreationWorkflow.cs:81-101`
- **Depends on:** none beyond `llm-call` (32-5 boundary preserved).

## UpdateIssueStatusWorkflow  (`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/UpdateIssueStatusWorkflow.cs`)
- **Purpose / owner story:** Thin fire-and-forget wrapper that posts a status comment (and optional
  label add/remove) on a GitHub issue via `UpdateIssueStatusActivity`. Used as a "living log" step by
  `SingleIssueCycleWorkflow.cs:887`. Still needed.
- **Health:** NEEDS-WORK
- **Findings:**
  - [P1] Error handling / fail-closed — `UpdateIssueStatusActivity` retries 3× with backoff
    (`UpdateIssueStatusActivity.cs:78-118`) but on final failure logs a warning and **returns
    normally** (`:120-124`). Because the `TammaAsyncActivity` base emits `.COMPLETED` only when
    `RunAsync` returns without throwing (`TammaActivity.cs:185-193`), a failed comment post is
    recorded as `CYCLE.ISSUE.UPDATE.COMPLETED` (success) in the audit trail. — **Fix:** on terminal
    failure, throw (so the base emits `CYCLE.ISSUE.UPDATE.FAILED`) for non-fire-and-forget callers, or
    explicitly emit a `CYCLE.ISSUE.UPDATE.FAILED` event before returning so the audit trail reflects
    the failed post even on the fire-and-forget path.
  - [P1] Per-mode correctness — label add (`/api/engine/issue-labels`) is followed by per-label
    DELETE calls that are NOT inside the retry loop's success guard and have no `EnsureSuccessStatusCode`
    (`UpdateIssueStatusActivity.cs:94-108`); a failed label add or remove is fully silent (the only
    `EnsureSuccessStatusCode` is on the comment, `:91`). — **Fix:** check the label add/remove
    responses and surface failures (event or throw) consistent with the comment path.
  - [P2] Naming — `EventType` is `CYCLE.ISSUE.UPDATE` (`UpdateIssueStatusActivity.cs:26`) which reads
    as a cycle event, but this workflow is also a general-purpose issue-comment utility; consider
    `ISSUE.STATUS.UPDATE` per the `AGGREGATE.ACTION.STATUS` convention. — `UpdateIssueStatusActivity.cs:26`
- **Depends on:** none; no pivot dependency.

---

## Cross-cutting observations (patterns shared across this cluster)

1. **No rule-1 violations.** Every LLM step in the cluster dispatches the centralized `llm-call`
   workflow (the mediation path 32-5 preserves), and every git-platform write goes through
   `Engine:CallbackUrl` → tamma-api `EngineEndpoints` → `IGitHubEngineCallbackService`. The Elsa
   engine holds no LLM or git credentials, so Epic 38's "steps never call vendors directly" rule is
   already met for this cluster. The remaining work is error-handling, audit-trail, and story-feature
   gaps — not architectural cutover.

2. **Pervasive silent-failure / soft-fail masking (the dominant P1 theme).** The triage pipeline is
   built so that *every* failure degrades to an empty/default value that flows downstream as if it
   succeeded: context → `"{}"`, each panel role → `"{}"`, PO decision → a fabricated `needs-human`
   default, and the issue-status post → a logged warning with a `.COMPLETED` event. The net effect is
   that a fully-failed triage (no context, no panel, no real PO decision) still applies labels and a
   comment to the issue and reports success. This is exactly the "NEVER swallow errors into a false
   success / NEVER empty fallback" rule the brief calls out. Recommended cluster-wide fix: add a
   per-stage "usable result?" decision in `TriageItemCycleWorkflow` that routes hard failures to a
   `ReportCycleResultActivity{ Reason="error" }` terminal, and have the leaf workflows/activities emit
   `*.FAILED` events instead of returning defaults.

3. **Activity-level try/catch defeats the base class's FAILED-event contract.** `TammaAsyncActivity`
   (`TammaActivity.cs:179-194`) only emits `.FAILED` when `RunAsync` throws; but
   `FetchUntriagedItemsActivity` (`:115,153,190`), `ApplyTriageResultActivity` (`:112-115`),
   `ReportCycleResultActivity` (`:79-82`) and `UpdateIssueStatusActivity` (`:120-124`) all catch and
   swallow, so they always emit `.COMPLETED`. Any HTTP failure to tamma-api (e.g. `502 BadGateway` /
   `503` from `EngineEndpoints.ToHttpResult` when the GitHub client is unwired) is invisible. Fix
   pattern: let the base handle failure (rethrow) or emit an explicit `.FAILED` event in the catch.

4. **Event names diverge from the owning story (Story 26-1 AC9).** The story specifies
   `TRIAGE.ISSUE.STARTED/COMPLETED`; the implementation emits `TRIAGE.FETCH.ITEMS`,
   `TRIAGE.APPLY.RESULT`, `ADL.TRIAGE.DISPATCH` and `CYCLE.RESULT.REPORT`, with no per-item or
   per-stage `TRIAGE.*` lifecycle events (this is also the exact gap recorded at `sprint-status.yaml:327`).
   A consistent `TRIAGE.{BATCH,ISSUE,CONTEXT,PANEL,PO_DECISION}.{STARTED,COMPLETED,FAILED}` event family
   would both satisfy the AC and make the soft-fails in observation #2 visible.

5. **Story 26-1 feature gaps remain (keeps it `in-progress`).** No triage caching / "already triaged?"
   skip, no re-triage-on-edit / `/triage` command trigger, no milestone or project-board assignment,
   and the `tamma-auto` automation outcome is not specially handled to hand the issue to ADL. These
   are story-completion items, not pivot items.

6. **Tenant/persona scoping is server-side and acceptable.** No triage workflow passes a `tenantId`,
   persona, or provider — tenant resolution and persona/per-tenant-enablement (pivot rules 4/6) happen
   inside `llm-call` / the tamma-api endpoints via `ITenantContext`. This is the correct layering;
   no per-workflow change is required, but the cluster correctly relies on those downstream paths
   being tenant-aware.
