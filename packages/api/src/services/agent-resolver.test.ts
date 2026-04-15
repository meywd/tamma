/**
 * Tests for AgentResolverService
 *
 * Story 9-8: Unified Agent Resolver API
 *
 * Tests resolution logic, clamping, phase mapping, prompt resolution,
 * error handling, and prototype pollution guards.
 */

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { AgentResolverService } from './agent-resolver.js';
import type { AgentResolverServiceDeps } from './agent-resolver.js';
import { InMemoryAgentConfigStore } from '../persistence/agent-config-store.js';
import type { AgentConfigDocument } from '../persistence/agent-config-store.js';
import { InMemoryHealthStore } from './health-store.js';
import { InMemoryPromptStore } from './in-memory-prompt-store.js';
import { InMemorySanitizationStore } from './sanitization-store.js';
import type { AgentType, WorkflowPhase } from '@tamma/shared';

// ---------------------------------------------------------------------------
// Test helpers
// ---------------------------------------------------------------------------

const TEST_ACCOUNT = 'test-account-123';

function createDefaultAgentDoc(): AgentConfigDocument {
  return {
    agents: {
      defaults: {
        providerChain: [
          { provider: 'claude-code', model: 'claude-sonnet-4' },
          { provider: 'openrouter', model: 'z-ai/z1-mini' },
        ],
        allowedTools: ['Read', 'Write', 'Bash'],
        maxBudgetUsd: 5.0,
        permissionMode: 'default' as const,
      },
      roles: {
        implementer: {
          providerChain: [{ provider: 'opencode' }],
          allowedTools: ['Read', 'Write', 'Bash', 'Grep'],
          maxBudgetUsd: 10.0,
        },
        reviewer: {
          providerChain: [],
          allowedTools: ['Read'],
        },
      },
      phaseRoleMap: {
        CODE_GENERATION: 'implementer' as AgentType,
      },
    },
    security: {
      sanitizeContent: true,
      validateUrls: true,
      gateActions: false,
      maxFetchSizeBytes: 10_485_760,
    },
  };
}

