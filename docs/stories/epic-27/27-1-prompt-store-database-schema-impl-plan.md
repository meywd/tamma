# Story 27-1: Prompt Store Database Schema + Migration — Implementation Plan

## Overview

Create three PostgreSQL tables (`prompts`, `system_prompts`, `action_prompts`) with multi-tenant support via nullable `tenant_id`, partial unique indexes for NULL handling, and seed data generated from `packages/api/src/services/default-prompts.ts`. Uses the existing migration runner at `database/migrate.ts`.

---

## Step-by-Step Implementation Tasks

### Task 1: Create the Migration SQL File (3 hours)

**File to create**: `database/migrations/011_prompt_store.sql`

The migration number is 011 (see `/docs/stories/migration-ordering.md`). Migrations 008-010 are reserved for Epic 17 (tenants, RLS, event store scoping).

```sql
-- Prompt Store: Multi-tenant prompt management
-- Epic 27, Story 27-1

-- =============================================================================
-- Table: prompts (80 system defaults + tenant overrides)
-- =============================================================================

CREATE TABLE IF NOT EXISTS prompts (
  id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id    UUID,  -- NULL = system default; FK added when tenants table exists
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

-- Partial unique indexes (handle NULL tenant_id correctly)
CREATE UNIQUE INDEX IF NOT EXISTS idx_prompts_system_default
  ON prompts (role, action)
  WHERE tenant_id IS NULL;

CREATE UNIQUE INDEX IF NOT EXISTS idx_prompts_tenant_override
  ON prompts (tenant_id, role, action)
  WHERE tenant_id IS NOT NULL;

-- Lookup indexes
CREATE INDEX IF NOT EXISTS idx_prompts_tenant_id ON prompts (tenant_id);
CREATE INDEX IF NOT EXISTS idx_prompts_role_action ON prompts (role, action);

-- =============================================================================
-- Table: system_prompts (8 role preambles + tenant overrides)
-- =============================================================================

CREATE TABLE IF NOT EXISTS system_prompts (
  id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id  UUID,
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

-- =============================================================================
-- Table: action_prompts (10 action defaults + tenant overrides)
-- =============================================================================

CREATE TABLE IF NOT EXISTS action_prompts (
  id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id   UUID,
  action       TEXT NOT NULL,
  template     TEXT NOT NULL,
  variables    JSONB NOT NULL DEFAULT '[]'::jsonb,
  enable_tools BOOLEAN NOT NULL DEFAULT false,
  max_tokens   INTEGER NOT NULL DEFAULT 4096 CHECK (max_tokens > 0),
  version      INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
  created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  created_by   UUID,
  updated_by   UUID
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

**Note on FK constraints**: The `tenant_id` FK to `tenants(id)` is deferred until Epic 17 is deployed. When adding the FK later, use:
```sql
ALTER TABLE prompts ADD CONSTRAINT fk_prompts_tenant_id
  FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
```

---

### Task 2: Create the Seed Data Generation Script (2 hours)

**File to create**: `scripts/generate-prompt-seed-sql.ts`

This script imports the existing default prompts and system prompts, then outputs SQL INSERT statements to stdout (which are then appended to the migration file).

```typescript
/**
 * Generates SQL INSERT statements for seeding the prompt store tables
 * from the existing default-prompts.ts constants.
 *
 * Usage: npx tsx scripts/generate-prompt-seed-sql.ts >> database/migrations/011_prompt_store.sql
 */

import { getDefaultPrompts, VALID_ROLES, VALID_ACTIONS } from '../packages/api/src/services/default-prompts.js';

