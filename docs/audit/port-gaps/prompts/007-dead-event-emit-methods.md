# Finding 007: `EmitCreatedAsync` and `EmitResetAsync` are never called from endpoints

**Scope**: prompts
**Severity**: P3 (drift/contract — event-sourcing gap)
**Status**: Incomplete (partial port, missing 2 event emissions)
**Estimated port effort**: 0.5h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/services/prompt-store-events.ts`.

- File: `packages/api/src/services/prompt-store-events.ts:20-25` (event type constants) and the corresponding `pg-prompt-store.ts` call sites.
- Contract/behavior: The TS event emitter defined 4 event types and the store dispatched them from distinct code paths:
  - `PROMPT.CREATED.SUCCESS` — emitted from `upsert*` when `existing` row was `undefined` (i.e., the INSERT branch of an UPSERT).
  - `PROMPT.UPDATED.SUCCESS` — emitted from `upsert*` when `existing` row was found (the UPDATE branch).
  - `PROMPT.DELETED.SUCCESS` — emitted from `delete`.
  - `PROMPT.RESET.SUCCESS` — emitted from `resetSystemDefault` (distinct from DELETE — this was a revert-to-hardcoded operation).
- Key code (verbatim quote, `prompt-store-events.ts:20-26`):

```typescript
// packages/api/src/services/prompt-store-events.ts (9e9a57c~1)
export const PROMPT_EVENT_TYPES = {
  CREATED: 'PROMPT.CREATED.SUCCESS',
  UPDATED: 'PROMPT.UPDATED.SUCCESS',
  DELETED: 'PROMPT.DELETED.SUCCESS',
  RESET: 'PROMPT.RESET.SUCCESS',
} as const;
```

The TS `upsert*` paths in `pg-prompt-store.ts` fetched the existing row first, then chose CREATED vs UPDATED based on its absence/presence — giving downstream consumers (the dashboard activity feed, audit log) the ability to distinguish first-time customization from incremental edits.

- Dependencies: `emitPromptEvent()`, `diffFields()` helpers, `IPromptEventStore`.
- Tests that exercised this: `pg-prompt-store.test.ts` — "emits CREATED on first upsert", "emits UPDATED on subsequent upsert", "emits RESET on resetSystemDefault".

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/PromptEventsService.cs:19-72` (all five methods defined) and `apps/tamma-elsa/src/Tamma.Api/Endpoints/PromptEndpoints.cs:146-244` (call sites).
- Contract/behavior: All five event type constants and emitter methods are present (`EmitCreatedAsync`, `EmitUpdatedAsync`, `EmitDeletedAsync`, `EmitResetAsync`, `EmitRenderedAsync`) — but `EmitCreatedAsync` and `EmitResetAsync` are **never invoked from any endpoint or service**. Every upsert path in `PromptEndpoints.cs` unconditionally calls `EmitUpdatedAsync`, regardless of whether the row existed before.
- Key code (verbatim quote, `PromptEventsService.cs:43-58`):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/PromptEventsService.cs (current)
public const string CreatedType = "PROMPT.CREATED.SUCCESS";
public const string UpdatedType = "PROMPT.UPDATED.SUCCESS";
public const string DeletedType = "PROMPT.DELETED.SUCCESS";
public const string ResetType = "PROMPT.RESET.SUCCESS";
public const string RenderedType = "PROMPT.RENDERED.SUCCESS";

...

/// <summary>Emit a <c>PROMPT.CREATED.SUCCESS</c> event.</summary>
public Task EmitCreatedAsync(
    Guid? tenantId,
    Guid? userId,
    string role,
    string action,
    IReadOnlyDictionary<string, object?> data)
    => EmitAsync(CreatedType, tenantId, userId, role, action, data);

/// <summary>Emit a <c>PROMPT.RESET.SUCCESS</c> event (override deleted, falls back to default).</summary>
public Task EmitResetAsync(Guid? tenantId, Guid? userId, string role, string action)
    => EmitAsync(ResetType, tenantId, userId, role, action, new Dictionary<string, object?>());
```

Callers in `PromptEndpoints.cs` only call `EmitUpdatedAsync` from upserts and `EmitDeletedAsync` from deletes:

```csharp
// PromptEndpoints.cs:146-156 (UpsertPrompt)
var saved = await store.UpsertRoleActionAsync(userId, tenantContext.TenantId, role, action, input);
await events.EmitUpdatedAsync(
    tenantContext.TenantId, userId, role, action,
    new Dictionary<string, object?>
    {
        ["templateLength"] = saved.Template.Length,
        ["enableTools"] = saved.EnableTools,
        ["maxTokens"] = saved.MaxTokens,
    });
