# Story 27-7: Prompt Store Event Sourcing — Implementation Plan

## Overview

Integrate DCB event emission into the `PgPromptStore` so that all prompt mutations (create, update, delete, reset) emit structured events with audit metadata. Events are best-effort (non-blocking) and include tags for accountId, role, action, and userId. A `diffFields()` utility identifies which fields changed without storing full template text in events.

---

## Step-by-Step Implementation Tasks

### Task 1: Define Event Type Constants (0.5 hours)

**File to create**: `packages/api/src/services/prompt-store-events.ts`

```typescript
/**
 * Prompt Store Event Constants and Helpers
 *
 * DCB event types for prompt mutation audit trail.
 * All events follow the AGGREGATE.ACTION.STATUS pattern.
 *
 * Story 27-7: Prompt Store Event Sourcing
 */

import type { PromptTemplate } from './default-prompts.js';

// ---------------------------------------------------------------------------
// Event type constants
// ---------------------------------------------------------------------------

export const PROMPT_EVENT_TYPES = {
  CREATED: 'PROMPT.CREATED.SUCCESS',
  UPDATED: 'PROMPT.UPDATED.SUCCESS',
  DELETED: 'PROMPT.DELETED.SUCCESS',
  RESET: 'PROMPT.RESET.SUCCESS',
} as const;

export type PromptEventType = (typeof PROMPT_EVENT_TYPES)[keyof typeof PROMPT_EVENT_TYPES];

// ---------------------------------------------------------------------------
// Event tags
// ---------------------------------------------------------------------------

export interface PromptEventTags {
  accountId?: string;
  role: string;
  action: string;
  userId?: string;
}

// ---------------------------------------------------------------------------
// Event store interface (minimal subset needed by prompt events)
// ---------------------------------------------------------------------------

export interface IEventStore {
  append(event: {
    type: string;
    tags: Record<string, string | undefined>;
    metadata: { workflowVersion: string; eventSource: 'system' | 'plugin' };
    data: Record<string, unknown>;
  }): Promise<void>;
}

// ---------------------------------------------------------------------------
// Event emission helper
// ---------------------------------------------------------------------------

export interface PromptEventLogger {
  warn: (obj: object, msg: string) => void;
}

/**
 * Emit a prompt event. Best-effort: logs and swallows errors.
 */
export async function emitPromptEvent(
  eventStore: IEventStore | null | undefined,
  type: PromptEventType,
  tags: PromptEventTags,
  data: Record<string, unknown>,
  logger?: PromptEventLogger,
): Promise<void> {
  if (!eventStore) return;

  try {
    const cleanTags: Record<string, string | undefined> = {
      role: tags.role,
      action: tags.action,
    };
    if (tags.accountId !== undefined) {
      cleanTags['accountId'] = tags.accountId;
    }
    if (tags.userId !== undefined) {
      cleanTags['userId'] = tags.userId;
    }

    await eventStore.append({
      type,
      tags: cleanTags,
      metadata: {
        workflowVersion: '1.0.0',
        eventSource: 'system',
      },
      data,
    });
  } catch (error) {
    // Best-effort: log and continue. Prompt mutation already succeeded.
    logger?.warn(
      { error: error instanceof Error ? error.message : String(error), type, tags },
      'Failed to emit prompt event',
    );
  }
}

// ---------------------------------------------------------------------------
// Field diff utility
// ---------------------------------------------------------------------------

/**
 * Compare two PromptTemplate objects and return the list of changed field names.
 * Does NOT include the full template text to avoid bloating the event store.
 */
export function diffFields(before: PromptTemplate, after: PromptTemplate): string[] {
  const fields: string[] = [];
  if (before.template !== after.template) fields.push('template');
  if (before.systemPrompt !== after.systemPrompt) fields.push('systemPrompt');
  if (before.enableTools !== after.enableTools) fields.push('enableTools');
  if (before.maxTokens !== after.maxTokens) fields.push('maxTokens');
  if (JSON.stringify(before.variables) !== JSON.stringify(after.variables)) fields.push('variables');
  return fields;
}
```

---

### Task 2: Update IPromptStore Interface for userId (0.5 hours)

**File to modify**: `packages/api/src/services/prompt-store.ts`

The `IPromptStore` interface from Story 27-2 already includes optional `userId` parameter on mutation methods:

```typescript
upsert(accountId: string | null, role: string, action: string, input: UpsertPromptInput, userId?: string): Promise<PromptTemplate>;
delete(accountId: string, role: string, action: string, userId?: string): Promise<boolean>;
resetSystemDefault(role: string, action: string, userId?: string): Promise<PromptTemplate | undefined>;
upsertSystemDefault(role: string, action: string, input: UpsertPromptInput, userId?: string): Promise<PromptTemplate>;
upsertSystemPrompt(accountId: string | null, role: string, prompt: string, userId?: string): Promise<void>;
```

