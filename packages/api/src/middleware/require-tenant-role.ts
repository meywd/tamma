/**
 * Tenant Role Middleware (Story 18-3)
 *
 * Checks that the authenticated user has a specific minimum role
 * within the active tenant. Must be used after requireTenant().
 */

import type { FastifyRequest, FastifyReply } from 'fastify';
import type { TenantMembership } from '../persistence/tenant-membership-store.js';

type TenantRole = 'member' | 'admin' | 'owner';

const ROLE_HIERARCHY: Record<TenantRole, number> = {
  member: 0,
  admin: 1,
  owner: 2,
};

/**
 * Create a preHandler that checks the user's role within the active tenant
 * meets a minimum threshold.
 *
 * Must be used after requireTenant() middleware.
 */
export function requireTenantRole(minimumRole: TenantRole) {
  return async (request: FastifyRequest, reply: FastifyReply): Promise<void> => {
    const membership = (request as FastifyRequest & { tenantMembership?: TenantMembership }).tenantMembership;

    if (!membership) {
      reply.status(403).send({ error: 'Tenant membership not resolved. Ensure requireTenant middleware runs first.' });
      return;
    }

    const userLevel = ROLE_HIERARCHY[membership.role as TenantRole] ?? -1;
    const requiredLevel = ROLE_HIERARCHY[minimumRole];

    if (userLevel < requiredLevel) {
      reply.status(403).send({ error: `Requires ${minimumRole} role or higher within this organization` });
      return;
    }
  };
}
