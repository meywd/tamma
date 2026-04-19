# Finding 010: `prompt_overrides` missing `version`, `created_by`, `updated_by` columns

**Scope**: prompts
**Severity**: P2 (correctness/observability — audit trail regression)
**Status**: Data-model regression
**Estimated port effort**: 1h

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed (schema already added by admin-db 030; wiring completed here)
- **Commit**: ea4d5e5
- **Notes**: Schema columns (`Version`, `CreatedBy`, `UpdatedBy`) were added by `SchemaHardeningPhase1` migration (admin-db finding 030). This commit completes the wiring: `PromptRepository.UpsertAsync` now bumps `Version` on every UPDATE, sets `CreatedBy` on INSERT, sets `UpdatedBy` on every write. An optional `actingUserId` parameter supports impersonation/service-key writes (defaults to row owner). `ResolvedPrompt.Version` flows into the render response (closes finding 003). Tests `UpsertRoleActionAsync_SetsCreatedByAndUpdatedBy_ToOwnerByDefault` and `ResolveRoleActionAsync_UserOverride_BumpsVersionOnEachUpdate` lock the behavior. **Not done**: optimistic-concurrency `If-Match` header guard at the endpoint layer (no story currently requires it).

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:database/archived-sql-migrations/012_prompt_store.sql`.

- File: `database/archived-sql-migrations/012_prompt_store.sql:17-32` (`prompts` table schema).
- Contract/behavior: The archived migration defines three tables (`prompts`, `system_prompts`, `action_prompts`) each with a matching audit pattern:
  - `version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0)` — optimistic concurrency / edit history.
  - `created_by UUID` — who first created the row.
  - `updated_by UUID` — who last edited the row.
- Key code (verbatim quote, `012_prompt_store.sql:17-32`):

```sql
-- database/archived-sql-migrations/012_prompt_store.sql (9e9a57c~1)
CREATE TABLE IF NOT EXISTS prompts (
  id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id     UUID REFERENCES tenants(id) ON DELETE CASCADE,
  role          TEXT NOT NULL,
  action        TEXT NOT NULL,
  template      TEXT NOT NULL,
  system_prompt TEXT NOT NULL DEFAULT '',
  variables     JSONB NOT NULL DEFAULT '[]'::jsonb,
  enable_tools  BOOLEAN NOT NULL DEFAULT false,
  max_tokens    INTEGER NOT NULL DEFAULT 4096 CHECK (max_tokens > 0),
  version       INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
  created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  created_by    UUID,
  updated_by    UUID
);
```

TS upsert code in `pg-prompt-store.ts` used `version = prompts.version + 1` in the UPDATE branch of `ON CONFLICT`, implementing monotonically increasing version numbers per row. `RenderedPrompt.version` was exposed on the render response (see Finding #003).

- Dependencies: TypeScript `PromptTemplate` interface with `version: number` field, returned from `getDefaultPrompts()`.
- Tests that exercised this: `pg-prompt-store.test.ts` — "version increments on upsert", "version starts at 1".

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/Entities/PromptOverride.cs:1-18`.
- Contract/behavior: The C# entity **drops** all three columns: `Version`, `CreatedBy`, `UpdatedBy` are absent. The only temporal tracking is `CreatedAt` / `UpdatedAt`.
- Key code (verbatim quote):

```csharp
// apps/tamma-elsa/src/Tamma.Data/Entities/PromptOverride.cs (current)
public class PromptOverride
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public Guid? TenantId { get; set; }
    public string Scope { get; set; } = null!;
    public string? Role { get; set; }
    public string? Action { get; set; }
    public string Template { get; set; } = null!;
    public string? SystemPrompt { get; set; }
    public string[] Variables { get; set; } = [];
    public bool EnableTools { get; set; }
    public int MaxTokens { get; set; } = 4096;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    // Missing: Version, CreatedBy, UpdatedBy
}
```

The `SystemPrompts.PromptTemplate` record retains a `Version` field with a default of 1 (`SystemPrompts.cs:17-25`), but this is for the in-memory system defaults — user overrides have no stored version.

- Dependencies: `PromptRepository.UpsertAsync` updates `UpdatedAt` on UPDATE; no version bump.
- Tests: `apps/tamma-elsa/tests/Tamma.Api.Tests/PromptStore/PromptStoreServiceTests.cs` does not cover optimistic concurrency or authorship.

## 3. The gap

Concrete behavioral difference:

Three columns dropped, each affecting a different capability:

1. **`version` (optimistic concurrency)**:
   - TS: `{ "version": 3, ... }` returned on render — callers could detect stale snapshots.
   - C#: no version returned (aligned with Finding #003 dropping the field from render response). If two dashboard tabs edit the same prompt simultaneously, the second save silently overwrites the first with no conflict detection.

