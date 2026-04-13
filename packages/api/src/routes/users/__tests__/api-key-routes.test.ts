/**
 * Per-User API Key Routes Tests
 *
 * Tests API key lifecycle:
 *   POST   /api/admin/users/:id/keys
 *   GET    /api/admin/users/:id/keys
 *   DELETE /api/admin/users/:id/keys/:keyId
 */

import { describe, it, expect, beforeEach } from 'vitest';
import Fastify from 'fastify';
import type { FastifyInstance } from 'fastify';
import { Writable } from 'node:stream';
import { registerApiKeyRoutes } from '../api-key-routes.js';
import { InMemoryUserStore } from '../../../persistence/user-store.js';
import { InMemoryUserApiKeyStore } from '../../../persistence/user-api-key-store.js';
import type { User } from '../../../persistence/user-store.js';

/** Collects structured log lines for audit verification. */
function createLogCollector(): { stream: Writable; lines: Record<string, unknown>[] } {
  const lines: Record<string, unknown>[] = [];
  const stream = new Writable({
    write(chunk: Buffer, _encoding: string, callback: () => void) {
      try {
        lines.push(JSON.parse(chunk.toString()) as Record<string, unknown>);
      } catch {
        // ignore non-JSON lines
      }
      callback();
    },
  });
  return { stream, lines };
}

function createTestApp(authUser: { id: string; role: string } | null = null, logStream?: Writable) {
  const appOptions: Record<string, unknown> = {};
  if (logStream) {
    appOptions['logger'] = { stream: logStream, level: 'info' };
  }
  const app = Fastify(appOptions);
  const userStore = new InMemoryUserStore();
  const apiKeyStore = new InMemoryUserApiKeyStore();

  app.decorateRequest('authUser', null);

  if (authUser) {
    app.addHook('onRequest', async (request) => {
      (request as unknown as { authUser: typeof authUser }).authUser = authUser;
    });
  }

  return { app, userStore, apiKeyStore };
}

async function setupRoutes(
  app: FastifyInstance,
  userStore: InstanceType<typeof InMemoryUserStore>,
  apiKeyStore: InstanceType<typeof InMemoryUserApiKeyStore>,
) {
  await registerApiKeyRoutes(app, { userStore, apiKeyStore });
  await app.ready();
}

