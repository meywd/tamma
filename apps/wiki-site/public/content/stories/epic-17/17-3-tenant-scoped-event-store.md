---
title: "Story 17.3: Tenant-Scoped Event Store"
sidebar:
  order: 170
---

Status: ready-for-dev

## Story

As a **platform engineer**,
I want every DCB event tagged with a `tenant_id` and event queries scoped to the current tenant,
so that the complete audit trail is isolated per organization and one tenant's events can never be observed or replayed by another.

## Acceptance Criteria

1. `EngineEvent` interface gains a `tenantId: string` field
2. `EngineEvent.tags` (in the DCB pattern from the architecture) includes `tenantId` as a first-class tag
3. `IEventStore.record()` requires `tenantId` in the input (no default — caller must provide it)
4. `IEventStore.getEvents()` accepts a `tenantId` parameter and returns only events for that tenant
5. `IEventStore.getLastEvent()` accepts a `tenantId` parameter and returns the last event for that tenant
6. The in-memory `IEventStore` implementation filters events by `tenantId` in all query methods
7. If/when a PostgreSQL event store table exists, it has a `tenant_id UUID NOT NULL` column with a B-tree index, and RLS policy applied (consistent with Story 17.2)
8. The `clear()` method on the event store clears only the current tenant's events (not all events globally)
9. CLI/self-hosted mode passes `DEFAULT_TENANT_ID` as the `tenantId` for all events
10. Event replay/time-travel debugging remains functional but scoped to a single tenant's event stream
11. Existing event emission call sites are updated to include `tenantId` from the current context
12. No cross-tenant event leakage: querying tenant A's events with tenant B's ID returns zero results

## Technical Context

### Current Event Store Interface

From `packages/shared/src/types/index.ts`:

```typescript
export interface EngineEvent {
  id: string;
  type: EngineEventType;
  timestamp: number;
  issueNumber?: number;
  data: Record<string, unknown>;
}

export interface IEventStore {
  record(event: Omit<EngineEvent, 'id' | 'timestamp'>): EngineEvent;
  getEvents(issueNumber?: number): EngineEvent[];
  getLastEvent(type: EngineEventType): EngineEvent | undefined;
  clear(): void;
}
```

The interface has no concept of tenancy. All events are stored in one flat collection.

### DCB Event Pattern from Architecture

The architecture document defines a richer event model with JSONB tags:

```typescript
interface DomainEvent {
  id: string;
  type: string;
  timestamp: string;
  tags: {
    issueId?: string;
    prId?: string;
    userId?: string;
    mode?: 'dev' | 'business';
    provider?: string;
    [key: string]: string | undefined;
  };
  metadata: { workflowVersion: string; eventSource: 'system' | 'plugin' };
  data: Record<string, unknown>;
}
```

`tenantId` will be added to `tags` for flexible querying, AND as a top-level column for RLS enforcement. The tag is redundant with the column but necessary for the DCB query pattern (tags-based projections).

### Tenant Scoping Strategy

Two complementary approaches:

1. **Application-level**: All `IEventStore` methods accept or infer `tenantId` and filter in code
2. **Database-level**: RLS on the event store table filters by `tenant_id` column using `app.current_tenant_id` session variable (defense-in-depth, same as Story 17.2)

### Files to Modify

| File | Change |
|------|--------|
| `packages/shared/src/types/index.ts` | Add `tenantId` to `EngineEvent`, update `IEventStore` signature |
| `packages/events/src/event-store.ts` (if exists) | Update implementation to scope by tenant |
| All call sites that use `IEventStore.record()` | Pass `tenantId` from execution context |

### Files to Create

| File | Purpose |
|------|---------|
| `packages/shared/src/types/__tests__/event-store-tenant.test.ts` | Tests for tenant-scoped event store behavior |
| `database/migrations/010_event_store_tenant.sql` (if PG event table exists) | Add `tenant_id` column and RLS policy to event store table |

## Implementation Plan

### Step 1: Update EngineEvent Interface

```typescript
export interface EngineEvent {
  id: string;
  type: EngineEventType;
  timestamp: number;
  tenantId: string;
  issueNumber?: number;
  data: Record<string, unknown>;
}
```

### Step 2: Update IEventStore Interface

```typescript
export interface IEventStore {
  record(event: Omit<EngineEvent, 'id' | 'timestamp'>): EngineEvent;
  getEvents(tenantId: string, issueNumber?: number): EngineEvent[];
  getLastEvent(tenantId: string, type: EngineEventType): EngineEvent | undefined;
  clear(tenantId: string): void;
}
```

Key change: every query method now takes `tenantId` as the first parameter. This makes tenant scoping explicit at the type level, preventing accidental cross-tenant queries.

### Step 3: Update In-Memory Implementation

