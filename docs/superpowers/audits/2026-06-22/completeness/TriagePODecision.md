# Completeness Audit — `TriagePODecisionWorkflow`

**Audited:** 2026-06-22
**File:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TriagePODecisionWorkflow.cs`
**DefinitionId:** `triage-po-decision`
**Maturity:** **partial** (core happy path is real, but several correctness/contract gaps; this is *not* a stub)

---

## Purpose & owner

The Product Owner (PO) step of the multi-stage triage pipeline. It takes a single untriaged item plus the 4-role panel-review result and asks an LLM (role=`product_owner`, action=`triage-intake`) to make the **final triage decision**: `priority`, `type`, `complexity`, `automation` level, `labels`, and a summary `comment`. Output `decisionJson` is consumed downstream by `ApplyTriageResultActivity` (sets labels / posts the comment / creates an issue for security alerts).

- **Owning epic/story:** Epic 26 (Project Management & Triage), Story **26-1** "Issue Triage Workflow" (`docs/stories/epic-26/story-26-1/26-1-issue-triage-workflow.md`).
- **Pipeline position:** `IssueTriageWorkflow` → (fire&forget, singleton) `TriageItemCycleWorkflow` → `triage-context-gathering` → `triage-panel-review` → **`triage-po-decision`** → `ApplyTriageResultActivity`.

---

## Maturity: partial

Three named LLM-mediation phases sit on top of the shared `llm-call` sub-workflow, and the JSON-extraction logic is non-trivial (field-by-field defaulting, JSON-block carving, raw-text fallback). That puts it well above the "thin happy-path skeleton" tier (e.g. PullRequest's CreatePR→3×SetOutput). But it has a **silent-failure / false-success** problem and several scope gaps versus the Story 26-1 spec, so it is **partial**, not complete.

### Current capabilities (what it does today)

1. **Init** — reads inputs `repository`, `itemJson`, `panelResultJson` into variables.
2. **PO Decision** — `DispatchWorkflow` to `llm-call` with `role=product_owner`, `action=triage-intake`, variables `{itemJson, panelResultJson, repository}`, `enableTools=false`, `WaitForCompletion=true`. Correctly routes the LLM call through the `llm-call` mediation sub-workflow (so it already complies with the "steps never call providers directly" rule — `llm-call` owns provider chain, circuit breaker, budget, retry, prompt/convention resolution).
3. **Extract Decision** — reads `llmResult["llmResponse"]`, carves the first `{`…last `}` JSON block, parses it, and builds a normalized decision dict with safe field defaults (`priority=normal`, `type=feature`, `complexity=medium`, `automation=needs-human`, `labels=[]`, `comment=""`). On parse failure or missing JSON it wraps the raw text as `comment`; with no result it emits a "No PO decision received." placeholder decision.
4. **Set Output** `decisionJson` → **Finish**.

---

## Intended full scope

### From Story 26-1 (`docs/stories/epic-26/story-26-1/26-1-issue-triage-workflow.md`)
The PO/triage decision must produce and apply:
- **Classification:** type (bug/feature/chore/question/security/docs), priority (critical/high/medium/low), complexity (trivial/simple/medium/complex/epic), autonomy (`tamma-auto`/`tamma-assist`/`needs-human`).
- **`reasoning`** field (Story §"LLM Triage Prompt" returns `reasoning`; `TriageDecision.Reasoning` exists on the consumer model but the workflow never populates it).
- **Suggested labels, suggested milestone, related issues** (`suggestedMilestone`, `relatedIssues`).
- **Triage comment** rendered as the documented markdown table.
- **AC — events:** "Events: `TRIAGE.ISSUE.STARTED/COMPLETED`" (acceptance criterion explicitly requires DCB triage events).
- Idempotency / re-triage caching ("Triage results cached — don't re-triage unchanged issues") — primarily an `IssueTriageWorkflow`/cycle concern, but the decision step should not silently fabricate a decision that then gets applied.

### From the project rules (`CLAUDE.md`) and the agent-architecture pivot spec (`docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md`)
- **No empty/plain fallback, no false success:** resolution is tenant→system→error; never fabricate a value and proceed as if successful (memory `feedback_resolution_no_empty_fallback`, `feedback_code_ownership`). A failed LLM call must surface, not be laundered into a default `needs-human` decision.
- **Steps never call external providers directly** — already satisfied (routes via `llm-call`; the pivot will move `llm-call`'s `CallLlmInlineActivity` behind `POST /api/v1/llm/call`, transparently to this workflow — spec §1–§2).
- **Emit DCB audit events** for every operation (`CLAUDE.md` §"Emitting Events for Audit Trail"; `TammaEventEmitter` infra exists in `Tamma.Activities/Core/TammaActivity.cs`). This workflow emits **no** workflow-level events.

### Domain best-practice for an LLM-decision step
Validate the model output against the allowed enum vocabulary (don't accept arbitrary `priority` strings), branch on call success vs failure, persist/output the raw model response for audit/time-travel, and make the failure path explicit rather than defaulting to a benign-looking value that then mutates the issue.

---

## Missing capabilities

| # | Capability | Priority | dependsOn |
|---|------------|----------|-----------|
| 1 | **Branch on `llm-call` success.** `llm-call` returns a `success` bool and a structured failure (`workflowOutput`), but this workflow ignores it — a total LLM failure (all providers down / budget exhausted / allowlist reject) silently produces a `needs-human` "No PO decision received." decision that `ApplyTriageResultActivity` then applies (labels/comment). Add a `FlowDecision` on `success`; failure path emits a failure event and does NOT fabricate an applied decision. | **P0** (false-success / silent-failure — violates `feedback_resolution_no_empty_fallback`) | none |
| 2 | **Distinguish "no parseable JSON" from a real decision.** When the model returns prose, the workflow wraps raw text as `comment` and stamps default classifications — downstream this applies `priority-normal`/`needs-human` labels as if the PO decided that. Mark such outputs as `unparsed`/needs-human-review instead of presenting them as a clean decision. | **P0** | none |
| 3 | **Emit DCB triage events** (`TRIAGE.PO_DECISION.STARTED` / `.COMPLETED` / `.FAILED`, carrying priority/type/automation + provider/cost from the `llm-call` result) — Story 26-1 AC requires `TRIAGE.*` events; today the workflow emits none (only the leaf `ApplyTriageResultActivity` emits `TRIAGE.APPLY.RESULT`). | **P1** | none (infra exists: `TammaEventEmitter`) |
| 4 | **Validate decision fields against the allowed vocabulary.** `priority`/`type`/`complexity`/`automation` are taken verbatim from the model with only null-defaults — an out-of-vocabulary value (e.g. `priority="P0"`, `automation="auto"`) flows straight to labels. Clamp/normalize to the enum sets from Story 26-1; on invalid, default + flag in the comment. | **P1** | none |
| 5 | **Populate `reasoning`, `suggestedMilestone`, `relatedIssues`.** Story 26-1's decision schema and the consumer model `TriageDecision.Reasoning` include these; the workflow drops `reasoning` and never carries milestone/related-issue suggestions. | **P1** | none (consumer `ApplyTriageResultActivity` must also be extended to apply milestone/related — separate item) |
| 6 | **Output the raw LLM response + provider/cost for audit.** Add `SetOutput`s for `rawResponse`, `providerUsed`, `costUsd` (all available on the `llm-call` result) to support time-travel debugging and analytics (Epic 36). Today only `decisionJson` is output. | **P2** | none |
| 7 | **Empty-input guard.** No validation that `itemJson`/`panelResultJson` are non-empty before spending an LLM call; an empty/`{}` panel result silently yields a low-quality decision. Short-circuit with a `TRIAGE.PO_DECISION.SKIPPED` event when inputs are empty. | **P2** | none |
| 8 | **Render the documented triage-comment markdown table.** Story 26-1 specifies an exact markdown table for the triage comment; today the `comment` is whatever the model emitted (or raw prose). Optionally render a deterministic table from the parsed fields so the applied comment is consistent. | **P3** | depends on #4 (validated fields) |

> Note: the Epic-32 pivot (route `llm-call` behind `POST /api/v1/llm/call`) requires **no change to this workflow** — it already dispatches `llm-call`. The pivot is internal to the `llm-call` sub-workflow.

---

## Ordered build-out spec

Honoring: tenant→system→error (never empty/plain fallback), no silent-failure/false-success, route LLM via the `llm-call` sub-workflow (already done), emit DCB audit events.

1. **Add a workflow-start event step (`EmitStart`).** Right after `Init`, emit `TRIAGE.PO_DECISION.STARTED` (via a small `TammaActivity`/`SetVariable`-backed emit, or by giving the dispatch a wrapper activity that carries `EventType`). Data: `{repository, itemNumber (parsed from itemJson), reviewCount}`. Tag with `issueId` from the item.

2. **Capture the `llm-call` success/output, not just `llmResponse`.** Extend the `PODecisionCall` result handling: read `llmResult["success"]` (bool), `llmResult["providerUsed"]`, `llmResult["costUsd"]`, and `llmResult["workflowOutput"]` (failure diagnostics) in addition to `llmResponse`. Store into new variables `callSucceeded`, `providerUsed`, `costUsd`, `rawResponse`.

3. **Insert a `FlowDecision` "PO Call Succeeded?"** between the dispatch and `ExtractDecision`, branching on `callSucceeded`:
   - **True →** `ExtractDecision` (existing, hardened — see step 4).
   - **False →** new `BuildFailureDecision` step that sets `decisionJson` to an **explicit failure marker** `{ "status": "llm-failed", "automation": "needs-human", "comment": "Triage PO decision could not be produced (LLM call failed); requires human triage.", "labels": ["needs-human","triage-failed"], "error": <diagnostics summary> }`, emits **`TRIAGE.PO_DECISION.FAILED`** (data: provider diagnostics, durationMs), then → `setOutputs` → `Finish`. This replaces today's silent "No PO decision received." default so the failure is visible and the applied labels are honest (`triage-failed`/`needs-human`), not a fabricated `priority-normal/feature`.

4. **Harden `ExtractDecision` (success branch):**
   - Carve the JSON block (existing). On **parse failure / no JSON**, set `decisionJson.status = "unparsed"` and `automation = "needs-human"`, add label `needs-human-review`, store the raw prose in `comment` — do NOT present it as a clean classified decision (item #2).
   - **Validate each field against the allowed vocabulary** (item #4): `priority ∈ {urgent/critical, high, normal/medium, low}`, `type ∈ {bug, feature, chore, question, security, docs}`, `complexity ∈ {trivial, simple, medium, complex, epic}`, `automation ∈ {tamma-auto, tamma-assist, needs-human}`. On out-of-vocabulary, default to the safe value AND append a note to `comment` ("PO returned invalid `<field>=<value>`, defaulted to `<default>`"). Use the canonical label vocabulary from Story 26-1.
   - **Populate `reasoning`** from the model JSON (`reasoning` property) into the decision dict, and carry `suggestedMilestone` / `relatedIssues` if present (item #5).

5. **Add an empty-input guard before the dispatch (item #7).** A `FlowDecision` "Inputs Present?" after `Init`: if `itemJson` is empty/`{}`, skip the LLM call, set `decisionJson` to a `skipped` marker, emit **`TRIAGE.PO_DECISION.SKIPPED`**, → `setOutputs` → `Finish`. Avoids spending an LLM call on garbage.

6. **Emit the completion event on the success branch.** After `ExtractDecision`, emit **`TRIAGE.PO_DECISION.COMPLETED`** with data `{priority, type, complexity, automation, labelCount, providerUsed, costUsd, durationMs}`, tagged `{issueId, mode, provider}` per the DCB tag convention. (Satisfies Story 26-1 `TRIAGE.*` events AC at the decision granularity.)

7. **Add audit outputs (item #6).** Alongside the existing `SetOutput decisionJson`, add `SetOutput`s for `rawResponse`, `providerUsed`, `costUsd`, and `callSucceeded` so the parent `TriageItemCycleWorkflow` (and analytics/Epic 36) can record them. Extend `TriageItemCycleWorkflow.ExtractDecision` to read `callSucceeded` and **skip / down-grade `ApplyLabels`** when the decision is a `llm-failed`/`unparsed` marker (so a failed triage doesn't auto-apply fabricated labels) — emit `TRIAGE.APPLY.SKIPPED` in that case.

8. **(P3) Deterministic comment rendering (item #8).** Once fields are validated (step 4), optionally render the Story 26-1 markdown table from the parsed/validated fields rather than passing through model prose, so the applied comment is consistent and audit-friendly. Keep the model's `reasoning` as the prose body of the table.

### Flowchart after build-out (success path)
```
Init
  → [Inputs Present?] ──No──► BuildSkippedDecision → (emit SKIPPED) → SetOutputs → Finish
       └─Yes─► EmitStart (TRIAGE.PO_DECISION.STARTED)
                 → PO Decision (llm-call)  [unchanged dispatch; capture success+cost+provider+raw]
                 → [PO Call Succeeded?] ──False──► BuildFailureDecision → (emit FAILED) → SetOutputs → Finish
                       └─True─► ExtractDecision (carve JSON, validate vocab, populate reasoning/milestone/related,
                                                  mark unparsed→needs-human)
                                 → (emit COMPLETED) → SetOutputs(decisionJson, rawResponse, providerUsed,
                                                                  costUsd, callSucceeded) → Finish
```

---

## Effort

**M** — single workflow, ~3 new branches/steps + event emission + field validation; the `llm-call` result already exposes `success`/`cost`/`provider`, so no new infra. The small downstream coupling (`TriageItemCycleWorkflow` must read `callSucceeded` and skip apply on failure; `ApplyTriageResultActivity` extension for milestone/related-issues is a separate, larger follow-on) pushes it above S but it stays within M.

**Overall priority: P1** — the workflow runs and produces decisions today, but the P0 silent-failure/false-success behavior (a failed or unparseable LLM call gets applied to the issue as a clean `needs-human`/`priority-normal` decision) is a real correctness/contract defect that should be fixed before relying on autonomous triage.
