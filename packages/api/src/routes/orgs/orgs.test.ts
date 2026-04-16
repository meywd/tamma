/**
 * Organization (Tenant) Routes Tests
 *
 * Story 18-3: Full tenant lifecycle — create, list, rename, transfer, delete,
 * members, invites, accept, switch-org, personal tenant auto-provisioning.
 */

import { describe, it, expect, beforeEach } from 'vitest';
import Fastify from 'fastify';
import type { FastifyInstance } from 'fastify';
import jwt from '@fastify/jwt';
import { registerOrgRoutes } from './index.js';
import type { OrgRoutesOptions } from './index.js';
import { InMemoryTenantStore } from '../../persistence/tenant-store.js';
import { InMemoryTenantMembershipStore, hashToken } from '../../persistence/tenant-membership-store.js';
import { InMemoryUserStore } from '../../persistence/user-store.js';
import { ConsoleEmailService } from '../../services/email.js';
import { createEnsurePersonalTenant } from '../../middleware/ensure-personal-tenant.js';

// ---------------------------------------------------------------------------
// Test helpers
// ---------------------------------------------------------------------------

const JWT_SECRET = 'test-jwt-secret';

interface TestAuthUser {
  id: string;
  role: string;
  email?: string;
  username?: string;
  tenantId?: string;
}

function createTestApp(authUser: TestAuthUser | null = null) {
  const app = Fastify();
  const tenantStore = new InMemoryTenantStore();
  const membershipStore = new InMemoryTenantMembershipStore();
  const userStore = new InMemoryUserStore();
  const emailService = new ConsoleEmailService();

  // Wire tenant store for dynamic lookup in listTenantsWithMembership
  membershipStore.setTenantStore(tenantStore);

  app.decorateRequest('authUser', null);

  if (authUser) {
    app.addHook('onRequest', async (request) => {
      (request as unknown as { authUser: TestAuthUser }).authUser = { ...authUser };
    });
  }

  return { app, tenantStore, membershipStore, userStore, emailService };
}

async function setupRoutes(
  app: FastifyInstance,
  opts: Omit<OrgRoutesOptions, 'jwtSecret'>,
) {
  await app.register(jwt, { secret: JWT_SECRET });
  await registerOrgRoutes(app, { ...opts, jwtSecret: JWT_SECRET });
  await app.ready();
}

