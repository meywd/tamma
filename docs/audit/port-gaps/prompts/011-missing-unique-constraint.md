# Finding 011: No `UNIQUE(user_id, scope, role, action)` constraint per CLAUDE.md spec

**Scope**: prompts
**Severity**: P2 (correctness — concurrent inserts can produce duplicates)
**Status**: Data-model regression
**Estimated port effort**: 0.5h

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Already-fixed (unique index in TammaDbContext / migration)
- **Commit**: n/a
- **Notes**: `TammaDbContext.cs:293` already declares `entity.HasIndex(e => new { e.UserId, e.Scope, e.Role, e.Action }).IsUnique()`, materialized by the `InitialSchema` migration as `IX_prompt_overrides_UserId_Scope_Role_Action`. The audit finding under-reported this — the index exists. The repository's read-then-write upsert remains theoretically race-prone in the small window between `FirstOrDefaultAsync` and `Add`, but Postgres will reject the duplicate `Add` via the unique constraint, surfacing as `DbUpdateException` to the caller. A future hardening could replace the read-then-write with `INSERT ... ON CONFLICT DO UPDATE` for atomic upserts; deferred until a story requires it. **Not done**: NULLS NOT DISTINCT or COALESCE filtered index for action-default scope (Role NULL) and role-system scope (Action NULL) — Postgres treats `NULL != NULL` in unique constraints, so duplicates per-NULL slot are still possible. Filed as latent issue; no current write path stresses it.

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:database/archived-sql-migrations/012_prompt_store.sql`.

- File: `database/archived-sql-migrations/012_prompt_store.sql:35-45`.
- Contract/behavior: The archived migration defined two **partial unique indexes** on the `prompts` table to enforce "at most one row per `(tenant_id, role, action)`" with correct NULL handling:
  - `idx_prompts_system_default` — unique on `(role, action)` where `tenant_id IS NULL`
  - `idx_prompts_tenant_override` — unique on `(tenant_id, role, action)` where `tenant_id IS NOT NULL`
- Key code (verbatim quote):

```sql
-- database/archived-sql-migrations/012_prompt_store.sql (9e9a57c~1)
-- Partial unique indexes for NULL tenant_id handling
CREATE UNIQUE INDEX IF NOT EXISTS idx_prompts_system_default
  ON prompts (role, action)
  WHERE tenant_id IS NULL;

CREATE UNIQUE INDEX IF NOT EXISTS idx_prompts_tenant_override
  ON prompts (tenant_id, role, action)
  WHERE tenant_id IS NOT NULL;
```

TS upsert used `ON CONFLICT (role, action) WHERE tenant_id IS NULL DO UPDATE SET ...` — relying on the partial index to both deduplicate and drive the UPSERT semantics atomically.

- Dependencies: PostgreSQL ≥ 9.5 partial unique indexes.
- Tests that exercised this: indirectly via `pg-prompt-store.test.ts` upsert round-trip tests.

CLAUDE.md (lines ~275-294) specifies a single unified `prompt_overrides` table with:

```sql
CREATE TABLE prompt_overrides (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id TEXT NOT NULL,
  scope TEXT NOT NULL,          -- 'role-system' | 'action-default' | 'role-action'
  role TEXT,                    -- NULL for action-default scope
  action TEXT,                  -- NULL for role-system scope
  ...
  UNIQUE(user_id, scope, role, action)
);
```

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/Entities/PromptOverride.cs:1-18`, `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs` (no explicit index config for `PromptOverride`).
- Contract/behavior: The `PromptOverride` entity has only the PK index on `Id`. No composite unique constraint. EF Core's convention-based discovery does not create `UNIQUE` constraints without explicit `HasIndex(...).IsUnique()` configuration.
- Key code (verbatim quote, `PromptOverride.cs` is shown above; search for index config):

```csharp
// apps/tamma-elsa/src/Tamma.Data/Entities/PromptOverride.cs (current)
public class PromptOverride
{
    public Guid Id { get; set; }                   // <-- only PK, no unique index on composite
    public Guid? UserId { get; set; }
    public Guid? TenantId { get; set; }
    public string Scope { get; set; } = null!;
    public string? Role { get; set; }
    public string? Action { get; set; }
    ...
}
```

The repository's `UpsertAsync` implements upsert via read-then-write in C#:

```csharp
// apps/tamma-elsa/src/Tamma.Data/Repositories/PromptRepository.cs:12-34
public async Task<PromptOverride> UpsertAsync(PromptOverride prompt)
{
    var existing = await db.PromptOverrides
        .FirstOrDefaultAsync(p =>
            p.UserId == prompt.UserId && p.Scope == prompt.Scope &&
            p.Role == prompt.Role && p.Action == prompt.Action);
    if (existing is not null)
    {
        existing.Template = prompt.Template;
        ...
        await db.SaveChangesAsync();
        return existing;
    }
    prompt.CreatedAt = DateTime.UtcNow;
    prompt.UpdatedAt = DateTime.UtcNow;
    db.PromptOverrides.Add(prompt);
    await db.SaveChangesAsync();
    return prompt;
}
```