2. **`created_by` (authorship trail)**:
   - TS: the UUID of the user who first upserted the row. Useful for "show me prompts created by user X" in admin views.
   - C#: unknown. The `UserId` column functions as *owner* (who sees the row, per Finding #004's user-scoping) but is not distinguishable from *creator*.

3. **`updated_by` (last editor)**:
   - TS: the UUID of the user whose last `PUT` mutated the row. Relevant when a row is shared across actors (e.g., tenant admins acting on behalf of a user, or service-account writes via Elsa workflows).
   - C#: unknown. Even `EmitUpdatedAsync` records `userId` on the event only, not on the row itself.

For a caller flow:
- User A sends `PUT /api/prompts/developer/plan` → row created.
- Platform admin B (with impersonation) sends `PUT /api/prompts/developer/plan` → row updated.
- TS could report `{ created_by: A, updated_by: B, version: 2 }`.
- C# can only report `{ created_at: T1, updated_at: T2 }` — no attribution.

In production with existing data / deployed clients, this means: the audit-trail guarantee in CLAUDE.md ("Complete audit trail (compliance: SOC2, ISO27001, GDPR)") is weakened. Compliance requires knowing *who* changed a prompt, not just *when*. The DCB event stream preserves this via the `userId` tag on `PROMPT.UPDATED.SUCCESS` events, but point-in-time queries against the `prompt_overrides` table cannot answer the question without event replay.

Error paths:
- TS error path: `version` column CHECK `version > 0` would reject bad inputs.
- C# error path: No equivalent guard.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-27/27-1-prompt-store-database-schema.md` AC #1 and #11.
- Story's acceptance criteria for this behavior: AC #1 explicitly lists `created_by (UUID nullable)`, `updated_by (UUID nullable)`, `version (INTEGER NOT NULL DEFAULT 1)`. AC #12 (summarized from story context) requires optimistic concurrency semantics.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - The story's schema is more complete than the C# implementation.

## 5. Status

- **Classification**: Data-model regression
- **What's needed to finish**:
  1. Add `Version` (`int`, default 1), `CreatedBy` (`Guid?`), `UpdatedBy` (`Guid?`) to `PromptOverride` entity.
  2. Update `PromptRepository.UpsertAsync` to bump `Version++` on UPDATE and set `UpdatedBy` / `CreatedBy` from the repository caller.
  3. Flow authenticated principal's userId into `UpsertAsync` (currently only `UserId = principal.UserId` lands on the row; need a separate "acting user" parameter for impersonation/service-key scenarios).
  4. Expose `version` in the render response (see Finding #003) and as an optional `If-Match` header for concurrency control.
- **Is it "just a stub" or is scope missing?** Scope was specified (story has the columns) but dropped during port. It's missing scope, not a stub.
- **Blockers**: Finding #004 (user vs tenant scoping) — the authorship semantics differ depending on scope. Also Finding #003 (render response shape) if exposing `version` to the wire.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Data/Entities/PromptOverride.cs` — add `Version`, `CreatedBy`, `UpdatedBy` properties.
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/PromptRepository.cs` — bump `Version` on UPDATE, set `UpdatedBy` from a new parameter.
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/IPromptRepository.cs` — update interface to accept `actingUserId`.
  - `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/PromptStoreService.cs` — thread `actingUserId` through upsert methods.
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/PromptEndpoints.cs` — pass `TryGetUserId(principal)` as acting user.
  - `apps/tamma-elsa/src/Tamma.Api/Dtos/Prompts/PromptDtos.cs` — add `Version` to `RenderedPromptResponse` (see Finding #003).
- Files to create:
  - EF migration adding the three columns (if not dev-schema-managed).
- Tests to add:
  - `PromptRepositoryTests.cs` — `UpsertIncrementsVersion`, `UpsertSetsCreatedByOnInsert`, `UpsertSetsUpdatedByOnUpdate`.
  - `PromptEndpointsTests.cs` — `RenderReturnsVersion`.
- Estimated effort: 1h broken down as:
  - Entity + migration: 0.3h
  - Repository + service: 0.3h
  - Endpoint wiring + DTO: 0.2h
  - Tests: 0.2h

## References

- TS source: `database/archived-sql-migrations/012_prompt_store.sql:17-32`, `packages/api/src/services/pg-prompt-store.ts` upsert implementation (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Data/Entities/PromptOverride.cs:1-18`, `apps/tamma-elsa/src/Tamma.Data/Repositories/PromptRepository.cs`
- Story: `docs/stories/epic-27/27-1-prompt-store-database-schema.md` AC #1
- Related findings: `docs/audit/port-gaps/prompts/003-render-response-field-names.md`, `docs/audit/port-gaps/prompts/004-tenant-scoped-to-user-scoped.md`, `docs/audit/port-gaps/prompts/011-missing-unique-constraint.md`
- CLAUDE.md section: "Event Sourcing (DCB Pattern)" audit-trail claims
- Archived SQL migration: `database/archived-sql-migrations/012_prompt_store.sql`
