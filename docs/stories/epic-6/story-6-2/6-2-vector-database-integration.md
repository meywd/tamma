# Story 6-2: Vector Database Integration

## User Story

As a **Tamma engine**, I need to store and query code embeddings in a vector database so that I can perform fast semantic search across the codebase.

## Description

Implement a vector database abstraction layer that supports multiple backends (ChromaDB, pgvector, Pinecone, Qdrant, Weaviate) and provides efficient similarity search for code retrieval.

## Acceptance Criteria

### AC1: Vector Store Interface
- [ ] Define `IVectorStore` interface for all operations
- [ ] Support CRUD operations for vectors
- [ ] Support batch operations for efficiency
- [ ] Support metadata filtering
- [ ] Support hybrid search (vector + keyword)

### AC2: ChromaDB Integration (Default)
- [ ] Implement ChromaDB adapter (embedded mode)
- [ ] Support persistent storage
- [ ] Support collection management (create, delete, list)
- [ ] Handle connection pooling

### AC3: pgvector Integration
- [ ] Implement pgvector adapter for PostgreSQL
- [ ] Support HNSW index for fast search
- [ ] Integrate with existing PostgreSQL infrastructure
- [ ] Support transactions

### AC4: Cloud Provider Support (Future)
- [ ] Pinecone adapter implementation
- [ ] Qdrant adapter implementation
- [ ] Weaviate adapter implementation

### AC5: Query Capabilities
- [ ] Similarity search (k-NN)
- [ ] Similarity with score threshold
- [ ] Metadata filtering (file path, language, type)
- [ ] Hybrid search (vector + BM25 keyword)
- [ ] Max marginal relevance (MMR) for diversity

### AC6: Performance Optimization
- [ ] Index configuration for optimal performance
- [ ] Query result caching
- [ ] Batch insert optimization
- [ ] Connection pooling

### AC7: Monitoring & Maintenance
- [ ] Track query latency metrics
- [ ] Monitor index size and memory usage
- [ ] Support index compaction/optimization
- [ ] Health check endpoint

## Technical Design

### Vector Store Interface

```typescript
interface IVectorStore {
  // Lifecycle
  initialize(config: VectorStoreConfig): Promise<void>;
  dispose(): Promise<void>;
  healthCheck(): Promise<HealthStatus>;

  // Collection management
  createCollection(name: string, options?: CollectionOptions): Promise<void>;
  deleteCollection(name: string): Promise<void>;
  listCollections(): Promise<string[]>;
  getCollectionStats(name: string): Promise<CollectionStats>;

  // Vector operations
  upsert(collection: string, documents: VectorDocument[]): Promise<void>;
  delete(collection: string, ids: string[]): Promise<void>;
  get(collection: string, ids: string[]): Promise<VectorDocument[]>;

  // Search
  search(collection: string, query: SearchQuery): Promise<SearchResult[]>;
  hybridSearch(collection: string, query: HybridSearchQuery): Promise<SearchResult[]>;
}

interface VectorDocument {
  id: string;
  embedding: number[];
  content: string;
  metadata: Record<string, unknown>;
}

interface SearchQuery {
  embedding: number[];
  topK: number;
  scoreThreshold?: number;
  filter?: MetadataFilter;
  includeMetadata?: boolean;
  includeContent?: boolean;
}

interface HybridSearchQuery extends SearchQuery {
  text: string;                  // For keyword search
  alpha?: number;                // Weight: 0 = keyword only, 1 = vector only
}

interface SearchResult {
  id: string;
  score: number;
  content?: string;
  metadata?: Record<string, unknown>;
}

interface MetadataFilter {
  where?: Record<string, unknown>;    // Exact match
  whereIn?: Record<string, unknown[]>; // In list
  whereGt?: Record<string, number>;   // Greater than
  whereLt?: Record<string, number>;   // Less than
}
```

### ChromaDB Adapter

```typescript
class ChromaDBVectorStore implements IVectorStore {
  private client: ChromaClient;
  private collections: Map<string, Collection>;

  async initialize(config: VectorStoreConfig): Promise<void> {
    this.client = new ChromaClient({
      path: config.persistPath ?? './chroma_db',
    });
  }

  async search(collection: string, query: SearchQuery): Promise<SearchResult[]> {
    const coll = await this.getCollection(collection);

    const results = await coll.query({
      queryEmbeddings: [query.embedding],
      nResults: query.topK,
      where: query.filter?.where,
      include: ['documents', 'metadatas', 'distances'],
    });

    return results.ids[0].map((id, i) => ({
      id,
      score: 1 - (results.distances?.[0]?.[i] ?? 0), // Convert distance to similarity
      content: results.documents?.[0]?.[i],
      metadata: results.metadatas?.[0]?.[i],
    }));
  }
}
```

