# Story 9-2: Diagnostics Service + API

## User Story

As a platform operator, I want all provider call diagnostics (costs, tokens, latency, errors) stored in Postgres and queryable via API, so that both the TypeScript engine and Elsa workflows write to the same store and I get a unified view of usage.

## Goal

Build a Postgres-backed diagnostics service with API endpoints for recording and querying provider call metrics. Replace the in-memory `DiagnosticsQueue` drain-to-cost-tracker pattern with a durable store. Both the TS engine (via in-process calls) and Elsa workflows (via HTTP API) write diagnostics to the same Postgres tables.

## Acceptance Criteria

1. A `provider_diagnostics` Postgres table stores per-call diagnostics records.
2. API endpoints:
   - `POST /api/v1/diagnostics` -- record a diagnostics event (used by Elsa and any external caller).
   - `GET /api/v1/diagnostics` -- query diagnostics with filters (provider, model, time range, account).
   - `GET /api/v1/diagnostics/report` -- generate aggregated cost/usage report.
   - `GET /api/v1/diagnostics/budget/:accountId` -- check current budget status against limits.
3. The existing `InstrumentedAgentProvider` and `InstrumentedLLMProvider` in `packages/providers/src/` continue to emit events to the `DiagnosticsQueue`, which now drains to the Postgres store (via the service) instead of only the in-memory cost tracker.
4. The `DiagnosticsProcessor` (`packages/shared/src/telemetry/diagnostics-processor.ts`) is updated to write to the diagnostics service in addition to (or instead of) the cost tracker.
5. Elsa's `RecordDiagnosticsActivity.cs` is replaced with an HTTP call to `POST /api/v1/diagnostics`.

## Technical Context

### Existing Files

- `packages/shared/src/telemetry/diagnostics-event.ts` -- `DiagnosticsEvent` types
- `packages/shared/src/telemetry/diagnostics-queue.ts` -- `DiagnosticsQueue` class
- `packages/shared/src/telemetry/diagnostics-processor.ts` -- processor that maps to cost tracker
- `packages/providers/src/instrumented-agent-provider.ts` -- emits `provider:call`/`provider:complete`/`provider:error`
- `packages/providers/src/instrumented-llm-provider.ts` -- emits for LLM providers
- `packages/providers/src/provider-name-mapping.ts` -- safe provider name validation
- `packages/api/src/routes/settings/diagnostics-routes.ts` -- placeholder diagnostics routes
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/RecordDiagnosticsActivity.cs` -- C# diagnostics (to be replaced)
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/LlmCallModels.cs` -- `ProviderAttemptDiagnostic`, `BudgetState`

### Database Schema

```sql
CREATE TABLE provider_diagnostics (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  account_id UUID NULL REFERENCES tenants(id),
  event_type TEXT NOT NULL,          -- 'provider:complete', 'provider:error', 'tool:complete', etc.
  provider_name TEXT NOT NULL,
  model TEXT,
  agent_type TEXT,
  project_id TEXT,
  engine_id TEXT,
  task_id TEXT,
  task_type TEXT,
  input_tokens INTEGER DEFAULT 0,
  output_tokens INTEGER DEFAULT 0,
  latency_ms INTEGER DEFAULT 0,
  cost_usd NUMERIC(12, 6) DEFAULT 0,
  success BOOLEAN NOT NULL DEFAULT false,
  error_code TEXT,
  error_message TEXT,
  correlation_id UUID,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_diagnostics_account_created ON provider_diagnostics (account_id, created_at DESC);
CREATE INDEX idx_diagnostics_provider ON provider_diagnostics (provider_name, created_at DESC);
```

### API Routes

```
POST /api/v1/diagnostics
  → Body: DiagnosticsEvent (or batch of events)
  → Inserts into provider_diagnostics
  → Returns: { recorded: number }

GET /api/v1/diagnostics
  → Query params: provider, model, from, to, limit, offset
  → accountId from JWT
  → Returns: { items: DiagnosticsRecord[], total: number }

GET /api/v1/diagnostics/report
  → Query params: from, to, groupBy (provider | model | agentType)
  → accountId from JWT
  → Returns: { groups: [{ key, totalCost, totalTokens, avgLatency, errorRate, count }] }

GET /api/v1/diagnostics/budget/:accountId
  → Returns: { spent: number, limit: number, remaining: number, percentUsed: number }
```

### Architecture

```
TS Engine (in-process)              Elsa Workflow (C#)
       │                                   │
  emit() to                           HTTP POST to
  DiagnosticsQueue                    /api/v1/diagnostics
       │                                   │
  drain to                                 │
  DiagnosticsService ◄─────────────────────┘
       │
  INSERT INTO provider_diagnostics
```

## Files

- CREATE `packages/api/src/services/diagnostics-store.ts` -- Postgres-backed diagnostics service
- CREATE `packages/api/src/services/diagnostics-store.test.ts`
- MODIFY `packages/api/src/routes/settings/diagnostics-routes.ts` -- implement query/report/budget endpoints
- CREATE `packages/api/src/routes/settings/diagnostics-ingest-routes.ts` -- POST endpoint for recording
- CREATE `database/migrations/NNNN_create_provider_diagnostics.sql`
- MODIFY `packages/shared/src/telemetry/diagnostics-processor.ts` -- update to write to diagnostics service

## Dependencies

- **Story 9-1** (account context for scoping)
- **Epic 16** (tenants table)
- **Epic 17** (JWT auth)

## Effort Estimate

**20 hours**

- 4h: Database migration + indexes
- 6h: Diagnostics store service (insert, query, report aggregation, budget check)
- 4h: API routes (POST ingest, GET query, GET report, GET budget)
- 3h: Update DiagnosticsProcessor to write to store
- 3h: Tests (service + routes + processor)
