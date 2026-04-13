/**
 * User Management Routes Tests
 *
 * Tests user CRUD operations:
 *   GET    /api/admin/users
 *   GET    /api/admin/users/:id
 *   PUT    /api/admin/users/:id/role
 *   DELETE /api/admin/users/:id
 */

import { describe, it, expect, beforeEach } from 'vitest';
import Fastify from 'fastify';
import type { FastifyInstance } from 'fastify';
import { Writable } from 'node:stream';
import { registerUserRoutes } from '../user-routes.js';
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

/**
 * Helper to inject an auth user into the request via the authUser decoration.
 * In production this is done by the JWT auth plugin; in tests we simulate it.
 */
function createTestApp(authUser: { id: string; role: string } | null = null, logStream?: Writable) {
  const appOptions: Record<string, unknown> = {};
  if (logStream) {
    appOptions['logger'] = { stream: logStream, level: 'info' };
  }
  const app = Fastify(appOptions);
  const userStore = new InMemoryUserStore();
  const apiKeyStore = new InMemoryUserApiKeyStore();

  // Decorate request with authUser so require-role middleware can read it
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
  await registerUserRoutes(app, { userStore, apiKeyStore });
  await app.ready();
}

async function createUser(
  store: InstanceType<typeof InMemoryUserStore>,
  overrides: Partial<{ githubId: number; githubLogin: string; email: string | null; role: 'owner' | 'admin' | 'member' }> = {},
): Promise<User> {
  return store.upsertUser({
    githubId: overrides.githubId ?? Math.floor(Math.random() * 100000),
    githubLogin: overrides.githubLogin ?? `user-${Date.now()}`,
    email: overrides.email ?? null,
    role: overrides.role ?? 'member',
  });
}

