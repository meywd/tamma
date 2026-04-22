# Story 12-5d Implementation Plan — A/B Testing Hooks

**Status**: Planned (2026-04-20)
**Parent brief**: [`12-5-prompt-engineering-framework.md`](./12-5-prompt-engineering-framework.md) §12-5d
**Team**: Layer 4 Team D
**Branch**: `feat/story-12-5d-ab-testing`
**Worktree**: `/home/meywd/tamma-worktrees/layer-4-team-d-12-5d-ab-testing`

---

## 1. Objective

Add minimal infrastructure to run prompt variant experiments: two or
more rows for the same `(tenantId, role, action)` differ only by
`variantId`; the prompt store picks one deterministically based on
`hash(tenantId + sessionId) % variantCount`; the chosen variant is
recorded alongside `providerUsed` and `modelUsed` on every diagnostic
event. This is NOT a full A/B framework — there is no outcome
tracking, no statistical significance testing, and no rollout
controller. It is the hooks that let a prompt engineer surface the
data in the dashboard so they can make decisions by eyeballing it
until a full framework is justified.

## 2. Dependencies

Hard blockers:

- **Story 27-1** (prompt store database schema) — need a `variantId`
  column on the prompt-override row and a uniqueness constraint on
  `(tenantId, role, action, variantId)`.
- **Story 27-2** (prompt store service) — resolver must return the
  selected variant ID.
- **Story 27-3** (prompt store API endpoints) — admin UI writes
  variants via the existing endpoints (small DTO change).
- **Story 9-2** (diagnostics service) — diagnostic event schema takes
  a new `variantId` field.

Soft:

