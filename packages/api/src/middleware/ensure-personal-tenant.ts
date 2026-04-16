/**
 * Ensure Personal Tenant Middleware
 *
 * Auto-provisions a personal tenant for users who registered before
 * the tenant system was shipped (18-1 users with no memberships).
 *
 * On first authenticated request:
 *   1. If user already has a tenantId, no-op.
 *   2. If user has existing memberships, pick the most recent.
 *   3. If user has zero memberships, auto-create a personal tenant.
 *
 * Wire as preHandler on /api/v1/* after JWT verification.
 */

import type { FastifyRequest, FastifyReply } from 'fastify';
import type { ITenantStore } from '../persistence/tenant-store.js';
import type { ITenantMembershipStore } from '../persistence/tenant-membership-store.js';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

interface AuthUser {
  id: string;
  tenantId?: string;
  username?: string;
  [key: string]: unknown;
}

export interface EnsurePersonalTenantOptions {
  tenantStore: ITenantStore;
  membershipStore: ITenantMembershipStore;
}

// ---------------------------------------------------------------------------
// Factory
// ---------------------------------------------------------------------------

/**
 * Create a preHandler hook that ensures the authenticated user has a tenant.
 */
export function createEnsurePersonalTenant(options: EnsurePersonalTenantOptions) {
  const { tenantStore, membershipStore } = options;

  return async (request: FastifyRequest, _reply: FastifyReply): Promise<void> => {
    const reqWithAuth = request as FastifyRequest & { authUser?: AuthUser };
    const user = reqWithAuth.authUser;

    // Not authenticated or already has a tenant — no-op
    if (!user || user.tenantId) return;

    const userId = user.id;

    // Check existing memberships
    const memberships = await membershipStore.getUserTenants(userId);
    if (memberships.length > 0) {
      // Pick most recent membership
      const sorted = [...memberships].sort(
        (a, b) => new Date(b.joinedAt).getTime() - new Date(a.joinedAt).getTime(),
      );
      user.tenantId = sorted[0]!.tenantId;
      return;
    }

    // Auto-create personal tenant
    const baseName = user.username ?? 'User';
    const name = `${baseName}'s Workspace`;
    const baseSlug = `u-${userId.slice(0, 8)}`;

    // Attempt slug with collision retry (add random suffix)
    let slug = baseSlug;
    let attempts = 0;
    const MAX_ATTEMPTS = 5;
    while (attempts < MAX_ATTEMPTS) {
      const existing = await tenantStore.getTenantBySlug(slug);
      if (!existing) break;
      attempts++;
      slug = `${baseSlug}-${Math.random().toString(36).slice(2, 6)}`;
    }

    const tenant = await tenantStore.createTenant({ name, slug });
    await membershipStore.addMember(tenant.id, userId, 'owner');
    user.tenantId = tenant.id;
  };
}
