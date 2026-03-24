/**
 * Codebase Indexer
 *
 * Main service for indexing codebases into searchable chunks.
 * Processes source files, chunks them based on code structure,
 * generates embeddings, and stores them for semantic search.
 */

import * as fs from 'node:fs';
import * as path from 'node:path';
import type {
  ICodebaseIndexer,
  IndexerConfig,
  IndexResult,
  IndexProgress,
  IndexError,
  IndexStatus,
  CodeChunk,
  IndexedChunk,
  DiscoveredFile,
  ProgressHandler,
  ErrorHandler,
  CompleteHandler,
  IndexerEventType,
  ChunkingStrategy,
} from './types.js';
import { DEFAULT_INDEXER_CONFIG, mergeConfig, validateConfig } from './config.js';
import { FileDiscovery, createFileDiscovery, createGitDiffDetector } from './discovery/index.js';
import type { GitDiffDetector } from './discovery/index.js';
import { ChunkerFactory, chunkerFactory } from './chunking/index.js';
import { EmbeddingService, type EmbeddingServiceConfig } from './embedding/index.js';
import {
  generateFileId,
  calculateHash,
  calculateFileHash,
} from './metadata/index.js';
import {
  IndexerError,
  FileReadError,
  NotInitializedError,
  OperationCancelledError,
} from './errors.js';
import type { IVectorStore, VectorDocument, VectorMetadata } from '../vector-store/index.js';
import { FileWatcher } from './triggers/file-watcher.js';
import { GitHookInstaller } from './triggers/git-hook-installer.js';
import { Scheduler } from './triggers/scheduler.js';

/**
 * Event emitter type for indexer events
 */
type EventHandlers = {
  progress: ProgressHandler[];
  error: ErrorHandler[];
  complete: CompleteHandler[];
};

/**
 * File metadata for tracking indexed files
 */
interface FileTrackingInfo {
  hash: string;
  lastIndexed: Date;
  chunkIds: string[];
}

/**
 * Codebase indexer implementation
 */
export class CodebaseIndexer implements ICodebaseIndexer {
  private config: IndexerConfig;
  private vectorStore: IVectorStore | null = null;
  private embeddingService: EmbeddingService | null = null;
  private chunkerFactory: ChunkerFactory;
  private initialized = false;
  private stopping = false;

  // Event handlers
  private eventHandlers: EventHandlers = {
    progress: [],
    error: [],
    complete: [],
  };

  // Tracking state
  private indexedFiles: Map<string, FileTrackingInfo> = new Map();
  private collectionName = 'codebase';

  // Triggers
  private fileWatcher: FileWatcher | null = null;
  private gitHookInstaller: GitHookInstaller | null = null;
  private scheduler: Scheduler | null = null;
  private activeProjectPath: string | null = null;

  constructor(
    vectorStore?: IVectorStore,
    config?: Partial<IndexerConfig>,
  ) {
    this.config = mergeConfig(config ?? {});
    this.vectorStore = vectorStore ?? null;
    this.chunkerFactory = chunkerFactory;
  }

  /**
   * Configure the indexer
   */
  async configure(config: Partial<IndexerConfig>): Promise<void> {
    this.config = mergeConfig({ ...this.config, ...config });
    validateConfig(this.config);
  }

  /**
   * Get current configuration
   */
  getConfig(): IndexerConfig {
    return { ...this.config };
  }

  /**
   * Initialize the indexer with embedding service
   * @param apiKey - API key for embedding provider (optional if using mock)
   */
  async initialize(apiKey?: string): Promise<void> {
    if (this.initialized) {
      return;
    }

    // Create embedding service
    const embeddingConfig: EmbeddingServiceConfig = {
      provider: this.config.embeddingProvider,
      providerConfig: {
        apiKey: apiKey,
        model: this.config.embeddingModel,
      },
      batchSize: this.config.batchSize,
      rateLimitPerMin: this.config.embeddingRateLimitPerMin,
    };

    this.embeddingService = new EmbeddingService(embeddingConfig);
    await this.embeddingService.initialize();

    // Initialize vector store if provided
    if (this.vectorStore) {
      await this.vectorStore.initialize();

      // Create collection if it doesn't exist
      const exists = await this.vectorStore.collectionExists(this.collectionName);
      if (!exists) {
        await this.vectorStore.createCollection(this.collectionName, {
          dimensions: this.embeddingService.getDimensions(),
        });
      }
    }

    this.initialized = true;
  }

