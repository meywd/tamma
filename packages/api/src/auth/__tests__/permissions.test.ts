/**
 * RBAC Permission Matrix & Middleware Tests
 */

import { describe, it, expect, beforeAll, afterAll, vi } from 'vitest';
import {
  hasPermission,
  getRolePermissions,
  isValidRole,
  PERMISSIONS,
} from '../permissions.js';
import type { Role, Permission } from '../permissions.js';
import { requirePermission } from '../require-permission.js';
import type { FastifyInstance } from 'fastify';

// ================================================================
// Permission Matrix Tests
// ================================================================

describe('Permission Matrix', () => {
  describe('hasPermission()', () => {
    // ---- member permissions ----
    it('member can view dashboard', () => {
      expect(hasPermission('member', 'dashboard:view')).toBe(true);
    });

    it('member can view workflows', () => {
      expect(hasPermission('member', 'workflows:view')).toBe(true);
    });

    it('member cannot manage workflows', () => {
      expect(hasPermission('member', 'workflows:manage')).toBe(false);
    });

    it('member cannot delete workflows', () => {
      expect(hasPermission('member', 'workflows:delete')).toBe(false);
    });

    it('member cannot view users', () => {
      expect(hasPermission('member', 'users:view')).toBe(false);
    });

    it('member cannot manage users', () => {
      expect(hasPermission('member', 'users:manage')).toBe(false);
    });

    it('member cannot access admin panel', () => {
      expect(hasPermission('member', 'admin:access')).toBe(false);
    });

    it('member cannot access ELSA Studio', () => {
      expect(hasPermission('member', 'elsa:access')).toBe(false);
    });

    it('member cannot access logs', () => {
      expect(hasPermission('member', 'logs:access')).toBe(false);
    });

    it('member cannot view settings', () => {
      expect(hasPermission('member', 'settings:view')).toBe(false);
    });

    it('member cannot manage settings', () => {
      expect(hasPermission('member', 'settings:manage')).toBe(false);
    });

    it('member cannot manage API keys', () => {
      expect(hasPermission('member', 'apikeys:manage')).toBe(false);
    });

    // ---- admin permissions ----
    it('admin can view dashboard', () => {
      expect(hasPermission('admin', 'dashboard:view')).toBe(true);
    });

    it('admin can view workflows', () => {
      expect(hasPermission('admin', 'workflows:view')).toBe(true);
    });

    it('admin can manage workflows', () => {
      expect(hasPermission('admin', 'workflows:manage')).toBe(true);
    });

    it('admin cannot delete workflows', () => {
      expect(hasPermission('admin', 'workflows:delete')).toBe(false);
    });

    it('admin can view users', () => {
      expect(hasPermission('admin', 'users:view')).toBe(true);
    });

    it('admin cannot manage users', () => {
      expect(hasPermission('admin', 'users:manage')).toBe(false);
    });

    it('admin can access admin panel', () => {
      expect(hasPermission('admin', 'admin:access')).toBe(true);
    });

    it('admin can access ELSA Studio', () => {
      expect(hasPermission('admin', 'elsa:access')).toBe(true);
    });

    it('admin can access logs', () => {
      expect(hasPermission('admin', 'logs:access')).toBe(true);
    });

    it('admin can view settings', () => {
      expect(hasPermission('admin', 'settings:view')).toBe(true);
    });

    it('admin cannot manage settings', () => {
      expect(hasPermission('admin', 'settings:manage')).toBe(false);
    });

    it('admin can manage API keys', () => {
      expect(hasPermission('admin', 'apikeys:manage')).toBe(true);
    });

    // ---- owner permissions ----
    it('owner can view dashboard', () => {
      expect(hasPermission('owner', 'dashboard:view')).toBe(true);
    });

    it('owner can manage workflows', () => {
      expect(hasPermission('owner', 'workflows:manage')).toBe(true);
    });

    it('owner can delete workflows', () => {
      expect(hasPermission('owner', 'workflows:delete')).toBe(true);
    });

    it('owner can view users', () => {
      expect(hasPermission('owner', 'users:view')).toBe(true);
    });

    it('owner can manage users', () => {
      expect(hasPermission('owner', 'users:manage')).toBe(true);
    });

    it('owner can access admin panel', () => {
      expect(hasPermission('owner', 'admin:access')).toBe(true);
    });

    it('owner can access ELSA Studio', () => {
      expect(hasPermission('owner', 'elsa:access')).toBe(true);
    });

    it('owner can access logs', () => {
      expect(hasPermission('owner', 'logs:access')).toBe(true);
    });

    it('owner can view settings', () => {
      expect(hasPermission('owner', 'settings:view')).toBe(true);
    });

    it('owner can manage settings', () => {
      expect(hasPermission('owner', 'settings:manage')).toBe(true);
    });

    it('owner can manage API keys', () => {
      expect(hasPermission('owner', 'apikeys:manage')).toBe(true);
    });
  });

  describe('getRolePermissions()', () => {
    it('returns correct subset for member', () => {
      const perms = getRolePermissions('member');
      expect(perms).toContain('dashboard:view');
      expect(perms).toContain('workflows:view');
      expect(perms).not.toContain('users:view');
      expect(perms).not.toContain('admin:access');
      expect(perms).not.toContain('elsa:access');
      expect(perms).toHaveLength(2);
    });

    it('returns correct subset for admin', () => {
      const perms = getRolePermissions('admin');
      expect(perms).toContain('dashboard:view');
      expect(perms).toContain('workflows:view');
      expect(perms).toContain('workflows:manage');
      expect(perms).toContain('users:view');
      expect(perms).toContain('admin:access');
      expect(perms).toContain('elsa:access');
      expect(perms).toContain('logs:access');
      expect(perms).toContain('settings:view');
      expect(perms).toContain('apikeys:manage');
      expect(perms).not.toContain('workflows:delete');
      expect(perms).not.toContain('users:manage');
      expect(perms).not.toContain('settings:manage');
    });

    it('returns all permissions for owner', () => {
      const perms = getRolePermissions('owner');
      const allPermissions = Object.keys(PERMISSIONS) as Permission[];
      expect(perms).toHaveLength(allPermissions.length);
      for (const p of allPermissions) {
        expect(perms).toContain(p);
      }
    });
  });

  describe('isValidRole()', () => {
    it('recognizes valid roles', () => {
      expect(isValidRole('member')).toBe(true);
      expect(isValidRole('admin')).toBe(true);
      expect(isValidRole('owner')).toBe(true);
    });

    it('rejects invalid roles', () => {
      expect(isValidRole('superadmin')).toBe(false);
      expect(isValidRole('')).toBe(false);
      expect(isValidRole('viewer')).toBe(false);
      expect(isValidRole('operator')).toBe(false);
    });
  });
});

