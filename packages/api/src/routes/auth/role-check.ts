/**
 * Role Check Endpoint
 *
 * GET /api/auth/role-check?service=elsa|logs|admin
 *
 * Used by nginx auth_request to gate access to proxied services
 * (ELSA Studio, OpenSearch Dashboards, admin panel).
 *
 * The endpoint reads the user's identity from the tamma_session JWT
 * cookie (shared across *.tamma.dev via domain=.tamma.dev) and checks
 * the user's role against the permission required for the service.
 *
 * Returns:
 *   200 — user has permission (access granted)
 *   401 — no valid session (redirect to login)
 *   403 — user lacks required role (show 403 page)
 *   400 — unknown service parameter
 */

import type { FastifyInstance, FastifyRequest, FastifyReply } from 'fastify';
import { hasPermission, isValidRole } from '../../auth/permissions.js';
import type { Permission } from '../../auth/permissions.js';

/** Maps service query param to the permission that gates it. */
const SERVICE_PERMISSION_MAP: Record<string, Permission> = {
  elsa: 'elsa:access',
  logs: 'logs:access',
  admin: 'admin:access',
};

export async function registerRoleCheckRoute(app: FastifyInstance): Promise<void> {
  app.get<{
    Querystring: { service?: string };
  }>('/api/auth/role-check', async (request: FastifyRequest<{ Querystring: { service?: string } }>, reply: FastifyReply) => {
    const service = request.query.service;

    if (!service) {
      return reply.status(400).send({ error: 'Missing required query parameter: service' });
    }

    const permission = SERVICE_PERMISSION_MAP[service];
    if (!permission) {
      return reply.status(400).send({ error: `Unknown service: ${service}` });
    }

    // Try to verify the tamma_session JWT cookie.
    // The cookie is set with domain=.tamma.dev so it's available on all subdomains.
    try {
      const decoded = await request.jwtVerify<{
        id: string;
        username: string;
        githubId: number;
        role: string;
      }>();

      const role = decoded.role;

      if (!isValidRole(role)) {
        request.log.warn(
          { userId: decoded.id, role, service },
          'Service access denied: unrecognized role',
        );
        return reply.status(403).send({ error: 'Insufficient role' });
      }

      if (hasPermission(role, permission)) {
        request.log.debug(
          { userId: decoded.id, role, service },
          'Service access granted',
        );
        return reply.status(200).send({ allowed: true });
      }

      request.log.warn(
        { userId: decoded.id, role, service, requiredPermission: permission },
        'Service access denied by RBAC',
      );
      return reply.status(403).send({ error: 'Insufficient role' });
    } catch {
      // JWT invalid, expired, or missing
      return reply.status(401).send({ error: 'Not authenticated' });
    }
  });
}
