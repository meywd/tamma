/**
 * Test fixtures for GitHub App installations.
 */

import type { GitHubInstallation, GitHubInstallationRepo } from '../../persistence/installation-store.js';

/** A standard active Organization installation. */
export function createTestInstallation(
  overrides: Partial<Omit<GitHubInstallation, 'createdAt' | 'updatedAt'>> = {},
): Omit<GitHubInstallation, 'createdAt' | 'updatedAt'> {
  return {
    installationId: 12345,
    accountLogin: 'test-org',
    accountType: 'Organization',
    appId: 99,
    permissions: { contents: 'write', issues: 'write', pull_requests: 'write' },
    suspendedAt: null,
    apiKeyHash: null,
    apiKeyPrefix: null,
    apiKeyEncrypted: null,
    ...overrides,
  };
}

/** A suspended installation fixture. */
export function createSuspendedInstallation(
  overrides: Partial<Omit<GitHubInstallation, 'createdAt' | 'updatedAt'>> = {},
): Omit<GitHubInstallation, 'createdAt' | 'updatedAt'> {
  return createTestInstallation({
    installationId: 99999,
    accountLogin: 'suspended-org',
    suspendedAt: '2025-01-01T00:00:00.000Z',
    ...overrides,
  });
}

/** A User-type installation fixture. */
export function createUserInstallation(
  overrides: Partial<Omit<GitHubInstallation, 'createdAt' | 'updatedAt'>> = {},
): Omit<GitHubInstallation, 'createdAt' | 'updatedAt'> {
  return createTestInstallation({
    installationId: 54321,
    accountLogin: 'test-user',
    accountType: 'User',
    ...overrides,
  });
}

/** A set of test repos for an installation. */
export function createTestRepos(
  _installationId: number = 12345,
): Omit<GitHubInstallationRepo, 'installationId' | 'isActive'>[] {
  return [
    { repoId: 100, owner: 'test-org', name: 'repo-alpha', fullName: 'test-org/repo-alpha' },
    { repoId: 101, owner: 'test-org', name: 'repo-beta', fullName: 'test-org/repo-beta' },
    { repoId: 102, owner: 'test-org', name: 'repo-gamma', fullName: 'test-org/repo-gamma' },
  ];
}