  /**
   * Set the vector store (for dependency injection)
   */
  setVectorStore(vectorStore: IVectorStore): void {
    this.vectorStore = vectorStore;
  }

  /**
   * Set the collection name for indexing
   */
  setCollectionName(name: string): void {
    this.collectionName = name;
  }

  /**
   * Index an entire project
   */
  async indexProject(projectPath: string): Promise<IndexResult> {
    this.ensureInitialized();
    this.stopping = false;

    const startTime = performance.now();
    const errors: IndexError[] = [];
    const result: IndexResult = {
      success: true,
      projectPath,
      filesProcessed: 0,
      filesSkipped: 0,
      chunksCreated: 0,
      chunksUpdated: 0,
      chunksDeleted: 0,
      embeddingCostUsd: 0,
      durationMs: 0,
      errors: [],
    };

    try {
      // Phase 1: File Discovery
      this.emitProgress({
        phase: 'discovery',
        filesTotal: 0,
        filesProcessed: 0,
        chunksTotal: 0,
        chunksProcessed: 0,
      });

      const discovery = await createFileDiscovery(projectPath, {
        includePatterns: this.config.includePatterns,
        excludePatterns: this.config.excludePatterns,
        respectGitignore: this.config.respectGitignore,
      });

      const files = await discovery.discover();

      if (this.stopping) {
        throw new OperationCancelledError('indexProject');
      }

      // Phase 2: Chunking
      this.emitProgress({
        phase: 'chunking',
        filesTotal: files.length,
        filesProcessed: 0,
        chunksTotal: 0,
        chunksProcessed: 0,
      });

      const allChunks: CodeChunk[] = [];

      for (let i = 0; i < files.length; i++) {
        if (this.stopping) {
          throw new OperationCancelledError('indexProject');
        }

        const file = files[i];
        this.emitProgress({
          phase: 'chunking',
          filesTotal: files.length,
          filesProcessed: i,
          chunksTotal: allChunks.length,
          chunksProcessed: 0,
          currentFile: file.relativePath,
        });

        try {
          const chunks = await this.processFile(file, projectPath);
          allChunks.push(...chunks);
          result.filesProcessed++;
        } catch (error) {
          const indexError: IndexError = {
            filePath: file.relativePath,
            error: error instanceof Error ? error.message : String(error),
            recoverable: true,
            timestamp: new Date(),
          };
          errors.push(indexError);
          this.emitError(indexError);
          result.filesSkipped++;
        }
      }

      if (this.stopping) {
        throw new OperationCancelledError('indexProject');
      }

      // Phase 3: Embedding
      this.emitProgress({
        phase: 'embedding',
        filesTotal: files.length,
        filesProcessed: files.length,
        chunksTotal: allChunks.length,
        chunksProcessed: 0,
      });

      // Estimate cost
      result.embeddingCostUsd = this.embeddingService!.estimateCost(allChunks);

      // Generate embeddings in batches
      const indexedChunks = await this.embedChunksWithProgress(
        allChunks,
        files.length,
      );

      if (this.stopping) {
        throw new OperationCancelledError('indexProject');
      }

      // Phase 4: Storing
      if (this.vectorStore) {
        this.emitProgress({
          phase: 'storing',
          filesTotal: files.length,
          filesProcessed: files.length,
          chunksTotal: allChunks.length,
          chunksProcessed: allChunks.length,
        });

        // Convert to vector documents and store
        const documents = indexedChunks.map((chunk) =>
          this.chunkToVectorDocument(chunk),
        );

        await this.vectorStore.upsert(this.collectionName, documents);
        result.chunksCreated = documents.length;
      }

      result.chunksCreated = indexedChunks.length;
      result.durationMs = Math.ceil(performance.now() - startTime);
      result.errors = errors;
      result.success = errors.length === 0;

      this.emitComplete(result);
      return result;
    } catch (error) {
      result.durationMs = Math.ceil(performance.now() - startTime);
      result.success = false;

      if (error instanceof OperationCancelledError) {
        result.errors = [
          ...errors,
          {
            filePath: '',
            error: 'Operation cancelled',
            recoverable: true,
            timestamp: new Date(),
          },
        ];
      } else {
        result.errors = [
          ...errors,
          {
            filePath: '',
            error: error instanceof Error ? error.message : String(error),
            recoverable: false,
            timestamp: new Date(),
          },
        ];
      }

      this.emitComplete(result);
      return result;
    }
  }

