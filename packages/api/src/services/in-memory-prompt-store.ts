/**
 * In-Memory Prompt Store
 *
 * Multi-tenant in-memory implementation of IPromptStore.
 * Used for unit testing and as a fallback when no database is available.
 *
 * Templates are stored in Maps keyed by "tenantId:role:action"
 * (with "null" for system defaults).
 *
 * Story 27-2: Prompt Store Service
 */

import type { PromptTemplate } from './default-prompts.js';
import { getDefaultPrompts, SYSTEM_PROMPTS, VALID_ROLES, VALID_ACTIONS } from './default-prompts.js';
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
  cloneTemplate,
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

type PromptMapKey = string;

interface SystemPromptEntry {
  prompt: string;
  version: number;
}

interface LoggerLike {
  info: (obj: object, msg: string) => void;
  warn: (obj: object, msg: string) => void;
  error: (obj: object, msg: string) => void;
}

// ---------------------------------------------------------------------------
// InMemoryPromptStore
// ---------------------------------------------------------------------------

export class InMemoryPromptStore implements IPromptStore {
  private readonly templates = new Map<PromptMapKey, PromptTemplate>();
  private readonly systemPrompts = new Map<PromptMapKey, SystemPromptEntry>();
  private readonly logger?: LoggerLike;
  private readonly skipDefaults: boolean;
  private readonly eventStore?: IPromptEventStore;
  private initialized = false;

  constructor(options?: { logger?: LoggerLike; skipDefaults?: boolean; eventStore?: IPromptEventStore }) {
    if (options?.logger !== undefined) {
      this.logger = options.logger;
    }
    this.skipDefaults = options?.skipDefaults ?? false;
    if (options?.eventStore !== undefined) {
      this.eventStore = options.eventStore;
    }
  }

  // -----------------------------------------------------------------------
  // Initialization
  // -----------------------------------------------------------------------

  async initialize(): Promise<void> {
    if (this.initialized) return;

    if (!this.skipDefaults) {
      this._seedDefaults();
    }

    this.initialized = true;
  }

  // -----------------------------------------------------------------------
  // Key helpers
  // -----------------------------------------------------------------------

  private _templateKey(tenantId: string | null, role: string, action: string): PromptMapKey {
    return `${String(tenantId)}:${role}:${action}`;
  }

  private _systemPromptKey(tenantId: string | null, role: string): PromptMapKey {
    return `${String(tenantId)}:${role}`;
  }

  // -----------------------------------------------------------------------
  // IPromptStore — Tenant-scoped operations
  // -----------------------------------------------------------------------

  async get(tenantId: string | null, role: string, action: string): Promise<PromptTemplate | undefined> {
    await this.initialize();

    // 1. Try tenant override
    if (tenantId !== null) {
      const override = this.templates.get(this._templateKey(tenantId, role, action));
      if (override !== undefined) {
        return cloneTemplate(override);
      }
    }

    // 2. Fall back to system default
    const systemDefault = this.templates.get(this._templateKey(null, role, action));
    return cloneTemplate(systemDefault);
  }

