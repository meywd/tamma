/**
 * Tenant Context Middleware
 *
 * Fastify plugin that resolves the current tenant from the authenticated
 * request and sets the PostgreSQL session variable `app.current_tenant_id`
 * for RLS enforcement.
 *
 * Resolution priority:
 *   1. AuthPrincipal.tenantId (unified API key auth — Story 16-7)
 *   2. JWT claims tenantId (OAuth/dashboard)
 *   3. Installation context → tenant lookup
 *   4. Auth user → user.tenantId
 *   5. Auth disabled (CLI/dev) → DEFAULT_TENANT_ID
 *
 * When tenant resolution fails, the request is rejected with 403.
 */

import fp from 'fastify-plugin';
import type { FastifyInstance, FastifyRequest, FastifyReply } from 'fastify';
import { DEFAULT_TENANT_ID } from '@tamma/shared';
import type { ITenantStore } from '../persistence/tenant-store.js';
import type { IUserStore } from '../persistence/user-store.js';
import type { AuthPrincipal } from '../auth/principal.js';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

export interface TenantContextConfig {
  tenantStore: ITenantStore;
  userStore: IUserStore;
  enableAuth: boolean;
}

/** Paths that do not require tenant context. */
const TENANT_FREE_PATHS = [
  '/api/health',
  '/api/auth/login',
  '/api/auth/api-key',
  '/api/auth/callback',
  '/api/auth/github',
];

// ---------------------------------------------------------------------------
// Augment Fastify request type
// ---------------------------------------------------------------------------

declare module 'fastify' {
  interface FastifyRequest {
    tenantId?: string;
  }
}

// ---------------------------------------------------------------------------
// Plugin
// ---------------------------------------------------------------------------

async function tenantContextPlugin(
  fastify: FastifyInstance,
  opts: TenantContextConfig,
): Promise<void> {
  const { tenantStore, userStore, enableAuth } = opts;

  // Decorate request with tenantId
  fastify.decorateRequest('tenantId', undefined);

  fastify.addHook('onRequest', async (request: FastifyRequest, reply: FastifyReply) => {
    // Skip tenant resolution for health/auth endpoints
    if (TENANT_FREE_PATHS.some((p) => request.url === p || request.url.startsWith(p + '/'))) {
      return;
    }

    let tenantId: string | undefined;

    if (!enableAuth) {
      // CLI/self-hosted/dev mode — use default tenant
      tenantId = DEFAULT_TENANT_ID;
    } else {
      // Source 1: AuthPrincipal (unified API key auth)
      const principal = (request as FastifyRequest & { authPrincipal?: AuthPrincipal }).authPrincipal;
      if (principal) {
        if (principal.tenantId !== null) {
          tenantId = principal.tenantId;
        }
      }

      // Source 2: JWT tenantId claim
      if (tenantId === undefined) {
        const authUser = (request as FastifyRequest & { authUser?: { tenantId?: string } }).authUser;
        if (authUser?.tenantId) {
          tenantId = authUser.tenantId;
        }
      }

      // Source 3: Installation context → tenant lookup
      if (tenantId === undefined) {
        const installCtx = (request as FastifyRequest & { installationContext?: { installationId: number } }).installationContext;
        if (installCtx?.installationId !== undefined) {
          const tenant = await tenantStore.getTenantByExternalId(
            String(installCtx.installationId),
          );
          if (tenant) {
            tenantId = tenant.id;
          }
        }
      }

      // Source 4: User's tenant
      if (tenantId === undefined) {
        const authUser = (request as FastifyRequest & { authUser?: { id?: string } }).authUser;
        if (authUser?.id) {
          const user = await userStore.getUser(authUser.id);
          if (user?.tenantId !== null && user?.tenantId !== undefined) {
            tenantId = user.tenantId;
          }
        }
      }
    }

    if (tenantId === undefined) {
      reply.status(403).send({
        error: 'Tenant context could not be resolved',
      });
      return;
    }

    // Set on request
    request.tenantId = tenantId;

    // Add tenantId to request logger for structured logging
    request.log = request.log.child({ tenantId });
  });
}

export const registerTenantContextPlugin = fp(tenantContextPlugin, {
  name: 'tamma-tenant-context',
});
