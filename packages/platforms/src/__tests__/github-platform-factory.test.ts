import { describe, it, expect, vi, beforeEach } from 'vitest';
import { createGitHubPlatformForInstallation } from '../github/github-platform-factory.js';
import type { AppCredentials } from '../github/github-platform-factory.js';

// Mock the GitHubPlatform class
const mockInitialize = vi.fn();
vi.mock('../github/github-platform.js', () => ({
  // Vitest 4: constructor mocks must use `function` keyword, not arrow
  GitHubPlatform: vi.fn(function () {
    return {
      initialize: mockInitialize,
      platformName: 'github',
    };
  }),
}));

describe('createGitHubPlatformForInstallation', () => {
  const credentials: AppCredentials = {
    appId: 12345,
    privateKey: '-----BEGIN RSA PRIVATE KEY-----\nfake\n-----END RSA PRIVATE KEY-----',
  };

  beforeEach(() => {
    vi.clearAllMocks();
    mockInitialize.mockResolvedValue(undefined);
  });

  it('should create a platform with App auth config', async () => {
    const platform = await createGitHubPlatformForInstallation(credentials, 99999);

    expect(mockInitialize).toHaveBeenCalledWith({
      type: 'app',
      appId: 12345,
      privateKey: credentials.privateKey,
      installationId: 99999,
    });
    expect(platform.platformName).toBe('github');
  });

  it('should pass baseUrl when provided', async () => {
    const credentialsWithUrl: AppCredentials = {
      ...credentials,
      baseUrl: 'https://github.example.com/api/v3',
    };

    await createGitHubPlatformForInstallation(credentialsWithUrl, 99999);

    expect(mockInitialize).toHaveBeenCalledWith({
      type: 'app',
      appId: 12345,
      privateKey: credentials.privateKey,
      installationId: 99999,
      baseUrl: 'https://github.example.com/api/v3',
    });
  });

  it('should not include baseUrl when not provided', async () => {
    await createGitHubPlatformForInstallation(credentials, 99999);

    const callArg = mockInitialize.mock.calls[0]![0];
    expect(callArg).not.toHaveProperty('baseUrl');
  });
});
