/**
 * InMemoryWorkflowStore Tests
 *
 * Tests the IWorkflowStore interface using the in-memory implementation.
 */

import { describe, it, expect, beforeEach } from 'vitest';
import { InMemoryWorkflowStore } from '../workflow-store.js';
import type { IWorkflowStore, WorkflowDefinition, WorkflowInstance } from '../workflow-store.js';

function createDefinition(overrides: Partial<WorkflowDefinition> = {}): WorkflowDefinition {
  return {
    id: 'def-1',
    name: 'Issue Workflow',
    version: 1,
    description: 'Handles issue processing',
    activities: [{ type: 'analyze' }, { type: 'generate' }],
    syncedAt: Date.now(),
    ...overrides,
  };
}

function createInstance(overrides: Partial<WorkflowInstance> = {}): WorkflowInstance {
  return {
    id: 'inst-1',
    definitionId: 'def-1',
    status: 'pending',
    variables: {},
    createdAt: Date.now(),
    updatedAt: Date.now(),
    ...overrides,
  };
}

describe('InMemoryWorkflowStore', () => {
  let store: IWorkflowStore;

  beforeEach(() => {
    store = new InMemoryWorkflowStore();
  });

  // -----------------------------------------------------------------------
  // Definitions
  // -----------------------------------------------------------------------

  describe('upsertDefinition', () => {
    it('creates a new definition', async () => {
      const def = await store.upsertDefinition(createDefinition());
      expect(def.id).toBe('def-1');
      expect(def.name).toBe('Issue Workflow');
      expect(def.syncedAt).toBeDefined();
    });

    it('updates an existing definition', async () => {
      await store.upsertDefinition(createDefinition());
      const updated = await store.upsertDefinition(
        createDefinition({ name: 'Updated Workflow', version: 2 }),
      );

      expect(updated.name).toBe('Updated Workflow');
      expect(updated.version).toBe(2);
    });

    it('sets syncedAt to current timestamp', async () => {
      const before = Date.now();
      const def = await store.upsertDefinition(createDefinition());
      const after = Date.now();

      expect(def.syncedAt).toBeGreaterThanOrEqual(before);
      expect(def.syncedAt).toBeLessThanOrEqual(after);
    });
  });

  describe('listDefinitions', () => {
    it('returns empty array when none exist', async () => {
      expect(await store.listDefinitions()).toEqual([]);
    });

    it('returns all definitions', async () => {
      await store.upsertDefinition(createDefinition({ id: 'def-1' }));
      await store.upsertDefinition(createDefinition({ id: 'def-2', name: 'PR Workflow' }));

      const defs = await store.listDefinitions();
      expect(defs).toHaveLength(2);
    });
  });

  describe('getDefinition', () => {
    it('returns null for nonexistent', async () => {
      expect(await store.getDefinition('nonexistent')).toBeNull();
    });

    it('returns the definition by id', async () => {
      await store.upsertDefinition(createDefinition());
      const def = await store.getDefinition('def-1');
      expect(def).not.toBeNull();
      expect(def!.name).toBe('Issue Workflow');
    });
  });

  // -----------------------------------------------------------------------
  // Instances
  // -----------------------------------------------------------------------

  describe('createInstance', () => {
    it('creates a new instance', async () => {
      const instance = await store.createInstance(createInstance());
      expect(instance.id).toBe('inst-1');
      expect(instance.definitionId).toBe('def-1');
      expect(instance.status).toBe('pending');
    });

    it('auto-generates id if empty', async () => {
      const instance = await store.createInstance(createInstance({ id: '' }));
      expect(instance.id).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/);
    });

    it('auto-sets timestamps if zero', async () => {
      const instance = await store.createInstance(
        createInstance({ createdAt: 0, updatedAt: 0 }),
      );
      expect(instance.createdAt).toBeGreaterThan(0);
      expect(instance.updatedAt).toBeGreaterThan(0);
    });
  });

  describe('updateInstance', () => {
    it('updates an existing instance', async () => {
      await store.createInstance(createInstance());
      const updated = await store.updateInstance('inst-1', { status: 'running' });

      expect(updated).not.toBeNull();
      expect(updated!.status).toBe('running');
    });

    it('preserves immutable id', async () => {
      await store.createInstance(createInstance());
      const updated = await store.updateInstance('inst-1', { id: 'hacked-id' } as any);

      expect(updated!.id).toBe('inst-1');
    });

    it('updates the updatedAt timestamp', async () => {
      await store.createInstance(createInstance());
      const before = Date.now();
      const updated = await store.updateInstance('inst-1', { status: 'running' });
      const after = Date.now();

      expect(updated!.updatedAt).toBeGreaterThanOrEqual(before);
      expect(updated!.updatedAt).toBeLessThanOrEqual(after);
    });

    it('returns null for nonexistent instance', async () => {
      const result = await store.updateInstance('nonexistent', { status: 'running' });
      expect(result).toBeNull();
    });

    it('updates variables', async () => {
      await store.createInstance(createInstance());
      const updated = await store.updateInstance('inst-1', {
        variables: { issueNumber: 42, prUrl: 'https://...' },
      });

      expect(updated!.variables).toEqual({ issueNumber: 42, prUrl: 'https://...' });
    });

    it('updates currentActivity', async () => {
      await store.createInstance(createInstance());
      const updated = await store.updateInstance('inst-1', {
        currentActivity: 'code-generation',
      });

      expect(updated!.currentActivity).toBe('code-generation');
    });
  });

  describe('getInstance', () => {
    it('returns null for nonexistent', async () => {
      expect(await store.getInstance('nonexistent')).toBeNull();
    });

    it('returns the instance by id', async () => {
      await store.createInstance(createInstance());
      const instance = await store.getInstance('inst-1');
      expect(instance).not.toBeNull();
      expect(instance!.definitionId).toBe('def-1');
    });
  });

  describe('listInstances', () => {
    it('returns empty result when none exist', async () => {
      const result = await store.listInstances();
      expect(result.data).toEqual([]);
      expect(result.total).toBe(0);
    });

    it('returns all instances', async () => {
      await store.createInstance(createInstance({ id: 'inst-1' }));
      await store.createInstance(createInstance({ id: 'inst-2' }));
      await store.createInstance(createInstance({ id: 'inst-3' }));

      const result = await store.listInstances();
      expect(result.data).toHaveLength(3);
      expect(result.total).toBe(3);
    });

    it('filters by definitionId', async () => {
      await store.createInstance(createInstance({ id: 'inst-1', definitionId: 'def-1' }));
      await store.createInstance(createInstance({ id: 'inst-2', definitionId: 'def-2' }));
      await store.createInstance(createInstance({ id: 'inst-3', definitionId: 'def-1' }));

      const result = await store.listInstances({ definitionId: 'def-1' });
      expect(result.data).toHaveLength(2);
      expect(result.total).toBe(2);
    });

    it('paginates correctly', async () => {
      for (let i = 1; i <= 10; i++) {
        await store.createInstance(createInstance({ id: `inst-${i}` }));
      }

      const page1 = await store.listInstances({ page: 1, pageSize: 3 });
      expect(page1.data).toHaveLength(3);
      expect(page1.total).toBe(10);

      const page2 = await store.listInstances({ page: 2, pageSize: 3 });
      expect(page2.data).toHaveLength(3);

      const page4 = await store.listInstances({ page: 4, pageSize: 3 });
      expect(page4.data).toHaveLength(1);
    });

    it('defaults to page 1 with pageSize 50', async () => {
      await store.createInstance(createInstance({ id: 'inst-1' }));
      const result = await store.listInstances();
      expect(result.data).toHaveLength(1);
    });
  });
});
