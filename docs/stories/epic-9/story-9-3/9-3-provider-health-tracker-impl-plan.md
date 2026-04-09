# Story 9-3: Health Tracker Service + API — Implementation Plan

## Overview

Promote the in-process `ProviderHealthTracker` to a persistent service backed by Postgres, with Fastify API endpoints for querying and managing circuit breaker state. This ensures a failure recorded by Elsa trips the circuit for the TS engine and vice versa. The existing `ProviderHealthTracker` class remains the in-process implementation but gains an `onCircuitChange` callback that syncs state transitions to the persistent store.

---

## Step-by-Step Implementation Tasks

### Task 1: Create the Migration SQL File (2 hours)

**File to create**: `database/migrations/014_provider_health.sql`

```sql
-- Provider health / circuit breaker state
-- Epic 9, Story 9-3

CREATE TABLE IF NOT EXISTS provider_health (
  key                   TEXT PRIMARY KEY,       -- "provider:model" e.g. "openrouter:z-ai/z1-mini"
  circuit_open          BOOLEAN NOT NULL DEFAULT false,
  circuit_open_until    TIMESTAMPTZ,
  failure_count         INTEGER NOT NULL DEFAULT 0,
  last_failure_at       TIMESTAMPTZ,
  last_success_at       TIMESTAMPTZ,
  half_open_in_progress BOOLEAN NOT NULL DEFAULT false,
  updated_at            TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Index for queries that filter on circuit_open
CREATE INDEX IF NOT EXISTS idx_provider_health_open
  ON provider_health (circuit_open)
  WHERE circuit_open = true;
```

---

### Task 2: Define IHealthStore Interface + Types (1.5 hours)

**File to create**: `packages/api/src/services/health-store.ts`

```typescript
/** Persistent health status for a provider+model key. */
export interface HealthRecord {
  key: string;
  circuitOpen: boolean;
  circuitOpenUntil: string | null;  // ISO 8601 or null
  failureCount: number;
  lastFailureAt: string | null;
  lastSuccessAt: string | null;
  halfOpenInProgress: boolean;
  updatedAt: string;
}

/** Summary status returned by API. */
export interface HealthStatus {
  healthy: boolean;
  failures: number;
  circuitOpen: boolean;
  circuitOpenUntil: string | null;
  halfOpen: boolean;
}

/** Interface for the persistent health store. */
export interface IHealthStore {
  /** Get health status for all tracked keys. */
  getAll(): Promise<Record<string, HealthStatus>>;
  /** Get health status for a specific key. */
  get(key: string): Promise<HealthStatus | null>;
  /** Record a failure for a key (may open circuit). */
  recordFailure(key: string, options?: { error?: string; retryable?: boolean }): Promise<{ circuitOpen: boolean; failures: number }>;
  /** Record a success for a key (closes circuit if half-open). */
  recordSuccess(key: string): Promise<{ circuitOpen: boolean; failures: number }>;
  /** Reset (delete) health state for a key. */
  reset(key: string): Promise<boolean>;
  /** Sync a circuit state change from the in-process tracker to Postgres. */
  syncCircuitChange(key: string, state: 'open' | 'half-open' | 'closed', metadata?: Record<string, unknown>): Promise<void>;
}
```

---

### Task 3: Implement PgHealthStore (4 hours)

**File to create**: `packages/api/src/services/pg-health-store.ts`

