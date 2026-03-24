/**
 * PgInstallationStore Integration Tests
 *
 * Runs against a real PostgreSQL database (port 5433 via docker-compose.test.yml).
 * Gated by INTEGRATION_TEST_PG=true environment variable.
 *
 * To run:
 *   docker compose -f docker/docker-compose.test.yml up -d
 *   INTEGRATION_TEST_PG=true npx vitest run --config vitest.integration.config.ts packages/api/src/persistence/__tests__/pg-installation-store.integration.test.ts
 */

import { describe, it, expect, beforeAll, afterAll, beforeEach } from 'vitest';
import type pg from 'pg';
import {
  isPgTestEnabled,
  createTestPool,
  runMigrations,
  truncateTables,
  dropTables,
} from './pg-test-helper.js';
import { PgInstallationStore } from '../pg-installation-store.js';

const describeIf = isPgTestEnabled() ? describe : describe.skip;

describeIf('PgInstallationStore (integration)', () => {
  let pool: pg.Pool;
  let store: PgInstallationStore;

  beforeAll(async () => {
    pool = createTestPool();
    await runMigrations(pool);
    store = new PgInstallationStore(pool);
  });

  afterAll(async () => {
    await dropTables(pool);
    await pool.end();
  });

  beforeEach(async () => {
    await truncateTables(pool);
  });

  // -----------------------------------------------------------------------
  // upsertInstallation
  // -----------------------------------------------------------------------

  it('inserts a new installation', async () => {
    await store.upsertInstallation({
      installationId: 1001,
      accountLogin: 'org-one',
      accountType: 'Organization',
      appId: 99,
      permissions: { contents: 'write' },
      suspendedAt: null,
      apiKeyHash: null,
      apiKeyPrefix: null,
      apiKeyEncrypted: null,
    });

    const result = await store.getInstallation(1001);
    expect(result).not.toBeNull();
    expect(result!.accountLogin).toBe('org-one');
    expect(result!.accountType).toBe('Organization');
    expect(result!.appId).toBe(99);
    expect(result!.permissions).toEqual({ contents: 'write' });
    expect(result!.suspendedAt).toBeNull();
  });

  it('updates an existing installation on conflict', async () => {
    await store.upsertInstallation({
      installationId: 1001,
      accountLogin: 'org-one',
      accountType: 'Organization',
      appId: 99,
      permissions: { contents: 'read' },
      suspendedAt: null,
      apiKeyHash: null,
      apiKeyPrefix: null,
      apiKeyEncrypted: null,
    });

    await store.upsertInstallation({
      installationId: 1001,
      accountLogin: 'org-one-renamed',
      accountType: 'Organization',
      appId: 99,
      permissions: { contents: 'write', issues: 'write' },
      suspendedAt: null,
      apiKeyHash: null,
      apiKeyPrefix: null,
      apiKeyEncrypted: null,
    });

    const result = await store.getInstallation(1001);
    expect(result!.accountLogin).toBe('org-one-renamed');
    expect(result!.permissions).toEqual({ contents: 'write', issues: 'write' });
  });

  it('preserves existing API key hash when upsert passes null', async () => {
    await store.upsertInstallation({
      installationId: 1001,
      accountLogin: 'org-one',
      accountType: 'Organization',
      appId: 99,
      permissions: {},
      suspendedAt: null,
      apiKeyHash: null,
      apiKeyPrefix: null,
      apiKeyEncrypted: null,
    });

    await store.updateApiKeyHash(1001, 'hash-abc', 'tamma_sk_abc');

    // Upsert with null API key fields — should COALESCE and keep existing
    await store.upsertInstallation({
      installationId: 1001,
      accountLogin: 'org-one-updated',
      accountType: 'Organization',
      appId: 99,
      permissions: {},
      suspendedAt: null,
      apiKeyHash: null,
      apiKeyPrefix: null,
      apiKeyEncrypted: null,
    });

    const result = await store.getInstallation(1001);
    expect(result!.apiKeyHash).toBe('hash-abc');
    expect(result!.apiKeyPrefix).toBe('tamma_sk_abc');
  });

  // -----------------------------------------------------------------------
  // removeInstallation
  // -----------------------------------------------------------------------

  it('removes an installation', async () => {
    await store.upsertInstallation({
      installationId: 1001,
      accountLogin: 'org-one',
      accountType: 'Organization',
      appId: 99,
      permissions: {},
      suspendedAt: null,
      apiKeyHash: null,
      apiKeyPrefix: null,
      apiKeyEncrypted: null,
    });

    await store.removeInstallation(1001);
    const result = await store.getInstallation(1001);
    expect(result).toBeNull();
  });

  it('cascades repo deletion on installation removal', async () => {
    await store.upsertInstallation({
      installationId: 1001,
      accountLogin: 'org-one',
      accountType: 'Organization',
      appId: 99,
      permissions: {},
      suspendedAt: null,
      apiKeyHash: null,
      apiKeyPrefix: null,
      apiKeyEncrypted: null,
    });

    await store.setRepos(1001, [
      { repoId: 100, owner: 'org-one', name: 'repo-a', fullName: 'org-one/repo-a' },
    ]);

    await store.removeInstallation(1001);
    const repos = await store.listRepos(1001);
    expect(repos).toHaveLength(0);
  });

  // -----------------------------------------------------------------------
  // getInstallation / listInstallations / listActiveInstallations
  // -----------------------------------------------------------------------

  it('returns null for nonexistent installation', async () => {
    const result = await store.getInstallation(999);
    expect(result).toBeNull();
  });

  it('lists all installations', async () => {
    await store.upsertInstallation({
      installationId: 1001, accountLogin: 'org-one', accountType: 'Organization',
      appId: 99, permissions: {}, suspendedAt: null, apiKeyHash: null, apiKeyPrefix: null, apiKeyEncrypted: null,
    });
    await store.upsertInstallation({
      installationId: 1002, accountLogin: 'org-two', accountType: 'Organization',
      appId: 99, permissions: {}, suspendedAt: null, apiKeyHash: null, apiKeyPrefix: null, apiKeyEncrypted: null,
    });

    const list = await store.listInstallations();
    expect(list).toHaveLength(2);
  });

  it('lists only active (non-suspended) installations', async () => {
    await store.upsertInstallation({
      installationId: 1001, accountLogin: 'active', accountType: 'Organization',
      appId: 99, permissions: {}, suspendedAt: null, apiKeyHash: null, apiKeyPrefix: null, apiKeyEncrypted: null,
    });
    await store.upsertInstallation({
      installationId: 1002, accountLogin: 'suspended', accountType: 'Organization',
      appId: 99, permissions: {}, suspendedAt: null, apiKeyHash: null, apiKeyPrefix: null, apiKeyEncrypted: null,
    });
    await store.suspendInstallation(1002);

    const active = await store.listActiveInstallations();
    expect(active).toHaveLength(1);
    expect(active[0]!.accountLogin).toBe('active');
  });

  // -----------------------------------------------------------------------
  // Repo CRUD
  // -----------------------------------------------------------------------

  it('sets repos (replaces all)', async () => {
    await store.upsertInstallation({
      installationId: 1001, accountLogin: 'org-one', accountType: 'Organization',
      appId: 99, permissions: {}, suspendedAt: null, apiKeyHash: null, apiKeyPrefix: null, apiKeyEncrypted: null,
    });

    await store.setRepos(1001, [
      { repoId: 100, owner: 'org-one', name: 'repo-a', fullName: 'org-one/repo-a' },
      { repoId: 101, owner: 'org-one', name: 'repo-b', fullName: 'org-one/repo-b' },
    ]);

    let repos = await store.listRepos(1001);
    expect(repos).toHaveLength(2);

    // Replace with a single repo
    await store.setRepos(1001, [
      { repoId: 102, owner: 'org-one', name: 'repo-c', fullName: 'org-one/repo-c' },
    ]);

    repos = await store.listRepos(1001);
    expect(repos).toHaveLength(1);
    expect(repos[0]!.fullName).toBe('org-one/repo-c');
  });

  it('adds repos without removing existing', async () => {
    await store.upsertInstallation({
      installationId: 1001, accountLogin: 'org-one', accountType: 'Organization',
      appId: 99, permissions: {}, suspendedAt: null, apiKeyHash: null, apiKeyPrefix: null, apiKeyEncrypted: null,
    });

    await store.setRepos(1001, [
      { repoId: 100, owner: 'org-one', name: 'repo-a', fullName: 'org-one/repo-a' },
    ]);

    await store.addRepos(1001, [
      { repoId: 101, owner: 'org-one', name: 'repo-b', fullName: 'org-one/repo-b' },
    ]);

    const repos = await store.listRepos(1001);
    expect(repos).toHaveLength(2);
  });

  it('removes specific repos by ID', async () => {
    await store.upsertInstallation({
      installationId: 1001, accountLogin: 'org-one', accountType: 'Organization',
      appId: 99, permissions: {}, suspendedAt: null, apiKeyHash: null, apiKeyPrefix: null, apiKeyEncrypted: null,
    });

    await store.setRepos(1001, [
      { repoId: 100, owner: 'org-one', name: 'repo-a', fullName: 'org-one/repo-a' },
      { repoId: 101, owner: 'org-one', name: 'repo-b', fullName: 'org-one/repo-b' },
    ]);

    await store.removeRepos(1001, [100]);

    const repos = await store.listRepos(1001);
    expect(repos).toHaveLength(1);
    expect(repos[0]!.repoId).toBe(101);
  });

  it('handles removeRepos with empty array gracefully', async () => {
    await store.upsertInstallation({
      installationId: 1001, accountLogin: 'org-one', accountType: 'Organization',
      appId: 99, permissions: {}, suspendedAt: null, apiKeyHash: null, apiKeyPrefix: null, apiKeyEncrypted: null,
    });

    await store.removeRepos(1001, []);
    // Should not throw
  });

  it('listAllActiveRepos excludes repos from suspended installations', async () => {
    await store.upsertInstallation({
      installationId: 1001, accountLogin: 'active-org', accountType: 'Organization',
      appId: 99, permissions: {}, suspendedAt: null, apiKeyHash: null, apiKeyPrefix: null, apiKeyEncrypted: null,
    });
    await store.upsertInstallation({
      installationId: 1002, accountLogin: 'suspended-org', accountType: 'Organization',
      appId: 99, permissions: {}, suspendedAt: null, apiKeyHash: null, apiKeyPrefix: null, apiKeyEncrypted: null,
    });

    await store.setRepos(1001, [
      { repoId: 100, owner: 'active-org', name: 'repo-a', fullName: 'active-org/repo-a' },
    ]);
    await store.setRepos(1002, [
      { repoId: 200, owner: 'suspended-org', name: 'repo-x', fullName: 'suspended-org/repo-x' },
    ]);

    await store.suspendInstallation(1002);

    const activeRepos = await store.listAllActiveRepos();
    expect(activeRepos).toHaveLength(1);
    expect(activeRepos[0]!.owner).toBe('active-org');
  });

  // -----------------------------------------------------------------------
  // Suspend / Unsuspend
  // -----------------------------------------------------------------------

  it('suspends and unsuspends an installation', async () => {
    await store.upsertInstallation({
      installationId: 1001, accountLogin: 'org-one', accountType: 'Organization',
      appId: 99, permissions: {}, suspendedAt: null, apiKeyHash: null, apiKeyPrefix: null, apiKeyEncrypted: null,
    });

    await store.suspendInstallation(1001);
    let result = await store.getInstallation(1001);
    expect(result!.suspendedAt).not.toBeNull();

    await store.unsuspendInstallation(1001);
    result = await store.getInstallation(1001);
    expect(result!.suspendedAt).toBeNull();
  });

  // -----------------------------------------------------------------------
  // API Key operations
  // -----------------------------------------------------------------------

  it('updates and finds by API key hash', async () => {
    await store.upsertInstallation({
      installationId: 1001, accountLogin: 'org-one', accountType: 'Organization',
      appId: 99, permissions: {}, suspendedAt: null, apiKeyHash: null, apiKeyPrefix: null, apiKeyEncrypted: null,
    });

    await store.updateApiKeyHash(1001, 'hash-xyz', 'tamma_sk_xyz', 'encrypted-blob');

    const result = await store.getInstallation(1001);
    expect(result!.apiKeyHash).toBe('hash-xyz');
    expect(result!.apiKeyPrefix).toBe('tamma_sk_xyz');
    expect(result!.apiKeyEncrypted).toBe('encrypted-blob');

    const found = await store.findByApiKeyHash('hash-xyz');
    expect(found).not.toBeNull();
    expect(found!.installationId).toBe(1001);
  });

  it('findByApiKeyHash returns null for unknown hash', async () => {
    const found = await store.findByApiKeyHash('nonexistent-hash');
    expect(found).toBeNull();
  });
});
