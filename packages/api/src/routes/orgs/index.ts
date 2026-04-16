/**
 * Organization (Tenant) Routes
 *
 * Full tenant lifecycle:
 *   POST   /api/v1/orgs                          — create tenant
 *   GET    /api/v1/orgs                           — list my tenants
 *   GET    /api/v1/orgs/:id                       — get tenant
 *   PATCH  /api/v1/orgs/:id                       — rename tenant (owner)
 *   PATCH  /api/v1/orgs/:id/settings              — update settings (admin+)
 *   DELETE /api/v1/orgs/:id                       — soft/hard delete (owner)
 *   POST   /api/v1/orgs/:id/transfer-ownership    — transfer ownership (owner)
 *   GET    /api/v1/orgs/:id/members               — list members
 *   PUT    /api/v1/orgs/:id/members/:userId/role  — update member role
 *   DELETE /api/v1/orgs/:id/members/:userId       — remove member
 *   POST   /api/v1/orgs/:id/invites               — invite member (admin+)
 *   GET    /api/v1/orgs/:id/invites               — list pending invites
 *   DELETE /api/v1/orgs/:id/invites/:inviteId     — revoke invite
 *   POST   /api/v1/orgs/:id/invites/:token/accept — accept invite
 *   POST   /api/v1/auth/switch-org                — switch active tenant
 */

import { createHmac, timingSafeEqual } from 'node:crypto';
import type { FastifyInstance, FastifyRequest, FastifyReply } from 'fastify';
import type { ITenantStore } from '../../persistence/tenant-store.js';
import type { ITenantMembershipStore } from '../../persistence/tenant-membership-store.js';
import { generateToken, hashToken } from '../../persistence/tenant-membership-store.js';
import type { IUserStore } from '../../persistence/user-store.js';
import type { IEmailService } from '../../services/email.js';
import { buildTenantInviteEmail } from '../../services/email.js';

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** Slug validation: lowercase alphanumeric + hyphens, 3-40 chars. */
const SLUG_REGEX = /^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$/;

const RESERVED_SLUGS = new Set([
  'admin', 'api', 'auth', 'settings', 'app', 'www',
  'help', 'support', 'billing', 'dashboard', 'login',
  'signup', 'register', 'system', 'tamma',
]);

/** Role hierarchy for RBAC checks. */
const ROLE_HIERARCHY: Record<string, number> = {
  member: 0,
  admin: 1,
  owner: 2,
};

/** Default invite expiry: 72 hours. */
const DEFAULT_INVITE_TTL_HOURS = 72;

/** HMAC confirmation token TTL: 10 minutes. */
const CONFIRM_TOKEN_TTL_MS = 10 * 60 * 1000;

// ---------------------------------------------------------------------------
// Options
// ---------------------------------------------------------------------------

export interface OrgRoutesOptions {
  tenantStore: ITenantStore;
  membershipStore: ITenantMembershipStore;
  userStore: IUserStore;
  emailService: IEmailService;
  jwtSecret: string;
  frontendUrl?: string;
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

interface AuthUser {
  id: string;
  role: string;
  email?: string;
  username?: string;
}

function getAuthUser(request: FastifyRequest): AuthUser | null {
  const req = request as FastifyRequest & { authUser?: AuthUser };
  return req.authUser ?? null;
}

function requireAuth(request: FastifyRequest, reply: FastifyReply): AuthUser | null {
  const user = getAuthUser(request);
  if (!user) {
    reply.status(401).send({ error: 'Not authenticated' });
    return null;
  }
  return user;
}

function isValidSlug(slug: string): boolean {
  return SLUG_REGEX.test(slug) && !RESERVED_SLUGS.has(slug);
}

function isValidEmail(email: string): boolean {
  if (email.length > 254) return false;
  // Linear-time regex — domain labels [a-zA-Z0-9-]+ separated by literal dots
  return /^[a-zA-Z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-zA-Z0-9](?:[a-zA-Z0-9-]*[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]*[a-zA-Z0-9])?)*\.[a-zA-Z]{2,}$/.test(email);
}

