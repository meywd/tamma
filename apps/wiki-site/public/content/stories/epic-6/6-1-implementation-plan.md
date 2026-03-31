---
title: "Story 6-1: Codebase Indexer - Implementation Plan"
sidebar:
  order: 60
---

**Epic**: Epic 6 - Context & Knowledge Management
**Status**: Ready for Development
**Priority**: High
**Estimated Effort**: 2-3 weeks

---

## Overview

This implementation plan details the approach for building the Codebase Indexer, which processes source files, chunks them intelligently based on code structure, generates embeddings, and stores them for semantic search.

---

## Package Location

**Primary Package**: `@tamma/intelligence`
**Path**: `/packages/intelligence/`

The intelligence package is the natural home for the codebase indexer as it:
- Aligns with the package's purpose (research and intelligence for the Tamma platform)
- Has existing dependencies on `@tamma/shared`, `@tamma/providers`, and `@tamma/observability`
- Will integrate closely with the RAG pipeline (Story 6-3) also planned for this package

---

## Files to Create

### Core Indexer Files

```
packages/intelligence/src/
├── indexer/
│   ├── index.ts                      # Public exports
│   ├── codebase-indexer.ts           # Main indexer service implementation
│   ├── codebase-indexer.test.ts      # Unit tests for indexer
│   ├── types.ts                      # All indexer-related types
│   │
│   ├── discovery/
│   │   ├── file-discovery.ts         # File discovery with glob patterns
│   │   ├── file-discovery.test.ts    # File discovery tests
│   │   ├── gitignore-parser.ts       # .gitignore pattern handling
│   │   └── gitignore-parser.test.ts
│   │
│   ├── chunking/
│   │   ├── chunker.ts                # Base chunker interface & factory
│   │   ├── chunker.test.ts
│   │   ├── typescript-chunker.ts     # TypeScript/JavaScript AST chunker
│   │   ├── typescript-chunker.test.ts
│   │   ├── python-chunker.ts         # Python AST chunker (tree-sitter)
│   │   ├── python-chunker.test.ts
│   │   ├── generic-chunker.ts        # Fallback line-based chunker
│   │   └── generic-chunker.test.ts
│   │
│   ├── metadata/
│   │   ├── metadata-extractor.ts     # Symbol, import, docstring extraction
│   │   ├── metadata-extractor.test.ts
│   │   ├── hash-calculator.ts        # Content hashing for change detection
│   │   └── hash-calculator.test.ts
│   │
│   ├── embedding/
│   │   ├── embedding-provider.ts     # Embedding provider abstraction
│   │   ├── embedding-provider.test.ts
│   │   ├── openai-embedder.ts        # OpenAI text-embedding-3-small
│   │   ├── openai-embedder.test.ts
│   │   ├── cohere-embedder.ts        # Cohere embed-english-v3.0
│   │   ├── ollama-embedder.ts        # Local Ollama embeddings
│   │   └── embedding-cache.ts        # LRU cache for embeddings
│   │
│   └── triggers/
│       ├── index-trigger.ts          # Base trigger interface
│       ├── git-hook-trigger.ts       # Git post-commit/merge hooks
│       ├── file-watcher-trigger.ts   # File system watcher (chokidar)
│       └── scheduler-trigger.ts      # Cron-based scheduled indexing
```

### Configuration Files

```
packages/intelligence/
├── src/
│   └── config/
│       └── indexer-config.ts         # Configuration schema and defaults
```

### Shared Types (if needed in other packages)

```
packages/shared/src/
└── types/
    └── indexer.ts                    # Shared indexer types (CodeChunk, etc.)
```

---

## Files to Modify

| File | Changes |
|------|---------|
| `packages/intelligence/src/index.ts` | Export indexer module |
| `packages/intelligence/package.json` | Add dependencies (tree-sitter, chokidar, etc.) |
| `packages/shared/src/index.ts` | Export shared indexer types (if any) |
| `packages/shared/src/types/index.ts` | Add indexer-related shared types |

---

## Interfaces and Types

### Core Types (`packages/intelligence/src/indexer/types.ts`)

