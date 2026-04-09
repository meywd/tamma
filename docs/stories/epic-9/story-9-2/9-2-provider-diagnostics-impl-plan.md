# Story 9-2: Diagnostics Service + API — Implementation Plan

## Overview

Replace the in-memory `DiagnosticsService` (capped at 500 events) with a Postgres-backed `PgDiagnosticsStore` that persists all provider call metrics. Expose Fastify API endpoints for recording, querying, reporting, and budget checking. Both the TS engine (via in-process `DiagnosticsQueue` drain) and Elsa workflows (via HTTP POST) write to the same store.

---

## Step-by-Step Implementation Tasks

### Task 1: Create the Migration SQL File (2 hours)

**File to create**: `database/migrations/013_provider_diagnostics.sql`

```sql
-- Provider diagnostics: per-call LLM/tool metrics
-- Epic 9, Story 9-2

CREATE TABLE IF NOT EXISTS provider_diagnostics (
  id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  account_id      UUID,              -- NULL for system-level calls; FK deferred to Epic 17
  event_type      TEXT NOT NULL,      -- 'provider:complete', 'provider:error', 'tool:complete', etc.
  provider_name   TEXT NOT NULL,
  model           TEXT,
  agent_type      TEXT,
  project_id      TEXT,
  engine_id       TEXT,
  task_id         TEXT,
  task_type       TEXT,
  input_tokens    INTEGER NOT NULL DEFAULT 0,
  output_tokens   INTEGER NOT NULL DEFAULT 0,
  latency_ms      INTEGER NOT NULL DEFAULT 0,
  cost_usd        NUMERIC(12, 6) NOT NULL DEFAULT 0,
  success         BOOLEAN NOT NULL DEFAULT false,
  error_code      TEXT,
  error_message   TEXT,
  correlation_id  UUID,
  created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Query indexes
CREATE INDEX IF NOT EXISTS idx_diagnostics_account_created
  ON provider_diagnostics (account_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_diagnostics_provider
  ON provider_diagnostics (provider_name, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_diagnostics_engine
  ON provider_diagnostics (engine_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_diagnostics_correlation
  ON provider_diagnostics (correlation_id)
  WHERE correlation_id IS NOT NULL;

-- Budget aggregation index (for SUM queries per account in time range)
CREATE INDEX IF NOT EXISTS idx_diagnostics_budget
  ON provider_diagnostics (account_id, created_at)
  WHERE success = true;
```

---

### Task 2: Define IDiagnosticsStore Interface + Types (2 hours)

**File to create**: `packages/api/src/services/diagnostics-store.ts`

```typescript
import type { DiagnosticsEvent } from '@tamma/shared';

/** A persisted diagnostics record (DB row mapped to TS). */
export interface DiagnosticsRecord {
  id: string;
  accountId: string | null;
  eventType: string;
  providerName: string;
  model: string | null;
  agentType: string | null;
  projectId: string | null;
  engineId: string | null;
  taskId: string | null;
  taskType: string | null;
  inputTokens: number;
  outputTokens: number;
  latencyMs: number;
  costUsd: number;
  success: boolean;
  errorCode: string | null;
  errorMessage: string | null;
  correlationId: string | null;
  createdAt: string;
}

/** Query filters for diagnostics. */
export interface DiagnosticsQuery {
  accountId?: string;
  provider?: string;
  model?: string;
  from?: string;     // ISO 8601
  to?: string;       // ISO 8601
  limit?: number;    // default 50
  offset?: number;   // default 0
}

/** Aggregated report group. */
export interface DiagnosticsReportGroup {
  key: string;
  totalCost: number;
  totalInputTokens: number;
  totalOutputTokens: number;
  avgLatencyMs: number;
  errorRate: number;
  count: number;
}

/** Budget status for an account. */
export interface BudgetStatus {
  spent: number;
  limit: number;
  remaining: number;
  percentUsed: number;
}

/** Interface for the diagnostics store. */
export interface IDiagnosticsStore {
  /** Record a single diagnostics event. */
  record(event: DiagnosticsEvent, accountId?: string): Promise<void>;
  /** Record a batch of diagnostics events. */
  recordBatch(events: DiagnosticsEvent[], accountId?: string): Promise<number>;
  /** Query diagnostics records with filters. */
  query(filters: DiagnosticsQuery): Promise<{ items: DiagnosticsRecord[]; total: number }>;
  /** Generate aggregated report. */
  report(
    accountId: string,
    options: { from?: string; to?: string; groupBy: 'provider' | 'model' | 'agentType' },
  ): Promise<DiagnosticsReportGroup[]>;
  /** Check budget status for an account. */
  checkBudget(accountId: string, limit: number): Promise<BudgetStatus>;
}
```