async function createDeps(
  overrides?: Partial<AgentResolverServiceDeps>,
): Promise<AgentResolverServiceDeps> {
  const configStore = new InMemoryAgentConfigStore();
  await configStore.upsert(TEST_ACCOUNT, createDefaultAgentDoc());

  const healthStore = new InMemoryHealthStore();
  const promptStore = new InMemoryPromptStore({ skipDefaults: true });
  const sanitizationStore = new InMemorySanitizationStore();

  return {
    configStore,
    healthStore,
    promptStore,
    sanitizationStore,
    ...overrides,
  };
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('AgentResolverService', () => {
  let originalEnv: NodeJS.ProcessEnv;

  beforeEach(() => {
    originalEnv = { ...process.env };
  });

  afterEach(() => {
    process.env = originalEnv;
  });

  // -----------------------------------------------------------------------
  // resolveForRole
  // -----------------------------------------------------------------------

  describe('resolveForRole', () => {
    it('resolves with default config for a role', async () => {
      const deps = await createDeps();
      const service = new AgentResolverService(deps);

      const result = await service.resolveForRole(TEST_ACCOUNT, 'architect');

      expect(result.role).toBe('architect');
      // architect has no role-specific chain, falls back to defaults
      expect(result.provider.name).toBe('claude-code');
      expect(result.provider.model).toBe('claude-sonnet-4');
      expect(result.taskConfig.allowedTools).toEqual(['Read', 'Write', 'Bash']);
      expect(result.taskConfig.maxBudgetUsd).toBe(5.0);
      expect(result.taskConfig.permissionMode).toBe('default');
      expect(result.chainEntries).toHaveLength(2);
      expect(result.chainEntries[0]?.provider).toBe('claude-code');
      expect(result.chainEntries[1]?.provider).toBe('openrouter');
    });

    it('resolves with role-specific provider chain', async () => {
      const deps = await createDeps();
      const service = new AgentResolverService(deps);

      const result = await service.resolveForRole(TEST_ACCOUNT, 'implementer');

      expect(result.role).toBe('implementer');
      expect(result.provider.name).toBe('opencode');
      expect(result.taskConfig.allowedTools).toEqual(['Read', 'Write', 'Bash', 'Grep']);
      expect(result.taskConfig.maxBudgetUsd).toBe(10.0);
      expect(result.chainEntries).toHaveLength(1);
      expect(result.chainEntries[0]?.provider).toBe('opencode');
    });

    it('falls back to default chain when role chain is empty', async () => {
      const deps = await createDeps();
      const service = new AgentResolverService(deps);

      const result = await service.resolveForRole(TEST_ACCOUNT, 'reviewer');

      // reviewer has empty providerChain, should fall back to defaults
      expect(result.provider.name).toBe('claude-code');
      expect(result.chainEntries).toHaveLength(2);
      // reviewer has its own allowedTools
      expect(result.taskConfig.allowedTools).toEqual(['Read']);
    });

    it('marks unhealthy providers in chain', async () => {
      const deps = await createDeps();
      // Mark the first default provider as unhealthy
      await deps.healthStore.recordFailure('claude-code:claude-sonnet-4');
      await deps.healthStore.recordFailure('claude-code:claude-sonnet-4');
      await deps.healthStore.recordFailure('claude-code:claude-sonnet-4');
      await deps.healthStore.recordFailure('claude-code:claude-sonnet-4');
      await deps.healthStore.recordFailure('claude-code:claude-sonnet-4');

      const service = new AgentResolverService(deps);
      const result = await service.resolveForRole(TEST_ACCOUNT, 'architect');

      // First entry should be unhealthy
      expect(result.chainEntries[0]?.healthy).toBe(false);
      expect(result.chainEntries[0]?.circuitOpen).toBe(true);
      // Second entry should be healthy
      expect(result.chainEntries[1]?.healthy).toBe(true);
      // Provider should be the first healthy one
      expect(result.provider.name).toBe('openrouter');
    });

    it('uses first provider when all healthy', async () => {
      const deps = await createDeps();
      const service = new AgentResolverService(deps);
      const result = await service.resolveForRole(TEST_ACCOUNT, 'architect');

      expect(result.provider.name).toBe('claude-code');
      expect(result.chainEntries.every((e) => e.healthy)).toBe(true);
    });

    it('uses first provider as fallback when all unhealthy', async () => {
      const deps = await createDeps();
      // Mark both default providers as unhealthy
      for (let i = 0; i < 5; i++) {
        await deps.healthStore.recordFailure('claude-code:claude-sonnet-4');
        await deps.healthStore.recordFailure('openrouter:z-ai/z1-mini');
      }

      const service = new AgentResolverService(deps);
      const result = await service.resolveForRole(TEST_ACCOUNT, 'architect');

      // All entries should be unhealthy
      expect(result.chainEntries.every((e) => !e.healthy)).toBe(true);
      // Still returns first provider as the fallback
      expect(result.provider.name).toBe('claude-code');
    });

    it('returns sanitization status', async () => {
      const deps = await createDeps();
      const service = new AgentResolverService(deps);

      const result = await service.resolveForRole(TEST_ACCOUNT, 'architect');
      expect(result.sanitizationEnabled).toBe(true);
    });

    it('returns sanitization disabled when account overrides', async () => {
      const deps = await createDeps();
      await deps.sanitizationStore.upsertRules(TEST_ACCOUNT, { enabled: false });

      const service = new AgentResolverService(deps);
      const result = await service.resolveForRole(TEST_ACCOUNT, 'architect');

      expect(result.sanitizationEnabled).toBe(false);
    });

    it('resolves system prompt from PromptStore', async () => {
      const deps = await createDeps();
      await deps.promptStore.upsertSystemPrompt(TEST_ACCOUNT, 'architect', 'You are an architect.');

      const service = new AgentResolverService(deps);
      const result = await service.resolveForRole(TEST_ACCOUNT, 'architect');

      expect(result.systemPrompt).toBe('You are an architect.');
    });

    it('falls back to generic prompt when PromptStore has nothing', async () => {
      const deps = await createDeps();
      const service = new AgentResolverService(deps);

      const result = await service.resolveForRole(TEST_ACCOUNT, 'architect');

      expect(result.systemPrompt).toContain('architect');
    });

    it('falls back to system defaults when account has no config', async () => {
      const deps = await createDeps();
      const service = new AgentResolverService(deps);

      const result = await service.resolveForRole('unknown-account', 'architect');

      // Should use the system defaults from InMemoryAgentConfigStore seed
      expect(result.role).toBe('architect');
      expect(result.provider).toBeDefined();
    });

    // --- Error cases ---

    it('throws on forbidden role name (__proto__)', async () => {
      const deps = await createDeps();
      const service = new AgentResolverService(deps);

      await expect(
        service.resolveForRole(TEST_ACCOUNT, '__proto__' as AgentType),
      ).rejects.toThrow('Forbidden role name');
    });

    it('throws on forbidden role name (constructor)', async () => {
      const deps = await createDeps();
      const service = new AgentResolverService(deps);

      await expect(
        service.resolveForRole(TEST_ACCOUNT, 'constructor' as AgentType),
      ).rejects.toThrow('Forbidden role name');
    });

    it('throws on empty role name', async () => {
      const deps = await createDeps();
      const service = new AgentResolverService(deps);

      await expect(
        service.resolveForRole(TEST_ACCOUNT, '' as AgentType),
      ).rejects.toThrow('Role name must be 1-64 characters');
    });

    it('throws on overly long role name', async () => {
      const deps = await createDeps();
      const service = new AgentResolverService(deps);

      const longRole = 'x'.repeat(65);
      await expect(
        service.resolveForRole(TEST_ACCOUNT, longRole as AgentType),
      ).rejects.toThrow('Role name must be 1-64 characters');
    });
  });

  // -----------------------------------------------------------------------
  // resolveForPhase
  // -----------------------------------------------------------------------

  describe('resolveForPhase', () => {
    it('maps phase to role and resolves', async () => {
      const deps = await createDeps();
      const service = new AgentResolverService(deps);

      const result = await service.resolveForPhase(TEST_ACCOUNT, 'CODE_GENERATION');

      expect(result.phase).toBe('CODE_GENERATION');
      expect(result.role).toBe('implementer');
      expect(result.provider.name).toBe('opencode');
    });

    it('uses default phase-role map when no custom mapping', async () => {
      const deps = await createDeps();
      const service = new AgentResolverService(deps);

      const result = await service.resolveForPhase(TEST_ACCOUNT, 'ISSUE_SELECTION');

      expect(result.phase).toBe('ISSUE_SELECTION');
      expect(result.role).toBe('scrum_master');
    });

    it('maps all 8 phases', async () => {
      const deps = await createDeps();
      const service = new AgentResolverService(deps);

      const phases: WorkflowPhase[] = [
        'ISSUE_SELECTION',
        'CONTEXT_ANALYSIS',
        'PLAN_GENERATION',
        'CODE_GENERATION',
        'PR_CREATION',
        'CODE_REVIEW',
        'TEST_EXECUTION',
        'STATUS_MONITORING',
      ];

      for (const phase of phases) {
        const result = await service.resolveForPhase(TEST_ACCOUNT, phase);
        expect(result.phase).toBe(phase);
        expect(result.role).toBeDefined();
        expect(typeof result.role).toBe('string');
      }
    });

    // --- Task overrides with clamping ---

    it('applies budget clamping on task overrides', async () => {
      const deps = await createDeps();
      const service = new AgentResolverService(deps);

      const result = await service.resolveForPhase(
        TEST_ACCOUNT,
        'CODE_GENERATION',
        { taskOverrides: { maxBudgetUsd: 20.0 } },
      );

      // implementer has maxBudgetUsd 10.0, override of 20 should be clamped
      expect(result.taskConfig.maxBudgetUsd).toBe(10.0);
    });

    it('allows budget below ceiling', async () => {
      const deps = await createDeps();
      const service = new AgentResolverService(deps);

      const result = await service.resolveForPhase(
        TEST_ACCOUNT,
        'CODE_GENERATION',
        { taskOverrides: { maxBudgetUsd: 3.0 } },
      );

      expect(result.taskConfig.maxBudgetUsd).toBe(3.0);
    });

    it('clamps allowedTools to intersection only', async () => {
      const deps = await createDeps();
      const service = new AgentResolverService(deps);

      const result = await service.resolveForPhase(
        TEST_ACCOUNT,
        'CODE_GENERATION',
        { taskOverrides: { allowedTools: ['Read', 'Bash', 'Exec'] } },
      );

      // implementer has ['Read', 'Write', 'Bash', 'Grep'], intersection with override
      expect(result.taskConfig.allowedTools).toEqual(['Read', 'Bash']);
    });

    it('clamps bypassPermissions without env var', async () => {
      const deps = await createDeps();
      delete process.env['TAMMA_ALLOW_BYPASS_PERMISSIONS'];

      const service = new AgentResolverService(deps);
      const result = await service.resolveForPhase(
        TEST_ACCOUNT,
        'CODE_GENERATION',
        { taskOverrides: { permissionMode: 'bypassPermissions' } },
      );

      expect(result.taskConfig.permissionMode).toBe('default');
    });

    it('allows bypassPermissions with env var', async () => {
      const deps = await createDeps();
      process.env['TAMMA_ALLOW_BYPASS_PERMISSIONS'] = 'true';

      const service = new AgentResolverService(deps);
      const result = await service.resolveForPhase(
        TEST_ACCOUNT,
        'CODE_GENERATION',
        { taskOverrides: { permissionMode: 'bypassPermissions' } },
      );

      expect(result.taskConfig.permissionMode).toBe('bypassPermissions');
    });

    // --- Error cases ---

    it('throws on invalid phase', async () => {
      const deps = await createDeps();
      const service = new AgentResolverService(deps);

      await expect(
        service.resolveForPhase(TEST_ACCOUNT, 'INVALID_PHASE' as WorkflowPhase),
      ).rejects.toThrow('Invalid workflow phase');
    });

    it('throws on empty phase', async () => {
      const deps = await createDeps();
      const service = new AgentResolverService(deps);

      await expect(
        service.resolveForPhase(TEST_ACCOUNT, '' as WorkflowPhase),
      ).rejects.toThrow('Invalid workflow phase');
    });
  });

  // -----------------------------------------------------------------------
  // Edge cases
  // -----------------------------------------------------------------------

  describe('edge cases', () => {
    it('handles account with no config (falls back to system defaults)', async () => {
      const deps = await createDeps();
      const service = new AgentResolverService(deps);

      // Use an account that has no specific config
      const result = await service.resolveForRole('nonexistent-account', 'architect');

      // Should resolve using system defaults (seeded by InMemoryAgentConfigStore)
      expect(result.role).toBe('architect');
      expect(result.provider).toBeDefined();
      expect(result.chainEntries.length).toBeGreaterThan(0);
    });

    it('handles health store returning null (treats as healthy)', async () => {
      const deps = await createDeps();
      const service = new AgentResolverService(deps);

      // Health store returns null for unknown keys (no record = healthy)
      const result = await service.resolveForRole(TEST_ACCOUNT, 'architect');

      expect(result.chainEntries.every((e) => e.healthy)).toBe(true);
    });

    it('provides stable output shape across multiple calls', async () => {
      const deps = await createDeps();
      const service = new AgentResolverService(deps);

      const result1 = await service.resolveForRole(TEST_ACCOUNT, 'implementer');
      const result2 = await service.resolveForRole(TEST_ACCOUNT, 'implementer');

      expect(result1.role).toEqual(result2.role);
      expect(result1.provider).toEqual(result2.provider);
      expect(result1.taskConfig).toEqual(result2.taskConfig);
      expect(result1.chainEntries).toEqual(result2.chainEntries);
    });
  });
});