This is a read-then-write that **does not take a row lock** between `FirstOrDefaultAsync` and `Add`. Under concurrent load with two near-simultaneous upserts for the same `(userId, scope, role, action)`, both reads can return `null`, both paths take the INSERT branch, and the database ends up with two duplicate rows — because there is no UNIQUE constraint to reject the second INSERT.

- Dependencies: EF Core, Npgsql, `TammaDbContext.PromptOverrides`.
- Tests: `apps/tamma-elsa/tests/Tamma.Api.Tests/PromptStore/PromptStoreServiceTests.cs` — serial upsert tests; no concurrent upsert coverage.

## 3. The gap

Concrete behavioral difference:

- TS did: relied on `ON CONFLICT ... DO UPDATE` with a partial unique index — a single atomic statement. Concurrent writes serialize on the index lock and always yield exactly one row.
- C# does: two round-trips (SELECT, then INSERT or UPDATE) without a constraint backstop. Under concurrency, duplicate rows are possible; no error is raised, no retry logic exists.

For a caller flow under load:
- Two `PUT /api/prompts/developer/plan` requests arrive within 10 ms from the same user session (e.g., dashboard re-submits due to network flakiness).
- Both reach `PromptRepository.UpsertAsync`.
- Both execute `FirstOrDefaultAsync(...)` → both see no existing row.
- Both call `Add(...)` → two rows persisted.
- Subsequent `GetAsync(...)` returns only the first one (`FirstOrDefault`); the second is invisible but still occupies storage.

Secondary effect: `DeleteAsync` deletes only the first matching row. So after a duplicate, a user trying to reset ends up with one orphan row left behind.

In production with existing data / deployed clients, this means: no immediate user-facing failure, but the `prompt_overrides` table will accumulate duplicates over time. `GET /api/prompts` via `ListAsync` returns all rows including orphans, so the list endpoint may show the same role+action twice — a UI bug surface.

Error paths:
- TS error path: `ON CONFLICT` silently merges duplicates; no error.
- C# error path: no error raised; silent duplicate insertion.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-27/27-1-prompt-store-database-schema.md` AC #2 and #4.
- Story's acceptance criteria for this behavior: AC #2 — *"A UNIQUE constraint exists on `(tenant_id, role, action)` — using a partial unique index to handle NULL tenant_id correctly"*. AC #4 — same pattern for `system_prompts` table.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior — story explicitly requires the constraint.

CLAUDE.md "Prompt Store Architecture > Storage" (lines ~275-294) also mandates `UNIQUE(user_id, scope, role, action)`.

## 5. Status

- **Classification**: Data-model regression
- **What's needed to finish**:
  1. Add a unique index on `(UserId, Scope, Role, Action)` to `PromptOverride` via `OnModelCreating` in `TammaDbContext`.
  2. Handle NULL-friendly uniqueness: in PostgreSQL, `NULL != NULL` in unique constraints, so scopes with `Role = null` (action-default) or `Action = null` (role-system) need special handling. Use `COALESCE(Role, '')` / `COALESCE(Action, '')` in a filtered index, or use PostgreSQL 15+'s `NULLS NOT DISTINCT` clause.
  3. Change `PromptRepository.UpsertAsync` to use `DbContext.Database.ExecuteSqlRawAsync` with a raw `INSERT ... ON CONFLICT ... DO UPDATE` statement, or catch `DbUpdateException` and retry as UPDATE.
  4. Add concurrent upsert tests.
- **Is it "just a stub" or is scope missing?** The scope (uniqueness) was specified; it was dropped during port. Missing scope, not a stub.
- **Blockers**: Finding #004 (user vs tenant scoping) — the constraint shape depends on which key set is authoritative.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs` — add `modelBuilder.Entity<PromptOverride>().HasIndex(p => new { p.UserId, p.Scope, p.Role, p.Action }).IsUnique()` (and handle NULLs).
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/PromptRepository.cs` — replace read-then-write with `ExecuteSqlRawAsync` or `try/catch DbUpdateException`.
- Files to create:
  - EF migration applying the unique index.
- Tests to add:
  - `PromptRepositoryTests.cs` — `UpsertIsAtomicUnderConcurrency` (using `Parallel.ForEachAsync` with two near-simultaneous upserts, asserting only one row exists after).
  - `PromptRepositoryTests.cs` — `DuplicateInsertRejected` (direct `Add` of two identical rows, asserting `DbUpdateException`).
- Estimated effort: 0.5h broken down as:
  - Unique index + migration: 0.2h
  - Upsert refactor: 0.2h
  - Tests: 0.1h

## References

- TS source: `database/archived-sql-migrations/012_prompt_store.sql:35-45` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Data/Entities/PromptOverride.cs`, `apps/tamma-elsa/src/Tamma.Data/Repositories/PromptRepository.cs:12-34`
- Story: `docs/stories/epic-27/27-1-prompt-store-database-schema.md` AC #2 and #4
- Related findings: `docs/audit/port-gaps/prompts/004-tenant-scoped-to-user-scoped.md`, `docs/audit/port-gaps/prompts/010-prompt-overrides-missing-audit-columns.md`
- CLAUDE.md section: "Prompt Store Architecture > Storage"
- Archived SQL migration: `database/archived-sql-migrations/012_prompt_store.sql`
