# Story 27-1: Prompt Store Database Schema + Migration

Status: ready-for-dev

## Story

As a **platform engineer**,
I want PostgreSQL tables for storing prompt templates, system prompts, and action prompts with multi-tenant support,
so that prompts are persisted in the database with tenant-level isolation and can be resolved using a two-tier fallback (tenant override then system default).

## Acceptance Criteria

1. A `prompts` table exists with columns: `id` (UUID PK), `tenant_id` (UUID nullable, FK to tenants), `role` (TEXT NOT NULL), `action` (TEXT NOT NULL), `template` (TEXT NOT NULL), `system_prompt` (TEXT NOT NULL DEFAULT ''), `variables` (JSONB NOT NULL DEFAULT '[]'), `enable_tools` (BOOLEAN NOT NULL DEFAULT false), `max_tokens` (INTEGER NOT NULL DEFAULT 4096), `version` (INTEGER NOT NULL DEFAULT 1), `created_at` (TIMESTAMPTZ), `updated_at` (TIMESTAMPTZ), `created_by` (UUID nullable), `updated_by` (UUID nullable)
2. A UNIQUE constraint exists on `(tenant_id, role, action)` — using a partial unique index to handle NULL tenant_id correctly: one index for `WHERE tenant_id IS NULL` and one for `WHERE tenant_id IS NOT NULL`
3. A `system_prompts` table exists with columns: `id` (UUID PK), `tenant_id` (UUID nullable), `role` (TEXT NOT NULL), `prompt` (TEXT NOT NULL), `version` (INTEGER NOT NULL DEFAULT 1), `created_at`, `updated_at`, `created_by`, `updated_by`
4. A UNIQUE constraint exists on `(tenant_id, role)` for `system_prompts` with the same partial index pattern
5. An `action_prompts` table exists with columns: `id` (UUID PK), `tenant_id` (UUID nullable), `action` (TEXT NOT NULL), `template` (TEXT NOT NULL), `variables` (JSONB NOT NULL DEFAULT '[]'), `enable_tools` (BOOLEAN NOT NULL DEFAULT false), `max_tokens` (INTEGER NOT NULL DEFAULT 4096), `version` (INTEGER NOT NULL DEFAULT 1), `created_at`, `updated_at`, `created_by`, `updated_by`
6. A UNIQUE constraint exists on `(tenant_id, action)` for `action_prompts` with the same partial index pattern
7. B-tree indexes exist on: `prompts(tenant_id)`, `prompts(role, action)`, `system_prompts(tenant_id)`, `system_prompts(role)`, `action_prompts(tenant_id)`, `action_prompts(action)`
8. Seed migration inserts 80 system default role+action templates (8 roles x 10 actions) from `default-prompts.ts` with `tenant_id = NULL`
9. Seed migration inserts 8 system prompts (one per role) with `tenant_id = NULL`
10. Seed migration inserts 10 action defaults (one per action) with `tenant_id = NULL`
11. All seed inserts use `ON CONFLICT DO NOTHING` for idempotency
12. Migration is idempotent (running it twice produces no errors)
13. CHECK constraints validate: `role` is in the known roles set, `action` is in the known actions set, `max_tokens > 0`, `version > 0`

## Technical Context

### Current State

Prompts are stored in an in-memory `Map<PromptKey, PromptTemplate>` inside `packages/api/src/services/prompt-store.ts`, backed by a JSON file at `./data/prompts.json`. The 80 default templates are generated at runtime by `packages/api/src/services/default-prompts.ts`.

The existing `PromptTemplate` interface:

```typescript
interface PromptTemplate {
  role: string;
  action: string;
  version: number;
  template: string;
  variables: string[];
  systemPrompt: string;
  enableTools: boolean;
  maxTokens: number;
  createdAt: string;
  updatedAt: string;
}
```

### Why Three Tables

| Table | Purpose | Example |
|-------|---------|---------|
| `prompts` | Full role+action templates (the main table, 80 rows) | developer + implement = full implementation prompt |
| `system_prompts` | Role identity preambles (8 rows) | developer = "You are an expert software developer..." |
| `action_prompts` | Action-level defaults without role specificity (10 rows) | implement = generic implementation template |