```typescript
/**
 * Supported programming languages for indexing
 */
export type SupportedLanguage =
  | 'typescript'
  | 'javascript'
  | 'python'
  | 'go'
  | 'rust'
  | 'java'
  | 'unknown';

/**
 * Chunk type classification
 */
export type ChunkType =
  | 'function'
  | 'class'
  | 'interface'
  | 'module'
  | 'block'
  | 'imports';

/**
 * A semantic chunk of code extracted from a source file
 */
export interface CodeChunk {
  id: string;                      // Unique chunk ID (hash-based)
  fileId: string;                  // Parent file ID
  filePath: string;                // Relative file path from project root
  language: SupportedLanguage;     // Programming language
  chunkType: ChunkType;            // Semantic unit type
  name: string;                    // Symbol name (function/class name, or 'anonymous')
  content: string;                 // Raw code content
  startLine: number;               // 1-indexed start line
  endLine: number;                 // 1-indexed end line
  parentScope?: string;            // Parent class/module name (for methods)
  imports: string[];               // Import dependencies
  exports: string[];               // Exported symbols
  docstring?: string;              // JSDoc/docstring if present
  tokenCount: number;              // Estimated token count
  hash: string;                    // Content hash for change detection
}

/**
 * Chunk with embedding vector attached
 */
export interface IndexedChunk extends CodeChunk {
  embedding: number[];             // Vector embedding (1536 dims for OpenAI)
  indexedAt: Date;                 // When this chunk was indexed
}

/**
 * Metadata for a processed file
 */
export interface FileMetadata {
  id: string;                      // Unique file ID
  path: string;                    // Relative file path
  language: SupportedLanguage;
  sizeBytes: number;
  lineCount: number;
  hash: string;                    // File content hash
  lastModified: Date;
  chunkCount: number;
  symbols: string[];               // All exported/public symbols
  imports: string[];               // All imports
}

/**
 * Indexer configuration
 */
export interface IndexerConfig {
  // File discovery
  includePatterns: string[];       // Glob patterns to include
  excludePatterns: string[];       // Glob patterns to exclude
  respectGitignore: boolean;       // Whether to respect .gitignore

  // Chunking
  maxChunkTokens: number;          // Max tokens per chunk (default: 512)
  overlapTokens: number;           // Overlap between chunks (default: 50)
  preserveImports: boolean;        // Keep imports as separate chunk
  groupRelatedCode: boolean;       // Group small related functions

  // Embedding
  embeddingProvider: 'openai' | 'cohere' | 'ollama';
  embeddingModel: string;          // Model ID
  batchSize: number;               // Batch size for embedding requests

  // Triggers
  enableGitHooks: boolean;
  enableFileWatcher: boolean;
  scheduleCron?: string;           // Cron expression for scheduled indexing

  // Performance
  concurrency: number;             // Max concurrent file processing
  embeddingRateLimitPerMin: number;
}

/**
 * Result of an indexing operation
 */
export interface IndexResult {
  success: boolean;
  projectPath: string;
  filesProcessed: number;
  filesSkipped: number;
  chunksCreated: number;
  chunksUpdated: number;
  chunksDeleted: number;
  embeddingCostUsd: number;
  durationMs: number;
  errors: IndexError[];
}

/**
 * Indexing error with context
 */
export interface IndexError {
  filePath: string;
  error: string;
  recoverable: boolean;
  timestamp: Date;
}

/**
 * Progress callback for indexing operations
 */
export interface IndexProgress {
  phase: 'discovery' | 'chunking' | 'embedding' | 'storing';
  filesTotal: number;
  filesProcessed: number;
  chunksTotal: number;
  chunksProcessed: number;
  currentFile?: string;
}

/**
 * Index status and metadata
 */
export interface IndexStatus {
  projectPath: string;
  lastIndexedAt?: Date;
  totalFiles: number;
  totalChunks: number;
  indexSizeBytes: number;
  isStale: boolean;
  staleSince?: Date;
}
```

### Main Indexer Interface

