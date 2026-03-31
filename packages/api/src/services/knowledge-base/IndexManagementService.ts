/**
 * Index Management Service
 *
 * Manages codebase indexing operations including status tracking,
 * triggering re-indexes, history, and configuration.
 *
 * Delegates to a real ICodebaseIndexer implementation when available;
 * otherwise returns empty/zero state.
 */

import { randomUUID } from 'node:crypto';
import { resolve } from 'node:path';
import type {
  IndexStatus,
  IndexHistoryEntry,
  IndexConfig,
  TriggerIndexRequest,
} from '@tamma/shared';
import type { ICodebaseIndexer } from './types.js';

/** Default index configuration (used when no indexer is available) */
const DEFAULT_INDEX_CONFIG: IndexConfig = {
  includePatterns: ['**/*.ts', '**/*.tsx', '**/*.js', '**/*.jsx', '**/*.md'],
  excludePatterns: ['**/node_modules/**', '**/dist/**', '**/.git/**', '**/coverage/**'],
  chunkingConfig: {
    maxTokens: 500,
    overlapTokens: 50,
    preserveImports: true,
    groupRelatedCode: true,
  },
  embeddingConfig: {
    provider: 'openai',
    model: 'text-embedding-3-small',
    batchSize: 100,
  },
  triggerConfig: {
    gitHooks: false,
    watchMode: false,
    schedule: null,
  },
};

export class IndexManagementService {
  private readonly indexer: ICodebaseIndexer | null;

  /** Tracks the current indexing state and the latest run for the API response */
  private currentStatus: IndexStatus = {
    status: 'idle',
    lastRun: null,
    filesIndexed: 0,
    chunksCreated: 0,
  };

  private history: IndexHistoryEntry[] = [];
  private config: IndexConfig = { ...DEFAULT_INDEX_CONFIG };
  private projectPath: string | null = null;

  constructor(indexer?: ICodebaseIndexer, projectPath?: string) {
    this.indexer = indexer ?? null;

    if (projectPath) {
      this.projectPath = projectPath;
    }
  }

  async getStatus(): Promise<IndexStatus> {
    // If we have a real indexer, query it for live status
    if (this.indexer && this.indexer.getIndexStatus) {
      try {
        const realStatus = await this.indexer.getIndexStatus();
        const result: IndexStatus = {
          status: this.currentStatus.status,
          lastRun: realStatus.lastIndexedAt ?? this.currentStatus.lastRun,
          filesIndexed: realStatus.filesIndexed,
          chunksCreated: realStatus.chunksCreated,
        };
        if (this.currentStatus.progress !== undefined) {
          result.progress = this.currentStatus.progress;
        }
        if (this.currentStatus.currentFile !== undefined) {
          result.currentFile = this.currentStatus.currentFile;
        }
        return result;
      } catch (err) {
        // Log error and surface it in status response instead of silently swallowing
        const errorMessage = err instanceof Error ? err.message : String(err);
        console.error('[IndexManagementService] getIndexStatus() failed:', err);
        return {
          ...this.currentStatus,
          error: errorMessage,
        };
      }
    }

    return { ...this.currentStatus };
  }

