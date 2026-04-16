/**
 * Organization / Tenant Routes (Story 18-3)
 *
 * Organization = Tenant from Epic 17. No separate organizations table.
 *
 * Endpoints:
 *   POST   /api/v1/orgs                              — Create organization
 *   GET    /api/v1/orgs/:tenantId                     — Get organization details
 *   PUT    /api/v1/orgs/:tenantId/settings            — Update org settings
 *   GET    /api/v1/orgs/:tenantId/members             — List members
 *   PUT    /api/v1/orgs/:tenantId/members/:userId/role — Update member role
 *   DELETE /api/v1/orgs/:tenantId/members/:userId     — Remove member
 *   POST   /api/v1/orgs/:tenantId/invites             — Send invite
 *   GET    /api/v1/orgs/:tenantId/invites             — List pending invites
 *   DELETE /api/v1/orgs/:tenantId/invites/:inviteId   — Revoke invite
 *   POST   /api/v1/orgs/invites/accept                — Accept invite
 *   POST   /api/v1/auth/switch-org                    — Switch active tenant
 *   GET    /api/v1/tenants                            — List user's tenants
 *   POST   /api/v1/orgs/:tenantId/transfer-ownership  — Transfer ownership
 *   DELETE /api/v1/orgs/:tenantId                     — Soft / hard delete tenant
 */

import { createHash, createHmac, randomBytes } from 'node:crypto';
import type { FastifyInstance, FastifyRequest, FastifyReply } from 'fastify';
import type { ITenantStore } from '../../persistence/tenant-store.js';
import type { IUserStore } from '../../persistence/user-store.js';
import type { ITenantMembershipStore } from '../../persistence/tenant-membership-store.js';
import type { IEmailService } from '../../services/email.js';
import { buildTenantInviteEmail } from '../../services/email.js';
import type { UnifiedJwtPayload } from '../../auth/jwt.js';
import { buildJwtClaims } from '../../auth/jwt.js';

export interface OrgRoutesOptions {
  tenantStore: ITenantStore;
  userStore: IUserStore;
  membershipStore: ITenantMembershipStore;
  emailService: IEmailService;
  jwtSecret: string;
}

/** Reserved org slugs that cannot be used. */
const RESERVED_SLUGS = new Set([
  'admin', 'api', 'auth', 'settings', 'app', 'www',
  'dashboard', 'login', 'register', 'signup', 'signin',
  'default', 'help', 'support', 'docs', 'blog',
]);

/** Slug validation regex: lowercase alphanumeric + hyphens, 3-40 chars. */
const SLUG_REGEX = /^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$/;

/** Tenant role hierarchy for permission checks. */
const ROLE_HIERARCHY: Record<string, number> = {
  member: 0,
  admin: 1,
  owner: 2,
};