async function createTenantHelper(
  app: FastifyInstance,
  opts: { name: string; slug: string; plan?: string },
) {
  return app.inject({
    method: 'POST',
    url: '/api/v1/orgs',
    payload: opts,
  });
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('Organization Routes', () => {
  // -----------------------------------------------------------------------
  // CREATE
  // -----------------------------------------------------------------------
  describe('POST /api/v1/orgs — create tenant', () => {
    it('creates tenant and assigns owner membership', async () => {
      const { app, tenantStore, membershipStore, userStore, emailService } =
        createTestApp({ id: 'user-1', role: 'admin' });
      await setupRoutes(app, { tenantStore, membershipStore, userStore, emailService });

      const res = await createTenantHelper(app, { name: 'Acme Corp', slug: 'acme-corp' });

      expect(res.statusCode).toBe(201);
      const body = res.json();
      expect(body.tenantId).toBeDefined();
      expect(body.name).toBe('Acme Corp');
      expect(body.slug).toBe('acme-corp');
      expect(body.role).toBe('owner');

      // Verify membership
      const members = await membershipStore.listMembers(body.tenantId);
      expect(members).toHaveLength(1);
      expect(members[0]!.userId).toBe('user-1');
      expect(members[0]!.role).toBe('owner');
    });

    it('returns 409 on slug collision', async () => {
      const { app, tenantStore, membershipStore, userStore, emailService } =
        createTestApp({ id: 'user-1', role: 'admin' });
      await setupRoutes(app, { tenantStore, membershipStore, userStore, emailService });

      await createTenantHelper(app, { name: 'First', slug: 'my-slug' });
      const res = await createTenantHelper(app, { name: 'Second', slug: 'my-slug' });

      expect(res.statusCode).toBe(409);
      expect(res.json().error).toBe('slug_taken');
    });

    it('returns 400 for reserved slug', async () => {
      const { app, tenantStore, membershipStore, userStore, emailService } =
        createTestApp({ id: 'user-1', role: 'admin' });
      await setupRoutes(app, { tenantStore, membershipStore, userStore, emailService });

      const res = await createTenantHelper(app, { name: 'Admin Org', slug: 'admin' });

      expect(res.statusCode).toBe(400);
      expect(res.json().error).toBe('slug_reserved');
    });

    it('returns 401 when unauthenticated', async () => {
      const { app, tenantStore, membershipStore, userStore, emailService } =
        createTestApp(null);
      await setupRoutes(app, { tenantStore, membershipStore, userStore, emailService });

      const res = await createTenantHelper(app, { name: 'Org', slug: 'org-slug' });

      expect(res.statusCode).toBe(401);
    });

    it('validates name length', async () => {
      const { app, tenantStore, membershipStore, userStore, emailService } =
        createTestApp({ id: 'user-1', role: 'admin' });
      await setupRoutes(app, { tenantStore, membershipStore, userStore, emailService });

      const res = await createTenantHelper(app, { name: '', slug: 'valid-slug' });
      expect(res.statusCode).toBe(400);
    });

    it('validates slug format', async () => {
      const { app, tenantStore, membershipStore, userStore, emailService } =
        createTestApp({ id: 'user-1', role: 'admin' });
      await setupRoutes(app, { tenantStore, membershipStore, userStore, emailService });

      const res = await createTenantHelper(app, { name: 'Org', slug: 'A' });
      expect(res.statusCode).toBe(400);
    });
  });

  // -----------------------------------------------------------------------
  // LIST
  // -----------------------------------------------------------------------
  describe('GET /api/v1/orgs — list my tenants', () => {
    it('returns all user memberships', async () => {
      const { app, tenantStore, membershipStore, userStore, emailService } =
        createTestApp({ id: 'user-1', role: 'admin' });
      await setupRoutes(app, { tenantStore, membershipStore, userStore, emailService });

      await createTenantHelper(app, { name: 'Org 1', slug: 'org-one' });
      await createTenantHelper(app, { name: 'Org 2', slug: 'org-two' });

      const res = await app.inject({ method: 'GET', url: '/api/v1/orgs' });

      expect(res.statusCode).toBe(200);
      const body = res.json();
      expect(body.tenants).toHaveLength(2);
      expect(body.tenants.map((t: { slug: string }) => t.slug).sort()).toEqual(['org-one', 'org-two']);
    });
  });

  // -----------------------------------------------------------------------
  // GET by ID
  // -----------------------------------------------------------------------
  describe('GET /api/v1/orgs/:id — get tenant', () => {
    it('returns tenant for a member', async () => {
      const { app, tenantStore, membershipStore, userStore, emailService } =
        createTestApp({ id: 'user-1', role: 'admin' });
      await setupRoutes(app, { tenantStore, membershipStore, userStore, emailService });

      const createRes = await createTenantHelper(app, { name: 'My Org', slug: 'my-org' });
      const tenantId = createRes.json().tenantId;

      const res = await app.inject({ method: 'GET', url: `/api/v1/orgs/${tenantId}` });

      expect(res.statusCode).toBe(200);
      expect(res.json().name).toBe('My Org');
      expect(res.json().role).toBe('owner');
    });

    it('returns 404 for non-member', async () => {
      const { app, tenantStore, membershipStore, userStore, emailService } =
        createTestApp({ id: 'user-2', role: 'admin' });

      // Create a tenant as user-1
      const tenant = await tenantStore.createTenant({ name: 'Private', slug: 'private' });
      await membershipStore.addMember(tenant.id, 'user-1', 'owner');

      await setupRoutes(app, { tenantStore, membershipStore, userStore, emailService });

      const res = await app.inject({ method: 'GET', url: `/api/v1/orgs/${tenant.id}` });
      expect(res.statusCode).toBe(404);
    });
  });

  // -----------------------------------------------------------------------
  // RENAME
  // -----------------------------------------------------------------------
  describe('PATCH /api/v1/orgs/:id — rename tenant', () => {
    it('owner can rename', async () => {
      const { app, tenantStore, membershipStore, userStore, emailService } =
        createTestApp({ id: 'user-1', role: 'admin' });
      await setupRoutes(app, { tenantStore, membershipStore, userStore, emailService });

      const createRes = await createTenantHelper(app, { name: 'Old Name', slug: 'old-name' });
      const tenantId = createRes.json().tenantId;

      const res = await app.inject({
        method: 'PATCH',
        url: `/api/v1/orgs/${tenantId}`,
        payload: { name: 'New Name' },
      });

      expect(res.statusCode).toBe(200);
      expect(res.json().name).toBe('New Name');
    });

    it('admin cannot rename', async () => {
      const { app, tenantStore, membershipStore, userStore, emailService } =
        createTestApp({ id: 'user-2', role: 'admin' });

      const tenant = await tenantStore.createTenant({ name: 'Org', slug: 'org-slug' });
      await membershipStore.addMember(tenant.id, 'user-2', 'admin');

      await setupRoutes(app, { tenantStore, membershipStore, userStore, emailService });

      const res = await app.inject({
        method: 'PATCH',
        url: `/api/v1/orgs/${tenant.id}`,
        payload: { name: 'Sneaky Rename' },
      });

      expect(res.statusCode).toBe(403);
    });
  });

  // -----------------------------------------------------------------------
  // TRANSFER OWNERSHIP
  // -----------------------------------------------------------------------
  describe('POST /api/v1/orgs/:id/transfer-ownership', () => {
    it('owner transfers to existing member — roles swap', async () => {
      const { app, tenantStore, membershipStore, userStore, emailService } =
        createTestApp({ id: 'owner-1', role: 'admin' });

      const tenant = await tenantStore.createTenant({ name: 'Org', slug: 'transfer-org' });
      await membershipStore.addMember(tenant.id, 'owner-1', 'owner');
      await membershipStore.addMember(tenant.id, 'member-1', 'member');

      await setupRoutes(app, { tenantStore, membershipStore, userStore, emailService });

      const res = await app.inject({
        method: 'POST',
        url: `/api/v1/orgs/${tenant.id}/transfer-ownership`,
        payload: { newOwnerUserId: 'member-1' },
      });

      expect(res.statusCode).toBe(200);
      expect(res.json().newOwnerId).toBe('member-1');
      expect(res.json().previousOwnerId).toBe('owner-1');

      // Verify roles
      const oldOwner = await membershipStore.getMembership(tenant.id, 'owner-1');
      const newOwner = await membershipStore.getMembership(tenant.id, 'member-1');
      expect(oldOwner!.role).toBe('admin');
      expect(newOwner!.role).toBe('owner');
    });

    it('returns 400 for non-member target', async () => {
      const { app, tenantStore, membershipStore, userStore, emailService } =
        createTestApp({ id: 'owner-1', role: 'admin' });

      const tenant = await tenantStore.createTenant({ name: 'Org', slug: 'xfer-org' });
      await membershipStore.addMember(tenant.id, 'owner-1', 'owner');

      await setupRoutes(app, { tenantStore, membershipStore, userStore, emailService });

      const res = await app.inject({
        method: 'POST',
        url: `/api/v1/orgs/${tenant.id}/transfer-ownership`,
        payload: { newOwnerUserId: 'stranger' },
      });

      expect(res.statusCode).toBe(400);
      expect(res.json().error).toBe('not_a_member');
    });

    it('returns 400 for transfer to self', async () => {
      const { app, tenantStore, membershipStore, userStore, emailService } =
        createTestApp({ id: 'owner-1', role: 'admin' });

      const tenant = await tenantStore.createTenant({ name: 'Org', slug: 'self-xfer' });
      await membershipStore.addMember(tenant.id, 'owner-1', 'owner');

      await setupRoutes(app, { tenantStore, membershipStore, userStore, emailService });

      const res = await app.inject({
        method: 'POST',
        url: `/api/v1/orgs/${tenant.id}/transfer-ownership`,
        payload: { newOwnerUserId: 'owner-1' },
      });

      expect(res.statusCode).toBe(400);
      expect(res.json().error).toBe('same_user');
    });
  });

  // -----------------------------------------------------------------------
  // DELETE (soft + hard)
  // -----------------------------------------------------------------------
  describe('DELETE /api/v1/orgs/:id — soft/hard delete', () => {
    it('owner soft-deletes and receives confirmation token', async () => {
      const { app, tenantStore, membershipStore, userStore, emailService } =
        createTestApp({ id: 'owner-1', role: 'admin' });

      // Need two tenants so last-tenant guard doesn't block
      const t1 = await tenantStore.createTenant({ name: 'Keep', slug: 'keep-org' });
      await membershipStore.addMember(t1.id, 'owner-1', 'owner');
      const t2 = await tenantStore.createTenant({ name: 'Delete', slug: 'del-org' });
      await membershipStore.addMember(t2.id, 'owner-1', 'owner');

      // Sync tenant data for listTenantsWithMembership
      membershipStore.tenantData.set(t1.id, t1);
      membershipStore.tenantData.set(t2.id, t2);

      await setupRoutes(app, { tenantStore, membershipStore, userStore, emailService });

      const res = await app.inject({
        method: 'DELETE',
        url: `/api/v1/orgs/${t2.id}`,
      });

      expect(res.statusCode).toBe(202);
      const body = res.json();
      expect(body.confirmationToken).toBeDefined();
      expect(body.expiresAt).toBeDefined();

      // Verify soft-deleted
      const tenant = await tenantStore.getTenant(t2.id);
      expect(tenant!.deletedAt).toBeDefined();
    });

    it('hard-deletes with valid HMAC token', async () => {
      const { app, tenantStore, membershipStore, userStore, emailService } =
        createTestApp({ id: 'owner-1', role: 'admin' });

      const t1 = await tenantStore.createTenant({ name: 'Keep', slug: 'keep-hard' });
      await membershipStore.addMember(t1.id, 'owner-1', 'owner');
      const t2 = await tenantStore.createTenant({ name: 'HardDel', slug: 'hard-del' });
      await membershipStore.addMember(t2.id, 'owner-1', 'owner');

      membershipStore.tenantData.set(t1.id, t1);
      membershipStore.tenantData.set(t2.id, t2);

      await setupRoutes(app, { tenantStore, membershipStore, userStore, emailService });

      // First: soft delete to get confirm token
      const softRes = await app.inject({
        method: 'DELETE',
        url: `/api/v1/orgs/${t2.id}`,
      });
      const confirmToken = softRes.json().confirmationToken;

      // Need to re-create the tenant for hard delete test since it's soft-deleted
      // Actually, let's test the hard delete path with a fresh tenant
      const t3 = await tenantStore.createTenant({ name: 'HardDel2', slug: 'hard-del2' });
      await membershipStore.addMember(t3.id, 'owner-1', 'owner');
      membershipStore.tenantData.set(t3.id, t3);

      // Soft delete first
      const softRes2 = await app.inject({
        method: 'DELETE',
        url: `/api/v1/orgs/${t3.id}`,
      });
      const token = softRes2.json().confirmationToken;

      // Hard delete
      const hardRes = await app.inject({
        method: 'DELETE',
        url: `/api/v1/orgs/${t3.id}?confirm=${encodeURIComponent(token)}`,
      });

      expect(hardRes.statusCode).toBe(204);

      // Verify hard-deleted
      const tenant = await tenantStore.getTenant(t3.id);
      expect(tenant).toBeNull();
    });

    it('returns 400 for invalid/expired HMAC token', async () => {
      const { app, tenantStore, membershipStore, userStore, emailService } =
        createTestApp({ id: 'owner-1', role: 'admin' });

      const t1 = await tenantStore.createTenant({ name: 'Keep', slug: 'keep-hmac' });
      await membershipStore.addMember(t1.id, 'owner-1', 'owner');
      const t2 = await tenantStore.createTenant({ name: 'HmacTest', slug: 'hmac-test' });
      await membershipStore.addMember(t2.id, 'owner-1', 'owner');
      membershipStore.tenantData.set(t1.id, t1);
      membershipStore.tenantData.set(t2.id, t2);

      await setupRoutes(app, { tenantStore, membershipStore, userStore, emailService });

      const res = await app.inject({
        method: 'DELETE',
        url: `/api/v1/orgs/${t2.id}?confirm=invalid-token`,
      });

      expect(res.statusCode).toBe(400);
      expect(res.json().error).toBe('confirmation_expired');
    });

    it('returns 409 when deleting last tenant', async () => {
      const { app, tenantStore, membershipStore, userStore, emailService } =
        createTestApp({ id: 'owner-1', role: 'admin' });

      const t = await tenantStore.createTenant({ name: 'Only', slug: 'only-org' });
      await membershipStore.addMember(t.id, 'owner-1', 'owner');
      membershipStore.tenantData.set(t.id, t);

      await setupRoutes(app, { tenantStore, membershipStore, userStore, emailService });

      const res = await app.inject({
        method: 'DELETE',
        url: `/api/v1/orgs/${t.id}`,
      });

      expect(res.statusCode).toBe(409);
      expect(res.json().error).toBe('last_tenant');
    });

    it('non-owner cannot delete', async () => {
      const { app, tenantStore, membershipStore, userStore, emailService } =
        createTestApp({ id: 'admin-1', role: 'admin' });

      const t = await tenantStore.createTenant({ name: 'Protected', slug: 'protected' });
      await membershipStore.addMember(t.id, 'admin-1', 'admin');

      await setupRoutes(app, { tenantStore, membershipStore, userStore, emailService });

      const res = await app.inject({
        method: 'DELETE',
        url: `/api/v1/orgs/${t.id}`,
      });

      expect(res.statusCode).toBe(403);
    });
  });

  // -----------------------------------------------------------------------
  // INVITE
  // -----------------------------------------------------------------------
  describe('POST /api/v1/orgs/:id/invites — invite member', () => {
    it('owner invites with admin role', async () => {
      const { app, tenantStore, membershipStore, userStore, emailService } =
        createTestApp({ id: 'owner-1', role: 'admin', username: 'owner1' });

      const t = await tenantStore.createTenant({ name: 'Invite Org', slug: 'invite-org' });
      await membershipStore.addMember(t.id, 'owner-1', 'owner');

      await setupRoutes(app, { tenantStore, membershipStore, userStore, emailService });

      const res = await app.inject({
        method: 'POST',
        url: `/api/v1/orgs/${t.id}/invites`,
        payload: { email: 'new@example.com', role: 'admin' },
      });

      expect(res.statusCode).toBe(201);
      const body = res.json();
      expect(body.inviteId).toBeDefined();
      expect(body.email).toBe('new@example.com');
      expect(body.role).toBe('admin');
      expect(body.expiresAt).toBeDefined();

      // Verify email sent
      expect(emailService.sent).toHaveLength(1);
      expect(emailService.sent[0]!.to).toBe('new@example.com');
    });

    it('admin cannot invite owner role', async () => {
      const { app, tenantStore, membershipStore, userStore, emailService } =
        createTestApp({ id: 'admin-1', role: 'admin' });

      const t = await tenantStore.createTenant({ name: 'Org', slug: 'admin-inv' });
      await membershipStore.addMember(t.id, 'admin-1', 'admin');

      await setupRoutes(app, { tenantStore, membershipStore, userStore, emailService });

      const res = await app.inject({
        method: 'POST',
        url: `/api/v1/orgs/${t.id}/invites`,
        payload: { email: 'new@example.com', role: 'owner' },
      });

      expect(res.statusCode).toBe(403);
      expect(res.json().error).toBe('cannot_invite_owner');
    });

    it('member cannot invite', async () => {
      const { app, tenantStore, membershipStore, userStore, emailService } =
        createTestApp({ id: 'member-1', role: 'member' });

      const t = await tenantStore.createTenant({ name: 'Org', slug: 'member-inv' });
      await membershipStore.addMember(t.id, 'member-1', 'member');

      await setupRoutes(app, { tenantStore, membershipStore, userStore, emailService });

      const res = await app.inject({
        method: 'POST',
        url: `/api/v1/orgs/${t.id}/invites`,
        payload: { email: 'new@example.com', role: 'member' },
      });

      expect(res.statusCode).toBe(403);
    });

    it('rejects duplicate pending invite', async () => {
      const { app, tenantStore, membershipStore, userStore, emailService } =
        createTestApp({ id: 'owner-1', role: 'admin' });

      const t = await tenantStore.createTenant({ name: 'Org', slug: 'dup-inv' });
      await membershipStore.addMember(t.id, 'owner-1', 'owner');

      await setupRoutes(app, { tenantStore, membershipStore, userStore, emailService });

      await app.inject({
        method: 'POST',
        url: `/api/v1/orgs/${t.id}/invites`,
        payload: { email: 'dup@example.com', role: 'member' },
      });

      const res = await app.inject({
        method: 'POST',
        url: `/api/v1/orgs/${t.id}/invites`,
        payload: { email: 'dup@example.com', role: 'admin' },
      });

      expect(res.statusCode).toBe(409);
      expect(res.json().error).toBe('invite_already_pending');
    });
  });

  // -----------------------------------------------------------------------
  // ACCEPT INVITE
  // -----------------------------------------------------------------------
  describe('POST /api/v1/orgs/:id/invites/:token/accept — accept invite', () => {
    it('valid token adds member with correct role', async () => {
      const tenantStore = new InMemoryTenantStore();
      const membershipStore = new InMemoryTenantMembershipStore();
      const userStore = new InMemoryUserStore();
      const emailService = new ConsoleEmailService();

      const t = await tenantStore.createTenant({ name: 'Accept Org', slug: 'accept-org' });
      await membershipStore.addMember(t.id, 'owner-1', 'owner');

      // Create invite via store directly
      const { generateToken: genToken, hashToken: hToken } = await import('../../persistence/tenant-membership-store.js');
      const rawToken = genToken();
      const tokenHash = hToken(rawToken);
      await membershipStore.createInvite({
        tenantId: t.id,
        email: 'invitee@example.com',
        role: 'admin',
        inviteTokenHash: tokenHash,
        invitedBy: 'owner-1',
        expiresAt: new Date(Date.now() + 72 * 60 * 60 * 1000).toISOString(),
      });

      // Auth as the invitee
      const app = Fastify();
      app.decorateRequest('authUser', null);
      app.addHook('onRequest', async (request) => {
        (request as unknown as { authUser: TestAuthUser }).authUser = {
          id: 'invitee-1',
          role: 'member',
          email: 'invitee@example.com',
        };
      });
      await app.register(jwt, { secret: JWT_SECRET });
      await registerOrgRoutes(app, {
        tenantStore, membershipStore, userStore, emailService, jwtSecret: JWT_SECRET,
      });
      await app.ready();

      const res = await app.inject({
        method: 'POST',
        url: `/api/v1/orgs/${t.id}/invites/${rawToken}/accept`,
      });

      expect(res.statusCode).toBe(200);
      expect(res.json().tenantId).toBe(t.id);
      expect(res.json().role).toBe('admin');

      // Verify membership created
      const membership = await membershipStore.getMembership(t.id, 'invitee-1');
      expect(membership).not.toBeNull();
      expect(membership!.role).toBe('admin');
    });

    it('wrong email returns 403 invite_not_for_you', async () => {
      const tenantStore = new InMemoryTenantStore();
      const membershipStore = new InMemoryTenantMembershipStore();
      const userStore = new InMemoryUserStore();
      const emailService = new ConsoleEmailService();

      const t = await tenantStore.createTenant({ name: 'WrongEmail', slug: 'wrong-email' });
      await membershipStore.addMember(t.id, 'owner-1', 'owner');

      const { generateToken: genToken, hashToken: hToken } = await import('../../persistence/tenant-membership-store.js');
      const rawToken = genToken();
      await membershipStore.createInvite({
        tenantId: t.id,
        email: 'correct@example.com',
        role: 'member',
        inviteTokenHash: hToken(rawToken),
        invitedBy: 'owner-1',
        expiresAt: new Date(Date.now() + 72 * 60 * 60 * 1000).toISOString(),
      });

      const app = Fastify();
      app.decorateRequest('authUser', null);
      app.addHook('onRequest', async (request) => {
        (request as unknown as { authUser: TestAuthUser }).authUser = {
          id: 'wrong-user',
          role: 'member',
          email: 'wrong@example.com',
        };
      });
      await app.register(jwt, { secret: JWT_SECRET });
      await registerOrgRoutes(app, {
        tenantStore, membershipStore, userStore, emailService, jwtSecret: JWT_SECRET,
      });
      await app.ready();

      const res = await app.inject({
        method: 'POST',
        url: `/api/v1/orgs/${t.id}/invites/${rawToken}/accept`,
      });

      expect(res.statusCode).toBe(403);
      expect(res.json().error).toBe('invite_not_for_you');
    });

    it('expired token returns 410', async () => {
      const tenantStore = new InMemoryTenantStore();
      const membershipStore = new InMemoryTenantMembershipStore();
      const userStore = new InMemoryUserStore();
      const emailService = new ConsoleEmailService();

      const t = await tenantStore.createTenant({ name: 'Expired', slug: 'expired-inv' });
      await membershipStore.addMember(t.id, 'owner-1', 'owner');

      const { generateToken: genToken, hashToken: hToken } = await import('../../persistence/tenant-membership-store.js');
      const rawToken = genToken();
      await membershipStore.createInvite({
        tenantId: t.id,
        email: 'user@example.com',
        role: 'member',
        inviteTokenHash: hToken(rawToken),
        invitedBy: 'owner-1',
        expiresAt: new Date(Date.now() - 1000).toISOString(), // expired
      });

      const app = Fastify();
      app.decorateRequest('authUser', null);
      app.addHook('onRequest', async (request) => {
        (request as unknown as { authUser: TestAuthUser }).authUser = {
          id: 'user-1',
          role: 'member',
          email: 'user@example.com',
        };
      });
      await app.register(jwt, { secret: JWT_SECRET });
      await registerOrgRoutes(app, {
        tenantStore, membershipStore, userStore, emailService, jwtSecret: JWT_SECRET,
      });
      await app.ready();

      const res = await app.inject({
        method: 'POST',
        url: `/api/v1/orgs/${t.id}/invites/${rawToken}/accept`,
      });

      expect(res.statusCode).toBe(410);
      expect(res.json().error).toBe('invite_expired');
    });

    it('mismatched tenant ID returns 404', async () => {
      const tenantStore = new InMemoryTenantStore();
      const membershipStore = new InMemoryTenantMembershipStore();
      const userStore = new InMemoryUserStore();
      const emailService = new ConsoleEmailService();

      const t = await tenantStore.createTenant({ name: 'Real', slug: 'real-org' });
      await membershipStore.addMember(t.id, 'owner-1', 'owner');

      const { generateToken: genToken, hashToken: hToken } = await import('../../persistence/tenant-membership-store.js');
      const rawToken = genToken();
      await membershipStore.createInvite({
        tenantId: t.id,
        email: 'user@example.com',
        role: 'member',
        inviteTokenHash: hToken(rawToken),
        invitedBy: 'owner-1',
        expiresAt: new Date(Date.now() + 72 * 60 * 60 * 1000).toISOString(),
      });

      const app = Fastify();
      app.decorateRequest('authUser', null);
      app.addHook('onRequest', async (request) => {
        (request as unknown as { authUser: TestAuthUser }).authUser = {
          id: 'user-1',
          role: 'member',
          email: 'user@example.com',
        };
      });
      await app.register(jwt, { secret: JWT_SECRET });
      await registerOrgRoutes(app, {
        tenantStore, membershipStore, userStore, emailService, jwtSecret: JWT_SECRET,
      });
      await app.ready();

      const res = await app.inject({
        method: 'POST',
        url: `/api/v1/orgs/wrong-tenant-id/invites/${rawToken}/accept`,
      });

      expect(res.statusCode).toBe(404);
    });
  });

  // -----------------------------------------------------------------------
  // MEMBERS
  // -----------------------------------------------------------------------
  describe('GET /api/v1/orgs/:id/members', () => {
    it('returns all members for a member', async () => {
      const { app, tenantStore, membershipStore, userStore, emailService } =
        createTestApp({ id: 'user-1', role: 'admin' });

      const t = await tenantStore.createTenant({ name: 'Org', slug: 'members-org' });
      await membershipStore.addMember(t.id, 'user-1', 'owner');
      await membershipStore.addMember(t.id, 'user-2', 'member');

      await setupRoutes(app, { tenantStore, membershipStore, userStore, emailService });

      const res = await app.inject({ method: 'GET', url: `/api/v1/orgs/${t.id}/members` });

      expect(res.statusCode).toBe(200);
      expect(res.json().members).toHaveLength(2);
    });
  });

  describe('PUT /api/v1/orgs/:id/members/:userId/role', () => {
    it('owner can change member to admin', async () => {
      const { app, tenantStore, membershipStore, userStore, emailService } =
        createTestApp({ id: 'owner-1', role: 'admin' });

      const t = await tenantStore.createTenant({ name: 'Org', slug: 'role-org' });
      await membershipStore.addMember(t.id, 'owner-1', 'owner');
      await membershipStore.addMember(t.id, 'member-1', 'member');

      await setupRoutes(app, { tenantStore, membershipStore, userStore, emailService });

      const res = await app.inject({
        method: 'PUT',
        url: `/api/v1/orgs/${t.id}/members/member-1/role`,
        payload: { role: 'admin' },
      });

      expect(res.statusCode).toBe(200);
      expect(res.json().role).toBe('admin');
    });
  });

  describe('DELETE /api/v1/orgs/:id/members/:userId', () => {
    it('owner can remove member', async () => {
      const { app, tenantStore, membershipStore, userStore, emailService } =
        createTestApp({ id: 'owner-1', role: 'admin' });

      const t = await tenantStore.createTenant({ name: 'Org', slug: 'remove-org' });
      await membershipStore.addMember(t.id, 'owner-1', 'owner');
      await membershipStore.addMember(t.id, 'member-1', 'member');

      await setupRoutes(app, { tenantStore, membershipStore, userStore, emailService });

      const res = await app.inject({
        method: 'DELETE',
        url: `/api/v1/orgs/${t.id}/members/member-1`,
      });

      expect(res.statusCode).toBe(200);
      expect(res.json().ok).toBe(true);

      const m = await membershipStore.getMembership(t.id, 'member-1');
      expect(m).toBeNull();
    });

    it('prevents removing last owner', async () => {
      const { app, tenantStore, membershipStore, userStore, emailService } =
        createTestApp({ id: 'owner-1', role: 'admin' });

      const t = await tenantStore.createTenant({ name: 'Org', slug: 'last-owner' });
      await membershipStore.addMember(t.id, 'owner-1', 'owner');

      await setupRoutes(app, { tenantStore, membershipStore, userStore, emailService });

      const res = await app.inject({
        method: 'DELETE',
        url: `/api/v1/orgs/${t.id}/members/owner-1`,
      });

      expect(res.statusCode).toBe(409);
    });
  });

  // -----------------------------------------------------------------------
  // SWITCH ORG
  // -----------------------------------------------------------------------
  describe('POST /api/v1/auth/switch-org', () => {
    it('valid membership returns new JWT with tenantId', async () => {
      const { app, tenantStore, membershipStore, userStore, emailService } =
        createTestApp({ id: 'user-1', role: 'admin', username: 'user1' });

      const t = await tenantStore.createTenant({ name: 'Switch', slug: 'switch-org' });
      await membershipStore.addMember(t.id, 'user-1', 'member');

      await setupRoutes(app, { tenantStore, membershipStore, userStore, emailService });

      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/switch-org',
        payload: { tenantId: t.id },
      });

      expect(res.statusCode).toBe(200);
      const body = res.json();
      expect(body.token).toBeDefined();
      expect(body.tenantId).toBe(t.id);
      expect(body.tenantRole).toBe('member');
    });

    it('non-member returns 403', async () => {
      const { app, tenantStore, membershipStore, userStore, emailService } =
        createTestApp({ id: 'user-1', role: 'admin' });

      const t = await tenantStore.createTenant({ name: 'Nope', slug: 'nope-org' });

      await setupRoutes(app, { tenantStore, membershipStore, userStore, emailService });

      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/switch-org',
        payload: { tenantId: t.id },
      });

      expect(res.statusCode).toBe(403);
    });
  });

  // -----------------------------------------------------------------------
  // PERSONAL TENANT AUTO-PROVISIONING
  // -----------------------------------------------------------------------
  describe('ensurePersonalTenant middleware', () => {
    it('creates personal tenant for user with no memberships', async () => {
      const tenantStore = new InMemoryTenantStore();
      const membershipStore = new InMemoryTenantMembershipStore();

      const hook = createEnsurePersonalTenant({ tenantStore, membershipStore });

      // Simulate a request with authUser but no tenantId
      const fakeRequest = {
        authUser: { id: 'user-abc12345', username: 'alice' },
      } as unknown as import('fastify').FastifyRequest;
      const fakeReply = {} as import('fastify').FastifyReply;

      await hook(fakeRequest, fakeReply);

      const authUser = (fakeRequest as unknown as { authUser: { tenantId?: string } }).authUser;
      expect(authUser.tenantId).toBeDefined();

      // Verify tenant was created
      const tenants = await membershipStore.getUserTenants('user-abc12345');
      expect(tenants).toHaveLength(1);
      expect(tenants[0]!.role).toBe('owner');
    });

    it('no-ops when user already has tenantId', async () => {
      const tenantStore = new InMemoryTenantStore();
      const membershipStore = new InMemoryTenantMembershipStore();

      const hook = createEnsurePersonalTenant({ tenantStore, membershipStore });

      const fakeRequest = {
        authUser: { id: 'user-1', tenantId: 'existing-tenant' },
      } as unknown as import('fastify').FastifyRequest;
      const fakeReply = {} as import('fastify').FastifyReply;

      await hook(fakeRequest, fakeReply);

      // Should not create any tenant
      const tenants = await membershipStore.getUserTenants('user-1');
      expect(tenants).toHaveLength(0);
    });

    it('picks most recent membership for user with existing memberships', async () => {
      const tenantStore = new InMemoryTenantStore();
      const membershipStore = new InMemoryTenantMembershipStore();

      // Add two memberships
      const t1 = await tenantStore.createTenant({ name: 'Old', slug: 'old-org' });
      const t2 = await tenantStore.createTenant({ name: 'New', slug: 'new-org' });
      await membershipStore.addMember(t1.id, 'user-1', 'member');
      // Delay slightly to ensure different timestamps
      await new Promise((r) => setTimeout(r, 10));
      await membershipStore.addMember(t2.id, 'user-1', 'admin');

      const hook = createEnsurePersonalTenant({ tenantStore, membershipStore });

      const fakeRequest = {
        authUser: { id: 'user-1' },
      } as unknown as import('fastify').FastifyRequest;
      const fakeReply = {} as import('fastify').FastifyReply;

      await hook(fakeRequest, fakeReply);

      const authUser = (fakeRequest as unknown as { authUser: { tenantId?: string } }).authUser;
      expect(authUser.tenantId).toBe(t2.id);
    });
  });
});
