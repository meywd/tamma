/**
 * Agent Config Routes Registration
 *
 * Registers agent config CRUD routes and resolver routes under /api/v1/agents.
 *
 * Rate limiting:
 *   GET  /config              → 100 req/min (read)
 *   PUT  /config              → 30 req/min  (write)
 *   POST /config/validate     → 100 req/min (read-like)
 *   GET  /:role/resolve       → 100 req/min (read)
 *   POST /resolve-for-phase   → 100 req/min (read)
 *
 * RBAC:
 *   GET  → requires 'settings:view' (admin, owner)
 *   PUT  → requires 'settings:manage' (owner only)
 *   POST → requires 'settings:view' (admin, owner) — validation/resolution is read-only
 */

import type { FastifyInstance } from 'fastify';

import type { IAgentConfigStore } from '../../persistence/agent-config-store.js';
import type { IAgentResolverService } from '../../services/agent-resolver.js';
import { registerAgentConfigRoutes } from './agent-config-routes.js';
import { registerAgentResolverRoutes } from './agent-resolver-routes.js';
import { requirePermission } from '../../auth/require-permission.js';

export interface AgentConfigRoutesOptions {
  store: IAgentConfigStore;
  /** Story 9-8: Unified Agent Resolver Service (optional for backward compat). */
  resolverService?: IAgentResolverService;
}

export async function registerAgentConfigApiRoutes(
  app: FastifyInstance,
  options: AgentConfigRoutesOptions,
): Promise<void> {
  await app.register(
    async (scoped) => {
      // Register rate-limit plugin at this scope
      await scoped.register((await import('@fastify/rate-limit')).default, {
        max: 100,
        timeWindow: '1 minute',
        keyGenerator: (request) => request.ip,
      });

      // RBAC: GET/POST → settings:view, PUT → settings:manage
      scoped.addHook('onRequest', async (request, reply) => {
        if (request.method === 'PUT') {
          await requirePermission('settings:manage')(request, reply);
        } else {
          await requirePermission('settings:view')(request, reply);
        }
      });

      await registerAgentConfigRoutes(scoped, { store: options.store });

      // Story 9-8: Resolver routes
      if (options.resolverService !== undefined) {
        await registerAgentResolverRoutes(scoped, {
          resolverService: options.resolverService,
        });
      }
    },
    { prefix: '/api/v1/agents' },
  );
}
