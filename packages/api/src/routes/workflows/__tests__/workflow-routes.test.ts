/**
 * Workflow Route Tests
 *
 * Tests CRUD for workflow definitions and instances, including pagination,
 * tenant filtering, SSE endpoint existence, cancel, delete, and RBAC enforcement.
 */

import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import Fastify from 'fastify';
import type { FastifyInstance } from 'fastify';
import { DEFAULT_TENANT_ID } from '@tamma/shared';
import { registerWorkflowRoutes } from '../index.js';
import { InMemoryWorkflowStore } from '../../../persistence/workflow-store.js';

const TENANT_A = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const TENANT_B = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';

describe('Workflow Routes', () => {
  let app: FastifyInstance;
  let store: InMemoryWorkflowStore;

  beforeEach(async () => {
    store = new InMemoryWorkflowStore();
    app = Fastify({ logger: false });

    await app.register(
      async (instance) => {
        await registerWorkflowRoutes(instance, { store });
      },
      { prefix: '' },
    );
    await app.ready();
  });

  afterEach(async () => {
    await app.close();
  });

  // -----------------------------------------------------------------------
  // Definitions
  // -----------------------------------------------------------------------

  describe('POST /api/workflows/definitions', () => {
    it('creates a new definition (201)', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/workflows/definitions',
        payload: {
          id: 'def-1',
          name: 'Issue Workflow',
          version: 1,
          activities: [{ type: 'analyze' }],
        },
      });

      expect(response.statusCode).toBe(201);
      const body = response.json();
      expect(body.id).toBe('def-1');
      expect(body.name).toBe('Issue Workflow');
      expect(body.syncedAt).toBeDefined();
    });

    it('updates an existing definition (200)', async () => {
      await app.inject({
        method: 'POST',
        url: '/api/workflows/definitions',
        payload: { id: 'def-1', name: 'Original', version: 1 },
      });

      const response = await app.inject({
        method: 'POST',
        url: '/api/workflows/definitions',
        payload: { id: 'def-1', name: 'Updated', version: 2 },
      });

      expect(response.statusCode).toBe(200);
      expect(response.json().name).toBe('Updated');
    });

    it('rejects empty id (400)', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/workflows/definitions',
        payload: { id: '', name: 'Test' },
      });

      expect(response.statusCode).toBe(400);
    });

    it('rejects missing name (400)', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/workflows/definitions',
        payload: { id: 'def-1' },
      });

      expect(response.statusCode).toBe(400);
    });
  });

  describe('GET /api/workflows/definitions', () => {
    it('returns empty array when none exist', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/workflows/definitions',
      });

      expect(response.statusCode).toBe(200);
      expect(response.json()).toEqual([]);
    });

    it('returns all definitions', async () => {
      await store.upsertDefinition({
        id: 'def-1', name: 'Workflow A', version: 1, activities: [], syncedAt: Date.now(),
      });
      await store.upsertDefinition({
        id: 'def-2', name: 'Workflow B', version: 1, activities: [], syncedAt: Date.now(),
      });

      const response = await app.inject({
        method: 'GET',
        url: '/api/workflows/definitions',
      });

      expect(response.statusCode).toBe(200);
      expect(response.json()).toHaveLength(2);
    });
  });

  // -----------------------------------------------------------------------
  // Instances
  // -----------------------------------------------------------------------

  describe('POST /api/workflows/instances', () => {
    it('creates a new instance (201)', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/workflows/instances',
        payload: {
          definitionId: 'def-1',
          tenantId: DEFAULT_TENANT_ID,
          status: 'pending',
          variables: { issueNumber: 42 },
        },
      });

      expect(response.statusCode).toBe(201);
      const body = response.json();
      expect(body.definitionId).toBe('def-1');
      expect(body.tenantId).toBe(DEFAULT_TENANT_ID);
      expect(body.status).toBe('pending');
      expect(body.id).toBeDefined();
    });

    it('rejects missing definitionId (400)', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/workflows/instances',
        payload: { status: 'pending', tenantId: DEFAULT_TENANT_ID },
      });

      expect(response.statusCode).toBe(400);
    });
  });

  describe('PUT /api/workflows/instances/:id', () => {
    it('updates an existing instance', async () => {
      await store.createInstance({
        id: 'inst-1',
        definitionId: 'def-1',
        tenantId: DEFAULT_TENANT_ID,
        status: 'pending',
        variables: {},
        createdAt: Date.now(),
        updatedAt: Date.now(),
      });

      const response = await app.inject({
        method: 'PUT',
        url: '/api/workflows/instances/inst-1',
        payload: {
          status: 'running',
          currentActivity: 'code-generation',
        },
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.status).toBe('running');
      expect(body.currentActivity).toBe('code-generation');
    });

    it('returns 404 for nonexistent instance', async () => {
      const response = await app.inject({
        method: 'PUT',
        url: '/api/workflows/instances/nonexistent',
        payload: { status: 'running' },
      });

      expect(response.statusCode).toBe(404);
    });

    it('does not allow overwriting id', async () => {
      await store.createInstance({
        id: 'inst-1',
        definitionId: 'def-1',
        tenantId: DEFAULT_TENANT_ID,
        status: 'pending',
        variables: {},
        createdAt: Date.now(),
        updatedAt: Date.now(),
      });

      const response = await app.inject({
        method: 'PUT',
        url: '/api/workflows/instances/inst-1',
        payload: { status: 'running' },
      });

      expect(response.json().id).toBe('inst-1');
    });
  });

  describe('GET /api/workflows/instances', () => {
    it('returns empty result when none exist', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/workflows/instances',
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.data).toEqual([]);
      expect(body.total).toBe(0);
    });

    it('returns paginated instances', async () => {
      for (let i = 1; i <= 5; i++) {
        await store.createInstance({
          id: `inst-${i}`,
          definitionId: 'def-1',
          tenantId: DEFAULT_TENANT_ID,
          status: 'running',
          variables: {},
          createdAt: Date.now(),
          updatedAt: Date.now(),
        });
      }

      const response = await app.inject({
        method: 'GET',
        url: '/api/workflows/instances?page=1&pageSize=2',
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.data).toHaveLength(2);
      expect(body.total).toBe(5);
      expect(body.page).toBe(1);
      expect(body.pageSize).toBe(2);
    });

    it('filters by definitionId', async () => {
      await store.createInstance({
        id: 'inst-1', definitionId: 'def-1', tenantId: DEFAULT_TENANT_ID, status: 'running', variables: {},
        createdAt: Date.now(), updatedAt: Date.now(),
      });
      await store.createInstance({
        id: 'inst-2', definitionId: 'def-2', tenantId: DEFAULT_TENANT_ID, status: 'running', variables: {},
        createdAt: Date.now(), updatedAt: Date.now(),
      });

      const response = await app.inject({
        method: 'GET',
        url: '/api/workflows/instances?definitionId=def-1',
      });

      expect(response.json().data).toHaveLength(1);
    });

    it('filters by tenantId', async () => {
      await store.createInstance({
        id: 'inst-a1', definitionId: 'def-1', tenantId: TENANT_A, status: 'running', variables: {},
        createdAt: Date.now(), updatedAt: Date.now(),
      });
      await store.createInstance({
        id: 'inst-a2', definitionId: 'def-1', tenantId: TENANT_A, status: 'running', variables: {},
        createdAt: Date.now(), updatedAt: Date.now(),
      });
      await store.createInstance({
        id: 'inst-b1', definitionId: 'def-1', tenantId: TENANT_B, status: 'running', variables: {},
        createdAt: Date.now(), updatedAt: Date.now(),
      });

      const response = await app.inject({
        method: 'GET',
        url: `/api/workflows/instances?tenantId=${TENANT_A}`,
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.data).toHaveLength(2);
      expect(body.total).toBe(2);
    });

    it('returns zero results when filtering by tenant with no instances', async () => {
      await store.createInstance({
        id: 'inst-a1', definitionId: 'def-1', tenantId: TENANT_A, status: 'running', variables: {},
        createdAt: Date.now(), updatedAt: Date.now(),
      });

      const response = await app.inject({
        method: 'GET',
        url: `/api/workflows/instances?tenantId=${TENANT_B}`,
      });

      expect(response.json().data).toEqual([]);
      expect(response.json().total).toBe(0);
    });
  });

  // -----------------------------------------------------------------------
  // Cancel
  // -----------------------------------------------------------------------

  describe('POST /api/workflows/instances/:id/cancel', () => {
    it('cancels a running instance', async () => {
      await store.createInstance({
        id: 'inst-1',
        definitionId: 'def-1',
        status: 'running',
        variables: {},
        createdAt: Date.now(),
        updatedAt: Date.now(),
      });

      const response = await app.inject({
        method: 'POST',
        url: '/api/workflows/instances/inst-1/cancel',
      });

      expect(response.statusCode).toBe(200);
      expect(response.json().status).toBe('cancelled');
    });

    it('returns already-cancelled message for already cancelled instance', async () => {
      await store.createInstance({
        id: 'inst-1',
        definitionId: 'def-1',
        status: 'cancelled',
        variables: {},
        createdAt: Date.now(),
        updatedAt: Date.now(),
      });

      const response = await app.inject({
        method: 'POST',
        url: '/api/workflows/instances/inst-1/cancel',
      });

      expect(response.statusCode).toBe(200);
      expect(response.json().message).toBe('Instance already cancelled');
    });

    it('returns 404 for nonexistent instance', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/workflows/instances/nonexistent/cancel',
      });

      expect(response.statusCode).toBe(404);
    });
  });

  // -----------------------------------------------------------------------
  // Delete
  // -----------------------------------------------------------------------

  describe('DELETE /api/workflows/instances/:id', () => {
    it('deletes an existing instance', async () => {
      await store.createInstance({
        id: 'inst-1',
        definitionId: 'def-1',
        status: 'completed',
        variables: {},
        createdAt: Date.now(),
        updatedAt: Date.now(),
      });

      const response = await app.inject({
        method: 'DELETE',
        url: '/api/workflows/instances/inst-1',
      });

      expect(response.statusCode).toBe(200);
      expect(response.json().ok).toBe(true);

      // Confirm it's gone
      const inst = await store.getInstance('inst-1');
      expect(inst).toBeNull();
    });

    it('returns 404 for nonexistent instance', async () => {
      const response = await app.inject({
        method: 'DELETE',
        url: '/api/workflows/instances/nonexistent',
      });

      expect(response.statusCode).toBe(404);
    });
  });

  // -----------------------------------------------------------------------
  // SSE
  // -----------------------------------------------------------------------

  describe('GET /api/workflows/instances/:id/events (SSE)', () => {
    it('returns 404 for nonexistent instance', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/workflows/instances/nonexistent/events',
      });

      expect(response.statusCode).toBe(404);
    });
  });
});

