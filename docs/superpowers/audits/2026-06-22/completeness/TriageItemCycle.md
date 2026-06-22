# Completeness Audit — `TriageItemCycleWorkflow`

**Date:** 2026-06-22
**Workflow:** `triage-item-cycle` (`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TriageItemCycleWorkflow.cs`)
**Maturity:** **partial** — the per-item orchestration spine is fully wired to a *real* multi-workflow subsystem (context → 4-role panel → PO decision → apply), but the orchestrator layer itself is a strictly linear happy-path: **no decision gates, no error edges, no cycle-scoped DCB events, no idempotency**. The robust sub-workflows it composes mask brittle orchestration.

---

## Purpose & Owner

**Purpose:** Process **one** untriaged work item end-to-end — gather triage context, run a 4-role review panel, get a Product-Owner decision, then apply labels + a triage comment (or create an issue for a security alert). Runs as a **singleton** so `IssueTriageWorkflow` can fire-and-forget one dispatch per item and Elsa serializes them.

**Owner:** Epic 26 — Project Management & Triage, **Story 26-1 Issue Triage Workflow** (`docs/stories/epic-26/story-26-1/26-1-issue-triage-workflow.md`; sprint-status: `in-progress` — "labels/comment exist but no tests, no cache/re-triage, events differ from AC"). `triage-item-cycle` is the per-item execution half of 26-1 (the parent fan-out half is audited separately in `IssueTriage.md`). Downstream consumer of the Epic 32 agent-architecture pivot for LLM mediation.

