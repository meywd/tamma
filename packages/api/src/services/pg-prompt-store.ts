/**
 * PostgreSQL-backed Prompt Store
 *
 * Multi-tenant PostgreSQL implementation of IPromptStore.
 * Reads/writes from the `prompts`, `system_prompts`, and `action_prompts` tables
 * created in migration 012.
 *
 * Resolution order for get(tenantId, role, action):
 *   1. Tenant override (tenant_id = tenantId) if tenantId is not null
 *   2. System default (tenant_id IS NULL)
 *   3. undefined
 *
 * Story 27-2: Prompt Store Service
 * Story 27-7: Prompt Store Event Sourcing
 */

import type pg from 'pg';

import type { PromptTemplate } from './default-prompts.js';
import { getDefaultPrompts, SYSTEM_PROMPTS } from './default-prompts.js';
import type {
  IPromptStore,
  UpsertPromptInput,
  RenderInput,
  PromptSummary,
  RenderedPrompt,
} from './prompt-store.js';
import {
  extractVariables,
  interpolateTemplate,
  validateKey,
} from './prompt-store.js';
import type { IPromptEventStore } from './prompt-store-events.js';
import {
  PROMPT_EVENT_TYPES,
  diffFields,
  emitPromptEvent,
} from './prompt-store-events.js';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

interface LoggerLike {
  info: (obj: object, msg: string) => void;
  warn: (obj: object, msg: string) => void;
  error: (obj: object, msg: string) => void;
}

// ---------------------------------------------------------------------------
// PgPromptStore
// ---------------------------------------------------------------------------

export class PgPromptStore implements IPromptStore {
  constructor(
    private readonly pool: pg.Pool,
    private readonly logger?: LoggerLike,
    private readonly eventStore?: IPromptEventStore,
  ) {}

  // -----------------------------------------------------------------------
  // IPromptStore — Tenant-scoped operations
  // -----------------------------------------------------------------------

  async get(tenantId: string | null, role: string, action: string): Promise<PromptTemplate | undefined> {
    // 1. Try tenant override
    if (tenantId !== null) {
      const override = await this.pool.query<Record<string, unknown>>(
        'SELECT * FROM prompts WHERE tenant_id = $1 AND role = $2 AND action = $3',
        [tenantId, role, action],
      );
      if (override.rows.length > 0) {
        return this._mapRow(override.rows[0]!);
      }
    }

    // 2. Fall back to system default
    const systemDefault = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM prompts WHERE tenant_id IS NULL AND role = $1 AND action = $2',
      [role, action],
    );
    if (systemDefault.rows.length > 0) {
      return this._mapRow(systemDefault.rows[0]!);
    }

