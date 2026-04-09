# Story 27-2: Prompt Store Service (TypeScript)

Status: ready-for-dev

## Story

As a **backend developer**,
I want a PostgreSQL-backed `PromptStore` class that resolves prompts using account-level overrides with system default fallback,
so that each account gets its own prompt configuration while inheriting sensible defaults from the platform.

## Acceptance Criteria

1. A new `PgPromptStore` class replaces the file-based `PromptStore` for database-backed deployments
2. `get(accountId, role, action)` returns the account override if it exists, otherwise falls back to the system default (`account_id IS NULL`)
3. `upsert(accountId, role, action, data)` creates or updates an account-specific prompt override, bumping the version number
4. `delete(accountId, role, action)` removes an account override (the system default remains available)
5. `list(accountId)` returns all resolved prompts for an account: account overrides merged with system defaults (overrides take precedence)
6. `render(accountId, role, action, variables)` resolves the prompt and interpolates `{{variable}}` placeholders in a single pass
7. System defaults are managed separately: `getSystemDefault(role, action)`, `upsertSystemDefault(role, action, data)`, `resetSystemDefault(role, action)` restores the hardcoded default from `default-prompts.ts`
8. An `InMemoryPromptStore` implementation exists for unit testing (implements the same interface)
9. The `IPromptStore` interface is defined in `packages/api/src/services/prompt-store.ts`, replacing the current class export
10. Resolution correctly handles the three-way distinction: `account_id IS NULL` (system default), `account_id = DEFAULT_TENANT_ID` (CLI/self-hosted overrides), and `account_id = <tenant-uuid>` (account overrides)
11. Template interpolation prevents recursive expansion (template injection safety), matching current behavior
12. Variable values exceeding 100 KB are rejected; rendered templates exceeding 1 MB are truncated
13. All methods use async/await with proper error handling; database errors are wrapped in `TammaError`
14. Backward compatibility: existing code that calls `store.get(role, action)` without accountId continues to work by defaulting to system defaults

## Technical Context

### Current Implementation

The existing `PromptStore` class in `packages/api/src/services/prompt-store.ts`:
- In-memory `Map<PromptKey, PromptTemplate>` (key = `"role:action"`)
- File-based JSON persistence at `./data/prompts.json`
- Lazy initialization: loads from file, then seeds defaults from `getDefaultPrompts()`
- Methods: `get(role, action)`, `upsert(role, action, input)`, `list()`, `render(role, action, input)`
- No concept of accounts, tenants, or multi-tenancy

### New Interface Design

```typescript
// packages/api/src/services/prompt-store.ts

export interface IPromptStore {
  // --- Account-scoped operations ---
  get(accountId: string | null, role: string, action: string): Promise<PromptTemplate | undefined>;
  upsert(accountId: string | null, role: string, action: string, input: UpsertPromptInput): Promise<PromptTemplate>;
  delete(accountId: string, role: string, action: string): Promise<boolean>;
  list(accountId: string | null): Promise<PromptSummary[]>;
  render(accountId: string | null, role: string, action: string, input: RenderInput): Promise<RenderedPrompt | undefined>;

  // --- System default operations ---
  getSystemDefault(role: string, action: string): Promise<PromptTemplate | undefined>;
  upsertSystemDefault(role: string, action: string, input: UpsertPromptInput): Promise<PromptTemplate>;
  resetSystemDefault(role: string, action: string): Promise<PromptTemplate | undefined>;
  listSystemDefaults(): Promise<PromptSummary[]>;

  // --- System prompts (role preambles) ---
  getSystemPrompt(accountId: string | null, role: string): Promise<string | undefined>;
  upsertSystemPrompt(accountId: string | null, role: string, prompt: string): Promise<void>;
}
```

### Resolution Algorithm

```
get(accountId, role, action):
  1. if accountId is not null:
     query: SELECT * FROM prompts WHERE account_id = $1 AND role = $2 AND action = $3
     if found → return it
  2. query: SELECT * FROM prompts WHERE account_id IS NULL AND role = $2 AND action = $3
     if found → return it
  3. return undefined
```

For `list(accountId)`:
```sql
-- Fetch all system defaults and account overrides in one query
SELECT DISTINCT ON (role, action)
  *
FROM prompts
WHERE account_id IS NULL OR account_id = $1
ORDER BY role, action, account_id NULLS LAST;
-- NULLS LAST means account override (non-NULL) sorts first → DISTINCT ON picks it
```

Wait -- `DISTINCT ON` with `NULLS LAST` would pick the NULL row. We need the account override to win:

```sql
SELECT DISTINCT ON (role, action)
  *
FROM prompts
WHERE account_id IS NULL OR account_id = $1
ORDER BY role, action,
  CASE WHEN account_id IS NOT NULL THEN 0 ELSE 1 END;
```

### Files to Create

| File | Purpose |
|------|---------|
| `packages/api/src/services/pg-prompt-store.ts` | PostgreSQL-backed implementation |
| `packages/api/src/services/in-memory-prompt-store.ts` | In-memory implementation for testing |
| `packages/api/src/services/prompt-store.test.ts` | Updated unit tests (replace existing) |
| `packages/api/src/services/pg-prompt-store.test.ts` | Integration tests for Pg implementation |

