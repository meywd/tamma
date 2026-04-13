/**
 * Tenant Context Middleware Tests (Story 17-5)
 *
 * Verifies that tenant context is correctly resolved from various
 * authentication sources and set on the request.
 */

import { describe, it, expect } from 'vitest';
import Fastify from 'fastify';
import type { FastifyInstance, FastifyRequest } from 'fastify';
import { DEFAULT_TENANT_ID } from '@tamma/shared';
import { registerTenantContextPlugin } from '../tenant-context.js';
import type { TenantContextConfig } from '../tenant-context.js';
import { InMemoryTenantStore } from '../../persistence/tenant-store.js';
import { InMemoryUserStore } from '../../persistence/user-store.js';

const TENANT_A_ID = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';

interface BuildAppOpts extends Partial<TenantContextConfig> {
  /** Simulate auth by setting fields on request before the tenant plugin runs. */
  simulateAuth?: (request: FastifyRequest) => void;
}

async function buildApp(opts: BuildAppOpts = {}): Promise<FastifyInstance> {
  const app = Fastify({ logger: false });

  // Decorate request with auth-related fields (simulating auth plugins)
  app.decorateRequest('authUser', null);
  app.decorateRequest('authPrincipal', null);
  app.decorateRequest('installationContext', null);

  // Simulate auth — onRequest hook runs BEFORE the tenant plugin's onRequest
  if (opts.simulateAuth) {
    const authFn = opts.simulateAuth;
    app.addHook('onRequest', async (request) => {
      authFn(request);
    });
  }

  const tenantStore = opts.tenantStore ?? new InMemoryTenantStore();
  const userStore = opts.userStore ?? new InMemoryUserStore();
  const enableAuth = opts.enableAuth ?? false;

  await app.register(registerTenantContextPlugin, {
    tenantStore,
    userStore,
    enableAuth,
  });

  // Test route that returns the resolved tenantId
  app.get('/api/test', async (request) => {
    return { tenantId: request.tenantId ?? null };
  });

  // Health check (should be tenant-free)
  app.get('/api/health', async () => {
    return { status: 'ok' };
  });

  return app;
}