// ================================================================
// requirePermission Middleware Tests
// ================================================================

describe('requirePermission middleware', () => {
  let app: FastifyInstance;

  beforeAll(async () => {
    const Fastify = (await import('fastify')).default;
    app = Fastify({ logger: false });

    // Decorate request with authUser (simulates what the auth plugin does)
    app.decorateRequest('authUser', null);

    // Hook to set authUser from a custom header for testing
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

    // Test routes with RBAC
    app.get('/test/dashboard', {
      preHandler: [requirePermission('dashboard:view')],
    }, async () => ({ ok: true }));

    app.get('/test/users', {
      preHandler: [requirePermission('users:view')],
    }, async () => ({ ok: true }));

    app.put('/test/settings', {
      preHandler: [requirePermission('settings:manage')],
    }, async () => ({ ok: true }));

    app.get('/test/elsa', {
      preHandler: [requirePermission('elsa:access')],
    }, async () => ({ ok: true }));

    app.delete('/test/workflows', {
      preHandler: [requirePermission('workflows:delete')],
    }, async () => ({ ok: true }));

    await app.ready();
  });

  afterAll(async () => {
    await app.close();
  });

  // Unauthenticated
  it('returns 401 for unauthenticated requests', async () => {
    const res = await app.inject({
      method: 'GET',
      url: '/test/dashboard',
    });
    expect(res.statusCode).toBe(401);
    const body = JSON.parse(res.body);
    expect(body.error).toBe('Not authenticated');
  });

  // member accessing member-level resource
  it('allows member to view dashboard', async () => {
    const res = await app.inject({
      method: 'GET',
      url: '/test/dashboard',
      headers: { 'x-test-role': 'member', 'x-test-user-id': 'user-1' },
    });
    expect(res.statusCode).toBe(200);
  });

  // member blocked from admin-level resource
  it('blocks member from viewing users', async () => {
    const res = await app.inject({
      method: 'GET',
      url: '/test/users',
      headers: { 'x-test-role': 'member', 'x-test-user-id': 'user-1' },
    });
    expect(res.statusCode).toBe(403);
    const body = JSON.parse(res.body);
    expect(body.error).toBe('Insufficient permissions');
    expect(body.required).toBe('users:view');
  });

  // admin accessing admin-level resource
  it('allows admin to view users', async () => {
    const res = await app.inject({
      method: 'GET',
      url: '/test/users',
      headers: { 'x-test-role': 'admin', 'x-test-user-id': 'user-2' },
    });
    expect(res.statusCode).toBe(200);
  });

  // admin blocked from owner-level resource
  it('blocks admin from managing settings', async () => {
    const res = await app.inject({
      method: 'PUT',
      url: '/test/settings',
      headers: { 'x-test-role': 'admin', 'x-test-user-id': 'user-2' },
    });
    expect(res.statusCode).toBe(403);
    const body = JSON.parse(res.body);
    expect(body.required).toBe('settings:manage');
  });

  // owner has full access
  it('allows owner to manage settings', async () => {
    const res = await app.inject({
      method: 'PUT',
      url: '/test/settings',
      headers: { 'x-test-role': 'owner', 'x-test-user-id': 'user-3' },
    });
    expect(res.statusCode).toBe(200);
  });

  it('allows owner to delete workflows', async () => {
    const res = await app.inject({
      method: 'DELETE',
      url: '/test/workflows',
      headers: { 'x-test-role': 'owner', 'x-test-user-id': 'user-3' },
    });
    expect(res.statusCode).toBe(200);
  });

  it('blocks admin from deleting workflows', async () => {
    const res = await app.inject({
      method: 'DELETE',
      url: '/test/workflows',
      headers: { 'x-test-role': 'admin', 'x-test-user-id': 'user-2' },
    });
    expect(res.statusCode).toBe(403);
  });

  // admin accessing ELSA
  it('allows admin to access ELSA', async () => {
    const res = await app.inject({
      method: 'GET',
      url: '/test/elsa',
      headers: { 'x-test-role': 'admin', 'x-test-user-id': 'user-2' },
    });
    expect(res.statusCode).toBe(200);
  });

  // member blocked from ELSA
  it('blocks member from accessing ELSA', async () => {
    const res = await app.inject({
      method: 'GET',
      url: '/test/elsa',
      headers: { 'x-test-role': 'member', 'x-test-user-id': 'user-1' },
    });
    expect(res.statusCode).toBe(403);
  });

  // Invalid role string
  it('returns 403 for unrecognized role', async () => {
    const res = await app.inject({
      method: 'GET',
      url: '/test/dashboard',
      headers: { 'x-test-role': 'superadmin', 'x-test-user-id': 'user-x' },
    });
    expect(res.statusCode).toBe(403);
  });
});

