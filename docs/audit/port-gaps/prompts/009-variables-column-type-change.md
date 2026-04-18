# Finding 009: `variables` column type changed JSONB → text[]

**Scope**: prompts
**Severity**: P3 (drift/contract — query capability regression)
**Status**: Data-model regression
**Estimated port effort**: 0.3h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:database/archived-sql-migrations/012_prompt_store.sql` and `packages/api/src/services/pg-prompt-store.ts`.

- File: `database/archived-sql-migrations/012_prompt_store.sql:17-32` (schema) and `packages/api/src/services/pg-prompt-store.ts` upsert code.
- Contract/behavior: `prompts.variables` was stored as `JSONB NOT NULL DEFAULT '[]'::jsonb`. Values serialized via `JSON.stringify(variables)` in the app layer, stored and queried using PostgreSQL JSONB operators (`->`, `@>`, `jsonb_array_elements`, etc.). This allowed future queries like "which prompts use the `conventions` variable?" via `WHERE variables @> '"conventions"'`.
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
  ...
);
```

And the app-layer serialization (`pg-prompt-store.ts:93`):

```typescript
const variablesJson = JSON.stringify(variables);
// used as $5::jsonb parameter in INSERT ... ON CONFLICT
```

- Dependencies: `pg` driver implicit JSONB parameter type, PostgreSQL JSONB operators.
- Tests that exercised this: `pg-prompt-store.test.ts` — variable roundtripping.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/Entities/PromptOverride.cs:13`.
- Contract/behavior: EF Core maps `Variables` as `string[]`, which Npgsql persists as the PostgreSQL `text[]` array type by default. The migration-less dev schema thus gets a `text[]` column, not `jsonb`.
- Key code (verbatim quote, `PromptOverride.cs:1-18`):

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
    public string[] Variables { get; set; } = [];           // <-- text[] in Postgres
    public bool EnableTools { get; set; }
    public int MaxTokens { get; set; } = 4096;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
```

Npgsql's default provider mapping for `string[]` → `text[]` (not `jsonb`). No `[Column(TypeName = "jsonb")]` annotation is present.

- Dependencies: Npgsql provider, EF Core conventions.
- Tests: `apps/tamma-elsa/tests/Tamma.Api.Tests/PromptStore/PromptStoreServiceTests.cs` roundtrips `Variables` but does not assert the storage type.

## 3. The gap

Concrete behavioral difference:

- TS did: store `variables` as JSONB, enabling ad-hoc query with `jsonb_array_elements`, `@>`, `?`, `?&`, `?|`.
- C# does: store `variables` as `text[]`, enabling `ANY()`, `= ANY(variables)`, `array_length`, `unnest(variables)`.

The two representations are semantically equivalent for the current app behavior (round-trip an array of strings), and both are indexable in PostgreSQL. Differences:

| Capability | TS (JSONB) | C# (text[]) |
|---|---|---|
| Query "prompts containing variable X" | `WHERE variables @> '"X"'` | `WHERE 'X' = ANY(variables)` |
| Index support | `CREATE INDEX ... USING gin (variables)` | `CREATE INDEX ... USING gin (variables)` |
| Nested structure future-proofing | Yes (can hold objects) | No (strings only) |
| Size per row (small arrays) | ~5-10 bytes overhead | ~2-4 bytes overhead |
| Type safety in JOIN/unnest | Requires casts | Native array |

For a caller, the behavioral difference is invisible — the `Variables` field round-trips correctly. But:

1. **Analytics SQL drift**: any ad-hoc query in the dashboard's postgres console written for JSONB operators will need translation to array operators.
2. **Future-proofing loss**: if a future story wanted variables to carry metadata (e.g., `{name: "role", type: "string", required: true}`), the JSONB column supported it directly. `text[]` forces a schema migration.
3. **ORM lock-in**: `text[]` binds the schema to Npgsql's `string[]` mapping; switching to a different ORM or bare ADO.NET would need explicit handling.

Data migration: none required for greenfield cutover since the C# API is the primary backend (per commit `0282f0e`) and the TS database was dropped.

Error paths:
- TS error path: invalid JSON in `variables` → 400 from JSONB cast.
- C# error path: `string[]` is always serializable — the only error path is at write time if the Npgsql array binding fails on a character outside the server encoding.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-27/27-1-prompt-store-database-schema.md` AC #1.
- Story's acceptance criteria for this behavior: AC #1 says *"`variables` (JSONB NOT NULL DEFAULT '[]')"* — explicitly JSONB.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs the story's explicit JSONB requirement)
  - [ ] Matches C# behavior — story mandates JSONB.

## 5. Status

- **Classification**: Data-model regression (contract drift)
- **What's needed to finish**:
  1. Decide whether the JSONB contract from epic-27-1 AC #1 is still relevant or whether the story should be amended.
  2. If keeping `text[]`: update epic-27-1 AC #1 to `text[]` and document the rationale (simplicity, no future nested-object use case).
  3. If restoring JSONB: add `[Column(TypeName = "jsonb")]` attribute to `PromptOverride.Variables` and annotate `OnModelCreating` with `.HasColumnType("jsonb")`; optionally change to `JsonDocument` or `string` for better portability.
- **Is it "just a stub" or is scope missing?** Scope was understood (array of strings) but the storage type choice was made by Npgsql's default mapping, not by deliberate decision.
- **Blockers**: None.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Data/Entities/PromptOverride.cs:13` — add `[Column(TypeName = "jsonb")]` (and change property type to `JsonDocument` or keep `string[]` with a value converter) if restoring JSONB.
  - Alternatively, `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs` — configure `.Property(p => p.Variables).HasColumnType("jsonb")`.
  - `docs/stories/epic-27/27-1-prompt-store-database-schema.md:13` — update AC #1 wording if keeping `text[]`.
- Files to create: None.
- Tests to add:
  - `PromptRepositoryTests.cs` — `Variables_RoundTripsWithJsonbContent` if restoring JSONB.
- Estimated effort: 0.3h broken down as:
  - Decision: 0.1h
  - Column-type annotation + roundtrip test: 0.2h

## References

- TS source: `database/archived-sql-migrations/012_prompt_store.sql:17-32`, `packages/api/src/services/pg-prompt-store.ts` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Data/Entities/PromptOverride.cs:13`
- Story: `docs/stories/epic-27/27-1-prompt-store-database-schema.md` AC #1
- Related findings: `docs/audit/port-gaps/prompts/010-prompt-overrides-missing-audit-columns.md`, `docs/audit/port-gaps/prompts/011-missing-unique-constraint.md`
- CLAUDE.md section: N/A (storage detail not documented)
- Archived SQL migration: `database/archived-sql-migrations/012_prompt_store.sql`