---

### Task 3: Implement PgDiagnosticsStore (5 hours)

**File to create**: `packages/api/src/services/pg-diagnostics-store.ts`

Follows `PgInstallationStore` pattern. Key methods:

```typescript
import type pg from 'pg';
import type { DiagnosticsEvent, ProviderDiagnosticsEvent } from '@tamma/shared';
import type {
  IDiagnosticsStore,
  DiagnosticsRecord,
  DiagnosticsQuery,
  DiagnosticsReportGroup,
  BudgetStatus,
} from './diagnostics-store.js';

export class PgDiagnosticsStore implements IDiagnosticsStore {
  constructor(private readonly pool: pg.Pool) {}

  async record(event: DiagnosticsEvent, accountId?: string): Promise<void> {
    // Single INSERT with parameterized values
    // Extract providerName/model/tokens based on event type discriminator
    await this.pool.query(
      `INSERT INTO provider_diagnostics (
        account_id, event_type, provider_name, model, agent_type,
        project_id, engine_id, task_id, task_type,
        input_tokens, output_tokens, latency_ms, cost_usd,
        success, error_code, error_message, correlation_id
      ) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17)`,
      [/* ... mapped from event ... */],
    );
  }

  async recordBatch(events: DiagnosticsEvent[], accountId?: string): Promise<number> {
    if (events.length === 0) return 0;
    // Use a transaction + multi-row INSERT for efficiency
    const client = await this.pool.connect();
    try {
      await client.query('BEGIN');
      for (const event of events) {
        await this._insertOne(client, event, accountId);
      }
      await client.query('COMMIT');
      return events.length;
    } catch (err) {
      await client.query('ROLLBACK');
      throw err;
    } finally {
      client.release();
    }
  }

  async query(filters: DiagnosticsQuery): Promise<{ items: DiagnosticsRecord[]; total: number }> {
    // Build WHERE clause dynamically based on filters
    // Include COUNT(*) OVER() for total
    // ORDER BY created_at DESC, LIMIT/OFFSET
  }

  async report(
    accountId: string,
    options: { from?: string; to?: string; groupBy: 'provider' | 'model' | 'agentType' },
  ): Promise<DiagnosticsReportGroup[]> {
    // GROUP BY column mapped from options.groupBy:
    //   'provider' -> provider_name
    //   'model' -> model
    //   'agentType' -> agent_type
    // SELECT key, SUM(cost_usd), SUM(input_tokens), SUM(output_tokens),
    //        AVG(latency_ms), COUNT(*) FILTER (WHERE NOT success) / COUNT(*), COUNT(*)
  }

  async checkBudget(accountId: string, limit: number): Promise<BudgetStatus> {
    // SUM(cost_usd) WHERE account_id = $1 AND created_at >= date_trunc('month', NOW())
    // Returns { spent, limit, remaining: limit - spent, percentUsed: (spent/limit)*100 }
  }

  private _mapRow(row: Record<string, unknown>): DiagnosticsRecord { /* ... */ }
  private async _insertOne(client: pg.PoolClient, event: DiagnosticsEvent, accountId?: string): Promise<void> { /* ... */ }
}
```

---

### Task 4: Implement Fastify Routes (4 hours)

