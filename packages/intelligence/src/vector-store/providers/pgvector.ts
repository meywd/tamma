/**
 * PostgreSQL pgvector Vector Store Adapter
 *
 * Implementation of IVectorStore for PostgreSQL with the pgvector extension.
 * Recommended for production deployments with existing PostgreSQL infrastructure.
 */

import type { Pool, PoolClient } from 'pg';
import type {
  VectorStoreConfig,
  PgVectorConfig,
  CollectionOptions,
  CollectionStats,
  VectorDocument,
  VectorMetadata,
  MetadataFilter,
  SearchQuery,
  HybridSearchQuery,
  MMRSearchQuery,
  SearchResult,
  DistanceMetric,
} from '../interfaces.js';
import { BaseVectorStore } from '../base-vector-store.js';
import {
  VectorStoreError,
  VectorStoreErrorCode,
  CollectionNotFoundError,
  CollectionExistsError,
  ConnectionError,
  InvalidConfigError,
} from '../errors.js';
import { toPgVectorFilter, isEmptyFilter } from '../utils/metadata-filter.js';
import {
  getPgVectorOperator,
  cosineDistanceToSimilarity,
  euclideanDistanceToSimilarity,
  cosineSimilarity,
} from '../utils/distance-metrics.js';

/**
 * Quote a SQL identifier (table name, column name, schema) by wrapping
 * it in double quotes and escaping any internal double quotes.
 * This prevents SQL injection through identifier names.
 */
function quoteIdentifier(identifier: string): string {
  return `"${identifier.replace(/"/g, '""')}"`;
}

/**
 * Validate and clamp a numeric value to a safe positive integer within a given range.
 * Throws if the value is not a finite number.
 */
function safePositiveInt(value: number, min: number, max: number, name: string): number {
  if (!Number.isFinite(value)) {
    throw new Error(`${name} must be a finite number, got: ${value}`);
  }
  const int = Math.floor(value);
  if (int < min) return min;
  if (int > max) return max;
  return int;
}

/**
 * PostgreSQL pgvector Vector Store implementation
 */
export class PgVectorStore extends BaseVectorStore {
  private pool: Pool | null = null;
  private readonly pgConfig: PgVectorConfig;
  private readonly schema: string;

  constructor(config: VectorStoreConfig) {
    super('pgvector', config);

    if (!config.pgvector) {
      throw new InvalidConfigError('pgvector configuration is required', 'pgvector');
    }

    this.pgConfig = config.pgvector;
    this.schema = config.pgvector.schema ?? 'public';
  }

  // === Lifecycle ===

  protected override async doInitialize(): Promise<void> {
    try {
      // Dynamic import to avoid loading pg if not used
      const pg = await import('pg');

      this.pool = new pg.default.Pool({
        connectionString: this.pgConfig.connectionString,
        max: this.pgConfig.poolSize ?? 10,
      });

      // Test connection and ensure pgvector extension is installed
      const client = await this.pool.connect();
      try {
        // Check if pgvector extension exists
        await client.query('CREATE EXTENSION IF NOT EXISTS vector');

        // Verify extension is working
        await client.query('SELECT vector_dims($1::vector)', [`[${Array(3).fill(0).join(',')}]`]);

        this.logger.info('pgvector connection established', {
          schema: this.schema,
          poolSize: this.pgConfig.poolSize ?? 10,
        });
      } finally {
        client.release();
      }
    } catch (error) {
      const cause = error instanceof Error ? error : undefined;
      throw new ConnectionError(
        `Failed to connect to PostgreSQL: ${cause?.message ?? 'Unknown error'}`,
        'pgvector',
        cause ? { cause } : undefined,
      );
    }
  }

  protected override async doDispose(): Promise<void> {
    if (this.pool) {
      await this.pool.end();
      this.pool = null;
    }
  }

  protected override async doHealthCheck(): Promise<Record<string, unknown>> {
    if (!this.pool) {
      throw new Error('Pool not initialized');
    }

    const result = await this.pool.query(`
      SELECT
        current_database() as database,
        current_schema() as schema,
        version() as version,
        (SELECT extversion FROM pg_extension WHERE extname = 'vector') as vector_version
    `);

    const row = result.rows[0] as Record<string, string> | undefined;

    return {
      database: row?.['database'],
      schema: row?.['schema'],
      version: row?.['version'],
      vectorExtensionVersion: row?.['vector_version'],
      poolTotalCount: this.pool.totalCount,
      poolIdleCount: this.pool.idleCount,
      poolWaitingCount: this.pool.waitingCount,
    };
  }

