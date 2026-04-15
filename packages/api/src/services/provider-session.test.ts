/**
 * Provider Session Service Unit Tests
 *
 * Story 9-4: Tests for ProviderSessionService.
 */

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { ProviderSessionService } from './provider-session.js';
import type { IAgentProviderFactory, IAgentProvider, ProviderChainEntry } from '@tamma/providers';
import type { AgentTaskConfig } from '@tamma/providers';
import type { AgentTaskResult } from '@tamma/shared';

// ---- Mock Provider ----

function createMockProvider(overrides: Partial<IAgentProvider> = {}): IAgentProvider {
  return {
    executeTask: vi.fn().mockResolvedValue({
      success: true,
      output: 'mock output',
      costUsd: 0,
      durationMs: 100,
    } satisfies AgentTaskResult),
    isAvailable: vi.fn().mockResolvedValue(true),
    dispose: vi.fn().mockResolvedValue(undefined),
    ...overrides,
  };
}

// ---- Mock Factory ----

function createMockFactory(overrides: Partial<IAgentProviderFactory> = {}): IAgentProviderFactory {
  const provider = createMockProvider();
  return {
    create: vi.fn().mockResolvedValue(provider),
    register: vi.fn(),
    dispose: vi.fn().mockResolvedValue(undefined),
    ...overrides,
  };
}

describe('ProviderSessionService', () => {
  let service: ProviderSessionService;
  let factory: IAgentProviderFactory;

  beforeEach(() => {
    factory = createMockFactory();
    service = new ProviderSessionService(factory, { autoCleanup: false });
  });

  afterEach(async () => {
    await service.disposeAll();
  });

  // ---- create ----

  describe('create', () => {
    it('creates a session and returns a handle', async () => {
      const result = await service.create({ provider: 'claude-code' });
      expect(result.handle).toBeTruthy();
      expect(result.provider).toBe('claude-code');
      expect(result.model).toBe('default');
    });

    it('creates a session with model', async () => {
      const result = await service.create({ provider: 'openrouter', model: 'gpt-4' });
      expect(result.model).toBe('gpt-4');
    });

    it('calls factory.create with correct entry', async () => {
      await service.create({
        provider: 'openrouter',
        model: 'gpt-4',
        apiKeyRef: 'OPENROUTER_KEY',
        config: { timeout: 30000 },
      });

      expect(factory.create).toHaveBeenCalledWith(
        expect.objectContaining({
          provider: 'openrouter',
          model: 'gpt-4',
          apiKeyRef: 'OPENROUTER_KEY',
          config: { timeout: 30000 },
        }),
      );
    });

    it('rejects empty provider name', async () => {
      await expect(service.create({ provider: '' }))
        .rejects.toThrow('provider is required');
    });

    it('lists the created session', async () => {
      await service.create({ provider: 'claude-code' });
      const sessions = service.listSessions();
      expect(sessions).toHaveLength(1);
      expect(sessions[0]!.provider).toBe('claude-code');
    });
  });

  // ---- execute ----

  describe('execute', () => {
    it('executes a task on the provider', async () => {
      const { handle } = await service.create({ provider: 'claude-code' });

      const config: AgentTaskConfig = {
        prompt: 'Implement feature X',
        workingDirectory: '/tmp/test',
      };

      const result = await service.execute(handle, config);
      expect(result.success).toBe(true);
      expect(result.output).toBe('mock output');
    });

    it('throws for unknown handle', async () => {
      const config: AgentTaskConfig = {
        prompt: 'test',
        workingDirectory: '/tmp/test',
      };

      await expect(
        service.execute('00000000-0000-0000-0000-000000000000', config),
      ).rejects.toThrow('Session not found');
    });

    it('updates lastUsed on execute', async () => {
      const { handle } = await service.create({ provider: 'claude-code' });
      const before = service.listSessions()[0]!.lastUsed;

      // Wait a bit
      await new Promise((resolve) => setTimeout(resolve, 10));

      const config: AgentTaskConfig = {
        prompt: 'test',
        workingDirectory: '/tmp/test',
      };
      await service.execute(handle, config);

      const after = service.listSessions()[0]!.lastUsed;
      expect(after).toBeGreaterThanOrEqual(before);
    });
  });

  // ---- dispose ----

  describe('dispose', () => {
    it('disposes a provider and removes the session', async () => {
      const { handle } = await service.create({ provider: 'claude-code' });
      expect(service.listSessions()).toHaveLength(1);

      const disposed = await service.dispose(handle);
      expect(disposed).toBe(true);
      expect(service.listSessions()).toHaveLength(0);
    });

    it('returns false for unknown handle', async () => {
      const disposed = await service.dispose('00000000-0000-0000-0000-000000000000');
      expect(disposed).toBe(false);
    });
  });

  // ---- cleanup ----

  describe('cleanup', () => {
    it('removes sessions that have been idle beyond TTL', async () => {
      // Create service with very short TTL
      const shortService = new ProviderSessionService(factory, {
        sessionTtlMs: 1,
        autoCleanup: false,
      });

      await shortService.create({ provider: 'claude-code' });
      expect(shortService.listSessions()).toHaveLength(1);

      // Wait for TTL to expire
      await new Promise((resolve) => setTimeout(resolve, 10));

      const cleaned = await shortService.cleanup();
      expect(cleaned).toBe(1);
      expect(shortService.listSessions()).toHaveLength(0);

      await shortService.disposeAll();
    });

    it('keeps active sessions', async () => {
      // Create service with long TTL
      const longService = new ProviderSessionService(factory, {
        sessionTtlMs: 60_000,
        autoCleanup: false,
      });

      await longService.create({ provider: 'claude-code' });
      const cleaned = await longService.cleanup();
      expect(cleaned).toBe(0);
      expect(longService.listSessions()).toHaveLength(1);

      await longService.disposeAll();
    });
  });

  // ---- disposeAll ----

  describe('disposeAll', () => {
    it('disposes all sessions', async () => {
      await service.create({ provider: 'claude-code' });
      await service.create({ provider: 'openrouter' });
      expect(service.listSessions()).toHaveLength(2);

      await service.disposeAll();
      expect(service.listSessions()).toHaveLength(0);
    });
  });

  // ---- concurrent sessions ----

  describe('concurrent sessions', () => {
    it('manages multiple sessions independently', async () => {
      const s1 = await service.create({ provider: 'claude-code' });
      const s2 = await service.create({ provider: 'openrouter' });

      expect(s1.handle).not.toBe(s2.handle);
      expect(service.listSessions()).toHaveLength(2);

      await service.dispose(s1.handle);
      expect(service.listSessions()).toHaveLength(1);
      expect(service.listSessions()[0]!.handle).toBe(s2.handle);
    });
  });
});
