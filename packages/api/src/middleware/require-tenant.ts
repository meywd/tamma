/**
 * Tenant Membership Middleware (Story 18-3)
 *
 * Verifies that the authenticated user is a member of the tenant
 * specified in the JWT. Decorates the request with membership info.
 */

import type { FastifyRequest, FastifyReply } from 'fastify';
import type { ITenantMembershipStore, TenantMembership } from '../persistence/tenant-membership-store.js';
import type { UnifiedJwtPayload } from '../auth/jwt.js';

/**
 * Create a preHandler that verifies the authenticated user's membership
 * in the tenant from their JWT tenantId claim.
 */
export function requireTenant(membershipStore: ITenantMembershipStore) {
  return async (request: FastifyRequest, reply: FastifyReply): Promise<void> => {
    const jwt = (request as FastifyRequest & { user?: UnifiedJwtPayload }).user as UnifiedJwtPayload | undefined;

    if (!jwt) {
      reply.status(401).send({ error: 'Not authenticated' });
      return;
    }

    if (!jwt.tenantId) {
      reply.status(403).send({ error: 'No active tenant. Please create or join an organization.' });
      return;
    }

    const membership = await membershipStore.getMembership(jwt.tenantId, jwt.sub);
    if (!membership) {
      reply.status(403).send({ error: 'Not a member of the active tenant' });
      return;
    }

    // Decorate request with membership
    (request as FastifyRequest & { tenantMembership?: TenantMembership }).tenantMembership = membership;
  };
}
