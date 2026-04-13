/**
 * Agent Configuration Store
 *
 * Manages per-account agent configuration (agents + security) with
 * a resolution chain: account override → system default → hardcoded defaults.
 *
 * The config JSONB column stores both `agents` (IAgentsConfig) and
 * `security` (SecurityConfig) in a single document.
 */

import { randomUUID } from 'node:crypto';

import type { IAgentsConfig, SecurityConfig } from '@tamma/shared';

// ---------------------------------------------------------------------------
// Models
// ---------------------------------------------------------------------------

/** Shape of the JSONB config stored in agent_configs rows. */
export interface AgentConfigDocument {
  agents: IAgentsConfig;
  security: SecurityConfig;
}

/** Full row returned by the store. */
export interface AgentConfigRow {
  id: string;
  accountId: string | null;
  config: AgentConfigDocument;
  version: number;
  createdAt: string;
  updatedAt: string;
  createdBy: string | null;
  updatedBy: string | null;
}

/** Resolved config result with source metadata. */
export interface ResolvedAgentConfig {
  config: AgentConfigDocument;
  source: 'account' | 'system' | 'hardcoded';
  version: number;
}

// ---------------------------------------------------------------------------
// Hardcoded defaults (last-resort fallback)
// ---------------------------------------------------------------------------

export const HARDCODED_AGENT_CONFIG: AgentConfigDocument = Object.freeze({
  agents: {
    defaults: {
      providerChain: [{ provider: 'claude-code' }],
      maxBudgetUsd: 5.0,
    },
  },
  security: {
    sanitizeContent: true,
    validateUrls: true,
    gateActions: false,
    maxFetchSizeBytes: 10_485_760,
    blockedCommandPatterns: ['rm\\s+-rf\\s+/', 'DROP\\s+TABLE', 'DELETE\\s+FROM'],
  },
});

// ---------------------------------------------------------------------------
// Interface
// ---------------------------------------------------------------------------

/** Interface for agent configuration persistence. */
export interface IAgentConfigStore {
  /**
   * Resolve the effective config for an account.
   * Resolution: account override → system default → hardcoded defaults.
   */
  resolve(accountId: string): Promise<ResolvedAgentConfig>;

  /** Get the raw row for a specific account (or null for system default). */
  getByAccountId(accountId: string | null): Promise<AgentConfigRow | null>;

  /**
   * Upsert the agent config for an account.
   * Creates a new row or updates the existing one.
   * Increments version on update.
   */
  upsert(
    accountId: string | null,
    config: AgentConfigDocument,
    userId?: string | null,
  ): Promise<AgentConfigRow>;

  /** Delete the account-level override. Falls back to system default after deletion. */
  deleteByAccountId(accountId: string): Promise<boolean>;
}

// ---------------------------------------------------------------------------
// In-memory implementation
// ---------------------------------------------------------------------------

/** In-memory implementation of IAgentConfigStore for testing. */
export class InMemoryAgentConfigStore implements IAgentConfigStore {
  private rows = new Map<string, AgentConfigRow>();

  /** Key for the system default row. */
  private static readonly SYSTEM_KEY = '__system_default__';

  constructor(seedSystemDefault = true) {
    if (seedSystemDefault) {
      const now = new Date().toISOString();
      this.rows.set(InMemoryAgentConfigStore.SYSTEM_KEY, {
        id: randomUUID(),
        accountId: null,
        config: structuredClone(HARDCODED_AGENT_CONFIG) as AgentConfigDocument,
        version: 1,
        createdAt: now,
        updatedAt: now,
        createdBy: null,
        updatedBy: null,
      });
    }
  }

  async resolve(accountId: string): Promise<ResolvedAgentConfig> {
    // 1. Account-specific row
    const accountRow = this.rows.get(accountId);
    if (accountRow) {
      return {
        config: structuredClone(accountRow.config),
        source: 'account',
        version: accountRow.version,
      };
    }

    // 2. System default row
    const systemRow = this.rows.get(InMemoryAgentConfigStore.SYSTEM_KEY);
    if (systemRow) {
      return {
        config: structuredClone(systemRow.config),
        source: 'system',
        version: systemRow.version,
      };
    }

    // 3. Hardcoded defaults
    return {
      config: structuredClone(HARDCODED_AGENT_CONFIG) as AgentConfigDocument,
      source: 'hardcoded',
      version: 0,
    };
  }

  async getByAccountId(accountId: string | null): Promise<AgentConfigRow | null> {
    const key = accountId ?? InMemoryAgentConfigStore.SYSTEM_KEY;
    const row = this.rows.get(key);
    if (!row) return null;
    return structuredClone(row);
  }

  async upsert(
    accountId: string | null,
    config: AgentConfigDocument,
    userId?: string | null,
  ): Promise<AgentConfigRow> {
    const key = accountId ?? InMemoryAgentConfigStore.SYSTEM_KEY;
    const existing = this.rows.get(key);
    const now = new Date().toISOString();
    const by = userId ?? null;

    if (existing) {
      const updated: AgentConfigRow = {
        ...existing,
        config: structuredClone(config),
        version: existing.version + 1,
        updatedAt: now,
        updatedBy: by,
      };
      this.rows.set(key, updated);
      return structuredClone(updated);
    }

    const row: AgentConfigRow = {
      id: randomUUID(),
      accountId,
      config: structuredClone(config),
      version: 1,
      createdAt: now,
      updatedAt: now,
      createdBy: by,
      updatedBy: by,
    };
    this.rows.set(key, row);
    return structuredClone(row);
  }

  async deleteByAccountId(accountId: string): Promise<boolean> {
    return this.rows.delete(accountId);
  }
}