function generateConfirmToken(tenantId: string, userId: string, jwtSecret: string): { token: string; expiresAt: string } {
  const issuedAt = Date.now();
  const payload = `${tenantId}:${userId}:${issuedAt}`;
  const hmac = createHmac('sha256', jwtSecret).update(payload).digest('hex');
  const token = `${issuedAt}:${hmac}`;
  return {
    token,
    expiresAt: new Date(issuedAt + CONFIRM_TOKEN_TTL_MS).toISOString(),
  };
}

function verifyConfirmToken(token: string, tenantId: string, userId: string, jwtSecret: string): boolean {
  const parts = token.split(':');
  if (parts.length !== 2) return false;
  const issuedAtStr = parts[0]!;
  const providedHmac = parts[1]!;

  const issuedAt = parseInt(issuedAtStr, 10);
  if (isNaN(issuedAt)) return false;

  // Check TTL
  if (Date.now() - issuedAt > CONFIRM_TOKEN_TTL_MS) return false;

  // Recompute HMAC
  const payload = `${tenantId}:${userId}:${issuedAt}`;
  const expectedHmac = createHmac('sha256', jwtSecret).update(payload).digest('hex');

  try {
    return (
      providedHmac.length === expectedHmac.length &&
      timingSafeEqual(Buffer.from(providedHmac), Buffer.from(expectedHmac))
    );
  } catch {
    return false;
  }
}

// ---------------------------------------------------------------------------
// Route registration
// ---------------------------------------------------------------------------