// ^^ always UPDATED, never CREATED
```

- Dependencies: `IEventRepository.AppendAsync` (best-effort, swallows failures).
- Tests: `apps/tamma-elsa/tests/Tamma.Api.Tests/PromptStore/PromptEventsServiceTests.cs` covers direct invocation of `EmitCreatedAsync` and `EmitResetAsync`, confirming the methods work — but does not assert they are wired into the endpoint flow.

Verification grep for callers (excluding tests):
```
$ grep -rn "EmitCreatedAsync\|EmitResetAsync" apps/tamma-elsa/src/
apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/PromptEventsService.cs:44:    public Task EmitCreatedAsync(...)
apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/PromptEventsService.cs:57:    public Task EmitResetAsync(...)
```
Zero production call sites.

## 3. The gap

Concrete behavioral difference:

- TS did: emit `PROMPT.CREATED.SUCCESS` on first-time upsert (no existing row), `PROMPT.UPDATED.SUCCESS` on subsequent upsert (existing row), and `PROMPT.RESET.SUCCESS` on reset-to-default.
- C# does: always emit `PROMPT.UPDATED.SUCCESS` for all upserts; does not emit `PROMPT.RESET.SUCCESS` at all; `PROMPT.CREATED.SUCCESS` is never emitted.

Impact on the DCB event stream:
- Tenants that had baseline telemetry splitting "new prompt" vs "edited prompt" see all events as `UPDATED`.
- Any downstream replay or projection keyed on `PROMPT.CREATED.SUCCESS` (e.g., "first-customization onboarding indicator") receives zero events and never triggers.
- Any analytics on reset-to-default behavior (e.g., "detect users who tried a custom prompt and reverted") gets no signal.

For a caller flow:
1. User A sends `PUT /api/prompts/developer/plan` (first time, no prior override).
2. TS emits `PROMPT.CREATED.SUCCESS` with `tags = { role, action, userId, tenantId }` and `data = { templateLength, ... }`.
3. C# emits `PROMPT.UPDATED.SUCCESS` instead, with the same tags and data.
4. User A re-sends `PUT /api/prompts/developer/plan` (update).
5. Both emit `PROMPT.UPDATED.SUCCESS` — indistinguishable on the wire from step 2's event.

Error paths:
- TS error path: event emission was best-effort and logged on failure. Same as C#.
- C# error path: identical best-effort contract (see `PromptEventsService.EmitAsync` try/catch at lines 86-119).

## 4. Gap from stories

- Referenced story: `docs/stories/epic-27/27-7-prompt-store-event-sourcing.md`.
- Story's acceptance criteria: Epic 27-7 mandates CREATED/UPDATED/DELETED/RESET event types with clear semantic distinctions. The story explicitly requires the CREATED-vs-UPDATED discriminator.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior — story calls for the discrimination that C# dropped.

## 5. Status

- **Classification**: Incomplete
- **What's needed to finish**:
  1. In `UpsertRoleActionAsync`/`UpsertRoleSystemAsync`/`UpsertActionDefaultAsync`, have the repository return a tuple `(PromptOverride, bool wasCreated)`.
  2. In the endpoint, branch on `wasCreated` to call `EmitCreatedAsync` vs `EmitUpdatedAsync`.
  3. Add a reset endpoint (or wire into DELETE when the row being deleted was created by a platform-admin path — see Finding #005) and call `EmitResetAsync`.
  4. Alternative: if the distinction is not needed by the dashboard, delete `EmitCreatedAsync` and `EmitResetAsync` and simplify — but that requires updating epic-27-7 AC and any downstream consumer.
- **Is it "just a stub" or is scope missing?** The *code* is fully implemented; it's the *wiring* that's missing. The repository does not expose the "was created" vs "was updated" signal that the endpoint would need.
- **Blockers**: `PromptRepository.UpsertAsync` currently returns the entity but not the "was new" flag. Minor refactor.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/PromptRepository.cs` — change `UpsertAsync` return to `(PromptOverride, bool wasCreated)` or add a second `InsertOrUpdateAsync` variant.
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/IPromptRepository.cs` — update interface.
  - `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/PromptStoreService.cs:190-270` — propagate `wasCreated` flag.
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/PromptEndpoints.cs:127-167, 194-225` — branch emit call.
  - If adopting simplification: delete `EmitCreatedAsync` and `EmitResetAsync` from `PromptEventsService.cs`.
- Files to create: None.
- Tests to add:
  - `PromptEventsServiceTests.cs` — `FirstUpsertEmitsCreated` (currently only direct `EmitCreatedAsync` is tested).
  - `PromptEventsServiceTests.cs` — `SubsequentUpsertEmitsUpdated`.
  - `PromptEventsServiceTests.cs` — `ResetOperationEmitsReset`.
- Estimated effort: 0.5h broken down as:
  - Repository signature: 0.1h
  - Endpoint wiring: 0.2h
  - Tests: 0.2h

## References

- TS source: `packages/api/src/services/prompt-store-events.ts:20-26`, `packages/api/src/services/pg-prompt-store.ts` (upsert call sites) (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/PromptEventsService.cs:19-72`, `apps/tamma-elsa/src/Tamma.Api/Endpoints/PromptEndpoints.cs:146-244`
- Story: `docs/stories/epic-27/27-7-prompt-store-event-sourcing.md`
- Related findings: `docs/audit/port-gaps/prompts/005-put-system-prompt-semantic-drift.md`
- CLAUDE.md section: "Event Types" pattern `AGGREGATE.ACTION.STATUS`
