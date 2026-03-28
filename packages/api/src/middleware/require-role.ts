/**
 * Role-checking middleware for route guards.
 *
 * Extracts the authenticated user from:
 *   1. request.authUser (set by JWT / auth plugin)
 *   2. X-Auth-Request-User + X-Auth-Request-Role headers (from oauth2-proxy)
 *
 * Provides two helpers:
 *   - requireRole(minimumRole)      — enforces a minimum role level
 *   - requireSelfOrRole(minimumRole) — allows self-access OR minimum role
 */

import type { FastifyRequest, FastifyReply } from 'fastify';
import type { Role } from '../auth/permissions.js';

const ROLE_HIERARCHY: Record<Role, number> = {
  member: 0,
  admin: 1,
  owner: 2,
};

/** Shape of the authenticated user attached to the request. */
export interface AuthenticatedUser {
  id: string;
  role: Role;
}

/**
 * Extract the authenticated user from the request.
 * Checks request.authUser first, then falls back to oauth2-proxy headers.
 */
function getAuthUser(request: FastifyRequest): AuthenticatedUser | null {
  // Check authUser decoration (from JWT auth plugin)
  const reqWithAuth = request as FastifyRequest & { authUser?: { id: string; role: string } };
  if (reqWithAuth.authUser) {
    return {
      id: reqWithAuth.authUser.id,
      role: reqWithAuth.authUser.role as Role,
    };
  }

  // Fallback: oauth2-proxy forwards user info via headers
  const userId = request.headers['x-auth-request-user'] as string | undefined;
  const userRole = request.headers['x-auth-request-role'] as string | undefined;
  if (userId && userRole) {
    return { id: userId, role: userRole as Role };
  }

  return null;
}

/**
 * Fastify preHandler that requires the authenticated user to have
 * at least the specified role level.
 */
export function requireRole(minimumRole: Role) {
  return async (request: FastifyRequest, reply: FastifyReply): Promise<void> => {
    const user = getAuthUser(request);

    if (!user) {
      reply.status(401).send({ error: 'Not authenticated' });
      return;
    }

    const userLevel = ROLE_HIERARCHY[user.role] ?? -1;
    const requiredLevel = ROLE_HIERARCHY[minimumRole];

    if (userLevel < requiredLevel) {
      reply.status(403).send({ error: `Requires ${minimumRole} role or higher` });
      return;
    }

    // Attach user to request for downstream handlers
    (request as FastifyRequest & { authUser: AuthenticatedUser }).authUser = user;
  };
}

/**
 * Fastify preHandler that allows access if the user is accessing their own
 * resource (params.id === user.id) OR if they have the minimum role.
 */
export function requireSelfOrRole(minimumRole: Role) {
  return async (request: FastifyRequest, reply: FastifyReply): Promise<void> => {
    const user = getAuthUser(request);

    if (!user) {
      reply.status(401).send({ error: 'Not authenticated' });
      return;
    }

    // Attach user to request for downstream handlers
    (request as FastifyRequest & { authUser: AuthenticatedUser }).authUser = user;

    const params = request.params as { id?: string };

    // Allow if user is accessing their own resource
    if (params.id === user.id) {
      return;
    }

    // Otherwise require minimum role
    const userLevel = ROLE_HIERARCHY[user.role] ?? -1;
    const requiredLevel = ROLE_HIERARCHY[minimumRole];

    if (userLevel < requiredLevel) {
      reply.status(403).send({ error: `Requires ${minimumRole} role or access to own resource` });
      return;
    }
  };
}