```typescript
/**
 * Main codebase indexer service interface
 */
export interface ICodebaseIndexer {
  /**
   * Configure the indexer with project-specific settings
   */
  configure(config: Partial<IndexerConfig>): Promise<void>;

  /**
   * Get current configuration
   */
  getConfig(): IndexerConfig;

  /**
   * Full project indexing (scans all files)
   */
  indexProject(projectPath: string): Promise<IndexResult>;

  /**
   * Incremental update (only changed files)
   * If changedFiles not provided, detects changes via git diff or file hashes
   */
  updateIndex(projectPath: string, changedFiles?: string[]): Promise<IndexResult>;

  /**
   * Remove specific files from the index
   */
  removeFromIndex(filePaths: string[]): Promise<void>;

  /**
   * Clear entire index for a project
   */
  clearIndex(projectPath: string): Promise<void>;

  /**
   * Get current index status and metadata
   */
  getIndexStatus(projectPath: string): Promise<IndexStatus>;

  /**
   * Check if a file needs re-indexing
   */
  isFileStale(filePath: string): Promise<boolean>;

  /**
   * Event subscription for progress updates
   */
  on(event: 'progress', handler: (progress: IndexProgress) => void): void;
  on(event: 'error', handler: (error: IndexError) => void): void;
  on(event: 'complete', handler: (result: IndexResult) => void): void;

  /**
   * Remove event listener
   */
  off(event: string, handler: Function): void;

  /**
   * Gracefully stop any ongoing indexing
   */
  stop(): Promise<void>;

  /**
   * Dispose resources
   */
  dispose(): Promise<void>;
}
```

### Chunker Interface

```typescript
/**
 * Language-specific chunking strategy
 */
export interface ChunkingStrategy {
  language: SupportedLanguage;
  parser: 'tree-sitter' | 'babel' | 'typescript' | 'custom';
  maxChunkTokens: number;
  overlapTokens: number;
  preserveImports: boolean;
  groupRelatedCode: boolean;
}

/**
 * Abstract chunker interface for language-specific implementations
 */
export interface ICodeChunker {
  /**
   * Languages this chunker supports
   */
  readonly supportedLanguages: SupportedLanguage[];

  /**
   * Chunk a file's content into semantic units
   */
  chunk(
    content: string,
    filePath: string,
    strategy: ChunkingStrategy
  ): Promise<CodeChunk[]>;

  /**
   * Estimate token count for content
   */
  estimateTokens(content: string): number;
}
```

### Embedding Provider Interface

```typescript
/**
 * Embedding provider abstraction
 */
export interface IEmbeddingProvider {
  readonly name: string;
  readonly dimensions: number;
  readonly maxBatchSize: number;

  /**
   * Initialize the provider
   */
  initialize(config: EmbeddingProviderConfig): Promise<void>;

  /**
   * Generate embedding for a single text
   */
  embed(text: string): Promise<number[]>;

  /**
   * Batch embed multiple texts
   */
  embedBatch(texts: string[]): Promise<number[][]>;

  /**
   * Get estimated cost for embedding (in USD)
   */
  estimateCost(tokenCount: number): number;

  /**
   * Dispose resources
   */
  dispose(): Promise<void>;
}

export interface EmbeddingProviderConfig {
  apiKey?: string;
  baseUrl?: string;
  model: string;
  timeout?: number;
}
```

---

## Implementation Phases

### Phase 1: Core Infrastructure (Week 1)

**Goal**: Basic indexing pipeline without embeddings

#### Tasks

1. **Project setup and dependencies** (Day 1)
   - Add dependencies to `package.json`
   - Set up TypeScript configuration
   - Create folder structure

2. **File Discovery** (Day 1-2)
   - Implement glob-based file scanning
   - Add .gitignore parsing and filtering
   - Support configurable include/exclude patterns
   - Add language detection by file extension

3. **TypeScript/JavaScript Chunker** (Day 2-3)
   - Use TypeScript compiler API for AST parsing
   - Extract functions, classes, interfaces
   - Handle JSDoc extraction
   - Preserve import statements
   - Handle large files with intelligent splitting

4. **Generic Chunker** (Day 3)
   - Line-based chunking for unsupported languages
   - Respect max token limits
   - Add overlap between chunks

5. **Metadata Extraction** (Day 4)
   - Content hash calculation (SHA-256)
   - Symbol extraction
   - Import/export detection
   - Token estimation (using tiktoken)

6. **Core Indexer Service** (Day 4-5)
   - Implement `ICodebaseIndexer` interface
   - Wire up file discovery and chunking
   - Add event emission for progress tracking
   - Basic error handling and recovery