Verify this is in place. If not, add the optional `userId` parameter.

---

### Task 3: Inject IEventStore into PgPromptStore (2 hours)

**File to modify**: `packages/api/src/services/pg-prompt-store.ts`

#### 3a. Update Constructor

```typescript
import type { IEventStore, PromptEventLogger } from './prompt-store-events.js';
import { PROMPT_EVENT_TYPES, emitPromptEvent, diffFields } from './prompt-store-events.js';

export class PgPromptStore implements IPromptStore {
  constructor(
    private readonly pool: pg.Pool,
    private readonly logger?: PromptEventLogger & { info: (obj: object, msg: string) => void; error: (obj: object, msg: string) => void },
    private readonly eventStore?: IEventStore | null,
  ) {}
```

#### 3b. Emit Events in upsert()

Modify `upsert()` to emit `PROMPT.CREATED.SUCCESS` or `PROMPT.UPDATED.SUCCESS`:

```typescript
async upsert(accountId: string | null, role: string, action: string, input: UpsertPromptInput, userId?: string): Promise<PromptTemplate> {
  // Fetch existing row before mutation (for diff)
  const existing = await this._getRow(accountId, role, action);

  // ... existing INSERT ... ON CONFLICT logic ...
  const result = this._mapRow(queryResult.rows[0]!);

  // Emit event (best-effort, non-blocking)
  if (existing !== undefined) {
    // UPDATE
    const changedFields = diffFields(existing, result);
    if (changedFields.length > 0) {
      emitPromptEvent(
        this.eventStore,
        PROMPT_EVENT_TYPES.UPDATED,
        { accountId: accountId ?? undefined, role, action, userId },
        { previousVersion: existing.version, newVersion: result.version, changedFields },
        this.logger,
      );
    }
  } else {
    // CREATE
    emitPromptEvent(
      this.eventStore,
      PROMPT_EVENT_TYPES.CREATED,
      { accountId: accountId ?? undefined, role, action, userId },
      { version: result.version, enableTools: result.enableTools, maxTokens: result.maxTokens },
      this.logger,
    );
  }

  return result;
}
```

#### 3c. Emit Events in delete()

```typescript
async delete(accountId: string, role: string, action: string, userId?: string): Promise<boolean> {
  // Fetch existing row for version info
  const existing = await this._getRow(accountId, role, action);

  const result = await this.pool.query(
    `DELETE FROM prompts WHERE account_id = $1 AND role = $2 AND action = $3`,
    [accountId, role, action],
  );
  const deleted = (result.rowCount ?? 0) > 0;

  if (deleted && existing !== undefined) {
    emitPromptEvent(
      this.eventStore,
      PROMPT_EVENT_TYPES.DELETED,
      { accountId, role, action, userId },
      { deletedVersion: existing.version },
      this.logger,
    );
  }

  return deleted;
}
```

#### 3d. Emit Events in resetSystemDefault()

```typescript
async resetSystemDefault(role: string, action: string, userId?: string): Promise<PromptTemplate | undefined> {
  const existing = await this._getRow(null, role, action);
  const defaults = getDefaultPrompts();
  const found = defaults.find((d) => d.role === role && d.action === action);
  if (found === undefined) return undefined;

  const result = await this.upsert(null, role, action, {
    template: found.template,
    variables: found.variables,
    systemPrompt: found.systemPrompt,
    enableTools: found.enableTools,
    maxTokens: found.maxTokens,
  }, userId);

  // Emit RESET event (distinct from the UPDATE emitted by upsert)
  emitPromptEvent(
    this.eventStore,
    PROMPT_EVENT_TYPES.RESET,
    { role, action, userId },
    {
      previousVersion: existing?.version ?? 0,
      newVersion: result.version,
      resetFrom: 'custom',
      resetTo: 'hardcoded',
    },
    this.logger,
  );

  return result;
}
```

#### 3e. Add Private Helper _getRow()

```typescript
private async _getRow(accountId: string | null, role: string, action: string): Promise<PromptTemplate | undefined> {
  let result: pg.QueryResult<Record<string, unknown>>;
  if (accountId === null) {
    result = await this.pool.query<Record<string, unknown>>(
      `SELECT * FROM prompts WHERE account_id IS NULL AND role = $1 AND action = $2`,
      [role, action],
    );
  } else {
    result = await this.pool.query<Record<string, unknown>>(
      `SELECT * FROM prompts WHERE account_id = $1 AND role = $2 AND action = $3`,
      [accountId, role, action],
    );
  }
  if (result.rows.length === 0) return undefined;
  return this._mapRow(result.rows[0]!);
}
```

---

### Task 4: Update Route Handlers to Pass userId (1 hour)

**File to modify**: `packages/api/src/routes/prompts/prompt-routes.ts`