describe('registerTenantContextPlugin', () => {
  // -----------------------------------------------------------------------
  // Auth disabled (dev/CLI mode)
  // -----------------------------------------------------------------------

  describe('auth disabled (dev mode)', () => {
    it('sets tenantId to DEFAULT_TENANT_ID', async () => {
      const app = await buildApp({ enableAuth: false });

      const response = await app.inject({
        method: 'GET',
        url: '/api/test',
      });

      expect(response.statusCode).toBe(200);
      const body = response.json<{ tenantId: string }>();
      expect(body.tenantId).toBe(DEFAULT_TENANT_ID);
    });
  });

  // -----------------------------------------------------------------------
  // Auth enabled — AuthPrincipal (unified API key)
  // -----------------------------------------------------------------------

  describe('auth enabled — AuthPrincipal', () => {
    it('uses tenantId from AuthPrincipal', async () => {
      const app = await buildApp({
        enableAuth: true,
        simulateAuth: (request) => {
          (request as any).authPrincipal = {
            scope: 'user',
            keyId: 'key-1',
            userId: 'user-1',
            role: 'admin',
            tenantId: TENANT_A_ID,
          };
        },
      });

      const response = await app.inject({
        method: 'GET',
        url: '/api/test',
      });

      expect(response.statusCode).toBe(200);
      const body = response.json<{ tenantId: string }>();
      expect(body.tenantId).toBe(TENANT_A_ID);
    });
  });

  // -----------------------------------------------------------------------
  // Auth enabled — JWT with tenantId claim
  // -----------------------------------------------------------------------

  describe('auth enabled — JWT tenantId', () => {
    it('uses tenantId from authUser JWT claims', async () => {
      const app = await buildApp({
        enableAuth: true,
        simulateAuth: (request) => {
          (request as any).authUser = {
            id: 'user-1',
            username: 'testuser',
            role: 'admin',
            tenantId: TENANT_A_ID,
          };
        },
      });

      const response = await app.inject({
        method: 'GET',
        url: '/api/test',
      });

      expect(response.statusCode).toBe(200);
      const body = response.json<{ tenantId: string }>();
      expect(body.tenantId).toBe(TENANT_A_ID);
    });
  });

  // -----------------------------------------------------------------------
  // Auth enabled — Installation context
  // -----------------------------------------------------------------------

  describe('auth enabled — installation context', () => {
    it('resolves tenant from installation external_id', async () => {
      const tenantStore = new InMemoryTenantStore();
      const tenant = await tenantStore.createTenant({
        name: 'Acme',
        slug: 'acme',
        externalId: '12345',
      });

      const app = await buildApp({
        enableAuth: true,
        tenantStore,
        simulateAuth: (request) => {
          (request as any).installationContext = { installationId: 12345 };
        },
      });

      const response = await app.inject({
        method: 'GET',
        url: '/api/test',
      });

      expect(response.statusCode).toBe(200);
      const body = response.json<{ tenantId: string }>();
      expect(body.tenantId).toBe(tenant.id);
    });
  });

  // -----------------------------------------------------------------------
  // Auth enabled — User tenantId fallback
  // -----------------------------------------------------------------------

  describe('auth enabled — user tenantId fallback', () => {
    it('resolves tenant from user record', async () => {
      const userStore = new InMemoryUserStore();
      const user = await userStore.upsertUser({
        githubId: 123,
        githubLogin: 'testuser',
        email: 'test@example.com',
        role: 'admin',
        tenantId: TENANT_A_ID,
      });

      const app = await buildApp({
        enableAuth: true,
        userStore,
        simulateAuth: (request) => {
          // Auth user without tenantId in JWT — fallback to user.tenantId
          (request as any).authUser = {
            id: user.id,
            username: 'testuser',
            role: 'admin',
          };
        },
      });

      const response = await app.inject({
        method: 'GET',
        url: '/api/test',
      });

      expect(response.statusCode).toBe(200);
      const body = response.json<{ tenantId: string }>();
      expect(body.tenantId).toBe(TENANT_A_ID);
    });
  });

  // -----------------------------------------------------------------------
  // Auth enabled — No tenant resolvable
  // -----------------------------------------------------------------------

  describe('auth enabled — no tenant resolvable', () => {
    it('returns 403 when tenant cannot be resolved', async () => {
      const app = await buildApp({ enableAuth: true });

      const response = await app.inject({
        method: 'GET',
        url: '/api/test',
      });

      expect(response.statusCode).toBe(403);
      const body = response.json<{ error: string }>();
      expect(body.error).toMatch(/tenant/i);
    });
  });

  // -----------------------------------------------------------------------
  // Tenant-free paths
  // -----------------------------------------------------------------------

  describe('tenant-free paths', () => {
    it('/api/health does not require tenant context', async () => {
      const app = await buildApp({ enableAuth: true });

      const response = await app.inject({
        method: 'GET',
        url: '/api/health',
      });

      expect(response.statusCode).toBe(200);
    });
  });

  // -----------------------------------------------------------------------
  // Request tenantId is accessible in route handler
  // -----------------------------------------------------------------------

  describe('request decoration', () => {
    it('tenantId is accessible on request in route handler', async () => {
      const app = await buildApp({ enableAuth: false });

      const response = await app.inject({
        method: 'GET',
        url: '/api/test',
      });

      expect(response.statusCode).toBe(200);
      const body = response.json<{ tenantId: string }>();
      expect(body.tenantId).toBe(DEFAULT_TENANT_ID);
    });
  });

  // -----------------------------------------------------------------------
  // Priority order: AuthPrincipal > JWT > Installation > User
  // -----------------------------------------------------------------------

  describe('resolution priority', () => {
    it('AuthPrincipal takes precedence over JWT tenantId', async () => {
      const principalTenant = 'cccccccc-cccc-cccc-cccc-cccccccccccc';
      const app = await buildApp({
        enableAuth: true,
        simulateAuth: (request) => {
          (request as any).authPrincipal = {
            scope: 'user',
            keyId: 'key-1',
            userId: 'user-1',
            role: 'admin',
            tenantId: principalTenant,
          };
          (request as any).authUser = {
            id: 'user-1',
            username: 'testuser',
            role: 'admin',
            tenantId: TENANT_A_ID,
          };
        },
      });

      const response = await app.inject({
        method: 'GET',
        url: '/api/test',
      });

      expect(response.statusCode).toBe(200);
      const body = response.json<{ tenantId: string }>();
      expect(body.tenantId).toBe(principalTenant);
    });
  });
});
