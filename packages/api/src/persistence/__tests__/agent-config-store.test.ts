/**
 * InMemoryAgentConfigStore Tests
 *
 * Tests the IAgentConfigStore interface using the in-memory implementation.
 */

import { describe, it, expect, beforeEach } from 'vitest';
import { InMemoryAgentConfigStore, HARDCODED_AGENT_CONFIG } from '../agent-config-store.js';
import type { IAgentConfigStore, AgentConfigDocument } from '../agent-config-store.js';

const TEST_ACCOUNT_ID = '11111111-1111-1111-1111-111111111111';
const TEST_USER_ID = '22222222-2222-2222-2222-222222222222';

function makeConfig(overrides?: Partial<AgentConfigDocument>): AgentConfigDocument {
  return {
    agents: overrides?.agents ?? {
      defaults: {
        providerChain: [{ provider: 'openrouter', model: 'z-ai/z1-mini' }],
        maxBudgetUsd: 10.0,
      },
    },
    security: overrides?.security ?? {
      sanitizeContent: true,
      validateUrls: true,
      gateActions: false,
      maxFetchSizeBytes: 5_242_880,
    },
  };
}

describe('InMemoryAgentConfigStore', () => {
  let store: IAgentConfigStore;

  beforeEach(() => {
    store = new InMemoryAgentConfigStore();
  });

  // -----------------------------------------------------------------------
  // resolve
  // -----------------------------------------------------------------------

  describe('resolve', () => {
    it('returns system default when no account override exists', async () => {
      const result = await store.resolve(TEST_ACCOUNT_ID);
      expect(result.source).toBe('system');
      expect(result.config.agents.defaults.providerChain).toHaveLength(1);
      expect(result.config.agents.defaults.providerChain[0]!.provider).toBe('claude-code');
    });

    it('returns account override when it exists', async () => {
      const config = makeConfig();
      await store.upsert(TEST_ACCOUNT_ID, config, TEST_USER_ID);

      const result = await store.resolve(TEST_ACCOUNT_ID);
      expect(result.source).toBe('account');
      expect(result.config.agents.defaults.providerChain[0]!.provider).toBe('openrouter');
      expect(result.config.agents.defaults.maxBudgetUsd).toBe(10.0);
    });

    it('returns hardcoded defaults when no system default exists', async () => {
      const bareStore = new InMemoryAgentConfigStore(false);
      const result = await bareStore.resolve(TEST_ACCOUNT_ID);
      expect(result.source).toBe('hardcoded');
      expect(result.version).toBe(0);
    });

    it('returns a clone — mutations do not affect stored data', async () => {
      const result = await store.resolve(TEST_ACCOUNT_ID);
      result.config.agents.defaults.providerChain.push({ provider: 'hacked' });

      const fresh = await store.resolve(TEST_ACCOUNT_ID);
      expect(fresh.config.agents.defaults.providerChain).toHaveLength(1);
    });
  });

  // -----------------------------------------------------------------------
  // getByAccountId
  // -----------------------------------------------------------------------

  describe('getByAccountId', () => {
    it('returns null for nonexistent account', async () => {
      const result = await store.getByAccountId(TEST_ACCOUNT_ID);
      expect(result).toBeNull();
    });

    it('returns the system default row for null accountId', async () => {
      const result = await store.getByAccountId(null);
      expect(result).not.toBeNull();
      expect(result!.accountId).toBeNull();
      expect(result!.version).toBe(1);
    });

    it('returns the account row after upsert', async () => {
      const config = makeConfig();
      await store.upsert(TEST_ACCOUNT_ID, config, TEST_USER_ID);

      const result = await store.getByAccountId(TEST_ACCOUNT_ID);
      expect(result).not.toBeNull();
      expect(result!.accountId).toBe(TEST_ACCOUNT_ID);
      expect(result!.createdBy).toBe(TEST_USER_ID);
    });
  });

  // -----------------------------------------------------------------------
  // upsert
  // -----------------------------------------------------------------------

  describe('upsert', () => {
    it('creates a new row on first upsert', async () => {
      const config = makeConfig();
      const row = await store.upsert(TEST_ACCOUNT_ID, config, TEST_USER_ID);

      expect(row.accountId).toBe(TEST_ACCOUNT_ID);
      expect(row.version).toBe(1);
      expect(row.createdBy).toBe(TEST_USER_ID);
      expect(row.updatedBy).toBe(TEST_USER_ID);
      expect(row.id).toBeDefined();
      expect(row.createdAt).toMatch(/^\d{4}-\d{2}-\d{2}T/);
    });

    it('increments version on subsequent upserts', async () => {
      const config = makeConfig();
      await store.upsert(TEST_ACCOUNT_ID, config, TEST_USER_ID);

      const updated = makeConfig({
        agents: {
          defaults: {
            providerChain: [{ provider: 'zen-mcp' }],
          },
        },
      });
      const row = await store.upsert(TEST_ACCOUNT_ID, updated, TEST_USER_ID);
      expect(row.version).toBe(2);
      expect(row.config.agents.defaults.providerChain[0]!.provider).toBe('zen-mcp');
    });

    it('preserves row id on update', async () => {
      const config = makeConfig();
      const first = await store.upsert(TEST_ACCOUNT_ID, config, TEST_USER_ID);
      const second = await store.upsert(TEST_ACCOUNT_ID, config, TEST_USER_ID);
      expect(second.id).toBe(first.id);
    });

    it('handles null userId gracefully', async () => {
      const config = makeConfig();
      const row = await store.upsert(TEST_ACCOUNT_ID, config);
      expect(row.createdBy).toBeNull();
      expect(row.updatedBy).toBeNull();
    });

    it('can upsert the system default (null accountId)', async () => {
      const config = makeConfig();
      const row = await store.upsert(null, config, TEST_USER_ID);
      expect(row.accountId).toBeNull();
      // System default was already seeded at version 1
      expect(row.version).toBe(2);
    });
  });

  // -----------------------------------------------------------------------
  // deleteByAccountId
  // -----------------------------------------------------------------------

  describe('deleteByAccountId', () => {
    it('returns false when no row exists', async () => {
      const deleted = await store.deleteByAccountId(TEST_ACCOUNT_ID);
      expect(deleted).toBe(false);
    });

    it('deletes existing account row and returns true', async () => {
      const config = makeConfig();
      await store.upsert(TEST_ACCOUNT_ID, config, TEST_USER_ID);

      const deleted = await store.deleteByAccountId(TEST_ACCOUNT_ID);
      expect(deleted).toBe(true);

      // After delete, resolve falls back to system default
      const resolved = await store.resolve(TEST_ACCOUNT_ID);
      expect(resolved.source).toBe('system');
    });

    it('does not affect other accounts', async () => {
      const otherAccount = '33333333-3333-3333-3333-333333333333';
      await store.upsert(TEST_ACCOUNT_ID, makeConfig(), TEST_USER_ID);
      await store.upsert(otherAccount, makeConfig(), TEST_USER_ID);

      await store.deleteByAccountId(TEST_ACCOUNT_ID);

      const other = await store.getByAccountId(otherAccount);
      expect(other).not.toBeNull();
    });
  });

  // -----------------------------------------------------------------------
  // HARDCODED_AGENT_CONFIG
  // -----------------------------------------------------------------------

  describe('HARDCODED_AGENT_CONFIG', () => {
    it('has non-empty providerChain', () => {
      expect(HARDCODED_AGENT_CONFIG.agents.defaults.providerChain.length).toBeGreaterThan(0);
    });

    it('has security defaults', () => {
      expect(HARDCODED_AGENT_CONFIG.security.sanitizeContent).toBe(true);
      expect(HARDCODED_AGENT_CONFIG.security.validateUrls).toBe(true);
    });

    it('is frozen', () => {
      expect(Object.isFrozen(HARDCODED_AGENT_CONFIG)).toBe(true);
    });
  });
});