### pgvector Adapter

```typescript
class PgVectorStore implements IVectorStore {
  private pool: Pool;

  async initialize(config: VectorStoreConfig): Promise<void> {
    this.pool = new Pool({ connectionString: config.connectionString });

    // Ensure pgvector extension
    await this.pool.query('CREATE EXTENSION IF NOT EXISTS vector');
  }

  async createCollection(name: string, options?: CollectionOptions): Promise<void> {
    const dimensions = options?.dimensions ?? 1536;

    await this.pool.query(`
      CREATE TABLE IF NOT EXISTS ${name} (
        id TEXT PRIMARY KEY,
        embedding vector(${dimensions}),
        content TEXT,
        metadata JSONB,
        created_at TIMESTAMP DEFAULT NOW()
      )
    `);

    // Create HNSW index for fast search
    await this.pool.query(`
      CREATE INDEX IF NOT EXISTS ${name}_embedding_idx
      ON ${name}
      USING hnsw (embedding vector_cosine_ops)
      WITH (m = 16, ef_construction = 64)
    `);
  }

  async search(collection: string, query: SearchQuery): Promise<SearchResult[]> {
    const embeddingStr = `[${query.embedding.join(',')}]`;

    let sql = `
      SELECT id, content, metadata,
             1 - (embedding <=> $1::vector) as score
      FROM ${collection}
    `;

    const params: unknown[] = [embeddingStr];
    let paramIndex = 2;

    // Add metadata filters
    if (query.filter?.where) {
      const conditions = Object.entries(query.filter.where)
        .map(([key, value]) => {
          params.push(value);
          return `metadata->>'${key}' = $${paramIndex++}`;
        });
      sql += ` WHERE ${conditions.join(' AND ')}`;
    }

    // Add score threshold
    if (query.scoreThreshold) {
      const whereOrAnd = query.filter?.where ? 'AND' : 'WHERE';
      params.push(query.scoreThreshold);
      sql += ` ${whereOrAnd} 1 - (embedding <=> $1::vector) >= $${paramIndex++}`;
    }

    sql += ` ORDER BY embedding <=> $1::vector LIMIT $${paramIndex}`;
    params.push(query.topK);

    const result = await this.pool.query(sql, params);

    return result.rows.map(row => ({
      id: row.id,
      score: row.score,
      content: query.includeContent ? row.content : undefined,
      metadata: query.includeMetadata ? row.metadata : undefined,
    }));
  }
}
```

## Dependencies

- Story 6-1: Codebase Indexer (produces embeddings)
- ChromaDB library
- pg + pgvector for PostgreSQL option
- Optional: Pinecone, Qdrant, Weaviate SDKs

## Testing Strategy

### Unit Tests
- CRUD operations for each adapter
- Metadata filtering logic
- Score calculation accuracy
- Error handling

### Integration Tests
- End-to-end search flow
- Large dataset performance (100k+ vectors)
- Concurrent access patterns
- Recovery after restart

### Performance Benchmarks
- Insert throughput: > 1000 vectors/second
- Search latency: < 50ms for 100k vectors
- Memory usage bounds

## Configuration

```yaml
vector_store:
  provider: chromadb  # chromadb | pgvector | pinecone | qdrant

  chromadb:
    persist_path: ./data/chroma
    anonymized_telemetry: false

  pgvector:
    connection_string: ${DATABASE_URL}
    pool_size: 10

  pinecone:
    api_key: ${PINECONE_API_KEY}
    environment: us-east-1
    index_name: tamma-codebase

  settings:
    default_collection: codebase
    dimensions: 1536
    distance_metric: cosine  # cosine | euclidean | dot_product
```

## Success Metrics

- Search latency p95 < 50ms
- Insert throughput > 1000 docs/second
- Query accuracy (recall@10) > 90%
- Uptime > 99.9%

## Logging Requirements

All intelligence modules MUST accept `ILogger` from `@tamma/shared/contracts` via constructor injection.

- **INFO**: Index scan started/completed (file count, duration), vector search executed (query, result count), RAG pipeline invoked, knowledge base query matched
- **DEBUG**: Chunk boundaries, embedding dimensions, similarity scores, cache hit/miss, budget allocation
- **WARN**: Embedding API rate limited, vector store connection degraded, stale index detected, budget threshold approaching
- **ERROR**: Embedding generation failed, vector store unreachable, index corruption detected, MCP tool call failed
- **Structured context**: Always include `{ repository, indexId, queryId, sourceType }` where applicable
- **Performance**: Log duration for all external API calls (embedding, vector search, MCP tool invocations)
- **Cost tracking**: Log token counts for embedding operations to support LLM cost monitoring (Story 6-7)