Separating system prompts and action prompts from the main `prompts` table avoids denormalization. The `prompts.system_prompt` column is retained for backward compatibility and can override the role-level system prompt for a specific role+action combination.

### NULL vs Sentinel for tenant_id

System defaults use `tenant_id IS NULL` rather than the sentinel `DEFAULT_TENANT_ID` from Epic 17. This is intentional:
- `NULL` = shipped with Tamma, managed by platform admin
- `DEFAULT_TENANT_ID` = the default tenant's own overrides (CLI/self-hosted mode)
- Any other UUID = a specific tenant's overrides

This three-way distinction matters because platform admins may want to update system defaults independently of any tenant's data.

### Partial Unique Indexes for NULL tenant_id

PostgreSQL treats `NULL != NULL` in unique constraints, so `UNIQUE(tenant_id, role, action)` would allow duplicate system defaults. The solution is two partial indexes:

```sql
CREATE UNIQUE INDEX idx_prompts_system_default
  ON prompts (role, action)
  WHERE tenant_id IS NULL;

CREATE UNIQUE INDEX idx_prompts_tenant_override
  ON prompts (tenant_id, role, action)
  WHERE tenant_id IS NOT NULL;
```

### Files to Create

| File | Purpose |
|------|---------|
| `database/migrations/XXX_prompt_store.sql` | Create tables, indexes, seed data |

### Files to Modify

| File | Change |
|------|--------|
| None | This story is purely additive (new tables) |

## Implementation Plan

### Step 1: Create the Tables

```sql
-- prompts table
CREATE TABLE IF NOT EXISTS prompts (
  id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id    UUID REFERENCES tenants(id) ON DELETE CASCADE,
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

-- Partial unique indexes
CREATE UNIQUE INDEX IF NOT EXISTS idx_prompts_system_default
  ON prompts (role, action)
  WHERE tenant_id IS NULL;

CREATE UNIQUE INDEX IF NOT EXISTS idx_prompts_tenant_override
  ON prompts (tenant_id, role, action)
  WHERE tenant_id IS NOT NULL;

-- Lookup indexes
CREATE INDEX IF NOT EXISTS idx_prompts_tenant_id ON prompts (tenant_id);
CREATE INDEX IF NOT EXISTS idx_prompts_role_action ON prompts (role, action);
```

```sql
-- system_prompts table
CREATE TABLE IF NOT EXISTS system_prompts (
  id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id  UUID REFERENCES tenants(id) ON DELETE CASCADE,
  role        TEXT NOT NULL,
  prompt      TEXT NOT NULL,
  version     INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
  created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  created_by  UUID,
  updated_by  UUID
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_system_prompts_system_default
  ON system_prompts (role)
  WHERE tenant_id IS NULL;

CREATE UNIQUE INDEX IF NOT EXISTS idx_system_prompts_tenant_override
  ON system_prompts (tenant_id, role)
  WHERE tenant_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_system_prompts_tenant_id ON system_prompts (tenant_id);
CREATE INDEX IF NOT EXISTS idx_system_prompts_role ON system_prompts (role);
```

```sql
-- action_prompts table
CREATE TABLE IF NOT EXISTS action_prompts (
  id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id  UUID REFERENCES tenants(id) ON DELETE CASCADE,
  action      TEXT NOT NULL,
  template    TEXT NOT NULL,
  variables   JSONB NOT NULL DEFAULT '[]'::jsonb,
  enable_tools BOOLEAN NOT NULL DEFAULT false,
  max_tokens  INTEGER NOT NULL DEFAULT 4096 CHECK (max_tokens > 0),
  version     INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
  created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  created_by  UUID,
  updated_by  UUID
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_action_prompts_system_default
  ON action_prompts (action)
  WHERE tenant_id IS NULL;

CREATE UNIQUE INDEX IF NOT EXISTS idx_action_prompts_tenant_override
  ON action_prompts (tenant_id, action)
  WHERE tenant_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_action_prompts_tenant_id ON action_prompts (tenant_id);
CREATE INDEX IF NOT EXISTS idx_action_prompts_action ON action_prompts (action);
```

### Step 2: Seed System Default Prompts