describe('API Key Routes', () => {
  let app: FastifyInstance;
  let userStore: InstanceType<typeof InMemoryUserStore>;
  let apiKeyStore: InstanceType<typeof InMemoryUserApiKeyStore>;
  let testUser: User;

  beforeEach(async () => {
    const ctx = createTestApp({ id: 'admin-1', role: 'admin' });
    app = ctx.app;
    userStore = ctx.userStore;
    apiKeyStore = ctx.apiKeyStore;
    testUser = await userStore.upsertUser({
      githubId: 1001,
      githubLogin: 'test-user',
      email: null,
      role: 'member',
    });
    await setupRoutes(app, userStore, apiKeyStore);
  });

  describe('POST /api/admin/users/:id/keys', () => {
    it('creates a new API key and returns the full key once', async () => {
      const res = await app.inject({
        method: 'POST',
        url: `/api/admin/users/${testUser.id}/keys`,
        payload: { label: 'test-key' },
      });

      expect(res.statusCode).toBe(201);
      const body = res.json();
      expect(body.id).toBeDefined();
      expect(body.key).toMatch(/^tamma_sk_/);
      expect(body.prefix).toBeDefined();
      expect(body.label).toBe('test-key');
      expect(body.createdAt).toBeDefined();
    });

    it('uses default label when none provided', async () => {
      const res = await app.inject({
        method: 'POST',
        url: `/api/admin/users/${testUser.id}/keys`,
        payload: {},
      });

      expect(res.statusCode).toBe(201);
      expect(res.json().label).toBe('default');
    });

    it('returns 404 for nonexistent user', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/admin/users/nonexistent/keys',
        payload: { label: 'key' },
      });

      expect(res.statusCode).toBe(404);
    });
  });

  describe('GET /api/admin/users/:id/keys', () => {
    it('lists API keys without the full key', async () => {
      // Create two keys
      await app.inject({
        method: 'POST',
        url: `/api/admin/users/${testUser.id}/keys`,
        payload: { label: 'key-1' },
      });
      await app.inject({
        method: 'POST',
        url: `/api/admin/users/${testUser.id}/keys`,
        payload: { label: 'key-2' },
      });

      const res = await app.inject({
        method: 'GET',
        url: `/api/admin/users/${testUser.id}/keys`,
      });

      expect(res.statusCode).toBe(200);
      const body = res.json();
      expect(body.apiKeys).toHaveLength(2);

      // Ensure full key is NOT exposed
      for (const key of body.apiKeys) {
        expect(key.keyPrefix).toBeDefined();
        expect(key).not.toHaveProperty('key');
        expect(key).not.toHaveProperty('keyHash');
      }
    });

    it('returns empty array for user with no keys', async () => {
      const res = await app.inject({
        method: 'GET',
        url: `/api/admin/users/${testUser.id}/keys`,
      });

      expect(res.statusCode).toBe(200);
      expect(res.json().apiKeys).toEqual([]);
    });
  });

  describe('DELETE /api/admin/users/:id/keys/:keyId', () => {
    it('revokes an API key', async () => {
      // Create a key
      const createRes = await app.inject({
        method: 'POST',
        url: `/api/admin/users/${testUser.id}/keys`,
        payload: { label: 'to-revoke' },
      });
      const keyId = createRes.json().id;

      // Revoke it
      const revokeRes = await app.inject({
        method: 'DELETE',
        url: `/api/admin/users/${testUser.id}/keys/${keyId}`,
      });
      expect(revokeRes.statusCode).toBe(200);
      expect(revokeRes.json().ok).toBe(true);

      // Verify it's gone from the list
      const listRes = await app.inject({
        method: 'GET',
        url: `/api/admin/users/${testUser.id}/keys`,
      });
      expect(listRes.json().apiKeys).toHaveLength(0);
    });

    it('returns 404 for nonexistent key', async () => {
      const res = await app.inject({
        method: 'DELETE',
        url: `/api/admin/users/${testUser.id}/keys/nonexistent`,
      });
      expect(res.statusCode).toBe(404);
    });
  });

  describe('Self-access for members', () => {
    it('member can create their own API key', async () => {
      const { app: memberApp, userStore: mStore, apiKeyStore: mAks } = createTestApp(null);
      const member = await mStore.upsertUser({
        githubId: 2002,
        githubLogin: 'member-user',
        email: null,
        role: 'member',
      });

      // Re-create with correct authUser
      const { app: app2, userStore: s2, apiKeyStore: aks2 } = createTestApp({ id: member.id, role: 'member' });
      // Copy the user into the new store
      await s2.upsertUser({ githubId: 2002, githubLogin: 'member-user', email: null, role: 'member' });
      const u = await s2.getUserByGithubId(2002);
      const { app: app3 } = createTestApp({ id: u!.id, role: 'member' });
      await registerApiKeyRoutes(app3, { userStore: s2, apiKeyStore: aks2 });
      await app3.ready();

      const res = await app3.inject({
        method: 'POST',
        url: `/api/admin/users/${u!.id}/keys`,
        payload: { label: 'my-key' },
      });
      expect(res.statusCode).toBe(201);
    });

    it('member cannot create API key for another user', async () => {
      const { app: app3, userStore: s2, apiKeyStore: aks2 } = createTestApp({ id: 'member-1', role: 'member' });
      const other = await s2.upsertUser({
        githubId: 3003,
        githubLogin: 'other-user',
        email: null,
        role: 'member',
      });
      await registerApiKeyRoutes(app3, { userStore: s2, apiKeyStore: aks2 });
      await app3.ready();

      const res = await app3.inject({
        method: 'POST',
        url: `/api/admin/users/${other.id}/keys`,
        payload: { label: 'stolen-key' },
      });
      expect(res.statusCode).toBe(403);
    });
  });

  describe('Audit logging', () => {
    it('emits USER.API_KEY_CREATED.SUCCESS on key creation', async () => {
      const { stream, lines } = createLogCollector();
      const { app: logApp, userStore: logStore, apiKeyStore: logAks } = createTestApp({ id: 'admin-1', role: 'admin' }, stream);
      const user = await logStore.upsertUser({
        githubId: 5001,
        githubLogin: 'audit-user',
        email: null,
        role: 'member',
      });
      await registerApiKeyRoutes(logApp, { userStore: logStore, apiKeyStore: logAks });
      await logApp.ready();

      await logApp.inject({
        method: 'POST',
        url: `/api/admin/users/${user.id}/keys`,
        payload: { label: 'audit-key' },
      });

      const auditLine = lines.find((l) => l['event'] === 'USER.API_KEY_CREATED.SUCCESS');
      expect(auditLine).toBeDefined();
      expect(auditLine!['targetUserId']).toBe(user.id);
      expect(auditLine!['keyPrefix']).toBeDefined();
      expect(auditLine!['label']).toBe('audit-key');
      expect(auditLine!['createdBy']).toBe('admin-1');
      // Must not log the full key
      expect(auditLine).not.toHaveProperty('key');
      expect(auditLine).not.toHaveProperty('rawKey');
    });

    it('emits USER.API_KEY_REVOKED.SUCCESS on key revocation', async () => {
      const { stream, lines } = createLogCollector();
      const { app: logApp, userStore: logStore, apiKeyStore: logAks } = createTestApp({ id: 'admin-1', role: 'admin' }, stream);
      const user = await logStore.upsertUser({
        githubId: 5002,
        githubLogin: 'revoke-user',
        email: null,
        role: 'member',
      });
      await registerApiKeyRoutes(logApp, { userStore: logStore, apiKeyStore: logAks });
      await logApp.ready();

      const createRes = await logApp.inject({
        method: 'POST',
        url: `/api/admin/users/${user.id}/keys`,
        payload: { label: 'to-revoke' },
      });
      const keyId = createRes.json().id;

      await logApp.inject({
        method: 'DELETE',
        url: `/api/admin/users/${user.id}/keys/${keyId}`,
      });

      const auditLine = lines.find((l) => l['event'] === 'USER.API_KEY_REVOKED.SUCCESS');
      expect(auditLine).toBeDefined();
      expect(auditLine!['targetUserId']).toBe(user.id);
      expect(auditLine!['keyId']).toBe(keyId);
      expect(auditLine!['revokedBy']).toBe('admin-1');
    });
  });
});
