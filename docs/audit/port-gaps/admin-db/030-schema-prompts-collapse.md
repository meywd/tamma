# Finding 030: `prompts`/`system_prompts`/`action_prompts` → `prompt_overrides` collapse

**Scope**: admin-db
**Severity**: P3 (per CLAUDE.md compliance) / P2 (data orphaning risk)
**Status**: Semantic rewrite
**Estimated port effort**: 2h (documentation + safety net)

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed (CHECK + version + audit columns; FK still deferred)
- **Notes**: `Phase1` migration adds (a) `ck_prompt_overrides_max_tokens_positive`, (b) `ck_prompt_overrides_version_positive`, (c) `Version int DEFAULT 1` for optimistic concurrency, (d) `CreatedBy / UpdatedBy uuid` audit columns. **Not done**: FK on `UserId / TenantId → users / tenants` per CLAUDE.md spec ("system defaults remain in code"). The collapse to `prompt_overrides` is intentional and CLAUDE.md-compliant; no regression vs current spec.

## 1. What's in TS

Archived at `database/archived-sql-migrations/012_prompt_store.sql`.

- File: `packages/api/database/migrations/012_prompt_store.sql`
- Contract/behavior: three separate tables with partial unique indexes that handled the system-default vs tenant-override split via `tenant_id IS NULL`.
- Key code (verbatim quote, annotated):

```sql
-- 012_prompt_store.sql (prompts)
CREATE TABLE IF NOT EXISTS prompts (
  id UUID PRIMARY KEY, tenant_id UUID REFERENCES tenants(id) ON DELETE CASCADE,
  role TEXT NOT NULL, action TEXT NOT NULL,
  template TEXT NOT NULL, system_prompt TEXT NOT NULL DEFAULT '',
  variables JSONB NOT NULL DEFAULT '[]'::jsonb,
  enable_tools BOOLEAN NOT NULL DEFAULT false,
  max_tokens INTEGER NOT NULL DEFAULT 4096 CHECK (max_tokens > 0),
  version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
  created_at ..., updated_at ..., created_by UUID, updated_by UUID
);
CREATE UNIQUE INDEX idx_prompts_system_default ON prompts (role, action) WHERE tenant_id IS NULL;
CREATE UNIQUE INDEX idx_prompts_tenant_override ON prompts (tenant_id, role, action) WHERE tenant_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS system_prompts (
  id UUID PRIMARY KEY, tenant_id UUID REFERENCES tenants(id) ON DELETE CASCADE,
  role TEXT NOT NULL, prompt TEXT NOT NULL,
  version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0), ...
);
CREATE UNIQUE INDEX idx_system_prompts_system_default ON system_prompts (role) WHERE tenant_id IS NULL;
CREATE UNIQUE INDEX idx_system_prompts_tenant_override ON system_prompts (tenant_id, role) WHERE tenant_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS action_prompts (
  id UUID PRIMARY KEY, tenant_id UUID, action TEXT NOT NULL, template TEXT NOT NULL,
  variables JSONB, enable_tools BOOLEAN, max_tokens INTEGER CHECK (max_tokens > 0),
  version INTEGER CHECK (version > 0), ...
);
-- Plus seed rows: 8 role preambles + 10 action templates
```

- Dependencies: `tenants(id)` FK with cascade. `version` field for optimistic concurrency.
- Tests that exercised this: prompt resolution unit tests.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs:94-115, 589-593`
- Contract/behavior: a single `prompt_overrides` table following CLAUDE.md's "Prompt Store Architecture" section — system defaults live in code (`default-prompts.ts`), user overrides in the table with a `Scope` discriminator. Variables is `text[]` instead of JSONB. `version` column dropped. No CHECK on `MaxTokens`. No FK to `users` or `tenants`. Uniqueness on `(UserId, Scope, Role, Action)`.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs (current)
migrationBuilder.CreateTable(
    name: "prompt_overrides",
    columns: table => new
    {
        Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
        UserId = table.Column<Guid>(type: "uuid", nullable: true),
        TenantId = table.Column<Guid>(type: "uuid", nullable: true),
        Scope = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
        Role = table.Column<string>(type: "text", nullable: true),
        Action = table.Column<string>(type: "text", nullable: true),
        Template = table.Column<string>(type: "text", nullable: false),
        SystemPrompt = table.Column<string>(type: "text", nullable: true),
        Variables = table.Column<string[]>(type: "text[]", nullable: false),          // ← was jsonb
        EnableTools = table.Column<bool>(type: "boolean", nullable: false),
        MaxTokens = table.Column<int>(type: "integer", nullable: false, defaultValue: 4096),  // ← no CHECK > 0
        CreatedAt = ..., UpdatedAt = ...
        // NO version column, NO created_by/updated_by
    },
    constraints: table => { table.PrimaryKey("PK_prompt_overrides", x => x.Id); });

migrationBuilder.CreateIndex(
    name: "IX_prompt_overrides_UserId_Scope_Role_Action",
    table: "prompt_overrides",
    columns: new[] { "UserId", "Scope", "Role", "Action" },
    unique: true);
```

