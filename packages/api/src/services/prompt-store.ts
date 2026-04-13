/**
 * Prompt Store
 *
 * Interface and in-memory implementation for prompt template management.
 * Templates are keyed by (role, action) and support CRUD operations,
 * version tracking, and {{variable}} interpolation.
 *
 * Supports multi-tenant resolution: tenant overrides fall back to
 * system defaults (tenant_id IS NULL).
 *
 * Story 12-5: Prompt Engineering Framework
 * Story 27-1: Prompt Store Database Schema
 * Story 27-2: Prompt Store Service
 */

import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { dirname } from 'node:path';
import type { PromptTemplate } from './default-prompts.js';
import { getDefaultPrompts, VALID_ROLES, VALID_ACTIONS } from './default-prompts.js';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

/** Composite key for a prompt template: "role:action" */
type PromptKey = `${string}:${string}`;

/** Input for creating or updating a prompt template */
export interface UpsertPromptInput {
  template: string;
  variables?: string[];
  systemPrompt?: string;
  enableTools?: boolean;
  maxTokens?: number;
}

/** Variables for rendering a prompt template */
export interface RenderInput {
  variables: Record<string, string>;
}

/** Summary of a registered prompt (for listing) */
export interface PromptSummary {
  role: string;
  action: string;
  version: number;
  enableTools: boolean;
  maxTokens: number;
  variableCount: number;
  updatedAt: string;
  /** Whether this is a tenant override or a system default */
  isOverride?: boolean;
}

/** Result of rendering a prompt template */
export interface RenderedPrompt {
  role: string;
  action: string;
  version: number;
  renderedTemplate: string;
  renderedSystemPrompt: string;
  enableTools: boolean;
  maxTokens: number;
  /** Variable names that were referenced in the template but not provided */
  unresolvedVariables: string[];
}

// ---------------------------------------------------------------------------
// IPromptStore Interface
// ---------------------------------------------------------------------------

/**
 * Multi-tenant prompt store interface.
 *
 * Resolution order for get(tenantId, role, action):
 *   1. Tenant override (tenant_id = tenantId) if tenantId is not null
 *   2. System default (tenant_id IS NULL)
 *   3. undefined
 */
export interface IPromptStore {
  // --- Tenant-scoped operations ---
  get(tenantId: string | null, role: string, action: string): Promise<PromptTemplate | undefined>;
  upsert(tenantId: string | null, role: string, action: string, input: UpsertPromptInput): Promise<PromptTemplate>;
  delete(tenantId: string, role: string, action: string): Promise<boolean>;
  list(tenantId: string | null): Promise<PromptSummary[]>;
  render(tenantId: string | null, role: string, action: string, input: RenderInput): Promise<RenderedPrompt | undefined>;

  // --- System default operations ---
  getSystemDefault(role: string, action: string): Promise<PromptTemplate | undefined>;
  upsertSystemDefault(role: string, action: string, input: UpsertPromptInput): Promise<PromptTemplate>;
  resetSystemDefault(role: string, action: string): Promise<PromptTemplate | undefined>;
  listSystemDefaults(): Promise<PromptSummary[]>;

  // --- System prompts (role preambles) ---
  getSystemPrompt(tenantId: string | null, role: string): Promise<string | undefined>;
  upsertSystemPrompt(tenantId: string | null, role: string, prompt: string): Promise<void>;
}

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** Maximum rendered template length (1 MB). */
export const MAX_TEMPLATE_LENGTH = 1_000_000;

/** Maximum variable value length (100 KB). */
export const MAX_VAR_VALUE_LENGTH = 100_000;

/** Prototype pollution guard. */
const FORBIDDEN_KEYS = new Set(['__proto__', 'constructor', 'prototype']);

// ---------------------------------------------------------------------------
// Shared Utilities
// ---------------------------------------------------------------------------

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
  logger?: { warn: (obj: object, msg: string) => void },
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

/**
 * Validate a role+action key pair.
 * Throws if the key is forbidden, empty, or too long.
 */
export function validateKey(role: string, action: string): void {
  if (FORBIDDEN_KEYS.has(role)) {
    throw new Error(`Forbidden role name: ${role}`);
  }
  if (FORBIDDEN_KEYS.has(action)) {
    throw new Error(`Forbidden action name: ${action}`);
  }
  if (role.length === 0 || role.length > 64) {
    throw new Error(`Role name must be 1-64 characters (got ${role.length})`);
  }
  if (action.length === 0 || action.length > 64) {
    throw new Error(`Action name must be 1-64 characters (got ${action.length})`);
  }
}

/**
 * Clone a template to prevent external mutation.
 */
export function cloneTemplate(t: PromptTemplate | undefined): PromptTemplate | undefined {
  if (t === undefined) return undefined;
  return {
    ...t,
    variables: [...t.variables],
  };
}

// ---------------------------------------------------------------------------
// PromptStore (legacy file-based implementation, kept for backward compat)
// ---------------------------------------------------------------------------