The seed data is generated from the existing `default-prompts.ts` constants. Each of the 80 role+action combinations becomes an INSERT:

```sql
INSERT INTO prompts (tenant_id, role, action, template, system_prompt, variables, enable_tools, max_tokens, version)
VALUES
  (NULL, 'developer', 'context-scan', E'...template text...', E'...system prompt...', '["role","workItemType","workItemJson","previousFindings"]'::jsonb, true, 4096, 1),
  -- ... 79 more rows
ON CONFLICT DO NOTHING;
```

System prompts (8 rows):

```sql
INSERT INTO system_prompts (tenant_id, role, prompt, version)
VALUES
  (NULL, 'developer', E'You are an expert software developer...', 1),
  (NULL, 'tester', E'You are a testing specialist...', 1),
  -- ... 6 more rows
ON CONFLICT DO NOTHING;
```

Action defaults (10 rows — one generic template per action, not role-specific):

```sql
INSERT INTO action_prompts (tenant_id, action, template, variables, enable_tools, max_tokens, version)
VALUES
  (NULL, 'context-scan', E'...generic context-scan template...', '["workItemType","workItemJson","previousFindings"]'::jsonb, true, 4096, 1),
  -- ... 9 more rows
ON CONFLICT DO NOTHING;
```

### Step 3: Seed Script Generation

To avoid manually transcribing 80 templates into SQL, create a one-time TypeScript script:

```
scripts/generate-prompt-seed-sql.ts
```

This script imports `getDefaultPrompts()` and `SYSTEM_PROMPTS` from `default-prompts.ts`, escapes strings for PostgreSQL, and outputs the INSERT statements. The generated SQL is committed into the migration file.

## Implementation Notes

1. The `tenant_id` FK references `tenants(id)` from Epic 17. If Epic 17 is not yet deployed, the FK constraint can be deferred or the migration can add a `tenants` dependency check.
2. `ON DELETE CASCADE` on the `tenant_id` FK means deleting a tenant automatically deletes all its prompt overrides. System defaults (`tenant_id IS NULL`) are unaffected.
3. The `created_by` and `updated_by` columns are nullable UUIDs referencing users. They are not FK-constrained to allow flexibility (system seeding has no user context).
4. The `variables` column is JSONB storing an array of strings (variable names). This matches the existing `PromptTemplate.variables: string[]`.
5. CHECK constraints on `role` and `action` can reference the valid sets directly in SQL, or be enforced at the application level only. Given that new roles/actions may be added, application-level validation is preferred, so CHECK constraints are omitted in favor of the partial unique indexes.

## Testing Strategy

### Unit Tests

1. Migration SQL parses without syntax errors (SQL parse test)
2. Tables are created with correct columns and types
3. Partial unique indexes prevent duplicate system defaults
4. Partial unique indexes allow the same role+action for different tenant_ids
5. Seed data inserts 80 rows into `prompts`, 8 into `system_prompts`, 10 into `action_prompts`
6. Re-running seed (ON CONFLICT DO NOTHING) does not change row counts

### Integration Tests

7. Run migration against a test PostgreSQL database -- verify tables, columns, indexes exist
8. Insert a system default and an tenant override for the same role+action -- both rows exist
9. Delete a tenant -- verify CASCADE deletes tenant overrides but system defaults remain
10. `max_tokens <= 0` rejected by CHECK constraint
11. `version <= 0` rejected by CHECK constraint

## Migration Number

This story uses **migration 011** (`011_prompt_store.sql`). See `/docs/stories/migration-ordering.md` for the cross-epic migration sequence.

## Dependencies

- **Epic 17** (Story 17-1: Tenant Model + Database Schema) -- the `tenants` table must exist for FK references (migration 008)
- Internal: `packages/api/src/services/default-prompts.ts` (source of seed data)

## Estimated Effort

| Task | Hours |
|------|-------|
| Migration SQL (3 tables, indexes, constraints) | 3 |
| Seed script to generate INSERT statements from default-prompts.ts | 2 |
| Seed data SQL (80 + 8 + 10 rows) | 2 |
| Unit tests | 1.5 |
| Integration tests | 1.5 |
| **Total** | **10 hours** |

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-04-08 | 1.0 | Initial story creation | Architecture Team |
