# Finding 015: `UpsertDefinitionAsync` always misses on `Guid.Empty` → unbounded duplicate inserts

**Scope**: engine
**Severity**: P2 (correctness — table grows unboundedly across syncs)
**Status**: Incomplete (upsert semantics broken)
**Estimated port effort**: 1h

## 1. What's in TS

- File: `packages/api/src/persistence/workflow-store.ts:78-89` (9e9a57c~1)

```typescript
// packages/api/src/persistence/workflow-store.ts:78-89 (9e9a57c~1)
async upsertDefinition(def: WorkflowDefinition): Promise<WorkflowDefinition> {
  const existing = this.definitions.get(def.id);
  const merged: WorkflowDefinition = {
    ...existing,
    ...def,
    syncedAt: Date.now(),
  };
  this.definitions.set(merged.id, merged);
  return merged;
}
```

The TS upsert keys by `def.id` (caller-supplied stable string). If `id` is already present, it merges; if not, it inserts. Idempotent.

## 2. What's in C#

- File: `apps/tamma-elsa/src/Tamma.Data/Repositories/WorkflowRepository.cs:8-28`

```csharp
// WorkflowRepository.cs:8-28 (current)
public async Task<WorkflowDefinition> UpsertDefinitionAsync(WorkflowDefinition def)
{
    var existing = await db.WorkflowDefinitions.FindAsync(def.Id);
    if (existing is not null)
    {
        existing.Name = def.Name;
        // ...
        return existing;
    }
    def.CreatedAt = DateTime.UtcNow;
    def.UpdatedAt = DateTime.UtcNow;
    def.SyncedAt = DateTime.UtcNow;
    db.WorkflowDefinitions.Add(def);
    await db.SaveChangesAsync();
    return def;
}
```

Used by `WorkflowEndpoints.cs:16-22`:

```csharp
var def = await workflowRepo.UpsertDefinitionAsync(new WorkflowDefinition
{
    Name = req.Name,
    Description = req.Description,
    Steps = req.Steps is not null ? JsonSerializer.Serialize(req.Steps) : "[]",
    TenantId = tc.TenantId
});
```

The endpoint constructs a `WorkflowDefinition` **without setting `Id`**. `Id` defaults to `Guid.Empty`. `FindAsync(Guid.Empty)` almost always misses (it could only hit if a previous definition also has `Guid.Empty` as PK, which EF Core's generator prevents because every insert gets `Guid.NewGuid()` by convention or by generated-identity column semantics).

Net effect: `UpsertDefinitionAsync` is effectively `CreateDefinitionAsync`. Every sync creates a fresh row.

- Tests: no test asserts upsert idempotency. `UpsertDefinition_SecondCall_ReturnsSameId` does not exist.

## 3. The gap

- TS did: idempotent upsert keyed by stable `id`.
- C# does: find-by-empty-Guid → miss → insert. Not idempotent.

For the Elsa runtime syncing 30 workflow definitions at startup:

- TS: 30 rows after first sync. 30 rows after second sync (no-op upserts).
- C#: 30 rows after first sync, 60 after second, etc. `dashboard/workflows` shows duplicates with identical `Name` and different `Id`.

Combined with finding 014 (Guid vs string), this is the mechanism by which the table grows unboundedly — even if the caller tried to send a stable `Id`, the DTO doesn't have an `Id` field.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-10/story-10-5/10-5-workflow-provider-abstraction-and-elsa-integration.md` describes syncing Elsa definitions idempotently.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Incomplete — the function is called "upsert" but the "update" branch is unreachable.
- **What's needed to finish**:
  1. Resolve finding 014 (Guid→string id) first, so the DTO can carry a stable id.
  2. Accept `Id` in `CreateDefinitionRequest`.
  3. Key the lookup by either `Id` (when provided) or `(Name, TenantId)` (as a fallback).
  4. Add integration test `UpsertDefinition_CalledTwice_UpdatesSameRow`.
- **Is it "just a stub" or is scope missing?** Implementation exists but is broken due to the empty-Guid issue. A 2-line fix once finding 014 lands.
- **Blockers**: finding 014.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/WorkflowRepository.cs:8-28` — key lookup by the stable external id (after finding 014 migration) or by `(Name, TenantId)` fallback.
  - `apps/tamma-elsa/src/Tamma.Api/Dtos/Workflows/CreateDefinitionRequest` — add `Id` field.
- Tests to add:
  - `UpsertDefinition_SecondCall_SameId_UpdatesInPlace`
  - `UpsertDefinition_NewId_Inserts`
  - `UpsertDefinition_IncrementsVersion_OnUpdate`
- Estimated effort: 1h (after finding 014 lands)

## References

- TS source: `packages/api/src/persistence/workflow-store.ts:78-89`
- C# source: `apps/tamma-elsa/src/Tamma.Data/Repositories/WorkflowRepository.cs:8-28`
- Story: `docs/stories/epic-10/story-10-5/10-5-workflow-provider-abstraction-and-elsa-integration.md`
- Related findings: `014-workflow-definition-id-guid-mismatch.md` (blocker)