In each mutating route handler, pass `request.userId` to the store method:

```typescript
// PUT /api/prompts/:role/:action
const updated = await store.upsert(accountId, role, action, input, request.userId);

// DELETE /api/prompts/:role/:action
const deleted = await store.delete(accountId, role, action, request.userId);

// PUT /api/prompts/system/:role/:action
const updated = await store.upsertSystemDefault(role, action, input, request.userId);

// DELETE /api/prompts/system/:role/:action (reset)
const restored = await store.resetSystemDefault(role, action, request.userId);
```

---

### Task 5: Update Application Wiring (0.5 hours)

**File to modify**: `packages/api/src/index.ts`

Pass the event store instance to `PgPromptStore`:

```typescript
// Before:
const promptStore = new PgPromptStore(pool, logger);

// After:
const promptStore = new PgPromptStore(pool, logger, eventStore);
```

Where `eventStore` is the existing `IEventStore` instance (from Epic 4 or a no-op implementation if not yet available).

---

### Task 6: Update InMemoryPromptStore for Testing (0.5 hours)

**File to modify**: `packages/api/src/services/in-memory-prompt-store.ts`

Add optional event store injection for tests that want to verify event emission:

```typescript
export class InMemoryPromptStore implements IPromptStore {
  constructor(
    private readonly eventStore?: IEventStore | null,
  ) {}

  // In upsert(), delete(), resetSystemDefault():
  // Call emitPromptEvent() if eventStore is provided
}
```

This is optional. Tests can also verify events by checking the event store mock directly.

---

### Task 7: Unit Tests (1.5 hours)

**File to create**: `packages/api/src/services/prompt-store-events.test.ts`

```typescript
import { describe, it, expect, vi } from 'vitest';
import { emitPromptEvent, diffFields, PROMPT_EVENT_TYPES } from './prompt-store-events.js';

describe('emitPromptEvent', () => {
  it('should call eventStore.append with correct event structure', async () => {
    const mockStore = { append: vi.fn().mockResolvedValue(undefined) };
    await emitPromptEvent(
      mockStore,
      PROMPT_EVENT_TYPES.CREATED,
      { accountId: 'acct-123', role: 'developer', action: 'implement', userId: 'user-456' },
      { version: 1, enableTools: true, maxTokens: 4096 },
    );

    expect(mockStore.append).toHaveBeenCalledOnce();
    const event = mockStore.append.mock.calls[0]![0]!;
    expect(event.type).toBe('PROMPT.CREATED.SUCCESS');
    expect(event.tags.accountId).toBe('acct-123');
    expect(event.tags.role).toBe('developer');
    expect(event.tags.action).toBe('implement');
    expect(event.tags.userId).toBe('user-456');
    expect(event.data.version).toBe(1);
  });

  it('should omit accountId tag when null', async () => {
    const mockStore = { append: vi.fn().mockResolvedValue(undefined) };
    await emitPromptEvent(
      mockStore,
      PROMPT_EVENT_TYPES.UPDATED,
      { role: 'developer', action: 'plan' },
      { previousVersion: 1, newVersion: 2, changedFields: ['template'] },
    );

    const event = mockStore.append.mock.calls[0]![0]!;
    expect(event.tags.accountId).toBeUndefined();
  });

  it('should not throw when eventStore.append fails', async () => {
    const mockStore = { append: vi.fn().mockRejectedValue(new Error('DB down')) };
    const mockLogger = { warn: vi.fn() };
    await expect(emitPromptEvent(
      mockStore,
      PROMPT_EVENT_TYPES.CREATED,
      { role: 'developer', action: 'plan' },
      { version: 1 },
      mockLogger,
    )).resolves.toBeUndefined();
    expect(mockLogger.warn).toHaveBeenCalledOnce();
  });

  it('should no-op when eventStore is null', async () => {
    await expect(emitPromptEvent(
      null,
      PROMPT_EVENT_TYPES.CREATED,
      { role: 'developer', action: 'plan' },
      { version: 1 },
    )).resolves.toBeUndefined();
  });
});

describe('diffFields', () => {
  const base = {
    role: 'developer', action: 'plan', version: 1,
    template: 'Plan {{x}}', variables: ['x'], systemPrompt: 'sys',
    enableTools: false, maxTokens: 4096,
    createdAt: '2026-01-01T00:00:00.000Z', updatedAt: '2026-01-01T00:00:00.000Z',
  };

  it('should return empty array when nothing changed', () => {
    expect(diffFields(base, { ...base })).toEqual([]);
  });

  it('should detect template change', () => {
    expect(diffFields(base, { ...base, template: 'Plan {{y}}' })).toEqual(['template']);
  });

  it('should detect multiple changes', () => {
    const after = { ...base, template: 'new', enableTools: true, maxTokens: 8192 };
    const result = diffFields(base, after);
    expect(result).toContain('template');
    expect(result).toContain('enableTools');
    expect(result).toContain('maxTokens');
    expect(result).toHaveLength(3);
  });

  it('should detect variables change', () => {
    expect(diffFields(base, { ...base, variables: ['x', 'y'] })).toEqual(['variables']);
  });

  it('should detect systemPrompt change', () => {
    expect(diffFields(base, { ...base, systemPrompt: 'new sys' })).toEqual(['systemPrompt']);
  });
});
```

