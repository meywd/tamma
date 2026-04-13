/**
 * Service Scope Authorization Middleware.
 *
 * Creates a Fastify preHandler that checks whether the authenticated
 * service principal has a specific scope string in its permissions array.
 *
 * For user-scope and installation-scope principals, the check is skipped
 * (those scopes use role-based access control instead).
 *
 * Usage:
 * ```ts
 * app.get('/api/prompts', {
 *   preHandler: [requireScope('prompts:read')],
 * }, handler);
 * ```
 */

import type { FastifyRequest, FastifyReply } from 'fastify';
import type { AuthPrincipal } from './principal.js';

/**
 * Create a Fastify preHandler that enforces a specific service scope.
 *
 * - For service-scope principals: checks that the required scope is
 *   present in the principal's permissions array.
 * - For user/installation principals: passes through (RBAC handles
 *   authorization for these scopes).
 * - If no authPrincipal is present on the request: returns 401.
 */
export function requireScope(requiredScope: string) {
  return async (request: FastifyRequest, reply: FastifyReply): Promise<void> => {
    const principal = (request as FastifyRequest & { authPrincipal?: AuthPrincipal }).authPrincipal;

    if (!principal) {
      reply.status(401).send({ error: 'Not authenticated' });
      return;
    }

    // Only enforce scope checks on service principals
    if (principal.scope !== 'service') {
      // User and installation principals are authorized by RBAC elsewhere
      return;
    }

    // Check that the required scope is in the service key's permissions
    if (!principal.permissions.includes(requiredScope)) {
      request.log.warn(
        {
          keyId: principal.keyId,
          requiredScope,
          presentScopes: principal.permissions,
        },
        'Auth failure: insufficient scope',
      );
      reply.status(403).send({
        error: 'Insufficient scope',
        required: requiredScope,
      });
      return;
    }
  };
}