export async function registerOrgRoutes(
  app: FastifyInstance,
  options: OrgRoutesOptions,
): Promise<void> {
  const {
    tenantStore,
    membershipStore,
    userStore: _userStore,
    emailService,
    jwtSecret,
    frontendUrl = 'https://app.tamma.dev',
  } = options;

  const inviteTtlHours = parseInt(process.env['TAMMA_INVITE_TTL_HOURS'] ?? '', 10) || DEFAULT_INVITE_TTL_HOURS;

  // -------------------------------------------------------------------------
  // POST /api/v1/orgs — create tenant
  // -------------------------------------------------------------------------
  app.post('/api/v1/orgs', async (request, reply) => {
    const user = requireAuth(request, reply);
    if (!user) return;

    const body = request.body as { name?: string; slug?: string; plan?: string } | null;
    const name = body?.name?.trim();
    const slug = body?.slug?.toLowerCase().trim();
    const plan = body?.plan as 'free' | 'pro' | 'enterprise' | undefined;

    if (!name || name.length < 1 || name.length > 100) {
      return reply.status(400).send({ error: 'Name must be 1-100 characters' });
    }
    if (!slug || !isValidSlug(slug)) {
      if (slug && RESERVED_SLUGS.has(slug)) {
        return reply.status(400).send({ error: 'slug_reserved' });
      }
      return reply.status(400).send({ error: 'Invalid slug. Must be lowercase alphanumeric + hyphens, 3-40 chars' });
    }
    if (plan && !['free', 'pro', 'enterprise'].includes(plan)) {
      return reply.status(400).send({ error: 'Invalid plan' });
    }

    // Check slug collision
    const existing = await tenantStore.getTenantBySlug(slug);
    if (existing) {
      return reply.status(409).send({ error: 'slug_taken' });
    }

    const createInput: { name: string; slug: string; plan?: 'free' | 'pro' | 'enterprise' } = { name, slug };
    if (plan) {
      createInput.plan = plan;
    }
    const tenant = await tenantStore.createTenant(createInput);
    await membershipStore.addMember(tenant.id, user.id, 'owner');

    return reply.status(201).send({
      tenantId: tenant.id,
      name: tenant.name,
      slug: tenant.slug,
      role: 'owner',
    });
  });

  // -------------------------------------------------------------------------
  // GET /api/v1/orgs — list my tenants
  // -------------------------------------------------------------------------
  app.get('/api/v1/orgs', async (request, reply) => {
    const user = requireAuth(request, reply);
    if (!user) return;

    const memberships = await membershipStore.listTenantsWithMembership(user.id);

    return reply.send({
      tenants: memberships.map((m) => ({
        id: m.tenant.id,
        name: m.tenant.name,
        slug: m.tenant.slug,
        plan: m.tenant.plan,
        role: m.role,
        joinedAt: m.joinedAt,
      })),
    });
  });

  // -------------------------------------------------------------------------
  // GET /api/v1/orgs/:id — get tenant
  // -------------------------------------------------------------------------
  app.get('/api/v1/orgs/:id', async (request, reply) => {
    const user = requireAuth(request, reply);
    if (!user) return;

    const { id } = request.params as { id: string };

    const membership = await membershipStore.getMembership(id, user.id);
    if (!membership) {
      return reply.status(404).send({ error: 'Tenant not found' });
    }

    const tenant = await tenantStore.getTenant(id);
    if (!tenant || tenant.deletedAt) {
      return reply.status(404).send({ error: 'Tenant not found' });
    }

    return reply.send({
      ...tenant,
      role: membership.role,
    });
  });

  // -------------------------------------------------------------------------
  // PATCH /api/v1/orgs/:id — rename tenant (owner only)
  // -------------------------------------------------------------------------
  app.patch('/api/v1/orgs/:id', async (request, reply) => {
    const user = requireAuth(request, reply);
    if (!user) return;

    const { id } = request.params as { id: string };
    const membership = await membershipStore.getMembership(id, user.id);
    if (!membership) {
      return reply.status(404).send({ error: 'Tenant not found' });
    }
    if (membership.role !== 'owner') {
      return reply.status(403).send({ error: 'Only owner can rename tenant' });
    }

    const body = request.body as { name?: string; slug?: string } | null;
    const name = body?.name?.trim();
    const slug = body?.slug?.toLowerCase().trim();

    const updates: Record<string, unknown> = {};
    if (name !== undefined) {
      if (name.length < 1 || name.length > 100) {
        return reply.status(400).send({ error: 'Name must be 1-100 characters' });
      }
      updates.name = name;
    }
    if (slug !== undefined) {
      if (!isValidSlug(slug)) {
        if (RESERVED_SLUGS.has(slug)) {
          return reply.status(400).send({ error: 'slug_reserved' });
        }
        return reply.status(400).send({ error: 'Invalid slug' });
      }
      const existing = await tenantStore.getTenantBySlug(slug);
      if (existing && existing.id !== id) {
        return reply.status(409).send({ error: 'slug_taken' });
      }
      updates.slug = slug;
    }

    if (Object.keys(updates).length === 0) {
      return reply.status(400).send({ error: 'No fields to update' });
    }

    const updated = await tenantStore.updateTenant(id, updates);
    return reply.send(updated);
  });

  // -------------------------------------------------------------------------
  // PATCH /api/v1/orgs/:id/settings — update settings (admin+)
  // -------------------------------------------------------------------------
  app.patch('/api/v1/orgs/:id/settings', async (request, reply) => {
    const user = requireAuth(request, reply);
    if (!user) return;

    const { id } = request.params as { id: string };
    const membership = await membershipStore.getMembership(id, user.id);
    if (!membership) {
      return reply.status(404).send({ error: 'Tenant not found' });
    }
    if ((ROLE_HIERARCHY[membership.role] ?? 0) < (ROLE_HIERARCHY['admin'] ?? 1)) {
      return reply.status(403).send({ error: 'Requires admin role or higher' });
    }

    const body = request.body as { plan?: string; settings?: Record<string, unknown> } | null;
    const updates: Record<string, unknown> = {};
    if (body?.plan) {
      if (!['free', 'pro', 'enterprise'].includes(body.plan)) {
        return reply.status(400).send({ error: 'Invalid plan' });
      }
      updates.plan = body.plan;
    }
    if (body?.settings) {
      updates.settings = body.settings;
    }

    if (Object.keys(updates).length === 0) {
      return reply.status(400).send({ error: 'No fields to update' });
    }

    const updated = await tenantStore.updateTenant(id, updates);
    return reply.send(updated);
  });

  // -------------------------------------------------------------------------
  // DELETE /api/v1/orgs/:id — soft/hard delete (owner)
  // -------------------------------------------------------------------------
  app.delete('/api/v1/orgs/:id', async (request, reply) => {
    const user = requireAuth(request, reply);
    if (!user) return;

    const { id } = request.params as { id: string };
    const membership = await membershipStore.getMembership(id, user.id);
    if (!membership) {
      return reply.status(404).send({ error: 'Tenant not found' });
    }
    if (membership.role !== 'owner') {
      return reply.status(403).send({ error: 'Only owner can delete tenant' });
    }

    const tenant = await tenantStore.getTenant(id);
    if (!tenant) {
      return reply.status(404).send({ error: 'Tenant not found' });
    }

    const query = request.query as { confirm?: string; force?: string };

    // Hard delete with HMAC confirm token (works on soft-deleted tenants too)
    if (query.confirm) {
      const valid = verifyConfirmToken(query.confirm, id, user.id, jwtSecret);
      if (!valid) {
        return reply.status(400).send({ error: 'confirmation_expired' });
      }
      await tenantStore.hardDeleteTenant(id);
      return reply.status(204).send();
    }

    // For soft delete, tenant must not already be soft-deleted
    if (tenant.deletedAt) {
      return reply.status(404).send({ error: 'Tenant already deleted' });
    }

    // Guard: cannot delete last tenant
    const userTenants = await membershipStore.getUserTenants(user.id);
    const activeTenants = userTenants.filter((t) => t.tenantId !== id);
    if (activeTenants.length === 0) {
      return reply.status(409).send({ error: 'last_tenant', message: 'Cannot delete your last tenant. Create another first.' });
    }

    // Soft delete
    await tenantStore.deleteTenant(id);
    const confirmation = generateConfirmToken(id, user.id, jwtSecret);
    return reply.status(202).send({
      message: 'Tenant soft-deleted. Use confirm token to permanently delete.',
      confirmationToken: confirmation.token,
      expiresAt: confirmation.expiresAt,
    });
  });

  // -------------------------------------------------------------------------
  // POST /api/v1/orgs/:id/transfer-ownership — transfer ownership (owner)
  // -------------------------------------------------------------------------
  app.post('/api/v1/orgs/:id/transfer-ownership', async (request, reply) => {
    const user = requireAuth(request, reply);
    if (!user) return;

    const { id } = request.params as { id: string };
    const membership = await membershipStore.getMembership(id, user.id);
    if (!membership) {
      return reply.status(404).send({ error: 'Tenant not found' });
    }
    if (membership.role !== 'owner') {
      return reply.status(403).send({ error: 'Only owner can transfer ownership' });
    }

    const tenant = await tenantStore.getTenant(id);
    if (!tenant || tenant.deletedAt) {
      return reply.status(404).send({ error: 'Tenant not found or deleted' });
    }

    const body = request.body as { newOwnerUserId?: string } | null;
    const newOwnerUserId = body?.newOwnerUserId;
    if (!newOwnerUserId) {
      return reply.status(400).send({ error: 'newOwnerUserId is required' });
    }
    if (newOwnerUserId === user.id) {
      return reply.status(400).send({ error: 'same_user' });
    }

    const targetMembership = await membershipStore.getMembership(id, newOwnerUserId);
    if (!targetMembership) {
      return reply.status(400).send({ error: 'not_a_member' });
    }

    // Transactional: demote old owner to admin, promote new owner
    await membershipStore.updateMemberRole(id, user.id, 'admin');
    await membershipStore.updateMemberRole(id, newOwnerUserId, 'owner');

    return reply.send({
      tenantId: id,
      previousOwnerId: user.id,
      newOwnerId: newOwnerUserId,
    });
  });

  // -------------------------------------------------------------------------
  // GET /api/v1/orgs/:id/members — list members
  // -------------------------------------------------------------------------
  app.get('/api/v1/orgs/:id/members', async (request, reply) => {
    const user = requireAuth(request, reply);
    if (!user) return;

    const { id } = request.params as { id: string };
    const membership = await membershipStore.getMembership(id, user.id);
    if (!membership) {
      return reply.status(404).send({ error: 'Tenant not found' });
    }

    const members = await membershipStore.listMembers(id);
    return reply.send({ members });
  });

  // -------------------------------------------------------------------------
  // PUT /api/v1/orgs/:id/members/:userId/role — update member role (admin+)
  // -------------------------------------------------------------------------
  app.put('/api/v1/orgs/:id/members/:userId/role', async (request, reply) => {
    const user = requireAuth(request, reply);
    if (!user) return;

    const params = request.params as { id: string; userId: string };
    const callerMembership = await membershipStore.getMembership(params.id, user.id);
    if (!callerMembership) {
      return reply.status(404).send({ error: 'Tenant not found' });
    }

    const callerLevel = ROLE_HIERARCHY[callerMembership.role] ?? 0;
    if (callerLevel < (ROLE_HIERARCHY['admin'] ?? 1)) {
      return reply.status(403).send({ error: 'Requires admin role or higher' });
    }

    const targetMembership = await membershipStore.getMembership(params.id, params.userId);
    if (!targetMembership) {
      return reply.status(404).send({ error: 'Member not found' });
    }

    const body = request.body as { role?: string } | null;
    const newRole = body?.role as 'owner' | 'admin' | 'member' | undefined;
    if (!newRole || !['owner', 'admin', 'member'].includes(newRole)) {
      return reply.status(400).send({ error: 'Invalid role' });
    }

    // Admin cannot assign owner role
    if (newRole === 'owner' && callerMembership.role !== 'owner') {
      return reply.status(403).send({ error: 'Only owner can assign owner role' });
    }

    // Cannot change own role
    if (params.userId === user.id) {
      return reply.status(400).send({ error: 'Cannot change own role' });
    }

    // Admin cannot change another admin's role
    if (callerMembership.role === 'admin' && targetMembership.role === 'admin') {
      return reply.status(403).send({ error: 'Admin cannot change another admin\'s role' });
    }

    const updated = await membershipStore.updateMemberRole(params.id, params.userId, newRole);
    return reply.send(updated);
  });

  // -------------------------------------------------------------------------
  // DELETE /api/v1/orgs/:id/members/:userId — remove member (admin+)
  // -------------------------------------------------------------------------
  app.delete('/api/v1/orgs/:id/members/:userId', async (request, reply) => {
    const user = requireAuth(request, reply);
    if (!user) return;

    const params = request.params as { id: string; userId: string };
    const callerMembership = await membershipStore.getMembership(params.id, user.id);
    if (!callerMembership) {
      return reply.status(404).send({ error: 'Tenant not found' });
    }

    const callerLevel = ROLE_HIERARCHY[callerMembership.role] ?? 0;
    if (callerLevel < (ROLE_HIERARCHY['admin'] ?? 1)) {
      return reply.status(403).send({ error: 'Requires admin role or higher' });
    }

    const targetMembership = await membershipStore.getMembership(params.id, params.userId);
    if (!targetMembership) {
      return reply.status(404).send({ error: 'Member not found' });
    }

    // Cannot remove self if last owner
    if (params.userId === user.id && callerMembership.role === 'owner') {
      const ownerCount = await membershipStore.countOwners(params.id);
      if (ownerCount <= 1) {
        return reply.status(409).send({ error: 'Cannot remove the last owner' });
      }
    }

    // Admin cannot remove another admin
    if (callerMembership.role === 'admin' && targetMembership.role === 'admin' && params.userId !== user.id) {
      return reply.status(403).send({ error: 'Admin cannot remove another admin' });
    }

    // Cannot remove an owner unless caller is owner
    if (targetMembership.role === 'owner' && callerMembership.role !== 'owner') {
      return reply.status(403).send({ error: 'Only owner can remove an owner' });
    }

    await membershipStore.removeMember(params.id, params.userId);
    return reply.send({ ok: true });
  });

  // -------------------------------------------------------------------------
  // POST /api/v1/orgs/:id/invites — invite member (admin+)
  // -------------------------------------------------------------------------
  app.post('/api/v1/orgs/:id/invites', async (request, reply) => {
    const user = requireAuth(request, reply);
    if (!user) return;

    const { id } = request.params as { id: string };
    const callerMembership = await membershipStore.getMembership(id, user.id);
    if (!callerMembership) {
      return reply.status(404).send({ error: 'Tenant not found' });
    }

    const callerLevel = ROLE_HIERARCHY[callerMembership.role] ?? 0;
    if (callerLevel < (ROLE_HIERARCHY['admin'] ?? 1)) {
      return reply.status(403).send({ error: 'Requires admin role or higher' });
    }

    const body = request.body as { email?: string; role?: string } | null;
    const email = body?.email?.trim().toLowerCase();
    const inviteRole = (body?.role ?? 'member') as 'owner' | 'admin' | 'member';

    if (!email || !isValidEmail(email)) {
      return reply.status(400).send({ error: 'Valid email is required' });
    }
    if (!['owner', 'admin', 'member'].includes(inviteRole)) {
      return reply.status(400).send({ error: 'Invalid role' });
    }

    // Admin cannot invite owner role
    if (inviteRole === 'owner' && callerMembership.role !== 'owner') {
      return reply.status(403).send({ error: 'cannot_invite_owner' });
    }

    // Check if already a member (by looking up users by email is complex,
    // so we check pending invites instead)
    const pendingInvites = await membershipStore.listPendingInvites(id);
    const existingInvite = pendingInvites.find((inv) => inv.email.toLowerCase() === email);
    if (existingInvite) {
      return reply.status(409).send({ error: 'invite_already_pending' });
    }

    const rawToken = generateToken();
    const tokenHash = hashToken(rawToken);
    const expiresAt = new Date(Date.now() + inviteTtlHours * 60 * 60 * 1000).toISOString();

    const tenant = await tenantStore.getTenant(id);
    const tenantName = tenant?.name ?? 'Organization';

    const invite = await membershipStore.createInvite({
      tenantId: id,
      email,
      role: inviteRole,
      inviteTokenHash: tokenHash,
      invitedBy: user.id,
      expiresAt,
    });

    // Send email (fire-and-forget)
    const emailMsg = buildTenantInviteEmail(
      user.username ?? user.id,
      tenantName,
      rawToken,
      frontendUrl,
      inviteTtlHours,
    );
    emailMsg.to = email;
    emailService.sendEmail(emailMsg).catch(() => {
      // Log silently; the invite is stored regardless
    });

    return reply.status(201).send({
      inviteId: invite.id,
      email: invite.email,
      role: invite.role,
      expiresAt: invite.expiresAt,
    });
  });

  // -------------------------------------------------------------------------
  // GET /api/v1/orgs/:id/invites — list pending invites (admin+)
  // -------------------------------------------------------------------------
  app.get('/api/v1/orgs/:id/invites', async (request, reply) => {
    const user = requireAuth(request, reply);
    if (!user) return;

    const { id } = request.params as { id: string };
    const callerMembership = await membershipStore.getMembership(id, user.id);
    if (!callerMembership) {
      return reply.status(404).send({ error: 'Tenant not found' });
    }

    const callerLevel = ROLE_HIERARCHY[callerMembership.role] ?? 0;
    if (callerLevel < (ROLE_HIERARCHY['admin'] ?? 1)) {
      return reply.status(403).send({ error: 'Requires admin role or higher' });
    }

    const invites = await membershipStore.listPendingInvites(id);
    return reply.send({ invites });
  });

  // -------------------------------------------------------------------------
  // DELETE /api/v1/orgs/:id/invites/:inviteId — revoke invite (admin+)
  // -------------------------------------------------------------------------
  app.delete('/api/v1/orgs/:id/invites/:inviteId', async (request, reply) => {
    const user = requireAuth(request, reply);
    if (!user) return;

    const params = request.params as { id: string; inviteId: string };
    const callerMembership = await membershipStore.getMembership(params.id, user.id);
    if (!callerMembership) {
      return reply.status(404).send({ error: 'Tenant not found' });
    }

    const callerLevel = ROLE_HIERARCHY[callerMembership.role] ?? 0;
    if (callerLevel < (ROLE_HIERARCHY['admin'] ?? 1)) {
      return reply.status(403).send({ error: 'Requires admin role or higher' });
    }

    // Verify invite belongs to this tenant
    const invite = await membershipStore.getInviteById(params.inviteId);
    if (!invite || invite.tenantId !== params.id) {
      return reply.status(404).send({ error: 'Invite not found' });
    }

    await membershipStore.revokeInvite(params.inviteId);
    return reply.send({ ok: true });
  });

  // -------------------------------------------------------------------------
  // POST /api/v1/orgs/:id/invites/:token/accept — accept invite
  // -------------------------------------------------------------------------
  app.post('/api/v1/orgs/:id/invites/:token/accept', async (request, reply) => {
    const user = requireAuth(request, reply);
    if (!user) return;

    const params = request.params as { id: string; token: string };
    const tokenHash = hashToken(params.token);
    const invite = await membershipStore.getInviteByTokenHash(tokenHash);

    if (!invite) {
      return reply.status(404).send({ error: 'Invite not found' });
    }
    if (invite.tenantId !== params.id) {
      return reply.status(404).send({ error: 'Invite not found' });
    }
    if (invite.acceptedAt) {
      return reply.status(410).send({ error: 'Invite already accepted' });
    }
    if (new Date(invite.expiresAt) < new Date()) {
      return reply.status(410).send({ error: 'invite_expired' });
    }

    // Email match check
    const userEmail = user.email?.toLowerCase();
    if (!userEmail || userEmail !== invite.email.toLowerCase()) {
      return reply.status(403).send({ error: 'invite_not_for_you' });
    }

    // Check if already a member
    const existingMembership = await membershipStore.getMembership(invite.tenantId, user.id);
    if (existingMembership) {
      return reply.status(409).send({ error: 'already_member' });
    }

    // Add member and accept invite
    const membership = await membershipStore.addMember(invite.tenantId, user.id, invite.role);
    await membershipStore.acceptInvite(invite.id);

    return reply.send({
      tenantId: membership.tenantId,
      role: membership.role,
      joinedAt: membership.joinedAt,
    });
  });

  // -------------------------------------------------------------------------
  // POST /api/v1/auth/switch-org — switch active tenant
  // -------------------------------------------------------------------------
  app.post('/api/v1/auth/switch-org', async (request, reply) => {
    const user = requireAuth(request, reply);
    if (!user) return;

    const body = request.body as { tenantId?: string } | null;
    const targetTenantId = body?.tenantId;
    if (!targetTenantId) {
      return reply.status(400).send({ error: 'tenantId is required' });
    }

    const membership = await membershipStore.getMembership(targetTenantId, user.id);
    if (!membership) {
      return reply.status(403).send({ error: 'Not a member of this tenant' });
    }

    const tenant = await tenantStore.getTenant(targetTenantId);
    if (!tenant || tenant.deletedAt) {
      return reply.status(404).send({ error: 'Tenant not found' });
    }

    // Issue a new JWT with the tenantId claim
    const tokenPayload = {
      id: user.id,
      username: user.username ?? user.id,
      role: user.role,
      tenantId: targetTenantId,
      tenantRole: membership.role,
    };

    const token = app.jwt.sign(tokenPayload);

    return reply.send({
      token,
      tenantId: targetTenantId,
      tenantName: tenant.name,
      tenantRole: membership.role,
    });
  });
}
