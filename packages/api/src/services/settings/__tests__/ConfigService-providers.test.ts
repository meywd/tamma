/**
 * Tests for ConfigService user-scoped provider methods and resolveForRepo.
 */

import { describe, it, expect, beforeEach } from 'vitest';
import { ConfigService } from '../ConfigService.js';
import { InMemoryUserStore } from '../../../persistence/user-store.js';
import type { IProvidersConfig } from '@tamma/shared';
import type { RepoConfigReader } from '../repo-config-reader.js';
import type { IRepoConfig } from '@tamma/shared';

describe('ConfigService - user providers', () => {
  let store: InMemoryUserStore;
  let service: ConfigService;

  beforeEach(async () => {
    store = new InMemoryUserStore();
    service = new ConfigService(undefined, undefined, null, store, null);

    // Create a test user
    await store.upsertUser({
      githubId: 1001,
      githubLogin: 'test-user',
      email: 'test@example.com',
      role: 'member',
    });
  });

  describe('getUserProviders', () => {
    it('returns empty config for a user with no settings', async () => {
      const user = await store.getUserByGithubId(1001);
      const providers = await service.getUserProviders(user!.id);
      expect(providers.providers).toEqual({});
    });

    it('returns empty config when user store is not configured', async () => {
      const noStoreService = new ConfigService(undefined, undefined, null, null, null);
      const providers = await noStoreService.getUserProviders('any-id');
      expect(providers.providers).toEqual({});
    });

    it('returns settings after they are updated', async () => {
      const user = await store.getUserByGithubId(1001);
      const settings: IProvidersConfig = {
        providers: {
          anthropic: { apiKey: 'sk-test', defaultModel: 'claude-sonnet-4-5' },
        },
        maxBudgetUsd: 10.0,
      };

      await service.updateUserProviders(user!.id, settings);
      const retrieved = await service.getUserProviders(user!.id);

      expect(retrieved.providers['anthropic']!.apiKey).toBe('sk-test');
      expect(retrieved.maxBudgetUsd).toBe(10.0);
    });
  });

  describe('updateUserProviders', () => {
    it('validates and persists settings', async () => {
      const user = await store.getUserByGithubId(1001);
      const settings: IProvidersConfig = {
        providers: {
          anthropic: { apiKey: 'sk-test' },
          openrouter: { apiKey: 'sk-or-test', baseUrl: 'https://openrouter.ai' },
        },
      };

      const result = await service.updateUserProviders(user!.id, settings);
      expect(Object.keys(result.providers)).toHaveLength(2);
    });

    it('throws when user store is not configured', async () => {
      const noStoreService = new ConfigService(undefined, undefined, null, null, null);
      await expect(
        noStoreService.updateUserProviders('any-id', { providers: { x: {} } }),
      ).rejects.toThrow('User store not configured');
    });

    it('throws on invalid config (empty providers)', async () => {
      const user = await store.getUserByGithubId(1001);
      await expect(
        service.updateUserProviders(user!.id, { providers: {} }),
      ).rejects.toThrow('At least one provider');
    });

    it('throws on invalid provider name', async () => {
      const user = await store.getUserByGithubId(1001);
      await expect(
        service.updateUserProviders(user!.id, { providers: { 'INVALID NAME': {} } }),
      ).rejects.toThrow('must match');
    });
  });

  describe('resolveForRepo', () => {
    it('resolves config with user providers and empty repo config', async () => {
      const user = await store.getUserByGithubId(1001);
      await service.updateUserProviders(user!.id, {
        providers: {
          anthropic: { apiKey: 'sk-test', defaultModel: 'claude-opus-4-6' },
        },
        maxBudgetUsd: 5.0,
      });

      const { config, warnings } = await service.resolveForRepo(
        user!.id, 'owner', 'repo', 'main',
      );

      expect(config.agents).toBeDefined();
      expect(config.agents!.defaults.providerChain[0]!.provider).toBe('anthropic');
      expect(config.agents!.defaults.providerChain[0]!.model).toBe('claude-opus-4-6');
      expect(config.agents!.defaults.maxBudgetUsd).toBe(5.0);
      expect(warnings).toHaveLength(0);
    });

    it('merges repo config from reader when available', async () => {
      const user = await store.getUserByGithubId(1001);
      await service.updateUserProviders(user!.id, {
        providers: { anthropic: { apiKey: 'sk-test' } },
      });

      const mockReader: RepoConfigReader = {
        async readRepoConfig(): Promise<IRepoConfig> {
          return {
            engine: { approvalMode: 'auto' },
            roles: { implementer: { provider: 'anthropic', model: 'claude-opus-4-6' } },
          };
        },
      };

      const serviceWithReader = new ConfigService(undefined, undefined, null, store, mockReader);
      // Re-set user providers through this service
      const { config } = await serviceWithReader.resolveForRepo(
        user!.id, 'owner', 'repo', 'main',
      );

      expect(config.engine.approvalMode).toBe('auto');
    });

    it('works with empty user providers when no reader configured', async () => {
      const user = await store.getUserByGithubId(1001);
      // User has default empty settings
      const { config } = await service.resolveForRepo(
        user!.id, 'owner', 'repo', 'main',
      );

      // Should still return a valid config with defaults
      expect(config.mode).toBe('standalone');
      expect(config.agents!.defaults.providerChain[0]!.provider).toBe('claude-code');
    });
  });
});