**Invoked by:** `IssueTriageWorkflow.DispatchTriageCycle` (`WaitForCompletion=false`, fire-and-forget) — `IssueTriageWorkflow.cs:104-116`. The singleton declaration is asserted in the header comment but is **not enforced in `Build()`** (no `builder.WithSingleton()` / options flag is set on this definition — see gap #9).

---

## Current Capabilities (what it actually does today)

`TriageItemCycleWorkflow` is a 6-node flowchart that threads JSON between three sub-workflows and one apply activity:

```
Init (read repository + itemJson)
  → DispatchWorkflow("triage-context-gathering")  → ExtractContext   (contextJson)
  → DispatchWorkflow("triage-panel-review")        → ExtractPanelResult (panelResultJson)
  → DispatchWorkflow("triage-po-decision")         → ExtractDecision  (poDecisionJson)
  → ApplyTriageResultActivity (labels + comment / create-issue)
  → Finish
```

The **sub-workflows it composes are genuinely well-built** (this is the orchestrator's strength, not the orchestrator's own merit):

- **`TriageContextGatheringWorkflow`** — dispatches `llm-call` (role=`developer`, action=`context-scan`, `scanFocus=triage`, `enableTools=true`); detects item type (issue / security / dependency); robust JSON-block extraction with raw-text wrap fallback; `SetOutput("contextJson")`.
- **`TriagePanelReviewWorkflow`** — 4 sequential `llm-call` dispatches (security / developer / devops / tester) using per-role triage actions from `RolePhaseMap.GetTriageActionForRole`; aggregates into `panelResultJson` with `reviewCount`.
- **`TriagePODecisionWorkflow`** — dispatches `llm-call` (role=`product_owner`, action=`triage-intake`); parses `priority/type/complexity/automation/labels/comment` with per-field defaults.
- **`ApplyTriageResultActivity`** — applies labels + posts a triage comment on an existing issue, or **creates an issue** for a security/Dependabot/CodeQL alert, via `Engine:CallbackUrl/api/engine/{issue-labels,issue-comment,create-issue}`.

**Mediation posture (Epic 32 pivot): compliant.** Every LLM call routes through the `llm-call` sub-workflow (the engine-side mediation seam slated to re-point at `POST /api/v1/llm/call` — `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` §1.2). All git effects go through engine-callback endpoints. No external provider key is touched by any triage activity. This orchestrator needs no mediation rework.

**DCB events emitted today:** only the leaf `ApplyTriageResultActivity` emits `TRIAGE.APPLY.RESULT.{STARTED,COMPLETED,FAILED}` (via the `TammaAsyncActivity` base). The three `DispatchWorkflow` nodes and three `SetVariable` extracts are bare Elsa primitives that emit **nothing** through the Tamma event channel. There is **no `TRIAGE.ISSUE.STARTED/COMPLETED/FAILED/SKIPPED`** cycle-scoped event anywhere — the unit of audit ("we triaged item X → decision Y") is invisible to the DCB stream.

---

## Intended Full Scope (with citations)

From **Story 26-1** (`docs/stories/epic-26/story-26-1/26-1-issue-triage-workflow.md`) and the project rules, the per-item cycle must:

- Classify type / priority / complexity / autonomy from issue title/body/comments (AC2) — *done via the panel + PO sub-workflows*.
- **Apply labels** (AC3), **assign milestone if configured** (AC4), **assign to project board if configured** (Flow), **post the prescribed markdown-table triage comment** (AC5) — *labels + comment partially done; milestone / board / canonical-comment-rendering absent*.
- **Cache triage results — do not re-triage unchanged items** (AC6); **re-triage on significant edits / new context comments / `/triage`** (AC7) — *entirely absent at the cycle level*.
- Emit **`TRIAGE.ISSUE.STARTED` / `TRIAGE.ISSUE.COMPLETED`** (and, per project rules, `.FAILED` / `.SKIPPED`) events (AC9) — *absent*.

**Project rules** (`CLAUDE.md`; `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md`; MEMORY `feedback_resolution_no_empty_fallback`):

- Resolution is **tenant → system → error**; **never** an empty/plain/**fabricated** fallback.
- **No silent-failure / no false-success** — a failed step must surface a `.FAILED` event and follow a failure edge, never silently flow into a `.COMPLETED`/apply path.
- Steps **never call external providers directly** — route LLM via `llm-call`/`call-LLM`, effects via internal endpoints (**satisfied today**).
- Every operation emits **DCB audit events**.

**Domain best-practice for a single-item triage orchestrator:** validate each sub-stage's output before consuming it; gate "apply" on a *valid* decision (never label from a synthesized/empty decision); isolate and report per-item failure with a typed reason; be idempotent/replay-safe so a re-dispatch of the same item does not double-label or double-comment; surface a structured per-item outcome to the parent for batch summarization.

---

## Missing Capabilities (gap to "complete")

| # | Capability | Priority | Depends on |
|---|------------|----------|-----------|
| 1 | **Decision-OK gate before apply.** No `FlowDecision` between `ExtractDecision` and `ApplyLabels`; a missing/unparseable/empty PO decision flows straight into labeling. Must branch and **skip apply** on a bad decision. | **P0** | none (consumes 26-1 fallback fix in `TriagePODecisionWorkflow`) |
| 2 | **No failure edges on the three `DispatchWorkflow` nodes.** If context/panel/PO sub-workflow faults, the orchestrator has no error path — it either dead-ends or proceeds with empty JSON into apply (false-success). Need a `Faulted`/timeout edge → fail-the-item path. | **P0** | none |
| 3 | **Cycle-scoped DCB events absent.** Emit `TRIAGE.ISSUE.STARTED` at `Init` and `TRIAGE.ISSUE.{COMPLETED,FAILED,SKIPPED}` at each exit, tagged `{ issueId/itemKey, repository, itemSource, type, priority, automation }`. Satisfies AC9 + time-travel audit. | **P0** | none |
| 4 | **No idempotency / re-triage gate.** Singleton serializes but does not dedupe; an overlapping ADL run + webhook re-dispatch of the same item re-runs the whole panel and re-labels/re-comments. Need a pre-flight triage-state check + skip path. | **P0** | new 26-1 sub-story (triage-state store + endpoint) |
| 5 | **No per-item outcome output for the parent.** The cycle returns nothing; the fire-and-forget parent reports a blanket `triageComplete` even if this item failed. Emit a structured `{ itemKey, outcome, decisionVersion, error? }` (SetOutput or POST to a results endpoint). | **P1** | coordinated with `IssueTriage.md` #5 |
| 6 | **Milestone assignment on apply.** AC4 — when the decision/repo-config names a milestone that exists, assign it; emit `TRIAGE.MILESTONE.ASSIGNED`. | **P1** | new `AssignMilestoneActivity` + engine endpoint |
| 7 | **Canonical comment render + label validation.** Render the AC5 markdown-table comment **deterministically from the parsed decision** (not raw LLM prose), and validate `labels` against the canonical type/priority/complexity/automation vocabulary before applying. | **P1** | none |
| 8 | **`ApplyTriageResultActivity` swallows failures.** Its `RunAsync` wraps everything in `try/catch` that logs and returns, and never checks `response.IsSuccessStatusCode` — a 4xx/5xx from `issue-labels`/`issue-comment`/`create-issue` still emits `TRIAGE.APPLY.RESULT.COMPLETED` (false-success). Must check status and `throw`. | **P0** | none |
| 9 | **Singleton not enforced in code.** Header claims "singleton — one instance at a time," but `Build()` sets no singleton/options flag; concurrency relies on an unstated assumption. Either enforce it (workflow options) or drop the claim and rely on the dedupe gate (#4). | **P1** | none |
| 10 | **Project-board assignment.** Flow step — assign to a configured project board; emit `TRIAGE.PROJECT.ASSIGNED`. | **P2** | new `AssignProjectBoardActivity` + endpoint |
| 11 | **Richer LLM context threading.** Pass existing repo labels, milestone list, recent PRs, CLAUDE.md/tech-stack excerpt, and recent similar issues into the context/panel/PO `variables`; have PO return `relatedIssues` and reference them in the comment. | **P2** | engine-callback enrichment (shared with `ContextGathering.md`) |
| 12 | **No orchestrator-level tests.** Add xUnit coverage for: decision-OK gate (good vs. unparseable), sub-workflow-faulted failure edge, cycle event emission on each exit, dedupe-skip path, apply-failure throw. | **P1** | none |

---

## Build-out Spec (ordered)

Implement P0 correctness first (it changes the flowchart shape), then events, then scope.

1. **Emit `TRIAGE.ISSUE.STARTED` at `Init`.** Add an `EmitTriageEventActivity` (or extend `Init`) emitting `TRIAGE.ISSUE.STARTED` with tags `{ itemKey = source+number/cveId, repository, itemSource, itemType }`. Derive `itemKey` deterministically from `itemJson` (issue → `repo#number`; alert → `repo:cveId|rule`). Connect `Init → EmitStarted → GatherTriageContext`.

2. **Idempotency / re-triage gate (head of cycle).** New `CheckTriageStateActivity` (`EventType=TRIAGE.STATE.CHECK`) calling a new engine endpoint `GET /api/engine/triage-state?repo=&key=` returning `{ triaged, bodyHash, decisionVersion }`. Insert after `Init`/`EmitStarted`. Add `FlowDecision("AlreadyTriaged?")`: if `triaged && bodyHash == currentHash` (and no `/triage` command / >20% body delta) → `Report Item Result(reason="triageSkipped")` emitting `TRIAGE.ISSUE.SKIPPED` → `Finish`; else continue. Persist state in step 8's success path (`TRIAGE.STATE.WRITE`).

3. **Add failure edges to the three `DispatchWorkflow` nodes.** Give `GatherTriageContext`, `PanelReview`, `PODecision` a `Faulted`/error outcome wired to a shared **`FailItem`** node: `Report Item Result(reason="triageFailed", stage=<name>, error=<msg>)` emitting `TRIAGE.ISSUE.FAILED` → `Finish`. The sub-workflows already fall back to `{}` on parse failure, so the engine fault is the real signal here — do not let a faulted dispatch silently continue.

4. **Decision-OK gate before apply.** Depends on the 26-1 fix that makes `TriagePODecisionWorkflow.ExtractDecision` stop synthesizing `normal/feature/needs-human` and instead surface `decisionStatus="unparseable"` (or the typed `call-LLM` error). Add `FlowDecision("DecisionOK?")` after `ExtractDecision`: parse `poDecisionJson`; on `unparseable` / empty / missing required fields → route to **`FailItem`** (`TRIAGE.ISSUE.FAILED`, reason `decisionUnparseable`) and **skip `ApplyLabels` entirely**. Never label an issue from a fabricated decision (no empty/plain fallback rule).

5. **Make `ApplyTriageResultActivity` fail loudly.** Check `response.IsSuccessStatusCode` on each `issue-labels` / `issue-comment` / `create-issue` POST; on non-success or exception, `throw` so the base emits `TRIAGE.APPLY.RESULT.FAILED`. Wire `ApplyLabels`'s implicit fault edge → **`FailItem`** (`TRIAGE.ISSUE.FAILED`, reason `applyFailed`). Remove the blanket swallow-and-return.

6. **Canonical comment render + label validation (inside / before `ApplyLabels`).** Build the AC5 markdown-table comment deterministically from the parsed decision fields. Validate each label against the canonical vocabulary (`FetchUntriagedItemsActivity.TriageLabels` superset / type-priority-complexity-automation grid); drop unknowns and emit `TRIAGE.LABELS.INVALID` (warning) rather than writing arbitrary LLM labels.

7. **Milestone assignment.** New `AssignMilestoneActivity` (`POST /api/engine/issue-milestone`, `EventType=TRIAGE.MILESTONE.ASSIGN`) invoked from the apply success path when the decision/repo-config names a milestone present in the repo's milestone list; emit `TRIAGE.MILESTONE.ASSIGNED`. (P2: `AssignProjectBoardActivity` → `TRIAGE.PROJECT.ASSIGNED`.)

8. **Persist triage state + emit success.** On a successful apply, POST `triage-state` `{ repo, key, bodyHash, decisionVersion, triagedAt }` (`TRIAGE.STATE.WRITE`), then emit `TRIAGE.ISSUE.COMPLETED` tagged `{ itemKey, repository, type, priority, automation, decisionVersion }`. Connect `ApplyLabels(success) → PersistState → EmitCompleted → Finish`.

9. **Surface per-item outcome to the parent.** `SetOutput("itemResult", { itemKey, outcome ∈ {triaged,failed,skipped}, decisionVersion, error? })`, and/or POST to `/api/engine/triage-item-result` so the parent's `ReportCycleResult` reports `{ triaged, failed, skipped }` instead of a blanket `triageComplete` (coordinate with `IssueTriage.md` #5).

10. **Enforce or drop the singleton claim.** Either set the singleton/options flag in `Build()` (so the header is true) or, once the dedupe gate (#2) lands, soften the header to rely on dedupe for correctness rather than serialization.

11. **Enrich LLM context (P2).** Thread repo labels, milestone list, recent PRs, CLAUDE.md/tech-stack excerpt, and recent similar issues into the context/panel/PO `variables` via engine-callback enrichment; have PO return `relatedIssues`; reference them in the comment.

12. **Tests (xUnit).** Cover: `DecisionOK?` good vs. unparseable (apply vs. fail edge); a faulted sub-workflow dispatch → `FailItem` → `TRIAGE.ISSUE.FAILED`; dedupe-skip path → `TRIAGE.ISSUE.SKIPPED`; apply HTTP-failure throw → `TRIAGE.APPLY.RESULT.FAILED`; `STARTED`/`COMPLETED` emission with correct tags.

---

## Verdict

**partial.** The per-item cycle orchestrates a genuinely strong subsystem — correct LLM/effect mediation, a real 4-role panel, a structured PO decision, and a labels/comment/create-issue apply step — so it is well past a thin stub. But the **orchestrator layer itself is happy-path-only**: it has **no `DecisionOK?` gate** (will label from a fabricated/empty decision), **no failure edges** on its three sub-workflow dispatches (false-success on a faulted stage), **no cycle-scoped `TRIAGE.ISSUE.*` DCB events** (the unit of audit is invisible), **no idempotency** (overlapping triggers re-triage/re-label), and `ApplyTriageResultActivity` **swallows HTTP failures** while still emitting `.COMPLETED`. These are P0 correctness/contract defects against the no-empty-fallback and no-false-success rules. P1 scope gaps: per-item outcome surfacing, milestone assignment, canonical-comment render + label validation, singleton enforcement, and tests. Build-out effort: **L** (re-shapes the flowchart with gates + failure edges, adds 2-3 activities + a triage-state endpoint, plus event/test work).