export async function registerOrgRoutes(
  app: FastifyInstance,
  options: OrgRoutesOptions,
): Promise<void> {
  const { tenantStore, userStore, membershipStore, emailService } = options;

  // Ensure JWT plugin is registered
  if (!app.hasDecorator('jwt')) {
    await app.register(await import('@fastify/jwt').then((m) => m.default ?? m), {
      secret: options.jwtSecret,
      cookie: { cookieName: 'tamma_session', signed: false },
    });
  }

  // Ensure cookie plugin is registered
  if (!app.hasDecorator('parseCookie')) {
    await app.register(await import('@fastify/cookie').then((m) => m.default ?? m));
  }

  // -------------------------------------------------------------------
  // Helper: extract and verify JWT user
  // -------------------------------------------------------------------
  async function getAuthenticatedUser(request: FastifyRequest, reply: FastifyReply): Promise<UnifiedJwtPayload | null> {
    try {
      return await request.jwtVerify<UnifiedJwtPayload>();
    } catch {
      reply.status(401).send({ error: 'Not authenticated' });
      return null;
    }
  }

  // -------------------------------------------------------------------
  // POST /api/v1/orgs — Create organization
  // -------------------------------------------------------------------
  app.post<{
    Body: { name?: string; slug?: string };
  }>(
    '/api/v1/orgs',
    async (request: FastifyRequest<{ Body: { name?: string; slug?: string } }>, reply: FastifyReply) => {
      const jwt = await getAuthenticatedUser(request, reply);
      if (!jwt) return;

      const { name, slug } = request.body ?? {};

      if (!name || !slug) {
        return reply.status(400).send({ error: 'name and slug are required' });
      }

      if (typeof name !== 'string' || name.trim().length < 2 || name.trim().length > 100) {
        return reply.status(400).send({ error: 'Name must be between 2 and 100 characters' });
      }

      // Validate slug
      if (!SLUG_REGEX.test(slug)) {
        return reply.status(400).send({
          error: 'Slug must be 3-40 characters, lowercase alphanumeric and hyphens only, cannot start or end with hyphen',
        });
      }

      if (RESERVED_SLUGS.has(slug)) {
        return reply.status(400).send({ error: 'This slug is reserved and cannot be used' });
      }

      // Check slug uniqueness
      const existingTenant = await tenantStore.getTenantBySlug(slug);
      if (existingTenant) {
        return reply.status(409).send({ error: 'An organization with this slug already exists' });
      }

      // Create tenant
      const tenant = await tenantStore.createTenant({
        name: name.trim(),
        slug,
      });

      // Add current user as owner
      await membershipStore.addMember(tenant.id, jwt.sub, 'owner');

      // Set as active tenant
      await userStore.updateActiveTenant(jwt.sub, tenant.id);

      request.log.info({
        event: 'TENANT.CREATED.SUCCESS',
        tenantId: tenant.id,
        userId: jwt.sub,
      }, 'Organization created');

      return reply.status(201).send({
        id: tenant.id,
        name: tenant.name,
        slug: tenant.slug,
        plan: tenant.plan,
      });
    },
  );

  // -------------------------------------------------------------------
  // GET /api/v1/orgs/:tenantId — Get organization details
  // -------------------------------------------------------------------
  app.get<{
    Params: { tenantId: string };
  }>(
    '/api/v1/orgs/:tenantId',
    async (request: FastifyRequest<{ Params: { tenantId: string } }>, reply: FastifyReply) => {
      const jwt = await getAuthenticatedUser(request, reply);
      if (!jwt) return;

      const { tenantId } = request.params;

      // Verify membership
      const membership = await membershipStore.getMembership(tenantId, jwt.sub);
      if (!membership) {
        return reply.status(403).send({ error: 'Not a member of this organization' });
      }

      const tenant = await tenantStore.getTenant(tenantId);
      if (!tenant) {
        return reply.status(404).send({ error: 'Organization not found' });
      }

      return reply.send({
        id: tenant.id,
        name: tenant.name,
        slug: tenant.slug,
        plan: tenant.plan,
        settings: tenant.settings,
        createdAt: tenant.createdAt,
        yourRole: membership.role,
      });
    },
  );

  // -------------------------------------------------------------------
  // PUT /api/v1/orgs/:tenantId/settings — Update org settings
  // -------------------------------------------------------------------
  app.put<{
    Params: { tenantId: string };
    Body: { name?: string; settings?: Record<string, unknown> };
  }>(
    '/api/v1/orgs/:tenantId/settings',
    async (request: FastifyRequest<{ Params: { tenantId: string }; Body: { name?: string; settings?: Record<string, unknown> } }>, reply: FastifyReply) => {
      const jwt = await getAuthenticatedUser(request, reply);
      if (!jwt) return;

      const { tenantId } = request.params;

      // Verify admin+ role
      const membership = await membershipStore.getMembership(tenantId, jwt.sub);
      if (!membership || (ROLE_HIERARCHY[membership.role] ?? 0) < (ROLE_HIERARCHY['admin'] ?? 1)) {
        return reply.status(403).send({ error: 'Requires admin role or higher' });
      }

      const { name, settings } = request.body ?? {};
      const update: Partial<{ name: string; settings: Record<string, unknown> }> = {};
      if (name !== undefined) {
        if (typeof name !== 'string' || name.trim().length < 2 || name.trim().length > 100) {
          return reply.status(400).send({ error: 'Name must be between 2 and 100 characters' });
        }
        update.name = name.trim();
      }
      if (settings !== undefined) {
        update.settings = settings;
      }

      if (Object.keys(update).length === 0) {
        return reply.status(400).send({ error: 'No fields to update' });
      }

      const tenant = await tenantStore.updateTenant(tenantId, update);

      return reply.send({
        id: tenant.id,
        name: tenant.name,
        slug: tenant.slug,
        plan: tenant.plan,
        settings: tenant.settings,
      });
    },
  );

  // -------------------------------------------------------------------
  // GET /api/v1/orgs/:tenantId/members — List members
  // -------------------------------------------------------------------
  app.get<{
    Params: { tenantId: string };
    Querystring: { limit?: string; offset?: string };
  }>(
    '/api/v1/orgs/:tenantId/members',
    async (request: FastifyRequest<{ Params: { tenantId: string }; Querystring: { limit?: string; offset?: string } }>, reply: FastifyReply) => {
      const jwt = await getAuthenticatedUser(request, reply);
      if (!jwt) return;

      const { tenantId } = request.params;

      // Verify membership
      const membership = await membershipStore.getMembership(tenantId, jwt.sub);
      if (!membership) {
        return reply.status(403).send({ error: 'Not a member of this organization' });
      }

      const limit = Math.min(parseInt(request.query.limit ?? '50', 10) || 50, 100);
      const offset = parseInt(request.query.offset ?? '0', 10) || 0;

      const result = await membershipStore.listMembers({ tenantId, limit, offset });

      return reply.send({
        members: result.members,
        total: result.total,
        limit,
        offset,
      });
    },
  );

  // -------------------------------------------------------------------
  // PUT /api/v1/orgs/:tenantId/members/:userId/role — Update member role
  // -------------------------------------------------------------------
  app.put<{
    Params: { tenantId: string; userId: string };
    Body: { role?: string };
  }>(
    '/api/v1/orgs/:tenantId/members/:userId/role',
    async (request: FastifyRequest<{ Params: { tenantId: string; userId: string }; Body: { role?: string } }>, reply: FastifyReply) => {
      const jwt = await getAuthenticatedUser(request, reply);
      if (!jwt) return;

      const { tenantId, userId } = request.params;
      const { role } = request.body ?? {};

      if (!role || !['owner', 'admin', 'member'].includes(role)) {
        return reply.status(400).send({ error: 'role must be one of: owner, admin, member' });
      }

      // Get requester's membership
      const requesterMembership = await membershipStore.getMembership(tenantId, jwt.sub);
      if (!requesterMembership) {
        return reply.status(403).send({ error: 'Not a member of this organization' });
      }

      // Get target's membership
      const targetMembership = await membershipStore.getMembership(tenantId, userId);
      if (!targetMembership) {
        return reply.status(404).send({ error: 'User is not a member of this organization' });
      }

      const requesterLevel = ROLE_HIERARCHY[requesterMembership.role] ?? 0;
      const targetLevel = ROLE_HIERARCHY[targetMembership.role] ?? 0;
      const newLevel = ROLE_HIERARCHY[role] ?? 0;

      // Only owner can change roles to/from owner level
      if (requesterMembership.role !== 'owner' && (newLevel >= (ROLE_HIERARCHY['owner'] ?? 2) || targetLevel >= (ROLE_HIERARCHY['owner'] ?? 2))) {
        return reply.status(403).send({ error: 'Only owners can change owner-level roles' });
      }

      // Admin can only change member roles
      if (requesterMembership.role === 'admin') {
        if (targetLevel >= requesterLevel) {
          return reply.status(403).send({ error: 'Cannot change role of users at or above your level' });
        }
        if (newLevel >= requesterLevel) {
          return reply.status(403).send({ error: 'Cannot promote users to or above your level' });
        }
      }

      // If demoting from owner, ensure at least one owner remains
      if (targetMembership.role === 'owner' && role !== 'owner') {
        const ownerCount = await membershipStore.countOwners(tenantId);
        if (ownerCount <= 1) {
          return reply.status(400).send({ error: 'Cannot remove the last owner' });
        }
      }

      const updated = await membershipStore.updateMemberRole(tenantId, userId, role as 'owner' | 'admin' | 'member');

      return reply.send({ membership: updated });
    },
  );

  // -------------------------------------------------------------------
  // DELETE /api/v1/orgs/:tenantId/members/:userId — Remove member
  // -------------------------------------------------------------------
  app.delete<{
    Params: { tenantId: string; userId: string };
  }>(
    '/api/v1/orgs/:tenantId/members/:userId',
    async (request: FastifyRequest<{ Params: { tenantId: string; userId: string } }>, reply: FastifyReply) => {
      const jwt = await getAuthenticatedUser(request, reply);
      if (!jwt) return;

      const { tenantId, userId } = request.params;

      // Get requester's membership
      const requesterMembership = await membershipStore.getMembership(tenantId, jwt.sub);
      if (!requesterMembership) {
        return reply.status(403).send({ error: 'Not a member of this organization' });
      }

      const requesterLevel = ROLE_HIERARCHY[requesterMembership.role] ?? 0;

      // Must be admin+
      if (requesterLevel < (ROLE_HIERARCHY['admin'] ?? 1)) {
        return reply.status(403).send({ error: 'Requires admin role or higher' });
      }

      // Get target's membership
      const targetMembership = await membershipStore.getMembership(tenantId, userId);
      if (!targetMembership) {
        return reply.status(404).send({ error: 'User is not a member of this organization' });
      }

      // Cannot remove self if last owner
      if (userId === jwt.sub && targetMembership.role === 'owner') {
        const ownerCount = await membershipStore.countOwners(tenantId);
        if (ownerCount <= 1) {
          return reply.status(400).send({ error: 'Cannot remove yourself as the last owner' });
        }
      }

      // Admins cannot remove owners
      if (requesterMembership.role !== 'owner' && targetMembership.role === 'owner') {
        return reply.status(403).send({ error: 'Cannot remove an owner' });
      }

      await membershipStore.removeMember(tenantId, userId);

      // If removed user's active tenant was this one, clear it
      const user = await userStore.getUser(userId);
      if (user && user.tenantId === tenantId) {
        await userStore.updateActiveTenant(userId, null);
      }

      request.log.info({
        event: 'TENANT.MEMBER_REMOVED.SUCCESS',
        tenantId,
        userId,
        removedBy: jwt.sub,
      }, 'Member removed from organization');

      return reply.send({ ok: true });
    },
  );

  // -------------------------------------------------------------------
  // POST /api/v1/orgs/:tenantId/invites — Send invite
  // -------------------------------------------------------------------
  app.post<{
    Params: { tenantId: string };
    Body: { email?: string; role?: string };
  }>(
    '/api/v1/orgs/:tenantId/invites',
    async (request: FastifyRequest<{ Params: { tenantId: string }; Body: { email?: string; role?: string } }>, reply: FastifyReply) => {
      const jwt = await getAuthenticatedUser(request, reply);
      if (!jwt) return;

      const { tenantId } = request.params;
      const { email, role } = request.body ?? {};

      if (!email) {
        return reply.status(400).send({ error: 'email is required' });
      }

      const inviteRole = role ?? 'member';
      if (!['owner', 'admin', 'member'].includes(inviteRole)) {
        return reply.status(400).send({ error: 'role must be one of: owner, admin, member' });
      }

      // Verify admin+ role
      const membership = await membershipStore.getMembership(tenantId, jwt.sub);
      if (!membership || (ROLE_HIERARCHY[membership.role] ?? 0) < (ROLE_HIERARCHY['admin'] ?? 1)) {
        return reply.status(403).send({ error: 'Requires admin role or higher to invite' });
      }

      // Get tenant for email
      const tenant = await tenantStore.getTenant(tenantId);
      if (!tenant) {
        return reply.status(404).send({ error: 'Organization not found' });
      }

      // Generate invite token
      const rawToken = randomBytes(32).toString('hex');
      const tokenHash = createHash('sha256').update(rawToken).digest('hex');
      const expiresAt = new Date(Date.now() + 72 * 60 * 60 * 1000).toISOString(); // 72 hours

      const invite = await membershipStore.createInvite({
        tenantId,
        email: email.toLowerCase().trim(),
        role: inviteRole as 'owner' | 'admin' | 'member',
        inviteTokenHash: tokenHash,
        invitedBy: jwt.sub,
        expiresAt,
      });

      // Send invite email
      const inviterName = jwt.name || jwt.email;
      emailService.sendEmail(
        buildTenantInviteEmail(email.toLowerCase().trim(), tenant.name, inviterName, rawToken, inviteRole),
      ).catch((err) => {
        request.log.error({ err, inviteId: invite.id }, 'Failed to send invite email');
      });

      request.log.info({
        event: 'TENANT.MEMBER_INVITED.SUCCESS',
        tenantId,
        email: email.toLowerCase().trim(),
        invitedBy: jwt.sub,
      }, 'Tenant invite sent');

      return reply.status(201).send({
        id: invite.id,
        email: invite.email,
        role: invite.role,
        expiresAt: invite.expiresAt,
      });
    },
  );

  // -------------------------------------------------------------------
  // GET /api/v1/orgs/:tenantId/invites — List pending invites
  // -------------------------------------------------------------------
  app.get<{
    Params: { tenantId: string };
  }>(
    '/api/v1/orgs/:tenantId/invites',
    async (request: FastifyRequest<{ Params: { tenantId: string } }>, reply: FastifyReply) => {
      const jwt = await getAuthenticatedUser(request, reply);
      if (!jwt) return;

      const { tenantId } = request.params;

      // Verify admin+ role
      const membership = await membershipStore.getMembership(tenantId, jwt.sub);
      if (!membership || (ROLE_HIERARCHY[membership.role] ?? 0) < (ROLE_HIERARCHY['admin'] ?? 1)) {
        return reply.status(403).send({ error: 'Requires admin role or higher' });
      }

      const invites = await membershipStore.listPendingInvites(tenantId);

      return reply.send({
        invites: invites.map((inv) => ({
          id: inv.id,
          email: inv.email,
          role: inv.role,
          invitedBy: inv.invitedBy,
          expiresAt: inv.expiresAt,
          createdAt: inv.createdAt,
        })),
      });
    },
  );

  // -------------------------------------------------------------------
  // DELETE /api/v1/orgs/:tenantId/invites/:inviteId — Revoke invite
  // -------------------------------------------------------------------
  app.delete<{
    Params: { tenantId: string; inviteId: string };
  }>(
    '/api/v1/orgs/:tenantId/invites/:inviteId',
    async (request: FastifyRequest<{ Params: { tenantId: string; inviteId: string } }>, reply: FastifyReply) => {
      const jwt = await getAuthenticatedUser(request, reply);
      if (!jwt) return;

      const { tenantId, inviteId } = request.params;

      // Verify admin+ role
      const membership = await membershipStore.getMembership(tenantId, jwt.sub);
      if (!membership || (ROLE_HIERARCHY[membership.role] ?? 0) < (ROLE_HIERARCHY['admin'] ?? 1)) {
        return reply.status(403).send({ error: 'Requires admin role or higher' });
      }

      try {
        await membershipStore.revokeInvite(inviteId);
      } catch {
        return reply.status(404).send({ error: 'Invite not found' });
      }

      return reply.send({ ok: true });
    },
  );

  // -------------------------------------------------------------------
  // POST /api/v1/orgs/invites/accept — Accept invite
  // -------------------------------------------------------------------
  app.post<{
    Body: { token?: string };
  }>(
    '/api/v1/orgs/invites/accept',
    async (request: FastifyRequest<{ Body: { token?: string } }>, reply: FastifyReply) => {
      const jwt = await getAuthenticatedUser(request, reply);
      if (!jwt) return;

      const { token } = request.body ?? {};

      if (!token) {
        return reply.status(400).send({ error: 'token is required' });
      }

      // Hash the incoming token
      const tokenHash = createHash('sha256').update(token).digest('hex');
      const invite = await membershipStore.getInviteByTokenHash(tokenHash);

      if (!invite) {
        return reply.status(400).send({ error: 'Invalid or expired invite token' });
      }

      // Check if already accepted
      if (invite.acceptedAt !== null) {
        return reply.status(400).send({ error: 'Invite has already been accepted' });
      }

      // Check expiry
      if (new Date(invite.expiresAt) < new Date()) {
        return reply.status(400).send({ error: 'Invite has expired' });
      }

      // Check if already a member
      const existingMembership = await membershipStore.getMembership(invite.tenantId, jwt.sub);
      if (existingMembership) {
        // Mark invite as accepted anyway
        await membershipStore.acceptInvite(invite.id);
        return reply.send({ message: 'You are already a member of this organization' });
      }

      // Accept invite
      await membershipStore.acceptInvite(invite.id);

      // Add as member
      await membershipStore.addMember(invite.tenantId, jwt.sub, invite.role);

      // Set as active tenant if user doesn't have one
      const user = await userStore.getUser(jwt.sub);
      if (user && !user.tenantId) {
        await userStore.updateActiveTenant(jwt.sub, invite.tenantId);
      }

      request.log.info({
        event: 'TENANT.MEMBER_JOINED.SUCCESS',
        tenantId: invite.tenantId,
        userId: jwt.sub,
        role: invite.role,
      }, 'User joined organization via invite');

      return reply.send({
        tenantId: invite.tenantId,
        role: invite.role,
        message: 'You have joined the organization',
      });
    },
  );

  // -------------------------------------------------------------------
  // POST /api/v1/auth/switch-org — Switch active tenant
  // -------------------------------------------------------------------
  app.post<{
    Body: { tenantId?: string };
  }>(
    '/api/v1/auth/switch-org',
    async (request: FastifyRequest<{ Body: { tenantId?: string } }>, reply: FastifyReply) => {
      const jwt = await getAuthenticatedUser(request, reply);
      if (!jwt) return;

      const { tenantId } = request.body ?? {};

      if (!tenantId) {
        return reply.status(400).send({ error: 'tenantId is required' });
      }

      // Verify membership in target tenant
      const membership = await membershipStore.getMembership(tenantId, jwt.sub);
      if (!membership) {
        return reply.status(403).send({ error: 'Not a member of the target organization' });
      }

      // Update active tenant
      await userStore.updateActiveTenant(jwt.sub, tenantId);

      // Issue new JWT with updated tenant
      const user = await userStore.getUser(jwt.sub);
      if (!user) {
        return reply.status(401).send({ error: 'User not found' });
      }

      const displayName = user.githubLogin || (user.email?.split('@')[0]) || 'User';
      const claims = buildJwtClaims(
        user.id,
        user.email ?? '',
        displayName,
        tenantId,
        membership.role,
        user.role === 'owner' ? 'platform_admin' : 'user',
        user.authMethod,
      );

      const accessToken = app.jwt.sign(claims as Record<string, unknown>);

      // Set session cookie with new JWT
      reply.setCookie('tamma_session', accessToken, {
        path: '/',
        httpOnly: true,
        secure: true,
        sameSite: 'lax' as const,
        maxAge: 900,
        domain: '.tamma.dev',
      });

      return reply.send({
        accessToken,
        tenantId,
        role: membership.role,
      });
    },
  );

  // -------------------------------------------------------------------
  // GET /api/v1/tenants — List user's tenants
  // -------------------------------------------------------------------
  app.get(
    '/api/v1/tenants',
    async (request: FastifyRequest, reply: FastifyReply) => {
      const jwt = await getAuthenticatedUser(request, reply);
      if (!jwt) return;

      const memberships = await membershipStore.getUserTenants(jwt.sub);

      const tenants = await Promise.all(
        memberships.map(async (m) => {
          const tenant = await tenantStore.getTenant(m.tenantId);
          return {
            id: m.tenantId,
            name: tenant?.name ?? 'Unknown',
            slug: tenant?.slug ?? '',
            plan: tenant?.plan ?? 'free',
            role: m.role,
            joinedAt: m.joinedAt,
            isActive: m.tenantId === jwt.tenantId,
          };
        }),
      );

      return reply.send({ tenants });
    },
  );

  // -------------------------------------------------------------------
  // POST /api/v1/orgs/:tenantId/transfer-ownership — Transfer ownership
  // -------------------------------------------------------------------
  app.post<{
    Params: { tenantId: string };
    Body: { newOwnerUserId?: string };
  }>(
    '/api/v1/orgs/:tenantId/transfer-ownership',
    async (request: FastifyRequest<{ Params: { tenantId: string }; Body: { newOwnerUserId?: string } }>, reply: FastifyReply) => {
      const jwt = await getAuthenticatedUser(request, reply);
      if (!jwt) return;

      const { tenantId } = request.params;
      const { newOwnerUserId } = request.body ?? {};

      if (!newOwnerUserId) {
        return reply.status(400).send({ error: 'newOwnerUserId is required' });
      }

      // Verify requester is owner
      const requesterMembership = await membershipStore.getMembership(tenantId, jwt.sub);
      if (!requesterMembership || requesterMembership.role !== 'owner') {
        return reply.status(403).send({ error: 'Only the owner can transfer ownership' });
      }

      // Cannot transfer to self
      if (newOwnerUserId === jwt.sub) {
        return reply.status(400).send({ error: 'same_user' });
      }

      // Verify tenant is not soft-deleted
      const tenant = await tenantStore.getTenant(tenantId);
      if (!tenant) {
        return reply.status(404).send({ error: 'Tenant not found or deleted' });
      }

      // Verify new owner is a member
      const newOwnerMembership = await membershipStore.getMembership(tenantId, newOwnerUserId);
      if (!newOwnerMembership) {
        return reply.status(400).send({ error: 'not_a_member' });
      }

      // Transfer: demote old owner to admin, promote new owner to owner
      await membershipStore.updateMemberRole(tenantId, jwt.sub, 'admin');
      await membershipStore.updateMemberRole(tenantId, newOwnerUserId, 'owner');

      request.log.info({
        event: 'TENANT.OWNERSHIP_TRANSFERRED.SUCCESS',
        tenantId,
        previousOwnerId: jwt.sub,
        newOwnerId: newOwnerUserId,
      }, 'Tenant ownership transferred');

      return reply.send({
        tenantId,
        previousOwnerId: jwt.sub,
        newOwnerId: newOwnerUserId,
      });
    },
  );

  // -------------------------------------------------------------------
  // DELETE /api/v1/orgs/:tenantId — Soft-delete or hard-delete tenant
  //
  // Without ?confirm: soft-delete, returns 202 with HMAC confirmation token
  // With ?confirm=<token>: hard-delete (cascade), returns 204
  // -------------------------------------------------------------------
  app.delete<{
    Params: { tenantId: string };
    Querystring: { confirm?: string; force?: string };
  }>(
    '/api/v1/orgs/:tenantId',
    async (request: FastifyRequest<{ Params: { tenantId: string }; Querystring: { confirm?: string; force?: string } }>, reply: FastifyReply) => {
      const jwt = await getAuthenticatedUser(request, reply);
      if (!jwt) return;

      const { tenantId } = request.params;
      const confirmToken = request.query.confirm;

      // Verify requester is owner
      const requesterMembership = await membershipStore.getMembership(tenantId, jwt.sub);
      if (!requesterMembership || requesterMembership.role !== 'owner') {
        return reply.status(403).send({ error: 'Only the owner can delete the organization' });
      }

      // Guard: cannot delete the last tenant the user belongs to
      const userTenants = await membershipStore.getUserTenants(jwt.sub);
      if (userTenants.length <= 1) {
        return reply.status(409).send({ error: 'last_tenant', message: 'Cannot delete your only organization. Create a replacement first.' });
      }

      const tenant = await tenantStore.getTenant(tenantId);
      if (!tenant) {
        return reply.status(404).send({ error: 'Tenant not found' });
      }

      if (confirmToken) {
        // Hard-delete path: verify HMAC token
        const isValid = verifyDeleteConfirmation(confirmToken, tenantId, jwt.sub, options.jwtSecret);
        if (!isValid) {
          return reply.status(400).send({ error: 'confirmation_expired', message: 'Invalid or expired confirmation token' });
        }

        // Hard delete: remove memberships, invites, then tenant
        // In a real Pg setup, ON DELETE CASCADE handles this.
        // For in-memory stores, we manually clean up.
        const members = await membershipStore.listMembers({ tenantId, limit: 10000, offset: 0 });
        for (const member of members.members) {
          await membershipStore.removeMember(tenantId, member.userId);
          // If this was the user's active tenant, clear it
          const memberUser = await userStore.getUser(member.userId);
          if (memberUser && memberUser.tenantId === tenantId) {
            await userStore.updateActiveTenant(member.userId, null);
          }
        }

        // Delete the tenant (soft-delete in store, but we've cascaded memberships)
        await tenantStore.deleteTenant(tenantId);

        request.log.info({
          event: 'TENANT.PURGED.SUCCESS',
          tenantId,
          userId: jwt.sub,
        }, 'Tenant hard-deleted');

        return reply.status(204).send();
      }

      // Soft-delete path
      await tenantStore.deleteTenant(tenantId);

      // Generate HMAC confirmation token for potential hard-delete (10-minute TTL)
      const confirmation = generateDeleteConfirmation(tenantId, jwt.sub, options.jwtSecret);

      // If the user's active tenant was this one, switch to another
      const user = await userStore.getUser(jwt.sub);
      if (user && user.tenantId === tenantId) {
        const otherTenant = userTenants.find((t) => t.tenantId !== tenantId);
        if (otherTenant) {
          await userStore.updateActiveTenant(jwt.sub, otherTenant.tenantId);
        }
      }

      request.log.info({
        event: 'TENANT.DELETED.SUCCESS',
        tenantId,
        userId: jwt.sub,
      }, 'Tenant soft-deleted');

      return reply.status(202).send({
        message: 'Organization has been soft-deleted',
        confirmationToken: confirmation.token,
        expiresAt: confirmation.expiresAt,
      });
    },
  );
}

