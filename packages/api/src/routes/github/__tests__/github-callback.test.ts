/**
 * GitHub Callback Route Tests
 *
 * Tests the GET /api/github/callback endpoint that handles GitHub App
 * installation and update actions. Validates parameter validation, GitHub
 * API interaction mocking, installation storage, API key provisioning,
 * and error handling.
 */

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import Fastify from 'fastify';
import type { FastifyInstance } from 'fastify';
import { registerGitHubCallbackRoute } from '../github-callback.js';
import { InMemoryInstallationStore } from '../../../persistence/installation-store.js';
import {
  octokitInstallationResponse,
  octokitReposResponse,
} from '../../../__tests__/fixtures/webhook-payloads.js';

// ---------------------------------------------------------------------------
// Mock Octokit + createAppAuth at the module level
// ---------------------------------------------------------------------------

const mockGetInstallation = vi.fn();
const mockListRepos = vi.fn();
const mockGetRepoPublicKey = vi.fn();
const mockCreateOrUpdateRepoSecret = vi.fn();

vi.mock('@octokit/rest', () => ({
  Octokit: vi.fn().mockImplementation(() => ({
    rest: {
      apps: {
        getInstallation: mockGetInstallation,
        listReposAccessibleToInstallation: mockListRepos,
      },
      actions: {
        getRepoPublicKey: mockGetRepoPublicKey,
        createOrUpdateRepoSecret: mockCreateOrUpdateRepoSecret,
      },
    },
  })),
}));

vi.mock('@octokit/auth-app', () => ({
  createAppAuth: vi.fn(),
}));

// Mock the GitHubSecretsProvisioner to avoid libsodium dependency
vi.mock('../../../services/github-secrets-provisioner.js', () => ({
  GitHubSecretsProvisioner: vi.fn().mockImplementation(() => ({
    provisionApiKey: vi.fn().mockResolvedValue([
      { owner: 'test-org', repo: 'repo-alpha', success: true },
      { owner: 'test-org', repo: 'repo-beta', success: true },
    ]),
  })),
}));

