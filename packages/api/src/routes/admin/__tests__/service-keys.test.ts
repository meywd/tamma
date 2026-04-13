/**
 * Admin Service Key Routes Tests
 *
 * Tests CRUD operations for service-to-service API keys:
 * create, list, rotate, revoke.
 */

import { describe, it, expect, beforeAll, afterAll, beforeEach } from 'vitest';
import type { FastifyInstance } from 'fastify';
import { InMemoryApiKeyStore } from '../../../persistence/api-key-store.js';
import { registerServiceKeyRoutes } from '../service-keys.js';

describe('Admin Service Key Routes', () => {
  let app: FastifyInstance;
  let apiKeyStore: InMemoryApiKeyStore;

  beforeAll(async () => {
    const Fastify = (await import('fastify')).default;
    app = Fastify({ logger: false });

    apiKeyStore = new InMemoryApiKeyStore();

    // Simulate auth: decorate request with authUser for requirePermission
    app.decorateRequest('authUser', null);
    app.addHook('onRequest', async (request) => {
      const roleHeader = request.headers['x-test-role'] as string | undefined;
      if (roleHeader) {
        (request as unknown as { authUser: { id: string; role: string; username: string } }).authUser = {
          id: 'admin-user-1',
          role: roleHeader,
          username: 'test-admin',
        };
      }
    });

    await registerServiceKeyRoutes(app, { apiKeyStore });
    await app.ready();
  });

  afterAll(async () => {
    await app.close();
  });

  // ----------------------------------------------------------------
  // POST /api/admin/service-keys — Create
  // ----------------------------------------------------------------

  describe('POST /api/admin/service-keys', () => {
    it('creates a service key and returns raw key', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/admin/service-keys',
        headers: { 'x-test-role': 'owner', 'content-type': 'application/json' },
        payload: {
          serviceName: 'elsa-server',
          label: 'ELSA workflow engine',
          permissions: ['prompts:read', 'diagnostics:write'],
        },
      });

      expect(res.statusCode).toBe(201);
      const body = JSON.parse(res.body);
      expect(body.id).toBeDefined();
      expect(body.serviceName).toBe('elsa-server');
      expect(body.label).toBe('ELSA workflow engine');
      expect(body.permissions).toEqual(['prompts:read', 'diagnostics:write']);
      expect(body.rawKey).toBeDefined();
      expect(body.rawKey).toMatch(/^tamma_sk_/);
      expect(body.warning).toContain('cannot be retrieved again');
    });

    it('returns 400 when serviceName is missing', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/admin/service-keys',
        headers: { 'x-test-role': 'owner', 'content-type': 'application/json' },
        payload: { label: 'test' },
      });

      expect(res.statusCode).toBe(400);
    });

    it('returns 403 for non-owner role', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/admin/service-keys',
        headers: { 'x-test-role': 'admin', 'content-type': 'application/json' },
        payload: { serviceName: 'test-svc' },
      });

      expect(res.statusCode).toBe(403);
    });

    it('returns 401 when unauthenticated', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/admin/service-keys',
        headers: { 'content-type': 'application/json' },
        payload: { serviceName: 'test-svc' },
      });

      expect(res.statusCode).toBe(401);
    });
  });

  // ----------------------------------------------------------------
  // GET /api/admin/service-keys — List
  // ----------------------------------------------------------------

  describe('GET /api/admin/service-keys', () => {
    it('lists service keys without raw keys', async () => {
      // Create a key first
      await app.inject({
        method: 'POST',
        url: '/api/admin/service-keys',
        headers: { 'x-test-role': 'owner', 'content-type': 'application/json' },
        payload: { serviceName: 'test-list-svc', permissions: ['prompts:read'] },
      });

      const res = await app.inject({
        method: 'GET',
        url: '/api/admin/service-keys',
        headers: { 'x-test-role': 'owner' },
      });

      expect(res.statusCode).toBe(200);
      const body = JSON.parse(res.body);
      expect(Array.isArray(body)).toBe(true);
      expect(body.length).toBeGreaterThan(0);

      // Verify no raw keys are returned
      for (const key of body) {
        expect(key.rawKey).toBeUndefined();
        expect(key.keyHash).toBeUndefined();
        expect(key.id).toBeDefined();
        expect(key.serviceName).toBeDefined();
        expect(key.keyPrefix).toBeDefined();
      }
    });
  });

  // ----------------------------------------------------------------
  // POST /api/admin/service-keys/:id/rotate — Rotate
  // ----------------------------------------------------------------

  describe('POST /api/admin/service-keys/:id/rotate', () => {
    it('rotates a service key and returns new raw key', async () => {
      // Create a key
      const createRes = await app.inject({
        method: 'POST',
        url: '/api/admin/service-keys',
        headers: { 'x-test-role': 'owner', 'content-type': 'application/json' },
        payload: { serviceName: 'rotate-svc', permissions: ['prompts:read'] },
      });
      const created = JSON.parse(createRes.body);

      // Rotate
      const rotateRes = await app.inject({
        method: 'POST',
        url: `/api/admin/service-keys/${created.id}/rotate`,
        headers: { 'x-test-role': 'owner' },
      });

      expect(rotateRes.statusCode).toBe(200);
      const rotated = JSON.parse(rotateRes.body);
      expect(rotated.rawKey).toBeDefined();
      expect(rotated.rawKey).toMatch(/^tamma_sk_/);
      expect(rotated.id).not.toBe(created.id);
      expect(rotated.rotatedFrom).toBe(created.id);
      expect(rotated.warning).toContain('24h');
    });

    it('returns 404 for unknown key ID', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/admin/service-keys/nonexistent-id/rotate',
        headers: { 'x-test-role': 'owner' },
      });

      expect(res.statusCode).toBe(404);
    });
  });

  // ----------------------------------------------------------------
  // DELETE /api/admin/service-keys/:id — Revoke
  // ----------------------------------------------------------------

  describe('DELETE /api/admin/service-keys/:id', () => {
    it('revokes a service key immediately', async () => {
      // Create a key
      const createRes = await app.inject({
        method: 'POST',
        url: '/api/admin/service-keys',
        headers: { 'x-test-role': 'owner', 'content-type': 'application/json' },
        payload: { serviceName: 'revoke-svc' },
      });
      const created = JSON.parse(createRes.body);

      // Revoke
      const deleteRes = await app.inject({
        method: 'DELETE',
        url: `/api/admin/service-keys/${created.id}`,
        headers: { 'x-test-role': 'owner' },
      });

      expect(deleteRes.statusCode).toBe(204);
    });

    it('returns 404 for unknown key ID', async () => {
      const res = await app.inject({
        method: 'DELETE',
        url: '/api/admin/service-keys/nonexistent-id',
        headers: { 'x-test-role': 'owner' },
      });

      expect(res.statusCode).toBe(404);
    });
  });
});
