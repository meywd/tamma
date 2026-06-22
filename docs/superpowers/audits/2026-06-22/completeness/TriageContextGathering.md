# Completeness Audit — `TriageContextGatheringWorkflow`

**File:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TriageContextGatheringWorkflow.cs`
**Definition ID:** `triage-context-gathering`
**Audit date:** 2026-06-22
**Verdict:** **THIN** — a 4-node happy-path skeleton (Init → one `llm-call` → extract JSON → output). The mediation posture is correct (it never touches a provider directly), but it has a **P0 contract defect** (the `variables` it passes do not match the `context-scan` prompt template's variables, so the LLM scans with an empty work item), it ignores the `llm-call` `success` flag (silent soft-fail), it advertises four context dimensions it does not actually drive, threads no `tenantId`, and emits no workflow-level DCB events. It is the weakest sub-workflow in the triage cluster.

> **Note vs. the prior cluster audit.** The sibling `workflow-audit-triage.md` (2026-06-22) rated this workflow "GOOD / 0 P0". That pass did not check the prompt template's declared variables against the dispatched `variables` dict. This deeper read finds a real variable-name mismatch (`itemJson`/`itemType` vs. the template's `{{workItemJson}}`/`{{workItemType}}`/`{{previousFindings}}`), which is a P0 correctness defect, so this completeness audit downgrades the verdict to **thin**.

---

## 1. Purpose & owner

**Purpose (one line):** Gather triage-time context for a single untriaged item (code usage of the affected package/module, dependency graph, CVE details for security alerts, changelog/migration guides) by dispatching one tool-enabled `llm-call` (`role=developer`, `action=context-scan`) and returning a `contextJson` bundle that the panel-review and PO-decision sub-workflows then reason over.

**Owning epic/story:** Epic 26 — Project Management & Triage, **Story 26-1 Issue Triage Workflow** (`docs/stories/epic-26/story-26-1/26-1-issue-triage-workflow.md`, status: in-progress per `sprint-status.yaml:327`). It is the first stage of `TriageItemCycleWorkflow` (`TriageItemCycleWorkflow.cs:66-96`), which is dispatched per item by `IssueTriageWorkflow`. It depends on the **Epic 32** agent-architecture pivot for LLM mediation — specifically the `call-LLM` seam preserved by 32-5 and tenant/BYOK threading by 32-3/32-16 (`docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` §1).

**Consumer contract (from `TriageItemCycleWorkflow.cs:66-96`):** input `{ repository, itemJson }`; output `{ contextJson }`. `TriageItemCycleWorkflow` reads `result["contextJson"]` into `ContextJson` and forwards it to `TriagePanelReviewWorkflow`.

---

## 2. Maturity: **THIN**

It is a 4-activity happy-path chain (`Init → GatherContext(llm-call) → ExtractResult → SetOutput → Finish`) with a single straight-line connection set and no decisions, no branches, no failure edge, and no events. The one substantive operation (the LLM scan) is correctly mediated through the `llm-call` sub-workflow — the engine holds no provider key, which honors the Epic-32 rule-1 boundary. That mediation correctness is the only thing keeping this above "stub".

Everything else is skeletal: it passes the wrong variable names into the prompt template (so the model never sees the item), it ignores `llm-call`'s `success` output, the item-type detection it performs is never consumed by the prompt (the template has no `{{itemType}}` / `{{scanFocus}}` placeholder), and it produces none of the four context dimensions its own doc-comment promises (code-usage, dependency graph, CVE, changelog) — it just runs the generic `context-scan` prompt and best-effort slices a `{...}` block out of the response.

---

## 3. Current capabilities (what it does today)

- **Init** (`SetVariable "Init"`, lines 56-80): reads `repository` and `itemJson` from workflow input; sniffs `itemType` ∈ {`issue`,`security`,`dependency`} by substring-matching the raw item JSON (`"type":"security"` / `"advisory"` / `"cve"` → security; `"type":"dependabot"` / `"dependency"` → dependency).
- **Gather Context** (`DispatchWorkflow("llm-call")`, lines 85-104): dispatches the universal `llm-call` with `role=developer`, `action=context-scan`, `enableTools=true`, and a `variables` dict of `{ itemJson, itemType, repository, scanFocus="triage" }`; `WaitForCompletion=true`; result → `llmResult`.
- **Extract Result** (`SetVariable "ExtractResult"`, lines 110-143): reads `llmResult["llmResponse"]`; finds the first `{` and last `}` and, if `JsonDocument.Parse` succeeds, returns that slice; otherwise wraps the raw text as `{ "rawContext": <text> }`. If `llmResponse` is absent, returns `"{}"`.
- **Set Output** (`SetOutput`, lines 149-151): emits `contextJson`.
- **Finish** (line 153).
- **Mediation posture:** correct. The only external effect is the LLM scan, routed through `llm-call`; no provider key in the engine. Matches the pattern the pivot audit blesses for `TriggerCIActivity`.

**DCB events emitted today:** none at this workflow's level. The only audit events on this path come from inside the dispatched `llm-call` sub-workflow (provider attempt diagnostics). There is no `TRIAGE.CONTEXT.STARTED/COMPLETED/FAILED`.

---

## 4. Intended full scope (with citations)

1. **It must actually produce the four context dimensions in its own contract.** The workflow header (lines 19-33) and `TriageItemCycleWorkflow.cs:65-66` both define this stage as gathering **code usage of the affected package/module, the dependency graph, CVE details for security alerts, and changelog/migration guides**. A complete version must drive those — e.g. branch by `itemType` so a `security`/`dependency` item runs a CVE/dep-focused scan (advisory metadata, affected version range, fixed-in version, dependency chain, changelog/migration link) and an `issue` item runs a code-usage scan — rather than running one generic `context-scan` whose template has no triage placeholders.

2. **The dispatched `variables` MUST match the resolved prompt template's variables.** The `context-scan` template (`SystemPrompts.cs:280-307`) declares `Variables: ["role", "workItemType", "workItemJson", "previousFindings"]` and its body interpolates `{{workItemType}}`, `{{workItemJson}}`, `{{previousFindings}}`. This workflow passes `itemJson`/`itemType`/`repository`/`scanFocus` — **none of which the template reads**. The render endpoint substitutes only provided variables and reports the rest as `UnresolvedVariables` (`PromptEndpoints.cs:448`), and `ResolvePromptFromRegistryActivity` does not read that field — so `{{workItemJson}}`/`{{workItemType}}`/`{{previousFindings}}` render **empty** and the model scans with no work item. The sibling `ContextGatheringWorkflow` passes the correct names (`workItemJson`/`workItemType`/`previousFindings`, `ContextGatheringWorkflow.cs:296-298`), which is the intended shape.

3. **Tenant- and BYOK-correct mediation (Epic 32).** Per the pivot spec §1 and `CLAUDE.md` Prompt-Store resolution order, the mediated `call-LLM` path resolves prompts and credentials per tenant (32-3/32-16). The cluster's design (see `workflow-audit-triage.md` obs. #6) relies on tenant resolution happening server-side inside `llm-call`/tamma-api via `ITenantContext`, so passing no `tenantId` is the cluster convention today. But `llm-call` already accepts a `tenantId` input (`LlmCallWorkflow.cs:153`, `CallLlmInlineActivity.TenantIdProp` line 898) and the consuming cycle would be more robust threading it explicitly. Treat as P1 (consistency with the cluster's stated server-side resolution; not a P0 unless that server-side resolution is later removed).

4. **No silent-failure / no false-success (project rule, `feedback_resolution_no_empty_fallback`).** `llm-call` returns a `success` bool plus a structured failure output (`LlmCallWorkflow.cs:548-574, 585-589`). This workflow ignores it: an all-providers-failed scan yields no `llmResponse`, `ExtractResult` returns `"{}"`, and the workflow finishes "successfully" with empty context — which then flows into the panel/PO as if context were genuinely gathered. A complete version gates on `success` and routes a failed/empty scan to a terminal failure (or an explicit `degraded` outcome), never an empty `"{}"` presented as success.

5. **Audit trail (DCB).** `CLAUDE.md` §"Emitting Events for Audit Trail" and Story 26-1 AC9 (`TRIAGE.ISSUE.STARTED/COMPLETED`) require lifecycle events. This stage emits no `TRIAGE.CONTEXT.STARTED/COMPLETED/FAILED`/`.EMPTY`, so time-travel debugging cannot reconstruct what context (or lack of it) drove a triage decision. The cluster audit (`workflow-audit-triage.md`, finding on this workflow) already flagged a `TRIAGE.CONTEXT.EMPTY`/`contextStatus` gap as the minimum here.

6. **Robust item-type detection that the prompt consumes.** The `itemType` sniff (lines 67-75) is substring-based (brittle to whitespace/pretty-printed JSON, same fragility called out for `ContextGatheringWorkflow`) AND is passed under a key (`itemType`) the template never reads — so it is currently dead. A complete version parses the item with `JsonDocument` and uses the type to select the scan focus and to populate a `{{workItemType}}` the template actually interpolates.

---

## 5. Missing capabilities (gap to complete)

| # | Missing capability | Priority | Depends on |
|---|---|---|---|
| 1 | **Variable-name contract mismatch — the LLM scans an empty work item.** Passes `itemJson`/`itemType`/`scanFocus`; the `context-scan` template reads `{{workItemJson}}`/`{{workItemType}}`/`{{previousFindings}}`. They render empty, so the model never sees the triage item. Must pass the template's declared variable names (mirror `ContextGatheringWorkflow.cs:296-298`). | **P0** | none (32-5 boundary preserved) |
| 2 | **Ignores `llm-call` `success` — silent soft-fail.** An all-providers-failed scan returns no `llmResponse`; `ExtractResult` emits `"{}"` and the workflow finishes "successful". Must gate on `success` and route failure to a terminal/degraded edge, never empty-as-success. | **P0** | 32-5 (`call-LLM` already returns `success`) |
| 3 | **Promised context dimensions not driven.** Doc-comment promises code-usage / dependency-graph / CVE / changelog; implementation runs one generic `context-scan` with no triage-specific prompt or branch. A security/dependency item gets the same generic scan as an issue. | **P1** | Story 26-1; a CVE/changelog/dep `action` or branch (32-5 mediation) |
| 4 | **No workflow-level DCB events.** No `TRIAGE.CONTEXT.STARTED/COMPLETED/FAILED`/`.EMPTY`; audit can't see whether context was gathered, degraded, or failed. | **P1** | none |
| 5 | **`itemType` detection is brittle AND dead.** Substring sniffing breaks on whitespaced JSON, and the resulting `itemType` is passed under a key the template never reads — so it influences nothing today. Parse JSON; feed a real `{{workItemType}}`. | **P1** | none |
| 6 | **`tenantId` not threaded into the `llm-call` dispatch.** Cluster convention resolves tenant server-side inside `llm-call`/tamma-api; `llm-call` nonetheless accepts a `tenantId` input. Threading it explicitly hardens SaaS prompt + BYOK resolution and matches `PlanGenerationWorkflow`. | **P1** | 32-3 / 32-16 |
| 7 | **No structured context inputs.** Pure free-form tool-enabled scan; no advisory metadata (affected/fixed version, dependency chain) fetched via engine-callback for security/dependency items, even though that data is exactly the "CVE details" the contract names. | **P2** | Epic 6 / engine-callback for advisory data |
| 8 | **No idempotency / cache.** Re-running for the same item re-spends LLM budget on identical context; no short-circuit. (Same pattern PRD Story 3.4 mandates 24h research caching for.) | **P2** | none for guard; store for cache |
| 9 | **Lossy `{...}` extraction.** `IndexOf('{')..LastIndexOf('}')` grabs outermost braces; nested/trailing prose breaks it and the result silently degrades to `rawContext`. No schema validation of the gathered context. | **P2** | none |
| 10 | **Near-duplicate of `ContextGatheringWorkflow` with drift.** Both run a `context-scan`; this one diverges (single scan, wrong variable names). No shared helper → the bug in #1 is exactly the drift this causes. | **P3** | none |

---

## 6. Ordered build-out spec (to reach complete & robust)

Ordered so P0 correctness/contract fixes land first. Honor: tenant→system→error (never empty/plain fallback), no silent-failure / no false-success, steps never call providers directly (route via `llm-call`), emit DCB events.

1. **Fix the variable contract so the model sees the item (P0, #1).**
   - In the `GatherContext` dispatch `variables` (lines 93-99), replace `itemJson`/`itemType`/`scanFocus` with the template's declared names: `["workItemJson"] = itemJson.Get(ctx)`, `["workItemType"] = itemType.Get(ctx)`, `["previousFindings"] = "{}"` (no prior roles in this single-scan flow). Keep `repository` only if a future template variant reads it.
   - This aligns the triage scan with `ContextGatheringWorkflow.cs:296-298` and removes the silent unresolved-variable render.
   - Add a unit/integration assertion that the rendered `context-scan` prompt contains the item body (guards against re-introducing the mismatch).

2. **Gate on `llm-call` `success` — no silent soft-fail (P0, #2).**
   - The `GatherContext` dispatch already captures `llmResult`. After it, add a `FlowDecision("Context Gathered?", ctx => llmResult["success"] is true)`.
   - `True` → `ExtractResult` → `SetOutput` (today's path).
   - `False` → a new `EmitContextFailed` node (step 4) that emits `TRIAGE.CONTEXT.FAILED` and sets `contextStatus="failed"` plus `contextJson="{}"`, then routes to `Finish` — but crucially surfaces a non-success signal so `TriageItemCycleWorkflow` can branch (it currently assumes success; pair with the cluster fix to add a per-stage "usable result?" decision there).
   - In `ExtractResult`, when `llmResponse` is present but empty/unparseable to meaningful context, set `contextStatus="empty"` rather than presenting `"{}"` as complete.

3. **Drive the promised context dimensions by item type (P1, #3).**
   - In `Init`, after parsing `itemType`, branch the scan: for `security`/`dependency` items, dispatch `llm-call` with a CVE/dependency-focused `action` (e.g. a `triage-vuln-context` action, or reuse `context-scan` with a `{{workItemType}}=security` that the template already conditionalizes) and include advisory variables (affected range, fixed-in, dependency chain — see step 7); for `issue` items, the code-usage `context-scan`.
   - Add a `FlowDecision` on `itemType` between `Init` and the dispatch(es); each branch sets the correct `action`/`variables`, then re-joins at `ExtractResult`.

4. **Emit workflow-level DCB events (P1, #4).**
   - At `Init`: emit `TRIAGE.CONTEXT.STARTED` with `{ repository, itemType }` (write to the `tamma:events` bag the way `TammaActivity`-based steps do, or via a small `EmitTriageEventActivity`).
   - On the success path before `SetOutput`: `TRIAGE.CONTEXT.COMPLETED` with `{ contextStatus, contextJsonLength, itemType }`.
   - On the failure path (step 2): `TRIAGE.CONTEXT.FAILED`. On the present-but-empty case: `TRIAGE.CONTEXT.EMPTY` (low severity).
   - Tags: `{ repository, itemSource: itemType, contextStatus }` — feeds AC9-style audit and makes the soft-fail visible.

5. **Robust item-type detection that the prompt consumes (P1, #5).**
   - Replace the substring sniff with `JsonDocument.Parse(itemJson)` reading the `type`/`advisory`/`cve` fields; default `issue` only when genuinely absent. Add a test with pretty-printed/whitespaced JSON.
   - Ensure the parsed `workItemType` is the value passed as `{{workItemType}}` in step 1 so the detection actually influences the scan.

6. **Thread `tenantId` (P1, #6).**
   - Add `var tenantId = builder.WithVariable<string>("TenantId", "")`, set it in `Init` from `ctx.GetInput<string>("tenantId")` (mirror `PlanGenerationWorkflow`), and add `["tenantId"] = tenantId.Get(ctx)` to the `GatherContext` dispatch input. Have `TriageItemCycleWorkflow` forward the tenant id it received into this sub-workflow's input.

7. **Fetch structured advisory/dependency signals for CVE items (P2, #7).**
   - For `security`/`dependency` items, before the scan, call an engine-callback (e.g. `GET /api/engine/security-alerts/{id}` / advisory detail) to fetch affected version range, fixed-in version, CVSS, and dependency chain; pass them as scan variables and include them in the `contextJson` so panel/PO reason over real CVE data, not free-form exploration. Route via engine-callback (mediation rule), never a direct registry/GitHub call.

8. **Idempotency / cache (P2, #8).**
   - Optional `CheckExistingTriageContextActivity` keyed `(repository, itemKey, bodyHash)`; on a fresh hit short-circuit to `SetOutput` with the cached `contextJson` and emit `TRIAGE.CONTEXT.CACHE_HIT`. Pairs with the cluster-level triage-state store proposed in the IssueTriage build-out.

9. **Tolerant extraction + schema validation (P2, #9).**
   - Replace the brace-slice with a balanced-brace scanner; validate the extracted object has the expected context shape (relevantFiles/dependencies/risks for issues; advisory/affected/fixed for security). On parse failure, set `contextStatus="unstructured"` and keep `rawContext` — never silently claim structured context.

10. **De-duplicate with `ContextGatheringWorkflow` (P3, #10).**
    - Extract a shared "init → context-scan dispatch (correct variables) → extract" builder used by both `context-gathering` and `triage-context-gathering` to prevent exactly the variable-name drift that caused #1.

### Suggested DCB event vocabulary to add
`TRIAGE.CONTEXT.STARTED`, `TRIAGE.CONTEXT.COMPLETED`, `TRIAGE.CONTEXT.FAILED`, `TRIAGE.CONTEXT.EMPTY`, `TRIAGE.CONTEXT.CACHE_HIT`.

---

## 7. Effort

**M.** The P0 set is small and self-contained — fix the four variable names, add one `success` `FlowDecision` + a failure terminal, and add start/complete/failed events — all within this one file plus the `TriageItemCycleWorkflow` join that consumes a non-success signal. P1 (item-type branch driving the real context dimensions, robust JSON parsing, tenant threading) adds modest surface. P2 (structured advisory fetch, cache) pulls in an engine-callback and a triage-state store and pushes the full build-out toward the upper end of M / into L, but the workflow is genuinely thin rather than a stub, so this is bounded enhancement on a correct mediation skeleton.
