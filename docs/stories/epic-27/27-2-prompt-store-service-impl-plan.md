# Story 27-2: Prompt Store Service (TypeScript) — Implementation Plan

## Overview

Replace the file-based `PromptStore` class with a PostgreSQL-backed `PgPromptStore` and an `InMemoryPromptStore` for testing. Both implement a new `IPromptStore` interface that adds `tenantId` as the first parameter to all methods. The existing `PromptStore` class is retained for backward compatibility in CLI/standalone mode but the interface is extracted and exported.

---

## Step-by-Step Implementation Tasks

### Task 1: Extract IPromptStore Interface (1 hour)

**File to modify**: `packages/api/src/services/prompt-store.ts`

Replace the class-only export with an interface + class. Keep the existing `PromptStore` class but make it implement `IPromptStore`.

```typescript
// packages/api/src/services/prompt-store.ts — additions at top of file

/** Tenant-scoped prompt store interface. */
export interface IPromptStore {
  // --- Tenant-scoped operations ---
  get(tenantId: string | null, role: string, action: string): Promise<PromptTemplate | undefined>;
  upsert(tenantId: string | null, role: string, action: string, input: UpsertPromptInput, userId?: string): Promise<PromptTemplate>;
  delete(tenantId: string, role: string, action: string, userId?: string): Promise<boolean>;
  list(tenantId: string | null): Promise<PromptSummary[]>;
  render(tenantId: string | null, role: string, action: string, input: RenderInput): Promise<RenderedPrompt | undefined>;

  // --- System default operations ---
  getSystemDefault(role: string, action: string): Promise<PromptTemplate | undefined>;
  upsertSystemDefault(role: string, action: string, input: UpsertPromptInput, userId?: string): Promise<PromptTemplate>;
  resetSystemDefault(role: string, action: string, userId?: string): Promise<PromptTemplate | undefined>;
  listSystemDefaults(): Promise<PromptSummary[]>;

  // --- System prompts (role preambles) ---
  getSystemPrompt(tenantId: string | null, role: string): Promise<string | undefined>;
  upsertSystemPrompt(tenantId: string | null, role: string, prompt: string, userId?: string): Promise<void>;
}
```

Also extend `PromptSummary` to include source information:

```typescript
export interface PromptSummary {
  role: string;
  action: string;
  version: number;
  enableTools: boolean;
  maxTokens: number;
  variableCount: number;
  updatedAt: string;
  /** Whether this is an tenant override ('override') or system default ('system') */
  source: 'system' | 'override';
  /** The tenant_id if this is an override, null if system default */
  tenantId: string | null;
}
```

---

### Task 2: Extract interpolateTemplate Utility (1 hour)

**File to create**: `packages/api/src/services/prompt-interpolation.ts`

Extract the `_interpolate()` and `_extractVariables()` methods from the existing `PromptStore` into a shared utility so both `PgPromptStore` and `InMemoryPromptStore` can use them.

```typescript
/** Maximum rendered template length (1 MB). */
export const MAX_TEMPLATE_LENGTH = 1_000_000;

/** Maximum variable value length (100 KB). */
export const MAX_VAR_VALUE_LENGTH = 100_000;

export interface InterpolationLogger {
  warn: (obj: object, msg: string) => void;
}

/**
 * Extract {{variable}} names from a template string.
 */
export function extractVariables(template: string): string[] {
  const matches = template.matchAll(/\{\{([^}]{1,64})\}\}/g);
  const vars = new Set<string>();
  for (const match of matches) {
    const varName = match[1];
    if (varName !== undefined) {
      vars.add(varName);
    }
  }
  return [...vars];
}

/**
 * Single-pass {{variable}} interpolation.
 * Prevents recursive expansion (template injection safety).
 * Tracks unresolved variables in the provided array.
 */
export function interpolateTemplate(
  template: string,
  vars: Record<string, string>,
  unresolvedTracker: string[],
  logger?: InterpolationLogger,
): string {
  let result = template.replace(/\{\{([^}]{1,64})\}\}/g, (_match, key: string) => {
    const value = vars[key];
    if (value === undefined) {
      unresolvedTracker.push(key);
      return `{{${key}}}`;
    }
    if (value.length > MAX_VAR_VALUE_LENGTH) {
      logger?.warn(
        { key, valueLength: value.length, limit: MAX_VAR_VALUE_LENGTH },
        'Variable value exceeds maximum length, leaving unresolved',
      );
      unresolvedTracker.push(key);
      return `{{${key}}}`;
    }
    return value;
  });

  if (result.length > MAX_TEMPLATE_LENGTH) {
    logger?.warn(
      { length: result.length, limit: MAX_TEMPLATE_LENGTH },
      'Rendered template exceeds maximum length, truncating',
    );
    result = result.slice(0, MAX_TEMPLATE_LENGTH);
  }

  return result;
}
```

