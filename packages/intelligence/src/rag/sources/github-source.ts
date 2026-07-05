/**
 * GitHub Source
 *
 * Retrieves content from GitHub issues, PRs, and commits.
 * Provides historical context for RAG queries.
 */

import type {
  RAGSourceType,
  SourceSettings,
  ProcessedQuery,
  RetrieveOptions,
  RetrievedChunk,
  ChunkMetadata,
} from '../types.js';
import { BaseRAGSource } from './source-interface.js';
import { KeywordSource } from './keyword-source.js';

/**
 * GitHub content types
 */
export type GitHubContentType = 'issue' | 'pr' | 'commit';

/**
 * GitHub content entry
 */
export interface GitHubEntry {
  id: string;
  type: GitHubContentType;
  title: string;
  body: string;
  url: string;
  author: string;
  date: Date;
  labels?: string[];
  state?: 'open' | 'closed' | 'merged';
  number?: number;
  filePaths?: string[];
}

/**
 * Base GitHub source for issues, PRs, and commits
 */
class GitHubBaseSource extends BaseRAGSource {
  readonly name: RAGSourceType;
  protected contentType: GitHubContentType;
  protected keywordIndex: KeywordSource;
  protected entries: Map<string, GitHubEntry>;

  constructor(name: RAGSourceType, contentType: GitHubContentType) {
    super();
    this.name = name;
    this.contentType = contentType;
    this.keywordIndex = new KeywordSource();
    this.entries = new Map();
  }

  protected async doInitialize(config: SourceSettings): Promise<void> {
    await this.keywordIndex.initialize(config);
  }

  protected async doRetrieve(
    query: ProcessedQuery,
    options: RetrieveOptions
  ): Promise<RetrievedChunk[]> {
    const results = await this.keywordIndex.retrieve(query, options);

    // Enhance results with GitHub-specific metadata
    return results.map((chunk) => {
      const entry = this.entries.get(chunk.id);
      return {
        ...chunk,
        source: this.name,
        metadata: this.buildMetadata(entry),
      };
    });
  }

  protected async doHealthCheck(): Promise<boolean> {
    return this.keywordIndex.healthCheck();
  }

  protected async doDispose(): Promise<void> {
    await this.keywordIndex.dispose();
    this.entries.clear();
  }

  /**
   * Add a GitHub entry
   */
  addEntry(entry: GitHubEntry): void {
    if (entry.type !== this.contentType) {
      return;
    }

    this.entries.set(entry.id, entry);

    // Build searchable text
    const searchText = this.buildSearchText(entry);

    this.keywordIndex.addDocument({
      id: entry.id,
      content: searchText,
      metadata: this.buildMetadata(entry),
    });
  }

  /**
   * Add multiple entries
   */
  addEntries(entries: GitHubEntry[]): void {
    for (const entry of entries) {
      this.addEntry(entry);
    }
  }

  /**
   * Remove an entry
   */
  removeEntry(id: string): void {
    this.entries.delete(id);
    this.keywordIndex.removeDocument(id);
  }

  /**
   * Clear all entries
   */
  clear(): void {
    this.entries.clear();
    this.keywordIndex.clear();
  }

  /**
   * Get the number of indexed entries
   */
  get size(): number {
    return this.entries.size;
  }

  /**
   * Build searchable text from entry
   */
  protected buildSearchText(entry: GitHubEntry): string {
    const parts: string[] = [];

    // Weight title higher
    parts.push(entry.title);
    parts.push(entry.title);

    if (entry.body) {
      parts.push(entry.body);
    }

    if (entry.labels?.length) {
      parts.push(entry.labels.join(' '));
    }

    if (entry.filePaths?.length) {
      parts.push(entry.filePaths.join(' '));
    }

    return parts.join('\n');
  }

  /**
   * Build metadata from entry
   */
  protected buildMetadata(entry?: GitHubEntry): ChunkMetadata {
    if (!entry) {
      return {};
    }

    return {
      url: entry.url,
      title: entry.title,
      author: entry.author,
      date: entry.date,
    };
  }
}

/**
 * GitHub Issues source
 */
export class IssuesSource extends GitHubBaseSource {
  constructor() {
    super('issues', 'issue');
  }
}

/**
 * GitHub Pull Requests source
 */
export class PullRequestsSource extends GitHubBaseSource {
  constructor() {
    super('prs', 'pr');
  }

  protected override buildSearchText(entry: GitHubEntry): string {
    const base = super.buildSearchText(entry);

    // Add file paths with higher weight for PRs
    if (entry.filePaths?.length) {
      return `${base}\n${entry.filePaths.join('\n')}`;
    }

    return base;
  }
}

/**
 * GitHub Commits source
 */
export class CommitsSource extends GitHubBaseSource {
  constructor() {
    super('commits', 'commit');
  }

  protected override buildMetadata(entry?: GitHubEntry): ChunkMetadata {
    if (!entry) {
      return {};
    }

    return {
      ...super.buildMetadata(entry),
      filePath: entry.filePaths?.[0], // Primary file
    };
  }
}

/**
 * Create a GitHub issues source
 */
export function createIssuesSource(): IssuesSource {
  return new IssuesSource();
}

/**
 * Create a GitHub PRs source
 */
export function createPullRequestsSource(): PullRequestsSource {
  return new PullRequestsSource();
}

/**
 * Create a GitHub commits source
 */
export function createCommitsSource(): CommitsSource {
  return new CommitsSource();
}
