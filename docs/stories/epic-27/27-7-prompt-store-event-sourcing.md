# Story 27-7: Prompt Store Event Sourcing

Status: ready-for-dev

## Story

As a **compliance officer / platform operator**,
I want all prompt changes to emit DCB events with full audit metadata,
so that I can trace who changed which prompts, when, and why -- supporting time-travel debugging and regulatory compliance.

## Acceptance Criteria

1. `PROMPT.CREATED` event emitted when a new prompt override is created (account or system)
2. `PROMPT.UPDATED` event emitted when an existing prompt is modified (template, system prompt, tools, max tokens)
3. `PROMPT.DELETED` event emitted when an account override is removed
4. `PROMPT.RESET` event emitted when a system default is reset to the hardcoded original
5. All events include tags: `accountId` (or `null` for system defaults), `role`, `action`, `userId` (who made the change)
6. Event `data` includes: previous version number, new version number, changed fields (diff summary, not full template text to avoid bloating the event store)
7. Events follow the DCB pattern: `AGGREGATE.ACTION.STATUS` naming (e.g., `PROMPT.CREATED.SUCCESS`, `PROMPT.UPDATED.SUCCESS`)
8. Events are emitted from the `IPromptStore` implementation (not the route handler) to ensure all mutation paths are covered
9. Event queries support: "show all prompt changes for account X", "show all changes by user Y", "show history of role=developer, action=implement"
10. Events are queryable via the existing event store API (`GET /api/v1/events?tags.role=developer&tags.action=implement`)
11. Backward compatibility: if the event store is unavailable, prompt mutations still succeed (emit is best-effort, not transactional)

## Technical Context

### DCB Event Format

From the Tamma architecture, all events follow this structure:

```typescript
interface DomainEvent {
  id: string;                    // UUID v7 (time-sortable)
  type: string;                  // "PROMPT.UPDATED.SUCCESS"
  timestamp: string;             // ISO 8601 millisecond precision
  tags: {
    accountId?: string;
    role?: string;
    action?: string;
    userId?: string;
    [key: string]: string | undefined;
  };
  metadata: {
    workflowVersion: string;
    eventSource: 'system' | 'plugin';
  };
  data: Record<string, unknown>;
}
```

### Event Types

| Event Type | Trigger | Tags | Data |
|-----------|---------|------|------|
| `PROMPT.CREATED.SUCCESS` | New prompt created (no prior row) | accountId, role, action, userId | `{ version: 1, enableTools, maxTokens }` |
| `PROMPT.UPDATED.SUCCESS` | Existing prompt modified | accountId, role, action, userId | `{ previousVersion, newVersion, changedFields: ["template", "maxTokens", ...] }` |
| `PROMPT.DELETED.SUCCESS` | Account override removed | accountId, role, action, userId | `{ deletedVersion }` |
| `PROMPT.RESET.SUCCESS` | System default restored to hardcoded | role, action, userId | `{ previousVersion, newVersion, resetFrom: "custom", resetTo: "hardcoded" }` |

### Event Emission Pattern

The `IPromptStore` methods emit events after successful database mutations:

```typescript
async upsert(accountId, role, action, input): Promise<PromptTemplate> {
  const existing = await this._getRow(accountId, role, action);
  const result = await this._upsertRow(accountId, role, action, input);

  // Emit event
  const eventType = existing
    ? 'PROMPT.UPDATED.SUCCESS'
    : 'PROMPT.CREATED.SUCCESS';

  await this.eventStore.append({
    type: eventType,
    tags: { accountId: accountId ?? undefined, role, action, userId: this._currentUserId() },
    metadata: { workflowVersion: '1.0.0', eventSource: 'system' },
    data: existing
      ? { previousVersion: existing.version, newVersion: result.version, changedFields: this._diffFields(existing, result) }
      : { version: result.version, enableTools: result.enableTools, maxTokens: result.maxTokens },
  });

  return result;
}
```

### changedFields Calculation

To avoid storing full template text in events (which could be 10-50 KB per event), the `data.changedFields` field lists which fields changed:

```typescript
function diffFields(before: PromptTemplate, after: PromptTemplate): string[] {
  const fields: string[] = [];
  if (before.template !== after.template) fields.push('template');
  if (before.systemPrompt !== after.systemPrompt) fields.push('systemPrompt');
  if (before.enableTools !== after.enableTools) fields.push('enableTools');
  if (before.maxTokens !== after.maxTokens) fields.push('maxTokens');
  if (JSON.stringify(before.variables) !== JSON.stringify(after.variables)) fields.push('variables');
  return fields;
}
```

### User Context

The `userId` tag identifies who made the change. This comes from:
- API routes: `request.userId` from auth middleware
- System operations (seed, reset): a sentinel system user ID or `null`

The `IPromptStore` needs access to the current user context. This can be:
1. Passed as a parameter to each method: `upsert(accountId, role, action, input, userId)`
2. Set on the store instance per-request (via middleware): `store.setContext({ userId })`
3. Injected via a context provider pattern

Option 1 (explicit parameter) is cleanest and avoids hidden state.

### Files to Create

| File | Purpose |
|------|---------|
| `packages/api/src/services/prompt-store-events.ts` | Event emission helper functions |
| `packages/api/src/services/prompt-store-events.test.ts` | Unit tests for event emission |

### Files to Modify

