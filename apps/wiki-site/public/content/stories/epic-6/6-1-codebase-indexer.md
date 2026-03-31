---
title: "Story 6-1: Codebase Indexer Implementation"
sidebar:
  order: 60
---

## User Story

As a **Tamma engine**, I need to index the codebase into searchable chunks so that agents can find relevant code through semantic search.

## Description

Implement a codebase indexer that processes source files, chunks them intelligently based on code structure (functions, classes, modules), generates embeddings, and stores them in a vector database for semantic search.

## Acceptance Criteria

### AC1: File Discovery
- [ ] Recursively scan project directories for source files
- [ ] Support configurable include/exclude patterns (glob)
- [ ] Respect .gitignore patterns
- [ ] Support multiple languages: TypeScript, JavaScript, Python, Go, Rust, Java

### AC2: Intelligent Chunking
- [ ] Parse code using language-specific AST parsers
- [ ] Chunk by semantic units:
  - Functions/methods
  - Classes/interfaces
  - Module-level code blocks
  - Import statements (grouped)
- [ ] Preserve context (file path, line numbers, parent scope)
- [ ] Handle large files (>1000 lines) gracefully
- [ ] Configurable max chunk size (default: 512 tokens)

### AC3: Metadata Extraction
- [ ] Extract symbols (function names, class names, exports)
- [ ] Extract imports/dependencies
- [ ] Extract JSDoc/docstrings
- [ ] Track file modification timestamps
- [ ] Calculate file hashes for change detection

### AC4: Embedding Generation
- [ ] Support multiple embedding providers:
  - OpenAI text-embedding-3-small (default)
  - Cohere embed-english-v3.0
  - Local models via Ollama
- [ ] Batch embedding requests for efficiency
- [ ] Handle rate limits with exponential backoff
- [ ] Cache embeddings to avoid re-computation

### AC5: Incremental Updates
- [ ] Detect changed files via git diff or file hash
- [ ] Re-index only modified files
- [ ] Remove deleted file chunks from index
- [ ] Support full re-index on demand

### AC6: Index Triggers
- [ ] Git hook integration (post-commit, post-merge)
- [ ] File system watcher (development mode)
- [ ] Scheduled re-index (configurable interval)
- [ ] Manual trigger via CLI/API

### AC7: Progress & Monitoring
- [ ] Report indexing progress (files processed, chunks created)
- [ ] Log errors without stopping entire index
- [ ] Emit metrics (indexing duration, chunk count, embedding cost)
- [ ] Store index metadata (last run, file count, total chunks)

## Technical Design

### Chunking Pipeline

```typescript
interface CodeChunk {
  id: string;                    // Unique chunk ID
  fileId: string;                // Parent file ID
  filePath: string;              // Relative file path
  language: string;              // Programming language
  chunkType: 'function' | 'class' | 'module' | 'block';
  name: string;                  // Symbol name (if applicable)
  content: string;               // Raw code content
  startLine: number;
  endLine: number;
  parentScope?: string;          // Parent class/module name
  imports: string[];             // Dependencies
  exports: string[];             // Exported symbols
  docstring?: string;            // Documentation
  tokenCount: number;            // Estimated tokens
  hash: string;                  // Content hash for change detection
}

interface IndexedChunk extends CodeChunk {
  embedding: number[];           // Vector embedding
  indexedAt: Date;
}
```

### Chunking Strategies

```typescript
interface ChunkingStrategy {
  language: string;
  parser: 'tree-sitter' | 'babel' | 'typescript' | 'custom';
  maxChunkTokens: number;
  overlapTokens: number;         // Overlap between chunks for context
  preserveImports: boolean;
  groupRelatedCode: boolean;     // Group small related functions
}

const defaultStrategies: Record<string, ChunkingStrategy> = {
  typescript: {
    language: 'typescript',
    parser: 'typescript',
    maxChunkTokens: 512,
    overlapTokens: 50,
    preserveImports: true,
    groupRelatedCode: true,
  },
  python: {
    language: 'python',
    parser: 'tree-sitter',
    maxChunkTokens: 512,
    overlapTokens: 50,
    preserveImports: true,
    groupRelatedCode: true,
  },
};
```

### Indexer Service

```typescript
interface ICodebaseIndexer {
  // Configuration
  configure(config: IndexerConfig): Promise<void>;

  // Full indexing
  indexProject(projectPath: string): Promise<IndexResult>;

  // Incremental update
  updateIndex(projectPath: string, changedFiles?: string[]): Promise<IndexResult>;

  // Remove from index
  removeFromIndex(filePaths: string[]): Promise<void>;

  // Status
  getIndexStatus(projectPath: string): Promise<IndexStatus>;

  // Events
  on(event: 'progress', handler: (progress: IndexProgress) => void): void;
  on(event: 'error', handler: (error: IndexError) => void): void;
}

interface IndexResult {
  success: boolean;
  filesProcessed: number;
  chunksCreated: number;
  chunksUpdated: number;
  chunksDeleted: number;
  embeddingCost: number;
  durationMs: number;
  errors: IndexError[];
}
```

## Dependencies

- Story 6-2: Vector Database Integration (storage backend)
- Tree-sitter or language-specific parsers for AST
- Embedding provider (OpenAI, Cohere, or local)

## Testing Strategy

### Unit Tests
- Chunking logic for each supported language
- Metadata extraction accuracy
- Change detection (hash comparison)
- Error handling for malformed files

### Integration Tests
- Full project indexing end-to-end
- Incremental update scenarios
- Git hook integration
- Embedding provider integration

### Performance Tests
- Index 10,000+ files in < 5 minutes
- Incremental update for 10 files in < 10 seconds
- Memory usage stays bounded during large index

## Configuration

```yaml
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

  chunking:
    max_tokens: 512
    overlap_tokens: 50
    preserve_imports: true

  embedding:
    provider: openai
    model: text-embedding-3-small
    batch_size: 100

  triggers:
    git_hooks: true
    watch_mode: false
    schedule: "0 2 * * *"  # 2 AM daily
```

## Success Metrics

- Indexing throughput: > 100 files/second
- Chunk quality: relevant code in 90%+ of chunks
- Change detection accuracy: 100%
- Embedding cost: < $0.01 per 1000 files

## Logging Requirements

All intelligence modules MUST accept `ILogger` from `@tamma/shared/contracts` via constructor injection.

- **INFO**: Index scan started/completed (file count, duration), vector search executed (query, result count), RAG pipeline invoked, knowledge base query matched
- **DEBUG**: Chunk boundaries, embedding dimensions, similarity scores, cache hit/miss, budget allocation
- **WARN**: Embedding API rate limited, vector store connection degraded, stale index detected, budget threshold approaching
- **ERROR**: Embedding generation failed, vector store unreachable, index corruption detected, MCP tool call failed
- **Structured context**: Always include `{ repository, indexId, queryId, sourceType }` where applicable
- **Performance**: Log duration for all external API calls (embedding, vector search, MCP tool invocations)
- **Cost tracking**: Log token counts for embedding operations to support LLM cost monitoring (Story 6-7)
