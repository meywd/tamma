# Story 27-14: Convention Store Event Sourcing

Status: ready-for-dev

## Story

As a **compliance officer / platform operator**,
I want all convention changes to emit DCB events with full audit metadata,
so that I can trace who changed which conventions, when, and why — supporting time-travel debugging and regulatory compliance.

## Acceptance Criteria

1. `CONVENTION.CREATED.SUCCESS` event emitted when a new convention is created (tenant override or system default)
2. `CONVENTION.UPDATED.SUCCESS` event emitted when an existing convention is modified (body, keywords, priority, enabled, etc.)
3. `CONVENTION.DELETED.SUCCESS` event emitted when a convention is removed
4. `CONVENTION.RESET.SUCCESS` event emitted when a system default is reset to the hardcoded original from `ConventionTemplates.cs`
5. All events include tags: `tenantId` (or `null` for system defaults), `key`, `category`, `userId` (who made the change)
6. Event `data` includes: previous version number, new version number, changed fields (diff summary, not full body text)
7. Events follow the DCB pattern: `AGGREGATE.ACTION.STATUS` naming
8. Events are emitted from the `IConventionStore` implementation (not the endpoint handler) to ensure all mutation paths are covered
9. Event queries support: "show all convention changes for tenant X", "show all changes by user Y", "show history of key=typescript-react"
10. Events are queryable via the existing event store API (`GET /api/v1/events?tags.key=typescript-react`)
11. Backward compatibility: if the event store is unavailable, convention mutations still succeed (emit is best-effort, not transactional)

## Technical Context

### DCB Event Format

Same structure as prompt events (Story 27-7):

```typescript
interface DomainEvent {
  id: string;
  type: string;
  timestamp: string;
  tags: {
    tenantId?: string;
    key?: string;
    category?: string;
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
| `CONVENTION.CREATED.SUCCESS` | New convention created | tenantId, key, category, userId | `{ version: 1, keywords, matchMode, alwaysApply, priority, enabled }` |
| `CONVENTION.UPDATED.SUCCESS` | Existing convention modified | tenantId, key, category, userId | `{ previousVersion, newVersion, changedFields: ["body","keywords",...] }` |
| `CONVENTION.DELETED.SUCCESS` | Convention removed | tenantId, key, category, userId | `{ deletedVersion }` |
| `CONVENTION.RESET.SUCCESS` | System default restored | key, category, userId | `{ previousVersion, newVersion, resetFrom: "custom", resetTo: "hardcoded" }` |

### changedFields Calculation

Same pattern as prompt events — list which fields changed without storing full text:

```csharp
private static string[] DiffFields(Convention before, Convention after)
{
    var fields = new List<string>();
    if (before.Name != after.Name) fields.Add("name");
    if (before.Description != after.Description) fields.Add("description");
    if (before.Category != after.Category) fields.Add("category");
    if (before.Body != after.Body) fields.Add("body");
    if (!before.Keywords.SequenceEqual(after.Keywords)) fields.Add("keywords");
    if (before.MatchMode != after.MatchMode) fields.Add("matchMode");
    if (before.AlwaysApply != after.AlwaysApply) fields.Add("alwaysApply");
    if (before.Priority != after.Priority) fields.Add("priority");
    if (before.Enabled != after.Enabled) fields.Add("enabled");
    return fields.ToArray();
}
```

### Event Emission Pattern

Identical to Story 27-7 — events emitted from `PgConventionStore` after successful database mutations:

```csharp
public async Task<Convention> UpsertAsync(Guid? tenantId, string key,
    UpsertConventionInput input, Guid? userId = null, CancellationToken ct = default)
{
    var existing = await GetRowAsync(tenantId, key, ct);
    var result = await UpsertRowAsync(tenantId, key, input, ct);

    var eventType = existing is null
        ? "CONVENTION.CREATED.SUCCESS"
        : "CONVENTION.UPDATED.SUCCESS";

    await EmitEventAsync(eventType, new
    {
        tenantId = tenantId?.ToString(),
        key,
        category = result.Category,
        userId = userId?.ToString()
    }, existing is null
        ? new { version = result.Version, keywords = result.Keywords,
                matchMode = result.MatchMode, alwaysApply = result.AlwaysApply,
                priority = result.Priority, enabled = result.Enabled }
        : new { previousVersion = existing.Version, newVersion = result.Version,
                changedFields = DiffFields(existing, result) },
    ct);

    return result;
}
```

### Files to Create

| File | Purpose |
|------|---------|
| `apps/tamma-elsa/src/Tamma.Api/Services/Conventions/ConventionStoreEvents.cs` | Event emission helper + constants |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Conventions/ConventionStoreEventsTests.cs` | Unit tests |