---

### Task 3: Implement InMemoryPromptStore (3 hours)

**File to create**: `packages/api/src/services/in-memory-prompt-store.ts`

Port the existing in-memory logic with tenant awareness. Uses a `Map<string, PromptTemplate>` keyed by `"tenantId|role:action"` (with `"null"` for system defaults).

```typescript
import type { PromptTemplate } from './default-prompts.js';
import { getDefaultPrompts } from './default-prompts.js';
import type { IPromptStore, UpsertPromptInput, RenderInput, PromptSummary, RenderedPrompt } from './prompt-store.js';
import { interpolateTemplate, extractVariables } from './prompt-interpolation.js';

export class InMemoryPromptStore implements IPromptStore {
  private readonly prompts: Map<string, PromptTemplate> = new Map();
  private readonly systemPromptMap: Map<string, string> = new Map(); // "tenantId|role" -> prompt text

  private _key(tenantId: string | null, role: string, action: string): string {
    return `${tenantId ?? 'null'}|${role}:${action}`;
  }

  private _systemPromptKey(tenantId: string | null, role: string): string {
    return `${tenantId ?? 'null'}|${role}`;
  }

  async get(tenantId: string | null, role: string, action: string): Promise<PromptTemplate | undefined> {
    // 1. Try tenant override
    if (tenantId !== null) {
      const override = this.prompts.get(this._key(tenantId, role, action));
      if (override !== undefined) return { ...override, variables: [...override.variables] };
    }
    // 2. Fall back to system default
    const system = this.prompts.get(this._key(null, role, action));
    if (system !== undefined) return { ...system, variables: [...system.variables] };
    return undefined;
  }

  async upsert(tenantId: string | null, role: string, action: string, input: UpsertPromptInput, _userId?: string): Promise<PromptTemplate> {
    const key = this._key(tenantId, role, action);
    const existing = this.prompts.get(key);
    const ts = new Date().toISOString();
    const template: PromptTemplate = {
      role, action,
      version: existing !== undefined ? existing.version + 1 : 1,
      template: input.template,
      variables: input.variables ?? extractVariables(input.template),
      systemPrompt: input.systemPrompt ?? existing?.systemPrompt ?? '',
      enableTools: input.enableTools ?? existing?.enableTools ?? false,
      maxTokens: input.maxTokens ?? existing?.maxTokens ?? 4096,
      createdAt: existing?.createdAt ?? ts,
      updatedAt: ts,
    };
    this.prompts.set(key, template);
    return { ...template, variables: [...template.variables] };
  }

  async delete(tenantId: string, role: string, action: string, _userId?: string): Promise<boolean> {
    return this.prompts.delete(this._key(tenantId, role, action));
  }

  async list(tenantId: string | null): Promise<PromptSummary[]> {
    // Collect system defaults, then overlay tenant overrides
    const merged = new Map<string, PromptSummary>();

    // System defaults first
    for (const [key, t] of this.prompts) {
      if (key.startsWith('null|')) {
        merged.set(`${t.role}:${t.action}`, {
          role: t.role, action: t.action, version: t.version,
          enableTools: t.enableTools, maxTokens: t.maxTokens,
          variableCount: t.variables.length, updatedAt: t.updatedAt,
          source: 'system', tenantId: null,
        });
      }
    }

    // Account overrides (replace defaults)
    if (tenantId !== null) {
      const prefix = `${tenantId}|`;
      for (const [key, t] of this.prompts) {
        if (key.startsWith(prefix)) {
          merged.set(`${t.role}:${t.action}`, {
            role: t.role, action: t.action, version: t.version,
            enableTools: t.enableTools, maxTokens: t.maxTokens,
            variableCount: t.variables.length, updatedAt: t.updatedAt,
            source: 'override', tenantId,
          });
        }
      }
    }

    const summaries = [...merged.values()];
    summaries.sort((a, b) => a.role.localeCompare(b.role) || a.action.localeCompare(b.action));
    return summaries;
  }

  async render(tenantId: string | null, role: string, action: string, input: RenderInput): Promise<RenderedPrompt | undefined> {
    const template = await this.get(tenantId, role, action);
    if (template === undefined) return undefined;

    const unresolvedVariables: string[] = [];
    const renderedTemplate = interpolateTemplate(template.template, input.variables, unresolvedVariables);
    const renderedSystemPrompt = interpolateTemplate(template.systemPrompt, input.variables, unresolvedVariables);

    return {
      role: template.role, action: template.action, version: template.version,
      renderedTemplate, renderedSystemPrompt,
      enableTools: template.enableTools, maxTokens: template.maxTokens,
      unresolvedVariables: [...new Set(unresolvedVariables)],
    };
  }

  // System default operations delegate to tenantId=null
  async getSystemDefault(role: string, action: string): Promise<PromptTemplate | undefined> {
    return this.get(null, role, action);
  }
  async upsertSystemDefault(role: string, action: string, input: UpsertPromptInput, userId?: string): Promise<PromptTemplate> {
    return this.upsert(null, role, action, input, userId);
  }
  async resetSystemDefault(role: string, action: string, _userId?: string): Promise<PromptTemplate | undefined> {
    const defaults = getDefaultPrompts();
    const found = defaults.find((d) => d.role === role && d.action === action);
    if (found === undefined) return undefined;
    return this.upsert(null, role, action, {
      template: found.template,
      variables: found.variables,
      systemPrompt: found.systemPrompt,
      enableTools: found.enableTools,
      maxTokens: found.maxTokens,
    });
  }
  async listSystemDefaults(): Promise<PromptSummary[]> {
    return this.list(null);
  }

  // System prompt operations
  async getSystemPrompt(tenantId: string | null, role: string): Promise<string | undefined> {
    if (tenantId !== null) {
      const override = this.systemPromptMap.get(this._systemPromptKey(tenantId, role));
      if (override !== undefined) return override;
    }
    return this.systemPromptMap.get(this._systemPromptKey(null, role));
  }
  async upsertSystemPrompt(tenantId: string | null, role: string, prompt: string, _userId?: string): Promise<void> {
    this.systemPromptMap.set(this._systemPromptKey(tenantId, role), prompt);
  }

  /** Seed system defaults for testing */
  seedDefaults(): void {
    const defaults = getDefaultPrompts();
    for (const d of defaults) {
      const key = this._key(null, d.role, d.action);
      if (!this.prompts.has(key)) {
        this.prompts.set(key, { ...d, variables: [...d.variables] });
      }
    }
  }
}
```

