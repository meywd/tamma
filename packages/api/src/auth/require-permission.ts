/**
 * RBAC Middleware — requirePermission
 *
 * Fastify preHandler hook that checks the authenticated user's role
 * against the central permission matrix.
 *
 * Extracts the user from:
 *   1. request.authUser (set by the JWT auth plugin)
 *   2. JWT in tamma_session cookie (for nginx auth_request sub-requests)
 *   3. X-Auth-Request-User header (fallback for oauth2-proxy)
 *
 * Returns 401 if no user can be determined, 403 if the user's role
 * is insufficient for the requested permission.
 */

import type { FastifyRequest, FastifyReply } from 'fastify';
import { hasPermission, isValidRole } from './permissions.js';
import type { Permission, Role } from './permissions.js';

/** Shape of the authUser decoration set by the auth plugin. */
interface AuthUserPayload {
  id: string;
  role: string;
  username?: string;
}

/**
 * Create a Fastify preHandler that enforces a specific permission.
 *
 * Usage:
 * ```ts
 * app.get('/api/admin/users', {
 *   preHandler: [requirePermission('users:view')],
 * }, handler);
 * ```
 */
export function requirePermission(permission: Permission) {
  return async (request: FastifyRequest, reply: FastifyReply): Promise<void> => {
    // Check if the auth plugin has been registered at all.
    // If not (dev mode / tests without auth), skip RBAC enforcement.
    if (!('authUser' in request)) {
      return;
    }

    // Try to get user from auth plugin decoration
    const authUser = (request as FastifyRequest & { authUser?: AuthUserPayload | null }).authUser;

    if (!authUser) {
      request.log.warn(
        { permission },
        'RBAC denied: no authenticated user on request',
      );
      reply.status(401).send({ error: 'Not authenticated' });
      return;
    }

    const role = authUser.role;

    if (!isValidRole(role)) {
      request.log.warn(
        { userId: authUser.id, role, permission },
        'RBAC denied: unrecognized role',
      );
      reply.status(403).send({
        error: 'Insufficient permissions',
        required: permission,
      });
      return;
    }

    if (!hasPermission(role as Role, permission)) {
      request.log.warn(
        { userId: authUser.id, userRole: role, permission },
        'RBAC denied: insufficient permissions',
      );
      reply.status(403).send({
        error: 'Insufficient permissions',
        required: permission,
      });
      return;
    }

    // Permission granted — let the request proceed
    request.log.debug(
      { userId: authUser.id, userRole: role, permission },
      'RBAC granted',
    );
  };
}