  // === Collection Management ===

  protected override async doCreateCollection(
    name: string,
    options?: CollectionOptions,
  ): Promise<void> {
    if (!this.pool) {
      throw new Error('Pool not initialized');
    }

    const tableName = this.getTableName(name);
    const rawDims = options?.dimensions ?? this.dimensions;
    const dims = safePositiveInt(rawDims, 1, 16000, 'dimensions');
    const metric = options?.distanceMetric ?? this.distanceMetric;

    // Check if table already exists
    const exists = await this.doCollectionExists(name);
    if (exists) {
      throw new CollectionExistsError(name, 'pgvector');
    }

    const client = await this.pool.connect();
    try {
      await client.query('BEGIN');

      // Create the table
      await client.query(`
        CREATE TABLE ${tableName} (
          id TEXT PRIMARY KEY,
          embedding vector(${dims}),
          content TEXT,
          metadata JSONB DEFAULT '{}',
          created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
          updated_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP
        )
      `);

      // Create HNSW index for vector similarity search
      const indexType = this.pgConfig.index?.type ?? 'hnsw';
      const operator = this.getIndexOperator(metric);

      if (indexType === 'hnsw') {
        const m = safePositiveInt(this.pgConfig.index?.m ?? 16, 2, 100, 'm');
        const efConstruction = safePositiveInt(
          this.pgConfig.index?.efConstruction ?? 64, 16, 500, 'efConstruction',
        );

        await client.query(`
          CREATE INDEX ON ${tableName}
          USING hnsw (embedding ${operator})
          WITH (m = ${m}, ef_construction = ${efConstruction})
        `);
      } else {
        // IVF Flat index
        await client.query(`
          CREATE INDEX ON ${tableName}
          USING ivfflat (embedding ${operator})
          WITH (lists = 100)
        `);
      }

      // Create index on metadata for common queries
      await client.query(`
        CREATE INDEX ON ${tableName} USING GIN (metadata)
      `);

      // Store collection metadata
      await this.storeCollectionMetadata(client, name, {
        dimensions: dims,
        distanceMetric: metric,
        indexType,
        ...(options?.metadata ?? {}),
      });

      await client.query('COMMIT');
    } catch (error) {
      await client.query('ROLLBACK');
      const cause = error instanceof Error ? error : undefined;
      throw new VectorStoreError(
        `Failed to create collection '${name}': ${cause?.message ?? 'Unknown error'}`,
        VectorStoreErrorCode.PROVIDER_ERROR,
        'pgvector',
        cause ? { cause } : undefined,
      );
    } finally {
      client.release();
    }
  }

  protected override async doDeleteCollection(name: string): Promise<void> {
    if (!this.pool) {
      throw new Error('Pool not initialized');
    }

    const exists = await this.doCollectionExists(name);
    if (!exists) {
      throw new CollectionNotFoundError(name, 'pgvector');
    }

    const tableName = this.getTableName(name);

    await this.pool.query(`DROP TABLE IF EXISTS ${tableName}`);
    await this.deleteCollectionMetadata(name);
  }

  protected override async doListCollections(): Promise<string[]> {
    if (!this.pool) {
      throw new Error('Pool not initialized');
    }

    // List tables that match our naming pattern
    const result = await this.pool.query(`
      SELECT table_name
      FROM information_schema.tables
      WHERE table_schema = $1
        AND table_name LIKE 'vector_%'
      ORDER BY table_name
    `, [this.schema]);

    return result.rows.map((row) => {
      const tableName = (row as Record<string, string>)['table_name'] ?? '';
      return tableName.replace(/^vector_/, '');
    });
  }