// -------------------------------------------------------------------
// HMAC-based delete confirmation helpers
// -------------------------------------------------------------------

const DELETE_CONFIRM_TTL_MS = 10 * 60 * 1000; // 10 minutes

function generateDeleteConfirmation(tenantId: string, userId: string, secret: string): { token: string; expiresAt: string } {
  const issuedAt = Date.now();
  const payload = `${tenantId}:${userId}:${issuedAt}`;
  const hmac = createHmac('sha256', secret).update(payload).digest('hex');
  const token = `${issuedAt}.${hmac}`;
  return {
    token,
    expiresAt: new Date(issuedAt + DELETE_CONFIRM_TTL_MS).toISOString(),
  };
}

function verifyDeleteConfirmation(token: string, tenantId: string, userId: string, secret: string): boolean {
  const dotIndex = token.indexOf('.');
  if (dotIndex === -1) return false;

  const issuedAtStr = token.substring(0, dotIndex);
  const providedHmac = token.substring(dotIndex + 1);

  const issuedAt = parseInt(issuedAtStr, 10);
  if (isNaN(issuedAt)) return false;

  // Check TTL
  if (Date.now() - issuedAt > DELETE_CONFIRM_TTL_MS) return false;

  // Verify HMAC
  const payload = `${tenantId}:${userId}:${issuedAt}`;
  const expectedHmac = createHmac('sha256', secret).update(payload).digest('hex');

  // Constant-time comparison
  if (providedHmac.length !== expectedHmac.length) return false;
  const a = Buffer.from(providedHmac, 'hex');
  const b = Buffer.from(expectedHmac, 'hex');
  if (a.length !== b.length) return false;

  let diff = 0;
  for (let i = 0; i < a.length; i++) {
    diff |= (a[i] ?? 0) ^ (b[i] ?? 0);
  }
  return diff === 0;
}
