# Story 17-3: Tenant-Scoped Event Store — Implementation Plan

**Story file**: [`17-3-tenant-scoped-event-store.md`](./17-3-tenant-scoped-event-store.md)
**Branch**: `feat/story-17-3-tenant-event-store` (currently landing on `feat/auth-foundation`)
**Status**: in-progress — types, in-memory store, and migration already landed via PR #328. This plan covers the remaining work: Postgres-backed event store, per-request DB tenant scoping, and call-site audit.
**Depends on**: 17-1 (tenants table), 17-2 (RLS + `tamma_app` role + `app.current_tenant_id` session var + `prevent_tenant_id_change()` trigger)
**Blocks**: 9-2 PgDiagnosticsStore integration, 9-11 Elsa diagnostics writes, 27-7 prompt-execution events

---

## Overview

Story 17-3 already landed partial work on `feat/auth-foundation`:

- `EngineEvent.tenantId: string` added to `packages/shared/src/types/index.ts`
- `IEventStore` methods accept `tenantId` as the first parameter
- `InMemoryEventStore` filters every query by `tenantId`
- `Engine.recordEvent()` in `packages/orchestrator/src/engine.ts` threads `this.tenantId` into every call
- Migration `011_tenant_scoped_stores.sql` creates `engine_events` with `tenant_id UUID NOT NULL`, B-tree indexes, RLS policy, and the `trg_prevent_tenant_change_engine_events` trigger
- In-memory tenant-isolation test suite at `packages/shared/src/__tests__/event-store-tenant.test.ts`

**What remains**:

1. A Postgres-backed `PgEventStore` implementing `IEventStore` against the `engine_events` table from migration 011
2. Wiring the tenant-context middleware to call `SET app.current_tenant_id` on the request-scoped DB connection (currently it only decorates `request.tenantId`)
3. Auditing every non-engine event emission site for correct `tenantId` plumbing
4. RLS integration tests that exercise `PgEventStore` via the `tamma_app` role to prove cross-tenant reads are blocked at the DB layer

> **Migration number correction**: the layered-plan entry (`docs/stories/plans/layer-2-parallel-infra.md`) labels this migration as `010`, but the actual landed migration is `011_tenant_scoped_stores.sql`. The `engine_events` and `workflow_instances` DDL share that file per the 17-3/17-4 coordination note. No new migration is needed for 17-3; tasks reference 011.

---

## Step-by-Step Implementation Tasks

### Task 1: Convert `IEventStore` to async (2 hours)

`IEventStore` is currently synchronous and `PgEventStore` cannot be. Convert the interface to async before writing the Pg implementation. `Engine.recordEvent()` already ignores the return value, and the only reader is `Engine.getEventStore()` — external callers can `await`.

**File to modify**: `packages/shared/src/types/index.ts`

```diff
 export interface IEventStore {
-  record(event: Omit<EngineEvent, 'id' | 'timestamp'>): EngineEvent;
-  getEvents(tenantId: string, issueNumber?: number): EngineEvent[];
-  getLastEvent(tenantId: string, type: EngineEventType): EngineEvent | undefined;
-  clear(tenantId: string): void;
+  record(event: Omit<EngineEvent, 'id' | 'timestamp'>): Promise<EngineEvent>;
+  getEvents(tenantId: string, issueNumber?: number): Promise<EngineEvent[]>;
+  getLastEvent(tenantId: string, type: EngineEventType): Promise<EngineEvent | undefined>;
+  clear(tenantId: string): Promise<void>;
 }
```

**Files to modify**:

- `packages/shared/src/event-store.ts` — `InMemoryEventStore` methods become `async` (trivial — bodies already pure)
- `packages/shared/src/event-store.test.ts` — `await` each call
- `packages/shared/src/__tests__/event-store-tenant.test.ts` — `await` each call
- `packages/orchestrator/src/engine.ts` line ~1092 — `recordEvent()` becomes `private async recordEvent(...)`; callers already sit inside `async` workflow methods, so `void this.recordEvent(...)` or `await this.recordEvent(...)` per site. Search `recordEvent(` in engine.ts and promote each call.