  protected override async doGetCollectionStats(name: string): Promise<CollectionStats> {
    if (!this.pool) {
      throw new Error('Pool not initialized');
    }

    const exists = await this.doCollectionExists(name);
    if (!exists) {
      throw new CollectionNotFoundError(name, 'pgvector');
    }

    const tableName = this.getTableName(name);

    // Get count and table size.
    // Use the sanitized collection name as a regclass literal so pg_total_relation_size
    // receives a proper schema-qualified identifier without double-quoting ambiguity.
    const sanitizedName = name.replace(/[^a-zA-Z0-9_]/g, '');
    const result = await this.pool.query(`
      SELECT
        (SELECT count(*) FROM ${tableName}) as count,
        pg_total_relation_size('"${this.schema}"."vector_${sanitizedName}"'::regclass) as size
    `);

    const row = result.rows[0] as Record<string, string | number> | undefined;
    const count = parseInt(String(row?.['count'] ?? '0'), 10);
    const size = parseInt(String(row?.['size'] ?? '0'), 10);

    // Get metadata
    const metadata = await this.getCollectionMetadata(name);

    const stats: CollectionStats = {
      name,
      documentCount: count,
      dimensions: (metadata?.['dimensions'] as number) ?? this.dimensions,
      indexSize: size,
    };

    if (metadata?.['createdAt']) {
      stats.createdAt = new Date(metadata['createdAt'] as string);
    }

    return stats;
  }

  protected override async doCollectionExists(name: string): Promise<boolean> {
    if (!this.pool) {
      throw new Error('Pool not initialized');
    }

    const result = await this.pool.query(`
      SELECT EXISTS (
        SELECT 1
        FROM information_schema.tables
        WHERE table_schema = $1
          AND table_name = $2
      ) as exists
    `, [this.schema, `vector_${name}`]);

    return (result.rows[0] as Record<string, boolean> | undefined)?.['exists'] ?? false;
  }

  // === Document Operations ===

  protected override async doUpsert(
    collection: string,
    documents: VectorDocument[],
  ): Promise<void> {
    if (!this.pool) {
      throw new Error('Pool not initialized');
    }

    const tableName = this.getTableName(collection);

    // Check collection exists
    const exists = await this.doCollectionExists(collection);
    if (!exists) {
      throw new CollectionNotFoundError(collection, 'pgvector');
    }

    const client = await this.pool.connect();
    try {
      await client.query('BEGIN');

      // Use batch insert with ON CONFLICT for upsert
      const batchSize = 100;
      for (let i = 0; i < documents.length; i += batchSize) {
        const batch = documents.slice(i, i + batchSize);
        await this.batchUpsert(client, tableName, batch);
      }

      await client.query('COMMIT');
    } catch (error) {
      await client.query('ROLLBACK');
      const cause = error instanceof Error ? error : undefined;
      throw new VectorStoreError(
        `Failed to upsert documents: ${cause?.message ?? 'Unknown error'}`,
        VectorStoreErrorCode.PROVIDER_ERROR,
        'pgvector',
        cause ? { cause } : undefined,
      );
    } finally {
      client.release();
    }
  }

  protected override async doDelete(collection: string, ids: string[]): Promise<void> {
    if (!this.pool) {
      throw new Error('Pool not initialized');
    }

    const tableName = this.getTableName(collection);

    // Delete in batches
    const batchSize = 100;
    for (let i = 0; i < ids.length; i += batchSize) {
      const batch = ids.slice(i, i + batchSize);
      const placeholders = batch.map((_, idx) => `$${idx + 1}`).join(', ');
      await this.pool.query(`DELETE FROM ${tableName} WHERE id IN (${placeholders})`, batch);
    }
  }

  protected override async doGet(collection: string, ids: string[]): Promise<VectorDocument[]> {
    if (!this.pool) {
      throw new Error('Pool not initialized');
    }

    const tableName = this.getTableName(collection);
    const placeholders = ids.map((_, idx) => `$${idx + 1}`).join(', ');

    const result = await this.pool.query(
      `SELECT id, embedding::text, content, metadata FROM ${tableName} WHERE id IN (${placeholders})`,
      ids,
    );

    return result.rows.map((row) => this.rowToDocument(row as Record<string, unknown>));
  }

  protected override async doCount(collection: string, filter?: MetadataFilter): Promise<number> {
    if (!this.pool) {
      throw new Error('Pool not initialized');
    }

    const tableName = this.getTableName(collection);

    let query = `SELECT count(*) as count FROM ${tableName}`;
    const params: unknown[] = [];

    if (filter && !isEmptyFilter(filter)) {
      const filterResult = toPgVectorFilter(filter, 1);
      if (filterResult.sql) {
        query += ` WHERE ${filterResult.sql}`;
        params.push(...filterResult.params);
      }
    }

    const result = await this.pool.query(query, params);
    return parseInt(String((result.rows[0] as Record<string, string>)?.['count'] ?? '0'), 10);
  }

