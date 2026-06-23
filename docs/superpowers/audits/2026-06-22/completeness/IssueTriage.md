# Completeness Audit — `IssueTriageWorkflow`

**Date:** 2026-06-22
**Workflow:** `issue-triage` (`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/IssueTriageWorkflow.cs`)
**Maturity:** **partial** — core multi-workflow pipeline is genuinely built; meaningful scope + robustness gaps vs. the canonical story and project rules.

---

## Purpose & Owner

**Purpose:** Fan-out dispatcher — fetch all untriaged work (open issues, Dependabot alerts, CodeQL alerts) for a repository and dispatch one singleton per-item triage cycle each, so items are triaged sequentially without overloading the LLM path.

**Owner:** Epic 26 — Project Management & Triage, Story **26-1 Issue Triage Workflow** (`docs/stories/epic-26/story-26-1/26-1-issue-triage-workflow.md`, status: Drafted). Also a downstream consumer of Epic 32's agent-architecture pivot (LLM mediation).

**Triggered by (today):** ADL Orchestrator only — `selectWorkItem → NeedsTriage` outcome → `DispatchTriageActivity` (`AdlOrchestratorWorkflow.cs` lines 92-196). The workflow header *claims* GitHub webhook (`issues.opened`) and manual dispatch as triggers, but no webhook handler dispatches `issue-triage` (`grep` of `Tamma.Api`/`Tamma.ElsaServer` finds the definition only) — those triggers are aspirational, not wired.

---

## Current Capabilities (what it actually does today)

The workflow is a **dispatcher over a real, multi-workflow subsystem** — not a thin stub:

- **`IssueTriageWorkflow`** (this file): `FetchUntriagedItems → HasItems? → loop[ ExtractItem → DispatchWorkflow(triage-item-cycle, fire&forget) → NextItem → MoreItems? ] → ReportCycleResult(reason="triageComplete") → Finish`. Empty-set short-circuits to report+finish.
- **`FetchUntriagedItemsActivity`** (`Tamma.Activities/ADL`): pulls open issues + Dependabot alerts + CodeQL alerts via `Engine:CallbackUrl/api/engine/{issues,security-alerts}`; "untriaged" = issue carries none of a fixed `TriageLabels` set; has a `UseMock` simulated path; emits `TRIAGE.FETCH.ITEMS.*` via the base class.
- **`TriageItemCycleWorkflow`** (singleton): `Init → Gather Context → Panel Review → PO Decision → ApplyTriageResult → Finish`, threading `contextJson`/`panelResultJson`/`decisionJson` between sub-workflows.
- **`TriageContextGatheringWorkflow`**: dispatches `llm-call` (role=developer, action=context-scan, `scanFocus=triage`, `enableTools=true`); detects item type (issue/security/dependency); robustly extracts a JSON block or wraps raw text.
- **`TriagePanelReviewWorkflow`**: 4 sequential `llm-call` dispatches (security/developer/devops/tester) with per-role triage actions via `RolePhaseMap.GetTriageActionForRole`; aggregates into `panelResultJson`.
- **`TriagePODecisionWorkflow`**: dispatches `llm-call` (role=product_owner, action=triage-intake); parses `priority/type/complexity/automation/labels/comment`.
- **`ApplyTriageResultActivity`**: applies labels + posts a triage comment on an existing issue, or **creates an issue** for a security alert, via engine-callback endpoints.

**Mediation posture (Epic 32 pivot):** Compliant for current topology. All LLM work routes through the `llm-call` workflow (the engine-side mediation seam slated to re-point to `POST /api/v1/llm/call`); GitHub writes go through `Engine:CallbackUrl/api/engine/*` (engine-callback) — same compliant pattern the pivot audit (`docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` §1.2) gives `TriggerCIActivity`. No external provider key is held in any triage activity.

**DCB events emitted today:** activity-level `.STARTED/.COMPLETED/.FAILED` from the `TammaActivity` base for `TRIAGE.FETCH.ITEMS`, `TRIAGE.APPLY.RESULT`, `CYCLE.RESULT.REPORT`, `ADL.TRIAGE.DISPATCH`. No *cycle-scoped* triage events.

---

## Intended Full Scope (with citations)

From **Story 26-1** (`docs/stories/epic-26/story-26-1/26-1-issue-triage-workflow.md`), the complete workflow must:

- Be **triggered by issue webhooks** (`issues.opened`, `issues.edited`) *and* ADL *and* manual (story "Trigger"; AC1).
- LLM **reads issue title/body/comments**, classifies type/priority/complexity, assesses autonomy (AC2). LLM receives **repo context (CLAUDE.md, recent PRs, tech stack), existing repo labels, milestone list, recent similar issues for consistency** (story "LLM Triage Prompt") and returns `relatedIssues`.
- **Apply labels** via the platform (AC3); **assign milestone if configured** (AC4); **assign to project board if configured** (story Flow); **post a triage comment** as the prescribed markdown table (AC5, "Triage Comment").
- **Cache triage results — don't re-triage unchanged issues** (AC6); **re-triage on significant edits** (>20% body change), new context-changing comments, or `/triage` command (AC7, "Re-triage").
- `tamma-auto` label drives ADL pickup (AC8).
- Emit **`TRIAGE.ISSUE.STARTED/COMPLETED`** events (AC9).

