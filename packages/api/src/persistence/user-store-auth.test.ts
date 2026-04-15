/**
 * Tests for user store auth extensions (Story 18-1).
 */

import { describe, it, expect, beforeEach } from 'vitest';
import { InMemoryUserStore } from './user-store.js';

describe('InMemoryUserStore - auth extensions', () => {
  let store: InMemoryUserStore;

  beforeEach(() => {
    store = new InMemoryUserStore();
  });

  describe('createEmailUser', () => {
    it('should create an email-based user', async () => {
      const user = await store.createEmailUser({
        email: 'alice@test.com',
        name: 'Alice',
        passwordHash: 'hash-abc',
        emailVerificationTokenHash: 'vtoken-hash',
        emailVerificationExpiresAt: '2099-01-01T00:00:00Z',
      });

      expect(user.email).toBe('alice@test.com');
      expect(user.githubId).toBeNull();
      expect(user.passwordHash).toBe('hash-abc');
      expect(user.emailVerified).toBe(false);
      expect(user.authMethod).toBe('email');
      expect(user.emailVerificationTokenHash).toBe('vtoken-hash');
    });

    it('should normalize email to lowercase', async () => {
      const user = await store.createEmailUser({
        email: 'Alice@Test.COM',
        name: 'Alice',
        passwordHash: 'hash',
        emailVerificationTokenHash: 'vtoken',
        emailVerificationExpiresAt: '2099-01-01T00:00:00Z',
      });

      expect(user.email).toBe('alice@test.com');
    });

    it('should reject duplicate email', async () => {
      await store.createEmailUser({
        email: 'alice@test.com',
        name: 'Alice',
        passwordHash: 'hash1',
        emailVerificationTokenHash: 'vtoken1',
        emailVerificationExpiresAt: '2099-01-01T00:00:00Z',
      });

      await expect(store.createEmailUser({
        email: 'Alice@TEST.com',
        name: 'Alice2',
        passwordHash: 'hash2',
        emailVerificationTokenHash: 'vtoken2',
        emailVerificationExpiresAt: '2099-01-01T00:00:00Z',
      })).rejects.toThrow('Email already exists');
    });
  });

  describe('getUserByEmail', () => {
    it('should find user by email (case-insensitive)', async () => {
      await store.createEmailUser({
        email: 'bob@test.com',
        name: 'Bob',
        passwordHash: 'hash',
        emailVerificationTokenHash: 'vtoken',
        emailVerificationExpiresAt: '2099-01-01T00:00:00Z',
      });

      const found = await store.getUserByEmail('BOB@TEST.COM');
      expect(found).not.toBeNull();
      expect(found!.email).toBe('bob@test.com');
    });

    it('should return null for non-existent email', async () => {
      expect(await store.getUserByEmail('nonexistent@test.com')).toBeNull();
    });
  });

  describe('setEmailVerified', () => {
    it('should mark user as verified and clear token', async () => {
      const user = await store.createEmailUser({
        email: 'charlie@test.com',
        name: 'Charlie',
        passwordHash: 'hash',
        emailVerificationTokenHash: 'vtoken',
        emailVerificationExpiresAt: '2099-01-01T00:00:00Z',
      });

      await store.setEmailVerified(user.id);

      const updated = await store.getUser(user.id);
      expect(updated!.emailVerified).toBe(true);
      expect(updated!.emailVerificationTokenHash).toBeNull();
      expect(updated!.emailVerificationExpiresAt).toBeNull();
    });
  });

  describe('updateVerificationToken', () => {
    it('should update the verification token', async () => {
      const user = await store.createEmailUser({
        email: 'dave@test.com',
        name: 'Dave',
        passwordHash: 'hash',
        emailVerificationTokenHash: 'old-hash',
        emailVerificationExpiresAt: '2099-01-01T00:00:00Z',
      });

      await store.updateVerificationToken(user.id, 'new-hash', '2099-06-01T00:00:00Z');

      const updated = await store.getUser(user.id);
      expect(updated!.emailVerificationTokenHash).toBe('new-hash');
      expect(updated!.emailVerificationExpiresAt).toBe('2099-06-01T00:00:00Z');
    });
  });

  describe('updatePasswordHash', () => {
    it('should update the password hash', async () => {
      const user = await store.createEmailUser({
        email: 'eve@test.com',
        name: 'Eve',
        passwordHash: 'old-hash',
        emailVerificationTokenHash: 'vtoken',
        emailVerificationExpiresAt: '2099-01-01T00:00:00Z',
      });

      await store.updatePasswordHash(user.id, 'new-hash');

      const updated = await store.getUser(user.id);
      expect(updated!.passwordHash).toBe('new-hash');
    });
  });

  describe('updateActiveTenant', () => {
    it('should set active tenant', async () => {
      const user = await store.createEmailUser({
        email: 'frank@test.com',
        name: 'Frank',
        passwordHash: 'hash',
        emailVerificationTokenHash: 'vtoken',
        emailVerificationExpiresAt: '2099-01-01T00:00:00Z',
      });

      await store.updateActiveTenant(user.id, 'tenant-123');

      const updated = await store.getUser(user.id);
      expect(updated!.tenantId).toBe('tenant-123');
    });

    it('should clear active tenant with null', async () => {
      const user = await store.createEmailUser({
        email: 'grace@test.com',
        name: 'Grace',
        passwordHash: 'hash',
        emailVerificationTokenHash: 'vtoken',
        emailVerificationExpiresAt: '2099-01-01T00:00:00Z',
      });

      await store.updateActiveTenant(user.id, 'tenant-123');
      await store.updateActiveTenant(user.id, null);

      const updated = await store.getUser(user.id);
      expect(updated!.tenantId).toBeNull();
    });
  });

  describe('updateAuthMethod', () => {
    it('should update auth method', async () => {
      const user = await store.createEmailUser({
        email: 'heidi@test.com',
        name: 'Heidi',
        passwordHash: 'hash',
        emailVerificationTokenHash: 'vtoken',
        emailVerificationExpiresAt: '2099-01-01T00:00:00Z',
      });

      await store.updateAuthMethod(user.id, 'both');

      const updated = await store.getUser(user.id);
      expect(updated!.authMethod).toBe('both');
    });
  });

  describe('setGithubId', () => {
    it('should set github ID and login', async () => {
      const user = await store.createEmailUser({
        email: 'ivan@test.com',
        name: 'Ivan',
        passwordHash: 'hash',
        emailVerificationTokenHash: 'vtoken',
        emailVerificationExpiresAt: '2099-01-01T00:00:00Z',
      });

      await store.setGithubId(user.id, 12345, 'ivan-gh');

      const updated = await store.getUser(user.id);
      expect(updated!.githubId).toBe(12345);
      expect(updated!.githubLogin).toBe('ivan-gh');
    });
  });

  describe('backward compatibility', () => {
    it('should still support upsertUser for GitHub users', async () => {
      const user = await store.upsertUser({
        githubId: 999,
        githubLogin: 'octocat',
        email: 'octocat@github.com',
        role: 'member',
      });

      expect(user.githubId).toBe(999);
      expect(user.emailVerified).toBe(true);  // GitHub users are pre-verified
      expect(user.authMethod).toBe('github');
      expect(user.passwordHash).toBeNull();
    });

    it('should handle upsertUser with null githubId', async () => {
      const user = await store.upsertUser({
        githubId: null,
        githubLogin: '',
        email: 'nullgh@test.com',
        role: 'member',
      });

      expect(user.githubId).toBeNull();
      expect(user.authMethod).toBe('email');
    });
  });
});
