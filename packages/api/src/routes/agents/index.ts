/**
 * Agent Config Routes Registration
 *
 * Registers agent config CRUD routes under /api/v1/agents.
 *
 * Rate limiting:
 *   GET  /config        → 100 req/min (read)
 *   PUT  /config        → 30 req/min  (write)
 *   POST /config/validate → 100 req/min (read-like)
 *
 * RBAC:
 *   GET  → requires 'settings:view' (admin, owner)
 *   PUT  → requires 'settings:manage' (owner only)
 *   POST → requires 'settings:view' (admin, owner) — validation is read-only
 */

import type { FastifyInstance } from 'fastify';

import type { IAgentConfigStore } from '../../persistence/agent-config-store.js';
import { registerAgentConfigRoutes } from './agent-config-routes.js';
import { requirePermission } from '../../auth/require-permission.js';

export interface AgentConfigRoutesOptions {
  store: IAgentConfigStore;
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
    },
    { prefix: '/api/v1/agents' },
  );
}
