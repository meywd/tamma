/**
 * requireScope Middleware Tests
 *
 * Tests service scope matching for service-scope principals,
 * and pass-through for user/installation scopes.
 */

import { describe, it, expect, beforeAll, afterAll } from 'vitest';
import type { FastifyInstance } from 'fastify';
import { requireScope } from '../require-scope.js';
import type { AuthPrincipal } from '../principal.js';

describe('requireScope middleware', () => {
  let app: FastifyInstance;

  beforeAll(async () => {
    const Fastify = (await import('fastify')).default;
    app = Fastify({ logger: false });

    // Decorate request with authPrincipal
    app.decorateRequest('authPrincipal', null);

    // Hook to set authPrincipal from test headers
    app.addHook('onRequest', async (request) => {
      const scopeHeader = request.headers['x-test-scope'] as string | undefined;
      if (scopeHeader === 'service') {
        const permsHeader = request.headers['x-test-permissions'] as string | undefined;
        const permissions = permsHeader ? permsHeader.split(',') : [];
        (request as typeof request & { authPrincipal: AuthPrincipal }).authPrincipal = {
          scope: 'service',
          keyId: 'key-1',
          serviceName: 'test-service',
          permissions,
          tenantId: null,
        };
      } else if (scopeHeader === 'user') {
        (request as typeof request & { authPrincipal: AuthPrincipal }).authPrincipal = {
          scope: 'user',
          keyId: 'key-2',
          userId: 'user-1',
          role: 'admin',
          tenantId: '00000000-0000-0000-0000-000000000000',
        };
      } else if (scopeHeader === 'installation') {
        (request as typeof request & { authPrincipal: AuthPrincipal }).authPrincipal = {
          scope: 'installation',
          keyId: 'key-3',
          installationId: 42,
          tenantId: '00000000-0000-0000-0000-000000000000',
        };
      }
      // If no scope header, authPrincipal remains null (unauthenticated)
    });

    // Test route requiring 'prompts:read' scope
    app.get('/test/prompts', {
      preHandler: [requireScope('prompts:read')],
    }, async () => ({ ok: true }));

    // Test route requiring 'diagnostics:write' scope
    app.post('/test/diagnostics', {
      preHandler: [requireScope('diagnostics:write')],
    }, async () => ({ ok: true }));

    await app.ready();
  });

  afterAll(async () => {
    await app.close();
  });

  // ----------------------------------------------------------------
  // No principal (unauthenticated)
  // ----------------------------------------------------------------

  it('returns 401 when no authPrincipal is present', async () => {
    const res = await app.inject({ method: 'GET', url: '/test/prompts' });
    expect(res.statusCode).toBe(401);
    expect(JSON.parse(res.body).error).toBe('Not authenticated');
  });

  // ----------------------------------------------------------------
  // Service scope — permission check
  // ----------------------------------------------------------------

  it('allows service principal with matching scope', async () => {
    const res = await app.inject({
      method: 'GET',
      url: '/test/prompts',
      headers: {
        'x-test-scope': 'service',
        'x-test-permissions': 'prompts:read,diagnostics:write',
      },
    });
    expect(res.statusCode).toBe(200);
  });

  it('rejects service principal without required scope', async () => {
    const res = await app.inject({
      method: 'GET',
      url: '/test/prompts',
      headers: {
        'x-test-scope': 'service',
        'x-test-permissions': 'diagnostics:write',
      },
    });
    expect(res.statusCode).toBe(403);
    const body = JSON.parse(res.body);
    expect(body.error).toBe('Insufficient scope');
    expect(body.required).toBe('prompts:read');
  });

  it('rejects service principal with empty permissions', async () => {
    const res = await app.inject({
      method: 'POST',
      url: '/test/diagnostics',
      headers: {
        'x-test-scope': 'service',
        'x-test-permissions': '',
      },
    });
    expect(res.statusCode).toBe(403);
  });

  // ----------------------------------------------------------------
  // User scope — passes through (RBAC handles auth)
  // ----------------------------------------------------------------

  it('allows user principal regardless of service scope requirement', async () => {
    const res = await app.inject({
      method: 'GET',
      url: '/test/prompts',
      headers: { 'x-test-scope': 'user' },
    });
    expect(res.statusCode).toBe(200);
  });

  // ----------------------------------------------------------------
  // Installation scope — passes through
  // ----------------------------------------------------------------

  it('allows installation principal regardless of service scope requirement', async () => {
    const res = await app.inject({
      method: 'GET',
      url: '/test/prompts',
      headers: { 'x-test-scope': 'installation' },
    });
    expect(res.statusCode).toBe(200);
  });
});
