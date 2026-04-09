/**
 * Tests for PromptStore
 *
 * Tests CRUD operations, template interpolation, file persistence,
 * default seeding, and validation.
 *
 * Story 12-5: Prompt Engineering Framework
 */

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { readFile, writeFile, mkdir, rm } from 'node:fs/promises';
import { PromptStore } from './prompt-store.js';
import type { UpsertPromptInput } from './prompt-store.js';

const TEST_FILE_PATH = '/tmp/tamma-test-prompts.json';

describe('PromptStore', () => {
  let store: PromptStore;

  beforeEach(async () => {
    // Clean up test file
    try {
      await rm(TEST_FILE_PATH, { force: true });
    } catch {
      // ignore
    }
    store = new PromptStore({
      filePath: TEST_FILE_PATH,
      skipDefaults: true,
    });
  });

  afterEach(async () => {
    try {
      await rm(TEST_FILE_PATH, { force: true });
    } catch {
      // ignore
    }
  });

  describe('initialize', () => {
    it('should initialize without errors when file does not exist', async () => {
      await store.initialize();
      const list = await store.list();
      expect(list).toEqual([]);
    });

    it('should be idempotent', async () => {
      await store.initialize();
      await store.initialize();
      const list = await store.list();
      expect(list).toEqual([]);
    });

    it('should seed defaults when skipDefaults is false', async () => {
      const storeWithDefaults = new PromptStore({
        filePath: TEST_FILE_PATH,
        skipDefaults: false,
      });
      await storeWithDefaults.initialize();
      const list = await storeWithDefaults.list();
      // 8 roles * 10 actions = 80 templates
      expect(list.length).toBe(80);
    });

    it('should load from file if it exists', async () => {
      const templates = [
        {
          role: 'developer',
          action: 'plan',
          version: 3,
          template: 'Plan: {{task}}',
          variables: ['task'],
          systemPrompt: 'You are a planner.',
          enableTools: false,
          maxTokens: 2048,
          createdAt: '2025-01-01T00:00:00.000Z',
          updatedAt: '2025-01-02T00:00:00.000Z',
        },
      ];
      await mkdir('/tmp', { recursive: true });
      await writeFile(TEST_FILE_PATH, JSON.stringify(templates), 'utf-8');

      const freshStore = new PromptStore({
        filePath: TEST_FILE_PATH,
        skipDefaults: true,
      });
      await freshStore.initialize();

      const loaded = await freshStore.get('developer', 'plan');
      expect(loaded).toBeDefined();
      expect(loaded!.version).toBe(3);
      expect(loaded!.template).toBe('Plan: {{task}}');
    });

    it('should not overwrite user-customized templates with defaults', async () => {
      const templates = [
        {
          role: 'developer',
          action: 'plan',
          version: 5,
          template: 'Custom plan: {{task}}',
          variables: ['task'],
          systemPrompt: 'Custom system prompt.',
          enableTools: true,
          maxTokens: 8192,
          createdAt: '2025-01-01T00:00:00.000Z',
          updatedAt: '2025-06-01T00:00:00.000Z',
        },
      ];
      await mkdir('/tmp', { recursive: true });
      await writeFile(TEST_FILE_PATH, JSON.stringify(templates), 'utf-8');

      const storeWithDefaults = new PromptStore({
        filePath: TEST_FILE_PATH,
        skipDefaults: false,
      });
      await storeWithDefaults.initialize();

      const loaded = await storeWithDefaults.get('developer', 'plan');
      expect(loaded!.version).toBe(5);
      expect(loaded!.template).toBe('Custom plan: {{task}}');
    });
  });

  describe('get', () => {
    it('should return undefined for non-existent template', async () => {
      const result = await store.get('developer', 'nonexistent');
      expect(result).toBeUndefined();
    });

    it('should return template after upsert', async () => {
      await store.upsert('developer', 'plan', { template: 'Do: {{thing}}' });
      const result = await store.get('developer', 'plan');
      expect(result).toBeDefined();
      expect(result!.role).toBe('developer');
      expect(result!.action).toBe('plan');
      expect(result!.template).toBe('Do: {{thing}}');
    });

    it('should return a clone (not mutable reference)', async () => {
      await store.upsert('developer', 'plan', { template: 'Do: {{thing}}' });
      const result1 = await store.get('developer', 'plan');
      const result2 = await store.get('developer', 'plan');
      expect(result1).toEqual(result2);
      // Mutating one should not affect the other
      result1!.variables.push('injected');
      const result3 = await store.get('developer', 'plan');
      expect(result3!.variables).not.toContain('injected');
    });
  });

  describe('upsert', () => {
    it('should create template with version 1', async () => {
      const result = await store.upsert('tester', 'write-tests', {
        template: 'Write tests for {{target}}',
      });
      expect(result.version).toBe(1);
      expect(result.role).toBe('tester');
      expect(result.action).toBe('write-tests');
    });

    it('should auto-extract variables from template', async () => {
      const result = await store.upsert('developer', 'implement', {
        template: 'Implement {{feature}} using {{framework}} in {{language}}',
      });
      expect(result.variables).toContain('feature');
      expect(result.variables).toContain('framework');
      expect(result.variables).toContain('language');
    });

    it('should use explicitly provided variables', async () => {
      const result = await store.upsert('developer', 'implement', {
        template: 'Implement {{feature}}',
        variables: ['feature', 'extra'],
      });
      expect(result.variables).toEqual(['feature', 'extra']);
    });

    it('should bump version on update', async () => {
      const v1 = await store.upsert('developer', 'plan', { template: 'v1: {{x}}' });
      expect(v1.version).toBe(1);

      const v2 = await store.upsert('developer', 'plan', { template: 'v2: {{x}}' });
      expect(v2.version).toBe(2);

      const v3 = await store.upsert('developer', 'plan', { template: 'v3: {{x}}' });
      expect(v3.version).toBe(3);
    });

    it('should preserve createdAt on update', async () => {
      const v1 = await store.upsert('developer', 'plan', { template: 'v1' });
      // Small delay to ensure different timestamps
      await new Promise((resolve) => setTimeout(resolve, 5));
      const v2 = await store.upsert('developer', 'plan', { template: 'v2' });
      expect(v2.createdAt).toBe(v1.createdAt);
      // updatedAt should be >= v1's (may be same millisecond in fast execution)
      expect(new Date(v2.updatedAt).getTime()).toBeGreaterThanOrEqual(
        new Date(v1.updatedAt).getTime(),
      );
    });

    it('should carry forward systemPrompt from previous version', async () => {
      await store.upsert('developer', 'plan', {
        template: 'v1',
        systemPrompt: 'You are a planner.',
      });
      const v2 = await store.upsert('developer', 'plan', { template: 'v2' });
      expect(v2.systemPrompt).toBe('You are a planner.');
    });

    it('should reject forbidden role names', async () => {
      await expect(
        store.upsert('__proto__', 'plan', { template: 'evil' }),
      ).rejects.toThrow('Forbidden role name');
    });

    it('should reject forbidden action names', async () => {
      await expect(
        store.upsert('developer', 'constructor', { template: 'evil' }),
      ).rejects.toThrow('Forbidden action name');
    });

    it('should reject empty role name', async () => {
      await expect(
        store.upsert('', 'plan', { template: 'no role' }),
      ).rejects.toThrow('Role name must be 1-64 characters');
    });
  });

  describe('list', () => {
    it('should return empty array for empty store', async () => {
      const result = await store.list();
      expect(result).toEqual([]);
    });

    it('should return summaries sorted by role then action', async () => {
      await store.upsert('tester', 'write-tests', { template: 'test' });
      await store.upsert('developer', 'plan', { template: 'plan' });
      await store.upsert('developer', 'implement', { template: 'impl' });

      const result = await store.list();
      expect(result.length).toBe(3);
      expect(result[0]!.role).toBe('developer');
      expect(result[0]!.action).toBe('implement');
      expect(result[1]!.role).toBe('developer');
      expect(result[1]!.action).toBe('plan');
      expect(result[2]!.role).toBe('tester');
      expect(result[2]!.action).toBe('write-tests');
    });

    it('should return correct summary fields', async () => {
      await store.upsert('developer', 'plan', {
        template: 'Plan {{task}} with {{priority}}',
        enableTools: true,
        maxTokens: 8192,
      });

      const list = await store.list();
      const summary = list[0]!;
      expect(summary.role).toBe('developer');
      expect(summary.action).toBe('plan');
      expect(summary.version).toBe(1);
      expect(summary.enableTools).toBe(true);
      expect(summary.maxTokens).toBe(8192);
      expect(summary.variableCount).toBe(2);
      expect(summary.updatedAt).toBeDefined();
    });
  });

  describe('render', () => {
    it('should return undefined for non-existent template', async () => {
      const result = await store.render('developer', 'nonexistent', {
        variables: {},
      });
      expect(result).toBeUndefined();
    });

    it('should interpolate variables in template and system prompt', async () => {
      await store.upsert('developer', 'implement', {
        template: 'Implement {{feature}} in {{language}}.',
        systemPrompt: 'You are a {{role}} developer.',
      });

      const result = await store.render('developer', 'implement', {
        variables: { feature: 'login', language: 'TypeScript', role: 'senior' },
      });

      expect(result).toBeDefined();
      expect(result!.renderedTemplate).toBe('Implement login in TypeScript.');
      expect(result!.renderedSystemPrompt).toBe('You are a senior developer.');
    });

    it('should track unresolved variables', async () => {
      await store.upsert('developer', 'plan', {
        template: 'Plan {{task}} using {{framework}} for {{deadline}}.',
      });

      const result = await store.render('developer', 'plan', {
        variables: { task: 'auth' },
      });

      expect(result!.renderedTemplate).toBe('Plan auth using {{framework}} for {{deadline}}.');
      expect(result!.unresolvedVariables).toContain('framework');
      expect(result!.unresolvedVariables).toContain('deadline');
    });

    it('should not recursively expand variables (injection safety)', async () => {
      await store.upsert('developer', 'plan', {
        template: 'Plan: {{task}}',
      });

      const result = await store.render('developer', 'plan', {
        variables: { task: '{{secret}}' },
      });

      // The {{secret}} should be left as literal text, not expanded
      expect(result!.renderedTemplate).toBe('Plan: {{secret}}');
    });

    it('should deduplicate unresolved variables', async () => {
      await store.upsert('developer', 'plan', {
        template: '{{x}} and {{x}} and {{y}}',
      });

      const result = await store.render('developer', 'plan', {
        variables: {},
      });

      expect(result!.unresolvedVariables).toEqual(['x', 'y']);
    });

    it('should include metadata in render result', async () => {
      await store.upsert('developer', 'plan', {
        template: 'Plan',
        enableTools: true,
        maxTokens: 16384,
      });

      const result = await store.render('developer', 'plan', {
        variables: {},
      });

      expect(result!.role).toBe('developer');
      expect(result!.action).toBe('plan');
      expect(result!.version).toBe(1);
      expect(result!.enableTools).toBe(true);
      expect(result!.maxTokens).toBe(16384);
    });
  });

  describe('isValidRole', () => {
    it('should return true for known roles', () => {
      expect(PromptStore.isValidRole('developer')).toBe(true);
      expect(PromptStore.isValidRole('tester')).toBe(true);
      expect(PromptStore.isValidRole('security')).toBe(true);
      expect(PromptStore.isValidRole('architect')).toBe(true);
    });

    it('should return false for unknown roles', () => {
      expect(PromptStore.isValidRole('wizard')).toBe(false);
      expect(PromptStore.isValidRole('')).toBe(false);
    });
  });

  describe('isValidAction', () => {
    it('should return true for known actions', () => {
      expect(PromptStore.isValidAction('context-scan')).toBe(true);
      expect(PromptStore.isValidAction('implement')).toBe(true);
      expect(PromptStore.isValidAction('debug')).toBe(true);
    });

    it('should return false for unknown actions', () => {
      expect(PromptStore.isValidAction('dance')).toBe(false);
      expect(PromptStore.isValidAction('')).toBe(false);
    });
  });

  describe('file persistence', () => {
    it('should persist to file after upsert', async () => {
      await store.upsert('developer', 'plan', {
        template: 'Plan: {{task}}',
        systemPrompt: 'You are a planner.',
      });

      // Wait a tick for async persist
      await new Promise((resolve) => setTimeout(resolve, 100));

      const content = await readFile(TEST_FILE_PATH, 'utf-8');
      const parsed = JSON.parse(content);
      expect(Array.isArray(parsed)).toBe(true);
      expect(parsed.length).toBe(1);
      expect(parsed[0].role).toBe('developer');
      expect(parsed[0].action).toBe('plan');
    });

    it('should roundtrip through file persistence', async () => {
      await store.upsert('developer', 'plan', {
        template: 'Plan: {{task}}',
        systemPrompt: 'Planner system prompt.',
        enableTools: true,
        maxTokens: 4096,
      });

      // Wait for persistence
      await new Promise((resolve) => setTimeout(resolve, 100));

      // Create new store from same file
      const store2 = new PromptStore({
        filePath: TEST_FILE_PATH,
        skipDefaults: true,
      });
      const loaded = await store2.get('developer', 'plan');
      expect(loaded).toBeDefined();
      expect(loaded!.template).toBe('Plan: {{task}}');
      expect(loaded!.systemPrompt).toBe('Planner system prompt.');
      expect(loaded!.enableTools).toBe(true);
      expect(loaded!.maxTokens).toBe(4096);
      expect(loaded!.version).toBe(1);
    });
  });
});