#### Deliverables
- File discovery with glob patterns
- TypeScript/JavaScript chunking
- Generic fallback chunker
- Basic indexer service (without embeddings)
- Unit tests for all components

### Phase 2: Embedding Integration (Week 2, Days 1-3)

**Goal**: Integrate embedding providers and vector storage

#### Tasks

1. **OpenAI Embedding Provider** (Day 1)
   - Implement `IEmbeddingProvider` for OpenAI
   - Use `text-embedding-3-small` model
   - Batch embedding support
   - Rate limiting with exponential backoff
   - Cost tracking

2. **Embedding Cache** (Day 1)
   - LRU cache for embeddings
   - Content-hash based cache keys
   - Configurable cache size

3. **Cohere & Ollama Providers** (Day 2)
   - Cohere `embed-english-v3.0` implementation
   - Ollama local embedding support
   - Provider factory for easy switching

4. **Vector Store Integration** (Day 2-3)
   - Interface with Story 6-2 vector store
   - Batch upsert for efficiency
   - Metadata storage with vectors
   - Deletion of stale chunks

5. **Full Pipeline Integration** (Day 3)
   - Connect chunking to embedding
   - Connect embedding to vector store
   - End-to-end indexing flow
   - Progress tracking through pipeline

#### Deliverables
- OpenAI embedding provider
- Cohere and Ollama providers
- Embedding cache
- Integration with vector store
- Full end-to-end indexing

### Phase 3: Incremental Updates & Triggers (Week 2, Days 4-5)

**Goal**: Efficient incremental indexing and automatic triggers

#### Tasks

1. **Change Detection** (Day 4)
   - Git diff-based change detection
   - File hash comparison for non-git scenarios
   - Detect added, modified, deleted files
   - Track indexed file metadata

2. **Incremental Update Logic** (Day 4)
   - Re-index only changed files
   - Remove chunks from deleted files
   - Handle renamed files (delete + add)
   - Optimize for minimal embedding calls

3. **Git Hook Integration** (Day 5)
   - Post-commit hook handler
   - Post-merge hook handler
   - Installation script for hooks

4. **File Watcher Trigger** (Day 5)
   - Chokidar-based file watching
   - Debouncing for rapid changes
   - Development mode support

5. **Scheduled Trigger** (Day 5)
   - Cron-based scheduling
   - Configurable intervals
   - Manual trigger API

#### Deliverables
- Incremental update system
- Git hook integration
- File watcher for development
- Scheduled re-indexing

### Phase 4: Additional Languages & Polish (Week 3, Days 1-3)

**Goal**: Expand language support and production-ready polish

#### Tasks

1. **Python Chunker** (Day 1)
   - Tree-sitter based parsing
   - Function/class extraction
   - Docstring handling
   - Import grouping

2. **Go Chunker** (Day 2)
   - Tree-sitter based parsing
   - Function/struct extraction
   - Package-level code handling

3. **Additional Languages** (Day 2)
   - Rust basic support
   - Java basic support
   - Graceful fallback to generic chunker

4. **Monitoring & Metrics** (Day 3)
   - Indexing duration metrics
   - Chunk count metrics
   - Embedding cost tracking
   - Error rate monitoring
   - Integration with `@tamma/observability`

5. **Configuration & Documentation** (Day 3)
   - YAML configuration support
   - Default configuration
   - API documentation
   - Usage examples

#### Deliverables
- Python, Go, Rust, Java chunkers
- Production metrics
- Configuration system
- Documentation

### Phase 5: Testing & Integration (Week 3, Days 4-5)

**Goal**: Comprehensive testing and system integration

#### Tasks

1. **Integration Tests** (Day 4)
   - End-to-end indexing tests
   - Incremental update scenarios
   - Multi-language project tests
   - Large file handling tests

2. **Performance Tests** (Day 4)
   - 10,000+ file indexing benchmark
   - Memory usage profiling
   - Embedding throughput testing
   - Incremental update performance

3. **Error Scenarios** (Day 5)
   - Malformed file handling
   - Network failure recovery
   - Rate limit handling
   - Disk space handling

