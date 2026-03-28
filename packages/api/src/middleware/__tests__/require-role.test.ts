/**
 * require-role Middleware Tests
 *
 * Tests role hierarchy enforcement:
 *   - requireRole(minimumRole) — enforces a minimum role level
 *   - requireSelfOrRole(minimumRole) — allows self-access OR minimum role
 */

import { describe, it, expect } from 'vitest';
import Fastify from 'fastify';
import { requireRole, requireSelfOrRole } from '../require-role.js';

describe('requireRole', () => {
  it('allows owner when admin is required', async () => {
    const app = Fastify();
    app.decorateRequest('authUser', null);
    app.addHook('onRequest', async (request) => {
      (request as unknown as { authUser: { id: string; role: string } }).authUser = {
        id: 'owner-1',
        role: 'owner',
      };
    });

    app.get('/test', { preHandler: [requireRole('admin')] }, async () => ({ ok: true }));
    await app.ready();

    const res = await app.inject({ method: 'GET', url: '/test' });
    expect(res.statusCode).toBe(200);
  });

  it('allows admin when admin is required', async () => {
    const app = Fastify();
    app.decorateRequest('authUser', null);
    app.addHook('onRequest', async (request) => {
      (request as unknown as { authUser: { id: string; role: string } }).authUser = {
        id: 'admin-1',
        role: 'admin',
      };
    });

    app.get('/test', { preHandler: [requireRole('admin')] }, async () => ({ ok: true }));
    await app.ready();

    const res = await app.inject({ method: 'GET', url: '/test' });
    expect(res.statusCode).toBe(200);
  });

  it('blocks member when admin is required', async () => {
    const app = Fastify();
    app.decorateRequest('authUser', null);
    app.addHook('onRequest', async (request) => {
      (request as unknown as { authUser: { id: string; role: string } }).authUser = {
        id: 'member-1',
        role: 'member',
      };
    });

    app.get('/test', { preHandler: [requireRole('admin')] }, async () => ({ ok: true }));
    await app.ready();

    const res = await app.inject({ method: 'GET', url: '/test' });
    expect(res.statusCode).toBe(403);
  });

  it('returns 401 when no auth user present', async () => {
    const app = Fastify();
    app.decorateRequest('authUser', null);

    app.get('/test', { preHandler: [requireRole('admin')] }, async () => ({ ok: true }));
    await app.ready();

    const res = await app.inject({ method: 'GET', url: '/test' });
    expect(res.statusCode).toBe(401);
  });

  it('blocks admin when owner is required', async () => {
    const app = Fastify();
    app.decorateRequest('authUser', null);
    app.addHook('onRequest', async (request) => {
      (request as unknown as { authUser: { id: string; role: string } }).authUser = {
        id: 'admin-1',
        role: 'admin',
      };
    });

    app.get('/test', { preHandler: [requireRole('owner')] }, async () => ({ ok: true }));
    await app.ready();

    const res = await app.inject({ method: 'GET', url: '/test' });
    expect(res.statusCode).toBe(403);
  });

  it('reads user from x-auth-request headers when authUser is not set', async () => {
    const app = Fastify();
    app.decorateRequest('authUser', null);

    app.get('/test', { preHandler: [requireRole('admin')] }, async () => ({ ok: true }));
    await app.ready();

    const res = await app.inject({
      method: 'GET',
      url: '/test',
      headers: {
        'x-auth-request-user': 'proxy-admin',
        'x-auth-request-role': 'admin',
      },
    });
    expect(res.statusCode).toBe(200);
  });
});

describe('requireSelfOrRole', () => {
  it('allows member to access their own resource', async () => {
    const app = Fastify();
    app.decorateRequest('authUser', null);
    app.addHook('onRequest', async (request) => {
      (request as unknown as { authUser: { id: string; role: string } }).authUser = {
        id: 'member-1',
        role: 'member',
      };
    });

    app.get('/users/:id', { preHandler: [requireSelfOrRole('admin')] }, async () => ({ ok: true }));
    await app.ready();

    const res = await app.inject({ method: 'GET', url: '/users/member-1' });
    expect(res.statusCode).toBe(200);
  });

  it('blocks member from accessing another user resource', async () => {
    const app = Fastify();
    app.decorateRequest('authUser', null);
    app.addHook('onRequest', async (request) => {
      (request as unknown as { authUser: { id: string; role: string } }).authUser = {
        id: 'member-1',
        role: 'member',
      };
    });

    app.get('/users/:id', { preHandler: [requireSelfOrRole('admin')] }, async () => ({ ok: true }));
    await app.ready();

    const res = await app.inject({ method: 'GET', url: '/users/member-2' });
    expect(res.statusCode).toBe(403);
  });

  it('allows admin to access any user resource', async () => {
    const app = Fastify();
    app.decorateRequest('authUser', null);
    app.addHook('onRequest', async (request) => {
      (request as unknown as { authUser: { id: string; role: string } }).authUser = {
        id: 'admin-1',
        role: 'admin',
      };
    });

    app.get('/users/:id', { preHandler: [requireSelfOrRole('admin')] }, async () => ({ ok: true }));
    await app.ready();

    const res = await app.inject({ method: 'GET', url: '/users/someone-else' });
    expect(res.statusCode).toBe(200);
  });

  it('returns 401 when no auth user present', async () => {
    const app = Fastify();
    app.decorateRequest('authUser', null);

    app.get('/users/:id', { preHandler: [requireSelfOrRole('admin')] }, async () => ({ ok: true }));
    await app.ready();

    const res = await app.inject({ method: 'GET', url: '/users/anyone' });
    expect(res.statusCode).toBe(401);
  });
});