export interface PromptStoreOptions {
  /** Path to the JSON persistence file. Defaults to ./data/prompts.json relative to cwd. */
  filePath?: string;
  /** Logger instance (Pino-compatible). */
  logger?: { info: (obj: object, msg: string) => void; warn: (obj: object, msg: string) => void; error: (obj: object, msg: string) => void };
  /** Skip loading defaults (for testing). */
  skipDefaults?: boolean;
}

export class PromptStore {
  private readonly templates: Map<PromptKey, PromptTemplate> = new Map();
  private readonly filePath: string;
  private readonly logger?: PromptStoreOptions['logger'];
  private readonly skipDefaults: boolean;
  private initialized = false;

  constructor(options: PromptStoreOptions = {}) {
    this.filePath = options.filePath ?? './data/prompts.json';
    if (options.logger !== undefined) {
      this.logger = options.logger;
    }
    this.skipDefaults = options.skipDefaults ?? false;
  }

  // -----------------------------------------------------------------------
  // Initialization
  // -----------------------------------------------------------------------

  /**
   * Ensure the store is initialized. Loads from file, then seeds defaults.
   * Safe to call multiple times (idempotent).
   */
  async initialize(): Promise<void> {
    if (this.initialized) return;

    // 1. Load persisted templates from file
    await this._loadFromFile();

    // 2. Seed missing defaults
    if (!this.skipDefaults) {
      this._seedDefaults();
    }

    this.initialized = true;
    this.logger?.info(
      { templateCount: this.templates.size },
      'Prompt store initialized',
    );
  }

  // -----------------------------------------------------------------------
  // CRUD Operations
  // -----------------------------------------------------------------------

  /**
   * Get a prompt template by role and action.
   * Returns undefined if not found.
   */
  async get(role: string, action: string): Promise<PromptTemplate | undefined> {
    await this.initialize();
    return this._cloneTemplate(this.templates.get(this._key(role, action)));
  }

  /**
   * Create or update a prompt template.
   * Bumps the version number if the template already exists.
   */
  async upsert(role: string, action: string, input: UpsertPromptInput): Promise<PromptTemplate> {
    await this.initialize();
    this._validateKey(role, action);

    const key = this._key(role, action);
    const existing = this.templates.get(key);
    const ts = new Date().toISOString();

    const template: PromptTemplate = {
      role,
      action,
      version: existing !== undefined ? existing.version + 1 : 1,
      template: input.template,
      variables: input.variables ?? this._extractVariables(input.template),
      systemPrompt: input.systemPrompt ?? existing?.systemPrompt ?? '',
      enableTools: input.enableTools ?? existing?.enableTools ?? false,
      maxTokens: input.maxTokens ?? existing?.maxTokens ?? 4096,
      createdAt: existing?.createdAt ?? ts,
      updatedAt: ts,
    };

    this.templates.set(key, template);

    // Persist to file (best-effort, non-blocking)
    this._persistToFile().catch((err) => {
      this.logger?.error(
        { error: String(err) },
        'Failed to persist prompt store to file',
      );
    });

    this.logger?.info(
      { role, action, version: template.version },
      'Prompt template upserted',
    );

    return this._cloneTemplate(template)!;
  }

  /**
   * List all registered prompt templates as summaries.
   */
  async list(): Promise<PromptSummary[]> {
    await this.initialize();

    const summaries: PromptSummary[] = [];
    for (const t of this.templates.values()) {
      summaries.push({
        role: t.role,
        action: t.action,
        version: t.version,
        enableTools: t.enableTools,
        maxTokens: t.maxTokens,
        variableCount: t.variables.length,
        updatedAt: t.updatedAt,
      });
    }

    // Sort by role, then action for consistent ordering
    summaries.sort((a, b) => {
      const roleCompare = a.role.localeCompare(b.role);
      if (roleCompare !== 0) return roleCompare;
      return a.action.localeCompare(b.action);
    });

    return summaries;
  }