describe('GitHub Callback Route', () => {
  let app: FastifyInstance;
  let store: InMemoryInstallationStore;

  const defaultOptions = {
    appId: 99,
    privateKey: 'fake-private-key',
    successRedirectUrl: 'https://example.com/success',
  };

  beforeEach(async () => {
    store = new InMemoryInstallationStore();

    mockGetInstallation.mockResolvedValue(octokitInstallationResponse());
    mockListRepos.mockResolvedValue(octokitReposResponse());
    mockGetRepoPublicKey.mockResolvedValue({
      data: { key_id: 'key-id', key: 'ug/7DBrOP4EIKNbrqwfx5OtBw1eSrkQOrAQVtOTh8kc=' },
    });
    mockCreateOrUpdateRepoSecret.mockResolvedValue(undefined);

    app = Fastify({ logger: false });
    await registerGitHubCallbackRoute(app, {
      ...defaultOptions,
      installationStore: store,
    });
    await app.ready();
  });

  afterEach(async () => {
    await app.close();
    vi.clearAllMocks();
  });

  // -----------------------------------------------------------------------
  // Happy path
  // -----------------------------------------------------------------------

  it('handles install action: stores installation, generates API key, redirects', async () => {
    const response = await app.inject({
      method: 'GET',
      url: '/api/github/callback?installation_id=12345&setup_action=install',
    });

    expect(response.statusCode).toBe(302);
    expect(response.headers.location).toBe(defaultOptions.successRedirectUrl);

    // Verify installation was stored
    const installation = await store.getInstallation(12345);
    expect(installation).not.toBeNull();
    expect(installation!.accountLogin).toBe('test-org');
    expect(installation!.accountType).toBe('Organization');
    expect(installation!.appId).toBe(99);
  });

  it('stores repos from GitHub API response', async () => {
    await app.inject({
      method: 'GET',
      url: '/api/github/callback?installation_id=12345&setup_action=install',
    });

    const repos = await store.listRepos(12345);
    expect(repos).toHaveLength(2);
    expect(repos.map((r) => r.fullName).sort()).toEqual([
      'test-org/repo-alpha',
      'test-org/repo-beta',
    ]);
  });

  it('generates and stores API key hash', async () => {
    await app.inject({
      method: 'GET',
      url: '/api/github/callback?installation_id=12345&setup_action=install',
    });

    const installation = await store.getInstallation(12345);
    expect(installation!.apiKeyHash).not.toBeNull();
    expect(installation!.apiKeyHash!.length).toBeGreaterThan(0);
    expect(installation!.apiKeyPrefix).not.toBeNull();
    expect(installation!.apiKeyPrefix!.startsWith('tamma_sk_')).toBe(true);
  });

  it('handles update action the same as install', async () => {
    const response = await app.inject({
      method: 'GET',
      url: '/api/github/callback?installation_id=12345&setup_action=update',
    });

    expect(response.statusCode).toBe(302);

    const installation = await store.getInstallation(12345);
    expect(installation).not.toBeNull();
    expect(installation!.apiKeyHash).not.toBeNull();
  });

  it('upserts installation on repeated callback', async () => {
    // First install
    await app.inject({
      method: 'GET',
      url: '/api/github/callback?installation_id=12345&setup_action=install',
    });

    const firstHash = (await store.getInstallation(12345))!.apiKeyHash;

    // Second install (update)
    await app.inject({
      method: 'GET',
      url: '/api/github/callback?installation_id=12345&setup_action=update',
    });

    const secondHash = (await store.getInstallation(12345))!.apiKeyHash;

    // API key should change on each callback
    expect(secondHash).not.toBeNull();
    expect(secondHash).not.toBe(firstHash);
  });

  // -----------------------------------------------------------------------
  // Parameter validation (400)
  // -----------------------------------------------------------------------

  it('returns 400 when installation_id is missing', async () => {
    const response = await app.inject({
      method: 'GET',
      url: '/api/github/callback?setup_action=install',
    });

    expect(response.statusCode).toBe(400);
    expect(response.json().error).toContain('Missing');
  });

  it('returns 400 when setup_action is missing', async () => {
    const response = await app.inject({
      method: 'GET',
      url: '/api/github/callback?installation_id=12345',
    });

    expect(response.statusCode).toBe(400);
    expect(response.json().error).toContain('Missing');
  });

  it('returns 400 when installation_id is not a number', async () => {
    const response = await app.inject({
      method: 'GET',
      url: '/api/github/callback?installation_id=abc&setup_action=install',
    });

    expect(response.statusCode).toBe(400);
    expect(response.json().error).toContain('Invalid');
  });

  // -----------------------------------------------------------------------
  // GitHub API failures (500)
  // -----------------------------------------------------------------------

  it('returns 500 when GitHub getInstallation fails', async () => {
    mockGetInstallation.mockRejectedValueOnce(new Error('GitHub API error'));

    const response = await app.inject({
      method: 'GET',
      url: '/api/github/callback?installation_id=12345&setup_action=install',
    });

    expect(response.statusCode).toBe(500);
    expect(response.json().error).toContain('Failed');
  });

  it('returns 500 when listing repos fails', async () => {
    mockListRepos.mockRejectedValueOnce(new Error('Repos API error'));

    const response = await app.inject({
      method: 'GET',
      url: '/api/github/callback?installation_id=12345&setup_action=install',
    });

    expect(response.statusCode).toBe(500);
  });

  // -----------------------------------------------------------------------
  // Provisioning failure should not block redirect
  // -----------------------------------------------------------------------

  it('redirects successfully even when provisioning fails', async () => {
    const { GitHubSecretsProvisioner } = await import('../../../services/github-secrets-provisioner.js');
    vi.mocked(GitHubSecretsProvisioner).mockImplementationOnce(() => ({
      provisionApiKey: vi.fn().mockRejectedValue(new Error('Provisioning failed')),
    }) as any);

    // Re-create app with the failing provisioner
    await app.close();
    app = Fastify({ logger: false });
    await registerGitHubCallbackRoute(app, {
      ...defaultOptions,
      installationStore: store,
    });
    await app.ready();

    const response = await app.inject({
      method: 'GET',
      url: '/api/github/callback?installation_id=12345&setup_action=install',
    });

    // Should still redirect — provisioning failure is non-fatal
    expect(response.statusCode).toBe(302);
    expect(response.headers.location).toBe(defaultOptions.successRedirectUrl);

    // Installation and key should still be stored
    const installation = await store.getInstallation(12345);
    expect(installation).not.toBeNull();
    expect(installation!.apiKeyHash).not.toBeNull();
  });

  // -----------------------------------------------------------------------
  // Unknown setup_action — should still redirect
  // -----------------------------------------------------------------------

  it('redirects for unknown setup_action without processing', async () => {
    const response = await app.inject({
      method: 'GET',
      url: '/api/github/callback?installation_id=12345&setup_action=request',
    });

    // Unknown action should just redirect without storing
    expect(response.statusCode).toBe(302);

    // No installation should be stored for unknown actions
    const installation = await store.getInstallation(12345);
    expect(installation).toBeNull();
  });
});