    return undefined;
  }

  async upsert(tenantId: string | null, role: string, action: string, input: UpsertPromptInput, userId?: string): Promise<PromptTemplate> {
    validateKey(role, action);

    // Fetch existing row before mutation (for event diffing)
    const existing = await this._getExact(tenantId, role, action);

    const variables = input.variables ?? extractVariables(input.template);
    const variablesJson = JSON.stringify(variables);

    let result: PromptTemplate;

    if (tenantId === null) {
      // System default: use partial index for tenant_id IS NULL
      const queryResult = await this.pool.query<Record<string, unknown>>(
        `INSERT INTO prompts (tenant_id, role, action, template, system_prompt, variables, enable_tools, max_tokens, version)
         VALUES (NULL, $1, $2, $3, $4, $5::jsonb, $6, $7, 1)
         ON CONFLICT (role, action) WHERE tenant_id IS NULL
         DO UPDATE SET
           template = EXCLUDED.template,
           system_prompt = COALESCE(NULLIF(EXCLUDED.system_prompt, ''), prompts.system_prompt),
           variables = EXCLUDED.variables,
           enable_tools = EXCLUDED.enable_tools,
           max_tokens = EXCLUDED.max_tokens,
           version = prompts.version + 1,
           updated_at = NOW()
         RETURNING *`,
        [
          role,
          action,
          input.template,
          input.systemPrompt ?? '',
          variablesJson,
          input.enableTools ?? false,
          input.maxTokens ?? 4096,
        ],
      );
      result = this._mapRow(queryResult.rows[0]!);
    } else {
      // Tenant override: use partial index for tenant_id IS NOT NULL
      const queryResult = await this.pool.query<Record<string, unknown>>(
        `INSERT INTO prompts (tenant_id, role, action, template, system_prompt, variables, enable_tools, max_tokens, version)
         VALUES ($1, $2, $3, $4, $5, $6::jsonb, $7, $8, 1)
         ON CONFLICT (tenant_id, role, action) WHERE tenant_id IS NOT NULL
         DO UPDATE SET
           template = EXCLUDED.template,
           system_prompt = COALESCE(NULLIF(EXCLUDED.system_prompt, ''), prompts.system_prompt),
           variables = EXCLUDED.variables,
           enable_tools = EXCLUDED.enable_tools,
           max_tokens = EXCLUDED.max_tokens,
           version = prompts.version + 1,
           updated_at = NOW()
         RETURNING *`,
        [
          tenantId,
          role,
          action,
          input.template,
          input.systemPrompt ?? '',
          variablesJson,
          input.enableTools ?? false,
          input.maxTokens ?? 4096,
        ],
      );
      result = this._mapRow(queryResult.rows[0]!);
    }

    // Emit DCB event (best-effort)
    if (this.eventStore !== undefined) {
      const eventType = existing !== undefined
        ? PROMPT_EVENT_TYPES.UPDATED
        : PROMPT_EVENT_TYPES.CREATED;

      const eventData: Record<string, unknown> = existing !== undefined
        ? {
            previousVersion: existing.version,
            newVersion: result.version,
            changedFields: diffFields(existing, result),
          }
        : {
            version: result.version,
            enableTools: result.enableTools,
            maxTokens: result.maxTokens,
          };

      await emitPromptEvent(
        this.eventStore,
        eventType,
        {
          tenantId: tenantId ?? undefined,
          role,
          action,
          userId,
        },
        eventData,
        this.logger,
      );
    }

    return result;
  }

  async delete(tenantId: string, role: string, action: string, userId?: string): Promise<boolean> {
    // Fetch existing for version info before deletion
    const existing = await this._getExact(tenantId, role, action);

    const result = await this.pool.query(
      'DELETE FROM prompts WHERE tenant_id = $1 AND role = $2 AND action = $3',
      [tenantId, role, action],
    );
    const deleted = (result.rowCount ?? 0) > 0;

    // Emit DCB event (best-effort)
    if (deleted && this.eventStore !== undefined && existing !== undefined) {
      await emitPromptEvent(
        this.eventStore,
        PROMPT_EVENT_TYPES.DELETED,
        {
          tenantId,
          role,
          action,
          userId,
        },
        {
          deletedVersion: existing.version,
        },
        this.logger,
      );
    }

    return deleted;
  }

  async list(tenantId: string | null): Promise<PromptSummary[]> {
    let result: pg.QueryResult<Record<string, unknown>>;

    if (tenantId === null) {
      // List system defaults only
      result = await this.pool.query<Record<string, unknown>>(
        `SELECT *, false AS is_override FROM prompts
         WHERE tenant_id IS NULL
         ORDER BY role, action`,
      );
    } else {
      // Merged view: tenant overrides take precedence over system defaults
      result = await this.pool.query<Record<string, unknown>>(
        `SELECT DISTINCT ON (role, action)
           *,
           CASE WHEN tenant_id IS NOT NULL THEN true ELSE false END AS is_override
         FROM prompts
         WHERE tenant_id IS NULL OR tenant_id = $1
         ORDER BY role, action,
           CASE WHEN tenant_id IS NOT NULL THEN 0 ELSE 1 END`,
        [tenantId],
      );
    }

    return result.rows.map((row) => this._mapSummary(row));
  }

  async render(tenantId: string | null, role: string, action: string, input: RenderInput): Promise<RenderedPrompt | undefined> {
    const template = await this.get(tenantId, role, action);
    if (template === undefined) return undefined;

    const unresolvedVariables: string[] = [];

    const renderedTemplate = interpolateTemplate(template.template, input.variables, unresolvedVariables, this.logger);
    const renderedSystemPrompt = interpolateTemplate(template.systemPrompt, input.variables, unresolvedVariables, this.logger);

    return {
      role: template.role,
      action: template.action,
      version: template.version,
      renderedTemplate,
      renderedSystemPrompt,
      enableTools: template.enableTools,
      maxTokens: template.maxTokens,
      unresolvedVariables: [...new Set(unresolvedVariables)],
    };
  }

  // -----------------------------------------------------------------------
  // IPromptStore — System default operations
  // -----------------------------------------------------------------------

  async getSystemDefault(role: string, action: string): Promise<PromptTemplate | undefined> {
    const result = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM prompts WHERE tenant_id IS NULL AND role = $1 AND action = $2',
      [role, action],
    );
    if (result.rows.length === 0) return undefined;
    return this._mapRow(result.rows[0]!);
  }

  async upsertSystemDefault(role: string, action: string, input: UpsertPromptInput, userId?: string): Promise<PromptTemplate> {
    return this.upsert(null, role, action, input, userId);
  }

  async resetSystemDefault(role: string, action: string, userId?: string): Promise<PromptTemplate | undefined> {
    // Fetch existing before reset (for event diffing)
    const existing = await this._getExact(null, role, action);

    const defaults = getDefaultPrompts();
    const match = defaults.find((d) => d.role === role && d.action === action);
    if (match === undefined) return undefined;

    const result = await this.upsert(null, role, action, {
      template: match.template,
      variables: [...match.variables],
      systemPrompt: match.systemPrompt,
      enableTools: match.enableTools,
      maxTokens: match.maxTokens,
    });

    // Emit RESET event (distinct from UPDATE for audit clarity)
    if (this.eventStore !== undefined) {
      await emitPromptEvent(
        this.eventStore,
        PROMPT_EVENT_TYPES.RESET,
        {
          role,
          action,
          userId,
        },
        {
          previousVersion: existing?.version ?? 0,
          newVersion: result.version,
          resetFrom: 'custom',
          resetTo: 'hardcoded',
        },
        this.logger,
      );
    }

    return result;
  }

  async listSystemDefaults(): Promise<PromptSummary[]> {
    return this.list(null);
  }

  // -----------------------------------------------------------------------
  // IPromptStore — System prompts (role preambles)
  // -----------------------------------------------------------------------

  async getSystemPrompt(tenantId: string | null, role: string): Promise<string | undefined> {
    // 1. Try tenant override
    if (tenantId !== null) {
      const override = await this.pool.query<Record<string, unknown>>(
        'SELECT prompt FROM system_prompts WHERE tenant_id = $1 AND role = $2',
        [tenantId, role],
      );
      if (override.rows.length > 0) {
        return String(override.rows[0]!['prompt']);
      }
    }

    // 2. Fall back to system default
    const systemDefault = await this.pool.query<Record<string, unknown>>(
      'SELECT prompt FROM system_prompts WHERE tenant_id IS NULL AND role = $1',
      [role],
    );
    if (systemDefault.rows.length > 0) {
      return String(systemDefault.rows[0]!['prompt']);
    }

    return undefined;
  }

  async upsertSystemPrompt(tenantId: string | null, role: string, prompt: string): Promise<void> {
    if (tenantId === null) {
      await this.pool.query(
        `INSERT INTO system_prompts (tenant_id, role, prompt, version)
         VALUES (NULL, $1, $2, 1)
         ON CONFLICT (role) WHERE tenant_id IS NULL
         DO UPDATE SET
           prompt = EXCLUDED.prompt,
           version = system_prompts.version + 1,
           updated_at = NOW()`,
        [role, prompt],
      );
    } else {
      await this.pool.query(
        `INSERT INTO system_prompts (tenant_id, role, prompt, version)
         VALUES ($1, $2, $3, 1)
         ON CONFLICT (tenant_id, role) WHERE tenant_id IS NOT NULL
         DO UPDATE SET
           prompt = EXCLUDED.prompt,
           version = system_prompts.version + 1,
           updated_at = NOW()`,
        [tenantId, role, prompt],
      );
    }
  }

  // -----------------------------------------------------------------------
  // Seed defaults (called at application startup)
  // -----------------------------------------------------------------------

  /**
   * Seed 80 role+action system default templates into the prompts table.
   * Uses ON CONFLICT DO NOTHING for idempotency.
   * Does NOT emit events (seed operations are not user-initiated).
   */
  async seedDefaults(): Promise<number> {
    const defaults = getDefaultPrompts();
    let seeded = 0;

    for (const d of defaults) {
      const result = await this.pool.query(
        `INSERT INTO prompts (tenant_id, role, action, template, system_prompt, variables, enable_tools, max_tokens, version)
         VALUES (NULL, $1, $2, $3, $4, $5::jsonb, $6, $7, 1)
         ON CONFLICT (role, action) WHERE tenant_id IS NULL
         DO NOTHING`,
        [
          d.role,
          d.action,
          d.template,
          d.systemPrompt,
          JSON.stringify(d.variables),
          d.enableTools,
          d.maxTokens,
        ],
      );
      if ((result.rowCount ?? 0) > 0) {
        seeded++;
      }
    }

    // Seed system prompts
    for (const [role, prompt] of Object.entries(SYSTEM_PROMPTS)) {
      await this.pool.query(
        `INSERT INTO system_prompts (tenant_id, role, prompt, version)
         VALUES (NULL, $1, $2, 1)
         ON CONFLICT (role) WHERE tenant_id IS NULL
         DO NOTHING`,
        [role, prompt],
      );
    }

    if (seeded > 0) {
      this.logger?.info(
        { seeded, total: defaults.length },
        'Seeded default prompt templates',
      );
    }

    return seeded;
  }

  // -----------------------------------------------------------------------
  // Private Helpers
  // -----------------------------------------------------------------------

  /**
   * Get the exact row for a tenantId+role+action (no fallback).
   * Used for event diffing before mutations.
   */
  private async _getExact(tenantId: string | null, role: string, action: string): Promise<PromptTemplate | undefined> {
    const result = tenantId === null
      ? await this.pool.query<Record<string, unknown>>(
          'SELECT * FROM prompts WHERE tenant_id IS NULL AND role = $1 AND action = $2',
          [role, action],
        )
      : await this.pool.query<Record<string, unknown>>(
          'SELECT * FROM prompts WHERE tenant_id = $1 AND role = $2 AND action = $3',
          [tenantId, role, action],
        );

    if (result.rows.length === 0) return undefined;
    return this._mapRow(result.rows[0]!);
  }

  private _mapRow(row: Record<string, unknown>): PromptTemplate {
    const variables = row['variables'];
    let parsedVars: string[];
    if (Array.isArray(variables)) {
      parsedVars = variables as string[];
    } else if (typeof variables === 'string') {
      parsedVars = JSON.parse(variables) as string[];
    } else {
      parsedVars = [];
    }

    return {
      role: String(row['role']),
      action: String(row['action']),
      version: Number(row['version']),
      template: String(row['template']),
      variables: parsedVars,
      systemPrompt: String(row['system_prompt'] ?? ''),
      enableTools: Boolean(row['enable_tools']),
      maxTokens: Number(row['max_tokens']),
      createdAt: String(row['created_at']),
      updatedAt: String(row['updated_at']),
    };
  }

  private _mapSummary(row: Record<string, unknown>): PromptSummary {
    const variables = row['variables'];
    let varCount: number;
    if (Array.isArray(variables)) {
      varCount = variables.length;
    } else if (typeof variables === 'string') {
      const parsed = JSON.parse(variables) as string[];
      varCount = parsed.length;
    } else {
      varCount = 0;
    }

    return {
      role: String(row['role']),
      action: String(row['action']),
      version: Number(row['version']),
      enableTools: Boolean(row['enable_tools']),
      maxTokens: Number(row['max_tokens']),
      variableCount: varCount,
      updatedAt: String(row['updated_at']),
      isOverride: Boolean(row['is_override']),
    };
  }
}