**File to modify**: `packages/api/src/routes/settings/diagnostics-routes.ts`

Replace placeholder with full endpoints:

```typescript
import type { FastifyInstance } from 'fastify';
import type { IDiagnosticsStore, DiagnosticsQuery } from '../../services/diagnostics-store.js';

export function registerDiagnosticsRoutes(app: FastifyInstance, store: IDiagnosticsStore): void {
  // POST /api/v1/diagnostics — record event(s)
  app.post('/diagnostics', {
    schema: {
      body: {
        type: 'object',
        properties: {
          events: { type: 'array', items: { type: 'object' } },
          event: { type: 'object' },
        },
      },
      response: { 200: { type: 'object', properties: { recorded: { type: 'integer' } } } },
    },
  }, async (request, reply) => {
    const body = request.body as { events?: DiagnosticsEvent[]; event?: DiagnosticsEvent };
    const accountId = (request as any).accountId ?? undefined;
    if (body.events) {
      const count = await store.recordBatch(body.events, accountId);
      return reply.send({ recorded: count });
    }
    if (body.event) {
      await store.record(body.event, accountId);
      return reply.send({ recorded: 1 });
    }
    return reply.status(400).send({ error: 'Provide event or events' });
  });

  // GET /api/v1/diagnostics — query with filters
  app.get('/diagnostics', async (request, reply) => {
    const accountId = (request as any).accountId ?? undefined;
    const query = request.query as DiagnosticsQuery;
    const result = await store.query({ ...query, accountId });
    return reply.send(result);
  });

  // GET /api/v1/diagnostics/report — aggregated report
  app.get('/diagnostics/report', async (request, reply) => {
    const accountId = (request as any).accountId;
    const { from, to, groupBy } = request.query as { from?: string; to?: string; groupBy?: string };
    const groups = await store.report(accountId, {
      from, to,
      groupBy: (groupBy as 'provider' | 'model' | 'agentType') ?? 'provider',
    });
    return reply.send({ groups });
  });

  // GET /api/v1/diagnostics/budget/:accountId — budget status
  app.get('/diagnostics/budget/:accountId', async (request, reply) => {
    const { accountId } = request.params as { accountId: string };
    // Load budget limit from config store (Story 9-1)
    const limit = 100; // Default; wire to config store
    const status = await store.checkBudget(accountId, limit);
    return reply.send(status);
  });
}
```

---

### Task 5: Update DiagnosticsProcessor to Support Store (3 hours)

**File to modify**: `packages/shared/src/telemetry/diagnostics-processor.ts`

Add an optional `IDiagnosticsStore` dependency to `DiagnosticsProcessorOptions`:

```typescript
export interface DiagnosticsProcessorOptions {
  costTracker: IDiagnosticsCostTracker;
  mapProviderName: ProviderNameMapper;
  mapTaskType: TaskTypeMapper;
  logger?: ILogger;
  /** Optional persistent diagnostics store for API-backed mode. */
  diagnosticsStore?: IDiagnosticsStore;
}
```

In the processor function, after recording to costTracker, also write to diagnosticsStore:

```typescript
// After existing costTracker.recordUsage(input):
if (options.diagnosticsStore) {
  try {
    await options.diagnosticsStore.recordBatch(events);
  } catch (err) {
    logger?.warn('Failed to write to diagnostics store', { error: ... });
  }
}
```

---

### Task 6: Wire PgDiagnosticsStore + Update Settings Index (2 hours)

**File to modify**: `packages/api/src/routes/settings/index.ts`

```typescript
export interface SettingsServices {
  configService: ConfigService;
  configStore: IAgentConfigStore;      // Story 9-1
  healthService: HealthService;
  diagnosticsStore: IDiagnosticsStore; // replaces DiagnosticsService
  diagnosticsService: DiagnosticsService; // retained for backward compat
}
```

---

### Task 7: Tests (4 hours)

**File to create**: `packages/api/src/services/diagnostics-store.test.ts`