### Files to Modify

| File | Change |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Services/Conventions/PgConventionStore.cs` | Add event emission to upsert, delete, reset |
| `apps/tamma-elsa/src/Tamma.Api/Services/Conventions/IConventionStore.cs` | Ensure userId parameter on mutation methods |

## Implementation Plan

### Step 1: Define Event Constants

```csharp
public static class ConventionEventTypes
{
    public const string Created = "CONVENTION.CREATED.SUCCESS";
    public const string Updated = "CONVENTION.UPDATED.SUCCESS";
    public const string Deleted = "CONVENTION.DELETED.SUCCESS";
    public const string Reset = "CONVENTION.RESET.SUCCESS";
}
```

### Step 2: Event Emission Helper

Create a helper that constructs and appends convention events. Best-effort — catches and logs failures without blocking the mutation. Same pattern as `prompt-store-events.ts` in Story 27-7.

### Step 3: Integrate into PgConventionStore

Inject `IEventStore` into `PgConventionStore` constructor. Emit events in `UpsertAsync()`, `DeleteAsync()`, `DeleteSystemDefaultAsync()`, and `ResetSystemDefaultAsync()`.

### Step 4: DiffFields Utility

Implement the field comparison utility. Convention has more diffable fields than prompts (keywords array, matchMode, alwaysApply, priority in addition to body/name/category).

## Implementation Notes

1. **Best-effort emission** — same as Story 27-7. Event store failure does not block convention mutations.
2. **No full body in events** — body text can be 500-5000 characters. Storing it in every update event would bloat the event store. Only `changedFields` is stored. The actual body is in the `conventions` table with version tracking.
3. **Keywords diff** — keywords are stored in the normalized `convention_keywords` table (see Story 27-8), not as a column on `conventions`. The `DiffFields` utility compares the `Keywords` arrays on the `Convention` record (which are populated by joining from `convention_keywords` on read). Use `SequenceEqual` after sorting both arrays to detect keyword changes regardless of order.
4. **System seed operations do NOT emit events** — only user-initiated changes emit events.
5. **The `CONVENTIONS.RESOLVED.SUCCESS` event (from Story 27-13) is separate** — it's emitted at runtime during LLM calls. This story covers CRUD events only.
6. **userId comes from the API layer** — passed as parameter to mutation methods, same as Story 27-7.
7. **Keyword change detail** — when `changedFields` includes `"keywords"`, the event `data` does NOT include the full keyword lists (they can be large). The convention version number + the `convention_keywords` table provide the authoritative before/after state for audit purposes.

## Testing Strategy

### Unit Tests

1. `EmitConventionEvent()` calls `eventStore.Append()` with correct event structure
2. Event includes tenantId tag when provided
3. Event omits tenantId tag when null (system default)
4. Event emission does not throw when eventStore fails (best-effort)
5. `DiffFields()` correctly identifies changed fields
6. `DiffFields()` returns empty array when nothing changed
7. `DiffFields()` detects keyword array changes regardless of order

### Integration Tests (with in-memory stores)

8. `UpsertAsync()` emits `CONVENTION.CREATED.SUCCESS` for new convention
9. `UpsertAsync()` emits `CONVENTION.UPDATED.SUCCESS` for existing convention with correct changedFields
10. `DeleteAsync()` emits `CONVENTION.DELETED.SUCCESS` with deleted version
11. `ResetSystemDefaultAsync()` emits `CONVENTION.RESET.SUCCESS`
12. Events are queryable by tenantId tag
13. Events are queryable by key tag

### Backward Compatibility

14. `PgConventionStore` works without event store (events skipped)
15. Existing convention tests pass without providing userId

## Dependencies

- **Story 27-9** (Convention Store Service) — `IConventionStore` and `PgConventionStore` must exist
- **Epic 4** (Event Sourcing & Audit Trail) — `IEventStore` interface must exist
- Same event store infrastructure as Story 27-7

## Estimated Effort

| Task | Hours |
|------|-------|
| Event type constants and helper | 1 |
| DiffFields utility | 0.5 |
| PgConventionStore event integration (upsert, delete, reset) | 2 |
| Unit tests (7 tests) | 1.5 |
| Integration tests (6 tests) | 1.5 |
| **Total** | **6.5 hours** |

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-05-04 | 1.0 | Initial story creation | Architecture Team |
