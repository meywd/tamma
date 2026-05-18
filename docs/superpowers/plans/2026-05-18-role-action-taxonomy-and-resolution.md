# Role/Action Taxonomy & Resolution — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Produce the Epic 27 story set, architecture, and wiki documentation for the shared typed `(role, action)` taxonomy + exact-lookup prompt/convention resolution model that replaces keyword matching.

**Architecture:** This is a documentation-production plan. Deliverables are markdown artifacts: 3 rewritten Epic 27 stories (27-8/27-9/27-13), 5 new stories (27-15..27-19), reconciliation of 4 dependent stories, and updates to the epic README, migration ordering, `docs/architecture.md`, and the wiki. The canonical source of truth is the committed design spec `docs/superpowers/specs/2026-05-18-role-action-taxonomy-and-resolution-design.md`; stories operationalize it and cite its sections rather than duplicating the full taxonomy.

**Tech Stack:** Markdown only. Verification = `grep` consistency checks (no stale keyword-model references, story numbers consistent, internal links resolve). One commit per task.

**Spec reference:** `docs/superpowers/specs/2026-05-18-role-action-taxonomy-and-resolution-design.md` (sections cited as SPEC §N).

**Scope guard:** Initiative (1) only. The `SingleIssueCycleWorkflow` roundabout (initiative 2) is referenced as a downstream consumer but **no task plans it**.

---

## File Structure

| File | Responsibility | Action |
|---|---|---|
| `docs/stories/epic-27/27-15-agent-role-action-taxonomy.md` | Enums + RolePhaseMap rebuild | Create |
| `docs/stories/epic-27/27-16-taxonomy-codegen.md` | Codegen: prompt + convention seed | Create |
| `docs/stories/epic-27/27-17-taxonomy-drift-build-test.md` | Build-time drift guard | Create |
| `docs/stories/epic-27/27-18-prompt-store-taxonomy-reshape.md` | Flat 8×10 → jagged taxonomy | Create |
| `docs/stories/epic-27/27-19-dispatch-site-migration.md` | ~21 sites → `AgentAction.X.ToWire()` | Create |
| `docs/stories/epic-27/27-8-convention-store-database-schema.md` | Keyed `conventions` table; drop keyword model | Rewrite |
| `docs/stories/epic-27/27-9-convention-store-service.md` | Single keyed-fetch resolver | Rewrite |
| `docs/stories/epic-27/27-13-convention-store-elsa-integration.md` | Exact `(role,action)` fetch activity | Rewrite |
| `docs/stories/epic-27/27-10..12,27-14*.md` | Remove keyword-model assumptions | Patch |
| `docs/stories/epic-27/README.md` | Story table, deps, estimates, architecture prose | Update |
| `docs/stories/migration-ordering.md` | Migration 018 redefinition | Update |
| `docs/architecture.md` | New "Role/Action Taxonomy & Resolution" section | Update |
| `wiki/Role-Action-Taxonomy.md` | Canonical wiki page | Create |
| `wiki/Architecture.md`, `wiki/Workflow-LLM-Call.md`, `wiki/Agent-Dispatch.md`, `wiki/Epics.md`, `wiki/Stories.md` | Cross-reference the new model | Update |

**Story numbering:** epic-27 currently ends at 27-14. New stories are 27-15 through 27-19. No numbers reused.

**Dependency order (and task order):** 27-15 (foundation) → 27-16 (codegen) → 27-17 (drift test) → 27-18 (prompt reshape) → 27-19 (dispatch migration) → 27-8 → 27-9 → 27-13 → dependent-story reconcile → README → migration-ordering → architecture.md → wiki.

---

## Task 1: New story 27-15 — AgentRole/AgentAction taxonomy + RolePhaseMap rebuild

**Files:**
- Create: `docs/stories/epic-27/27-15-agent-role-action-taxonomy.md`

- [ ] **Step 1: Write the story file**

Create the file with exactly this content:

```markdown
# Story 27-15: AgentRole/AgentAction Taxonomy + RolePhaseMap Rebuild

## Story

As the Tamma platform, I need a single typed `(role, action)` taxonomy owned by
`RolePhaseMap`, so that prompts, conventions, agent resolution, provider
routing, and workflow dispatch all key off the same canonical vocabulary and
cannot drift.

Canonical design: see `docs/superpowers/specs/2026-05-18-role-action-taxonomy-and-resolution-design.md` (SPEC §3, §4).

## Priority

P0 (Critical) — foundation for 27-8, 27-9, 27-13, 27-16, 27-18, 27-19.

## Dependencies

None (pure code-defined types; no DB, no Epic 17 dependency).

## Acceptance Criteria

1. `AgentRole` enum exists with exactly: `Developer, Tester, Security, Devops,
   Architect, ProductOwner, SeniorDeveloper, TechWriter`.
2. `AgentAction` enum exists as the **union of all distinct action tokens** in
   SPEC §4 (~70 distinct values). Shared tokens (`context-scan`, `code-review`,
   `plan-review`, `write-tests`) are single enum values reused across roles.
3. `AgentRole.ToWire()` / `AgentAction.ToWire()` return the canonical
   kebab/snake string (`PlanSystemDesign` → `"plan-system-design"`,
   `ProductOwner` → `"product_owner"`). One mapping table, one place.
4. `AgentRole.Parse(string)` / `AgentAction.Parse(string)`: apply
   `RolePhaseMap.LegacyRoleAliases` first (`"implementer"`→`Developer`,
   `"analyst"`→`ProductOwner`), then exact match; throw `TammaError`
   (code `INVALID_ROLE` / `INVALID_ACTION`) on unknown.
5. Round-trip invariant holds for every enum value: `Parse(x.ToWire()) == x`.
6. Wire format remains a primitive string — no `JsonConverter`, no change to
   Elsa serialized dispatch payloads or persisted workflow state.
7. `RolePhaseMap` is rebuilt on the enums:
   - `ValidRoles` / `ValidActions` derive from `Enum.GetValues<>()`.
   - The per-role action set from SPEC §4 replaces `s_eligibleRoles`.
   - `IsRoleEligibleForPhase(role, action)` returns "is `action` in `role`'s
     SPEC §4 set".
   - `GetPrimaryActionForRole`, normalization, legacy aliases keep current
     observable behaviour, keyed off enums.
8. The four existing consumers (`AgentResolverService`, `ProviderChainResolver`,
   `AgentEndpoints`, `DefaultAgentConfig`) compile unchanged and exhibit
   identical observable behaviour (regression tests pass).

## Technical Context

- Files: create `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentRole.cs`,
  `AgentAction.cs`; modify
  `apps/tamma-elsa/src/Tamma.Api/Services/Agents/RolePhaseMap.cs`.
- The per-role action lists are the authority for codegen (Story 27-16) and the
  drift test (Story 27-17). They are reproduced verbatim from SPEC §4 in the
  enum/RolePhaseMap source as the single code-side source of truth.
- No DB tables. Roles/actions stay code-defined (SPEC §2).

## Estimate

8 hours.
```