// ================================================================
// Role Check Endpoint Tests
// ================================================================

describe('GET /api/auth/role-check', () => {
  let app: FastifyInstance;

  beforeAll(async () => {
    const Fastify = (await import('fastify')).default;
    app = Fastify({ logger: false });

    // Register JWT + cookie (needed by role-check)
    await app.register(await import('@fastify/jwt').then((m) => m.default ?? m), {
      secret: 'test-jwt-secret',
      cookie: { cookieName: 'tamma_session', signed: false },
    });
    await app.register(await import('@fastify/cookie').then((m) => m.default ?? m));

    // Register the role-check route
    const { registerRoleCheckRoute } = await import('../../routes/auth/role-check.js');
    await registerRoleCheckRoute(app);

    await app.ready();
  });

  afterAll(async () => {
    await app.close();
  });

  function signToken(payload: Record<string, unknown>): string {
    return app.jwt.sign(payload);
  }

  it('returns 400 when service param is missing', async () => {
    const res = await app.inject({
      method: 'GET',
      url: '/api/auth/role-check',
    });
    expect(res.statusCode).toBe(400);
  });

  it('returns 400 for unknown service', async () => {
    const token = signToken({ id: '1', username: 'user', githubId: 123, role: 'admin' });
    const res = await app.inject({
      method: 'GET',
      url: '/api/auth/role-check?service=unknown',
      cookies: { tamma_session: token },
    });
    expect(res.statusCode).toBe(400);
    const body = JSON.parse(res.body);
    expect(body.error).toContain('Unknown service');
  });

  it('returns 401 when no session cookie', async () => {
    const res = await app.inject({
      method: 'GET',
      url: '/api/auth/role-check?service=elsa',
    });
    expect(res.statusCode).toBe(401);
  });

  it('returns 200 for admin accessing elsa', async () => {
    const token = signToken({ id: '1', username: 'admin-user', githubId: 100, role: 'admin' });
    const res = await app.inject({
      method: 'GET',
      url: '/api/auth/role-check?service=elsa',
      cookies: { tamma_session: token },
    });
    expect(res.statusCode).toBe(200);
    const body = JSON.parse(res.body);
    expect(body.allowed).toBe(true);
  });

  it('returns 200 for owner accessing elsa', async () => {
    const token = signToken({ id: '2', username: 'owner-user', githubId: 200, role: 'owner' });
    const res = await app.inject({
      method: 'GET',
      url: '/api/auth/role-check?service=elsa',
      cookies: { tamma_session: token },
    });
    expect(res.statusCode).toBe(200);
  });

  it('returns 403 for member accessing elsa', async () => {
    const token = signToken({ id: '3', username: 'member-user', githubId: 300, role: 'member' });
    const res = await app.inject({
      method: 'GET',
      url: '/api/auth/role-check?service=elsa',
      cookies: { tamma_session: token },
    });
    expect(res.statusCode).toBe(403);
  });

  it('returns 200 for admin accessing logs', async () => {
    const token = signToken({ id: '1', username: 'admin-user', githubId: 100, role: 'admin' });
    const res = await app.inject({
      method: 'GET',
      url: '/api/auth/role-check?service=logs',
      cookies: { tamma_session: token },
    });
    expect(res.statusCode).toBe(200);
  });

  it('returns 403 for member accessing logs', async () => {
    const token = signToken({ id: '3', username: 'member-user', githubId: 300, role: 'member' });
    const res = await app.inject({
      method: 'GET',
      url: '/api/auth/role-check?service=logs',
      cookies: { tamma_session: token },
    });
    expect(res.statusCode).toBe(403);
  });

  it('returns 200 for admin accessing admin panel', async () => {
    const token = signToken({ id: '1', username: 'admin-user', githubId: 100, role: 'admin' });
    const res = await app.inject({
      method: 'GET',
      url: '/api/auth/role-check?service=admin',
      cookies: { tamma_session: token },
    });
    expect(res.statusCode).toBe(200);
  });

  it('returns 403 for member accessing admin panel', async () => {
    const token = signToken({ id: '3', username: 'member-user', githubId: 300, role: 'member' });
    const res = await app.inject({
      method: 'GET',
      url: '/api/auth/role-check?service=admin',
      cookies: { tamma_session: token },
    });
    expect(res.statusCode).toBe(403);
  });

  it('returns 401 for expired token', async () => {
    // Sign a token that expired 10 seconds ago by backdating iat
    const nowSec = Math.floor(Date.now() / 1000);
    const token = app.jwt.sign(
      { id: '1', username: 'user', githubId: 100, role: 'admin', iat: nowSec - 20 },
      { expiresIn: '5s' },
    );
    const res = await app.inject({
      method: 'GET',
      url: '/api/auth/role-check?service=elsa',
      cookies: { tamma_session: token },
    });
    expect(res.statusCode).toBe(401);
  });
});
