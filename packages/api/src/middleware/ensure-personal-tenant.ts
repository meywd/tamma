/**
 * Personal Tenant Auto-Provisioning Middleware (Story 18-3, Task 9)
 *
 * Users who registered in 18-1 before 18-3 shipped have `users.tenant_id = NULL`
 * and zero rows in `tenant_memberships`. On their first authenticated request,
 * this middleware auto-provisions a personal tenant and adds the user as owner.
 *
 * Resolution order:
 * 1. If `request.user.tenantId` is already set, no-op (fast path).
 * 2. If the user has existing memberships, pick the most-recently-joined.
 * 3. If no memberships, create a personal tenant and add user as owner.
 */

import type { FastifyRequest, FastifyReply } from 'fastify';
import type { ITenantStore } from '../persistence/tenant-store.js';
import type { IUserStore } from '../persistence/user-store.js';
import type { ITenantMembershipStore } from '../persistence/tenant-membership-store.js';
import type { UnifiedJwtPayload } from '../auth/jwt.js';

export interface EnsurePersonalTenantOptions {
  tenantStore: ITenantStore;
  userStore: IUserStore;
  membershipStore: ITenantMembershipStore;
}

/**
 * Create a preHandler that ensures every authenticated user has at least one tenant.
 *
 * Idempotent and cheap: a simple `users.tenant_id IS NOT NULL` check
 * short-circuits 99% of requests.
 */
export function ensurePersonalTenant(opts: EnsurePersonalTenantOptions) {
  const { tenantStore, userStore, membershipStore } = opts;

  return async (request: FastifyRequest, _reply: FastifyReply): Promise<void> => {
    // Only run for authenticated requests
    let jwt: UnifiedJwtPayload | undefined;
    try {
      jwt = await request.jwtVerify<UnifiedJwtPayload>();
    } catch {
      // Not authenticated — skip, let downstream auth hooks handle it
      return;
    }

    if (!jwt) return;

    // Fast path: user already has a tenant set
    if (jwt.tenantId) return;

    // Check the DB for current state (JWT may be stale)
    const user = await userStore.getUser(jwt.sub);
    if (!user) return;
    if (user.tenantId) return;

    // Check if user has any existing memberships
    const existingTenants = await membershipStore.getUserTenants(jwt.sub);
    if (existingTenants.length > 0) {
      // Pick the most-recently-joined (last in array, sorted by joinedAt)
      const latest = existingTenants[existingTenants.length - 1]!;
      await userStore.updateActiveTenant(jwt.sub, latest.tenantId);

      request.log.info({
        event: 'TENANT.RESOLVED.SUCCESS',
        tenantId: latest.tenantId,
        userId: jwt.sub,
        reason: 'existing_membership',
      }, 'Resolved tenant from existing membership');
      return;
    }

    // No memberships: auto-create a personal tenant
    const displayName = user.githubLogin || user.email?.split('@')[0] || 'User';
    const baseSlug = `u-${jwt.sub.slice(0, 8)}`;

    let slug = baseSlug;
    let attempts = 0;
    // Retry with suffix on collision (unlikely with UUID-based slugs)
    while (await tenantStore.getTenantBySlug(slug)) {
      attempts++;
      slug = `${baseSlug}-${attempts}`;
      if (attempts > 5) {
        request.log.error({ userId: jwt.sub }, 'Failed to generate unique personal tenant slug');
        return;
      }
    }

    const tenant = await tenantStore.createTenant({
      name: `${displayName}'s Workspace`,
      slug,
    });

    await membershipStore.addMember(tenant.id, jwt.sub, 'owner');
    await userStore.updateActiveTenant(jwt.sub, tenant.id);

    request.log.info({
      event: 'TENANT.AUTO_CREATED.SUCCESS',
      tenantId: tenant.id,
      userId: jwt.sub,
      reason: 'first_login',
    }, 'Auto-provisioned personal tenant');
  };
}