- [ ] **Step 2: Verify the taxonomy matches the spec**

Run: `grep -c '`' docs/stories/epic-27/27-15-agent-role-action-taxonomy.md && grep -n 'SPEC §4' docs/stories/epic-27/27-15-agent-role-action-taxonomy.md`
Expected: file exists, contains the SPEC §4 citation, AC 1 lists the 8 exact roles.

- [ ] **Step 3: Verify role list exactly matches spec §4 role headings**

Run: `grep -oE 'Developer, Tester, Security, Devops,\n?\s*Architect, ProductOwner, SeniorDeveloper, TechWriter' docs/stories/epic-27/27-15-agent-role-action-taxonomy.md`
Expected: match found (8 roles, exact spelling).

- [ ] **Step 4: Commit**

```bash
git add docs/stories/epic-27/27-15-agent-role-action-taxonomy.md
git commit -m "docs(epic-27): add story 27-15 — AgentRole/AgentAction taxonomy + RolePhaseMap rebuild"
```

---

## Task 2: New story 27-16 — Taxonomy codegen (prompt + convention seed)

**Files:**
- Create: `docs/stories/epic-27/27-16-taxonomy-codegen.md`

- [ ] **Step 1: Write the story file**

Create the file with exactly this content:

```markdown
# Story 27-16: Taxonomy Codegen — Prompt Seed + Convention Seed

## Story

As the Tamma build, I need a codegen step that generates both the prompt-store
seed and the convention-store seed from the single Story 27-15 taxonomy, so the
two seeds share keys and cannot drift.

Canonical design: SPEC §3.4.

## Priority

P0 (Critical).

## Dependencies

Story 27-15 (the enums + RolePhaseMap per-role sets are the codegen input).

## Acceptance Criteria

1. A codegen tool reads the Story 27-15 per-role action sets and emits:
   - The prompt-store seed: one row per `(role, action)` cell in SPEC §4
     (jagged ~80 cells), each with a system-default template body placeholder
     keyed by `(role, action)`.
   - The convention-store seed: one row per `(role, action)` cell with a
     system-default convention body keyed by `(role, action)`.
2. Both seeds use the identical `(role, action)` key set — asserted equal by
   the generator (fail generation if the two key sets differ).
3. Generated SQL is idempotent (`INSERT ... ON CONFLICT DO NOTHING`).
4. Codegen output is deterministic (stable ordering) so diffs are reviewable.
5. The generator runs in CI and the working tree must be clean afterwards
   (generated files committed; CI fails if regeneration produces a diff).
6. Transitional generic-action cells (SPEC §3.5) are emitted as ordinary seed
   rows, marked with a comment `-- transitional: remove when dispatch site
   specialised (initiative 2)`.

## Technical Context

- Generates: `database/migrations/018_convention_store.sql` (seed portion) and
  the prompt-store seed migration (Story 27-1's seed portion / `SystemPrompts.cs`
  reshape per Story 27-18).
- The generator is the only writer of seed rows; hand-edited seed rows are
  forbidden (enforced by AC 5).

## Estimate

10 hours.
```

- [ ] **Step 2: Verify**

Run: `grep -n 'SPEC §3.4\|ON CONFLICT DO NOTHING\|initiative 2' docs/stories/epic-27/27-16-taxonomy-codegen.md`
Expected: all three strings present.

- [ ] **Step 3: Commit**

```bash
git add docs/stories/epic-27/27-16-taxonomy-codegen.md
git commit -m "docs(epic-27): add story 27-16 — taxonomy codegen for prompt + convention seed"
```

---

## Task 3: New story 27-17 — Taxonomy drift build test

**Files:**
- Create: `docs/stories/epic-27/27-17-taxonomy-drift-build-test.md`

- [ ] **Step 1: Write the story file**

Create the file with exactly this content:

```markdown
# Story 27-17: Taxonomy Drift Build Test

## Story

As the Tamma build, I need a test that fails the build when any compiled
workflow dispatch site emits a `(role, action)` not in the Story 27-15
taxonomy, so drift between workflows and the taxonomy is impossible to ship.

Canonical design: SPEC §3.4, §7.

## Priority

P0 (Critical).

## Dependencies

Story 27-15 (taxonomy), Story 27-19 (dispatch sites emit `AgentAction.X.ToWire()`).

## Acceptance Criteria

1. A test enumerates every `["action"]` / `["role"]` value passed at the ~21
   `llm-call` dispatch sites (after Story 27-19 migration these are
   `AgentAction.X.ToWire()` / `AgentRole.X.ToWire()` expressions).
2. The test asserts every emitted `(role, action)` ∈ the Story 27-15 taxonomy
   and that the role is eligible for the action per the rebuilt RolePhaseMap.
3. The test asserts the `Parse(x.ToWire()) == x` round-trip for every
   `AgentRole` and `AgentAction` value.
4. The test asserts the prompt seed key set == the convention seed key set
   (codegen output equality, SPEC §3.4).
5. Failure breaks the build (runs in the standard `dotnet test` CI gate).
6. The test lists, on failure, exactly which dispatch site / which pair drifted.

## Technical Context

- Test project: `apps/tamma-elsa/tests/Tamma.Activities.Tests/` (workflow
  structure test area, alongside existing `WorkflowStructureTests.cs`).
- Dispatch-site enumeration: reflect over the compiled workflow assembly or
  parse the known 21 sites; the design spec lists them as the audit set.

## Estimate

6 hours.
```

- [ ] **Step 2: Verify**

Run: `grep -n 'SPEC §3.4, §7\|round-trip\|breaks the build' docs/stories/epic-27/27-17-taxonomy-drift-build-test.md`
Expected: all present.

- [ ] **Step 3: Commit**

```bash
git add docs/stories/epic-27/27-17-taxonomy-drift-build-test.md
git commit -m "docs(epic-27): add story 27-17 — taxonomy drift build test"
```

---

## Task 4: New story 27-18 — Prompt store taxonomy reshape

**Files:**
- Create: `docs/stories/epic-27/27-18-prompt-store-taxonomy-reshape.md`

- [ ] **Step 1: Write the story file**

Create the file with exactly this content:

```markdown
# Story 27-18: Prompt Store Taxonomy Reshape

## Story

As the Tamma platform, I need the prompt store reshaped from the flat 8×10
matrix to the jagged ~80-cell SPEC §4 taxonomy, so prompts and conventions key
off the identical `(role, action)` taxonomy.

Canonical design: SPEC §1.2, §3.3, §4, §5.

## Priority

P0 (Critical).

## Dependencies

Story 27-15 (taxonomy), Story 27-16 (codegen), Story 27-1 (prompt store schema —
the `prompts` table already keys by `(tenant_id, role, action)`; only the seed
content and the action vocabulary change, not the schema).

## Acceptance Criteria

1. `SystemPrompts.cs` `RoleActionTemplates` is regenerated from the SPEC §4
   taxonomy via Story 27-16 codegen — jagged per-role cells, not flat 8×10.
2. `ActionDefaults` (the 10 generic action templates) is removed; there is no
   generic action fallback tier (SPEC §3.3). Transitional generic cells, where
   still needed pre-initiative-2, exist as ordinary `(role, action)` seed rows
   (SPEC §3.5), not as a separate defaults layer.
3. `RoleSystemPrompts` (8 role identity preambles) is retained, keyed by
   `AgentRole`.
4. Prompt resolution remains exact `(tenant_id, role, action)` lookup:
   tenant override → system default → `TammaError` for a taxonomy-valid pair
   with no row (no silent empty).
5. `ResolvePromptFromRegistryActivity` keys lookups by the taxonomy-validated
   `(role, action)` (via `AgentRole.Parse`/`AgentAction.Parse` at the boundary).
6. Story 27-1 / 27-2 docs are annotated: the schema is unchanged; the seed and
   action vocabulary are governed by Story 27-15/27-16.

## Technical Context

- Modify: `apps/tamma-elsa/src/Tamma.Api/Auth/SystemPrompts.cs`,
  `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolvePromptFromRegistryActivity.cs`.
- No migration number change for the prompt store (schema stable; seed only).

## Estimate

12 hours.
```

- [ ] **Step 2: Verify**

Run: `grep -n 'SPEC §1.2, §3.3, §4, §5\|ActionDefaults.*removed\|exact .tenant_id, role, action. lookup' docs/stories/epic-27/27-18-prompt-store-taxonomy-reshape.md`
Expected: spec citation + the ActionDefaults-removal AC present.

- [ ] **Step 3: Commit**

```bash
git add docs/stories/epic-27/27-18-prompt-store-taxonomy-reshape.md
git commit -m "docs(epic-27): add story 27-18 — prompt store taxonomy reshape"
```

---

## Task 5: New story 27-19 — Dispatch-site migration

**Files:**
- Create: `docs/stories/epic-27/27-19-dispatch-site-migration.md`

- [ ] **Step 1: Write the story file**

Create the file with exactly this content:

```markdown
# Story 27-19: Workflow Dispatch-Site Migration

## Story

As the Tamma workflows, I need the ~21 `llm-call` dispatch sites to emit
`AgentRole.X.ToWire()` / `AgentAction.X.ToWire()` instead of raw string
literals, so dispatched `(role, action)` pairs are compile-time safe and
guaranteed to be in the taxonomy.

Canonical design: SPEC §3.1, §5; verified audit set = 21 sites across 14 files.

## Priority

P0 (Critical).

## Dependencies

Story 27-15 (enums). Enables Story 27-17 (drift test).

## Acceptance Criteria

1. Every `["action"] = "<literal>"` and `["role"]/["agentRole"] = "<literal>"`
   at the 21 `llm-call` dispatch sites is replaced with
   `AgentAction.X.ToWire()` / `AgentRole.X.ToWire()`.
2. Legacy aliases at dispatch (`"implementer"`, `"analyst"`) are replaced with
   the canonical enum (`AgentRole.Developer`, `AgentRole.ProductOwner`); wire
   output is canonical (`"developer"`, `"product_owner"`).
3. Dynamic role-loop dispatch (`["role"] = role` in `ReviewRoles` arrays in
   `PlanReviewWorkflow`, `TaskReviewWorkflow`, `TriagePanelReviewWorkflow`,
   and the `ContextGatheringWorkflow` RoleScan param) iterates `AgentRole`
   values and emits `.ToWire()`.
4. Sites that currently emit a *specific* action keep emitting it; sites that
   only know the generic action emit the generic enum value (transitional,
   SPEC §3.5) — no behaviour change, only type-safety.
5. The constructed dynamic role `"po-decision-round-{n}"` in
   `PlanReviewWorkflow` is NOT a taxonomy role; document it as a session
   identifier passed via a different input, not the `(role, action)` key
   (no change required, just annotate to prevent false drift-test failures).
6. Wire output is byte-identical to today for every site (regression: existing
   suspended workflow instances unaffected; SPEC §7).

## Technical Context

- 14 files under `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/`:
  ContextGathering, TriageContextGathering, PlanGeneration, PlanReview,
  TaskCreation, TaskReview, TestCaseCreation, DeploymentPipeline,
  TriagePanelReview, TriagePODecision, ReviewFix, Debugging, plus the
  `ReviewRoles` arrays and RoleScan helper.
- `ReviewFix`/`Debugging` currently dispatch `agentRole="implementer"` with NO
  `["action"]` key — add the correct `AgentAction` per SPEC §4 mapping table
  (developer/`address-review-comments`, developer/`debug`).

## Estimate

10 hours.
```

- [ ] **Step 2: Verify**

Run: `grep -n 'SPEC §3.1, §5\|po-decision-round\|byte-identical' docs/stories/epic-27/27-19-dispatch-site-migration.md`
Expected: all present.

- [ ] **Step 3: Commit**

```bash
git add docs/stories/epic-27/27-19-dispatch-site-migration.md
git commit -m "docs(epic-27): add story 27-19 — workflow dispatch-site migration"
```

---

## Task 6: Rewrite story 27-8 — convention store schema (keyed table, no keywords)

**Files:**
- Modify (full rewrite): `docs/stories/epic-27/27-8-convention-store-database-schema.md`

- [ ] **Step 1: Overwrite the file with the new schema story**

Replace the entire file content with:

```markdown
# Story 27-8: Convention Store Database Schema + Migration

## Story

As the Tamma platform, I need a `conventions` table keyed by
`(tenant_id, role, action)` mirroring the prompt store, so conventions resolve
by exact `(role, action)` lookup with tenant override — no keyword matching.

Canonical design: SPEC §1, §3.3, §4. **The keyword model is deleted, not
migrated.**

## Priority

P1 (High).

## Dependencies

Epic 17 (tenants table for FK), Story 27-15 (taxonomy), Story 27-16 (seed
codegen). Uses migration **018**.

## Acceptance Criteria

1. A `conventions` table exists: `id` (UUID PK), `tenant_id` (UUID nullable,
   FK `tenants(id)`), `role` (TEXT NOT NULL), `action` (TEXT NOT NULL),
   `body` (TEXT NOT NULL), `version` (INT NOT NULL DEFAULT 1), `enabled`
   (BOOL NOT NULL DEFAULT true), `created_at`, `updated_at`, `created_by`
   (UUID nullable), `updated_by` (UUID nullable).
2. `UNIQUE (tenant_id, role, action)` — one convention per pair per tenant;
   `tenant_id IS NULL` = system default (partial unique index handling NULL
   as a distinct value, same pattern as prompts).
3. **No `convention_keywords` table. No `match_mode`, `always_apply`,
   `priority`, `category` columns. No keyword B-tree index.** These are
   explicitly removed from the design.
4. B-tree index on `conventions(tenant_id, role, action)` (the resolution hot
   path is a single index seek).
5. **No RLS** (exempt — same rationale as prompts; resolution crosses tenant
   boundary to read system defaults).
6. Seed: one system-default row per SPEC §4 `(role, action)` cell, generated by
   Story 27-16 codegen (jagged ~80 rows), idempotent
   `INSERT ... ON CONFLICT DO NOTHING`. No keyword rows (none exist).
7. Migration 018 is online-safe (DEFAULT values, no table-lock seed).

## Technical Context

- File: `database/migrations/018_convention_store.sql`.
- The seed body content per cell is generated, not hand-written (Story 27-16).
- `ConventionTemplates.cs` (the 46 starter templates) is unaffected — it
  remains the `GET /api/convention-templates` starter catalogue, unrelated to
  the resolution `conventions` table.

## Estimate

7 hours (down from 10.5 — keyword table/index/seed removed).
```

- [ ] **Step 2: Verify the keyword model is fully removed**

Run: `grep -niE 'convention_keywords|match_mode|always_apply|tokenize|keyword' docs/stories/epic-27/27-8-convention-store-database-schema.md`
Expected: matches appear ONLY in the negative ACs ("No `convention_keywords`...", "No keyword rows"). No positive use.

- [ ] **Step 3: Commit**

```bash
git add docs/stories/epic-27/27-8-convention-store-database-schema.md
git commit -m "docs(epic-27): rewrite 27-8 — keyed conventions table, delete keyword model"
```

---

## Task 7: Rewrite story 27-9 — single keyed-fetch resolver

**Files:**
- Modify (full rewrite): `docs/stories/epic-27/27-9-convention-store-service.md`

- [ ] **Step 1: Overwrite the file**

Replace the entire file content with:

```markdown
# Story 27-9: Convention Store Service (C#)

## Story

As the Tamma engine, I need an `IConventionStore` that resolves a convention by
exact `(tenant_id, role, action)` lookup with tenant override, so `{{conventions}}`
is populated by the same model as the prompt store.

Canonical design: SPEC §3.3. **Both matchers from the prior draft (the
`WHERE keyword IN (@terms)` set-membership and the `Regex.IsMatch(\b…\b)`) are
deleted. There is no tokenizer.**

## Priority

P1 (High).

## Dependencies

Story 27-8 (schema), Story 27-15 (taxonomy types).

## Acceptance Criteria

### Core CRUD
1. `GetAsync(tenantId, role, action)` returns the resolved convention or null.
2. `UpsertAsync` / `DeleteAsync` operate on tenant-override rows; system
   defaults (`tenant_id IS NULL`) are not mutable via tenant operations.
3. `ListAsync(tenantId)` returns resolved conventions for all taxonomy cells.

### Resolution (replaces the entire keyword algorithm)
4. `ResolveAsync(tenantId, AgentRole role, AgentAction action)`:
   a. Select the tenant-override row `WHERE tenant_id = @tenantId AND
      role = @role AND action = @action AND enabled = true`.
   b. Else select the system-default row `WHERE tenant_id IS NULL AND
      role = @role AND action = @action AND enabled = true`.
   c. Else throw `TammaError(CONVENTION_NOT_FOUND)` — a taxonomy-valid pair
      must have a seeded row (codegen guarantees this; absence = bug).
5. Resolution is a single index seek on `(tenant_id, role, action)`. No
   tokenisation, no keyword query, no merge/concat, no `match_mode` post-filter,
   no `always_apply` union.
6. `ConventionResolution` contains: `Body` (the single row body), `Source`
   (`"tenant"` | `"system"`), `Role`, `Action`. No `Triggered`/`Skipped`
   keyword lists (no keywords exist).
7. `ConventionResolution.Body` is what substitutes into `{{conventions}}`.

### Edge Cases
8. Unknown role/action string at the boundary → `AgentRole.Parse` /
   `AgentAction.Parse` throws before resolution (fail-fast, SPEC §3.1).
9. `enabled = false` tenant override → falls through to system default
   (a tenant disabling its override reverts to system, it does not blank).

## Technical Context

- Files: create
  `apps/tamma-elsa/src/Tamma.Api/Services/Conventions/IConventionStore.cs`,
  `ConventionStore.cs`.
- Interface:
  ```
  Task<Convention?> GetAsync(Guid? tenantId, AgentRole role, AgentAction action, CancellationToken ct);
  Task UpsertAsync(Guid tenantId, AgentRole role, AgentAction action, string body, Guid userId, CancellationToken ct);
  Task DeleteAsync(Guid tenantId, AgentRole role, AgentAction action, CancellationToken ct);
  Task<ConventionResolution> ResolveAsync(Guid? tenantId, AgentRole role, AgentAction action, CancellationToken ct);
  Task<IReadOnlyList<ConventionSummary>> ListAsync(Guid? tenantId, CancellationToken ct);
  ```
- `LlmCallContext` is no longer used for convention resolution (it had no Role
  field; that whole approach is removed).

## Estimate

8 hours (down from 15.5 — no keyword engine).
```

- [ ] **Step 2: Verify both matchers and the tokenizer are gone**

Run: `grep -niE 'tokenize|Regex\.IsMatch|keyword IN|match_mode|always_apply|LlmCallContext' docs/stories/epic-27/27-9-convention-store-service.md`
Expected: matches only inside the negative statements ("are deleted", "no tokenizer", "no longer used"). No positive algorithm use.

- [ ] **Step 3: Verify the resolution AC is exact-lookup**

Run: `grep -n 'single index seek\|CONVENTION_NOT_FOUND\|tenant-override row' docs/stories/epic-27/27-9-convention-store-service.md`
Expected: all present.

- [ ] **Step 4: Commit**

```bash
git add docs/stories/epic-27/27-9-convention-store-service.md
git commit -m "docs(epic-27): rewrite 27-9 — single keyed-fetch resolver, delete both matchers"
```

---

## Task 8: Rewrite story 27-13 — ResolveConventionsActivity (exact (role,action) fetch)

**Files:**
- Modify (full rewrite): `docs/stories/epic-27/27-13-convention-store-elsa-integration.md`

- [ ] **Step 1: Overwrite the file**

Replace the entire file content with:

```markdown
# Story 27-13: Convention Store Elsa Integration

## Story

As `LlmCallWorkflow`, I need a `ResolveConventionsActivity` that fetches the
convention for the resolved `(role, action)` at the prompt-pull boundary, so
`{{conventions}}` is populated from the convention store.

Canonical design: SPEC §3.3, §6. **No composite action string, no tokenizer,
no `LlmCallContext`-based matching.**

## Priority

P1 (High).

## Dependencies

Story 27-9 (resolver), Story 27-15 (taxonomy), Story 27-6 (prompt-pull
boundary in `LlmCallWorkflow`).

## Acceptance Criteria

1. `ResolveConventionsActivity` runs at the same boundary as
   `ResolvePromptFromRegistryActivity` inside `LlmCallWorkflow` (intra-workflow,
   after the role/action strings are read from the dispatch bag).
2. It calls `AgentRole.Parse(roleInput)` and `AgentAction.Parse(actionInput)`
   (fail-fast on unknown), then `IConventionStore.ResolveAsync(tenantId,
   role, action)`.
3. The resolved `ConventionResolution.Body` overrides the `{{conventions}}`
   template variable. If the convention store has no row for a taxonomy-valid
   pair it throws (codegen guarantees a row; absence = bug, not silent empty).
4. `ReadRepoConventionsActivity` remains only as the legacy fallback source for
   repos with an explicit `.tamma/config.json` `conventions` field; the store
   result takes precedence when present.
5. A `CONVENTIONS.RESOLVED.SUCCESS` event is emitted with `tenantId`, `role`,
   `action`, `source` (`tenant`/`system`), `chars`. (No keyword/trigger fields.)
6. No `agentRole + "/" + taskAction` composite is constructed anywhere
   (that approach is abandoned — SPEC §1.1).

## Technical Context

- Files: create
  `apps/tamma-elsa/src/Tamma.Activities/Context/ResolveConventionsActivity.cs`;
  modify `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs`
  (wire the activity at the prompt-pull boundary), DI registration.
- Initiative (2) (`SingleIssueCycleWorkflow` roundabout) is a downstream
  consumer: it will choose the `(role, action)` dynamically and pass it through
  the *same* dispatch inputs this activity reads — no change to this activity
  required when (2) lands.

## Estimate

8 hours (down from 14 — no composite/tokenizer wiring).
```

