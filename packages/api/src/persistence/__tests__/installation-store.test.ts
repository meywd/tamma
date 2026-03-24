/**
 * InMemoryInstallationStore Contract Tests
 *
 * Tests the IGitHubInstallationStore interface using the in-memory implementation.
 * This same test suite could be adapted to run against PgInstallationStore.
 */

import { describe, it, expect, beforeEach } from 'vitest';
import { InMemoryInstallationStore } from '../installation-store.js';
import type { IGitHubInstallationStore } from '../installation-store.js';

function createStore(): IGitHubInstallationStore {
  return new InMemoryInstallationStore();
}

const baseInstallation = {
  installationId: 1001,
  accountLogin: 'org-one',
  accountType: 'Organization' as const,
  appId: 99,
  permissions: { contents: 'write', issues: 'write' },
  suspendedAt: null,
  apiKeyHash: null,
  apiKeyPrefix: null,
  apiKeyEncrypted: null,
};

describe('InMemoryInstallationStore', () => {
  let store: IGitHubInstallationStore;

  beforeEach(() => {
    store = createStore();
  });

  // -----------------------------------------------------------------------
  // upsertInstallation
  // -----------------------------------------------------------------------

  describe('upsertInstallation', () => {
    it('inserts a new installation', async () => {
      await store.upsertInstallation(baseInstallation);
      const result = await store.getInstallation(1001);
      expect(result).not.toBeNull();
      expect(result!.accountLogin).toBe('org-one');
      expect(result!.appId).toBe(99);
      expect(result!.createdAt).toBeDefined();
      expect(result!.updatedAt).toBeDefined();
    });

    it('updates an existing installation', async () => {
      await store.upsertInstallation(baseInstallation);
      await store.upsertInstallation({
        ...baseInstallation,
        accountLogin: 'org-one-renamed',
        permissions: { contents: 'read' },
      });

      const result = await store.getInstallation(1001);
      expect(result!.accountLogin).toBe('org-one-renamed');
      expect(result!.permissions).toEqual({ contents: 'read' });
    });

    it('preserves createdAt on update', async () => {
      await store.upsertInstallation(baseInstallation);
      const first = await store.getInstallation(1001);

      await store.upsertInstallation({ ...baseInstallation, accountLogin: 'changed' });
      const second = await store.getInstallation(1001);

      expect(second!.createdAt).toBe(first!.createdAt);
    });
  });

  // -----------------------------------------------------------------------
  // removeInstallation
  // -----------------------------------------------------------------------

  describe('removeInstallation', () => {
    it('removes an installation', async () => {
      await store.upsertInstallation(baseInstallation);
      await store.removeInstallation(1001);
      expect(await store.getInstallation(1001)).toBeNull();
    });

    it('also removes associated repos', async () => {
      await store.upsertInstallation(baseInstallation);
      await store.setRepos(1001, [
        { repoId: 100, owner: 'org-one', name: 'repo-a', fullName: 'org-one/repo-a' },
      ]);
      await store.removeInstallation(1001);
      expect(await store.listRepos(1001)).toHaveLength(0);
    });

    it('is idempotent (no error on nonexistent)', async () => {
      await expect(store.removeInstallation(999)).resolves.not.toThrow();
    });
  });

  // -----------------------------------------------------------------------
  // getInstallation
  // -----------------------------------------------------------------------

  describe('getInstallation', () => {
    it('returns null for nonexistent', async () => {
      expect(await store.getInstallation(999)).toBeNull();
    });

    it('returns full installation data', async () => {
      await store.upsertInstallation(baseInstallation);
      const result = await store.getInstallation(1001);
      expect(result!.installationId).toBe(1001);
      expect(result!.accountType).toBe('Organization');
    });
  });

  // -----------------------------------------------------------------------
  // listInstallations / listActiveInstallations
  // -----------------------------------------------------------------------

  describe('listInstallations', () => {
    it('returns empty array when no installations exist', async () => {
      expect(await store.listInstallations()).toEqual([]);
    });

    it('returns all installations', async () => {
      await store.upsertInstallation(baseInstallation);
      await store.upsertInstallation({ ...baseInstallation, installationId: 1002, accountLogin: 'org-two' });
      expect(await store.listInstallations()).toHaveLength(2);
    });
  });

  describe('listActiveInstallations', () => {
    it('excludes suspended installations', async () => {
      await store.upsertInstallation(baseInstallation);
      await store.upsertInstallation({ ...baseInstallation, installationId: 1002, accountLogin: 'suspended' });
      await store.suspendInstallation(1002);

      const active = await store.listActiveInstallations();
      expect(active).toHaveLength(1);
      expect(active[0]!.accountLogin).toBe('org-one');
    });
  });

  // -----------------------------------------------------------------------
  // Repos CRUD
  // -----------------------------------------------------------------------

  describe('setRepos', () => {
    it('sets repos for an installation', async () => {
      await store.upsertInstallation(baseInstallation);
      await store.setRepos(1001, [
        { repoId: 100, owner: 'org-one', name: 'repo-a', fullName: 'org-one/repo-a' },
        { repoId: 101, owner: 'org-one', name: 'repo-b', fullName: 'org-one/repo-b' },
      ]);

      const repos = await store.listRepos(1001);
      expect(repos).toHaveLength(2);
      expect(repos.every((r) => r.isActive)).toBe(true);
    });

    it('replaces existing repos', async () => {
      await store.upsertInstallation(baseInstallation);
      await store.setRepos(1001, [
        { repoId: 100, owner: 'org-one', name: 'repo-a', fullName: 'org-one/repo-a' },
      ]);
      await store.setRepos(1001, [
        { repoId: 200, owner: 'org-one', name: 'repo-x', fullName: 'org-one/repo-x' },
      ]);

      const repos = await store.listRepos(1001);
      expect(repos).toHaveLength(1);
      expect(repos[0]!.repoId).toBe(200);
    });
  });

  describe('addRepos', () => {
    it('adds repos without removing existing', async () => {
      await store.upsertInstallation(baseInstallation);
      await store.setRepos(1001, [
        { repoId: 100, owner: 'org-one', name: 'repo-a', fullName: 'org-one/repo-a' },
      ]);
      await store.addRepos(1001, [
        { repoId: 101, owner: 'org-one', name: 'repo-b', fullName: 'org-one/repo-b' },
      ]);

      expect(await store.listRepos(1001)).toHaveLength(2);
    });
  });

  describe('removeRepos', () => {
    it('removes specific repos by ID', async () => {
      await store.upsertInstallation(baseInstallation);
      await store.setRepos(1001, [
        { repoId: 100, owner: 'org-one', name: 'repo-a', fullName: 'org-one/repo-a' },
        { repoId: 101, owner: 'org-one', name: 'repo-b', fullName: 'org-one/repo-b' },
      ]);

      await store.removeRepos(1001, [100]);

      const repos = await store.listRepos(1001);
      expect(repos).toHaveLength(1);
      expect(repos[0]!.repoId).toBe(101);
    });
  });

  describe('listAllActiveRepos', () => {
    it('returns repos only from active installations', async () => {
      await store.upsertInstallation(baseInstallation);
      await store.upsertInstallation({
        ...baseInstallation, installationId: 1002, accountLogin: 'suspended-org',
      });

      await store.setRepos(1001, [
        { repoId: 100, owner: 'org-one', name: 'repo-a', fullName: 'org-one/repo-a' },
      ]);
      await store.setRepos(1002, [
        { repoId: 200, owner: 'suspended-org', name: 'repo-x', fullName: 'suspended-org/repo-x' },
      ]);

      await store.suspendInstallation(1002);

      const repos = await store.listAllActiveRepos();
      expect(repos).toHaveLength(1);
      expect(repos[0]!.owner).toBe('org-one');
    });
  });

  // -----------------------------------------------------------------------
  // Suspend / Unsuspend
  // -----------------------------------------------------------------------

  describe('suspendInstallation / unsuspendInstallation', () => {
    it('sets suspendedAt on suspend', async () => {
      await store.upsertInstallation(baseInstallation);
      await store.suspendInstallation(1001);

      const result = await store.getInstallation(1001);
      expect(result!.suspendedAt).not.toBeNull();
    });

    it('clears suspendedAt on unsuspend', async () => {
      await store.upsertInstallation(baseInstallation);
      await store.suspendInstallation(1001);
      await store.unsuspendInstallation(1001);

      const result = await store.getInstallation(1001);
      expect(result!.suspendedAt).toBeNull();
    });
  });

  // -----------------------------------------------------------------------
  // API Key operations
  // -----------------------------------------------------------------------

  describe('updateApiKeyHash / findByApiKeyHash', () => {
    it('stores and retrieves by API key hash', async () => {
      await store.upsertInstallation(baseInstallation);
      await store.updateApiKeyHash(1001, 'hash-abc', 'tamma_sk_abc');

      const result = await store.getInstallation(1001);
      expect(result!.apiKeyHash).toBe('hash-abc');
      expect(result!.apiKeyPrefix).toBe('tamma_sk_abc');

      const found = await store.findByApiKeyHash('hash-abc');
      expect(found).not.toBeNull();
      expect(found!.installationId).toBe(1001);
    });

    it('stores encrypted key when provided', async () => {
      await store.upsertInstallation(baseInstallation);
      await store.updateApiKeyHash(1001, 'hash-abc', 'tamma_sk_abc', 'encrypted-blob');

      const result = await store.getInstallation(1001);
      expect(result!.apiKeyEncrypted).toBe('encrypted-blob');
    });

    it('findByApiKeyHash returns null for unknown hash', async () => {
      const result = await store.findByApiKeyHash('nonexistent');
      expect(result).toBeNull();
    });

    it('is a no-op for nonexistent installation', async () => {
      await store.updateApiKeyHash(999, 'hash', 'prefix');
      const found = await store.findByApiKeyHash('hash');
      expect(found).toBeNull();
    });
  });
});