  async triggerIndex(_request?: TriggerIndexRequest): Promise<void> {
    if (this.currentStatus.status === 'indexing') {
      throw new Error('Indexing is already in progress');
    }

    let effectivePath = _request?.repositoryPath ?? this.projectPath;

    if (!this.indexer || !effectivePath) {
      throw new Error('No indexer or project path configured');
    }

    // If a custom repositoryPath was provided and it's a local path (not a URL),
    // ensure it resolves within the configured project path to prevent directory traversal.
    const isUrl = /^https?:\/\//i.test(effectivePath);
    if (_request?.repositoryPath && !isUrl && this.projectPath) {
      const resolved = resolve(effectivePath);
      const base = resolve(this.projectPath);
      if (!resolved.startsWith(base)) {
        throw new Error('repositoryPath must be within the configured project directory');
      }
      effectivePath = resolved;
    }

    const startTime = new Date().toISOString();

    this.currentStatus = {
      status: 'indexing',
      lastRun: this.currentStatus.lastRun,
      filesIndexed: 0,
      chunksCreated: 0,
      progress: 0,
      currentFile: 'Scanning files...',
    };

    // Subscribe to progress events for live status updates
    const progressHandler = (...args: unknown[]): void => {
      const progress = args[0] as {
        phase: string;
        filesTotal: number;
        filesProcessed: number;
        chunksTotal: number;
        chunksProcessed: number;
        currentFile?: string;
      };
      const total = progress.filesTotal || 1;
      const updated: IndexStatus = {
        status: 'indexing',
        lastRun: this.currentStatus.lastRun,
        filesIndexed: progress.filesProcessed,
        chunksCreated: progress.chunksProcessed,
        progress: Math.round((progress.filesProcessed / total) * 100),
      };
      if (progress.currentFile !== undefined) {
        updated.currentFile = progress.currentFile;
      }
      this.currentStatus = updated;
    };
    if (this.indexer.on) {
      this.indexer.on('progress', progressHandler);
    }

    // Capture indexer reference for use in closures below
    const indexer = this.indexer;

    /** Remove the progress listener — called in both success and error paths. */
    const removeProgressListener = (): void => {
      // ICodebaseIndexer does not expose removeListener; if the real implementation
      // does, cast through unknown to call it defensively.
      const maybeEmitter = indexer as unknown as { removeListener?(event: string, handler: (...args: unknown[]) => void): void };
      maybeEmitter.removeListener?.('progress', progressHandler);
    };

    // Run indexing asynchronously (fire-and-forget for the caller)
    const isFullReindex = _request?.fullReindex === true;
    const changedFiles = _request?.changedFiles;
    const indexPromise = (isFullReindex || !this.indexer.updateIndex)
      ? this.indexer.indexProject(effectivePath, { fullReindex: isFullReindex })
      : this.indexer.updateIndex(effectivePath, changedFiles);

    indexPromise
      .then(async () => {
        removeProgressListener();

        const now = new Date().toISOString();

        // Query status after indexing completes
        const status = await indexer.getIndexStatus?.();

        this.currentStatus = {
          status: 'idle',
          lastRun: now,
          filesIndexed: status?.filesIndexed ?? this.currentStatus.filesIndexed,
          chunksCreated: status?.chunksCreated ?? this.currentStatus.chunksCreated,
          progress: 100,
        };

        const entry: IndexHistoryEntry = {
          id: randomUUID(),
          startTime,
          endTime: now,
          filesProcessed: status?.filesIndexed ?? 0,
          chunksCreated: status?.chunksCreated ?? 0,
          chunksUpdated: 0,
          chunksDeleted: 0,
          embeddingCost: 0,
          durationMs: Date.now() - new Date(startTime).getTime(),
          status: 'success',
          errors: [],
        };

        this.history.unshift(entry);
        if (this.history.length > 100) {
          this.history.length = 100;
        }
      })
      .catch((error) => {
        removeProgressListener();

        const now = new Date().toISOString();

        this.currentStatus = {
          status: 'error',
          lastRun: this.currentStatus.lastRun,
          filesIndexed: this.currentStatus.filesIndexed,
          chunksCreated: this.currentStatus.chunksCreated,
          error: error instanceof Error ? error.message : String(error),
        };

        this.history.unshift({
          id: randomUUID(),
          startTime,
          endTime: now,
          filesProcessed: 0,
          chunksCreated: 0,
          chunksUpdated: 0,
          chunksDeleted: 0,
          embeddingCost: 0,
          durationMs: 0,
          status: 'failed',
          errors: [{
            filePath: '',
            error: error instanceof Error ? error.message : String(error),
            timestamp: now,
          }],
        });
      });
  }

  async cancelIndex(): Promise<void> {
    if (this.currentStatus.status !== 'indexing') {
      throw new Error('No indexing operation in progress');
    }

    if (this.indexer?.stop) {
      await this.indexer.stop();
    }

    this.currentStatus = {
      status: 'idle',
      lastRun: this.currentStatus.lastRun,
      filesIndexed: this.currentStatus.filesIndexed,
      chunksCreated: this.currentStatus.chunksCreated,
    };
  }

  async getHistory(limit = 20): Promise<IndexHistoryEntry[]> {
    return this.history.slice(0, limit);
  }

  async getConfig(): Promise<IndexConfig> {
    return { ...this.config };
  }

  async updateConfig(config: Partial<IndexConfig>): Promise<IndexConfig> {
    if (config.includePatterns !== undefined) {
      this.config.includePatterns = config.includePatterns;
    }
    if (config.excludePatterns !== undefined) {
      this.config.excludePatterns = config.excludePatterns;
    }
    if (config.chunkingConfig) {
      this.config.chunkingConfig = { ...this.config.chunkingConfig, ...config.chunkingConfig };
    }
    if (config.embeddingConfig) {
      this.config.embeddingConfig = { ...this.config.embeddingConfig, ...config.embeddingConfig };
    }
    if (config.triggerConfig) {
      this.config.triggerConfig = { ...this.config.triggerConfig, ...config.triggerConfig };
    }

    // Push config changes to real indexer
    if (this.indexer?.configure) {
      this.indexer.configure({
        includePatterns: this.config.includePatterns,
        excludePatterns: this.config.excludePatterns,
        maxChunkTokens: this.config.chunkingConfig.maxTokens,
        overlapTokens: this.config.chunkingConfig.overlapTokens,
        preserveImports: this.config.chunkingConfig.preserveImports,
        groupRelatedCode: this.config.chunkingConfig.groupRelatedCode,
        embeddingProvider: this.config.embeddingConfig.provider,
        embeddingModel: this.config.embeddingConfig.model,
        batchSize: this.config.embeddingConfig.batchSize,
        enableGitHooks: this.config.triggerConfig.gitHooks,
        enableFileWatcher: this.config.triggerConfig.watchMode,
        ...(this.config.triggerConfig.schedule !== null
          ? { scheduleCron: this.config.triggerConfig.schedule }
          : {}),
      });
    }

    return { ...this.config };
  }

  dispose(): void {
    // No timers to clean up; indexer lifecycle is managed externally
  }
}