- [ ] **Step 2: Verify**

Run: `grep -niE 'composite|tokenize|LlmCallContext|keyword' docs/stories/epic-27/27-13-convention-store-elsa-integration.md`
Expected: only inside negative statements ("No composite", "abandoned"). 

- [ ] **Step 3: Commit**

```bash
git add docs/stories/epic-27/27-13-convention-store-elsa-integration.md
git commit -m "docs(epic-27): rewrite 27-13 — ResolveConventionsActivity exact (role,action) fetch"
```

---

## Task 9: Reconcile dependent convention stories 27-10/27-11/27-12/27-14

**Files:**
- Modify: `docs/stories/epic-27/27-10-convention-store-api-endpoints.md`
- Modify: `docs/stories/epic-27/27-11-convention-store-admin-ui.md`
- Modify: `docs/stories/epic-27/27-12-convention-store-tenant-ui.md`
- Modify: `docs/stories/epic-27/27-14-convention-store-event-sourcing.md`

- [ ] **Step 1: Find every keyword-model reference in the four files**

Run: `grep -niE 'keyword|match_mode|always_apply|tokenize|priority|category|convention_keywords' docs/stories/epic-27/27-10-convention-store-api-endpoints.md docs/stories/epic-27/27-11-convention-store-admin-ui.md docs/stories/epic-27/27-12-convention-store-tenant-ui.md docs/stories/epic-27/27-14-convention-store-event-sourcing.md`
Expected: a list of lines to fix. Record each.

- [ ] **Step 2: Patch each occurrence**

For each file, apply these rules (edit in place):
- Any CRUD/endpoint keyed by `key`/`slug`/keyword → re-key by `(role, action)`.
- Remove any UI field / API field / event field for `keywords`, `match_mode`,
  `always_apply`, `priority`, `category`.
- Replace "keyword resolution" / "keyword match" prose with "exact
  `(role, action)` lookup with tenant override (SPEC §3.3)".
- 27-14 event sourcing: convention events carry `(role, action, source)` not
  triggered/skipped keyword lists.
- Add to each file's Dependencies line: `Story 27-15 (taxonomy)`.
- Add a note at the top of each: `> Updated 2026-05-18: keyword model removed;
  see SPEC docs/superpowers/specs/2026-05-18-role-action-taxonomy-and-resolution-design.md`.

- [ ] **Step 3: Verify no positive keyword-model references remain**

Run: `grep -niE 'keyword|match_mode|always_apply|tokenize' docs/stories/epic-27/27-10-convention-store-api-endpoints.md docs/stories/epic-27/27-11-convention-store-admin-ui.md docs/stories/epic-27/27-12-convention-store-tenant-ui.md docs/stories/epic-27/27-14-convention-store-event-sourcing.md`
Expected: zero matches, OR matches only inside an explicit "removed: ..." note.

- [ ] **Step 4: Commit**

```bash
git add docs/stories/epic-27/27-10-convention-store-api-endpoints.md docs/stories/epic-27/27-11-convention-store-admin-ui.md docs/stories/epic-27/27-12-convention-store-tenant-ui.md docs/stories/epic-27/27-14-convention-store-event-sourcing.md
git commit -m "docs(epic-27): reconcile 27-10/11/12/14 to (role,action) model"
```

---

## Task 10: Update epic-27 README (story table, deps, estimates, architecture prose)

**Files:**
- Modify: `docs/stories/epic-27/README.md`

- [ ] **Step 1: Add the 5 new stories to the story table**

In the story table (after the `27-14` row, ~line 144), add:

```markdown
| 27-15 | AgentRole/AgentAction Taxonomy + RolePhaseMap Rebuild | P0 (Critical) | None | Planned |
| 27-16 | Taxonomy Codegen (Prompt + Convention Seed) | P0 (Critical) | Story 27-15 | Planned |
| 27-17 | Taxonomy Drift Build Test | P0 (Critical) | Story 27-15, 27-19 | Planned |
| 27-18 | Prompt Store Taxonomy Reshape | P0 (Critical) | Story 27-15, 27-16, 27-1 | Planned |
| 27-19 | Workflow Dispatch-Site Migration | P0 (Critical) | Story 27-15 | Planned |
```

- [ ] **Step 2: Update the convention store dependency rows**

Change rows 27-8/27-9/27-13 Dependencies column to add `Story 27-15`. Update
their titles/notes to drop "keyword" wording.

- [ ] **Step 3: Replace the keyword-model architecture prose**