4. **System Integration** (Day 5)
   - Integration with RAG pipeline (Story 6-3)
   - Integration with Scrum Master context
   - CLI commands for manual indexing

#### Deliverables
- Full test coverage
- Performance benchmarks
- Error handling documentation
- System integration verified

---

## Dependencies

### External NPM Packages

| Package | Version | Purpose |
|---------|---------|---------|
| `typescript` | `~5.7.2` | TypeScript compiler API for JS/TS parsing |
| `tree-sitter` | `^0.21.x` | AST parsing for Python, Go, Rust, Java |
| `tree-sitter-python` | `^0.21.x` | Python grammar |
| `tree-sitter-go` | `^0.21.x` | Go grammar |
| `tree-sitter-rust` | `^0.21.x` | Rust grammar |
| `tree-sitter-java` | `^0.21.x` | Java grammar |
| `glob` | `^10.x` | File pattern matching |
| `ignore` | `^5.x` | .gitignore parsing |
| `chokidar` | `^3.x` | File system watching |
| `tiktoken` | `^1.x` | Token counting |
| `openai` | `^4.x` | OpenAI API client |
| `cohere-ai` | `^7.x` | Cohere API client |
| `lru-cache` | `^10.x` | Embedding cache |
| `node-cron` | `^3.x` | Scheduled indexing |
| `crypto` | (built-in) | Content hashing |
| `simple-git` | `^3.x` | Git operations for change detection |

### Internal Package Dependencies

| Package | Purpose |
|---------|---------|
| `@tamma/shared` | Common types, errors, utilities |
| `@tamma/providers` | Potential reuse of API client patterns |
| `@tamma/observability` | Metrics, logging, tracing |

### Story Dependencies

| Story | Dependency Type | Notes |
|-------|-----------------|-------|
| Story 6-2: Vector Database | Hard dependency | Storage backend for indexed chunks |
| Story 6-3: RAG Pipeline | Soft dependency | Consumer of indexed data |
| Story 6-5: Context Aggregator | Soft dependency | Uses index for context |

---

## Testing Strategy

### Unit Tests

**Coverage Target**: 90%+

```typescript
// Example test structure
describe('TypeScriptChunker', () => {
  describe('chunk', () => {
    it('should extract functions as separate chunks');
    it('should extract classes with their methods');
    it('should preserve JSDoc comments');
    it('should handle arrow functions');
    it('should group imports into single chunk');
    it('should handle large files with multiple chunks');
    it('should respect maxChunkTokens limit');
    it('should add overlap between chunks');
  });
});

describe('FileDiscovery', () => {
  describe('discover', () => {
    it('should find files matching include patterns');
    it('should exclude files matching exclude patterns');
    it('should respect .gitignore');
    it('should detect language from extension');
    it('should handle nested directories');
  });
});

describe('EmbeddingProvider', () => {
  describe('embed', () => {
    it('should return correct dimension vectors');
    it('should handle rate limits with backoff');
    it('should cache duplicate content');
    it('should track cost accurately');
  });
});
```

### Integration Tests

```typescript
describe('CodebaseIndexer Integration', () => {
  it('should index a complete TypeScript project');
  it('should perform incremental update after file change');
  it('should handle mixed-language projects');
  it('should recover from embedding provider errors');
  it('should emit progress events during indexing');
  it('should clean up stale chunks on re-index');
});
```

### Performance Tests

```typescript
describe('Performance', () => {
  it('should index 10,000 files in under 5 minutes');
  it('should perform incremental update for 10 files in under 10 seconds');
  it('should maintain bounded memory usage (< 1GB for large projects)');
  it('should batch embed efficiently (>100 chunks/second)');
});
```

### Test Fixtures

Create test fixtures for:
- Small TypeScript project (10 files)
- Large TypeScript project (1000+ files)
- Python project with classes and docstrings
- Mixed-language project
- Project with malformed files
- Project with very large files (5000+ lines)

---

## Configuration

### Default Configuration