**Project rules** (`CLAUDE.md`, pivot spec, MEMORY feedback `feedback_resolution_no_empty_fallback`):
- Resolution is **tenant → system → error**; **never** empty/plain/fabricated fallback.
- **No silent-failure / no false-success** — an activity that fails must surface a `.FAILED` event and a failure edge, not emit `.COMPLETED`.
- Steps **never call external providers directly** — route LLM via `call-LLM`/`llm-call`, external effects via internal endpoints (satisfied today).
- All operations emit **DCB audit events**.

Domain best-practice for an autonomous triage fan-out: idempotency/dedupe across overlapping triggers, bounded fan-out / failure isolation that still surfaces per-item outcomes, partial-batch reporting (n succeeded / m failed), and replay-safe re-triage.

---

## Missing Capabilities (gap to "complete")

| # | Capability | Priority | Depends on |
|---|---|---|---|
| 1 | **PO-decision empty/fabricated fallback removed.** On LLM failure `TriagePODecisionWorkflow` returns hardcoded `priority=normal,type=feature,automation=needs-human` and still labels the issue — a fabricated triage. Must error/defer per tenant→system→error. | **P0** | 32-5 mediation (typed error from `call-LLM`) |
| 2 | **Failure paths / no false-success.** `ApplyTriageResultActivity` and `FetchUntriagedItemsActivity` catch exceptions internally and return normally → base class emits `.COMPLETED` on failure. Apply-failure (label/comment/create-issue) is invisible. | **P0** | none |
| 3 | **Idempotency / re-triage / caching.** No record of "already triaged at body-hash X". Label-presence filter is a half-dedupe; overlapping ADL + webhook runs double-triage. Story AC6/AC7 (cache; re-triage on >20% edit / `/triage`). | **P0** | new story (triage-state store) |
| 4 | **Cycle-level DCB events.** Story AC9 `TRIAGE.ISSUE.STARTED/COMPLETED` (+ `.FAILED`, `.SKIPPED`, `.DEFERRED`) per item, with `issueId`/`itemSource`/`decision` tags. Today only generic activity events. | **P1** | none |
| 5 | **Per-item outcome surfaced to batch.** Fan-out is fire-and-forget, so cycle failures never reach the parent; `ReportCycleResult` always reports `triageComplete` regardless of per-item results. No "n triaged / m failed / k skipped" summary. | **P1** | none |
| 6 | **Milestone assignment.** Story AC4 — not implemented in any activity/workflow. | **P1** | none |
| 7 | **Project-board assignment.** Story Flow — not implemented. | **P2** | none |
| 8 | **Webhook trigger wiring.** Header claims `issues.opened`/`issues.edited`/manual; only ADL dispatches it. Need a webhook handler that dispatches `issue-triage` (or a single-item entry) and a manual endpoint. | **P1** | none |
| 9 | **Richer LLM context inputs.** Story prompt: repo `CLAUDE.md`, recent PRs, tech stack, **existing repo labels**, **milestone list**, **recent similar issues** + return `relatedIssues`. Today the panel/PO only get item+context+repo string. | **P2** | 32-5 mediation |
| 10 | **Triage comment format.** Story prescribes a specific markdown table (Type/Priority/Complexity/Autonomous + reasoning + applied labels). Today the comment is whatever the PO LLM returns. | **P2** | none |
| 11 | **Label-vocabulary validation.** PO `labels` are applied as-is; no validation against the canonical type/priority/complexity/automation vocabulary in the story before write. | **P2** | none |
| 12 | **Tests.** Zero tests for any triage workflow/activity (fetch parsing, item-type detection, decision parse + fallback, apply branches, dedupe, failure edges). | **P1** | none |

---

## Ordered Build-out Spec (to reach complete + robust)

Steps are ordered so safety/contract fixes land first.

1. **P0 — Kill the fabricated PO fallback (no empty/plain fallback).** In `TriagePODecisionWorkflow.ExtractDecision`, when the LLM result is missing/unparseable, do **not** synthesize `normal/feature/needs-human`. Set an output `decisionStatus="unparseable"` (or surface the typed error from `call-LLM`) and add a `FlowDecision("Decision OK?")` in `TriageItemCycleWorkflow` after `ExtractDecision`: on failure route to a new `Report Item Result(reason="triageFailed", error=...)` edge that emits `TRIAGE.ISSUE.FAILED` and **skips** `ApplyLabels`. Never label an issue from a fabricated decision.