- Dependencies: `IPromptStore` / `PromptResolutionService` (runtime).
- Tests: none.

## 3. The gap

| Aspect | TS | C# | Impact |
|---|---|---|---|
| Table count | 3 | 1 | CLAUDE.md compliance — intentional |
| `variables` type | `jsonb` | `text[]` | Minor — `text[]` is sufficient for the actual use case (list of var names) |
| `max_tokens > 0` CHECK | present | **absent** | Zero or negative max_tokens insertable, causes provider-API 400s downstream |
| `version` column | present (optimistic concurrency) | **absent** | Concurrent edits silently overwrite |
| `created_by`/`updated_by` | present | **absent** | No audit of who last edited |
| Seed rows (8 role preambles + 10 action templates) | inserted via migration | **not migrated** — defaults live in code | Intentional per CLAUDE.md. However, any TS data imported would be orphaned (no FK, no place to go) |
| FK to `tenants`/`users` | present with CASCADE | **absent** | Deleting a tenant leaves orphaned override rows |

- For a caller with `role=developer, action=implement` and no override, TS resolves via `prompts` system-default row; C# resolves via application code — both work, but TS could be patched via SQL while C# requires a code deploy.
- For a caller migrating TS overrides into C#: the data shape doesn't map 1:1 (3 tables → 1 with a `Scope` discriminator). Requires a bespoke ETL script.

Error paths: none at write time; concurrency issues (finding 030's missing `version`) are silent.

## 4. Gap from stories

- Referenced story: CLAUDE.md "Prompt Store Architecture" explicitly describes the `prompt_overrides` shape C# uses.
- Story alignment:
  - [ ] Matches TS behavior
  - [x] Matches C# behavior (aligned with CLAUDE.md spec)
  - [ ] Describes a third behavior
  - [ ] No story

CLAUDE.md mandates: `scope TEXT NOT NULL, role TEXT, action TEXT, template TEXT NOT NULL, system_prompt TEXT, variables TEXT[], enable_tools BOOLEAN DEFAULT false, max_tokens INTEGER DEFAULT 4096`. C# matches this schema. The three-table approach in TS was ahead of CLAUDE.md and has been unified.

## 5. Status

- **Classification**: Semantic rewrite (CLAUDE.md compliant) — **not a regression** against the current spec; only a regression vs. TS's richer schema.
- **What's needed to finish**:
  1. Add `CHECK (max_tokens > 0)` constraint.
  2. Add `version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0)` for optimistic concurrency.
  3. Add `CreatedBy`/`UpdatedBy uuid` nullable columns.
  4. Add FK on `UserId` → `users(Id)` with `ON DELETE CASCADE`, and `TenantId` → `tenants(Id)` with `ON DELETE CASCADE` (note: CLAUDE.md schema shows no FK; decide deliberately).
- **Is it "just a stub" or is scope missing?** Compliant with CLAUDE.md spec but hardening (CHECK, version, audit columns) dropped.
- **Blockers**: none.

## Remediation

- Files to modify: `PromptOverride.cs` entity.
- Files to create: `20260418000016_PromptOverridesHardening.cs`.
- Tests to add: insert `MaxTokens = 0` → CHECK violation; concurrent update → version conflict raised.
- Estimated effort: 2h.

## References

- TS source: `database/archived-sql-migrations/012_prompt_store.sql`
- C# source: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs`
- Story: none (CLAUDE.md is the spec)
- CLAUDE.md section: "Prompt Store Architecture"
- Related findings: none