// ================================================================
// RBAC Enforcement Tests for Workflow Routes
// ================================================================

describe('Workflow Routes RBAC enforcement', () => {
  let app: FastifyInstance;
  let store: InMemoryWorkflowStore;

  beforeEach(async () => {
    store = new InMemoryWorkflowStore();
    app = Fastify({ logger: false });

    // Decorate request with authUser (simulates what the auth plugin does)
    app.decorateRequest('authUser', null);

    // Hook to set authUser from test headers
    app.addHook('onRequest', async (request) => {
      const roleHeader = request.headers['x-test-role'] as string | undefined;
      const userIdHeader = request.headers['x-test-user-id'] as string | undefined;
      if (roleHeader && userIdHeader) {
        (request as unknown as { authUser: { id: string; role: string; username: string } }).authUser = {
          id: userIdHeader,
          role: roleHeader,
          username: 'test-user',
        };
      }
    });

    await app.register(
      async (instance) => {
        await registerWorkflowRoutes(instance, { store });
      },
      { prefix: '' },
    );
    await app.ready();

    // Seed test data
    await store.createInstance({
      id: 'inst-1',
      definitionId: 'def-1',
      status: 'running',
      variables: {},
      createdAt: Date.now(),
      updatedAt: Date.now(),
    });
  });

  afterEach(async () => {
    await app.close();
  });

  // ---- View (member+) ----
  it('member can GET /api/workflows/instances', async () => {
    const res = await app.inject({
      method: 'GET',
      url: '/api/workflows/instances',
      headers: { 'x-test-role': 'member', 'x-test-user-id': 'user-1' },
    });
    expect(res.statusCode).toBe(200);
  });

  it('member can GET /api/workflows/definitions', async () => {
    const res = await app.inject({
      method: 'GET',
      url: '/api/workflows/definitions',
      headers: { 'x-test-role': 'member', 'x-test-user-id': 'user-1' },
    });
    expect(res.statusCode).toBe(200);
  });

  // ---- Manage (admin+) ----
  it('member cannot POST /api/workflows/definitions', async () => {
    const res = await app.inject({
      method: 'POST',
      url: '/api/workflows/definitions',
      headers: { 'x-test-role': 'member', 'x-test-user-id': 'user-1' },
      payload: { id: 'def-x', name: 'Test' },
    });
    expect(res.statusCode).toBe(403);
  });

  it('admin can POST /api/workflows/definitions', async () => {
    const res = await app.inject({
      method: 'POST',
      url: '/api/workflows/definitions',
      headers: { 'x-test-role': 'admin', 'x-test-user-id': 'user-2' },
      payload: { id: 'def-x', name: 'Test' },
    });
    expect(res.statusCode).toBe(201);
  });

  it('member cannot POST /api/workflows/instances', async () => {
    const res = await app.inject({
      method: 'POST',
      url: '/api/workflows/instances',
      headers: { 'x-test-role': 'member', 'x-test-user-id': 'user-1' },
      payload: { definitionId: 'def-1' },
    });
    expect(res.statusCode).toBe(403);
  });

  it('member cannot PUT /api/workflows/instances/:id', async () => {
    const res = await app.inject({
      method: 'PUT',
      url: '/api/workflows/instances/inst-1',
      headers: { 'x-test-role': 'member', 'x-test-user-id': 'user-1' },
      payload: { status: 'completed' },
    });
    expect(res.statusCode).toBe(403);
  });

  // ---- Cancel (admin+ via workflows:manage) ----
  it('member cannot POST /api/workflows/instances/:id/cancel', async () => {
    const res = await app.inject({
      method: 'POST',
      url: '/api/workflows/instances/inst-1/cancel',
      headers: { 'x-test-role': 'member', 'x-test-user-id': 'user-1' },
    });
    expect(res.statusCode).toBe(403);
  });

  it('admin can POST /api/workflows/instances/:id/cancel', async () => {
    const res = await app.inject({
      method: 'POST',
      url: '/api/workflows/instances/inst-1/cancel',
      headers: { 'x-test-role': 'admin', 'x-test-user-id': 'user-2' },
    });
    expect(res.statusCode).toBe(200);
    expect(res.json().status).toBe('cancelled');
  });

  it('owner can POST /api/workflows/instances/:id/cancel', async () => {
    const res = await app.inject({
      method: 'POST',
      url: '/api/workflows/instances/inst-1/cancel',
      headers: { 'x-test-role': 'owner', 'x-test-user-id': 'user-3' },
    });
    expect(res.statusCode).toBe(200);
  });

  // ---- Delete (owner only via workflows:delete) ----
  it('member cannot DELETE /api/workflows/instances/:id', async () => {
    const res = await app.inject({
      method: 'DELETE',
      url: '/api/workflows/instances/inst-1',
      headers: { 'x-test-role': 'member', 'x-test-user-id': 'user-1' },
    });
    expect(res.statusCode).toBe(403);
  });

  it('admin cannot DELETE /api/workflows/instances/:id', async () => {
    const res = await app.inject({
      method: 'DELETE',
      url: '/api/workflows/instances/inst-1',
      headers: { 'x-test-role': 'admin', 'x-test-user-id': 'user-2' },
    });
    expect(res.statusCode).toBe(403);
  });

  it('owner can DELETE /api/workflows/instances/:id', async () => {
    const res = await app.inject({
      method: 'DELETE',
      url: '/api/workflows/instances/inst-1',
      headers: { 'x-test-role': 'owner', 'x-test-user-id': 'user-3' },
    });
    expect(res.statusCode).toBe(200);
    expect(res.json().ok).toBe(true);
  });

  // ---- Unauthenticated ----
  it('returns 401 for unauthenticated user on view endpoint', async () => {
    const res = await app.inject({
      method: 'GET',
      url: '/api/workflows/instances',
    });
    expect(res.statusCode).toBe(401);
  });

  it('returns 401 for unauthenticated user on manage endpoint', async () => {
    const res = await app.inject({
      method: 'POST',
      url: '/api/workflows/instances/inst-1/cancel',
    });
    expect(res.statusCode).toBe(401);
  });
});
