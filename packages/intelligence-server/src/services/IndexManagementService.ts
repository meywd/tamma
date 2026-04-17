/**
 * Index Management Service
 *
 * Wraps a real ICodebaseIndexer-like dependency to expose the 6 C# /kb/index/*
 * endpoints. Falls back to zero state when no indexer is configured.
 *
 * This is a port of the deleted packages/api/src/services/knowledge-base/
 * IndexManagementService.ts, trimmed to match the 6 routes the C# KbEndpoints
 * exposes (status, trigger, get/update config, stats, clear).
 */

import type { IIndexer } from '../types.js';

export interface IndexStatusResponse {
  status: 'idle' | 'indexing' | 'error';
  indexed: number;
  pending: number;
  lastRun?: string;
  currentFile?: string;
  progress?: number;
  error?: string;
}

export interface IndexConfigResponse {
  configured: boolean;
  includePatterns: string[];
  excludePatterns: string[];
  chunkingConfig: {
    maxTokens: number;
    overlapTokens: number;
    preserveImports: boolean;
    groupRelatedCode: boolean;
  };
  embeddingConfig: {
    provider: string;
    model: string;
    batchSize: number;
  };
  triggerConfig: {
    gitHooks: boolean;
    watchMode: boolean;
    schedule: string | null;
  };
}

export interface IndexStatsResponse {
  documents: number;
  chunks: number;
  lastIndexed: string | null;
}

const DEFAULT_CONFIG: Omit<IndexConfigResponse, 'configured'> = {
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
  private readonly indexer: IIndexer | null;
  private readonly projectPath: string | null;
  private currentStatus: IndexStatusResponse = {
    status: 'idle',
    indexed: 0,
    pending: 0,
  };
  private config: IndexConfigResponse = {
    configured: false,
    ...DEFAULT_CONFIG,
  };

  constructor(indexer?: IIndexer, projectPath?: string) {
    this.indexer = indexer ?? null;
    this.projectPath = projectPath ?? null;
    if (this.indexer) {
      this.config = { ...this.config, configured: true };
    }
  }

  async getStatus(): Promise<IndexStatusResponse> {
    if (this.indexer?.getIndexStatus) {
      try {
        const real = await this.indexer.getIndexStatus();
        const result: IndexStatusResponse = {
          status: this.currentStatus.status,
          indexed: real.filesIndexed,
          pending: 0,
        };
        if (real.lastIndexedAt !== undefined) {
          result.lastRun = real.lastIndexedAt;
        }
        if (this.currentStatus.progress !== undefined) {
          result.progress = this.currentStatus.progress;
        }
        if (this.currentStatus.currentFile !== undefined) {
          result.currentFile = this.currentStatus.currentFile;
        }
        return result;
      } catch (err) {
        const msg = err instanceof Error ? err.message : String(err);
        return { ...this.currentStatus, status: 'error', error: msg };
      }
    }
    return { ...this.currentStatus };
  }

  async triggerIndex(
    body?: { fullReindex?: boolean; repositoryPath?: string; changedFiles?: string[] },
  ): Promise<{ message: string }> {
    if (!this.indexer) {
      return { message: 'Indexing triggered (stub — no indexer configured)' };
    }
    if (this.currentStatus.status === 'indexing') {
      throw new Error('Indexing already in progress');
    }
    const effectivePath = body?.repositoryPath ?? this.projectPath;
    if (!effectivePath) {
      throw new Error('No project path configured');
    }
    this.currentStatus = { status: 'indexing', indexed: 0, pending: 0 };

    // Fire-and-forget. We capture a local reference because TS narrows
    // `this.indexer` back to `IIndexer | null` on async boundaries.
    const indexer = this.indexer;
    const isFull = body?.fullReindex === true;
    const promise = isFull || !indexer.updateIndex
      ? indexer.indexProject(effectivePath, { fullReindex: isFull })
      : indexer.updateIndex(effectivePath, body?.changedFiles);

    promise
      .then(async () => {
        const status = await indexer.getIndexStatus?.();
        this.currentStatus = {
          status: 'idle',
          indexed: status?.filesIndexed ?? 0,
          pending: 0,
          lastRun: new Date().toISOString(),
          progress: 100,
        };
      })
      .catch((err: unknown) => {
        const msg = err instanceof Error ? err.message : String(err);
        this.currentStatus = {
          status: 'error',
          indexed: this.currentStatus.indexed,
          pending: 0,
          error: msg,
        };
      });

    return { message: 'Indexing triggered' };
  }

  async getConfig(): Promise<IndexConfigResponse> {
    return { ...this.config };
  }

  async updateConfig(
    patch: Partial<Omit<IndexConfigResponse, 'configured'>>,
  ): Promise<IndexConfigResponse & { message: string }> {
    if (patch.includePatterns !== undefined) {
      this.config.includePatterns = patch.includePatterns;
    }
    if (patch.excludePatterns !== undefined) {
      this.config.excludePatterns = patch.excludePatterns;
    }
    if (patch.chunkingConfig) {
      this.config.chunkingConfig = { ...this.config.chunkingConfig, ...patch.chunkingConfig };
    }
    if (patch.embeddingConfig) {
      this.config.embeddingConfig = { ...this.config.embeddingConfig, ...patch.embeddingConfig };
    }
    if (patch.triggerConfig) {
      this.config.triggerConfig = { ...this.config.triggerConfig, ...patch.triggerConfig };
    }
    if (this.indexer?.configure) {
      this.indexer.configure({
        includePatterns: this.config.includePatterns,
        excludePatterns: this.config.excludePatterns,
      });
    }
    return { ...this.config, message: 'Index config updated' };
  }

  async getStats(): Promise<IndexStatsResponse> {
    if (!this.indexer?.getIndexStatus) {
      return { documents: 0, chunks: 0, lastIndexed: null };
    }
    try {
      const s = await this.indexer.getIndexStatus();
      return {
        documents: s.filesIndexed,
        chunks: s.chunksCreated,
        lastIndexed: s.lastIndexedAt ?? null,
      };
    } catch {
      return { documents: 0, chunks: 0, lastIndexed: null };
    }
  }

  async clear(): Promise<{ message: string }> {
    if (this.indexer?.stop) {
      try {
        await this.indexer.stop();
      } catch {
        // Swallow: clear should be best-effort.
      }
    }
    this.currentStatus = { status: 'idle', indexed: 0, pending: 0 };
    return { message: 'Index cleared' };
  }
}
