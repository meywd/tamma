/**
 * Test fixtures for GitHub webhook payloads.
 */

/** GitHub App installation created payload. */
export function installationCreatedPayload(installationId: number = 12345) {
  return {
    action: 'created',
    installation: {
      id: installationId,
      account: {
        login: 'test-org',
        type: 'Organization',
        id: 1001,
      },
      app_id: 99,
      permissions: { contents: 'write', issues: 'write' },
      suspended_at: null,
    },
    repositories: [
      { id: 100, name: 'repo-alpha', full_name: 'test-org/repo-alpha', private: false },
      { id: 101, name: 'repo-beta', full_name: 'test-org/repo-beta', private: true },
    ],
  };
}

/** GitHub App installation deleted payload. */
export function installationDeletedPayload(installationId: number = 12345) {
  return {
    action: 'deleted',
    installation: {
      id: installationId,
      account: { login: 'test-org', type: 'Organization', id: 1001 },
      app_id: 99,
      permissions: {},
      suspended_at: null,
    },
  };
}

/** GitHub App installation suspended payload. */
export function installationSuspendedPayload(installationId: number = 12345) {
  return {
    action: 'suspend',
    installation: {
      id: installationId,
      account: { login: 'test-org', type: 'Organization', id: 1001 },
      app_id: 99,
      permissions: {},
      suspended_at: '2025-06-01T00:00:00Z',
    },
  };
}

/** Issues opened payload. */
export function issueOpenedPayload(installationId: number = 12345) {
  return {
    action: 'opened',
    installation: { id: installationId },
    issue: {
      number: 42,
      title: 'Test issue',
      body: 'This is a test issue',
      user: { login: 'test-user' },
    },
    repository: {
      id: 100,
      full_name: 'test-org/repo-alpha',
      owner: { login: 'test-org' },
      name: 'repo-alpha',
    },
  };
}

/** Pull request opened payload. */
export function pullRequestOpenedPayload(installationId: number = 12345) {
  return {
    action: 'opened',
    installation: { id: installationId },
    pull_request: {
      number: 7,
      title: 'Test PR',
      head: { ref: 'feature-branch', sha: 'abc123' },
      base: { ref: 'main' },
    },
    repository: {
      id: 100,
      full_name: 'test-org/repo-alpha',
      owner: { login: 'test-org' },
      name: 'repo-alpha',
    },
  };
}

/** Push event payload. */
export function pushPayload(installationId: number = 12345) {
  return {
    ref: 'refs/heads/main',
    installation: { id: installationId },
    commits: [{ id: 'abc123', message: 'test commit' }],
    repository: {
      id: 100,
      full_name: 'test-org/repo-alpha',
      owner: { login: 'test-org' },
      name: 'repo-alpha',
    },
  };
}

/** Octokit getInstallation response mock data. */
export function octokitInstallationResponse(installationId: number = 12345) {
  return {
    data: {
      id: installationId,
      account: {
        login: 'test-org',
        type: 'Organization',
        id: 1001,
      },
      app_id: 99,
      permissions: { contents: 'write', issues: 'write', pull_requests: 'write' },
      suspended_at: null,
    },
  };
}

/** Octokit listReposAccessibleToInstallation response mock data. */
export function octokitReposResponse() {
  return {
    data: {
      total_count: 2,
      repositories: [
        {
          id: 100,
          owner: { login: 'test-org' },
          name: 'repo-alpha',
          full_name: 'test-org/repo-alpha',
        },
        {
          id: 101,
          owner: { login: 'test-org' },
          name: 'repo-beta',
          full_name: 'test-org/repo-beta',
        },
      ],
    },
  };
}