**Tests to run**:
- `pnpm --filter @tamma/shared test packages/shared/src/__tests__/event-store-tenant.test.ts`
- `pnpm --filter @tamma/shared test packages/shared/src/event-store.test.ts`
- `pnpm --filter @tamma/orchestrator build`
- `pnpm --filter @tamma/orchestrator test`

---

### Task 2: Implement `PgEventStore` against `engine_events` (3 hours)

**File to create**: `packages/api/src/persistence/pg-event-store.ts`

```typescript
import type pg from 'pg';
import { randomUUID } from 'node:crypto';
import type { EngineEvent, EngineEventType, IEventStore } from '@tamma/shared';

export class PgEventStore implements IEventStore {
  constructor(private readonly pool: pg.Pool) {}
  // method bodies below
}
```

`PgEventStore` does NOT call `SET app.current_tenant_id` itself. The caller (middleware or `withTenantContext()`) owns that session variable. The `tenantId` parameter on every method is still required — it is both a defense-in-depth WHERE filter and the value inserted into the `tenant_id` column.

Method signatures (bodies are straight parameterized SQL against `engine_events`):

```typescript
async record(event: Omit<EngineEvent, 'id' | 'timestamp'>): Promise<EngineEvent>;
async getEvents(tenantId: string, issueNumber?: number): Promise<EngineEvent[]>;
async getLastEvent(tenantId: string, type: EngineEventType): Promise<EngineEvent | undefined>;
async clear(tenantId: string): Promise<void>;
```

- `record()` — `INSERT INTO engine_events (id, type, timestamp, tenant_id, issue_number, data) VALUES (...) RETURNING *`. `id` is `randomUUID()`, `timestamp` is `Date.now()`.
- `getEvents()` — `SELECT ... WHERE tenant_id = $1 [AND issue_number = $2] ORDER BY timestamp ASC, id ASC`.
- `getLastEvent()` — `SELECT ... WHERE tenant_id = $1 AND type = $2 ORDER BY timestamp DESC, id DESC LIMIT 1`.
- `clear()` — `DELETE FROM engine_events WHERE tenant_id = $1`. Explicit WHERE is defense-in-depth in case the session variable was never set; RLS would otherwise reject silently.
- Private `_mapRow(row)` converts snake_case columns to the `EngineEvent` shape. Drop `issueNumber` from the object when `issue_number` is NULL (required by `exactOptionalPropertyTypes`).

**Test to run**: `pnpm --filter @tamma/api build`

---

### Task 3: Add `withTenantContext()` helper + wire middleware (2 hours)

**Problem**: `tenant-context.ts` currently sets `request.tenantId` but never touches the DB. RLS on `engine_events` will reject every query because `app.current_tenant_id` is unset.

**File to create**: `packages/api/src/persistence/with-tenant-context.ts`

```typescript
import type pg from 'pg';

/**
 * Execute `fn` with `app.current_tenant_id` set on a dedicated pool client.
 *
 * Always uses `SET LOCAL` inside a transaction so the session variable is
 * scoped to this unit of work only — the connection is safe to return to
 * the pool afterwards without contaminating the next caller.
 */
export async function withTenantContext<T>(
  pool: pg.Pool,
  tenantId: string,
  fn: (client: pg.PoolClient) => Promise<T>,
): Promise<T> {
  const client = await pool.connect();
  try {
    await client.query('BEGIN');
    await client.query("SELECT set_config('app.current_tenant_id', $1, true)", [tenantId]);
    const result = await fn(client);
    await client.query('COMMIT');
    return result;
  } catch (err) {
    await client.query('ROLLBACK');
    throw err;
  } finally {
    client.release();
  }
}
```

**File to modify**: `packages/api/src/middleware/tenant-context.ts`