  /**
   * Update index with changed files only
   */
  async updateIndex(
    projectPath: string,
    changedFiles?: string[],
  ): Promise<IndexResult> {
    this.ensureInitialized();
    this.stopping = false;

    const startTime = performance.now();
    const errors: IndexError[] = [];
    const result: IndexResult = {
      success: true,
      projectPath,
      filesProcessed: 0,
      filesSkipped: 0,
      chunksCreated: 0,
      chunksUpdated: 0,
      chunksDeleted: 0,
      embeddingCostUsd: 0,
      durationMs: 0,
      errors: [],
    };

    try {
      let filesToProcess: DiscoveredFile[] = [];

      if (changedFiles && changedFiles.length > 0) {
        // Use provided list of changed files
        for (const filePath of changedFiles) {
          const absolutePath = path.isAbsolute(filePath)
            ? filePath
            : path.join(projectPath, filePath);
          const relativePath = path.relative(projectPath, absolutePath);

          try {
            const stat = await fs.promises.stat(absolutePath);
            const language = FileDiscovery.detectLanguage(filePath);

            filesToProcess.push({
              absolutePath,
              relativePath,
              language,
              sizeBytes: stat.size,
              lastModified: stat.mtime,
            });
          } catch {
            // File might have been deleted
            await this.handleDeletedFile(relativePath, result);
          }
        }
      } else {
        // Try git diff first for efficient change detection
        const gitDetector = createGitDiffDetector(projectPath);

        if (gitDetector) {
          // Use git-based change detection
          const changes = gitDetector.detectChanges();

          // Handle deleted files
          for (const change of changes) {
            if (change.changeType === 'deleted') {
              await this.handleDeletedFile(change.filePath, result);
            }
          }

          // Build list of added/modified files
          const changedPaths = changes
            .filter((c) => c.changeType !== 'deleted')
            .map((c) => c.filePath);

          for (const relPath of changedPaths) {
            const absolutePath = path.join(projectPath, relPath);
            try {
              const stat = await fs.promises.stat(absolutePath);
              const language = FileDiscovery.detectLanguage(relPath);
              filesToProcess.push({
                absolutePath,
                relativePath: relPath,
                language,
                sizeBytes: stat.size,
                lastModified: stat.mtime,
              });
            } catch {
              // File might have been deleted between detection and stat
            }
          }
        } else {
          // Fall back to hash-based change detection
          const discovery = await createFileDiscovery(projectPath, {
            includePatterns: this.config.includePatterns,
            excludePatterns: this.config.excludePatterns,
            respectGitignore: this.config.respectGitignore,
          });

          const allFiles = await discovery.discover();
          filesToProcess = await this.filterChangedFiles(allFiles, projectPath);
        }
      }

      if (filesToProcess.length === 0) {
        result.durationMs = Math.ceil(performance.now() - startTime);
        return result;
      }

      // Process changed files
      const allChunks: CodeChunk[] = [];

      for (const file of filesToProcess) {
        if (this.stopping) {
          throw new OperationCancelledError('updateIndex');
        }

        try {
          // Delete old chunks for this file
          const tracking = this.indexedFiles.get(file.relativePath);
          if (tracking && tracking.chunkIds.length > 0 && this.vectorStore) {
            await this.vectorStore.delete(this.collectionName, tracking.chunkIds);
            result.chunksDeleted += tracking.chunkIds.length;
          }

          const chunks = await this.processFile(file, projectPath);
          allChunks.push(...chunks);
          result.filesProcessed++;
        } catch (error) {
          const indexError: IndexError = {
            filePath: file.relativePath,
            error: error instanceof Error ? error.message : String(error),
            recoverable: true,
            timestamp: new Date(),
          };
          errors.push(indexError);
          this.emitError(indexError);
          result.filesSkipped++;
        }
      }

      // Generate embeddings and store
      if (allChunks.length > 0) {
        result.embeddingCostUsd = this.embeddingService!.estimateCost(allChunks);
        const indexedChunks = await this.embedChunksWithProgress(
          allChunks,
          filesToProcess.length,
        );

        if (this.vectorStore) {
          const documents = indexedChunks.map((chunk) =>
            this.chunkToVectorDocument(chunk),
          );
          await this.vectorStore.upsert(this.collectionName, documents);
        }

        result.chunksCreated = indexedChunks.length;
      }

      result.durationMs = Math.ceil(performance.now() - startTime);
      result.errors = errors;
      result.success = errors.length === 0;

      return result;
    } catch (error) {
      result.durationMs = Math.ceil(performance.now() - startTime);
      result.success = false;
      result.errors = [
        ...errors,
        {
          filePath: '',
          error: error instanceof Error ? error.message : String(error),
          recoverable: false,
          timestamp: new Date(),
        },
      ];
      return result;
    }
  }

