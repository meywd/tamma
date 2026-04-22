# Story 12-5b Implementation Plan — Few-Shot Example Injection

**Status**: Planned (2026-04-20)
**Parent brief**: [`12-5-prompt-engineering-framework.md`](./12-5-prompt-engineering-framework.md) §12-5b
**Team**: Layer 4 Team D
**Branch**: `feat/story-12-5b-few-shot`
**Worktree**: `/home/meywd/tamma-worktrees/layer-4-team-d-12-5b-fewshot`

---

## 1. Objective

Capture successful `(input, output)` pairs from completed LLM calls,
embed them into ChromaDB with per-tenant namespaces, and inject the top
1–3 most-similar historical examples into new prompts before the
user's request. Enables self-improving prompts without changing
template text: the model sees examples of how the same role handled
similar tasks successfully in the past. All storage and retrieval is
tenant-isolated — a helpful example from tenant A must never surface in
tenant B's prompt.

## 2. Dependencies

Hard blockers:

- **Story 27-2** (prompt store service) — templates must carry a
  `{{fewShotExamples}}` variable (author via template metadata).
- **Epic 6** (ChromaDB integration in `packages/intelligence/`) — we
  reuse the existing `VectorStore` client rather than hand-rolling one.
- **Story 17-2 / Story 28-3** (tenant scoping) — ChromaDB collection
  names must embed the tenant ID to enforce isolation.
- **Story 12-7a** (vector DB search tools) — overlaps; we reuse the
  `SearchCodeSemanticTool` wiring and embedding pipeline. Implement
  12-7a first; this story is a thin additional consumer.

Soft:

- **Story 12-5a** (priority truncation) — few-shot examples become
  `NORMAL` priority by default so truncation drops them before CRITICAL
  content.

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/packages/intelligence/src/few-shot/few-shot-store.ts` | Stores successful `(input, output)` pairs with role + tenant tags in ChromaDB. |
| `/home/meywd/tamma/packages/intelligence/src/few-shot/few-shot-retriever.ts` | Queries ChromaDB for top-K similar examples by embedding similarity. |
| `/home/meywd/tamma/packages/intelligence/src/few-shot/success-signal.ts` | Determines which completed calls are "successful enough" to store (e.g. workflow step passed CI, PR merged, QA signed off). |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Activities/FewShot/StoreFewShotExampleActivity.cs` | Post-call Elsa activity invoked at success points; POSTs to the C# API proxy for intelligence. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Activities/FewShot/InjectFewShotExamplesActivity.cs` | Pre-call Elsa activity; fetches top-K and stashes them in the `{{fewShotExamples}}` workflow variable. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/FewShotEndpoints.cs` | Proxy endpoints (same pattern as 12-7e bridge): `POST /api/v1/few-shot/store`, `POST /api/v1/few-shot/retrieve`. |
| `/home/meywd/tamma/packages/intelligence/src/few-shot/few-shot-store.test.ts` | Vitest: store round-trip, tenant isolation, duplicate detection. |
| `/home/meywd/tamma/packages/intelligence/src/few-shot/few-shot-retriever.test.ts` | Vitest: top-K ranking, per-tenant filter, empty-collection handling. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Activities.Tests/FewShot/StoreFewShotExampleActivityTests.cs` | xUnit: activity wires correctly, only stores on success signal. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Activities.Tests/FewShot/InjectFewShotExamplesActivityTests.cs` | xUnit: injection formats correctly for each role template. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/packages/intelligence/src/vector-store.ts` | Export a `getFewShotCollection(tenantId, role)` helper that returns the tenant-scoped ChromaDB collection handle. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs` | After each successful activity (ImplementActivity, TestsPassActivity, ReviewPassedActivity), dispatch `StoreFewShotExampleActivity`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs` | Before `CallLlmInlineActivity`, dispatch `InjectFewShotExamplesActivity` that populates `{{fewShotExamples}}`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Prompts/PromptTemplateRenderer.cs` | Recognise `{{fewShotExamples}}` placeholder and render as a formatted block (XML tags). |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.ElsaServer/ServiceRegistration.cs` | Register HTTP client for the few-shot bridge endpoints (reuses 12-7e's `ContextToolsApi` client with a `FewShotApi` alias pointing at the same sidecar). |
| `/home/meywd/tamma/packages/intelligence-server/src/routes/few-shot.ts` | New route on the intelligence sidecar (see 12-7e for bridge architecture). |

## 5. Sequence of changes

### Step 1 — Success signal classifier (2h)

- Implement `SuccessSignal.shouldStoreExample(context)` that returns
  `true` only when:
  - The Elsa workflow completed without entering the escalation path.
  - The PR passed CI on first try (no debug retries).
  - If a reviewer approved (for reviewer-relevant roles).
- Lookup matrix per role (stored as a JSON config file).
- Unit tests: 8 cases across roles and workflow states.
- **Commit**: `feat(few-shot): success signal classifier`.

### Step 2 — Few-shot store (TS side) (3h)

- `FewShotStore.store(tenantId, role, input, output, metadata)`:
  1. Embeds `input` via existing `VectorStore` embedding pipeline.
  2. Writes to collection `tamma_fewshot_tenant_${tenantId}_role_${role}`.
  3. Deduplicates by input hash (SHA-256 of normalised input).
  4. Trims collection to N=1000 rows per (tenant, role) with LRU eviction.
- Vitest: round-trip; cross-tenant query returns zero; dedup works.
- **Commit**: `feat(intelligence): few-shot store with tenant scoping`.

### Step 3 — Few-shot retriever (TS side) (2h)

- `FewShotRetriever.retrieve(tenantId, role, input, k=3)`:
  1. Embed input.
  2. Query collection `tamma_fewshot_tenant_${tenantId}_role_${role}`
     with `n_results=k`.
  3. Filter by similarity threshold (default 0.7 cosine).
  4. Return `[{ input, output, similarity, storedAt }, ...]`.
- Vitest: top-K ordering is correct; below-threshold returns empty.
- **Commit**: `feat(intelligence): few-shot retriever`.

### Step 4 — Intelligence sidecar routes (2h)

- Add `POST /few-shot/store` and `POST /few-shot/retrieve` to the
  intelligence sidecar (`packages/intelligence-server/src/routes/few-shot.ts`).
- Both accept JSON body + require `X-Tenant-Id` header (enforced by
  sidecar middleware).
- Response shapes mirror the TS store/retriever signatures.
- **Commit**: `feat(intelligence-server): few-shot routes`.

### Step 5 — C# API proxy (2h)

- `FewShotEndpoints.cs`:
  - `POST /api/v1/few-shot/store` → forwards to sidecar with `X-Tenant-Id`.
  - `POST /api/v1/few-shot/retrieve` → same pattern.
- Timeout: 3s (matches 12-7e); on timeout, return 202 Accepted with empty
  examples (store is fire-and-forget) or 200 with `examples=[]` (retrieve
  gracefully degrades).
- Integration test: use Testcontainers Postgres + a fake sidecar.
- **Commit**: `feat(api): few-shot proxy endpoints`.

### Step 6 — Store activity + workflow hooks (3h)

- `StoreFewShotExampleActivity.cs`:
  - Inputs: tenantId (from workflow context), role, input, output.
  - Calls `IFewShotClient.StoreAsync(...)`; errors logged but don't fail workflow.
- Hook into `SingleIssueCycleWorkflow` after success markers.
- xUnit: activity invoked only on success states; failed-state runs don't store.
- **Commit**: `feat(activities): store few-shot examples on success`.

### Step 7 — Inject activity + template hook (3h)

- `InjectFewShotExamplesActivity.cs`:
  - Inputs: tenantId, role, current user input.
  - Calls `IFewShotClient.RetrieveAsync(tenantId, role, input, k=3)`.
  - Formats as XML block:
    ```xml
    <few-shot-examples>
      <example similarity="0.82">
        <input>...</input>
        <output>...</output>
      </example>
      ...
    </few-shot-examples>
    ```
  - Stores in workflow variable `FewShotExamples`.
- `PromptTemplateRenderer`: render `{{fewShotExamples}}` as the XML block
  (or empty string if no examples).
- Unit test: rendering with 0, 1, 3 examples; correct XML escaping.
- **Commit**: `feat(activities): inject few-shot examples into prompt`.

### Step 8 — Template metadata + diagnostics (2h)

- Update system-default prompt templates for roles `coder`, `tester`,
  `reviewer`, `mentor` to include `{{fewShotExamples}}` near the top
  (before the current task).
- Emit a `FEW_SHOT.INJECTED.SUCCESS` diagnostic event per injection with
  `{ count, avgSimilarity, role }`.
- **Commit**: `feat(prompts): add fewShotExamples to default templates`.

### Step 9 — Integration test (1h)

- End-to-end test: run a workflow, complete it successfully, observe
  the example is stored; start another workflow with similar input,
  assert the example is retrieved and injected.
- **Commit**: `test(integration): few-shot end-to-end`.

## 6. Test strategy

### Unit (TS Vitest)

- `SuccessSignalTests` — 8 cases (each role × success/failure matrix).
- `FewShotStoreTests` — round-trip, dedup by input hash, LRU trim at N=1000,
  tenant isolation (store in A, query B returns empty).
- `FewShotRetrieverTests` — top-K ranking, similarity threshold, empty
  collection returns empty.

### Unit (C# xUnit)

- `StoreFewShotExampleActivityTests` — activity wiring, error swallow.
- `InjectFewShotExamplesActivityTests` — XML formatting, empty case.
- `FewShotEndpointsTests` — proxy forwards headers, timeout degrades.

### Integration

- Full ChromaDB + sidecar + C# API round-trip using Testcontainers.
- Performance: retrieve p95 < 200ms (ChromaDB embedding + query).

### Regression

- Pre-existing prompt templates without `{{fewShotExamples}}` render
  unchanged — no accidental injection.

## 7. Rollback plan

- **Feature flag**: `FewShot:Enabled` (default `false` at first ship;
  flip to `true` after 1-week soak).
- **Data rollback**: the ChromaDB collections are standalone — dropping
  them affects only the few-shot feature. No cross-feature impact.
- **Revertable commits**: each step independent. The template edits
  in step 8 are the only non-code change; revert by reverting the
  default-prompts commit.
- **Non-reversible**: ChromaDB rows written during soak are kept (dev
  data, low risk). A `scripts/few-shot-drop-collections.sh` convenience
  script ships for emergency teardown.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Success signal | 2 |
| 2. Few-shot store (TS) | 3 |
| 3. Few-shot retriever (TS) | 2 |
| 4. Intelligence sidecar routes | 2 |
| 5. C# API proxy | 2 |
| 6. Store activity + workflow hooks | 3 |
| 7. Inject activity + template | 3 |
| 8. Template metadata + diagnostics | 2 |
| 9. Integration test | 1 |
| **Total** | **20** (matches brief) |

## 9. Open questions

- **What embeds the input — user input only, or the full rendered
  prompt?** User input only; embedding the full prompt would include
  the template text and bias similarity toward the role's boilerplate.
  Confirmed with research.
- **Where does the 1000-row-per-(tenant, role) cap come from?** First-pass
  guess to avoid unbounded ChromaDB growth on self-hosted tenants. Team
  D to revisit once production usage settles — may become per-tier
  (free = 100, pro = 1000, enterprise = unlimited).
- **Do we include negative examples?** Not in this story. `SuccessSignal`
  only fires on positive outcomes. A future story (P3) may add
  negative examples (anti-patterns) with role-specific formatting
  (e.g. "Do NOT do this:").
- **Similarity threshold default (0.7).** Arbitrary; needs tuning via
  the diagnostics captured in step 8. First-week ops review should
  either confirm or adjust.
- **Cross-tenant leakage via embedding fingerprints.** If two tenants
  submit identical input, their embeddings are identical — is that a
  data-leak risk? No: the collection is per-tenant, so the other
  tenant's collection never returns a match even if the embedding is
  identical. Documented in security audit checklist (Layer 5 §5.3).
