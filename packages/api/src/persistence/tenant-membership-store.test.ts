/**
 * Tests for tenant membership store (Story 18-3).
 */

import { describe, it, expect, beforeEach } from 'vitest';
import { InMemoryTenantMembershipStore, generateToken, hashToken } from './tenant-membership-store.js';

describe('InMemoryTenantMembershipStore', () => {
  let store: InMemoryTenantMembershipStore;

  beforeEach(() => {
    store = new InMemoryTenantMembershipStore();
  });

  describe('memberships', () => {
    it('should add a member to a tenant', async () => {
      const membership = await store.addMember('tenant-1', 'user-1', 'member');
      expect(membership.tenantId).toBe('tenant-1');
      expect(membership.userId).toBe('user-1');
      expect(membership.role).toBe('member');
    });

    it('should reject duplicate membership', async () => {
      await store.addMember('tenant-1', 'user-1', 'member');
      await expect(store.addMember('tenant-1', 'user-1', 'admin'))
        .rejects.toThrow('already a member');
    });

    it('should remove a member', async () => {
      await store.addMember('tenant-1', 'user-1', 'member');
      await store.removeMember('tenant-1', 'user-1');

      const membership = await store.getMembership('tenant-1', 'user-1');
      expect(membership).toBeNull();
    });

    it('should throw when removing non-existent membership', async () => {
      await expect(store.removeMember('tenant-1', 'user-1'))
        .rejects.toThrow('Membership not found');
    });

    it('should update member role', async () => {
      await store.addMember('tenant-1', 'user-1', 'member');
      const updated = await store.updateMemberRole('tenant-1', 'user-1', 'admin');
      expect(updated.role).toBe('admin');
    });

    it('should list members with pagination', async () => {
      await store.addMember('tenant-1', 'user-1', 'owner');
      await store.addMember('tenant-1', 'user-2', 'admin');
      await store.addMember('tenant-1', 'user-3', 'member');
      await store.addMember('tenant-2', 'user-4', 'member');

      const result = await store.listMembers({ tenantId: 'tenant-1', limit: 2, offset: 0 });
      expect(result.total).toBe(3);
      expect(result.members).toHaveLength(2);

      const result2 = await store.listMembers({ tenantId: 'tenant-1', limit: 2, offset: 2 });
      expect(result2.members).toHaveLength(1);
    });

    it('should get user tenants', async () => {
      await store.addMember('tenant-1', 'user-1', 'owner');
      await store.addMember('tenant-2', 'user-1', 'member');
      await store.addMember('tenant-1', 'user-2', 'member');

      const tenants = await store.getUserTenants('user-1');
      expect(tenants).toHaveLength(2);
    });

    it('should get specific membership', async () => {
      await store.addMember('tenant-1', 'user-1', 'admin');

      const membership = await store.getMembership('tenant-1', 'user-1');
      expect(membership).not.toBeNull();
      expect(membership!.role).toBe('admin');

      const noMembership = await store.getMembership('tenant-1', 'user-2');
      expect(noMembership).toBeNull();
    });

    it('should count owners', async () => {
      await store.addMember('tenant-1', 'user-1', 'owner');
      await store.addMember('tenant-1', 'user-2', 'owner');
      await store.addMember('tenant-1', 'user-3', 'admin');

      expect(await store.countOwners('tenant-1')).toBe(2);
    });
  });

  describe('invites', () => {
    it('should create and retrieve an invite by token hash', async () => {
      const invite = await store.createInvite({
        tenantId: 'tenant-1',
        email: 'invitee@test.com',
        role: 'member',
        inviteTokenHash: 'hash-abc',
        invitedBy: 'user-1',
        expiresAt: '2099-01-01T00:00:00Z',
      });

      expect(invite.email).toBe('invitee@test.com');

      const found = await store.getInviteByTokenHash('hash-abc');
      expect(found).not.toBeNull();
      expect(found!.id).toBe(invite.id);
    });

    it('should accept an invite', async () => {
      const invite = await store.createInvite({
        tenantId: 'tenant-1',
        email: 'invitee@test.com',
        role: 'member',
        inviteTokenHash: 'hash-1',
        invitedBy: 'user-1',
        expiresAt: '2099-01-01T00:00:00Z',
      });

      await store.acceptInvite(invite.id);

      const found = await store.getInviteByTokenHash('hash-1');
      expect(found!.acceptedAt).not.toBeNull();
    });

    it('should list pending invites for a tenant', async () => {
      await store.createInvite({
        tenantId: 'tenant-1',
        email: 'a@test.com',
        role: 'member',
        inviteTokenHash: 'hash-a',
        invitedBy: 'user-1',
        expiresAt: '2099-01-01T00:00:00Z',
      });
      await store.createInvite({
        tenantId: 'tenant-1',
        email: 'b@test.com',
        role: 'member',
        inviteTokenHash: 'hash-b',
        invitedBy: 'user-1',
        expiresAt: '2020-01-01T00:00:00Z', // expired
      });
      await store.createInvite({
        tenantId: 'tenant-2',
        email: 'c@test.com',
        role: 'member',
        inviteTokenHash: 'hash-c',
        invitedBy: 'user-1',
        expiresAt: '2099-01-01T00:00:00Z',
      });

      const pending = await store.listPendingInvites('tenant-1');
      expect(pending).toHaveLength(1);
      expect(pending[0]!.email).toBe('a@test.com');
    });

    it('should revoke an invite', async () => {
      const invite = await store.createInvite({
        tenantId: 'tenant-1',
        email: 'test@test.com',
        role: 'member',
        inviteTokenHash: 'hash-x',
        invitedBy: 'user-1',
        expiresAt: '2099-01-01T00:00:00Z',
      });

      await store.revokeInvite(invite.id);
      expect(await store.getInviteByTokenHash('hash-x')).toBeNull();
    });

    it('should throw when revoking non-existent invite', async () => {
      await expect(store.revokeInvite('nonexistent')).rejects.toThrow('Invite not found');
    });
  });
});

describe('token utilities', () => {
  it('should generate a 64-char hex token', () => {
    const token = generateToken();
    expect(token).toMatch(/^[0-9a-f]{64}$/);
  });

  it('should produce consistent hashes', () => {
    const token = 'abc123';
    const hash1 = hashToken(token);
    const hash2 = hashToken(token);
    expect(hash1).toBe(hash2);
  });

  it('should produce different hashes for different tokens', () => {
    expect(hashToken('token1')).not.toBe(hashToken('token2'));
  });
});