```typescript
import type pg from 'pg';
import type { IHealthStore, HealthRecord, HealthStatus } from './health-store.js';

/** Default circuit breaker settings (match ProviderHealthTracker defaults). */
const DEFAULT_FAILURE_THRESHOLD = 5;
const DEFAULT_CIRCUIT_OPEN_DURATION_MS = 300_000; // 5 minutes

export class PgHealthStore implements IHealthStore {
  constructor(
    private readonly pool: pg.Pool,
    private readonly options?: {
      failureThreshold?: number;
      circuitOpenDurationMs?: number;
    },
  ) {}

  private get failureThreshold(): number {
    return this.options?.failureThreshold ?? DEFAULT_FAILURE_THRESHOLD;
  }

  private get circuitOpenDurationMs(): number {
    return this.options?.circuitOpenDurationMs ?? DEFAULT_CIRCUIT_OPEN_DURATION_MS;
  }

  async getAll(): Promise<Record<string, HealthStatus>> {
    const result = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM provider_health ORDER BY key',
    );
    const out: Record<string, HealthStatus> = {};
    for (const row of result.rows) {
      const record = this._mapRow(row);
      out[record.key] = this._toStatus(record);
    }
    return out;
  }

  async get(key: string): Promise<HealthStatus | null> {
    const result = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM provider_health WHERE key = $1',
      [key],
    );
    if (result.rows.length === 0) return null;
    return this._toStatus(this._mapRow(result.rows[0]!));
  }

  async recordFailure(key: string, options?: { error?: string; retryable?: boolean }): Promise<{ circuitOpen: boolean; failures: number }> {
    // Non-retryable errors don't count (match ProviderHealthTracker behavior)
    if (options?.retryable === false) {
      const current = await this.get(key);
      return { circuitOpen: current?.circuitOpen ?? false, failures: current?.failures ?? 0 };
    }

    // UPSERT: increment failure_count, check threshold, potentially open circuit
    const openUntil = new Date(Date.now() + this.circuitOpenDurationMs).toISOString();
    const result = await this.pool.query<Record<string, unknown>>(`
      INSERT INTO provider_health (key, failure_count, last_failure_at, updated_at)
      VALUES ($1, 1, NOW(), NOW())
      ON CONFLICT (key) DO UPDATE SET
        failure_count = provider_health.failure_count + 1,
        last_failure_at = NOW(),
        circuit_open = CASE
          WHEN provider_health.failure_count + 1 >= $2 THEN true
          ELSE provider_health.circuit_open
        END,
        circuit_open_until = CASE
          WHEN provider_health.failure_count + 1 >= $2 THEN $3::timestamptz
          ELSE provider_health.circuit_open_until
        END,
        half_open_in_progress = CASE
          WHEN provider_health.half_open_in_progress THEN false
          ELSE provider_health.half_open_in_progress
        END,
        updated_at = NOW()
      RETURNING *
    `, [key, this.failureThreshold, openUntil]);

    const record = this._mapRow(result.rows[0]!);
    return { circuitOpen: record.circuitOpen, failures: record.failureCount };
  }

  async recordSuccess(key: string): Promise<{ circuitOpen: boolean; failures: number }> {
    // Reset circuit to closed, clear failures
    await this.pool.query(`
      UPDATE provider_health SET
        circuit_open = false,
        circuit_open_until = NULL,
        failure_count = 0,
        half_open_in_progress = false,
        last_success_at = NOW(),
        updated_at = NOW()
      WHERE key = $1
    `, [key]);
    return { circuitOpen: false, failures: 0 };
  }

  async reset(key: string): Promise<boolean> {
    const result = await this.pool.query('DELETE FROM provider_health WHERE key = $1', [key]);
    return (result.rowCount ?? 0) > 0;
  }

  async syncCircuitChange(key: string, state: 'open' | 'half-open' | 'closed'): Promise<void> {
    if (state === 'closed') {
      await this.recordSuccess(key);
    } else if (state === 'open') {
      const openUntil = new Date(Date.now() + this.circuitOpenDurationMs).toISOString();
      await this.pool.query(`
        INSERT INTO provider_health (key, circuit_open, circuit_open_until, half_open_in_progress, updated_at)
        VALUES ($1, true, $2, false, NOW())
        ON CONFLICT (key) DO UPDATE SET
          circuit_open = true,
          circuit_open_until = $2,
          half_open_in_progress = false,
          updated_at = NOW()
      `, [key, openUntil]);
    } else {
      // half-open
      await this.pool.query(`
        INSERT INTO provider_health (key, circuit_open, half_open_in_progress, updated_at)
        VALUES ($1, true, true, NOW())
        ON CONFLICT (key) DO UPDATE SET
          half_open_in_progress = true,
          updated_at = NOW()
      `, [key]);
    }
  }

  private _mapRow(row: Record<string, unknown>): HealthRecord { /* ... */ }
  private _toStatus(record: HealthRecord): HealthStatus { /* ... */ }
}
```