// PostgreSQL string escaping
function escSql(s: string): string {
  return s.replace(/'/g, "''");
}

// JSON array of variable names
function varsToJsonb(vars: string[]): string {
  return `'${JSON.stringify(vars)}'::jsonb`;
}

function main(): void {
  const prompts = getDefaultPrompts();

  // --- prompts table (80 rows) ---
  console.log('\n-- Seed: prompts (80 system defaults)');
  console.log('INSERT INTO prompts (tenant_id, role, action, template, system_prompt, variables, enable_tools, max_tokens, version)');
  console.log('VALUES');

  const promptRows = prompts.map((p, i) => {
    const comma = i < prompts.length - 1 ? ',' : '';
    return `  (NULL, '${escSql(p.role)}', '${escSql(p.action)}', E'${escSql(p.template)}', E'${escSql(p.systemPrompt)}', ${varsToJsonb(p.variables)}, ${p.enableTools}, ${p.maxTokens}, 1)${comma}`;
  });
  console.log(promptRows.join('\n'));
  console.log('ON CONFLICT DO NOTHING;\n');

  // --- system_prompts table (8 rows) ---
  // Extract unique system prompts per role from the defaults
  console.log('-- Seed: system_prompts (8 role preambles)');
  console.log('INSERT INTO system_prompts (tenant_id, role, prompt, version)');
  console.log('VALUES');

  const seenRoles = new Set<string>();
  const systemPromptRows: string[] = [];
  for (const p of prompts) {
    if (!seenRoles.has(p.role) && p.systemPrompt.length > 0) {
      seenRoles.add(p.role);
      systemPromptRows.push(`  (NULL, '${escSql(p.role)}', E'${escSql(p.systemPrompt)}', 1)`);
    }
  }
  console.log(systemPromptRows.join(',\n'));
  console.log('ON CONFLICT DO NOTHING;\n');

  // --- action_prompts table (10 rows) ---
  // Use the 'developer' role's templates as the generic action defaults
  console.log('-- Seed: action_prompts (10 action defaults)');
  console.log('INSERT INTO action_prompts (tenant_id, action, template, variables, enable_tools, max_tokens, version)');
  console.log('VALUES');

  const actionRows: string[] = [];
  for (const action of VALID_ACTIONS) {
    const exemplar = prompts.find((p) => p.role === 'developer' && p.action === action);
    if (exemplar) {
      actionRows.push(`  (NULL, '${escSql(action)}', E'${escSql(exemplar.template)}', ${varsToJsonb(exemplar.variables)}, ${exemplar.enableTools}, ${exemplar.maxTokens}, 1)`);
    }
  }
  console.log(actionRows.join(',\n'));
  console.log('ON CONFLICT DO NOTHING;');
}

main();
```

**Execution**: Run this script once and pipe the output into the migration file:
```bash
npx tsx scripts/generate-prompt-seed-sql.ts >> database/migrations/011_prompt_store.sql
```

The generated INSERT statements are then committed as part of the migration file. The script is retained as a reference tool.

---

### Task 3: Run and Verify the Migration (1 hour)

```bash
# Apply migration
DATABASE_URL=postgres://... npx tsx database/migrate.ts

# Verify tables exist
psql $DATABASE_URL -c "\dt prompts"
psql $DATABASE_URL -c "\dt system_prompts"
psql $DATABASE_URL -c "\dt action_prompts"

# Verify row counts
psql $DATABASE_URL -c "SELECT COUNT(*) FROM prompts WHERE tenant_id IS NULL"       -- expect 80
psql $DATABASE_URL -c "SELECT COUNT(*) FROM system_prompts WHERE tenant_id IS NULL" -- expect 8
psql $DATABASE_URL -c "SELECT COUNT(*) FROM action_prompts WHERE tenant_id IS NULL" -- expect 10

# Verify idempotency (run again)
DATABASE_URL=postgres://... npx tsx database/migrate.ts
# Should print "skip: 011_prompt_store.sql (already applied)"
```

---

### Task 4: Write Tests (3 hours)

**File to create**: `database/migrations/__tests__/011_prompt_store.test.ts`

This test connects to a test PostgreSQL instance and validates the migration.

```typescript
import { describe, it, expect, beforeAll, afterAll } from 'vitest';
import pg from 'pg';

// Uses DATABASE_URL_TEST env var for a test database
const TEST_DB_URL = process.env['DATABASE_URL_TEST'];

describe('Migration 008: Prompt Store', () => {
  let client: pg.Client;

  beforeAll(async () => {
    if (!TEST_DB_URL) throw new Error('DATABASE_URL_TEST required');
    client = new pg.Client({ connectionString: TEST_DB_URL });
    await client.connect();
  });

  afterAll(async () => {
    await client.end();
  });

  // Test 1: Tables exist with correct columns
  it('should create prompts table with expected columns', async () => {
    const result = await client.query(`
      SELECT column_name, data_type, is_nullable
      FROM information_schema.columns
      WHERE table_name = 'prompts'
      ORDER BY ordinal_position
    `);
    const columns = result.rows.map((r: Record<string, unknown>) => r['column_name']);
    expect(columns).toContain('id');
    expect(columns).toContain('tenant_id');
    expect(columns).toContain('role');
    expect(columns).toContain('action');
    expect(columns).toContain('template');
    expect(columns).toContain('system_prompt');
    expect(columns).toContain('variables');
    expect(columns).toContain('enable_tools');
    expect(columns).toContain('max_tokens');
    expect(columns).toContain('version');
    expect(columns).toContain('created_by');
    expect(columns).toContain('updated_by');
  });

  // Test 2: Partial unique indexes prevent duplicate system defaults
  it('should reject duplicate system default for same role+action', async () => {
    await expect(client.query(`
      INSERT INTO prompts (tenant_id, role, action, template) VALUES (NULL, 'developer', 'context-scan', 'dup')
    `)).rejects.toThrow(/unique/i);
  });

  // Test 3: Partial unique indexes allow same role+action for different tenants
  it('should allow same role+action for different tenant_ids', async () => {
    const acct1 = '11111111-1111-1111-1111-111111111111';
    const acct2 = '22222222-2222-2222-2222-222222222222';
    await client.query(`INSERT INTO prompts (tenant_id, role, action, template) VALUES ($1, 'developer', 'plan', 'acct1 override')`, [acct1]);
    await client.query(`INSERT INTO prompts (tenant_id, role, action, template) VALUES ($1, 'developer', 'plan', 'acct2 override')`, [acct2]);
    // Cleanup
    await client.query(`DELETE FROM prompts WHERE tenant_id IN ($1, $2)`, [acct1, acct2]);
  });

  // Test 4: Seed data row counts
  it('should seed 80 system default prompts', async () => {
    const result = await client.query(`SELECT COUNT(*)::int as cnt FROM prompts WHERE tenant_id IS NULL`);
    expect(result.rows[0]!['cnt']).toBe(80);
  });

  it('should seed 8 system prompts', async () => {
    const result = await client.query(`SELECT COUNT(*)::int as cnt FROM system_prompts WHERE tenant_id IS NULL`);
    expect(result.rows[0]!['cnt']).toBe(8);
  });

  it('should seed 10 action defaults', async () => {
    const result = await client.query(`SELECT COUNT(*)::int as cnt FROM action_prompts WHERE tenant_id IS NULL`);
    expect(result.rows[0]!['cnt']).toBe(10);
  });

  // Test 5: CHECK constraints
  it('should reject max_tokens <= 0', async () => {
    await expect(client.query(`
      INSERT INTO prompts (tenant_id, role, action, template, max_tokens)
      VALUES ('33333333-3333-3333-3333-333333333333', 'test', 'test', 'test', 0)
    `)).rejects.toThrow(/check/i);
  });

  it('should reject version <= 0', async () => {
    await expect(client.query(`
      INSERT INTO prompts (tenant_id, role, action, template, version)
      VALUES ('33333333-3333-3333-3333-333333333333', 'test2', 'test2', 'test', 0)
    `)).rejects.toThrow(/check/i);
  });
});
```

---

## Files to Create

| # | File Path | Purpose |
|---|-----------|---------|
| 1 | `database/migrations/011_prompt_store.sql` | DDL + seed data |
| 2 | `scripts/generate-prompt-seed-sql.ts` | One-time seed SQL generator |
| 3 | `database/migrations/__tests__/011_prompt_store.test.ts` | Integration tests |

## Files to Modify

None. This story is purely additive.

---

## Dependencies

- **Internal**: `packages/api/src/services/default-prompts.ts` (source of seed data, provides `getDefaultPrompts()`, `VALID_ROLES`, `VALID_ACTIONS`, `SYSTEM_PROMPTS`)
- **External**: PostgreSQL 17 (existing instance)
- **Future FK**: `tenants` table from Epic 17 (FK constraint deferred)

---

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| `tenants` table does not exist yet (Epic 17) | Omit FK constraint on `tenant_id` for now; add it via a future migration when tenants table is deployed |
| Template text contains single quotes or escape characters | The seed generator script properly escapes SQL strings using `E'...'` syntax and `''` for literal quotes |
| Migration file becomes very large (80 templates can be lengthy) | Expected ~200KB of SQL — well within PostgreSQL's migration limits; could split into `008a_prompt_store_ddl.sql` and `008b_prompt_store_seed.sql` if needed |
| Idempotency on re-run | All `CREATE TABLE IF NOT EXISTS`, `CREATE INDEX IF NOT EXISTS`, and `INSERT ... ON CONFLICT DO NOTHING` ensure safe re-runs |

---

## Estimated Effort

| Task | Hours |
|------|-------|
| Migration SQL (3 tables, indexes, constraints) | 3 |
| Seed script to generate INSERT statements | 2 |
| Seed data SQL (80 + 8 + 10 rows) | 2 |
| Integration tests | 3 |
| **Total** | **10 hours** |
