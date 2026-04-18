# Finding 014: Workflow definition `id` type string→Guid breaks Elsa definition IDs

**Scope**: engine
**Severity**: P1 (feature broken — Elsa definition ids won't bind to Guid)
**Status**: Data-model regression
**Estimated port effort**: 4h

## 1. What's in TS

- File: `packages/api/src/persistence/workflow-store.ts:18-25` (9e9a57c~1)

```typescript
// packages/api/src/persistence/workflow-store.ts:18-25 (9e9a57c~1)
export interface WorkflowDefinition {
  id: string;
  name: string;
  version: number;
  description?: string;
  activities: unknown[];
  syncedAt: number;
}
```

- File: `packages/api/src/routes/workflows/index.ts:33-40` — the POST schema uses `id: z.string().min(1)`.

Elsa workflow definition IDs are strings (for example `IssueAnalysisWorkflow`, `TddRedGreenRefactor-v2`). Elsa itself uses string ids throughout its own data model. The TS API faithfully accepted and stored these.

SQL schema in the archived migration confirms: `definition_id TEXT NOT NULL` (`database/archived-sql-migrations/011_tenant_scoped_stores.sql:49`).

## 2. What's in C#

- File: `apps/tamma-elsa/src/Tamma.Data/Entities/WorkflowDefinition.cs:5`

```csharp
public class WorkflowDefinition
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    // ...
}
```

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/WorkflowEndpoints.cs:11-25` — `CreateDefinition` accepts no external id, relies on EF Core to generate a Guid.
- File: `WorkflowEndpoints.cs:49` — `UpdateInstance(Guid id, ...)` — the route parameter is typed `Guid`, so any request with a string definition id (`/api/workflows/definitions/TddRedGreenRefactor-v2/...`) returns 400 at routing time.
- File: `WorkflowEndpoints.cs:65-79` — `ListInstances(..., Guid? definitionId, ...)` — again Guid-typed query param.

### The Elsa integration problem

Elsa workflow definition ids are stable string identifiers (`Workflow.DefinitionId = "IssueAnalysisWorkflow"`). When the Elsa runtime syncs definitions to this API via `POST /api/workflows/definitions`, it sends `{id: "IssueAnalysisWorkflow", name: "...", ...}`. ASP.NET Core's model binder cannot coerce `"IssueAnalysisWorkflow"` to `Guid` — the request is rejected with 400 / validation errors.

In practice the current C# `CreateDefinition` DTO (`CreateDefinitionRequest`) doesn't even include an `id` field (the Guid is server-assigned), so Elsa's stable identifier is thrown away on every sync and a new Guid is minted. The next sync mints another Guid. There is no way to keep a stable reference to the same definition across restarts.

- Tests: there are C# tests exercising `CreateDefinition` with Guid-only ids; no test attempts to use an Elsa-style string id.

## 3. The gap

- TS did: stored the Elsa definition id verbatim as a string. `GET /definitions/TddRedGreenRefactor-v2/instances` worked. Updating a definition (sync after workflow author change) used the same id.
- C# does: mints a new Guid per upsert. The Elsa-shaped `id` field is discarded. Every sync creates a duplicate.

For a deployment that syncs 30 Elsa workflow definitions on startup:

- TS: 30 definitions in DB, each stably keyed by Elsa definition id, surviving restarts.
- C#: 30 definitions on first boot, 60 after second boot, 90 after third. `WorkflowDefinition.Name` collides; `Id` diverges forever. Dashboard shows duplicates. No way to pin an instance to a specific workflow version.

Error paths:

- TS: 400 on empty string id.
- C#: 400 on any non-Guid-parseable string passed to the route.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-10/story-10-5/10-5-workflow-provider-abstraction-and-elsa-integration.md` — Elsa integration spec. Archived SQL migration `011_tenant_scoped_stores.sql` explicitly defines `definition_id TEXT NOT NULL`.
- Story alignment:
  - [x] Matches TS behavior (C# is a data-model regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Data-model regression.
- **What's needed to finish**:
  1. Change `WorkflowDefinition.Id` from `Guid` to `string` (or add a separate `ExternalId string` column and keep the Guid surrogate).
  2. Option A (simpler): drop the Guid PK, make `Id string` the PK.
  3. Option B (safer): keep `Id Guid` as the internal PK, add `ExternalId string` unique. Upsert-by-external-id. Translate incoming requests.
  4. Update every call site: `CreateDefinition`, `UpsertDefinition`, `GetDefinition`, `ListDefinitions`, all route parameters that key by definition id, and `WorkflowInstance.DefinitionId` FK.
  5. Update EF Core migration.
- **Is it "just a stub" or is scope missing?** Scope was understood (archived SQL has `TEXT`), but the C# author pivoted to Guid without considering the Elsa-string-id constraint. Pure data-model regression.
- **Blockers**: requires EF Core schema migration. Existing data is negligible (per CLAUDE.md "No migration anxiety").

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Data/Entities/WorkflowDefinition.cs:5` — `Guid Id` → `string Id`.
  - `apps/tamma-elsa/src/Tamma.Data/Entities/WorkflowInstance.cs:6` — `Guid DefinitionId` → `string DefinitionId`.
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/WorkflowRepository.cs` — all methods taking Guid.
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/WorkflowEndpoints.cs` — `ListInstances(..., Guid? definitionId, ...)` → string.
  - `apps/tamma-elsa/src/Tamma.Api/Dtos/Workflows/*` — accept `string Id`.
  - New EF Core migration.
- Tests to add:
  - `UpsertDefinition_AcceptsElsaStringId_Roundtrip`
  - `CreateInstance_ReferencesDefinitionByStringId`
  - `ListInstances_FilterByDefinitionId_StringMatch`
- Estimated effort: 4h
  - Entity + repo changes: 1h
  - Endpoint rewrites: 1h
  - Migration: 30m
  - Tests: 1.5h

## References

- TS source: `packages/api/src/persistence/workflow-store.ts:18-25`, `routes/workflows/index.ts:33-40`
- C# source: `apps/tamma-elsa/src/Tamma.Data/Entities/WorkflowDefinition.cs:5`, `Tamma.Api/Endpoints/WorkflowEndpoints.cs:11-106`
- Story: `docs/stories/epic-10/story-10-5/10-5-workflow-provider-abstraction-and-elsa-integration.md`
- Archived SQL: `database/archived-sql-migrations/011_tenant_scoped_stores.sql:49` (`definition_id TEXT NOT NULL`)
- Related findings: `015-upsert-definition-find-empty-guid.md` (downstream consequence)