---

### Task 4: Implement Fastify Routes (3 hours)

**File to modify**: `packages/api/src/routes/settings/health-routes.ts`

Replace the placeholder with full endpoints:

```typescript
import type { FastifyInstance } from 'fastify';
import type { IHealthStore } from '../../services/health-store.js';

export function registerHealthRoutes(app: FastifyInstance, store: IHealthStore): void {
  // GET /api/v1/health/providers — all provider health statuses
  app.get('/health/providers', async (_request, reply) => {
    const status = await store.getAll();
    return reply.send(status);
  });

  // GET /api/v1/health/providers/:key — specific provider health
  app.get('/health/providers/:key', async (request, reply) => {
    const { key } = request.params as { key: string };
    const status = await store.get(key);
    if (status === null) {
      // Unknown key = healthy (never failed)
      return reply.send({ healthy: true, failures: 0, circuitOpen: false, circuitOpenUntil: null, halfOpen: false });
    }
    return reply.send(status);
  });

  // POST /api/v1/health/providers/:key/failure — record failure (used by Elsa)
  app.post('/health/providers/:key/failure', async (request, reply) => {
    const { key } = request.params as { key: string };
    const body = (request.body ?? {}) as { error?: string; retryable?: boolean };
    const result = await store.recordFailure(key, body);
    return reply.send(result);
  });

  // POST /api/v1/health/providers/:key/success — record success (used by Elsa)
  app.post('/health/providers/:key/success', async (request, reply) => {
    const { key } = request.params as { key: string };
    const result = await store.recordSuccess(key);
    return reply.send(result);
  });

  // POST /api/v1/health/providers/:key/reset — admin reset (admin only)
  app.post('/health/providers/:key/reset', async (request, reply) => {
    const { key } = request.params as { key: string };
    const deleted = await store.reset(key);
    return reply.send({ reset: deleted });
  });
}
```

---

### Task 5: Add Persistence Sync to ProviderHealthTracker (2 hours)

**File to modify**: `packages/providers/src/provider-health.ts`

The existing `onCircuitChange` callback already supports hooking into state transitions. The wiring happens at construction time:

```typescript
// In CLI start.tsx or API server wiring:
const healthStore = new PgHealthStore(pool);
const healthTracker = new ProviderHealthTracker({
  onCircuitChange: (key, state) => {
    // Fire-and-forget sync to Postgres
    healthStore.syncCircuitChange(key, state).catch((err) => {
      logger.warn('Failed to sync circuit change to store', { key, state, error: err });
    });
  },
});
```

No changes to `ProviderHealthTracker` class itself -- the `onCircuitChange` callback is already supported.

---

### Task 6: Wire PgHealthStore + Update Settings Index (1.5 hours)

**File to modify**: `packages/api/src/routes/settings/index.ts`

```typescript
export interface SettingsServices {
  configService: ConfigService;
  configStore: IAgentConfigStore;
  healthStore: IHealthStore;        // replaces HealthService
  healthService: HealthService;     // retained for backward compat
  diagnosticsStore: IDiagnosticsStore;
  diagnosticsService: DiagnosticsService;
}
```

**File to modify**: `packages/api/src/services/settings/HealthService.ts`

Update to delegate to `IHealthStore`:

```typescript
export class HealthService {
  private store: IHealthStore | null;
  // ...
  async getStatus(): Promise<Record<string, HealthStatusEntry>> {
    if (this.store) return this.store.getAll();
    if (this.tracker) return this.tracker.getStatus();
    return {};
  }
}
```

---

### Task 7: Tests (3 hours)

**File to create**: `packages/api/src/services/health-store.test.ts`

| # | Test | Assertion |
|---|------|-----------|
| 1 | `get()` for unknown key returns null | Healthy by default |
| 2 | `recordFailure()` creates entry on first failure | failureCount = 1, circuitOpen = false |
| 3 | `recordFailure()` N times opens circuit at threshold | circuitOpen = true after 5 failures |
| 4 | `recordFailure()` with retryable=false is no-op | failureCount unchanged |
| 5 | `recordSuccess()` closes circuit | circuitOpen = false, failures = 0 |
| 6 | `reset()` removes entry | `get()` returns null after reset |
| 7 | `reset()` for non-existent key returns false | No error |
| 8 | `getAll()` returns all tracked keys | Correct shape for each |
| 9 | `syncCircuitChange('open')` persists open state | Row reflects open circuit |
| 10 | `syncCircuitChange('half-open')` persists half-open | halfOpenInProgress = true |
| 11 | `syncCircuitChange('closed')` resets to closed | circuitOpen = false |

**File to create**: `packages/api/src/routes/settings/__tests__/health-routes.test.ts`

| # | Test | Assertion |
|---|------|-----------|
| 12 | GET /health/providers returns 200 | Record of statuses |
| 13 | GET /health/providers/:key for unknown key | Returns healthy default |
| 14 | POST /health/providers/:key/failure increments | circuitOpen changes at threshold |
| 15 | POST /health/providers/:key/success closes | circuitOpen = false |
| 16 | POST /health/providers/:key/reset deletes | reset = true |

**Total tests**: ~16

---

## Files to Create

| # | File Path | Purpose |
|---|-----------|---------|
| 1 | `database/migrations/014_provider_health.sql` | DDL + indexes |
| 2 | `packages/api/src/services/health-store.ts` | Interface + types |
| 3 | `packages/api/src/services/pg-health-store.ts` | Postgres implementation |
| 4 | `packages/api/src/services/health-store.test.ts` | Service tests |
| 5 | `packages/api/src/routes/settings/__tests__/health-routes.test.ts` | Route tests |

## Files to Modify

| # | File Path | Change |
|---|-----------|--------|
| 1 | `packages/api/src/routes/settings/health-routes.ts` | Replace placeholder with full endpoints |
| 2 | `packages/api/src/routes/settings/index.ts` | Wire PgHealthStore |
| 3 | `packages/api/src/services/settings/HealthService.ts` | Delegate to IHealthStore |

---

## Dependencies

- None (health tracking is global per deployment, not per-account)
- PostgreSQL migration 010 requires migrations 001-009 applied first

## Migration from Existing Code

1. The existing `ProviderHealthTracker` in `packages/providers/src/provider-health.ts` remains the in-process circuit breaker with sliding-window logic. No changes to its class.
2. The `onCircuitChange` callback (already supported) is wired to `PgHealthStore.syncCircuitChange()` at application startup.
3. The `HealthService` in `packages/api/src/services/settings/HealthService.ts` gains an `IHealthStore` dependency and delegates reads to Postgres when available.
4. Elsa's `CheckCircuitBreakerActivity.cs` calls `GET /api/v1/health/providers/:key` instead of managing in-workflow JSON state (wired in Story 9-11).

---

## Estimated Effort

| Task | Hours |
|------|-------|
| Migration SQL | 2 |
| IHealthStore interface + types | 1.5 |
| PgHealthStore implementation | 4 |
| Fastify routes (5 endpoints) | 3 |
| Persistence sync wiring | 2 |
| Settings index wiring | 1.5 |
| Tests (16 tests) | 3 |
| **Total** | **17 hours** |