---

### Task 4: Implement PgPromptStore (4 hours)

**File to create**: `packages/api/src/services/pg-prompt-store.ts`

Follows the `PgInstallationStore` pattern: takes `pg.Pool` via constructor, uses parameterized queries.

```typescript
import type pg from 'pg';
import type { PromptTemplate } from './default-prompts.js';
import { getDefaultPrompts } from './default-prompts.js';
import type { IPromptStore, UpsertPromptInput, RenderInput, PromptSummary, RenderedPrompt } from './prompt-store.js';
import { interpolateTemplate, extractVariables } from './prompt-interpolation.js';

export class PgPromptStore implements IPromptStore {
  constructor(
    private readonly pool: pg.Pool,
    private readonly logger?: { info: (obj: object, msg: string) => void; warn: (obj: object, msg: string) => void; error: (obj: object, msg: string) => void },
  ) {}

  // --- Key queries ---

  async get(tenantId: string | null, role: string, action: string): Promise<PromptTemplate | undefined> {
    // 1. Try tenant override
    if (tenantId !== null) {
      const override = await this.pool.query<Record<string, unknown>>(
        `SELECT * FROM prompts WHERE tenant_id = $1 AND role = $2 AND action = $3`,
        [tenantId, role, action],
      );
      if (override.rows.length > 0) return this._mapRow(override.rows[0]!);
    }
    // 2. Fall back to system default
    const system = await this.pool.query<Record<string, unknown>>(
      `SELECT * FROM prompts WHERE tenant_id IS NULL AND role = $1 AND action = $2`,
      [role, action],
    );
    if (system.rows.length > 0) return this._mapRow(system.rows[0]!);
    return undefined;
  }

  async upsert(tenantId: string | null, role: string, action: string, input: UpsertPromptInput, _userId?: string): Promise<PromptTemplate> {
    const variables = input.variables ?? extractVariables(input.template);
    const variablesJson = JSON.stringify(variables);

    // Use different ON CONFLICT targets based on whether tenant_id is NULL
    let result: pg.QueryResult<Record<string, unknown>>;
    if (tenantId === null) {
      result = await this.pool.query<Record<string, unknown>>(`
        INSERT INTO prompts (tenant_id, role, action, template, system_prompt, variables, enable_tools, max_tokens, version, created_by, updated_by)
        VALUES (NULL, $1, $2, $3, COALESCE($4, ''), $5::jsonb, COALESCE($6, false), COALESCE($7, 4096), 1, $8, $8)
        ON CONFLICT (role, action) WHERE tenant_id IS NULL
        DO UPDATE SET
          template = $3,
          system_prompt = COALESCE($4, prompts.system_prompt),
          variables = $5::jsonb,
          enable_tools = COALESCE($6, prompts.enable_tools),
          max_tokens = COALESCE($7, prompts.max_tokens),
          version = prompts.version + 1,
          updated_at = NOW(),
          updated_by = $8
        RETURNING *
      `, [role, action, input.template, input.systemPrompt, variablesJson, input.enableTools, input.maxTokens, _userId ?? null]);
    } else {
      result = await this.pool.query<Record<string, unknown>>(`
        INSERT INTO prompts (tenant_id, role, action, template, system_prompt, variables, enable_tools, max_tokens, version, created_by, updated_by)
        VALUES ($1, $2, $3, $4, COALESCE($5, ''), $6::jsonb, COALESCE($7, false), COALESCE($8, 4096), 1, $9, $9)
        ON CONFLICT (tenant_id, role, action) WHERE tenant_id IS NOT NULL
        DO UPDATE SET
          template = $4,
          system_prompt = COALESCE($5, prompts.system_prompt),
          variables = $6::jsonb,
          enable_tools = COALESCE($7, prompts.enable_tools),
          max_tokens = COALESCE($8, prompts.max_tokens),
          version = prompts.version + 1,
          updated_at = NOW(),
          updated_by = $9
        RETURNING *
      `, [tenantId, role, action, input.template, input.systemPrompt, variablesJson, input.enableTools, input.maxTokens, _userId ?? null]);
    }

    return this._mapRow(result.rows[0]!);
  }

  async delete(tenantId: string, role: string, action: string, _userId?: string): Promise<boolean> {
    const result = await this.pool.query(
      `DELETE FROM prompts WHERE tenant_id = $1 AND role = $2 AND action = $3`,
      [tenantId, role, action],
    );
    return (result.rowCount ?? 0) > 0;
  }

  async list(tenantId: string | null): Promise<PromptSummary[]> {
    let result: pg.QueryResult<Record<string, unknown>>;
    if (tenantId !== null) {
      // Merged view: tenant overrides take precedence over system defaults
      result = await this.pool.query<Record<string, unknown>>(`
        SELECT DISTINCT ON (role, action) *
        FROM prompts
        WHERE tenant_id IS NULL OR tenant_id = $1
        ORDER BY role, action,
          CASE WHEN tenant_id IS NOT NULL THEN 0 ELSE 1 END
      `, [tenantId]);
    } else {
      result = await this.pool.query<Record<string, unknown>>(`
        SELECT * FROM prompts WHERE tenant_id IS NULL ORDER BY role, action
      `);
    }

    return result.rows.map((row: Record<string, unknown>) => ({
      role: String(row['role']),
      action: String(row['action']),
      version: Number(row['version']),
      enableTools: Boolean(row['enable_tools']),
      maxTokens: Number(row['max_tokens']),
      variableCount: Array.isArray(row['variables']) ? (row['variables'] as unknown[]).length : 0,
      updatedAt: String(row['updated_at']),
      source: (row['tenant_id'] !== null ? 'override' : 'system') as 'system' | 'override',
      tenantId: row['tenant_id'] !== null ? String(row['tenant_id']) : null,
    }));
  }

  async render(tenantId: string | null, role: string, action: string, input: RenderInput): Promise<RenderedPrompt | undefined> {
    const template = await this.get(tenantId, role, action);
    if (template === undefined) return undefined;

    const unresolvedVariables: string[] = [];
    const renderedTemplate = interpolateTemplate(template.template, input.variables, unresolvedVariables, this.logger);
    const renderedSystemPrompt = interpolateTemplate(template.systemPrompt, input.variables, unresolvedVariables, this.logger);

    return {
      role: template.role, action: template.action, version: template.version,
      renderedTemplate, renderedSystemPrompt,
      enableTools: template.enableTools, maxTokens: template.maxTokens,
      unresolvedVariables: [...new Set(unresolvedVariables)],
    };
  }

  // --- System default operations ---
  async getSystemDefault(role: string, action: string): Promise<PromptTemplate | undefined> {
    return this.get(null, role, action);
  }

  async upsertSystemDefault(role: string, action: string, input: UpsertPromptInput, userId?: string): Promise<PromptTemplate> {
    return this.upsert(null, role, action, input, userId);
  }

  async resetSystemDefault(role: string, action: string, userId?: string): Promise<PromptTemplate | undefined> {
    const defaults = getDefaultPrompts();
    const found = defaults.find((d) => d.role === role && d.action === action);
    if (found === undefined) return undefined;
    return this.upsert(null, role, action, {
      template: found.template,
      variables: found.variables,
      systemPrompt: found.systemPrompt,
      enableTools: found.enableTools,
      maxTokens: found.maxTokens,
    }, userId);
  }

  async listSystemDefaults(): Promise<PromptSummary[]> {
    return this.list(null);
  }

  // --- System prompts (role preambles) ---
  async getSystemPrompt(tenantId: string | null, role: string): Promise<string | undefined> {
    if (tenantId !== null) {
      const override = await this.pool.query<Record<string, unknown>>(
        `SELECT prompt FROM system_prompts WHERE tenant_id = $1 AND role = $2`, [tenantId, role]);
      if (override.rows.length > 0) return String(override.rows[0]!['prompt']);
    }
    const system = await this.pool.query<Record<string, unknown>>(
      `SELECT prompt FROM system_prompts WHERE tenant_id IS NULL AND role = $1`, [role]);
    if (system.rows.length > 0) return String(system.rows[0]!['prompt']);
    return undefined;
  }

  async upsertSystemPrompt(tenantId: string | null, role: string, prompt: string, userId?: string): Promise<void> {
    if (tenantId === null) {
      await this.pool.query(`
        INSERT INTO system_prompts (tenant_id, role, prompt, created_by, updated_by)
        VALUES (NULL, $1, $2, $3, $3)
        ON CONFLICT (role) WHERE tenant_id IS NULL
        DO UPDATE SET prompt = $2, version = system_prompts.version + 1, updated_at = NOW(), updated_by = $3
      `, [role, prompt, userId ?? null]);
    } else {
      await this.pool.query(`
        INSERT INTO system_prompts (tenant_id, role, prompt, created_by, updated_by)
        VALUES ($1, $2, $3, $4, $4)
        ON CONFLICT (tenant_id, role) WHERE tenant_id IS NOT NULL
        DO UPDATE SET prompt = $3, version = system_prompts.version + 1, updated_at = NOW(), updated_by = $4
      `, [tenantId, role, prompt, userId ?? null]);
    }
  }

  // --- Private ---
  private _mapRow(row: Record<string, unknown>): PromptTemplate {
    return {
      role: String(row['role']),
      action: String(row['action']),
      version: Number(row['version']),
      template: String(row['template']),
      variables: Array.isArray(row['variables']) ? row['variables'] as string[] : [],
      systemPrompt: String(row['system_prompt'] ?? ''),
      enableTools: Boolean(row['enable_tools']),
      maxTokens: Number(row['max_tokens']),
      createdAt: String(row['created_at']),
      updatedAt: String(row['updated_at']),
    };
  }
}
```