  // === Search Operations ===

  protected override async doSearch(
    collection: string,
    query: SearchQuery,
  ): Promise<SearchResult[]> {
    if (!this.pool) {
      throw new Error('Pool not initialized');
    }

    const tableName = this.getTableName(collection);
    const operator = getPgVectorOperator(this.distanceMetric);
    const embeddingStr = `[${query.embedding.join(',')}]`;

    // Build SELECT clause
    const selectFields = ['id', `embedding ${operator} $1::vector as distance`];
    if (query.includeContent !== false) selectFields.push('content');
    if (query.includeMetadata !== false) selectFields.push('metadata');
    if (query.includeEmbedding) selectFields.push('embedding::text as embedding_text');

    // Build WHERE clause
    const params: unknown[] = [embeddingStr];
    let whereClause = '';

    if (query.filter && !isEmptyFilter(query.filter)) {
      const filterResult = toPgVectorFilter(query.filter, 2);
      if (filterResult.sql) {
        whereClause = `WHERE ${filterResult.sql}`;
        params.push(...filterResult.params);
      }
    }

    // Use parameterized LIMIT to prevent injection via topK
    const topK = safePositiveInt(query.topK, 1, 10000, 'topK');
    const limitParamIdx = params.length + 1;
    params.push(topK);

    const sql = `
      SELECT ${selectFields.join(', ')}
      FROM ${tableName}
      ${whereClause}
      ORDER BY embedding ${operator} $1::vector
      LIMIT $${limitParamIdx}
    `;

    const result = await this.pool.query(sql, params);

    return result.rows
      .map((row) => this.rowToSearchResult(row as Record<string, unknown>, query))
      .filter((r) => query.scoreThreshold === undefined || r.score >= query.scoreThreshold);
  }

  protected override async doHybridSearch(
    collection: string,
    query: HybridSearchQuery,
  ): Promise<SearchResult[]> {
    if (!this.pool) {
      throw new Error('Pool not initialized');
    }

    const tableName = this.getTableName(collection);
    const operator = getPgVectorOperator(this.distanceMetric);
    const embeddingStr = `[${query.embedding.join(',')}]`;
    const alpha = Math.max(0, Math.min(1, query.alpha ?? 0.5));
    const topK = safePositiveInt(query.topK, 1, 10000, 'topK');
    const fetchLimit = topK * 2;

    // Build WHERE clause
    const params: unknown[] = [embeddingStr, query.text];
    let whereClause = '';

    if (query.filter && !isEmptyFilter(query.filter)) {
      const filterResult = toPgVectorFilter(query.filter, 3);
      if (filterResult.sql) {
        whereClause = `WHERE ${filterResult.sql}`;
        params.push(...filterResult.params);
      }
    }

    // Hybrid query using RRF-style combination
    // Note: topK/fetchLimit/alpha are validated integers/floats above, safe for interpolation
    const sql = `
      WITH vector_results AS (
        SELECT id, embedding ${operator} $1::vector as distance,
               ROW_NUMBER() OVER (ORDER BY embedding ${operator} $1::vector) as vector_rank
        FROM ${tableName}
        ${whereClause}
        ORDER BY embedding ${operator} $1::vector
        LIMIT ${fetchLimit}
      ),
      text_results AS (
        SELECT id,
               ts_rank_cd(to_tsvector('english', content), plainto_tsquery('english', $2)) as text_score,
               ROW_NUMBER() OVER (ORDER BY ts_rank_cd(to_tsvector('english', content), plainto_tsquery('english', $2)) DESC) as text_rank
        FROM ${tableName}
        ${whereClause ? `${whereClause} AND to_tsvector('english', content) @@ plainto_tsquery('english', $2)` : `WHERE to_tsvector('english', content) @@ plainto_tsquery('english', $2)`}
        LIMIT ${fetchLimit}
      ),
      combined AS (
        SELECT
          COALESCE(v.id, t.id) as id,
          ${alpha} / (60 + COALESCE(v.vector_rank, ${fetchLimit})) +
          ${1 - alpha} / (60 + COALESCE(t.text_rank, ${fetchLimit})) as combined_score
        FROM vector_results v
        FULL OUTER JOIN text_results t ON v.id = t.id
      )
      SELECT
        c.id,
        c.combined_score as score
        ${query.includeContent !== false ? ', d.content' : ''}
        ${query.includeMetadata !== false ? ', d.metadata' : ''}
        ${query.includeEmbedding ? ', d.embedding::text as embedding_text' : ''}
      FROM combined c
      JOIN ${tableName} d ON c.id = d.id
      ORDER BY c.combined_score DESC
      LIMIT ${topK}
    `;

    const result = await this.pool.query(sql, params);

    const results: SearchResult[] = [];
    for (const row of result.rows) {
      const r = row as Record<string, unknown>;
      const resultItem: SearchResult = {
        id: r['id'] as string,
        score: r['score'] as number,
      };

      if (query.includeContent !== false && r['content'] !== undefined) {
        resultItem.content = r['content'] as string;
      }

      if (query.includeMetadata !== false) {
        const metadata = r['metadata'];
        if (metadata !== undefined && metadata !== null) {
          resultItem.metadata = metadata as VectorMetadata;
        }
      }

      if (query.includeEmbedding && r['embedding_text'] !== undefined) {
        resultItem.embedding = this.parseEmbedding(r['embedding_text']);
      }

      if (query.scoreThreshold === undefined || resultItem.score >= query.scoreThreshold) {
        results.push(resultItem);
      }
    }

    return results;
  }

