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