| # | Test | Assertion |
|---|------|-----------|
| 1 | `record()` inserts a provider:complete event | Row exists with correct fields |
| 2 | `record()` inserts a tool:complete event | Correct extraction of toolName |
| 3 | `recordBatch()` inserts multiple events atomically | All rows present, count matches |
| 4 | `recordBatch([])` returns 0 | No-op |
| 5 | `query()` with no filters returns recent events | Ordered by created_at DESC |
| 6 | `query()` with provider filter | Only matching provider |
| 7 | `query()` with time range filter | Only events in range |
| 8 | `query()` with limit/offset | Correct pagination |
| 9 | `report()` grouped by provider | Correct aggregations |
| 10 | `report()` grouped by model | Correct aggregations |
| 11 | `report()` with time range | Filtered aggregations |
| 12 | `checkBudget()` returns correct spent | SUM matches inserted costs |
| 13 | `checkBudget()` with no usage | spent = 0, remaining = limit |

**File to create**: `packages/api/src/routes/settings/__tests__/diagnostics-routes.test.ts`

| # | Test | Assertion |
|---|------|-----------|
| 14 | POST /diagnostics with single event returns recorded=1 | 200 OK |
| 15 | POST /diagnostics with batch returns recorded=N | 200 OK |
| 16 | GET /diagnostics returns paginated results | Correct shape |
| 17 | GET /diagnostics/report returns groups | Correct shape |
| 18 | GET /diagnostics/budget/:accountId returns status | Correct shape |
| 19 | POST /diagnostics with no body returns 400 | Error message |

**Total tests**: ~19

---

## Files to Create

| # | File Path | Purpose |
|---|-----------|---------|
| 1 | `database/migrations/013_provider_diagnostics.sql` | DDL + indexes |
| 2 | `packages/api/src/services/diagnostics-store.ts` | Interface + types |
| 3 | `packages/api/src/services/pg-diagnostics-store.ts` | Postgres implementation |
| 4 | `packages/api/src/services/diagnostics-store.test.ts` | Service tests |
| 5 | `packages/api/src/routes/settings/__tests__/diagnostics-routes.test.ts` | Route tests |

## Files to Modify

| # | File Path | Change |
|---|-----------|--------|
| 1 | `packages/api/src/routes/settings/diagnostics-routes.ts` | Replace placeholder with full CRUD |
| 2 | `packages/api/src/routes/settings/index.ts` | Wire PgDiagnosticsStore |
| 3 | `packages/shared/src/telemetry/diagnostics-processor.ts` | Add optional store dependency |

---

## Dependencies

- **Story 9-1** (account context for scoping; config store for budget limit)
- **Epic 17** (tenants table for account_id FK -- deferred)
- **Epic 18** (JWT auth for API endpoints)

## Migration from Existing Code

The existing `DiagnosticsService` in `packages/api/src/services/settings/DiagnosticsService.ts` is an in-memory ring buffer of 500 events. The migration:

1. `PgDiagnosticsStore` replaces `DiagnosticsService` for persistent storage.
2. `DiagnosticsService` is retained temporarily for backward compatibility with the dashboard SSE bridge.
3. The `DiagnosticsProcessor` in `packages/shared/src/telemetry/diagnostics-processor.ts` gains an optional `diagnosticsStore` parameter. When set, it writes to Postgres in addition to (or instead of) the in-memory cost tracker.
4. Elsa workflows call `POST /api/v1/diagnostics` instead of the C# `RecordDiagnosticsActivity` local state (wired in Story 9-11).

---

## Estimated Effort

| Task | Hours |
|------|-------|
| Migration SQL (DDL + indexes) | 2 |
| IDiagnosticsStore interface + types | 2 |
| PgDiagnosticsStore implementation | 5 |
| Fastify routes (POST, GET, GET report, GET budget) | 4 |
| DiagnosticsProcessor update | 3 |
| Settings index wiring | 2 |
| Tests (19 tests) | 4 |
| **Total** | **22 hours** |