- **Story 27-4** (prompt store admin UI) — Team B can surface variant
  filters after this story ships; not required for this story's AC.

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Prompts/VariantSelector.cs` | Deterministic selector: `hash(tenantId + sessionId) % variants.Count`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Migrations/20260520000000_AddPromptVariantId.cs` | EF migration: add `VariantId TEXT NOT NULL DEFAULT 'default'` to `prompt_overrides`. Update unique index. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/Prompts/VariantSelectorTests.cs` | xUnit: hash determinism, uniform distribution, single-variant edge case. |
| `/home/meywd/tamma/docs/stories/epic-12/story-12-5/ab-testing-admin-howto.md` | 1-page "how to author a variant, how to read the dashboard" guide for prompt engineers. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Entities/PromptOverride.cs` | Add `VariantId` property (string, default `"default"`). |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs` | Update `HasIndex(...)` to include `VariantId` in the composite unique constraint. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Prompts/PromptResolver.cs` | Call `VariantSelector.Select(...)` to pick a variant; return `(template, variantId)`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/PromptsEndpoints.cs` | Accept `variantId` on PUT/POST; return all variants from GET when `?variants=all`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Dtos/Prompts/PromptOverrideDto.cs` | Add `variantId` field. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Diagnostics/DiagnosticsSchema.cs` | Add `variantId` (nullable) to the diagnostic event record. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs` | Capture the resolved `variantId` and pass it to the diagnostics record. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/DiagnosticsEndpoints.cs` | Extend the query API to filter by `variantId`. |

## 5. Sequence of changes

### Step 1 — Schema migration (1h)

- Add `VariantId TEXT NOT NULL DEFAULT 'default'` to `prompt_overrides`.
- Drop-and-recreate the unique index to include `VariantId`:
  `UNIQUE(tenant_id, role, action, variant_id)`.
- Write the migration so existing rows get `VariantId='default'`.
- Run `dotnet ef database update` against the test DB; assert existing
  integration tests still pass.
- **Commit**: `feat(prompts): add variantId column to prompt_overrides`.

### Step 2 — Entity + DTO (1h)

- Update `PromptOverride.cs` with the new property.
- Update DTO + request validators to accept optional `variantId` (treat
  missing as `"default"`).
- **Commit**: `feat(prompts): wire variantId through entity/DTO`.

### Step 3 — Variant selector (2h)

- `VariantSelector.Select(variants, tenantId, sessionId)`:
  1. If `variants.Count == 1` → return the only variant.
  2. Compute `hash = SHA-256(tenantId.ToString() + "|" + sessionId)`.
  3. Take first 8 bytes as a uint64; `index = hash % variants.Count`.
  4. Return `variants[index]`.
- xUnit:
  - Same (tenantId, sessionId) → same variant across calls (determinism).
  - Distribution test: 100k random sessions across 2 variants → 49±2k each.
  - Single variant → always returns that variant.
  - Empty variants → throws `InvalidOperationException`.
- **Commit**: `feat(prompts): deterministic variant selector`.

### Step 4 — Resolver integration (2h)

- `PromptResolver.Resolve(tenantId, role, action, sessionId)`:
  - Load all rows for `(tenantId, role, action)` where not soft-deleted.
  - If `.Count > 1`, call `VariantSelector.Select`; else use the only row.
  - Fall through to system default if zero rows.
  - Return `(template, variantId)` tuple.
- Integration test with 3 variants; assert selection stickiness over
  session's life.
- **Commit**: `feat(prompts): resolver picks variant by hash`.

### Step 5 — Endpoint updates (2h)

- `PUT /api/prompts/:role/:action` now accepts optional
  `?variantId=v2-concise` query param (or body field).
- `GET /api/prompts/:role/:action` returns the single resolved variant.
- New `GET /api/prompts/:role/:action/variants` returns all variants.
- `DELETE /api/prompts/:role/:action/variants/:variantId` removes one.
- Permission: same as existing prompt-admin (owner/admin).
- **Commit**: `feat(api): prompt variant CRUD endpoints`.

### Step 6 — Diagnostics (2h)

- Schema: add `variantId` (nullable string) to the diagnostic record.
- `CallLlmInlineActivity` passes `resolvedVariantId` into the record.
- `DiagnosticsEndpoints` supports `?variantId=` filter and a
  `GROUP BY variantId` aggregation (`GET /api/v1/diagnostics/variants/summary`).
- xUnit: diagnostic round-trip with variant ID; aggregation returns
  per-variant totals.
- **Commit**: `feat(diagnostics): record resolved variantId`.

### Step 7 — Admin how-to doc (1h)

- Author `ab-testing-admin-howto.md`: step-by-step create two variants,
  how to monitor in the dashboard, how to roll back.
- **Commit**: `docs(prompts): A/B testing admin how-to`.

### Step 8 — Integration test (1h)

- Seed two variants for `coder/implement`.
- Fire 100 workflows from a single session with varying session IDs.
- Assert ~50/50 split in `GET /api/v1/diagnostics/variants/summary`.
- **Commit**: `test(integration): variant distribution across sessions`.

## 6. Test strategy

### Unit tests

- `VariantSelectorTests` (6 cases): single variant, two variants
  distribution, determinism, empty input error, hash stability across
  platforms (SHA-256 is deterministic — cross-platform safe).
- `PromptResolverTests` — extend existing suite with 4 variant cases.

### Integration tests

- Seed two variants, run 1000 calls, assert per-variant call counts
  within ±5%.
- Variant deletion: creating variant A and variant B, deleting B,
  assert resolver returns A for all sessions.

### Regression

- Existing tenants with no variants (only `default`) resolve unchanged.
- `GET /api/prompts/:role/:action` without `?variantId=` returns the
  same shape as before (adds a new `variantId` field; pre-existing
  clients ignore unknown JSON fields).

## 7. Rollback plan

- **Schema migration**: single additive column + index rewrite. Revert
  migration script: drops the index, drops the column. Safe on
  low-volume data.
- **Feature flag**: `Prompts:EnableVariants` (default `true` at ship;
  flipping to `false` makes the resolver ignore non-default variants
  and route everything to `variantId='default'`).
- **Revertable commits**: each step independent. Migration rollback
  script stored in the story worktree.
- **Non-reversible**: tenants that create variants during soak retain
  them across a rollback (the DB column survives). Consequence:
  benign; the feature flag hides them.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Schema migration | 1 |
| 2. Entity + DTO | 1 |
| 3. Variant selector | 2 |
| 4. Resolver integration | 2 |
| 5. Endpoint CRUD | 2 |
| 6. Diagnostics recording | 2 |
| 7. Admin how-to | 1 |
| 8. Integration test | 1 |
| **Total** | **12** (matches brief) |

## 9. Open questions

- **Is `sessionId` the right slicing key?** Alternative: `userId`. Using
  session makes the experiment stickier for the session but drops
  stickiness across sessions. Using user keeps stickiness across
  sessions but means two simultaneous sessions by the same user see
  different variants. Plan: use `sessionId` by default; admin UI
  exposes a "stickiness scope" dropdown in a future story. Open for
  Team B feedback.
- **How does the diagnostics dashboard render per-variant stats?** Not
  this story's problem; just expose the filter + aggregation
  endpoint. Team B's Story 27-4 admin UI picks it up.
- **What about variant rollout ramping (e.g. 10% → 50% → 100%)?** Out
  of scope per brief ("not the full A/B framework"). Would require a
  new `variant_weights` table and a weighted sampler. Tracked as a
  future P2 story.
- **What prevents prompt engineers from creating 20 variants with
  confusing IDs?** No constraint today. Soft guard: admin UI can
  enforce "variant IDs must match `[a-z0-9-]+` and be ≤ 32 chars".
  Recommend adding that regex to the DTO validator. Open for Team B.
- **Outcome signal integration with 12-5b's success classifier.**
  When 12-5b's `SuccessSignal` fires, it should tag the stored example
  with its `variantId`. Depending on sequencing (12-5b ships first
  per priority), this is a small follow-up PR that adds the tag to
  `StoreFewShotExampleActivity`. Tracked as a short-term follow-up.