  /**
   * Remove files from the index
   */
  async removeFromIndex(filePaths: string[]): Promise<void> {
    if (!this.vectorStore) {
      return;
    }

    const chunkIdsToDelete: string[] = [];

    for (const filePath of filePaths) {
      const tracking = this.indexedFiles.get(filePath);
      if (tracking) {
        chunkIdsToDelete.push(...tracking.chunkIds);
        this.indexedFiles.delete(filePath);
      }
    }

    if (chunkIdsToDelete.length > 0) {
      await this.vectorStore.delete(this.collectionName, chunkIdsToDelete);
    }
  }

  /**
   * Clear the entire index for a project
   */
  async clearIndex(projectPath: string): Promise<void> {
    if (this.vectorStore) {
      const exists = await this.vectorStore.collectionExists(this.collectionName);
      if (exists) {
        await this.vectorStore.deleteCollection(this.collectionName);
        await this.vectorStore.createCollection(this.collectionName, {
          dimensions: this.embeddingService?.getDimensions() ?? 1536,
        });
      }
    }

    this.indexedFiles.clear();
  }

  /**
   * Get index status
   */
  async getIndexStatus(projectPath: string): Promise<IndexStatus> {
    const status: IndexStatus = {
      projectPath,
      lastIndexedAt: undefined,
      totalFiles: this.indexedFiles.size,
      totalChunks: 0,
      indexSizeBytes: 0,
      isStale: false,
    };

    if (this.vectorStore) {
      try {
        const stats = await this.vectorStore.getCollectionStats(this.collectionName);
        status.totalChunks = stats.documentCount;
        status.indexSizeBytes = stats.indexSize ?? 0;
      } catch {
        // Collection might not exist yet
      }
    }

    // Calculate total chunks from tracking
    for (const tracking of this.indexedFiles.values()) {
      if (tracking.lastIndexed) {
        if (!status.lastIndexedAt || tracking.lastIndexed > status.lastIndexedAt) {
          status.lastIndexedAt = tracking.lastIndexed;
        }
      }
    }

    return status;
  }

  /**
   * Check if a file is stale (needs re-indexing)
   */
  async isFileStale(filePath: string): Promise<boolean> {
    const tracking = this.indexedFiles.get(filePath);
    if (!tracking) {
      return true; // Not indexed yet
    }

    try {
      const content = await fs.promises.readFile(filePath, 'utf-8');
      const currentHash = calculateHash(content);
      return currentHash !== tracking.hash;
    } catch {
      return true; // File might have been deleted
    }
  }

  /**
   * Subscribe to events
   */
  on(event: 'progress', handler: ProgressHandler): void;
  on(event: 'error', handler: ErrorHandler): void;
  on(event: 'complete', handler: CompleteHandler): void;
  on(
    event: IndexerEventType,
    handler: ProgressHandler | ErrorHandler | CompleteHandler,
  ): void {
    this.eventHandlers[event].push(handler as never);
  }