| File | Change |
|------|--------|
| `packages/api/src/services/pg-prompt-store.ts` | Add event emission to upsert, delete, resetSystemDefault |
| `packages/api/src/services/prompt-store.ts` | Update IPromptStore to accept optional userId parameter |
| `packages/api/src/routes/prompts/prompt-routes.ts` | Pass userId from auth context to store methods |

## Implementation Plan

### Step 1: Define Event Types

Create constants for the prompt event types:

```typescript
// packages/api/src/services/prompt-store-events.ts

export const PROMPT_EVENT_TYPES = {
  CREATED: 'PROMPT.CREATED.SUCCESS',
  UPDATED: 'PROMPT.UPDATED.SUCCESS',
  DELETED: 'PROMPT.DELETED.SUCCESS',
  RESET: 'PROMPT.RESET.SUCCESS',
} as const;
```

### Step 2: Event Emission Helper

Create a helper that constructs and appends prompt events:

```typescript
export async function emitPromptEvent(
  eventStore: IEventStore,
  type: string,
  tags: { accountId?: string; role: string; action: string; userId?: string },
  data: Record<string, unknown>,
): Promise<void> {
  try {
    await eventStore.append({
      type,
      tags: {
        ...(tags.accountId !== undefined ? { accountId: tags.accountId } : {}),
        role: tags.role,
        action: tags.action,
        ...(tags.userId !== undefined ? { userId: tags.userId } : {}),
      },
      metadata: {
        workflowVersion: '1.0.0',
        eventSource: 'system',
      },
      data,
    });
  } catch (error) {
    // Best-effort: log the failure but do not block the mutation
    logger.warn({ error, type, tags }, 'Failed to emit prompt event');
  }
}
```

### Step 3: Integrate into PgPromptStore

Inject `IEventStore` into `PgPromptStore` constructor:

```typescript
class PgPromptStore implements IPromptStore {
  constructor(
    private readonly pool: pg.Pool,
    private readonly eventStore: IEventStore,
    private readonly logger: Logger,
  ) {}
}
```

Emit events in `upsert()`, `delete()`, and `resetSystemDefault()`.

### Step 4: Add userId to Mutation Methods

Update `IPromptStore` method signatures:

```typescript
upsert(accountId: string | null, role: string, action: string, input: UpsertPromptInput, userId?: string): Promise<PromptTemplate>;
delete(accountId: string, role: string, action: string, userId?: string): Promise<boolean>;
resetSystemDefault(role: string, action: string, userId?: string): Promise<PromptTemplate | undefined>;
```

### Step 5: Pass userId from Routes

Update route handlers to pass `request.userId`:

```typescript
const updated = await store.upsert(accountId, role, action, input, request.userId);
```

## Implementation Notes

1. Event emission is **best-effort, not transactional**. If the event store append fails, the prompt mutation has already succeeded. This avoids coupling prompt availability to event store availability.
2. Full template text is NOT stored in events to avoid bloating the event store. Only `changedFields` (a list of field names) is stored. If full history is needed, the `version` field in the prompts table can be used with a future "prompt versions" table.
3. The `InMemoryPromptStore` used in tests can emit events to an `InMemoryEventStore` or skip emission entirely (configurable).
4. Event tags enable flexible queries: `tags.accountId = 'acme-uuid'` for all changes to an account, `tags.role = 'developer' AND tags.action = 'implement'` for a specific prompt's history.
5. The `PROMPT.RESET.SUCCESS` event is distinct from `PROMPT.UPDATED.SUCCESS` because it carries semantic meaning: the admin explicitly chose to revert to the platform default. This is important for audit trails.
6. System seed operations (the initial migration) do NOT emit events. Only user-initiated changes emit events.

## Testing Strategy

### Unit Tests

1. `emitPromptEvent()` calls `eventStore.append()` with correct event structure
2. `emitPromptEvent()` includes accountId tag when provided
3. `emitPromptEvent()` omits accountId tag when null (system default)
4. `emitPromptEvent()` does not throw when eventStore.append() fails (best-effort)
5. `diffFields()` correctly identifies changed fields
6. `diffFields()` returns empty array when nothing changed

### Integration Tests (with InMemory stores)

7. `upsert()` emits `PROMPT.CREATED.SUCCESS` for new prompt
8. `upsert()` emits `PROMPT.UPDATED.SUCCESS` for existing prompt with correct changedFields
9. `delete()` emits `PROMPT.DELETED.SUCCESS` with deleted version
10. `resetSystemDefault()` emits `PROMPT.RESET.SUCCESS`
11. Events are queryable by accountId tag
12. Events are queryable by role + action tags

### Backward Compatibility

13. `InMemoryPromptStore` works without event store (events skipped)
14. Existing prompt tests pass without providing userId

## Dependencies

- **Story 27-2** (Prompt Store Service) -- `IPromptStore` must exist to integrate events into
- **Epic 4** (Event Sourcing & Audit Trail) -- `IEventStore` interface must exist
- Internal: `packages/events/` or `packages/api/src/services/event-store.ts`

## Estimated Effort

| Task | Hours |
|------|-------|
| Event type constants and helper function | 1 |
| diffFields utility | 0.5 |
| PgPromptStore event integration (upsert, delete, reset) | 2 |
| IPromptStore interface update (userId parameter) | 0.5 |
| Route handler updates (pass userId) | 1 |
| Unit tests (6 tests) | 1.5 |
| Integration tests (7 tests) | 1.5 |
| **Total** | **8 hours** |

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-04-08 | 1.0 | Initial story creation | Architecture Team |