---

### Task 5: Wire Up in Application (1 hour)

**File to modify**: `packages/api/src/index.ts` (or wherever `PromptStore` is instantiated)

Replace `new PromptStore(options)` with `new PgPromptStore(pool, logger)` when a database connection is available, falling back to the file-based `PromptStore` for CLI mode.

```typescript
// Conditional wiring
let promptStore: IPromptStore;
if (pool) {
  promptStore = new PgPromptStore(pool, logger);
} else {
  promptStore = new PromptStore({ filePath: './data/prompts.json', logger });
  // Note: PromptStore needs to implement IPromptStore adapter
}
```

---

### Task 6: Unit Tests (2 hours)

**File to modify**: `packages/api/src/services/prompt-store.test.ts`

Replace/augment existing tests to cover `InMemoryPromptStore` with tenant awareness.

Key test cases (from story acceptance criteria):

| # | Test | Assertion |
|---|------|-----------|
| 1 | `get(null, role, action)` returns system default | Template matches seeded default |
| 2 | `get(tenantId, role, action)` returns tenant override | Override template returned |
| 3 | `get(tenantId, role, action)` falls back to system default | When no override exists, default returned |
| 4 | `upsert(tenantId, role, action, input)` creates new override | Version = 1, role/action match |
| 5 | `upsert(tenantId, role, action, input)` bumps version | Version increments |
| 6 | `delete(tenantId, role, action)` removes override | Subsequent get returns system default |
| 7 | `delete(tenantId, role, action)` returns false for missing | No error, returns false |
| 8 | `list(null)` returns all system defaults | All 80 defaults if seeded |
| 9 | `list(tenantId)` returns merged view | Override wins, defaults fill gaps |
| 10 | `render()` interpolates variables | Correct substitution |
| 11 | `render()` tracks unresolved variables | Listed in result |
| 12 | `render()` truncates at 1 MB | Output capped |
| 13 | Template injection safety | `{{secret}}` in value not re-expanded |
| 14 | `resetSystemDefault()` restores hardcoded | Matches `getDefaultPrompts()` |
| 15 | `getSystemPrompt(null, role)` returns role preamble | Correct string |
| 16 | `getSystemPrompt(tenantId, role)` returns override | Account override wins |