  protected override async doMMRSearch(
    collection: string,
    query: MMRSearchQuery,
  ): Promise<SearchResult[]> {
    if (!this.pool) {
      throw new Error('Pool not initialized');
    }

    const tableName = this.getTableName(collection);
    const operator = getPgVectorOperator(this.distanceMetric);
    const embeddingStr = `[${query.embedding.join(',')}]`;
    const lambda = Math.max(0, Math.min(1, query.lambda ?? 0.5));
    const topK = safePositiveInt(query.topK, 1, 10000, 'topK');
    const fetchK = safePositiveInt(query.fetchK ?? topK * 4, 1, 40000, 'fetchK');

    // Step 1: Fetch candidates with embeddings
    const params: unknown[] = [embeddingStr];
    let whereClause = '';

    if (query.filter && !isEmptyFilter(query.filter)) {
      const filterResult = toPgVectorFilter(query.filter, 2);
      if (filterResult.sql) {
        whereClause = `WHERE ${filterResult.sql}`;
        params.push(...filterResult.params);
      }
    }

    // Use parameterized LIMIT for fetchK
    const limitParamIdx = params.length + 1;
    params.push(fetchK);

    const sql = `
      SELECT id, embedding ${operator} $1::vector as distance, content, metadata, embedding::text as embedding_text
      FROM ${tableName}
      ${whereClause}
      ORDER BY embedding ${operator} $1::vector
      LIMIT $${limitParamIdx}
    `;

    const result = await this.pool.query(sql, params);

    if (result.rows.length === 0) {
      return [];
    }

    // Extract candidates
    const candidates: Array<{
      id: string;
      embedding: number[];
      distance: number;
      content?: string;
      metadata?: VectorMetadata;
    }> = result.rows.map((row) => {
      const r = row as Record<string, unknown>;
      return {
        id: r['id'] as string,
        embedding: this.parseEmbedding(r['embedding_text']),
        distance: r['distance'] as number,
        content: r['content'] as string | undefined,
        metadata: r['metadata'] as VectorMetadata | undefined,
      };
    });

    // Step 2: Apply MMR algorithm
    const selected: SearchResult[] = [];
    const selectedEmbeddings: number[][] = [];
    const remainingIndices = new Set(candidates.map((_, i) => i));

    while (selected.length < topK && remainingIndices.size > 0) {
      let bestIdx = -1;
      let bestMMRScore = -Infinity;

      for (const idx of remainingIndices) {
        const candidate = candidates[idx];
        if (!candidate) continue;

        // Relevance score (convert distance to similarity)
        const relevance = this.distanceToScore(candidate.distance);

        // Diversity score: max similarity to already selected items
        let maxSimilarityToSelected = 0;
        for (const selectedEmb of selectedEmbeddings) {
          const sim = cosineSimilarity(candidate.embedding, selectedEmb);
          maxSimilarityToSelected = Math.max(maxSimilarityToSelected, sim);
        }

        // MMR score: lambda * relevance - (1 - lambda) * maxSimilarity
        const mmrScore = lambda * relevance - (1 - lambda) * maxSimilarityToSelected;

        if (mmrScore > bestMMRScore) {
          bestMMRScore = mmrScore;
          bestIdx = idx;
        }
      }

      if (bestIdx === -1) break;

      const bestCandidate = candidates[bestIdx];
      if (!bestCandidate) break;

      // Apply score threshold
      const score = this.distanceToScore(bestCandidate.distance);
      if (query.scoreThreshold !== undefined && score < query.scoreThreshold) {
        remainingIndices.delete(bestIdx);
        continue;
      }

      const resultItem: SearchResult = {
        id: bestCandidate.id,
        score,
      };

      if (query.includeContent !== false && bestCandidate.content !== undefined) {
        resultItem.content = bestCandidate.content;
      }

      if (query.includeMetadata !== false && bestCandidate.metadata !== undefined) {
        resultItem.metadata = bestCandidate.metadata;
      }

      if (query.includeEmbedding) {
        resultItem.embedding = bestCandidate.embedding;
      }

      selected.push(resultItem);
      selectedEmbeddings.push(bestCandidate.embedding);
      remainingIndices.delete(bestIdx);
    }

    return selected;
  }

