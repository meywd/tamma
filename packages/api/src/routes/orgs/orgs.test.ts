/**
 * Tests for organization routes (Story 18-3).
 */

import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import Fastify from 'fastify';
import type { FastifyInstance } from 'fastify';
import { InMemoryTenantStore } from '../../persistence/tenant-store.js';
import { InMemoryUserStore } from '../../persistence/user-store.js';
import { InMemoryTenantMembershipStore } from '../../persistence/tenant-membership-store.js';
import { InMemoryEmailService } from '../../services/email.js';
import { registerOrgRoutes } from './index.js';

describe('Organization Routes', () => {
  let app: FastifyInstance;
  let tenantStore: InMemoryTenantStore;
  let userStore: InMemoryUserStore;
  let membershipStore: InMemoryTenantMembershipStore;
  let emailService: InMemoryEmailService;

  const JWT_SECRET = 'test-jwt-secret-for-org-tests';

  async function createUserAndLogin(email: string, name: string): Promise<{ userId: string; accessToken: string }> {
    const user = await userStore.createEmailUser({
      email,
      name,
      passwordHash: 'hash',
      emailVerificationTokenHash: 'vtoken',
      emailVerificationExpiresAt: '2099-01-01T00:00:00Z',
    });
    await userStore.setEmailVerified(user.id);

    // Sign a JWT manually for the user
    const accessToken = app.jwt.sign({
      sub: user.id,
      tenantId: null,
      role: 'member',
      platformRole: 'user',
      email,
      name,
      authMethod: 'email',
    });

    return { userId: user.id, accessToken };
  }

  beforeEach(async () => {
    app = Fastify({ logger: false });
    tenantStore = new InMemoryTenantStore();
    userStore = new InMemoryUserStore();
    membershipStore = new InMemoryTenantMembershipStore();
    emailService = new InMemoryEmailService();

    await registerOrgRoutes(app, {
      tenantStore,
      userStore,
      membershipStore,
      emailService,
      jwtSecret: JWT_SECRET,
    });

    await app.ready();
  });

  afterEach(async () => {
    await app.close();
  });

  describe('POST /api/v1/orgs', () => {
    it('should create an organization', async () => {
      const { accessToken } = await createUserAndLogin('owner@test.com', 'Owner');

      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/orgs',
        headers: { authorization: `Bearer ${accessToken}` },
        payload: { name: 'Acme Corp', slug: 'acme-corp' },
      });

      expect(res.statusCode).toBe(201);
      const body = res.json();
      expect(body.name).toBe('Acme Corp');
      expect(body.slug).toBe('acme-corp');
      expect(body.id).toBeDefined();
    });

    it('should add creator as owner', async () => {
      const { userId, accessToken } = await createUserAndLogin('owner2@test.com', 'Owner2');

      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/orgs',
        headers: { authorization: `Bearer ${accessToken}` },
        payload: { name: 'Beta Inc', slug: 'beta-inc' },
      });

      const orgId = res.json().id;
      const membership = await membershipStore.getMembership(orgId, userId);
      expect(membership).not.toBeNull();
      expect(membership!.role).toBe('owner');
    });

    it('should reject reserved slugs', async () => {
      const { accessToken } = await createUserAndLogin('reserved@test.com', 'Reserved');

      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/orgs',
        headers: { authorization: `Bearer ${accessToken}` },
        payload: { name: 'Admin Org', slug: 'admin' },
      });

      expect(res.statusCode).toBe(400);
    });

    it('should reject invalid slugs', async () => {
      const { accessToken } = await createUserAndLogin('invalid@test.com', 'Invalid');

      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/orgs',
        headers: { authorization: `Bearer ${accessToken}` },
        payload: { name: 'Bad Slug', slug: 'A B C' },
      });

      expect(res.statusCode).toBe(400);
    });

    it('should reject duplicate slugs', async () => {
      const { accessToken } = await createUserAndLogin('dup@test.com', 'Dup');

      await app.inject({
        method: 'POST',
        url: '/api/v1/orgs',
        headers: { authorization: `Bearer ${accessToken}` },
        payload: { name: 'First', slug: 'unique-slug' },
      });

      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/orgs',
        headers: { authorization: `Bearer ${accessToken}` },
        payload: { name: 'Second', slug: 'unique-slug' },
      });

      expect(res.statusCode).toBe(409);
    });

    it('should require authentication', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/orgs',
        payload: { name: 'NoAuth', slug: 'noauth' },
      });

      expect(res.statusCode).toBe(401);
    });
  });

  describe('GET /api/v1/orgs/:tenantId', () => {
    it('should get organization details for members', async () => {
      const { userId, accessToken } = await createUserAndLogin('member@test.com', 'Member');

      // Create org
      const createRes = await app.inject({
        method: 'POST',
        url: '/api/v1/orgs',
        headers: { authorization: `Bearer ${accessToken}` },
        payload: { name: 'View Org', slug: 'view-org' },
      });
      const orgId = createRes.json().id;

      const res = await app.inject({
        method: 'GET',
        url: `/api/v1/orgs/${orgId}`,
        headers: { authorization: `Bearer ${accessToken}` },
      });

      expect(res.statusCode).toBe(200);
      expect(res.json().name).toBe('View Org');
      expect(res.json().yourRole).toBe('owner');
    });

    it('should reject non-members', async () => {
      const { accessToken: ownerToken } = await createUserAndLogin('orgowner@test.com', 'OrgOwner');
      const { accessToken: outsiderToken } = await createUserAndLogin('outsider@test.com', 'Outsider');

      const createRes = await app.inject({
        method: 'POST',
        url: '/api/v1/orgs',
        headers: { authorization: `Bearer ${ownerToken}` },
        payload: { name: 'Private Org', slug: 'private-org' },
      });
      const orgId = createRes.json().id;

      const res = await app.inject({
        method: 'GET',
        url: `/api/v1/orgs/${orgId}`,
        headers: { authorization: `Bearer ${outsiderToken}` },
      });

      expect(res.statusCode).toBe(403);
    });
  });

  describe('Member management', () => {
    it('should list members', async () => {
      const { userId: ownerId, accessToken: ownerToken } = await createUserAndLogin('listowner@test.com', 'ListOwner');

      const createRes = await app.inject({
        method: 'POST',
        url: '/api/v1/orgs',
        headers: { authorization: `Bearer ${ownerToken}` },
        payload: { name: 'List Org', slug: 'list-org' },
      });
      const orgId = createRes.json().id;

      const res = await app.inject({
        method: 'GET',
        url: `/api/v1/orgs/${orgId}/members`,
        headers: { authorization: `Bearer ${ownerToken}` },
      });

      expect(res.statusCode).toBe(200);
      expect(res.json().total).toBe(1);
      expect(res.json().members[0].userId).toBe(ownerId);
    });

    it('should update member role', async () => {
      const { userId: ownerId, accessToken: ownerToken } = await createUserAndLogin('roleowner@test.com', 'RoleOwner');
      const { userId: memberId } = await createUserAndLogin('rolemember@test.com', 'RoleMember');

      const createRes = await app.inject({
        method: 'POST',
        url: '/api/v1/orgs',
        headers: { authorization: `Bearer ${ownerToken}` },
        payload: { name: 'Role Org', slug: 'role-org' },
      });
      const orgId = createRes.json().id;

      // Add member
      await membershipStore.addMember(orgId, memberId, 'member');

      const res = await app.inject({
        method: 'PUT',
        url: `/api/v1/orgs/${orgId}/members/${memberId}/role`,
        headers: { authorization: `Bearer ${ownerToken}` },
        payload: { role: 'admin' },
      });

      expect(res.statusCode).toBe(200);
      expect(res.json().membership.role).toBe('admin');
    });

    it('should prevent removing last owner', async () => {
      const { userId: ownerId, accessToken: ownerToken } = await createUserAndLogin('lastowner@test.com', 'LastOwner');

      const createRes = await app.inject({
        method: 'POST',
        url: '/api/v1/orgs',
        headers: { authorization: `Bearer ${ownerToken}` },
        payload: { name: 'Last Owner Org', slug: 'last-owner-org' },
      });
      const orgId = createRes.json().id;

      // Try to demote self from owner
      const res = await app.inject({
        method: 'PUT',
        url: `/api/v1/orgs/${orgId}/members/${ownerId}/role`,
        headers: { authorization: `Bearer ${ownerToken}` },
        payload: { role: 'admin' },
      });

      expect(res.statusCode).toBe(400);
      expect(res.json().error).toContain('last owner');
    });

    it('should remove a member', async () => {
      const { accessToken: ownerToken } = await createUserAndLogin('rmowner@test.com', 'RmOwner');
      const { userId: memberId } = await createUserAndLogin('rmmember@test.com', 'RmMember');

      const createRes = await app.inject({
        method: 'POST',
        url: '/api/v1/orgs',
        headers: { authorization: `Bearer ${ownerToken}` },
        payload: { name: 'Remove Org', slug: 'remove-org' },
      });
      const orgId = createRes.json().id;
      await membershipStore.addMember(orgId, memberId, 'member');

      const res = await app.inject({
        method: 'DELETE',
        url: `/api/v1/orgs/${orgId}/members/${memberId}`,
        headers: { authorization: `Bearer ${ownerToken}` },
      });

      expect(res.statusCode).toBe(200);
      expect(res.json().ok).toBe(true);

      const membership = await membershipStore.getMembership(orgId, memberId);
      expect(membership).toBeNull();
    });
  });

  describe('Invites', () => {
    it('should send an invite', async () => {
      const { accessToken: ownerToken } = await createUserAndLogin('invowner@test.com', 'InvOwner');

      const createRes = await app.inject({
        method: 'POST',
        url: '/api/v1/orgs',
        headers: { authorization: `Bearer ${ownerToken}` },
        payload: { name: 'Invite Org', slug: 'invite-org' },
      });
      const orgId = createRes.json().id;

      const res = await app.inject({
        method: 'POST',
        url: `/api/v1/orgs/${orgId}/invites`,
        headers: { authorization: `Bearer ${ownerToken}` },
        payload: { email: 'newmember@test.com', role: 'member' },
      });

      expect(res.statusCode).toBe(201);
      expect(res.json().email).toBe('newmember@test.com');

      // Verify email was sent
      await new Promise((resolve) => setTimeout(resolve, 50));
      const emails = emailService.getEmailsTo('newmember@test.com');
      expect(emails.length).toBe(1);
    });

    it('should list pending invites', async () => {
      const { accessToken: ownerToken } = await createUserAndLogin('listinv@test.com', 'ListInv');

      const createRes = await app.inject({
        method: 'POST',
        url: '/api/v1/orgs',
        headers: { authorization: `Bearer ${ownerToken}` },
        payload: { name: 'Pending Org', slug: 'pending-org' },
      });
      const orgId = createRes.json().id;

      await app.inject({
        method: 'POST',
        url: `/api/v1/orgs/${orgId}/invites`,
        headers: { authorization: `Bearer ${ownerToken}` },
        payload: { email: 'pending1@test.com' },
      });

      const res = await app.inject({
        method: 'GET',
        url: `/api/v1/orgs/${orgId}/invites`,
        headers: { authorization: `Bearer ${ownerToken}` },
      });

      expect(res.statusCode).toBe(200);
      expect(res.json().invites).toHaveLength(1);
    });

    it('should accept an invite', async () => {
      const { userId: ownerId, accessToken: ownerToken } = await createUserAndLogin('accowner@test.com', 'AccOwner');
      const { userId: inviteeId, accessToken: inviteeToken } = await createUserAndLogin('invitee@test.com', 'Invitee');

      const createRes = await app.inject({
        method: 'POST',
        url: '/api/v1/orgs',
        headers: { authorization: `Bearer ${ownerToken}` },
        payload: { name: 'Accept Org', slug: 'accept-org' },
      });
      const orgId = createRes.json().id;

      // Send invite
      await app.inject({
        method: 'POST',
        url: `/api/v1/orgs/${orgId}/invites`,
        headers: { authorization: `Bearer ${ownerToken}` },
        payload: { email: 'invitee@test.com', role: 'admin' },
      });

      await new Promise((resolve) => setTimeout(resolve, 50));

      // Get the invite token from email
      const emails = emailService.getEmailsTo('invitee@test.com');
      const tokenMatch = emails[0]!.text.match(/token=([a-f0-9]+)/);
      expect(tokenMatch).not.toBeNull();
      const token = tokenMatch![1]!;

      // Accept invite
      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/orgs/invites/accept',
        headers: { authorization: `Bearer ${inviteeToken}` },
        payload: { token },
      });

      expect(res.statusCode).toBe(200);
      expect(res.json().role).toBe('admin');

      // Verify membership
      const membership = await membershipStore.getMembership(orgId, inviteeId);
      expect(membership).not.toBeNull();
      expect(membership!.role).toBe('admin');
    });

    it('should reject invalid invite token', async () => {
      const { accessToken } = await createUserAndLogin('badinv@test.com', 'BadInv');

      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/orgs/invites/accept',
        headers: { authorization: `Bearer ${accessToken}` },
        payload: { token: 'invalid-token' },
      });

      expect(res.statusCode).toBe(400);
    });
  });

  describe('POST /api/v1/auth/switch-org', () => {
    it('should switch active organization', async () => {
      const { userId, accessToken } = await createUserAndLogin('switch@test.com', 'Switcher');

      // Create two orgs
      const org1Res = await app.inject({
        method: 'POST',
        url: '/api/v1/orgs',
        headers: { authorization: `Bearer ${accessToken}` },
        payload: { name: 'Org One', slug: 'org-one' },
      });
      const org1Id = org1Res.json().id;

      const org2Res = await app.inject({
        method: 'POST',
        url: '/api/v1/orgs',
        headers: { authorization: `Bearer ${accessToken}` },
        payload: { name: 'Org Two', slug: 'org-two' },
      });
      const org2Id = org2Res.json().id;

      // Switch to org2
      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/switch-org',
        headers: { authorization: `Bearer ${accessToken}` },
        payload: { tenantId: org2Id },
      });

      expect(res.statusCode).toBe(200);
      expect(res.json().tenantId).toBe(org2Id);
      expect(res.json().accessToken).toBeDefined();

      // Verify user's active tenant was updated
      const user = await userStore.getUser(userId);
      expect(user!.tenantId).toBe(org2Id);
    });

    it('should reject switching to non-member org', async () => {
      const { accessToken: user1Token } = await createUserAndLogin('u1@test.com', 'User1');
      const { accessToken: user2Token } = await createUserAndLogin('u2@test.com', 'User2');

      // User1 creates an org
      const orgRes = await app.inject({
        method: 'POST',
        url: '/api/v1/orgs',
        headers: { authorization: `Bearer ${user1Token}` },
        payload: { name: 'Private', slug: 'private-switch' },
      });
      const orgId = orgRes.json().id;

      // User2 tries to switch to it
      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/switch-org',
        headers: { authorization: `Bearer ${user2Token}` },
        payload: { tenantId: orgId },
      });

      expect(res.statusCode).toBe(403);
    });
  });
});