---

### Task 8: Integration Tests with InMemory Stores (1.5 hours)

**File to create or extend**: `packages/api/src/services/prompt-store-events.integration.test.ts`

Uses `InMemoryPromptStore` with a mock event store to verify end-to-end event emission.

| # | Test | Assertion |
|---|------|-----------|
| 1 | `upsert()` new prompt emits `PROMPT.CREATED.SUCCESS` | Event type matches, version=1 |
| 2 | `upsert()` existing prompt emits `PROMPT.UPDATED.SUCCESS` | changedFields contains changed fields |
| 3 | `upsert()` with no changes does NOT emit UPDATE | No event when nothing changed |
| 4 | `delete()` emits `PROMPT.DELETED.SUCCESS` | deletedVersion matches |
| 5 | `resetSystemDefault()` emits `PROMPT.RESET.SUCCESS` | resetFrom/resetTo present |
| 6 | Events are queryable by accountId tag | Tag filtering works |
| 7 | Events are queryable by role + action tags | Tag filtering works |

---

## Files to Create

| # | File Path | Purpose |
|---|-----------|---------|
| 1 | `packages/api/src/services/prompt-store-events.ts` | Event constants, helpers, diffFields |
| 2 | `packages/api/src/services/prompt-store-events.test.ts` | Unit tests |
| 3 | `packages/api/src/services/prompt-store-events.integration.test.ts` | Integration tests |

## Files to Modify

| # | File Path | Change |
|---|-----------|--------|
| 1 | `packages/api/src/services/pg-prompt-store.ts` | Add eventStore to constructor, emit events in upsert/delete/reset |
| 2 | `packages/api/src/services/prompt-store.ts` | Verify userId parameter on IPromptStore mutations |
| 3 | `packages/api/src/services/in-memory-prompt-store.ts` | Add optional eventStore for testing |
| 4 | `packages/api/src/routes/prompts/prompt-routes.ts` | Pass request.userId to store methods |
| 5 | `packages/api/src/index.ts` | Pass eventStore to PgPromptStore constructor |

---

## Dependencies

- **Story 27-2** (Prompt Store Service) — `PgPromptStore` and `InMemoryPromptStore` must exist
- **Epic 4** (Event Sourcing) — `IEventStore` interface and implementation
- If Epic 4 is not yet deployed, the `eventStore` parameter defaults to `null` and events are silently skipped

---

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| IEventStore interface not yet available (Epic 4) | Define minimal `IEventStore` interface locally in `prompt-store-events.ts`; replace with import when Epic 4 lands |
| Event emission adds latency to mutations | Best-effort: emit after successful mutation, catch and log errors without blocking |
| `_getRow()` adds an extra query before upsert | One additional SELECT per mutation; acceptable for admin operations (not high-throughput) |
| Full template text stored in events (bloat) | Only `changedFields` (array of field names) stored, not full text |
| `resetSystemDefault` emits both UPDATED (from inner upsert) and RESET | The RESET event carries additional semantic meaning; the UPDATED event can be suppressed by passing a flag to the inner upsert or by emitting RESET instead. Recommendation: suppress the UPDATED event in reset path |

---

## Design Decisions

1. **Best-effort emission**: Events are emitted after the database mutation succeeds. If the event store is unavailable, the mutation still succeeds. This avoids coupling prompt availability to event store availability.

2. **No full template text in events**: Only `changedFields` is stored to avoid 10-50KB per event. The prompt table's `version` column provides history; a future "prompt versions" table can store full snapshots if needed.

3. **userId passed explicitly**: Rather than hidden state on the store instance, `userId` is an explicit parameter on each mutation method. This is cleaner and avoids per-request state management.

4. **Separate RESET event**: `PROMPT.RESET.SUCCESS` is distinct from `PROMPT.UPDATED.SUCCESS` because it carries semantic meaning (admin chose to revert to platform default). This is important for audit trails.

---

## Estimated Effort

| Task | Hours |
|------|-------|
| Event type constants and helper function | 0.5 |
| IPromptStore interface verification | 0.5 |
| PgPromptStore event integration | 2 |
| Route handler userId updates | 1 |
| Application wiring | 0.5 |
| InMemoryPromptStore event support | 0.5 |
| Unit tests (6 tests) | 1.5 |
| Integration tests (7 tests) | 1.5 |
| **Total** | **8 hours** |