  // === Maintenance ===

  protected override async doOptimize(collection: string): Promise<void> {
    if (!this.pool) {
      throw new Error('Pool not initialized');
    }

    const tableName = this.getTableName(collection);

    // Reindex to optimize search performance
    await this.pool.query(`REINDEX TABLE ${tableName}`);

    // Update statistics
    await this.pool.query(`ANALYZE ${tableName}`);
  }

  protected override async doVacuum(collection: string): Promise<void> {
    if (!this.pool) {
      throw new Error('Pool not initialized');
    }

    const tableName = this.getTableName(collection);

    // VACUUM cannot run inside a transaction, so we need a direct connection
    const client = await this.pool.connect();
    try {
      await client.query(`VACUUM ANALYZE ${tableName}`);
    } finally {
      client.release();
    }
  }

  // === Private Helpers ===

  /**
   * Get the fully qualified table name for a collection.
   * Uses quoteIdentifier() to safely escape identifiers and prevent SQL injection.
   */
  private getTableName(collection: string): string {
    // Sanitize collection name: strip non-alphanumeric/underscore chars
    const sanitized = collection.replace(/[^a-zA-Z0-9_]/g, '');
    return `${quoteIdentifier(this.schema)}.${quoteIdentifier(`vector_${sanitized}`)}`;
  }

  /**
   * Get the pgvector index operator class for a distance metric
   */
  private getIndexOperator(metric: DistanceMetric): string {
    switch (metric) {
      case 'cosine':
        return 'vector_cosine_ops';
      case 'euclidean':
        return 'vector_l2_ops';
      case 'dot_product':
        return 'vector_ip_ops';
      default:
        return 'vector_cosine_ops';
    }
  }

  /**
   * Perform batch upsert
   */
  private async batchUpsert(
    client: PoolClient,
    tableName: string,
    documents: VectorDocument[],
  ): Promise<void> {
    if (documents.length === 0) return;

    const values: unknown[] = [];
    const placeholders: string[] = [];

    for (let i = 0; i < documents.length; i++) {
      const doc = documents[i];
      if (!doc) continue;

      const offset = i * 4;
      placeholders.push(`($${offset + 1}, $${offset + 2}::vector, $${offset + 3}, $${offset + 4}::jsonb)`);
      values.push(
        doc.id,
        `[${doc.embedding.join(',')}]`,
        doc.content,
        JSON.stringify(doc.metadata),
      );
    }

    await client.query(
      `
      INSERT INTO ${tableName} (id, embedding, content, metadata)
      VALUES ${placeholders.join(', ')}
      ON CONFLICT (id) DO UPDATE SET
        embedding = EXCLUDED.embedding,
        content = EXCLUDED.content,
        metadata = EXCLUDED.metadata,
        updated_at = CURRENT_TIMESTAMP
    `,
      values,
    );
  }

