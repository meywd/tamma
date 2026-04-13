/**
 * Tests for InMemoryPromptStore
 *
 * Tests multi-tenant resolution logic, CRUD operations,
 * template interpolation, and system prompt management.
 *
 * Story 27-2: Prompt Store Service
 */

import { describe, it, expect, beforeEach } from 'vitest';
import { InMemoryPromptStore } from './in-memory-prompt-store.js';
import type { IPromptStore } from './prompt-store.js';

describe('InMemoryPromptStore', () => {
  let store: IPromptStore;

  beforeEach(async () => {
    store = new InMemoryPromptStore({ skipDefaults: true });
  });

  // -----------------------------------------------------------------------
  // get() — resolution logic
  // -----------------------------------------------------------------------

  describe('get() resolution', () => {
    it('should return undefined for non-existent template', async () => {
      const result = await store.get(null, 'developer', 'nonexistent');
      expect(result).toBeUndefined();
    });

    it('should return system default when tenantId is null', async () => {
      await store.upsert(null, 'developer', 'plan', { template: 'System default plan' });

      const result = await store.get(null, 'developer', 'plan');
      expect(result).toBeDefined();
      expect(result!.template).toBe('System default plan');
    });

    it('should return tenant override when it exists', async () => {
      await store.upsert(null, 'developer', 'plan', { template: 'System default' });
      await store.upsert('tenant-1', 'developer', 'plan', { template: 'Tenant override' });

      const result = await store.get('tenant-1', 'developer', 'plan');
      expect(result).toBeDefined();
      expect(result!.template).toBe('Tenant override');
    });

    it('should fall back to system default when no tenant override exists', async () => {
      await store.upsert(null, 'developer', 'plan', { template: 'System default' });

      const result = await store.get('tenant-1', 'developer', 'plan');
      expect(result).toBeDefined();
      expect(result!.template).toBe('System default');
    });

    it('should not return one tenant override when requesting another tenant', async () => {
      await store.upsert('tenant-1', 'developer', 'plan', { template: 'Tenant 1 override' });

      const result = await store.get('tenant-2', 'developer', 'plan');
      expect(result).toBeUndefined();
    });

    it('should return a clone (not mutable reference)', async () => {
      await store.upsert(null, 'developer', 'plan', { template: 'Do: {{thing}}' });
      const result1 = await store.get(null, 'developer', 'plan');
      const result2 = await store.get(null, 'developer', 'plan');
      expect(result1).toEqual(result2);
      result1!.variables.push('injected');
      const result3 = await store.get(null, 'developer', 'plan');
      expect(result3!.variables).not.toContain('injected');
    });
  });

  // -----------------------------------------------------------------------
  // upsert()
  // -----------------------------------------------------------------------

  describe('upsert()', () => {
    it('should create template with version 1', async () => {
      const result = await store.upsert(null, 'tester', 'write-tests', {
        template: 'Write tests for {{target}}',
      });
      expect(result.version).toBe(1);
      expect(result.role).toBe('tester');
      expect(result.action).toBe('write-tests');
    });

    it('should auto-extract variables from template', async () => {
      const result = await store.upsert(null, 'developer', 'implement', {
        template: 'Implement {{feature}} using {{framework}} in {{language}}',
      });
      expect(result.variables).toContain('feature');
      expect(result.variables).toContain('framework');
      expect(result.variables).toContain('language');
    });

    it('should use explicitly provided variables', async () => {
      const result = await store.upsert(null, 'developer', 'implement', {
        template: 'Implement {{feature}}',
        variables: ['feature', 'extra'],
      });
      expect(result.variables).toEqual(['feature', 'extra']);
    });

    it('should bump version on update', async () => {
      const v1 = await store.upsert(null, 'developer', 'plan', { template: 'v1' });
      expect(v1.version).toBe(1);

      const v2 = await store.upsert(null, 'developer', 'plan', { template: 'v2' });
      expect(v2.version).toBe(2);

      const v3 = await store.upsert(null, 'developer', 'plan', { template: 'v3' });
      expect(v3.version).toBe(3);
    });

    it('should preserve createdAt on update', async () => {
      const v1 = await store.upsert(null, 'developer', 'plan', { template: 'v1' });
      await new Promise((resolve) => setTimeout(resolve, 5));
      const v2 = await store.upsert(null, 'developer', 'plan', { template: 'v2' });
      expect(v2.createdAt).toBe(v1.createdAt);
    });

    it('should carry forward systemPrompt from previous version', async () => {
      await store.upsert(null, 'developer', 'plan', {
        template: 'v1',
        systemPrompt: 'You are a planner.',
      });
      const v2 = await store.upsert(null, 'developer', 'plan', { template: 'v2' });
      expect(v2.systemPrompt).toBe('You are a planner.');
    });

    it('should reject forbidden role names', async () => {
      await expect(
        store.upsert(null, '__proto__', 'plan', { template: 'evil' }),
      ).rejects.toThrow('Forbidden role name');
    });

    it('should reject forbidden action names', async () => {
      await expect(
        store.upsert(null, 'developer', 'constructor', { template: 'evil' }),
      ).rejects.toThrow('Forbidden action name');
    });

    it('should reject empty role name', async () => {
      await expect(
        store.upsert(null, '', 'plan', { template: 'no role' }),
      ).rejects.toThrow('Role name must be 1-64 characters');
    });

    it('should track versions independently per tenant', async () => {
      const sys = await store.upsert(null, 'developer', 'plan', { template: 'sys' });
      const t1 = await store.upsert('tenant-1', 'developer', 'plan', { template: 't1' });
      expect(sys.version).toBe(1);
      expect(t1.version).toBe(1);

      const sys2 = await store.upsert(null, 'developer', 'plan', { template: 'sys2' });
      expect(sys2.version).toBe(2);
      // tenant-1 version is still 1
      const t1Get = await store.get('tenant-1', 'developer', 'plan');
      expect(t1Get!.version).toBe(1);
    });
  });

  // -----------------------------------------------------------------------
  // delete()
  // -----------------------------------------------------------------------

  describe('delete()', () => {
    it('should remove tenant override and fall back to system default', async () => {
      await store.upsert(null, 'developer', 'plan', { template: 'System default' });
      await store.upsert('tenant-1', 'developer', 'plan', { template: 'Override' });

      // Verify override is returned
      let result = await store.get('tenant-1', 'developer', 'plan');
      expect(result!.template).toBe('Override');

      // Delete override
      const deleted = await store.delete('tenant-1', 'developer', 'plan');
      expect(deleted).toBe(true);

      // Should now fall back to system default
      result = await store.get('tenant-1', 'developer', 'plan');
      expect(result!.template).toBe('System default');
    });

    it('should return false if override does not exist', async () => {
      const deleted = await store.delete('tenant-1', 'developer', 'plan');
      expect(deleted).toBe(false);
    });
  });

  // -----------------------------------------------------------------------
  // list()
  // -----------------------------------------------------------------------

  describe('list()', () => {
    it('should return empty array for empty store', async () => {
      const result = await store.list(null);
      expect(result).toEqual([]);
    });

    it('should return all system defaults when tenantId is null', async () => {
      await store.upsert(null, 'developer', 'plan', { template: 'plan' });
      await store.upsert(null, 'tester', 'write-tests', { template: 'test' });

      const result = await store.list(null);
      expect(result.length).toBe(2);
      expect(result.every((s) => !s.isOverride)).toBe(true);
    });

    it('should return merged view with tenant overrides taking precedence', async () => {
      // 2 system defaults
      await store.upsert(null, 'developer', 'plan', { template: 'sys plan' });
      await store.upsert(null, 'developer', 'implement', { template: 'sys impl' });

      // 1 tenant override (for plan)
      await store.upsert('tenant-1', 'developer', 'plan', { template: 'tenant plan' });

      const result = await store.list('tenant-1');
      expect(result.length).toBe(2);

      const plan = result.find((s) => s.action === 'plan')!;
      expect(plan.isOverride).toBe(true);

      const impl = result.find((s) => s.action === 'implement')!;
      expect(impl.isOverride).toBe(false);
    });

    it('should sort by role then action', async () => {
      await store.upsert(null, 'tester', 'write-tests', { template: 'test' });
      await store.upsert(null, 'developer', 'plan', { template: 'plan' });
      await store.upsert(null, 'developer', 'implement', { template: 'impl' });

      const result = await store.list(null);
      expect(result.length).toBe(3);
      expect(result[0]!.role).toBe('developer');
      expect(result[0]!.action).toBe('implement');
      expect(result[1]!.role).toBe('developer');
      expect(result[1]!.action).toBe('plan');
      expect(result[2]!.role).toBe('tester');
      expect(result[2]!.action).toBe('write-tests');
    });
  });

  // -----------------------------------------------------------------------
  // render()
  // -----------------------------------------------------------------------

  describe('render()', () => {
    it('should return undefined for non-existent template', async () => {
      const result = await store.render(null, 'developer', 'nonexistent', {
        variables: {},
      });
      expect(result).toBeUndefined();
    });

    it('should interpolate variables in template and system prompt', async () => {
      await store.upsert(null, 'developer', 'implement', {
        template: 'Implement {{feature}} in {{language}}.',
        systemPrompt: 'You are a {{role}} developer.',
      });

      const result = await store.render(null, 'developer', 'implement', {
        variables: { feature: 'login', language: 'TypeScript', role: 'senior' },
      });

      expect(result).toBeDefined();
      expect(result!.renderedTemplate).toBe('Implement login in TypeScript.');
      expect(result!.renderedSystemPrompt).toBe('You are a senior developer.');
    });

    it('should track unresolved variables', async () => {
      await store.upsert(null, 'developer', 'plan', {
        template: 'Plan {{task}} using {{framework}} for {{deadline}}.',
      });

      const result = await store.render(null, 'developer', 'plan', {
        variables: { task: 'auth' },
      });

      expect(result!.renderedTemplate).toBe('Plan auth using {{framework}} for {{deadline}}.');
      expect(result!.unresolvedVariables).toContain('framework');
      expect(result!.unresolvedVariables).toContain('deadline');
    });

    it('should not recursively expand variables (injection safety)', async () => {
      await store.upsert(null, 'developer', 'plan', {
        template: 'Plan: {{task}}',
      });

      const result = await store.render(null, 'developer', 'plan', {
        variables: { task: '{{secret}}' },
      });

      expect(result!.renderedTemplate).toBe('Plan: {{secret}}');
    });

    it('should deduplicate unresolved variables', async () => {
      await store.upsert(null, 'developer', 'plan', {
        template: '{{x}} and {{x}} and {{y}}',
      });

      const result = await store.render(null, 'developer', 'plan', {
        variables: {},
      });

      expect(result!.unresolvedVariables).toEqual(['x', 'y']);
    });

    it('should render using tenant override when it exists', async () => {
      await store.upsert(null, 'developer', 'plan', { template: 'System: {{task}}' });
      await store.upsert('tenant-1', 'developer', 'plan', { template: 'Tenant: {{task}}' });

      const result = await store.render('tenant-1', 'developer', 'plan', {
        variables: { task: 'auth' },
      });

      expect(result!.renderedTemplate).toBe('Tenant: auth');
    });

    it('should include metadata in render result', async () => {
      await store.upsert(null, 'developer', 'plan', {
        template: 'Plan',
        enableTools: true,
        maxTokens: 16384,
      });

      const result = await store.render(null, 'developer', 'plan', {
        variables: {},
      });

      expect(result!.role).toBe('developer');
      expect(result!.action).toBe('plan');
      expect(result!.version).toBe(1);
      expect(result!.enableTools).toBe(true);
      expect(result!.maxTokens).toBe(16384);
    });
  });

  // -----------------------------------------------------------------------
  // System default operations
  // -----------------------------------------------------------------------

  describe('getSystemDefault()', () => {
    it('should return system default template', async () => {
      await store.upsert(null, 'developer', 'plan', { template: 'System plan' });
      const result = await store.getSystemDefault('developer', 'plan');
      expect(result).toBeDefined();
      expect(result!.template).toBe('System plan');
    });

    it('should not return tenant overrides', async () => {
      await store.upsert('tenant-1', 'developer', 'plan', { template: 'Tenant plan' });
      const result = await store.getSystemDefault('developer', 'plan');
      expect(result).toBeUndefined();
    });
  });

  describe('resetSystemDefault()', () => {
    it('should restore hardcoded default', async () => {
      const storeWithDefaults = new InMemoryPromptStore();
      await storeWithDefaults.upsert(null, 'developer', 'plan', { template: 'Custom plan' });

      // Verify custom template is set
      let result = await storeWithDefaults.getSystemDefault('developer', 'plan');
      expect(result!.template).toBe('Custom plan');

      // Reset to default
      const reset = await storeWithDefaults.resetSystemDefault('developer', 'plan');
      expect(reset).toBeDefined();
      expect(reset!.template).not.toBe('Custom plan');
      expect(reset!.version).toBe(1);

      // Verify it was restored
      result = await storeWithDefaults.getSystemDefault('developer', 'plan');
      expect(result!.template).toBe(reset!.template);
    });

    it('should return undefined for unknown role+action', async () => {
      const result = await store.resetSystemDefault('wizard', 'dance');
      expect(result).toBeUndefined();
    });
  });

  describe('listSystemDefaults()', () => {
    it('should return only system defaults', async () => {
      await store.upsert(null, 'developer', 'plan', { template: 'sys' });
      await store.upsert('tenant-1', 'developer', 'plan', { template: 'tenant' });

      const result = await store.listSystemDefaults();
      expect(result.length).toBe(1);
      expect(result[0]!.isOverride).toBe(false);
    });
  });

  // -----------------------------------------------------------------------
  // System prompts (role preambles)
  // -----------------------------------------------------------------------

  describe('getSystemPrompt()', () => {
    it('should return system default role preamble after seeding', async () => {
      const storeWithDefaults = new InMemoryPromptStore();
      const result = await storeWithDefaults.getSystemPrompt(null, 'developer');
      expect(result).toBeDefined();
      expect(result!).toContain('expert software developer');
    });

    it('should return tenant override when it exists', async () => {
      const storeWithDefaults = new InMemoryPromptStore();
      await storeWithDefaults.upsertSystemPrompt('tenant-1', 'developer', 'Custom developer prompt');

      const result = await storeWithDefaults.getSystemPrompt('tenant-1', 'developer');
      expect(result).toBe('Custom developer prompt');
    });

    it('should fall back to system default when no tenant override', async () => {
      const storeWithDefaults = new InMemoryPromptStore();
      const result = await storeWithDefaults.getSystemPrompt('tenant-1', 'developer');
      expect(result).toBeDefined();
      expect(result!).toContain('expert software developer');
    });

    it('should return undefined for unknown role', async () => {
      const result = await store.getSystemPrompt(null, 'wizard');
      expect(result).toBeUndefined();
    });
  });

  describe('upsertSystemPrompt()', () => {
    it('should create system prompt for tenant', async () => {
      await store.upsertSystemPrompt('tenant-1', 'developer', 'Custom prompt');
      const result = await store.getSystemPrompt('tenant-1', 'developer');
      expect(result).toBe('Custom prompt');
    });

    it('should update existing system prompt', async () => {
      await store.upsertSystemPrompt(null, 'developer', 'v1');
      await store.upsertSystemPrompt(null, 'developer', 'v2');
      const result = await store.getSystemPrompt(null, 'developer');
      expect(result).toBe('v2');
    });
  });

  // -----------------------------------------------------------------------
  // Default seeding
  // -----------------------------------------------------------------------

  describe('default seeding', () => {
    it('should seed 80 templates when skipDefaults is false', async () => {
      const storeWithDefaults = new InMemoryPromptStore();
      const list = await storeWithDefaults.list(null);
      // 8 roles * 10 actions = 80
      expect(list.length).toBe(80);
    });

    it('should seed 8 system prompts when skipDefaults is false', async () => {
      const storeWithDefaults = new InMemoryPromptStore();
      const roles = ['developer', 'tester', 'security', 'devops', 'architect', 'product_owner', 'senior_developer', 'tech_writer'];
      for (const role of roles) {
        const prompt = await storeWithDefaults.getSystemPrompt(null, role);
        expect(prompt).toBeDefined();
      }
    });
  });

  // -----------------------------------------------------------------------
  // Static helpers
  // -----------------------------------------------------------------------

  describe('isValidRole', () => {
    it('should return true for known roles', () => {
      expect(InMemoryPromptStore.isValidRole('developer')).toBe(true);
      expect(InMemoryPromptStore.isValidRole('tester')).toBe(true);
    });

    it('should return false for unknown roles', () => {
      expect(InMemoryPromptStore.isValidRole('wizard')).toBe(false);
    });
  });

  describe('isValidAction', () => {
    it('should return true for known actions', () => {
      expect(InMemoryPromptStore.isValidAction('context-scan')).toBe(true);
      expect(InMemoryPromptStore.isValidAction('implement')).toBe(true);
    });

    it('should return false for unknown actions', () => {
      expect(InMemoryPromptStore.isValidAction('dance')).toBe(false);
    });
  });
});