### Files to Modify

| File | Purpose |
|------|---------|
| `packages/api/src/services/prompt-store.ts` | Replace class with `IPromptStore` interface |
| `packages/api/src/index.ts` | Wire up `PgPromptStore` instead of `PromptStore` |

## Implementation Plan

### Step 1: Define the IPromptStore Interface

Extract the interface from the current class. Add `accountId` as the first parameter to all methods. Keep the existing types (`UpsertPromptInput`, `RenderInput`, `PromptSummary`, `RenderedPrompt`) and extend them as needed.

### Step 2: Implement InMemoryPromptStore

Port the current in-memory logic, adding account awareness. The in-memory store uses a `Map<string, PromptTemplate>` keyed by `"accountId:role:action"` (with `"null"` for system defaults).

### Step 3: Implement PgPromptStore

Connect to PostgreSQL via the existing `pg.Pool` instance. Each method maps to the corresponding SQL query.

Key queries:
- `get()`: Two queries with early return (account override, then system default)
- `upsert()`: `INSERT ... ON CONFLICT (account_id, role, action) DO UPDATE SET ...` with version bump
- `delete()`: `DELETE FROM prompts WHERE account_id = $1 AND role = $2 AND action = $3`
- `list()`: Single query with `DISTINCT ON` for merged view
- `render()`: Calls `get()` then applies interpolation

### Step 4: Implement resetSystemDefault

`resetSystemDefault(role, action)` re-inserts the hardcoded default from `getDefaultPrompts()`:
1. Find the matching template in `getDefaultPrompts()`
2. `upsertSystemDefault(role, action, template)` to overwrite the database row

### Step 5: Backward Compatibility Adapter

For callers that don't provide `accountId`, create overloaded signatures or a wrapper that passes `null` (resolves system defaults only).

### Step 6: Wire Up in Application

Replace `new PromptStore(options)` with `new PgPromptStore(pool, logger)` in `packages/api/src/index.ts`.

## Implementation Notes

1. The `PgPromptStore` constructor takes a `pg.Pool` instance (dependency injection), not a connection string. This follows the existing pattern in `PgTenantStore`, `PgInstallationStore`, etc.
2. All SQL queries use parameterized statements (`$1`, `$2`, ...) to prevent SQL injection.
3. The `upsert` method uses `INSERT ... ON CONFLICT DO UPDATE` which is atomic and avoids race conditions.
4. Version bumping: `version = EXCLUDED.version + 1` in the ON CONFLICT clause (or `prompts.version + 1` for existing rows).
5. The `render()` method reuses the existing `_interpolate()` logic from the current `PromptStore` class. This is extracted into a standalone `interpolateTemplate()` utility function.
6. The `InMemoryPromptStore` is the primary test double. Tests should verify behavior, not SQL.
7. `list()` uses `DISTINCT ON` which is PostgreSQL-specific. This is acceptable since Tamma targets PostgreSQL exclusively.

## Testing Strategy

### Unit Tests (InMemoryPromptStore)

1. `get(null, role, action)` returns system default
2. `get(accountId, role, action)` returns account override when it exists
3. `get(accountId, role, action)` falls back to system default when no account override exists
4. `upsert(accountId, role, action, input)` creates new override
5. `upsert(accountId, role, action, input)` updates existing override and bumps version
6. `delete(accountId, role, action)` removes override; subsequent `get()` returns system default
7. `delete(accountId, role, action)` returns false if override does not exist
8. `list(null)` returns all system defaults
9. `list(accountId)` returns merged view: account overrides + system defaults for non-overridden prompts
10. `render(accountId, role, action, variables)` interpolates variables correctly
11. `render()` tracks unresolved variables
12. `render()` truncates output exceeding 1 MB
13. Template injection safety: interpolated values containing `{{...}}` are not re-expanded
14. `resetSystemDefault()` restores hardcoded default
15. `getSystemPrompt(null, role)` returns system default role preamble
16. `getSystemPrompt(accountId, role)` returns account override when it exists

### Integration Tests (PgPromptStore)

17. Full CRUD cycle against test PostgreSQL: create, read, update, delete
18. `list(accountId)` returns correctly merged results
19. Concurrent `upsert()` calls do not produce duplicate rows
20. `delete()` on non-existent row returns false (no error)
21. `render()` end-to-end with Postgres-backed resolution

### Backward Compatibility

22. Existing prompt route tests pass with the new interface (with minor adapter adjustments)

## Dependencies

- **Story 27-1** (Prompt Store Database Schema) -- tables must exist
- Internal: `packages/api/src/services/default-prompts.ts` (for `getDefaultPrompts()`, `SYSTEM_PROMPTS`)
- Internal: `packages/api/src/services/prompt-store.ts` (replaced)

## Estimated Effort

| Task | Hours |
|------|-------|
| IPromptStore interface definition | 1 |
| InMemoryPromptStore implementation | 3 |
| PgPromptStore implementation | 4 |
| interpolateTemplate() utility extraction | 1 |
| Unit tests (16 tests) | 2 |
| Integration tests (5 tests) | 2 |
| Backward compatibility wiring | 1 |
| **Total** | **14 hours** |

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-04-08 | 1.0 | Initial story creation | Architecture Team |