```typescript
class InMemoryEventStore implements IEventStore {
  private events: EngineEvent[] = [];
  private nextId = 1;

  record(event: Omit<EngineEvent, 'id' | 'timestamp'>): EngineEvent {
    const recorded: EngineEvent = {
      ...event,
      id: String(this.nextId++),
      timestamp: Date.now(),
    };
    this.events.push(recorded);
    return recorded;
  }

  getEvents(tenantId: string, issueNumber?: number): EngineEvent[] {
    return this.events.filter((e) => {
      if (e.tenantId !== tenantId) return false;
      if (issueNumber !== undefined && e.issueNumber !== issueNumber) return false;
      return true;
    });
  }

  getLastEvent(tenantId: string, type: EngineEventType): EngineEvent | undefined {
    const tenantEvents = this.events.filter(
      (e) => e.tenantId === tenantId && e.type === type,
    );
    return tenantEvents[tenantEvents.length - 1];
  }

  clear(tenantId: string): void {
    this.events = this.events.filter((e) => e.tenantId !== tenantId);
  }
}
```

### Step 4: Update All Event Emission Sites

Search the codebase for all `eventStore.record(` calls and add the `tenantId` field. In self-hosted/CLI mode, this comes from the engine context which defaults to `DEFAULT_TENANT_ID`. In SaaS mode, it comes from the request's tenant context.

The engine's `LaunchContext` (from `packages/shared/src/types/index.ts`) should be extended with a `tenantId` field:

```typescript
export interface LaunchContext {
  mode: 'cli' | 'service' | 'web' | 'worker';
  config: TammaConfig;
  logger: ILogger;
  tenantId: string;  // NEW: defaults to DEFAULT_TENANT_ID
}
```

### Step 5: PostgreSQL Event Store Table (if applicable)

If a PostgreSQL-backed event store table exists or is created:

```sql
-- Add tenant_id to the event store table
ALTER TABLE engine_events
  ADD COLUMN IF NOT EXISTS tenant_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'
  REFERENCES tenants(id);

CREATE INDEX IF NOT EXISTS idx_engine_events_tenant_id ON engine_events (tenant_id);

-- Composite index for the most common query: events by tenant + issue
CREATE INDEX IF NOT EXISTS idx_engine_events_tenant_issue
  ON engine_events (tenant_id, issue_number) WHERE issue_number IS NOT NULL;

-- RLS policy
ALTER TABLE engine_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE engine_events FORCE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation_policy ON engine_events
  USING (tenant_id = current_setting('app.current_tenant_id', true)::uuid)
  WITH CHECK (tenant_id = current_setting('app.current_tenant_id', true)::uuid);
```

### Step 6: DCB Tag Integration

For the richer `DomainEvent` type from the architecture (if/when Emmett is integrated), add `tenantId` to the `tags` object:

```typescript
interface DomainEvent {
  // ... existing fields ...
  tags: {
    tenantId: string;  // Required, not optional
    issueId?: string;
    prId?: string;
    // ...
  };
}
```

This enables DCB projections scoped to a tenant without relying solely on RLS.

## Implementation Notes

1. The `tenantId` parameter is required (not optional) on all `IEventStore` methods to prevent accidental global queries. Callers must explicitly pass it.
2. The `LaunchContext.tenantId` defaults to `DEFAULT_TENANT_ID` so CLI mode requires zero configuration changes.
3. Time-travel debugging replays events within a single tenant's stream. Cross-tenant replay is not supported and should be explicitly rejected.
4. The event store `clear(tenantId)` method deletes only the specified tenant's events, not all events. This is critical for tenant offboarding/data deletion (GDPR).
5. If Emmett is the event store backend, its stream naming may incorporate the tenant ID (e.g., `tenant:{id}:issue:{issueId}`). Research Emmett's stream partitioning capabilities before implementation.
6. Performance: The composite index `(tenant_id, issue_number)` covers the most common query pattern (get events for a specific issue in a specific tenant).

## Testing Strategy

### Unit Tests

1. `record()` stores event with correct `tenantId`
2. `getEvents(tenantA)` returns only tenant A's events, not tenant B's
3. `getEvents(tenantA, issueNumber)` filters by both tenant and issue
4. `getLastEvent(tenantA, type)` returns last event of that type for tenant A only
5. `clear(tenantA)` removes only tenant A's events, tenant B's remain
6. Events from multiple tenants stored interleaved — queries correctly separate them
7. Empty tenant (no events) returns empty array, not error

### Integration Tests (if PG event table exists)

8. RLS prevents cross-tenant event reads at the database level
9. Event insertion with wrong `tenant_id` (mismatched with session variable) is rejected by RLS WITH CHECK
10. EXPLAIN ANALYZE shows index usage on `(tenant_id, issue_number)` composite index

### Backward Compatibility

11. All existing engine tests pass with `DEFAULT_TENANT_ID` injected
12. CLI mode produces events with `tenantId = DEFAULT_TENANT_ID`

## Dependencies

- **Story 17.1** (Tenant Model + Database Schema) — `tenants` table must exist for FK reference
- Internal: `packages/shared/src/types/index.ts` (EngineEvent, IEventStore interfaces)
- Internal: `packages/events/src/` (event store implementation)

## Estimated Effort

| Task | Hours |
|------|-------|
| Update EngineEvent + IEventStore interfaces | 1 |
| Update InMemory event store implementation | 1 |
| Update LaunchContext with tenantId | 0.5 |
| Update all event emission call sites | 2 |
| PostgreSQL event store migration (if applicable) | 1.5 |
| DCB tag integration planning | 1 |
| Unit tests | 2 |
| Integration tests | 1 |
| **Total** | **10 hours** |

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-28 | 1.0 | Initial story creation | Architecture Team |
