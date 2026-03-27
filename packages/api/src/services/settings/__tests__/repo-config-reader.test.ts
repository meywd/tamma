/**
 * Tests for GitHubRepoConfigReader.
 */

import { describe, it, expect, vi } from 'vitest';
import { GitHubRepoConfigReader } from '../repo-config-reader.js';

describe('GitHubRepoConfigReader', () => {
  it('parses valid base64-encoded config from GitHub API', async () => {
    const repoConfig = { engine: { approvalMode: 'auto' }, roles: { implementer: { provider: 'anthropic' } } };
    const encoded = Buffer.from(JSON.stringify(repoConfig)).toString('base64');

    const getContent = vi.fn().mockResolvedValue({
      data: { content: encoded, encoding: 'base64' },
    });

    const reader = new GitHubRepoConfigReader(getContent);
    const result = await reader.readRepoConfig('owner', 'repo', 'main');

    expect(result.engine?.approvalMode).toBe('auto');
    expect(result.roles?.['implementer']?.provider).toBe('anthropic');
    expect(getContent).toHaveBeenCalledWith({
      owner: 'owner',
      repo: 'repo',
      path: '.tamma/config.json',
      ref: 'main',
    });
  });

  it('returns empty config on 404', async () => {
    const getContent = vi.fn().mockRejectedValue(
      Object.assign(new Error('Not Found'), { status: 404 }),
    );

    const reader = new GitHubRepoConfigReader(getContent);
    const result = await reader.readRepoConfig('owner', 'repo', 'main');
    expect(result).toEqual({});
  });

  it('returns empty config when content is empty', async () => {
    const getContent = vi.fn().mockResolvedValue({
      data: { content: '', encoding: 'base64' },
    });

    const reader = new GitHubRepoConfigReader(getContent);
    const result = await reader.readRepoConfig('owner', 'repo', 'main');
    expect(result).toEqual({});
  });

  it('throws on non-404 errors', async () => {
    const getContent = vi.fn().mockRejectedValue(
      Object.assign(new Error('Server Error'), { status: 500 }),
    );

    const reader = new GitHubRepoConfigReader(getContent);
    await expect(reader.readRepoConfig('owner', 'repo', 'main')).rejects.toThrow('Server Error');
  });

  it('throws on config with embedded secrets', async () => {
    const repoConfig = { roles: { implementer: { provider: 'sk-ant-secret123' } } };
    const encoded = Buffer.from(JSON.stringify(repoConfig)).toString('base64');

    const getContent = vi.fn().mockResolvedValue({
      data: { content: encoded, encoding: 'base64' },
    });

    const reader = new GitHubRepoConfigReader(getContent);
    await expect(reader.readRepoConfig('owner', 'repo', 'main')).rejects.toThrow('secrets');
  });

  it('throws on invalid JSON content', async () => {
    const encoded = Buffer.from('not json {{{').toString('base64');

    const getContent = vi.fn().mockResolvedValue({
      data: { content: encoded, encoding: 'base64' },
    });

    const reader = new GitHubRepoConfigReader(getContent);
    await expect(reader.readRepoConfig('owner', 'repo', 'main')).rejects.toThrow();
  });
});