Add an optional `pool` to `TenantContextConfig`. When present, the `onRequest` hook (after resolving `tenantId`) must set the session variable on the pool so downstream handlers running in-request reuse the same scoping. For per-request connection scoping, add a second `onRequest` hook that eagerly calls `SELECT set_config('app.current_tenant_id', $1, false)` against the shared pool — acceptable because `SET` without `LOCAL` targets the single pooled client, and the connection stays in use until `onResponse` fires a `RESET app.current_tenant_id`.

```diff
 export interface TenantContextConfig {
   tenantStore: ITenantStore;
   userStore: IUserStore;
   enableAuth: boolean;
+  pool?: pg.Pool;
 }
```

Add inside the plugin (after `request.tenantId = tenantId`):

```typescript
if (opts.pool) {
  await opts.pool.query("SELECT set_config('app.current_tenant_id', $1, false)", [tenantId]);
}

fastify.addHook('onResponse', async () => {
  if (opts.pool) {
    await opts.pool.query('RESET app.current_tenant_id');
  }
});
```

> **Judgment call**: using `SET` (session-wide) across a shared pool is race-prone if two requests overlap on the same connection. The robust pattern is to switch `PgEventStore` / `PgWorkflowStore` to use `withTenantContext()` (Task 4a below) and drop the middleware's direct pool mutation. Pick one and apply consistently.

**Task 4a (recommended alternative)**: refactor `PgEventStore` to use `withTenantContext`. Change the constructor to accept a `pool` and wrap every query in `withTenantContext(pool, tenantId, async (client) => client.query(...))`. The middleware only resolves `tenantId`; the store opens its own scoped transaction per call. This removes the race window.

**Tests to run**:
- `pnpm --filter @tamma/api test packages/api/src/middleware/__tests__/tenant-context.test.ts`
- `pnpm --filter @tamma/api build`

---

### Task 4: Write `PgEventStore` integration tests (2 hours)

**File to create**: `packages/api/src/persistence/__tests__/pg-event-store.integration.test.ts`

Model after `rls-tenant-isolation.integration.test.ts` — uses the shared pg-test-helper, `setAppRole()`, and two tenant sentinels.

| # | Test | Assertion |
|---|------|-----------|
| 1 | `record()` inserts with explicit `tenantId` | Row visible to same tenant |
| 2 | `record()` under tenant A with wrong session tenant B | RLS `WITH CHECK` rejects insert |
| 3 | `getEvents(TENANT_A)` returns only tenant A rows | Tenant B rows invisible |
| 4 | `getEvents(TENANT_A, 42)` filters by tenant + issue | Composite index path |
| 5 | `getLastEvent(TENANT_A, type)` returns most recent for tenant | Tenant B event of same type ignored |
| 6 | `clear(TENANT_A)` removes only tenant A rows | Tenant B rows remain |
| 7 | Query under no `app.current_tenant_id` | Zero rows returned (RLS fail-closed) |
| 8 | UPDATE attempt of `tenant_id` column | Trigger `prevent_tenant_id_change()` raises exception |
| 9 | `record()` stores JSONB `data` round-trip | Shape preserved |
| 10 | Composite index `(tenant_id, issue_number)` is used | `EXPLAIN` shows index scan |

**Test bootstrap requirements**:
- Seed two tenants in `tenants` table via `pg-test-helper.ts` `cleanDatabase()`
- Call `setAppRole(pool)` before each test (superuser bypasses RLS)
- Use `setTenantContext(pool, TENANT_X)` before each query

**Test to run**: `pnpm --filter @tamma/api test packages/api/src/persistence/__tests__/pg-event-store.integration.test.ts`

---

### Task 5: Audit non-engine event emission call sites (1 hour)

The engine already threads `tenantId` (`packages/orchestrator/src/engine.ts:1095`). Two other files reference `IEventStore` — verify they pass `tenantId`:

**Files to audit**:

- `packages/api/src/services/prompt-store-events.ts`
- `packages/api/src/services/convention-templates.ts`

For each:
1. Grep for `.record(` calls on an event store
2. Confirm `tenantId` is sourced from `request.tenantId` (API route) or the caller's context (service)
3. If a service emits events without a request context, add a `tenantId: string` parameter to the service constructor or method

**Test to run**: `pnpm --filter @tamma/api test packages/api/src/services/` (existing tests; add `tenantId` fixtures where the compiler flags missing fields)

---

### Task 6: Wire `PgEventStore` into the composition root (1 hour)

**File to modify**: `packages/api/src/serve.ts` (or wherever the persistence container is built)

- Construct a single `PgEventStore(pool)` alongside the other Pg stores
- Expose it on the services container passed to Fastify route builders
- Keep `InMemoryEventStore` as the default for CLI/self-hosted mode (select via `config.backend`)

**Files to modify**:
- `packages/api/src/serve.ts` — add `const eventStore = new PgEventStore(pool)` and register
- `packages/cli/src/...` if the CLI constructs the engine directly — already uses `InMemoryEventStore`, no change

**Test to run**: `pnpm --filter @tamma/api build && pnpm --filter @tamma/api test`

---

## Migration Block (already landed — reference only)

`database/migrations/011_tenant_scoped_stores.sql` already created `engine_events` with:

- `tenant_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000' REFERENCES tenants(id)`
- Indexes: `idx_engine_events_tenant_id`, `idx_engine_events_tenant_issue (tenant_id, issue_number) WHERE issue_number IS NOT NULL`, `idx_engine_events_tenant_type (tenant_id, type)`
- RLS: `ENABLE` + `FORCE`, policy `tenant_isolation_policy` with `USING` and `WITH CHECK` both against `current_setting('app.current_tenant_id', true)::uuid`
- Trigger: `trg_prevent_tenant_change_engine_events` → `prevent_tenant_id_change()` (from 17-2)
- `GRANT SELECT, INSERT, UPDATE, DELETE ... TO tamma_app` (conditional on the role existing)

No new migration file is required. If integration tests reveal a missing index (e.g. `(tenant_id, timestamp DESC)` for time-travel replay), add migration **019** rather than editing 011.

---

## RLS Integration Notes

- RLS is enforced only for the `tamma_app` role; migrations run as superuser, which bypasses RLS. Integration tests must call `SET ROLE tamma_app` before exercising RLS.
- `PgEventStore` never reads `current_setting('app.current_tenant_id')` itself. The caller (middleware or `withTenantContext()`) owns that session variable. This keeps `PgEventStore` composable with both per-request pooling and transactional helpers.
- The DEFAULT value `'00000000-0000-0000-0000-000000000000'` on `tenant_id` exists to keep existing integration test fixtures happy; do **not** rely on it in production code — always pass `tenantId` explicitly.

---

## Event Schema (already landed)

`packages/shared/src/types/index.ts`:

```typescript
export interface EngineEvent {
  id: string;
  type: EngineEventType;
  timestamp: number;
  tenantId: string;       // <-- added
  issueNumber?: number;
  data: Record<string, unknown>;
}

export interface IEventStore {
  record(event: Omit<EngineEvent, 'id' | 'timestamp'>): Promise<EngineEvent>;
  getEvents(tenantId: string, issueNumber?: number): Promise<EngineEvent[]>;
  getLastEvent(tenantId: string, type: EngineEventType): Promise<EngineEvent | undefined>;
  clear(tenantId: string): Promise<void>;
}
```

The async conversion from Task 2 is the only schema change remaining. The `tenantId` field itself is already in `main`.

---

## Test Strategy

