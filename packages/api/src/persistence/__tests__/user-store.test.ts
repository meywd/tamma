/**
 * InMemoryUserStore Tests
 *
 * Tests the IUserStore interface using the in-memory implementation.
 */

import { describe, it, expect, beforeEach } from 'vitest';
import { InMemoryUserStore } from '../user-store.js';
import type { IUserStore } from '../user-store.js';

describe('InMemoryUserStore', () => {
  let store: IUserStore;

  beforeEach(() => {
    store = new InMemoryUserStore();
  });

  // -----------------------------------------------------------------------
  // upsertUser
  // -----------------------------------------------------------------------

  describe('upsertUser', () => {
    it('creates a new user', async () => {
      const user = await store.upsertUser({
        githubId: 1001,
        githubLogin: 'test-user',
        email: 'test@example.com',
        role: 'member',
      });

      expect(user.id).toBeDefined();
      expect(user.githubId).toBe(1001);
      expect(user.githubLogin).toBe('test-user');
      expect(user.email).toBe('test@example.com');
      expect(user.role).toBe('member');
      expect(user.createdAt).toMatch(/^\d{4}-\d{2}-\d{2}T/);
      expect(user.updatedAt).toMatch(/^\d{4}-\d{2}-\d{2}T/);
    });

    it('updates an existing user by githubId', async () => {
      await store.upsertUser({
        githubId: 1001,
        githubLogin: 'old-login',
        email: 'old@example.com',
        role: 'member',
      });

      const updated = await store.upsertUser({
        githubId: 1001,
        githubLogin: 'new-login',
        email: 'new@example.com',
        role: 'admin',
      });

      expect(updated.githubLogin).toBe('new-login');
      expect(updated.email).toBe('new@example.com');
    });

    it('preserves existing email when null is provided on upsert', async () => {
      await store.upsertUser({
        githubId: 1001,
        githubLogin: 'test-user',
        email: 'original@example.com',
        role: 'member',
      });

      const updated = await store.upsertUser({
        githubId: 1001,
        githubLogin: 'test-user',
        email: null,
        role: 'member',
      });

      expect(updated.email).toBe('original@example.com');
    });

    it('creates separate users for different githubIds', async () => {
      const user1 = await store.upsertUser({
        githubId: 1001,
        githubLogin: 'user-one',
        email: null,
        role: 'member',
      });

      const user2 = await store.upsertUser({
        githubId: 1002,
        githubLogin: 'user-two',
        email: null,
        role: 'member',
      });

      expect(user1.id).not.toBe(user2.id);
    });
  });

  // -----------------------------------------------------------------------
  // getUser / getUserByGithubId
  // -----------------------------------------------------------------------

  describe('getUser', () => {
    it('returns null for nonexistent user', async () => {
      expect(await store.getUser('nonexistent')).toBeNull();
    });

    it('returns the user by id', async () => {
      const created = await store.upsertUser({
        githubId: 1001,
        githubLogin: 'test-user',
        email: null,
        role: 'member',
      });

      const found = await store.getUser(created.id);
      expect(found).not.toBeNull();
      expect(found!.githubLogin).toBe('test-user');
    });
  });

  describe('getUserByGithubId', () => {
    it('returns null for nonexistent githubId', async () => {
      expect(await store.getUserByGithubId(999)).toBeNull();
    });

    it('returns user by githubId', async () => {
      await store.upsertUser({
        githubId: 1001,
        githubLogin: 'test-user',
        email: null,
        role: 'owner',
      });

      const found = await store.getUserByGithubId(1001);
      expect(found).not.toBeNull();
      expect(found!.githubLogin).toBe('test-user');
      expect(found!.role).toBe('owner');
    });
  });

  // -----------------------------------------------------------------------
  // linkUserToInstallation / getUserInstallations
  // -----------------------------------------------------------------------

  describe('linkUserToInstallation', () => {
    it('links a user to an installation', async () => {
      const user = await store.upsertUser({
        githubId: 1001,
        githubLogin: 'test-user',
        email: null,
        role: 'member',
      });

      await store.linkUserToInstallation(user.id, 12345, 'owner');

      const installations = await store.getUserInstallations(user.id);
      expect(installations).toHaveLength(1);
      expect(installations[0]!.installationId).toBe(12345);
      expect(installations[0]!.role).toBe('owner');
      expect(installations[0]!.createdAt).toBeDefined();
    });

    it('updates role on duplicate link', async () => {
      const user = await store.upsertUser({
        githubId: 1001,
        githubLogin: 'test-user',
        email: null,
        role: 'member',
      });

      await store.linkUserToInstallation(user.id, 12345, 'member');
      await store.linkUserToInstallation(user.id, 12345, 'admin');

      const installations = await store.getUserInstallations(user.id);
      expect(installations).toHaveLength(1);
      expect(installations[0]!.role).toBe('admin');
    });

    it('links user to multiple installations', async () => {
      const user = await store.upsertUser({
        githubId: 1001,
        githubLogin: 'test-user',
        email: null,
        role: 'member',
      });

      await store.linkUserToInstallation(user.id, 12345, 'owner');
      await store.linkUserToInstallation(user.id, 67890, 'member');

      const installations = await store.getUserInstallations(user.id);
      expect(installations).toHaveLength(2);
    });
  });

  describe('getUserInstallations', () => {
    it('returns empty array for user with no installations', async () => {
      const user = await store.upsertUser({
        githubId: 1001,
        githubLogin: 'test-user',
        email: null,
        role: 'member',
      });

      expect(await store.getUserInstallations(user.id)).toEqual([]);
    });

    it('returns empty array for nonexistent user', async () => {
      expect(await store.getUserInstallations('nonexistent')).toEqual([]);
    });
  });
});