describe('User Management Routes', () => {
  describe('GET /api/admin/users', () => {
    it('returns paginated list for admin', async () => {
      const { app, userStore, apiKeyStore } = createTestApp({ id: 'admin-1', role: 'admin' });
      await createUser(userStore, { githubId: 1, githubLogin: 'alice', role: 'member' });
      await createUser(userStore, { githubId: 2, githubLogin: 'bob', role: 'admin' });
      await setupRoutes(app, userStore, apiKeyStore);

      const res = await app.inject({ method: 'GET', url: '/api/admin/users' });
      expect(res.statusCode).toBe(200);
      const body = res.json();
      expect(body.users).toHaveLength(2);
      expect(body.total).toBe(2);
    });

    it('returns 403 for member', async () => {
      const { app, userStore, apiKeyStore } = createTestApp({ id: 'member-1', role: 'member' });
      await setupRoutes(app, userStore, apiKeyStore);

      const res = await app.inject({ method: 'GET', url: '/api/admin/users' });
      expect(res.statusCode).toBe(403);
    });

    it('returns 401 for unauthenticated', async () => {
      const { app, userStore, apiKeyStore } = createTestApp(null);
      await setupRoutes(app, userStore, apiKeyStore);

      const res = await app.inject({ method: 'GET', url: '/api/admin/users' });
      expect(res.statusCode).toBe(401);
    });

    it('filters by role', async () => {
      const { app, userStore, apiKeyStore } = createTestApp({ id: 'admin-1', role: 'admin' });
      await createUser(userStore, { githubId: 1, role: 'member' });
      await createUser(userStore, { githubId: 2, role: 'admin' });
      await setupRoutes(app, userStore, apiKeyStore);

      const res = await app.inject({ method: 'GET', url: '/api/admin/users?role=admin' });
      expect(res.statusCode).toBe(200);
      const body = res.json();
      expect(body.users).toHaveLength(1);
      expect(body.users[0].role).toBe('admin');
    });

    it('respects pagination params', async () => {
      const { app, userStore, apiKeyStore } = createTestApp({ id: 'admin-1', role: 'admin' });
      for (let i = 0; i < 5; i++) {
        await createUser(userStore, { githubId: 100 + i });
      }
      await setupRoutes(app, userStore, apiKeyStore);

      const res = await app.inject({ method: 'GET', url: '/api/admin/users?limit=2&offset=1' });
      expect(res.statusCode).toBe(200);
      const body = res.json();
      expect(body.users).toHaveLength(2);
      expect(body.total).toBe(5);
    });
  });

  describe('GET /api/admin/users/:id', () => {
    it('returns user detail for admin', async () => {
      const { app, userStore, apiKeyStore } = createTestApp({ id: 'admin-1', role: 'admin' });
      const user = await createUser(userStore, { githubId: 1, githubLogin: 'alice' });
      await setupRoutes(app, userStore, apiKeyStore);

      const res = await app.inject({ method: 'GET', url: `/api/admin/users/${user.id}` });
      expect(res.statusCode).toBe(200);
      const body = res.json();
      expect(body.user.githubLogin).toBe('alice');
      expect(body.installations).toEqual([]);
      expect(body.apiKeys).toEqual([]);
    });

    it('allows member to get their own user', async () => {
      const { app, userStore, apiKeyStore } = createTestApp({ id: 'will-be-overridden', role: 'member' });
      const user = await createUser(userStore, { githubId: 1, githubLogin: 'me' });

      // Re-create app with the correct user ID
      const { app: app2, userStore: store2, apiKeyStore: apiKeyStore2 } = createTestApp({ id: user.id, role: 'member' });
      await store2.upsertUser({ githubId: 1, githubLogin: 'me', email: null, role: 'member' });
      const user2 = await store2.getUserByGithubId(1);
      const { app: app3, userStore: _s, apiKeyStore: aks } = createTestApp({ id: user2!.id, role: 'member' });
      await setupRoutes(app3, store2, apiKeyStore2);

      const res = await app3.inject({ method: 'GET', url: `/api/admin/users/${user2!.id}` });
      expect(res.statusCode).toBe(200);
    });

    it('blocks member from viewing another user', async () => {
      const { app, userStore, apiKeyStore } = createTestApp({ id: 'member-1', role: 'member' });
      const other = await createUser(userStore, { githubId: 2, githubLogin: 'other' });
      await setupRoutes(app, userStore, apiKeyStore);

      const res = await app.inject({ method: 'GET', url: `/api/admin/users/${other.id}` });
      expect(res.statusCode).toBe(403);
    });

    it('returns 404 for nonexistent user', async () => {
      const { app, userStore, apiKeyStore } = createTestApp({ id: 'admin-1', role: 'admin' });
      await setupRoutes(app, userStore, apiKeyStore);

      const res = await app.inject({ method: 'GET', url: '/api/admin/users/nonexistent' });
      expect(res.statusCode).toBe(404);
    });
  });

  describe('PUT /api/admin/users/:id/role', () => {
    let app: FastifyInstance;
    let userStore: InstanceType<typeof InMemoryUserStore>;
    let apiKeyStore: InstanceType<typeof InMemoryUserApiKeyStore>;
    let targetUser: User;

    beforeEach(async () => {
      const ctx = createTestApp({ id: 'owner-1', role: 'owner' });
      app = ctx.app;
      userStore = ctx.userStore;
      apiKeyStore = ctx.apiKeyStore;
      targetUser = await createUser(userStore, { githubId: 1, githubLogin: 'target', role: 'member' });
      await setupRoutes(app, userStore, apiKeyStore);
    });

    it('owner can change role to admin', async () => {
      const res = await app.inject({
        method: 'PUT',
        url: `/api/admin/users/${targetUser.id}/role`,
        payload: { role: 'admin' },
      });
      expect(res.statusCode).toBe(200);
      expect(res.json().user.role).toBe('admin');
    });

    it('admin cannot promote to owner', async () => {
      const { app: adminApp, userStore: store, apiKeyStore: aks } = createTestApp({ id: 'admin-1', role: 'admin' });
      const user = await createUser(store, { githubId: 1, role: 'member' });
      await setupRoutes(adminApp, store, aks);

      const res = await adminApp.inject({
        method: 'PUT',
        url: `/api/admin/users/${user.id}/role`,
        payload: { role: 'owner' },
      });
      expect(res.statusCode).toBe(403);
    });

    it('prevents self-role-change', async () => {
      const res = await app.inject({
        method: 'PUT',
        url: '/api/admin/users/owner-1/role',
        payload: { role: 'member' },
      });
      expect(res.statusCode).toBe(400);
      expect(res.json().error).toContain('Cannot change your own role');
    });

    it('returns 400 for invalid role', async () => {
      const res = await app.inject({
        method: 'PUT',
        url: `/api/admin/users/${targetUser.id}/role`,
        payload: { role: 'superadmin' },
      });
      expect(res.statusCode).toBe(400);
    });

    it('returns 404 for nonexistent user', async () => {
      const res = await app.inject({
        method: 'PUT',
        url: '/api/admin/users/nonexistent/role',
        payload: { role: 'admin' },
      });
      expect(res.statusCode).toBe(404);
    });
  });

  describe('DELETE /api/admin/users/:id', () => {
    it('owner can soft-delete a user', async () => {
      const { app, userStore, apiKeyStore } = createTestApp({ id: 'owner-1', role: 'owner' });
      const target = await createUser(userStore, { githubId: 1, role: 'member' });
      await setupRoutes(app, userStore, apiKeyStore);

      const res = await app.inject({ method: 'DELETE', url: `/api/admin/users/${target.id}` });
      expect(res.statusCode).toBe(200);
      expect(res.json().ok).toBe(true);

      // Verify user is no longer listed
      const listRes = await userStore.listUsers({ limit: 10, offset: 0 });
      expect(listRes.users).toHaveLength(0);
    });

    it('admin cannot delete users (403)', async () => {
      const { app, userStore, apiKeyStore } = createTestApp({ id: 'admin-1', role: 'admin' });
      const target = await createUser(userStore, { githubId: 1, role: 'member' });
      await setupRoutes(app, userStore, apiKeyStore);

      const res = await app.inject({ method: 'DELETE', url: `/api/admin/users/${target.id}` });
      expect(res.statusCode).toBe(403);
    });

    it('prevents self-deletion', async () => {
      const { app, userStore, apiKeyStore } = createTestApp({ id: 'owner-1', role: 'owner' });
      await setupRoutes(app, userStore, apiKeyStore);

      const res = await app.inject({ method: 'DELETE', url: '/api/admin/users/owner-1' });
      expect(res.statusCode).toBe(400);
      expect(res.json().error).toContain('Cannot delete yourself');
    });

    it('returns 404 for nonexistent user', async () => {
      const { app, userStore, apiKeyStore } = createTestApp({ id: 'owner-1', role: 'owner' });
      await setupRoutes(app, userStore, apiKeyStore);

      const res = await app.inject({ method: 'DELETE', url: '/api/admin/users/nonexistent' });
      expect(res.statusCode).toBe(404);
    });

    it('removes installation links on soft delete', async () => {
      const { app, userStore, apiKeyStore } = createTestApp({ id: 'owner-1', role: 'owner' });
      const target = await createUser(userStore, { githubId: 1, role: 'member' });
      await userStore.linkUserToInstallation(target.id, 12345, 'member');
      await userStore.linkUserToInstallation(target.id, 67890, 'member');
      await setupRoutes(app, userStore, apiKeyStore);

      // Verify links exist before delete
      const beforeLinks = await userStore.getUserInstallations(target.id);
      expect(beforeLinks).toHaveLength(2);

      const res = await app.inject({ method: 'DELETE', url: `/api/admin/users/${target.id}` });
      expect(res.statusCode).toBe(200);

      // Verify links removed after delete
      const afterLinks = await userStore.getUserInstallations(target.id);
      expect(afterLinks).toHaveLength(0);
    });
  });

  describe('Audit logging', () => {
    it('emits USER.ROLE_CHANGED.SUCCESS on role change', async () => {
      const { stream, lines } = createLogCollector();
      const { app, userStore, apiKeyStore } = createTestApp({ id: 'owner-1', role: 'owner' }, stream);
      const target = await createUser(userStore, { githubId: 1, githubLogin: 'target', role: 'member' });
      await setupRoutes(app, userStore, apiKeyStore);

      await app.inject({
        method: 'PUT',
        url: `/api/admin/users/${target.id}/role`,
        payload: { role: 'admin' },
      });

      const auditLine = lines.find((l) => l['event'] === 'USER.ROLE_CHANGED.SUCCESS');
      expect(auditLine).toBeDefined();
      expect(auditLine!['targetUserId']).toBe(target.id);
      expect(auditLine!['oldRole']).toBe('member');
      expect(auditLine!['newRole']).toBe('admin');
      expect(auditLine!['changedBy']).toBe('owner-1');
    });

    it('emits USER.DELETED.SUCCESS on soft delete', async () => {
      const { stream, lines } = createLogCollector();
      const { app, userStore, apiKeyStore } = createTestApp({ id: 'owner-1', role: 'owner' }, stream);
      const target = await createUser(userStore, { githubId: 1, role: 'member' });
      await setupRoutes(app, userStore, apiKeyStore);

      await app.inject({ method: 'DELETE', url: `/api/admin/users/${target.id}` });

      const auditLine = lines.find((l) => l['event'] === 'USER.DELETED.SUCCESS');
      expect(auditLine).toBeDefined();
      expect(auditLine!['targetUserId']).toBe(target.id);
      expect(auditLine!['deletedBy']).toBe('owner-1');
    });
  });

  describe('lastActiveAt field', () => {
    it('returns lastActiveAt in user response', async () => {
      const { app, userStore, apiKeyStore } = createTestApp({ id: 'admin-1', role: 'admin' });
      const user = await createUser(userStore, { githubId: 1, githubLogin: 'alice' });
      await setupRoutes(app, userStore, apiKeyStore);

      const res = await app.inject({ method: 'GET', url: `/api/admin/users/${user.id}` });
      expect(res.statusCode).toBe(200);
      const body = res.json();
      expect(body.user).toHaveProperty('lastActiveAt');
      expect(body.user.lastActiveAt).toBeNull();
    });

    it('returns lastActiveAt after update', async () => {
      const { app, userStore, apiKeyStore } = createTestApp({ id: 'admin-1', role: 'admin' });
      const user = await createUser(userStore, { githubId: 1, githubLogin: 'alice' });
      await userStore.updateLastActive(user.id);
      await setupRoutes(app, userStore, apiKeyStore);

      const res = await app.inject({ method: 'GET', url: `/api/admin/users/${user.id}` });
      expect(res.statusCode).toBe(200);
      const body = res.json();
      expect(body.user.lastActiveAt).toMatch(/^\d{4}-\d{2}-\d{2}T/);
    });
  });
});