Find the section describing convention keyword matching / `convention_keywords`
/ tokenize (the "Convention Store" architecture/design-constraint paragraphs,
incl. design constraint #3 and the "Cross-Cutting Requirements → Convention
Store" block). Replace with:

```markdown
### Convention Store (Stories 27-8 to 27-19)

Conventions are resolved by **exact `(role, action)` lookup with tenant
override**, mirroring the prompt store — there is no keyword matching, no
`convention_keywords` table, no tokenizer. The `(role, action)` vocabulary is
the single shared taxonomy owned by `RolePhaseMap` (Story 27-15), consumed
identically by prompts and conventions; both seeds are codegen'd from it
(Story 27-16) and a build test prevents drift (Story 27-17). See
`docs/superpowers/specs/2026-05-18-role-action-taxonomy-and-resolution-design.md`.
```

- [ ] **Step 4: Update the estimates table**

Adjust: 27-8 → 7h, 27-9 → 8h, 27-13 → 8h; add 27-15 (8h), 27-16 (10h),
27-17 (6h), 27-18 (12h), 27-19 (10h). Recompute the total line.

- [ ] **Step 5: Verify**

Run: `grep -nE '27-1[5-9]' docs/stories/epic-27/README.md && grep -niE 'keyword|tokenize' docs/stories/epic-27/README.md`
Expected: 27-15..27-19 present; keyword/tokenize only inside the "no keyword matching" sentence.

- [ ] **Step 6: Commit**

```bash
git add docs/stories/epic-27/README.md
git commit -m "docs(epic-27): README — add 27-15..27-19, replace keyword-model prose"
```

---

## Task 11: Update migration-ordering.md (migration 018 redefinition)

**Files:**
- Modify: `docs/stories/migration-ordering.md:55`

- [ ] **Step 1: Replace the migration 018 row**

Replace the `| 018 | ... |` row (currently describing `convention_keywords`
+ keyword B-tree + 46 defaults + ~190 keyword rows) with:

```markdown
| 018 | `018_convention_store.sql` | 27-8 | Create `conventions` table keyed by `(tenant_id, role, action)` (UNIQUE, partial-null for system defaults), B-tree index on `(tenant_id, role, action)`. Seed one system-default row per taxonomy `(role, action)` cell, codegen'd from the Story 27-15 taxonomy (Story 27-16). **No `convention_keywords` table. No RLS** (exempt — same as prompts). FK to `tenants(id)` on `tenant_id`. | 008 (tenants), 27-15 (taxonomy) |
```

- [ ] **Step 2: Update the migration dependency graph note** (if `018` appears in the graph block) to drop the "convention_keywords" label.

- [ ] **Step 3: Verify**

Run: `grep -n '018' docs/stories/migration-ordering.md | grep -iE 'convention_keywords|keyword'`
Expected: zero matches (no keyword wording remains for 018).

- [ ] **Step 4: Commit**

```bash
git add docs/stories/migration-ordering.md
git commit -m "docs: redefine migration 018 — keyed conventions table, no keyword table"
```

---

## Task 12: Update docs/architecture.md (new Role/Action Taxonomy & Resolution section)

**Files:**
- Modify: `docs/architecture.md` (insert a new `##` section before `## Implementation Patterns (AI Agent Consistency)` at line 986)

- [ ] **Step 1: Insert the new section**

Immediately before the line `## Implementation Patterns (AI Agent Consistency)`,
insert:

```markdown
## Role/Action Taxonomy & Prompt/Convention Resolution

Tamma resolves both **prompts** and **coding conventions** by exact
`(role, action)` lookup against a single shared, code-defined taxonomy owned by
`RolePhaseMap`. There is no keyword matching, no tokenizer, no composite action
string.

- **Taxonomy:** `AgentRole` (8 roles) × per-role specific actions (~80 jagged
  cells, e.g. architect/`plan-system-design`, developer/`plan-implementation`).
  Strong-typed via `AgentRole`/`AgentAction` enums with `ToWire()`/`Parse()`;
  the wire format stays a primitive string (Elsa serialization back-compat).
- **Resolution:** at the prompt-pull boundary in `LlmCallWorkflow`,
  `(role, action)` resolves tenant-override → system-default → error. Prompts
  and conventions use the identical key.
- **Anti-drift:** prompt seed and convention seed are codegen'd from the one
  taxonomy; a build test fails if any workflow dispatch site emits a pair
  outside it.
- **Scope note:** dynamic selection of `(role, action)` per issue context
  (the `SingleIssueCycleWorkflow` "roundabout") is a separate initiative that
  consumes this model unchanged.

Full design: `docs/superpowers/specs/2026-05-18-role-action-taxonomy-and-resolution-design.md`.

```

- [ ] **Step 2: Verify insertion placement and links**

Run: `grep -n 'Role/Action Taxonomy & Prompt/Convention Resolution\|Implementation Patterns (AI Agent Consistency)' docs/architecture.md`
Expected: the new section heading appears immediately before the Implementation Patterns heading.

- [ ] **Step 3: Commit**

```bash
git add docs/architecture.md
git commit -m "docs(architecture): add Role/Action Taxonomy & Resolution section"
```

---

## Task 13: Create wiki/Role-Action-Taxonomy.md

**Files:**
- Create: `wiki/Role-Action-Taxonomy.md`

- [ ] **Step 1: Write the wiki page**

Create the file with:

```markdown
# Role/Action Taxonomy & Resolution

Tamma keys **prompts** and **coding conventions** off one shared, code-defined
taxonomy: `AgentRole` × per-role specific `AgentAction`. Resolution is an exact
`(role, action)` lookup (tenant override → system default), performed at the
prompt-pull step of the [LLM Call workflow](Workflow-LLM-Call.md).

## Why not keywords?

A bare action like `plan` is ambiguous — *plan what?* The **role** answers it:
architect + `plan` → plan a system design; developer + `plan` → plan an
implementation/fix. The meaningful unit is the `(role, action)` pair, so
resolution is a keyed fetch, not keyword matching.

## Taxonomy

8 roles: developer, tester, security, devops, architect, product_owner,
senior_developer, tech_writer. Each role has its own specific action set
(~80 jagged cells total). Shared tokens (`context-scan`, `code-review`,
`plan-review`) repeat across roles; the role half of the key disambiguates.

See the canonical list and rationale in the design spec:
`docs/superpowers/specs/2026-05-18-role-action-taxonomy-and-resolution-design.md`.

## Guarantees

- **Strong-typed:** `AgentRole`/`AgentAction` enums; wire format is a plain
  string (`PlanSystemDesign` → `"plan-system-design"`).
- **No drift:** prompt + convention seeds are generated from the taxonomy; a
  build test rejects any workflow dispatching a pair outside it.
- **Code-defined:** roles/actions are not in the database; tenant
  customization is per-`(role, action)` convention/prompt overrides only.

## Related

- [LLM Call Workflow](Workflow-LLM-Call.md)
- [Agent Dispatch](Agent-Dispatch.md)
- [Epics](Epics.md) — Epic 27 stories 27-8..27-19
```

- [ ] **Step 2: Verify links resolve**

Run: `for p in Workflow-LLM-Call Agent-Dispatch Epics; do test -f wiki/$p.md && echo "$p OK" || echo "$p MISSING"; done`
Expected: all three OK.

- [ ] **Step 3: Commit**

```bash
git add wiki/Role-Action-Taxonomy.md
git commit -m "docs(wiki): add Role-Action-Taxonomy page"
```

---

## Task 14: Update wiki cross-pages

**Files:**
- Modify: `wiki/Architecture.md`, `wiki/Workflow-LLM-Call.md`, `wiki/Agent-Dispatch.md`, `wiki/Epics.md`, `wiki/Stories.md`

- [ ] **Step 1: Architecture.md — add a link + one-paragraph summary**

Append to `wiki/Architecture.md` a subsection:

```markdown
## Role/Action Taxonomy & Resolution

Prompts and conventions resolve by exact `(role, action)` lookup against one
shared code-defined taxonomy (no keyword matching). See
[Role/Action Taxonomy](Role-Action-Taxonomy.md).
```

- [ ] **Step 2: Workflow-LLM-Call.md — correct the resolution description**

Find any text describing prompt/convention resolution via keywords/repo-config
and replace with: "At the prompt-pull step, prompt and conventions are resolved
by exact `(role, action)` lookup (tenant override → system default). See
[Role/Action Taxonomy](Role-Action-Taxonomy.md)." Remove any
`{{conventions}}`-from-keyword wording.

- [ ] **Step 3: Agent-Dispatch.md — add a cross-reference**

Add, near the role/action discussion: "The `(role, action)` vocabulary is the
single shared taxonomy — see [Role/Action Taxonomy](Role-Action-Taxonomy.md)."

- [ ] **Step 4: Epics.md / Stories.md — list the new stories**

In the Epic 27 area of each, add stories 27-15..27-19 with one-line
descriptions matching the README table from Task 10 Step 1. If a stale
"convention keyword" description exists for 27-8/27-9/27-13, replace it with
the keyed-lookup description.

- [ ] **Step 5: Verify no stale keyword wording in the wiki**

Run: `grep -rniE 'convention.{0,3}keyword|keyword.{0,3}match|tokenize' wiki/ | grep -viE 'no keyword|not.*keyword|without keyword'`
Expected: zero matches (all keyword references are now negative/removed).

- [ ] **Step 6: Commit**

```bash
git add wiki/Architecture.md wiki/Workflow-LLM-Call.md wiki/Agent-Dispatch.md wiki/Epics.md wiki/Stories.md
git commit -m "docs(wiki): cross-reference Role/Action taxonomy; remove stale keyword wording"
```

---

## Self-Review

**Spec coverage** (SPEC § → task):
- §1 problem / §1.1 findings → Tasks 6,7,8 (rewrites encode the fixes)
- §1.2 keyword-model reframe → Tasks 6,7,8,9,10,11
- §1.3 greenfield → reflected in rewrites (no migration of keyword model)
- §2 decision model → Task 1 (27-15)
- §3.1 strong types → Tasks 1, 5
- §3.2 RolePhaseMap authority → Task 1
- §3.3 exact lookup, deletions → Tasks 6, 7, 8
- §3.4 codegen + anti-drift → Tasks 2, 3
- §3.5 generic = transitional → Tasks 2, 4, 5 (explicit AC each)
- §4 taxonomy → Task 1 (source), Tasks 2/4 (consume)
- §5 components → Tasks 1–8 (each names exact files)
- §6 (1)↔(2) seam → Tasks 8, 12, 13 (scope note repeated, never planned)
- §7 testing → Task 3 (drift/round-trip/coverage)
- §8 out of scope → scope guard honored; no task plans initiative (2)

No gaps found.

**Placeholder scan:** No "TBD/TODO/handle appropriately". Story bodies cite
SPEC §-numbers for the canonical taxonomy (a committed in-repo source of truth,
not a placeholder) and inline all acceptance criteria verbatim.

**Type consistency:** `AgentRole`/`AgentAction`, `ToWire()`/`Parse()`,
`IConventionStore.ResolveAsync(Guid?, AgentRole, AgentAction, CancellationToken)`,
`ConventionResolution { Body, Source, Role, Action }`,
`TammaError(CONVENTION_NOT_FOUND)` / `INVALID_ROLE` / `INVALID_ACTION`,
migration `018` — used consistently across Tasks 1, 7, 8, 11.

No issues found on re-review.
```