2. **P0 — Make apply/fetch failures real failures.** Stop swallowing exceptions in `ApplyTriageResultActivity.RunAsync` and the per-source `try/catch` blocks of `FetchUntriagedItemsActivity` that affect the success contract: on a non-success HTTP status or exception, `throw` so the base class emits `.FAILED` (keep per-source resilience in fetch only if you record `partialFetch=true` in end-data and still emit a distinct `TRIAGE.FETCH.ITEMS.PARTIAL` event — no silent success). `ApplyTriageResultActivity` must check `response.IsSuccessStatusCode` on each label/comment/create-issue POST and throw on failure.

3. **P0 — Idempotency / dedupe / re-triage gate (new story).** Add a triage-state record keyed `(repository, itemSource, itemKey)` storing `bodyHash`, `triagedAt`, `decisionVersion` (via a new engine-callback endpoint, e.g. `GET/POST /api/engine/triage-state`). New activity `CheckTriageStateActivity` (`EventType=TRIAGE.STATE.CHECK`) at the head of `TriageItemCycleWorkflow`: branch `AlreadyTriaged? & body unchanged → Report Item Result(reason="triageSkipped")` emitting `TRIAGE.ISSUE.SKIPPED`; else proceed. Re-triage when body-hash differs >20%, a new comment requests it, or a `/triage` command is seen. Persist state in `ApplyTriageResultActivity` on success (`TRIAGE.STATE.WRITE`).

4. **P1 — Cycle-scoped DCB events.** Add an `EmitTriageEventActivity` (or extend `TriageItemCycleWorkflow` start/end) to emit `TRIAGE.ISSUE.STARTED` at `Init` and `TRIAGE.ISSUE.COMPLETED` / `.FAILED` / `.SKIPPED` / `.DEFERRED` at each exit, with tags `{ issueId, repository, itemSource, type, priority, automation, decisionVersion }`. Satisfies AC9 and feeds time-travel/audit.

5. **P1 — Per-item result reporting + batch summary.** Have each `triage-item-cycle` return an output `{ itemKey, outcome, error? }`. Two options: (a) switch the parent dispatch to `WaitForCompletion=true` and accumulate, or (b) keep fire-and-forget but have each cycle POST a per-item result to a new `/api/engine/triage-item-result` endpoint that the parent's `ReportCycleResult` summarizes. Replace the unconditional `reason="triageComplete"` with a computed `{ triaged, failed, skipped }` summary so a fully-failed batch never reports success.

6. **P1 — Webhook + manual trigger.** Add a `Tamma.Api` webhook handler for `issues.opened`/`issues.edited` that dispatches a single-item triage (a `single-item` input shape on `triage-item-cycle`, bypassing fetch), and a manual `POST /api/v1/triage` endpoint. Make the workflow header's stated triggers real. Gate re-triage on `issues.edited` through step 3's dedupe.

7. **P1 — Milestone assignment.** New `AssignMilestoneActivity` (engine-callback `POST /api/engine/issue-milestone`), invoked from `ApplyTriageResultActivity`'s success path when the PO decision (or repo config) names a milestone and it exists in the repo's milestone list. Emit `TRIAGE.MILESTONE.ASSIGNED`.

8. **P1 — Tests.** Add Vitest-equivalent C# tests (xUnit) for: fetch JSON parse + item-type detection; PO decision parse + the *new* error-not-fallback behavior; apply label/comment/create-issue branches incl. failure throw; dedupe skip path; empty-batch short-circuit; cycle event emission.

9. **P2 — Richer LLM context.** Extend `TriageContextGatheringWorkflow` / panel / PO `variables` to include existing repo labels, milestone list, recent PRs, tech-stack/CLAUDE.md excerpt, and recent similar issues; have PO return `relatedIssues`. Fetch these via engine-callback, not direct git calls (mediation rule). Reference issues in the triage comment.

10. **P2 — Project-board assignment.** New `AssignProjectBoardActivity` (engine-callback), invoked when configured; emit `TRIAGE.PROJECT.ASSIGNED`.

11. **P2 — Canonical triage comment + label validation.** Render the story's markdown-table comment from the parsed decision (deterministic, not raw LLM prose). Validate `labels` against the canonical type/priority/complexity/automation vocabulary before applying; drop/flag unknown labels and emit a `TRIAGE.LABELS.INVALID` warning event rather than writing arbitrary labels.

---

## Verdict

**partial** — the triage subsystem is one of the better-built flows (real fan-out, real 4-role panel + PO pipeline, correct LLM/effect mediation, no in-engine keys), clearly past the "thin happy-path" stage. But it is not complete: it has **P0 correctness/contract defects** (fabricated PO fallback that still labels issues; swallowed failures emitting false `.COMPLETED`; no idempotency so overlapping triggers double-triage) plus **P1 scope gaps** (cycle-level `TRIAGE.ISSUE.*` events, per-item outcome surfacing, milestone assignment, real webhook/manual triggers, tests). Build-out is **L** (one P0 store/dedupe story + several activities + event/test work).
