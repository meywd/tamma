/**
 * Reads .tamma/config.json from a GitHub repository via the API.
 * Used in SaaS mode to load repo-level project settings.
 */

import type { IRepoConfig } from '@tamma/shared';
import { validateRepoConfig } from '@tamma/shared';

/**
 * Interface for reading repo config from a Git hosting platform.
 * Decoupled from Octokit so it can support other platforms in future.
 */
export interface RepoConfigReader {
  readRepoConfig(owner: string, repo: string, branch: string): Promise<IRepoConfig>;
}

/**
 * GitHub implementation using Octokit.
 */
export class GitHubRepoConfigReader implements RepoConfigReader {
  constructor(
    private readonly getContent: (params: {
      owner: string;
      repo: string;
      path: string;
      ref: string;
    }) => Promise<{ data: { content?: string; encoding?: string } | unknown }>,
  ) {}

  async readRepoConfig(owner: string, repo: string, branch: string): Promise<IRepoConfig> {
    try {
      const response = await this.getContent({
        owner,
        repo,
        path: '.tamma/config.json',
        ref: branch,
      });

      const data = response.data as { content?: string; encoding?: string };
      if (!data.content) {
        return {};
      }

      const raw = Buffer.from(data.content, (data.encoding as BufferEncoding) ?? 'base64').toString('utf-8');
      const parsed = JSON.parse(raw) as IRepoConfig;
      validateRepoConfig(parsed);
      return parsed;
    } catch (error: unknown) {
      // 404 means no config file — return empty config
      if (error && typeof error === 'object' && 'status' in error && error.status === 404) {
        return {};
      }
      throw error;
    }
  }
}