```typescript
// packages/intelligence/src/config/indexer-config.ts

export const DEFAULT_INDEXER_CONFIG: IndexerConfig = {
  // File discovery
  includePatterns: [
    '**/*.ts',
    '**/*.tsx',
    '**/*.js',
    '**/*.jsx',
    '**/*.py',
    '**/*.go',
    '**/*.rs',
    '**/*.java',
  ],
  excludePatterns: [
    '**/node_modules/**',
    '**/dist/**',
    '**/build/**',
    '**/.git/**',
    '**/vendor/**',
    '**/target/**',
    '**/*.test.ts',
    '**/*.spec.ts',
    '**/*.test.js',
    '**/*.spec.js',
    '**/__tests__/**',
    '**/*.d.ts',
    '**/*.min.js',
  ],
  respectGitignore: true,

  // Chunking
  maxChunkTokens: 512,
  overlapTokens: 50,
  preserveImports: true,
  groupRelatedCode: true,

  // Embedding
  embeddingProvider: 'openai',
  embeddingModel: 'text-embedding-3-small',
  batchSize: 100,

  // Triggers
  enableGitHooks: false,
  enableFileWatcher: false,
  scheduleCron: undefined,

  // Performance
  concurrency: 10,
  embeddingRateLimitPerMin: 3000,
};
```

### YAML Configuration Example

```yaml
# tamma.config.yaml - indexer section

indexer:
  include_patterns:
    - "**/*.ts"
    - "**/*.tsx"
    - "**/*.js"
    - "**/*.py"
    - "**/*.go"

  exclude_patterns:
    - "**/node_modules/**"
    - "**/dist/**"
    - "**/*.test.ts"
    - "**/*.spec.ts"

  respect_gitignore: true

  chunking:
    max_tokens: 512
    overlap_tokens: 50
    preserve_imports: true
    group_related_code: true

  embedding:
    provider: openai
    model: text-embedding-3-small
    batch_size: 100
    rate_limit_per_min: 3000

  triggers:
    git_hooks: true
    watch_mode: false
    schedule: "0 2 * * *"  # 2 AM daily

  performance:
    concurrency: 10
```

### Environment Variables

```bash
# Required for OpenAI embedding
OPENAI_API_KEY=sk-...

# Required for Cohere embedding
COHERE_API_KEY=...

# For Ollama (optional)
OLLAMA_BASE_URL=http://localhost:11434

# Index storage location
TAMMA_INDEX_PATH=./data/index
```

---

## Success Metrics

| Metric | Target | Measurement |
|--------|--------|-------------|
| Indexing throughput | > 100 files/second | Time to index / file count |
| Incremental update speed | < 10 seconds for 10 files | Time to update changed files |
| Memory usage | < 1GB peak | Memory profiling during large index |
| Chunk quality | 90%+ relevant code | Manual evaluation of chunk boundaries |
| Change detection accuracy | 100% | No missed changes in incremental updates |
| Embedding cost | < $0.01 per 1000 files | Cost tracking during indexing |
| Error recovery rate | 95%+ | Recoverable errors handled gracefully |

---

## Risk Mitigation

| Risk | Mitigation |
|------|------------|
| Tree-sitter compilation issues | Provide pre-built binaries; fallback to generic chunker |
| Embedding API rate limits | Exponential backoff; request queuing; cost monitoring |
| Large file memory issues | Stream-based processing; chunking before full parse |
| Partial indexing failures | Continue-on-error; detailed error reporting; partial results |
| Vector DB unavailability | Queue chunks for later storage; health checks before indexing |

---

## Open Questions

1. **Vector Store Choice**: Should we implement a mock vector store for testing, or require Story 6-2 to be completed first?
   - **Recommendation**: Implement with an in-memory mock store, swap to real store later

2. **Embedding Dimension Flexibility**: Different providers have different dimensions. How to handle?
   - **Recommendation**: Store dimensions in metadata; require same provider for a project

3. **Cross-project Indexing**: Should one index span multiple repos?
   - **Recommendation**: Start with single-project scope; extend later if needed

4. **Index Persistence Format**: Where to store index metadata (file hashes, last indexed)?
   - **Recommendation**: SQLite for metadata; vector DB for embeddings

---

## Next Steps

1. Review and approve this implementation plan
2. Complete Story 6-2 (Vector Database) or implement mock store
3. Begin Phase 1 implementation
4. Set up CI pipeline for the intelligence package

---

**Last Updated**: 2026-02-05
**Author**: Implementation Plan Generator
**Reviewers**: TBD