  /**
   * Convert a database row to a VectorDocument
   */
  private rowToDocument(row: Record<string, unknown>): VectorDocument {
    return {
      id: row['id'] as string,
      embedding: this.parseEmbedding(row['embedding']),
      content: (row['content'] as string) ?? '',
      metadata: (row['metadata'] as VectorDocument['metadata']) ?? {},
    };
  }

  /**
   * Convert a database row to a SearchResult
   */
  private rowToSearchResult(row: Record<string, unknown>, query: SearchQuery): SearchResult {
    const distance = row['distance'] as number;
    const score = this.distanceToScore(distance);

    const result: SearchResult = {
      id: row['id'] as string,
      score,
    };

    if (query.includeContent !== false && row['content'] !== undefined) {
      result.content = row['content'] as string;
    }

    if (query.includeMetadata !== false) {
      const metadata = row['metadata'];
      if (metadata !== undefined && metadata !== null) {
        result.metadata = metadata as VectorMetadata;
      }
    }

    if (query.includeEmbedding && row['embedding_text'] !== undefined) {
      result.embedding = this.parseEmbedding(row['embedding_text']);
    }

    return result;
  }

  /**
   * Parse a pgvector embedding string to array
   */
  private parseEmbedding(value: unknown): number[] {
    if (Array.isArray(value)) {
      return value as number[];
    }
    if (typeof value === 'string') {
      // pgvector format: "[0.1,0.2,0.3]"
      const cleaned = value.replace(/[\[\]]/g, '');
      return cleaned.split(',').map(Number);
    }
    return [];
  }

  /**
   * Convert pgvector distance to similarity score (0-1)
   */
  private distanceToScore(distance: number): number {
    switch (this.distanceMetric) {
      case 'cosine':
        // pgvector cosine returns distance (0-2), convert to similarity
        return Math.max(0, Math.min(1, (cosineDistanceToSimilarity(distance) + 1) / 2));
      case 'euclidean':
        return euclideanDistanceToSimilarity(distance);
      case 'dot_product':
        // Negative inner product, negate for similarity
        return Math.max(0, Math.min(1, -distance));
      default:
        return Math.max(0, Math.min(1, 1 - distance));
    }
  }

  /**
   * Store collection metadata in a separate metadata table
   */
  private async storeCollectionMetadata(
    client: PoolClient,
    name: string,
    metadata: Record<string, unknown>,
  ): Promise<void> {
    // Create metadata table if not exists
    await client.query(`
      CREATE TABLE IF NOT EXISTS ${quoteIdentifier(this.schema)}.${quoteIdentifier('vector_collections_metadata')} (
        name TEXT PRIMARY KEY,
        metadata JSONB DEFAULT '{}',
        created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP
      )
    `);

    await client.query(
      `
      INSERT INTO ${quoteIdentifier(this.schema)}.${quoteIdentifier('vector_collections_metadata')} (name, metadata)
      VALUES ($1, $2::jsonb)
      ON CONFLICT (name) DO UPDATE SET metadata = $2::jsonb
    `,
      [name, JSON.stringify({ ...metadata, createdAt: new Date().toISOString() })],
    );
  }

  /**
   * Get collection metadata
   */
  private async getCollectionMetadata(name: string): Promise<Record<string, unknown> | null> {
    if (!this.pool) return null;

    try {
      const result = await this.pool.query(
        `SELECT metadata FROM ${quoteIdentifier(this.schema)}.${quoteIdentifier('vector_collections_metadata')} WHERE name = $1`,
        [name],
      );
      return (result.rows[0] as Record<string, Record<string, unknown>> | undefined)?.['metadata'] ?? null;
    } catch {
      return null;
    }
  }

  /**
   * Delete collection metadata
   */
  private async deleteCollectionMetadata(name: string): Promise<void> {
    if (!this.pool) return;

    try {
      await this.pool.query(
        `DELETE FROM ${quoteIdentifier(this.schema)}.${quoteIdentifier('vector_collections_metadata')} WHERE name = $1`,
        [name],
      );
    } catch {
      // Ignore errors - metadata table might not exist
    }
  }
}