  /**
   * Unsubscribe from events
   */
  off(event: IndexerEventType, handler: Function): void {
    const handlers = this.eventHandlers[event];
    const index = handlers.indexOf(handler as never);
    if (index !== -1) {
      handlers.splice(index, 1);
    }
  }

  /**
   * Start configured triggers for automatic re-indexing.
   * Must be called after indexProject to know which project to watch.
   *
   * @param projectPath - Path to the project root to watch
   */
  async startTriggers(projectPath: string): Promise<void> {
    this.activeProjectPath = projectPath;

    // File watcher trigger
    if (this.config.enableFileWatcher) {
      this.fileWatcher = new FileWatcher({
        watchPaths: [projectPath],
      });
      this.fileWatcher.start((changedFiles) => {
        if (this.activeProjectPath && this.initialized) {
          // Convert absolute paths to relative
          const relative = changedFiles.map((f) =>
            path.relative(this.activeProjectPath!, f),
          );
          this.updateIndex(this.activeProjectPath, relative).catch((error: unknown) => {
            console.warn('[CodebaseIndexer] File watcher re-index failed:', error instanceof Error ? error.message : String(error));
          });
        }
      });
    }

    // Git hooks trigger
    if (this.config.enableGitHooks) {
      this.gitHookInstaller = new GitHookInstaller({
        projectRoot: projectPath,
      });
      await this.gitHookInstaller.install();
    }

    // Scheduler trigger
    if (this.config.scheduleCron) {
      // Parse simple interval from cron-like expression or use default 30 min
      const intervalMs = 30 * 60 * 1000;
      this.scheduler = new Scheduler({ intervalMs });
      this.scheduler.start(() => {
        if (this.activeProjectPath && this.initialized) {
          this.updateIndex(this.activeProjectPath).catch((error: unknown) => {
            console.warn('[CodebaseIndexer] Scheduled re-index failed:', error instanceof Error ? error.message : String(error));
          });
        }
      });
    }
  }

  /**
   * Stop all triggers
   */
  async stopTriggers(): Promise<void> {
    if (this.fileWatcher) {
      this.fileWatcher.stop();
      this.fileWatcher = null;
    }

    if (this.gitHookInstaller) {
      await this.gitHookInstaller.uninstall();
      this.gitHookInstaller = null;
    }

    if (this.scheduler) {
      this.scheduler.stop();
      this.scheduler = null;
    }

    this.activeProjectPath = null;
  }

  /**
   * Stop ongoing indexing
   */
  async stop(): Promise<void> {
    this.stopping = true;
  }

  /**
   * Dispose resources
   */
  async dispose(): Promise<void> {
    this.stopping = true;

    // Stop triggers first
    await this.stopTriggers();

    if (this.embeddingService) {
      await this.embeddingService.dispose();
      this.embeddingService = null;
    }

    // The vector store is injected externally — do not dispose it here.
    // The owner is responsible for its lifecycle.
    this.vectorStore = null;

    this.indexedFiles.clear();
    this.initialized = false;
  }

  // === Private Methods ===

  /**
   * Ensure indexer is initialized
   */
  private ensureInitialized(): void {
    if (!this.initialized || !this.embeddingService) {
      throw new NotInitializedError();
    }
  }

  /**
   * Process a single file into chunks
   */
  private async processFile(
    file: DiscoveredFile,
    projectPath: string,
  ): Promise<CodeChunk[]> {
    // Read file content
    let content: string;
    try {
      content = await fs.promises.readFile(file.absolutePath, 'utf-8');
    } catch (error) {
      throw new FileReadError(file.relativePath, {
        cause: error instanceof Error ? error : new Error(String(error)),
      });
    }

    const fileId = generateFileId(file.relativePath);

    // Get chunker and strategy for this language
    const chunker = this.chunkerFactory.getChunker(file.language);
    const strategy: ChunkingStrategy = this.chunkerFactory.createStrategy(
      file.language,
      {
        maxChunkTokens: this.config.maxChunkTokens,
        overlapTokens: this.config.overlapTokens,
        preserveImports: this.config.preserveImports,
        groupRelatedCode: this.config.groupRelatedCode,
      },
    );

    // Chunk the file
    const chunks = await chunker.chunk(content, file.relativePath, fileId, strategy);

    // Update tracking
    this.indexedFiles.set(file.relativePath, {
      hash: calculateHash(content),
      lastIndexed: new Date(),
      chunkIds: chunks.map((c) => c.id),
    });

    return chunks;
  }

