# Completeness Audit — `ContextGatheringWorkflow`

**File:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/ContextGatheringWorkflow.cs`
**Definition ID:** `context-gathering`
**Audit date:** 2026-06-22
**Verdict:** **PARTIAL** — the core happy-path pipeline is real and correctly mediated, but it has several correctness/contract gaps (tenant scoping, silent-failure on store, synthetic-vs-real context IDs, no failure edge, no workflow-level DCB events) and is missing the adjacent "intended scope" pieces (research, ambiguity check, the richer assemble/budget context pipeline that already exists but is bypassed).

---

## 1. Purpose & owner

**Purpose (one line):** Build the per-issue context bundle that downstream loop steps (plan-generation, plan-review, code-gen) consume — by running a sequential, role-by-role LLM-mediated codebase scan, persisting each role's findings to the context store immediately, then producing a PO summary + link set + context-id handles.

**Owning epic/story:** This is the `CONTEXT_GATHERING` step of the **14-step autonomous loop** (`docs/architecture.md` §"Base 14-Step Workflow", line 830). It is consumed by `SingleIssueCycleWorkflow` (Story-2.x cycle). The LLM mediation it depends on is owned by **Epic 32** (revised agent architecture, `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md`) — specifically the `call-LLM` mediation (32-5) and tenant/BYOK threading (32-3/32-16). The context store / RAG side is **Epic 6** (intelligence/RAG wiring, open per MEMORY).

**Consumer contract (from `SingleIssueCycleWorkflow.cs` lines 124-151):** input `{ repository, issueNumber, workItemJson, conventions, tenantId }`; output `{ summary, contextIds, links }`.

---

## 2. Maturity: **PARTIAL**

Not a stub and not "thin" — it has a real 6-role accumulating pipeline (Dev → QA → Security → DevOps → Architect → PO), each role sees prior findings, each role's findings are persisted before the next scan, and **all LLM work is correctly routed through the `llm-call` sub-workflow** (it never calls a provider directly — the cross-cutting Epic-32 rule is already honored). That is more than a skeleton.

But it is not complete: it drops the `tenantId` the caller passes (breaks SaaS prompt + BYOK resolution), swallows store failures silently, emits synthetic context IDs that don't match what the store returns, has no failure/error edge, and emits no workflow- or scan-level DCB audit events. It also bypasses an already-built richer context pipeline (`AssembleContextActivity`, `ApplyBudgetActivity`, the `Fetch*` activities) and omits the research/ambiguity scope the architecture pairs with this step.

---

## 3. Current capabilities (what it does today)

- **Init** (`SetVariable "Init"`): reads `repository`, `issueNumber`, `workItemJson`; derives `workItemType` (feature/bug/security/test/docs) by substring-sniffing the work-item JSON.
- **5 role scans, sequential & accumulating** (`RoleScan` → `DispatchWorkflow("llm-call")` with `action=context-scan`, `enableTools=true`): Developer, Tester(QA), Security, Devops, Architect. Each scan receives the prior roles' findings as `previousFindings`, so context compounds.
- **Per-role extract** (`Extract` `SetVariable`): pulls `llmResponse` out of the `llm-call` result into a findings var (defaults to `"{}"` if absent).
- **Per-role store** (`StoreRoleFindingActivity`, `EventType=CONTEXT.STORE_ROLE`): POSTs `{repository, issueNumber, findings:{role:findings}}` to `{Engine:CallbackUrl}/api/engine/store-context`, appends a context id to the accumulated `contextIds` JSON array. Persists each role before the next scan runs (partial-progress durability).
- **PO review** (`DispatchWorkflow("llm-call")`, `role=product-owner`, `action=summarize-stakeholder`, `enableTools=false`): summarizes all five findings + context ids.
- **Extract PO** (`SetVariable "ExtractPO"`): stores raw response as `poSummary`; best-effort parses a `{...}` block to lift `summary` and `links`.
- **Set outputs**: `summary`, `contextIds`, `links` → `Finish`.
- **Mediation compliance:** every external effect goes through a sub-workflow/endpoint; the engine holds no provider key here. Good.
- **Activity-level events:** `StoreRoleFindingActivity` inherits `TammaAsyncActivity`, so it emits `CONTEXT.STORE_ROLE.STARTED/.COMPLETED/.FAILED` into the transient `tamma:events` bag.

---

## 4. Intended full scope (with citations)

1. **It is the `CONTEXT_GATHERING` step of the 14-step loop** (`docs/architecture.md` line 830). The architecture deliberately separates `CONTEXT_GATHERING` (831) from `RESEARCH` (831) and `AMBIGUITY_CHECK` (832) as distinct steps; the `hotfix` branch (line 878) skips RESEARCH + AMBIGUITY_CHECK but keeps CONTEXT_GATHERING — i.e. context gathering is the always-on substrate the others build on.
2. **A complete context-gathering step must persist real, retrievable handles.** The whole point of `contextIds` is that downstream steps (`plan-generation`, `plan-review`, `code-generation` in `SingleIssueCycleWorkflow`) re-hydrate the gathered context via those ids. `StoreFindingsActivity`'s own doc comment: *"Returns context IDs that downstream steps use to fetch relevant chunks."* IDs must come from the store, not be fabricated.
3. **Research + ambiguity** belong to the surrounding loop but interlock with context: PRD **FR-3** ("generate clarifying questions when encountering ambiguous specifications and wait for user approval") and **Story 3.4 Research Capability** (`docs/epics.md` lines 1088-1104: research unfamiliar concepts, cache 24h, log to event trail) and **Story 3.5/3.6 ambiguity detection** (lines 1108-1128). A "complete" context substrate should at minimum surface an ambiguity/confidence signal and a research-needs list for those steps, and cache scans.
4. **Tenant- and BYOK-correct mediation (Epic 32).** `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` §1: *"A workflow STEP MUST NEVER call an external API/provider directly … the engine never holds a provider key."* Honored. But the mediated `call-LLM` path resolves prompts **and credentials per-tenant** (32-3/32-16); the caller's `tenantId` MUST be threaded into every `llm-call` dispatch or SaaS resolves system-default prompts and the wrong/no credential. Per `CLAUDE.md` Prompt-Store §"Resolution Order — SaaS mode", resolution is `tenant → system`; the rule (`feedback_resolution_no_empty_fallback`) is **tenant → system → error, never empty/plain**.
5. **A richer assembled context already exists in-repo and is the intended target shape.** `AssembleContextActivity` + `ApplyBudgetActivity` + `Fetch{StoryMetadata,RecentCommits,FileContents,TestResults,SessionHistory,SimilarPatterns}Activity` produce a prioritized, size-budgeted `AssembledContext` (story metadata = Critical, similar patterns = Low/trim-first, purpose-driven priorities). A complete workflow should feed structured signals (commits, file contents, test results, similar patterns from the vector store) into the role scans and/or the final bundle — not rely solely on tool-enabled free-form LLM scans.
6. **Audit trail (DCB).** `CLAUDE.md` §"Emitting Events for Audit Trail": every operation emits events. The workflow currently emits store events but **no `CONTEXT.GATHERING.STARTED/COMPLETED/FAILED`** and no per-scan events, so the audit trail can't time-travel "what context drove this plan".

---

## 5. Missing capabilities (gap to complete)

| # | Missing capability | Priority | Depends on |
|---|---|---|---|
| 1 | **Thread `tenantId` into every `llm-call` dispatch (5 role scans + PO review) and into `StoreRoleFindingActivity`.** Caller passes it (`SingleIssueCycleWorkflow` line 135); workflow drops it entirely. Without it, SaaS resolves system-default prompts and (post-32) wrong/missing BYOK credentials, and store rows are mis-scoped. | **P0** | 32-3 / 32-16 (already-live `tenantId` input on `llm-call`) |
| 2 | **Real context IDs vs. synthetic.** `/api/engine/store-context` returns `{ ok, repository, issueNumber, storedAt }` — **no `contextIds`**. So `StoreRoleFindingActivity` always falls to the synthetic `ctx:{issue}:{role}`. Downstream steps receive handles the store can't resolve. Either the endpoint must return real ids or the contract must be redefined. | **P0** | Epic 6 (context store) / `IContextStore` |
| 3 | **Silent failure on store.** On HTTP non-2xx or exception, `StoreRoleFindingActivity` sets `ContextId=""` and the workflow proceeds; the empty id is still appended and the run reports success. Violates no-silent-failure / no-false-success. Need a real failure edge or an explicit degraded/partial outcome. | **P0** | none |
| 4 | **No failure/error edge for role scans or PO review.** Every `llm-call` dispatch ignores its `success=false` output; an all-providers-failed scan yields `"{}"` findings and the pipeline marches on to a "successful" PO summary built on empty context. Need per-scan success gate + a terminal failure path (DCB `CONTEXT.GATHERING.FAILED`). | **P0** | 32-5 (`call-LLM` already returns `success`) |
| 5 | **No workflow-/scan-level DCB events.** No `CONTEXT.GATHERING.STARTED/COMPLETED/FAILED`, no per-role `CONTEXT.SCAN.{ROLE}.*`. Audit/time-travel can't reconstruct what context produced a plan. | **P1** | none |
| 6 | **No idempotency / re-run guard.** Re-running for the same `(repository, issueNumber)` re-stores duplicate findings and re-spends LLM budget. No cache, no dedupe, no "already gathered" short-circuit (PRD Story 3.4 mandates 24h research caching as the pattern). | **P1** | Epic 6 (store), none for guard |
| 7 | **Structured signals bypassed.** `AssembleContextActivity` + `Fetch*` + `ApplyBudgetActivity` (prioritized, size-budgeted context with real commits/files/tests/similar-patterns) exist and are unused. Scans rely entirely on free-form tool-enabled LLM exploration with no token/size budget on the assembled bundle. | **P1** | Epic 6 (vector retrieval), none for assemble |
| 8 | **`previousFindings` accumulation is unbounded** (each later role gets all prior findings serialized inline). No compaction/budget → token blow-up on large issues. `ContextCompactor` exists in `Tamma.Activities/LlmCall/Tools` but isn't applied here. | **P1** | none |
| 9 | **No ambiguity / confidence / research-needs output.** The step feeds AMBIGUITY_CHECK and RESEARCH (architecture lines 831-832; PRD FR-3, Stories 3.4-3.6) but emits no ambiguity score, confidence, or "concepts to research" list. | **P2** | Story 3.4/3.5/3.6 |
| 10 | **`workItemType` detection is brittle** (substring sniffing exact `"type":"bug"` with no whitespace tolerance). A pretty-printed/whitespaced work item silently classifies as `feature`, skewing every role's scan focus. | **P2** | none |
| 11 | **PO summary / links parsing is best-effort & lossy.** `IndexOf('{')..LastIndexOf('}')` grabs the outermost braces; nested or trailing prose breaks it and `links` silently stays `[]`. No schema validation of the PO output. | **P2** | none |
| 12 | **Near-duplicate workflow drift.** `TriageContextGatheringWorkflow` is a single-scan sibling; no shared helper. Risk of divergence as either is fixed. | **P3** | none |

---

## 6. Ordered build-out spec (to reach complete & robust)

Steps are ordered so P0 correctness/contract fixes land first. Honor: tenant→system→error (never empty/plain), no silent-failure / no false-success, steps never call providers directly (route via `llm-call` / engine endpoints), emit DCB events.

1. **Add `TenantId` variable + thread it everywhere (P0, #1).**
   - Add `var tenantId = builder.WithVariable<string>("TenantId", "")` and set it in `Init` from `ctx.GetInput<string>("tenantId")` (mirror `PlanGenerationWorkflow` lines 51/77).
   - In `RoleScan` (both overloads) and the PO-review `DispatchWorkflow`, add `["tenantId"] = tenantId.Get(ctx)` to the dispatch input dict.
   - Add `TenantId` input to `StoreRoleFindingActivity` and forward it in `StoreRole(...)`; include it in the `store-context` POST body so rows are tenant-scoped.
   - **DCB:** unchanged here; verified by the existing `llm-call` tenant-resolution path.

2. **Make context IDs real, or redefine the contract (P0, #2).**
   - Preferred: extend `/api/engine/store-context` (`EngineEndpoints.StoreContext`) to return `{ contextIds: [...] }` from `IContextStore.StoreAsync` (the store must mint a stable id per `(tenant, repo, issue, role)`), and have `StoreRoleFindingActivity` read `result.contextIds[0]`.
   - If real ids aren't available yet (Epic 6 gap), make the synthetic id **explicit and deterministic** and stop pretending the store returned it: tag the output `synthetic=true` and emit a `CONTEXT.STORE_ROLE.DEGRADED` event so downstream/audit knows handles are not resolvable.
   - **Branch:** `store returned ids?` → real path; else → degraded path (event + flag), never silent.

3. **Per-role store failure edge — no silent failure (P0, #3).**
   - `StoreRoleFindingActivity` must surface failure as an **outcome** (convert to `TammaOutcomeActivity` with `Stored` / `StoreFailed`) instead of returning `""`.
   - In the flowchart, connect each `StoreX` `StoreFailed` outcome to a shared **`HandlePartialContext`** node that emits `CONTEXT.STORE_ROLE.FAILED` (already produced by the base) **plus** a workflow decision: continue-degraded vs. fail. Default per project rules: continue only if at least one role stored; otherwise route to terminal failure (step 5).

4. **Per-scan success gate (P0, #4).**
   - After each `RoleScan`, insert a `FlowDecision(ctx => llmResult.success == true)`.
   - `True` → `Extract` → `Store` as today. `False` → record a `CONTEXT.SCAN.{ROLE}.FAILED` event and route to `HandlePartialContext`.
   - The PO-review dispatch gets the same gate; a failed PO review must **not** emit a success output with an empty summary — it routes to terminal failure.

5. **Terminal failure path + workflow DCB events (P0/P1, #4/#5).**
   - Add an `EmitWorkflowEventActivity` (or a `SetVariable` writing to the `tamma:events` bag) at the start: `CONTEXT.GATHERING.STARTED` with `{ repository, issueNumber, tenantId, workItemType }`.
   - On the all-good path before `SetOutputs`: `CONTEXT.GATHERING.COMPLETED` with `{ rolesStored, contextIds.Count, poSummaryLength, degraded:bool }`.
   - Add a `FailGathering` terminal node (reached when zero roles stored OR PO review failed) that emits `CONTEXT.GATHERING.FAILED` and sets a `success=false` / `error` output so `SingleIssueCycleWorkflow` can branch (it currently assumes success). Per-scan events: `CONTEXT.SCAN.{ROLE}.STARTED/COMPLETED`.

6. **Idempotency / cache guard (P1, #6).**
   - Before the Dev scan, add a `CheckExistingContextActivity` calling `GET /api/engine/context?issueNumber=...&repository=...` (the `GetContext` endpoint already exists at `EngineEndpoints.GetContext`).
   - `FlowDecision(found && fresh within TTL)` → short-circuit to `SetOutputs` with the cached `summary`/`contextIds` and emit `CONTEXT.GATHERING.CACHE_HIT`. Default TTL = the 24h pattern from PRD Story 3.4. Else proceed to scans.

7. **Compact accumulated `previousFindings` (P1, #8).**
   - Wrap the `previousFindings` builder with `ContextCompactor` (already in `Tamma.Activities/LlmCall/Tools/ContextCompactor.cs`) so each later role gets a size-budgeted summary of prior findings, not the raw concatenation. Cap via a `MaxPreviousFindingsChars` variable.

8. **Wire the structured-signal pipeline into the bundle (P1, #7).**
   - Before/alongside the role scans, run the existing `Fetch*` activities (story metadata, recent commits, changed file contents, test results, similar patterns from the vector store) → `AssembleContextActivity` (purpose = `Assessment`/`Implementation`) → `ApplyBudgetActivity` to produce a prioritized, size-budgeted `AssembledContext`.
   - Pass the assembled bundle into each role scan's `variables` (e.g. `assembledContext`) so scans reason over real commits/files/tests/patterns, not just tool exploration. Store the assembled bundle id alongside the role findings.

9. **Emit ambiguity / research-needs signal (P2, #9).**
   - Have the PO-review action (or a dedicated `assess-ambiguity` action) return `{ summary, links, ambiguityScore, openQuestions[], researchNeeds[] }`.
   - Add outputs `ambiguityScore`, `openQuestions`, `researchNeeds` so the downstream `AMBIGUITY_CHECK` / `RESEARCH` steps (PRD FR-3, Stories 3.4-3.6) consume them rather than re-deriving. Emit `CONTEXT.AMBIGUITY.DETECTED` when score exceeds threshold.

10. **Robust `workItemType` detection (P2, #10).**
    - Parse `workItemJson` with `JsonDocument` and read the `type` property instead of substring-sniffing; default `feature` only when the field is genuinely absent. Add a unit test with whitespaced/pretty JSON.

11. **Schema-validate PO output (P2, #11).**
    - Replace the brace-slice with a tolerant JSON extractor (balanced-brace scan) and validate against a small schema (`summary` required). On parse failure, route to a one-shot `repair` `llm-call` (action returns strict JSON) rather than silently dropping `links`.

12. **De-duplicate with `TriageContextGatheringWorkflow` (P3, #12).**
    - Extract the shared "init → role-scan → extract → store" helper into a common builder used by both `context-gathering` and `triage-context-gathering` to prevent drift.

### Suggested DCB event vocabulary to add
`CONTEXT.GATHERING.STARTED`, `CONTEXT.GATHERING.COMPLETED`, `CONTEXT.GATHERING.FAILED`, `CONTEXT.GATHERING.CACHE_HIT`, `CONTEXT.SCAN.{ROLE}.STARTED/COMPLETED/FAILED`, `CONTEXT.STORE_ROLE.DEGRADED`, `CONTEXT.AMBIGUITY.DETECTED` (reuse the existing `CONTEXT.STORE_ROLE.*` from `StoreRoleFindingActivity`).

---

## 7. Effort

**L.** P0 set (tenant threading, real/explicit context ids, store + scan failure edges, terminal failure path + workflow DCB events) is a focused, self-contained pass touching this workflow + `StoreRoleFindingActivity` + the `store-context` endpoint. P1 (cache guard, compaction, assembled-pipeline wiring) and P2 (ambiguity output, robust parsing) add real surface area and pull in Epic 6 (vector store) and the research/ambiguity stories, pushing the full build-out toward L/XL — but the workflow is genuinely partial, not a stub, so this is bounded enhancement rather than greenfield.