  /**
   * Render a prompt template by interpolating {{variable}} placeholders.
   * Returns the rendered template and system prompt.
   */
  async render(role: string, action: string, input: RenderInput): Promise<RenderedPrompt | undefined> {
    await this.initialize();

    const template = this.templates.get(this._key(role, action));
    if (template === undefined) return undefined;

    const unresolvedVariables: string[] = [];

    const renderedTemplate = this._interpolate(template.template, input.variables, unresolvedVariables);
    const renderedSystemPrompt = this._interpolate(template.systemPrompt, input.variables, unresolvedVariables);

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
  // Private Helpers
  // -----------------------------------------------------------------------

  private _key(role: string, action: string): PromptKey {
    return `${role}:${action}`;
  }

  private _validateKey(role: string, action: string): void {
    if (FORBIDDEN_KEYS.has(role)) {
      throw new Error(`Forbidden role name: ${role}`);
    }
    if (FORBIDDEN_KEYS.has(action)) {
      throw new Error(`Forbidden action name: ${action}`);
    }
    if (role.length === 0 || role.length > 64) {
      throw new Error(`Role name must be 1-64 characters (got ${role.length})`);
    }
    if (action.length === 0 || action.length > 64) {
      throw new Error(`Action name must be 1-64 characters (got ${action.length})`);
    }
  }

  /**
   * Extract {{variable}} names from a template string.
   */
  private _extractVariables(template: string): string[] {
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
  private _interpolate(
    template: string,
    vars: Record<string, string>,
    unresolvedTracker: string[],
  ): string {
    let result = template.replace(/\{\{([^}]{1,64})\}\}/g, (_match, key: string) => {
      const value = vars[key];
      if (value === undefined) {
        unresolvedTracker.push(key);
        return `{{${key}}}`;
      }
      if (value.length > MAX_VAR_VALUE_LENGTH) {
        this.logger?.warn(
          { key, valueLength: value.length, limit: MAX_VAR_VALUE_LENGTH },
          'Variable value exceeds maximum length, leaving unresolved',
        );
        unresolvedTracker.push(key);
        return `{{${key}}}`;
      }
      return value;
    });

    if (result.length > MAX_TEMPLATE_LENGTH) {
      this.logger?.warn(
        { length: result.length, limit: MAX_TEMPLATE_LENGTH },
        'Rendered template exceeds maximum length, truncating',
      );
      result = result.slice(0, MAX_TEMPLATE_LENGTH);
    }

    return result;
  }

  /**
   * Clone a template to prevent external mutation.
   */
  private _cloneTemplate(t: PromptTemplate | undefined): PromptTemplate | undefined {
    if (t === undefined) return undefined;
    return {
      ...t,
      variables: [...t.variables],
    };
  }

  /**
   * Seed missing default prompts into the store.
   * Only adds templates that don't already exist (preserving user customizations).
   */
  private _seedDefaults(): void {
    const defaults = getDefaultPrompts();
    let seeded = 0;

    for (const d of defaults) {
      const key = this._key(d.role, d.action);
      if (!this.templates.has(key)) {
        this.templates.set(key, { ...d, variables: [...d.variables] });
        seeded++;
      }
    }

    if (seeded > 0) {
      this.logger?.info(
        { seeded, total: this.templates.size },
        'Seeded default prompt templates',
      );
    }
  }

  /**
   * Load templates from the JSON persistence file.
   * Silently ignores if file doesn't exist (first run).
   */
  private async _loadFromFile(): Promise<void> {
    try {
      const content = await readFile(this.filePath, 'utf-8');
      const parsed = JSON.parse(content) as PromptTemplate[];

      if (!Array.isArray(parsed)) {
        this.logger?.warn({}, 'Prompt file is not an array, skipping load');
        return;
      }

      for (const t of parsed) {
        if (
          typeof t.role === 'string' &&
          typeof t.action === 'string' &&
          typeof t.template === 'string'
        ) {
          const key = this._key(t.role, t.action);
          this.templates.set(key, {
            role: t.role,
            action: t.action,
            version: typeof t.version === 'number' ? t.version : 1,
            template: t.template,
            variables: Array.isArray(t.variables) ? t.variables : [],
            systemPrompt: typeof t.systemPrompt === 'string' ? t.systemPrompt : '',
            enableTools: typeof t.enableTools === 'boolean' ? t.enableTools : false,
            maxTokens: typeof t.maxTokens === 'number' ? t.maxTokens : 4096,
            createdAt: typeof t.createdAt === 'string' ? t.createdAt : new Date().toISOString(),
            updatedAt: typeof t.updatedAt === 'string' ? t.updatedAt : new Date().toISOString(),
          });
        }
      }

      this.logger?.info(
        { loaded: this.templates.size, filePath: this.filePath },
        'Loaded prompt templates from file',
      );
    } catch (err) {
      const error = err as NodeJS.ErrnoException;
      if (error.code === 'ENOENT') {
        // File doesn't exist — first run, that's fine
        return;
      }
      this.logger?.warn(
        { error: String(err), filePath: this.filePath },
        'Failed to load prompt templates from file',
      );
    }
  }

  /**
   * Persist all templates to the JSON file.
   * Creates the directory if it doesn't exist.
   */
  private async _persistToFile(): Promise<void> {
    const templates = [...this.templates.values()];

    // Sort for stable output
    templates.sort((a, b) => {
      const roleCompare = a.role.localeCompare(b.role);
      if (roleCompare !== 0) return roleCompare;
      return a.action.localeCompare(b.action);
    });

    const dir = dirname(this.filePath);
    await mkdir(dir, { recursive: true });
    await writeFile(this.filePath, JSON.stringify(templates, null, 2), 'utf-8');
  }

  /**
   * Check if a role is in the known default roles set.
   */
  static isValidRole(role: string): boolean {
    return (VALID_ROLES as readonly string[]).includes(role);
  }

  /**
   * Check if an action is in the known default actions set.
   */
  static isValidAction(action: string): boolean {
    return (VALID_ACTIONS as readonly string[]).includes(action);
  }
}