  /**
   * Embed chunks with progress updates
   */
  private async embedChunksWithProgress(
    chunks: CodeChunk[],
    totalFiles: number,
  ): Promise<IndexedChunk[]> {
    const batchSize = this.config.batchSize;
    const allIndexedChunks: IndexedChunk[] = [];

    for (let i = 0; i < chunks.length; i += batchSize) {
      if (this.stopping) {
        throw new OperationCancelledError('embedding');
      }

      const batch = chunks.slice(i, i + batchSize);
      const indexedBatch = await this.embeddingService!.embedChunks(batch);
      allIndexedChunks.push(...indexedBatch);

      this.emitProgress({
        phase: 'embedding',
        filesTotal: totalFiles,
        filesProcessed: totalFiles,
        chunksTotal: chunks.length,
        chunksProcessed: i + batch.length,
      });
    }

    return allIndexedChunks;
  }

  /**
   * Convert indexed chunk to vector document
   */
  private chunkToVectorDocument(chunk: IndexedChunk): VectorDocument {
    const metadata: VectorMetadata = {
      filePath: chunk.filePath,
      language: chunk.language,
      chunkType: chunk.chunkType as 'function' | 'class' | 'module' | 'block',
      name: chunk.name,
      startLine: chunk.startLine,
      endLine: chunk.endLine,
      parentScope: chunk.parentScope,
      imports: chunk.imports,
      exports: chunk.exports,
      docstring: chunk.docstring,
      hash: chunk.hash,
      indexedAt: chunk.indexedAt.toISOString(),
    };

    return {
      id: chunk.id,
      embedding: chunk.embedding,
      content: chunk.content,
      metadata,
    };
  }

  /**
   * Filter files to only those that have changed
   */
  private async filterChangedFiles(
    files: DiscoveredFile[],
    projectPath: string,
  ): Promise<DiscoveredFile[]> {
    const changedFiles: DiscoveredFile[] = [];

    for (const file of files) {
      const tracking = this.indexedFiles.get(file.relativePath);

      if (!tracking) {
        // New file
        changedFiles.push(file);
        continue;
      }

      // Check if file content has changed
      try {
        const content = await fs.promises.readFile(file.absolutePath, 'utf-8');
        const currentHash = calculateHash(content);

        if (currentHash !== tracking.hash) {
          changedFiles.push(file);
        }
      } catch {
        // File might have been deleted or is unreadable
        changedFiles.push(file);
      }
    }

    return changedFiles;
  }

  /**
   * Handle a deleted file
   */
  private async handleDeletedFile(
    relativePath: string,
    result: IndexResult,
  ): Promise<void> {
    const tracking = this.indexedFiles.get(relativePath);
    if (tracking && this.vectorStore) {
      await this.vectorStore.delete(this.collectionName, tracking.chunkIds);
      result.chunksDeleted += tracking.chunkIds.length;
    }
    this.indexedFiles.delete(relativePath);
  }

  // === Event Emission ===

  private emitProgress(progress: IndexProgress): void {
    for (const handler of this.eventHandlers.progress) {
      try {
        handler(progress);
      } catch {
        // Ignore handler errors
      }
    }
  }

  private emitError(error: IndexError): void {
    for (const handler of this.eventHandlers.error) {
      try {
        handler(error);
      } catch {
        // Ignore handler errors
      }
    }
  }

  private emitComplete(result: IndexResult): void {
    for (const handler of this.eventHandlers.complete) {
      try {
        handler(result);
      } catch {
        // Ignore handler errors
      }
    }
  }
}

/**
 * Create a codebase indexer instance
 * @param vectorStore - Optional vector store for persistence
 * @param config - Optional configuration
 * @returns CodebaseIndexer instance
 */
export function createCodebaseIndexer(
  vectorStore?: IVectorStore,
  config?: Partial<IndexerConfig>,
): CodebaseIndexer {
  return new CodebaseIndexer(vectorStore, config);
}
