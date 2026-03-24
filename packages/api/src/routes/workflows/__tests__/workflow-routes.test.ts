/**
 * Workflow Route Tests
 *
 * Tests CRUD for workflow definitions and instances, including pagination
 * and SSE endpoint existence.
 */

import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import Fastify from 'fastify';
import type { FastifyInstance } from 'fastify';
import { registerWorkflowRoutes } from '../index.js';
import { InMemoryWorkflowStore } from '../../../persistence/workflow-store.js';

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
          status: 'pending',
          variables: { issueNumber: 42 },
        },
      });

      expect(response.statusCode).toBe(201);
      const body = response.json();
      expect(body.definitionId).toBe('def-1');
      expect(body.status).toBe('pending');
      expect(body.id).toBeDefined();
    });

    it('rejects missing definitionId (400)', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/workflows/instances',
        payload: { status: 'pending' },
      });

      expect(response.statusCode).toBe(400);
    });
  });

  describe('PUT /api/workflows/instances/:id', () => {
    it('updates an existing instance', async () => {
      await store.createInstance({
        id: 'inst-1',
        definitionId: 'def-1',
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
        id: 'inst-1', definitionId: 'def-1', status: 'running', variables: {},
        createdAt: Date.now(), updatedAt: Date.now(),
      });
      await store.createInstance({
        id: 'inst-2', definitionId: 'def-2', status: 'running', variables: {},
        createdAt: Date.now(), updatedAt: Date.now(),
      });

      const response = await app.inject({
        method: 'GET',
        url: '/api/workflows/instances?definitionId=def-1',
      });

      expect(response.json().data).toHaveLength(1);
    });
  });

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