---

### Task 7: Integration Tests (2 hours)

**File to create**: `packages/api/src/services/pg-prompt-store.test.ts`

Requires a test PostgreSQL database (set via `DATABASE_URL_TEST`).

| # | Test | Assertion |
|---|------|-----------|
| 17 | Full CRUD against Postgres | Create, read, update, delete cycle |
| 18 | `list(tenantId)` merged results | Override + defaults correctly merged |
| 19 | Concurrent `upsert()` calls | No duplicates (atomic UPSERT) |
| 20 | `delete()` on non-existent row | Returns false, no error |
| 21 | `render()` end-to-end | Postgres-backed resolution + interpolation |

---

## Files to Create

| # | File Path | Purpose |
|---|-----------|---------|
| 1 | `packages/api/src/services/prompt-interpolation.ts` | Shared interpolation utilities |
| 2 | `packages/api/src/services/in-memory-prompt-store.ts` | In-memory IPromptStore for testing |
| 3 | `packages/api/src/services/pg-prompt-store.ts` | PostgreSQL-backed IPromptStore |
| 4 | `packages/api/src/services/pg-prompt-store.test.ts` | Integration tests |

## Files to Modify

| # | File Path | Change |
|---|-----------|--------|
| 1 | `packages/api/src/services/prompt-store.ts` | Export `IPromptStore` interface; extend `PromptSummary` with `source` and `tenantId` |
| 2 | `packages/api/src/services/prompt-store.test.ts` | Update/augment tests for `InMemoryPromptStore` |
| 3 | `packages/api/src/index.ts` (or equivalent) | Wire `PgPromptStore` when pool available |

