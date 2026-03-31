/**
 * User-scoped provider configuration routes.
 *
 * GET  /providers — get the authenticated user's provider settings
 * PUT  /providers — update the authenticated user's provider settings
 */

import type { FastifyInstance, FastifyRequest } from 'fastify';
import type { ConfigService } from '../../services/settings/ConfigService.js';
import type { IProvidersConfig } from '@tamma/shared';

/**
 * Extract the authenticated user ID from the request.
 * In production this comes from JWT or API key auth middleware.
 * Falls back to a header for development/testing.
 */
function getUserId(request: FastifyRequest): string | null {
  // Try auth plugin user context (attached by auth middleware)
  const user = request.user as unknown;
  if (user && typeof user === 'object' && 'id' in user && typeof (user as Record<string, unknown>)['id'] === 'string') {
    return (user as Record<string, unknown>)['id'] as string;
  }

  // Fallback for dev/testing only: X-User-Id header
  // SECURITY: Never trust this header in production — it allows impersonation
  if (process.env['NODE_ENV'] !== 'production') {
    const header = request.headers['x-user-id'];
    if (typeof header === 'string' && header.length > 0) return header;
  }

  return null;
}

export function registerProvidersRoutes(app: FastifyInstance, service: ConfigService): void {
  app.get('/providers', async (request, reply) => {
    const userId = getUserId(request);
    if (!userId) {
      return reply.status(401).send({ error: 'Authentication required' });
    }

    const config = await service.getUserProviders(userId);
    return reply.send(config);
  });

  app.put('/providers', async (request, reply) => {
    const userId = getUserId(request);
    if (!userId) {
      return reply.status(401).send({ error: 'Authentication required' });
    }

    try {
      const body = request.body;
      if (!body || typeof body !== 'object' || Array.isArray(body)) {
        return reply.status(400).send({ error: 'Request body must be a JSON object' });
      }
      const updated = await service.updateUserProviders(userId, body as IProvidersConfig);
      return reply.send(updated);
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Invalid configuration';
      return reply.status(400).send({ error: message });
    }
  });
}
