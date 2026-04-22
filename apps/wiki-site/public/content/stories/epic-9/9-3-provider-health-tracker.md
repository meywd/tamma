---
title: "Story 9-3: Health Tracker Service + API"
sidebar:
  order: 90
---

## User Story

As a platform operator, I want circuit breaker state shared across all callers (TypeScript engine and Elsa workflows) via a persistent store and API, so that when a provider is marked unhealthy by one caller, all callers skip it.

## Goal

Promote the existing in-process `ProviderHealthTracker` to a service backed by Postgres (or Redis for hot-path reads). Expose API endpoints so Elsa workflows can check and update health state without re-implementing circuit breakers in C#. The existing `ProviderHealthTracker` class in `packages/providers/src/provider-health.ts` remains the in-process implementation; the API service wraps it with persistence.

## Acceptance Criteria

1. Circuit breaker state is persisted in Postgres (with optional Redis cache for hot reads).
2. State is shared: a failure recorded by Elsa trips the circuit for the TS engine and vice versa.
3. API endpoints:
   - `GET /api/v1/health/providers` -- returns health status for all tracked provider+model keys.
   - `GET /api/v1/health/providers/:key` -- returns health status for a specific key.
   - `POST /api/v1/health/providers/:key/reset` -- manually reset circuit breaker (admin only).
   - `POST /api/v1/health/providers/:key/failure` -- record a failure (used by Elsa).
   - `POST /api/v1/health/providers/:key/success` -- record a success (used by Elsa).
4. The existing `ProviderHealthTracker` class continues to work in-process for the TS engine, but syncs state with the persistent store on circuit transitions.
5. `onCircuitChange` callback publishes state transitions to the store and optionally to SSE for real-time dashboard updates.
6. Elsa's `CheckCircuitBreakerActivity.cs` is replaced with an HTTP call to `GET /api/v1/health/providers/:key`.

## Technical Context

### Existing Files

- `packages/providers/src/provider-health.ts` -- `ProviderHealthTracker` class (in-memory, sliding window, half-open probing)
- `packages/providers/src/types.ts` -- `IProviderHealthTracker`, `HealthStatusEntry` interfaces
- `packages/providers/src/errors.ts` -- `createProviderError()`, `isProviderError()`
- `packages/api/src/routes/settings/health-routes.ts` -- placeholder health routes
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CheckCircuitBreakerActivity.cs` -- C# circuit breaker (to be replaced)
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/LlmCallModels.cs` -- `CircuitBreakerState`, `CircuitBreakerStatus`

### Database Schema

```sql
CREATE TABLE provider_health (
  key TEXT PRIMARY KEY,              -- "provider:model" e.g. "openrouter:z-ai/z1-mini"
  circuit_open BOOLEAN NOT NULL DEFAULT false,
  circuit_open_until TIMESTAMPTZ,
  failure_count INTEGER NOT NULL DEFAULT 0,
  last_failure_at TIMESTAMPTZ,
  last_success_at TIMESTAMPTZ,
  half_open_in_progress BOOLEAN NOT NULL DEFAULT false,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

### API Routes

```
GET /api/v1/health/providers
  → Returns: Record<string, { healthy, failures, circuitOpen, circuitOpenUntil }>

GET /api/v1/health/providers/:key
  → Returns: { healthy, failures, circuitOpen, circuitOpenUntil, halfOpen }

POST /api/v1/health/providers/:key/failure
  → Body: { error?: string, retryable?: boolean }
  → Updates failure count, may open circuit
  → Returns: { circuitOpen, failures }

POST /api/v1/health/providers/:key/success
  → Resets circuit to closed
  → Returns: { circuitOpen: false, failures: 0 }

POST /api/v1/health/providers/:key/reset  (admin only)
  → Deletes health state for key
  → Returns: { reset: true }
```

### Architecture

```
TS Engine                           Elsa Workflow
  │                                      │
  ProviderHealthTracker                  HTTP calls to
  (in-memory, syncs on                  /api/v1/health/...
   circuit transitions)                      │
  │                                          │
  └──── onCircuitChange ───────►  HealthService ◄──────┘
                                       │
                                  provider_health (Postgres)
```

## Files

- CREATE `packages/api/src/services/health-store.ts` -- Postgres-backed health service
- CREATE `packages/api/src/services/health-store.test.ts`
- MODIFY `packages/api/src/routes/settings/health-routes.ts` -- implement health endpoints
- CREATE `database/migrations/014_provider_health.sql` (migration 014 -- see `/docs/stories/migration-ordering.md`)
- MODIFY `packages/providers/src/provider-health.ts` -- add optional persistence sync via `onCircuitChange`

## Dependencies

- None (can start immediately; no account scoping needed for health -- health is global per deployment)

## Effort Estimate

**16 hours**

- 3h: Database migration
- 5h: Health store service (isHealthy, recordFailure, recordSuccess, getStatus, reset with persistence)
- 4h: API routes with input validation
- 2h: Sync mechanism between in-process tracker and persistent store
- 2h: Tests