| Layer | File | What it proves |
|-------|------|----------------|
| Unit | `packages/shared/src/__tests__/event-store-tenant.test.ts` | In-memory store isolates by tenant (already landed — update to `await`) |
| Unit | `packages/api/src/middleware/__tests__/tenant-context.test.ts` | Middleware sets `request.tenantId` and, when `pool` is provided, sets the DB session variable |
| Integration | `packages/api/src/persistence/__tests__/pg-event-store.integration.test.ts` | PgEventStore + RLS end-to-end, with `tamma_app` role |
| Integration | `packages/api/src/persistence/__tests__/rls-tenant-isolation.integration.test.ts` | Already covers `engine_events` select isolation — keep green |
| Engine | `packages/orchestrator/src/__tests__/engine.test.ts` | Engine.recordEvent threads `tenantId` through every emission (already exists — update for async) |

**Backwards-compatibility clause**: there are no legacy `engine_events` rows to migrate. Migration 011 created the table fresh with `NOT NULL DEFAULT '00000000-...'`. The default exists solely for the shared-DB test fixtures. There is no production data to backfill because no orchestrator build has ever written to this table — all prior event writes went through `InMemoryEventStore` only.

---

## Rollout Risks

1. **Async interface break** (Task 2). Every call site to `eventStore.record()` / `getEvents()` / `clear()` must be updated. Mitigation: run `pnpm -r build` after the `@tamma/shared` change and fix each compiler error.
2. **Pool-level session-variable race** (Task 4). If two concurrent requests reuse the same pooled connection, the later `SET` can clobber the earlier request's tenant. Mitigation: prefer `withTenantContext()` (Task 4a) so each query runs inside a `BEGIN ... SET LOCAL ... COMMIT` envelope.
3. **Test DB teardown ordering**. `cleanDatabase()` in `pg-test-helper.ts` already truncates `engine_events` in the correct order. Verify that order still holds after adding new foreign-key-referencing tables in later stories.
4. **Backfill**: none required. No production events exist in `engine_events`; the DEFAULT `'00000000-...'` on `tenant_id` is a test-fixture convenience only and is safe to leave in place. Story 17-6 (if created) would tighten the DEFAULT to `NULL`.

---

## Estimated Effort

| Task | Hours |
|------|-------|
| 1. Sync → async `IEventStore` conversion | 2 |
| 2. PgEventStore body + `_mapRow` | 3 |
| 3. `withTenantContext` helper + middleware wiring | 2 |
| 4. PgEventStore integration tests (10 cases) | 2 |
| 5. Non-engine call-site audit | 1 |
| 6. Composition root wiring | 1 |
| **Total** | **11 hours** |

Layered plan originally estimated 10 hours. The +1 hour delta reflects the sync→async interface conversion not in the original scope.

---

## Files to Create

| # | File | Purpose |
|---|------|---------|
| 1 | `packages/api/src/persistence/pg-event-store.ts` | Postgres `IEventStore` impl |
| 2 | `packages/api/src/persistence/with-tenant-context.ts` | Tenant-scoped transaction helper |
| 3 | `packages/api/src/persistence/__tests__/pg-event-store.integration.test.ts` | RLS integration tests |

## Files to Modify

| # | File | Change |
|---|------|--------|
| 1 | `packages/shared/src/types/index.ts` | `IEventStore` methods become `Promise`-returning |
| 2 | `packages/shared/src/event-store.ts` | `InMemoryEventStore` methods become `async` |
| 3 | `packages/shared/src/event-store.test.ts` | `await` each call |
| 4 | `packages/shared/src/__tests__/event-store-tenant.test.ts` | `await` each call |
| 5 | `packages/orchestrator/src/engine.ts` | `recordEvent()` becomes `async`; all callers update |
| 6 | `packages/api/src/middleware/tenant-context.ts` | Optional `pool` + `SET app.current_tenant_id` / `RESET` on response |
| 7 | `packages/api/src/serve.ts` | Construct `PgEventStore(pool)` and register on services container |
| 8 | `packages/api/src/services/prompt-store-events.ts` | Audit — ensure `tenantId` flows through (if any `.record(` calls) |
| 9 | `packages/api/src/services/convention-templates.ts` | Same audit |