  async upsert(tenantId: string | null, role: string, action: string, input: UpsertPromptInput, userId?: string): Promise<PromptTemplate> {
    await this.initialize();
    validateKey(role, action);

    const key = this._templateKey(tenantId, role, action);
    const existing = this.templates.get(key);
    const ts = new Date().toISOString();

    const template: PromptTemplate = {
      role,
      action,
      version: existing !== undefined ? existing.version + 1 : 1,
      template: input.template,
      variables: input.variables ?? extractVariables(input.template),
      systemPrompt: input.systemPrompt ?? existing?.systemPrompt ?? '',
      enableTools: input.enableTools ?? existing?.enableTools ?? false,
      maxTokens: input.maxTokens ?? existing?.maxTokens ?? 4096,
      createdAt: existing?.createdAt ?? ts,
      updatedAt: ts,
    };

    this.templates.set(key, template);

    this.logger?.info(
      { tenantId, role, action, version: template.version },
      'Prompt template upserted',
    );

    // Emit DCB event (best-effort)
    if (this.eventStore !== undefined) {
      const eventType = existing !== undefined
        ? PROMPT_EVENT_TYPES.UPDATED
        : PROMPT_EVENT_TYPES.CREATED;

      const eventData: Record<string, unknown> = existing !== undefined
        ? {
            previousVersion: existing.version,
            newVersion: template.version,
            changedFields: diffFields(existing, template),
          }
        : {
            version: template.version,
            enableTools: template.enableTools,
            maxTokens: template.maxTokens,
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

    return cloneTemplate(template)!;
  }

  async delete(tenantId: string, role: string, action: string, userId?: string): Promise<boolean> {
    await this.initialize();
    const key = this._templateKey(tenantId, role, action);
    const existing = this.templates.get(key);
    const deleted = this.templates.delete(key);

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
    await this.initialize();

    // Build merged view: system defaults + tenant overrides
    const merged = new Map<string, { template: PromptTemplate; isOverride: boolean }>();

    // First: add all system defaults
    for (const [key, t] of this.templates.entries()) {
      if (key.startsWith('null:')) {
        const roleAction = `${t.role}:${t.action}`;
        merged.set(roleAction, { template: t, isOverride: false });
      }
    }

    // Then: overlay tenant overrides
    if (tenantId !== null) {
      const prefix = `${tenantId}:`;
      for (const [key, t] of this.templates.entries()) {
        if (key.startsWith(prefix)) {
          const roleAction = `${t.role}:${t.action}`;
          merged.set(roleAction, { template: t, isOverride: true });
        }
      }
    }

    const summaries: PromptSummary[] = [];
    for (const { template: t, isOverride } of merged.values()) {
      summaries.push({
        role: t.role,
        action: t.action,
        version: t.version,
        enableTools: t.enableTools,
        maxTokens: t.maxTokens,
        variableCount: t.variables.length,
        updatedAt: t.updatedAt,
        isOverride,
      });
    }

    summaries.sort((a, b) => {
      const roleCompare = a.role.localeCompare(b.role);
      if (roleCompare !== 0) return roleCompare;
      return a.action.localeCompare(b.action);
    });

    return summaries;
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
    await this.initialize();
    return cloneTemplate(this.templates.get(this._templateKey(null, role, action)));
  }

  async upsertSystemDefault(role: string, action: string, input: UpsertPromptInput, userId?: string): Promise<PromptTemplate> {
    return this.upsert(null, role, action, input, userId);
  }

  async resetSystemDefault(role: string, action: string, userId?: string): Promise<PromptTemplate | undefined> {
    await this.initialize();

    // Fetch existing before reset (for event data)
    const existing = this.templates.get(this._templateKey(null, role, action));

    const defaults = getDefaultPrompts();
    const match = defaults.find((d) => d.role === role && d.action === action);
    if (match === undefined) return undefined;

    const key = this._templateKey(null, role, action);
    // Reset to the hardcoded default (force version 1)
    const reset: PromptTemplate = { ...match, variables: [...match.variables] };
    this.templates.set(key, reset);

    // Emit RESET event (best-effort)
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
          newVersion: reset.version,
          resetFrom: 'custom',
          resetTo: 'hardcoded',
        },
        this.logger,
      );
    }

    return cloneTemplate(reset);
  }

  async listSystemDefaults(): Promise<PromptSummary[]> {
    return this.list(null);
  }

  // -----------------------------------------------------------------------
  // IPromptStore — System prompts (role preambles)
  // -----------------------------------------------------------------------

  async getSystemPrompt(tenantId: string | null, role: string): Promise<string | undefined> {
    await this.initialize();

    // 1. Try tenant override
    if (tenantId !== null) {
      const override = this.systemPrompts.get(this._systemPromptKey(tenantId, role));
      if (override !== undefined) {
        return override.prompt;
      }
    }

    // 2. Fall back to system default
    const systemDefault = this.systemPrompts.get(this._systemPromptKey(null, role));
    if (systemDefault !== undefined) {
      return systemDefault.prompt;
    }

    return undefined;
  }

  async upsertSystemPrompt(tenantId: string | null, role: string, prompt: string): Promise<void> {
    await this.initialize();

    const key = this._systemPromptKey(tenantId, role);
    const existing = this.systemPrompts.get(key);
    this.systemPrompts.set(key, {
      prompt,
      version: existing !== undefined ? existing.version + 1 : 1,
    });
  }

  // -----------------------------------------------------------------------
  // Static validation helpers (backward compat with PromptStore)
  // -----------------------------------------------------------------------

  static isValidRole(role: string): boolean {
    return (VALID_ROLES as readonly string[]).includes(role);
  }

  static isValidAction(action: string): boolean {
    return (VALID_ACTIONS as readonly string[]).includes(action);
  }

  // -----------------------------------------------------------------------
  // Private Helpers
  // -----------------------------------------------------------------------

  private _seedDefaults(): void {
    // Seed 80 role+action templates
    const defaults = getDefaultPrompts();
    let seeded = 0;
    for (const d of defaults) {
      const key = this._templateKey(null, d.role, d.action);
      if (!this.templates.has(key)) {
        this.templates.set(key, { ...d, variables: [...d.variables] });
        seeded++;
      }
    }

    // Seed 8 system prompts
    for (const [role, prompt] of Object.entries(SYSTEM_PROMPTS)) {
      const key = this._systemPromptKey(null, role);
      if (!this.systemPrompts.has(key)) {
        this.systemPrompts.set(key, { prompt, version: 1 });
      }
    }

    if (seeded > 0) {
      this.logger?.info(
        { seeded, total: this.templates.size },
        'Seeded default prompt templates',
      );
    }
  }
}
