/**
 * InMemoryWorkflowStore Tests — Tenant Scoping (Story 17-4)
 *
 * Verifies that workflow instances are correctly isolated by tenantId.
 */

import { describe, it, expect, beforeEach } from 'vitest';
import { InMemoryWorkflowStore } from '../workflow-store.js';
import type { WorkflowInstance } from '../workflow-store.js';
import { DEFAULT_TENANT_ID } from '@tamma/shared';

const TENANT_A = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const TENANT_B = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';

function createInstance(overrides: Partial<WorkflowInstance> = {}): WorkflowInstance {
  return {
    id: 'inst-1',
    definitionId: 'def-1',
    tenantId: DEFAULT_TENANT_ID,
    status: 'pending',
    variables: {},
    createdAt: Date.now(),
    updatedAt: Date.now(),
    ...overrides,
  };
}

describe('InMemoryWorkflowStore — tenant scoping', () => {
  let store: InMemoryWorkflowStore;

  beforeEach(() => {
    store = new InMemoryWorkflowStore();
  });

  // -----------------------------------------------------------------------
  // createInstance
  // -----------------------------------------------------------------------

  describe('createInstance', () => {
    it('stores tenantId correctly', async () => {
      const instance = await store.createInstance(createInstance({ tenantId: TENANT_A }));
      expect(instance.tenantId).toBe(TENANT_A);
    });

    it('defaults tenantId to DEFAULT_TENANT_ID when empty', async () => {
      const instance = await store.createInstance(createInstance({ tenantId: '' }));
      expect(instance.tenantId).toBe(DEFAULT_TENANT_ID);
    });
  });

  // -----------------------------------------------------------------------
  // listInstances — tenantId filter
  // -----------------------------------------------------------------------

  describe('listInstances with tenantId filter', () => {
    it('returns only tenant A instances', async () => {
      await store.createInstance(createInstance({ id: 'inst-a1', tenantId: TENANT_A }));
      await store.createInstance(createInstance({ id: 'inst-b1', tenantId: TENANT_B }));
      await store.createInstance(createInstance({ id: 'inst-a2', tenantId: TENANT_A }));

      const result = await store.listInstances({ tenantId: TENANT_A });
      expect(result.data).toHaveLength(2);
      expect(result.total).toBe(2);
      expect(result.data.every((i) => i.tenantId === TENANT_A)).toBe(true);
    });

    it('returns only tenant B instances', async () => {
      await store.createInstance(createInstance({ id: 'inst-a1', tenantId: TENANT_A }));
      await store.createInstance(createInstance({ id: 'inst-b1', tenantId: TENANT_B }));

      const result = await store.listInstances({ tenantId: TENANT_B });
      expect(result.data).toHaveLength(1);
      expect(result.total).toBe(1);
      expect(result.data[0]!.tenantId).toBe(TENANT_B);
    });

    it('returns all instances when tenantId is not specified', async () => {
      await store.createInstance(createInstance({ id: 'inst-a1', tenantId: TENANT_A }));
      await store.createInstance(createInstance({ id: 'inst-b1', tenantId: TENANT_B }));

      const result = await store.listInstances();
      expect(result.data).toHaveLength(2);
      expect(result.total).toBe(2);
    });

    it('filters by both tenantId and definitionId', async () => {
      await store.createInstance(createInstance({ id: 'inst-1', tenantId: TENANT_A, definitionId: 'def-1' }));
      await store.createInstance(createInstance({ id: 'inst-2', tenantId: TENANT_A, definitionId: 'def-2' }));
      await store.createInstance(createInstance({ id: 'inst-3', tenantId: TENANT_B, definitionId: 'def-1' }));

      const result = await store.listInstances({ tenantId: TENANT_A, definitionId: 'def-1' });
      expect(result.data).toHaveLength(1);
      expect(result.data[0]!.id).toBe('inst-1');
    });

    it('returns empty result for tenant with no instances', async () => {
      await store.createInstance(createInstance({ id: 'inst-a1', tenantId: TENANT_A }));

      const result = await store.listInstances({ tenantId: TENANT_B });
      expect(result.data).toEqual([]);
      expect(result.total).toBe(0);
    });
  });

  // -----------------------------------------------------------------------
  // getInstance — no implicit tenant filter
  // -----------------------------------------------------------------------

  describe('getInstance', () => {
    it('returns instance regardless of tenant (no implicit filter)', async () => {
      await store.createInstance(createInstance({ id: 'inst-a1', tenantId: TENANT_A }));

      // getInstance does NOT filter by tenant — the caller must verify
      const instance = await store.getInstance('inst-a1');
      expect(instance).not.toBeNull();
      expect(instance!.tenantId).toBe(TENANT_A);
    });
  });

  // -----------------------------------------------------------------------
  // Pagination with tenant filter
  // -----------------------------------------------------------------------

  describe('pagination with tenant filter', () => {
    it('paginates correctly within a tenant', async () => {
      for (let i = 1; i <= 5; i++) {
        await store.createInstance(createInstance({ id: `a-${i}`, tenantId: TENANT_A }));
      }
      for (let i = 1; i <= 3; i++) {
        await store.createInstance(createInstance({ id: `b-${i}`, tenantId: TENANT_B }));
      }

      const page1 = await store.listInstances({ tenantId: TENANT_A, page: 1, pageSize: 2 });
      expect(page1.data).toHaveLength(2);
      expect(page1.total).toBe(5);

      const page3 = await store.listInstances({ tenantId: TENANT_A, page: 3, pageSize: 2 });
      expect(page3.data).toHaveLength(1);
      expect(page3.total).toBe(5);
    });
  });

  // -----------------------------------------------------------------------
  // DEFAULT_TENANT_ID backward compat
  // -----------------------------------------------------------------------

  describe('backward compatibility', () => {
    it('CLI mode uses DEFAULT_TENANT_ID', async () => {
      await store.createInstance(createInstance({ id: 'cli-inst', tenantId: DEFAULT_TENANT_ID }));

      const result = await store.listInstances({ tenantId: DEFAULT_TENANT_ID });
      expect(result.data).toHaveLength(1);
      expect(result.data[0]!.tenantId).toBe(DEFAULT_TENANT_ID);
    });
  });
});