---

## Dependencies

- **Story 27-1** (Database Schema) — tables must exist for `PgPromptStore`
- **Internal**: `packages/api/src/services/default-prompts.ts` (for `getDefaultPrompts()`)
- **Internal**: `pg` package (already a dependency)

---

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| `DISTINCT ON` is PostgreSQL-specific | Tamma targets PostgreSQL exclusively; document this in code comments |
| ON CONFLICT with partial indexes may need specific syntax | Use `ON CONFLICT (role, action) WHERE tenant_id IS NULL` — verified this works with PostgreSQL partial unique indexes |
| Backward compatibility: callers using `store.get(role, action)` without tenantId | The existing `PromptStore` class is kept for CLI mode; route handlers updated in Story 27-3 to pass tenantId |
| Large template text in `_mapRow` could consume memory | Template text is bounded at 500KB by validation in the API layer (Story 27-3) |

---

## Estimated Effort

| Task | Hours |
|------|-------|
| IPromptStore interface definition | 1 |
| interpolateTemplate utility extraction | 1 |
| InMemoryPromptStore implementation | 3 |
| PgPromptStore implementation | 4 |
| Application wiring | 1 |
| Unit tests (16 tests) | 2 |
| Integration tests (5 tests) | 2 |
| **Total** | **14 hours** |
