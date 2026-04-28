/**
 * ChromaDB Vector Store Adapter
 *
 * Implementation of IVectorStore for ChromaDB, supporting both embedded
 * and client modes. ChromaDB is the default provider for local development.
 */

import type {
  ChromaClient as ChromaClientType,
  Collection as ChromaCollection,
  IncludeEnum,
} from 'chromadb';
import type {
  VectorStoreConfig,
  ChromaDBConfig,
  CollectionOptions,
  CollectionStats,
  VectorDocument,
  VectorMetadata,
  MetadataFilter,
  SearchQuery,
  HybridSearchQuery,
  MMRSearchQuery,
  SearchResult,
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
import { toChromaDBFilter, isEmptyFilter } from '../utils/metadata-filter.js';
import {
  getChromaDBDistanceFunction,
  cosineDistanceToSimilarity,
  euclideanDistanceToSimilarity,
  cosineSimilarity,
} from '../utils/distance-metrics.js';

/**
 * ChromaDB Vector Store implementation
 */
export class ChromaDBVectorStore extends BaseVectorStore {
  private client: ChromaClientType | null = null;
  private readonly chromaConfig: ChromaDBConfig;
  private readonly collections: Map<string, ChromaCollection> = new Map();

  constructor(config: VectorStoreConfig) {
    super('chromadb', config);

    if (!config.chromadb) {
      throw new InvalidConfigError('ChromaDB configuration is required', 'chromadb');
    }

    this.chromaConfig = config.chromadb;
  }

  // === Lifecycle ===

  protected override async doInitialize(): Promise<void> {
    try {
      // Dynamic import to avoid loading chromadb if not used
      const chromadb = await import('chromadb');

      if (this.chromaConfig.host) {
        // Client mode - connect to remote ChromaDB server
        this.client = new chromadb.ChromaClient({
          host: this.chromaConfig.host,
          port: this.chromaConfig.port ?? 8000,
          ssl: false,
        });
      } else {
        // chromadb v3 dropped embedded persistence — only the HTTP/server
        // mode remains. We still accept persistPath as a URL or
        // host:port string so existing callers (e.g. integration tests
        // that pass CHROMADB_TEST_URL through createChromaDBStore) keep
        // working.
        const parsed = this._parseEndpoint(this.chromaConfig.persistPath);
        this.client = new chromadb.ChromaClient({
          host: parsed.host,
          port: parsed.port,
          ssl: parsed.ssl,
        });
      }

      // Test connection
      await this.client.heartbeat();

      this.logger.info('ChromaDB client initialized', {
        mode: this.chromaConfig.host ? 'client' : 'embedded',
        persistPath: this.chromaConfig.persistPath,
      });
    } catch (error) {
      const cause = error instanceof Error ? error : undefined;
      throw new ConnectionError(
        `Failed to connect to ChromaDB: ${cause?.message ?? 'Unknown error'}`,
        'chromadb',
        cause ? { cause } : undefined,
      );
    }
  }

  protected override async doDispose(): Promise<void> {
    this.collections.clear();
    this.client = null;
  }

  protected override async doHealthCheck(): Promise<Record<string, unknown>> {
    if (!this.client) {
      throw new Error('Client not initialized');
    }

    const heartbeat = await this.client.heartbeat();
    const version = await this.client.version();

    return {
      heartbeat,
      version,
      collectionsLoaded: this.collections.size,
    };
  }

  // === Collection Management ===

  protected override async doCreateCollection(
    name: string,
    options?: CollectionOptions,
  ): Promise<void> {
    if (!this.client) {
      throw new Error('Client not initialized');
    }

    // Check if collection already exists
    const exists = await this.doCollectionExists(name);
    if (exists) {
      throw new CollectionExistsError(name, 'chromadb');
    }

    try {
      const distanceFunction = getChromaDBDistanceFunction(
        options?.distanceMetric ?? this.distanceMetric,
      );

      // chromadb v3 moved index-level config (hnsw:space) out of metadata
      // and into a dedicated `configuration.hnsw` object. Free-form fields
      // such as `dimensions` and caller-supplied metadata still live on
      // the collection metadata for downstream stats consumption.
      const collection = await this.client.createCollection({
        name,
        configuration: {
          hnsw: {
            space: distanceFunction,
          },
        },
        metadata: {
          dimensions: options?.dimensions ?? this.dimensions,
          ...options?.metadata,
        },
        // We always upsert pre-computed embeddings, so there is no need
        // for chromadb to instantiate its (now optional, separately
        // installed) DefaultEmbeddingFunction.
        embeddingFunction: null,
      });

      this.collections.set(name, collection);
    } catch (error) {
      const cause = error instanceof Error ? error : undefined;
      throw new VectorStoreError(
        `Failed to create collection '${name}': ${cause?.message ?? 'Unknown error'}`,
        VectorStoreErrorCode.PROVIDER_ERROR,
        'chromadb',
        cause ? { cause } : undefined,
      );
    }
  }

  protected override async doDeleteCollection(name: string): Promise<void> {
    if (!this.client) {
      throw new Error('Client not initialized');
    }

    try {
      await this.client.deleteCollection({ name });
      this.collections.delete(name);
    } catch (error) {
      const message = error instanceof Error ? error.message : '';
      if (message.includes('does not exist') || message.includes('not found')) {
        throw new CollectionNotFoundError(name, 'chromadb');
      }
      const cause = error instanceof Error ? error : undefined;
      throw new VectorStoreError(
        `Failed to delete collection '${name}': ${message}`,
        VectorStoreErrorCode.PROVIDER_ERROR,
        'chromadb',
        cause ? { cause } : undefined,
      );
    }
  }

  protected override async doListCollections(): Promise<string[]> {
    if (!this.client) {
      throw new Error('Client not initialized');
    }

    // chromadb v3's listCollections() returns Collection instances; we
    // only need their names for the BaseVectorStore contract.
    const collections = await this.client.listCollections();
    return collections.map((c) => c.name);
  }

  protected override async doGetCollectionStats(name: string): Promise<CollectionStats> {
    const collection = await this.getOrLoadCollection(name);
    const count = await collection.count();

    // Get collection metadata
    const metadata = collection.metadata ?? {};

    const stats: CollectionStats = {
      name,
      documentCount: count,
      dimensions: (metadata['dimensions'] as number) ?? this.dimensions,
    };

    if (metadata['created_at']) {
      stats.createdAt = new Date(metadata['created_at'] as string);
    }
    if (metadata['updated_at']) {
      stats.updatedAt = new Date(metadata['updated_at'] as string);
    }

    return stats;
  }

  protected override async doCollectionExists(name: string): Promise<boolean> {
    if (!this.client) {
      throw new Error('Client not initialized');
    }

    const collections = await this.client.listCollections();
    return collections.some((c) => c.name === name);
  }

  // === Document Operations ===

  protected override async doUpsert(
    collection: string,
    documents: VectorDocument[],
  ): Promise<void> {
    const col = await this.getOrLoadCollection(collection);

    // Prepare data for ChromaDB
    const ids = documents.map((d) => d.id);
    const embeddings = documents.map((d) => d.embedding);
    const metadatas = documents.map((d) => this.prepareMetadata(d.metadata));
    const contents = documents.map((d) => d.content);

    // ChromaDB has a batch size limit, process in chunks
    const batchSize = 100;
    for (let i = 0; i < documents.length; i += batchSize) {
      const end = Math.min(i + batchSize, documents.length);
      await col.upsert({
        ids: ids.slice(i, end),
        embeddings: embeddings.slice(i, end),
        metadatas: metadatas.slice(i, end),
        documents: contents.slice(i, end),
      });
    }
  }

  protected override async doDelete(collection: string, ids: string[]): Promise<void> {
    const col = await this.getOrLoadCollection(collection);

    // ChromaDB has a batch size limit
    const batchSize = 100;
    for (let i = 0; i < ids.length; i += batchSize) {
      const end = Math.min(i + batchSize, ids.length);
      await col.delete({ ids: ids.slice(i, end) });
    }
  }

  protected override async doGet(collection: string, ids: string[]): Promise<VectorDocument[]> {
    const col = await this.getOrLoadCollection(collection);

    const result = await col.get({
      ids,
      include: ['embeddings' as IncludeEnum, 'metadatas' as IncludeEnum, 'documents' as IncludeEnum],
    });

    const documents: VectorDocument[] = [];
    for (let i = 0; i < result.ids.length; i++) {
      const id = result.ids[i];
      const embedding = result.embeddings?.[i];
      const metadata = result.metadatas?.[i];
      const content = result.documents?.[i];

      if (id !== undefined && embedding !== undefined) {
        documents.push({
          id,
          embedding: embedding as number[],
          content: content ?? '',
          metadata: (metadata ?? {}) as VectorDocument['metadata'],
        });
      }
    }

    return documents;
  }

  protected override async doCount(collection: string, filter?: MetadataFilter): Promise<number> {
    const col = await this.getOrLoadCollection(collection);

    if (!filter || isEmptyFilter(filter)) {
      return col.count();
    }

    // ChromaDB doesn't have a direct count with filter, so we need to query
    const whereClause = toChromaDBFilter(filter);
    const result = await col.get({
      where: whereClause as Record<string, unknown>,
      include: [],
    });

    return result.ids.length;
  }

  // === Search Operations ===

  protected override async doSearch(
    collection: string,
    query: SearchQuery,
  ): Promise<SearchResult[]> {
    const col = await this.getOrLoadCollection(collection);

    const include: IncludeEnum[] = ['distances' as IncludeEnum];
    if (query.includeMetadata !== false) include.push('metadatas' as IncludeEnum);
    if (query.includeContent !== false) include.push('documents' as IncludeEnum);
    if (query.includeEmbedding) include.push('embeddings' as IncludeEnum);

    const queryParams: {
      queryEmbeddings: number[][];
      nResults: number;
      include: IncludeEnum[];
      where?: Record<string, unknown>;
    } = {
      queryEmbeddings: [query.embedding],
      nResults: query.topK,
      include,
    };

    if (query.filter && !isEmptyFilter(query.filter)) {
      queryParams.where = toChromaDBFilter(query.filter) as Record<string, unknown>;
    }

    const result = await col.query(queryParams);

    // Process results
    const results: SearchResult[] = [];
    const ids = result.ids[0] ?? [];
    const distances = result.distances?.[0] ?? [];
    const metadatas = result.metadatas?.[0] ?? [];
    const documents = result.documents?.[0] ?? [];
    const embeddings = result.embeddings?.[0] ?? [];

    for (let i = 0; i < ids.length; i++) {
      const distance = distances[i] ?? 0;
      const score = this.distanceToScore(distance);

      // Apply score threshold filter
      if (query.scoreThreshold !== undefined && score < query.scoreThreshold) {
        continue;
      }

      const resultItem: SearchResult = {
        id: ids[i] as string,
        score,
      };

      if (query.includeContent !== false) {
        const content = documents[i];
        if (content !== null && content !== undefined) {
          resultItem.content = content;
        }
      }

      if (query.includeMetadata !== false) {
        const metadata = metadatas[i];
        if (metadata !== null && metadata !== undefined) {
          resultItem.metadata = metadata as VectorMetadata;
        }
      }

      if (query.includeEmbedding) {
        const embedding = embeddings[i];
        if (embedding !== null && embedding !== undefined) {
          resultItem.embedding = embedding as number[];
        }
      }

      results.push(resultItem);
    }

    return results;
  }

  protected override async doHybridSearch(
    collection: string,
    query: HybridSearchQuery,
  ): Promise<SearchResult[]> {
    const col = await this.getOrLoadCollection(collection);
    const alpha = query.alpha ?? 0.5;

    const include: IncludeEnum[] = ['distances' as IncludeEnum];
    if (query.includeMetadata !== false) include.push('metadatas' as IncludeEnum);
    if (query.includeContent !== false) include.push('documents' as IncludeEnum);
    if (query.includeEmbedding) include.push('embeddings' as IncludeEnum);

    // Perform vector search
    const vectorQueryParams: {
      queryEmbeddings: number[][];
      nResults: number;
      include: IncludeEnum[];
      where?: Record<string, unknown>;
    } = {
      queryEmbeddings: [query.embedding],
      nResults: query.topK * 2, // Get more results for fusion
      include,
    };

    if (query.filter && !isEmptyFilter(query.filter)) {
      vectorQueryParams.where = toChromaDBFilter(query.filter) as Record<string, unknown>;
    }

    const vectorResults = await col.query(vectorQueryParams);

    // Perform text search (using ChromaDB's where_document)
    const textQueryParams: {
      queryTexts: string[];
      nResults: number;
      include: IncludeEnum[];
      where?: Record<string, unknown>;
    } = {
      queryTexts: [query.text],
      nResults: query.topK * 2,
      include,
    };

    if (query.filter && !isEmptyFilter(query.filter)) {
      textQueryParams.where = toChromaDBFilter(query.filter) as Record<string, unknown>;
    }

    const textResults = await col.query(textQueryParams);

    // Fuse results with Reciprocal Rank Fusion (RRF)
    const scoreMap = new Map<string, { vectorRank: number; textRank: number; data: SearchResult }>();

    // Process vector results
    const vectorIds = vectorResults.ids[0] ?? [];
    const vectorMetadatas = vectorResults.metadatas?.[0] ?? [];
    const vectorDocuments = vectorResults.documents?.[0] ?? [];
    const vectorEmbeddings = vectorResults.embeddings?.[0] ?? [];

    for (let i = 0; i < vectorIds.length; i++) {
      const id = vectorIds[i] as string;
      const data: SearchResult = {
        id,
        score: 0, // Will be calculated
      };

      if (query.includeContent !== false) {
        const content = vectorDocuments[i];
        if (content !== null && content !== undefined) {
          data.content = content;
        }
      }

      if (query.includeMetadata !== false) {
        const metadata = vectorMetadatas[i];
        if (metadata !== null && metadata !== undefined) {
          data.metadata = metadata as VectorMetadata;
        }
      }

      if (query.includeEmbedding) {
        const embedding = vectorEmbeddings[i];
        if (embedding !== null && embedding !== undefined) {
          data.embedding = embedding as number[];
        }
      }

      scoreMap.set(id, {
        vectorRank: i + 1,
        textRank: vectorIds.length + 1, // Default rank if not in text results
        data,
      });
    }

    // Process text results
    const textIds = textResults.ids[0] ?? [];
    const textMetadatas = textResults.metadatas?.[0] ?? [];
    const textDocuments = textResults.documents?.[0] ?? [];
    const textEmbeddings = textResults.embeddings?.[0] ?? [];

    for (let i = 0; i < textIds.length; i++) {
      const id = textIds[i] as string;
      const existing = scoreMap.get(id);
      if (existing !== undefined) {
        existing.textRank = i + 1;
      } else {
        const data: SearchResult = {
          id,
          score: 0,
        };

        if (query.includeContent !== false) {
          const content = textDocuments[i];
          if (content !== null && content !== undefined) {
            data.content = content;
          }
        }

        if (query.includeMetadata !== false) {
          const metadata = textMetadatas[i];
          if (metadata !== null && metadata !== undefined) {
            data.metadata = metadata as VectorMetadata;
          }
        }

        if (query.includeEmbedding) {
          const embedding = textEmbeddings[i];
          if (embedding !== null && embedding !== undefined) {
            data.embedding = embedding as number[];
          }
        }

        scoreMap.set(id, {
          vectorRank: textIds.length + 1,
          textRank: i + 1,
          data,
        });
      }
    }

    // Calculate RRF scores
    const k = 60; // RRF constant
    const results: SearchResult[] = [];

    for (const [, entry] of scoreMap) {
      const vectorScore = alpha / (k + entry.vectorRank);
      const textScore = (1 - alpha) / (k + entry.textRank);
      const combinedScore = vectorScore + textScore;

      if (query.scoreThreshold === undefined || combinedScore >= query.scoreThreshold) {
        results.push({
          ...entry.data,
          score: combinedScore,
        });
      }
    }

    // Sort by score and limit to topK
    results.sort((a, b) => b.score - a.score);
    return results.slice(0, query.topK);
  }

  protected override async doMMRSearch(
    collection: string,
    query: MMRSearchQuery,
  ): Promise<SearchResult[]> {
    const col = await this.getOrLoadCollection(collection);
    const lambda = query.lambda ?? 0.5;
    const fetchK = query.fetchK ?? query.topK * 4;

    // Step 1: Fetch more candidates than needed
    const include: IncludeEnum[] = ['distances' as IncludeEnum, 'embeddings' as IncludeEnum];
    if (query.includeMetadata !== false) include.push('metadatas' as IncludeEnum);
    if (query.includeContent !== false) include.push('documents' as IncludeEnum);

    const queryParams: {
      queryEmbeddings: number[][];
      nResults: number;
      include: IncludeEnum[];
      where?: Record<string, unknown>;
    } = {
      queryEmbeddings: [query.embedding],
      nResults: fetchK,
      include,
    };

    if (query.filter && !isEmptyFilter(query.filter)) {
      queryParams.where = toChromaDBFilter(query.filter) as Record<string, unknown>;
    }

    const result = await col.query(queryParams);

    // Extract candidates
    const ids = result.ids[0] ?? [];
    const distances = result.distances?.[0] ?? [];
    const embeddings = result.embeddings?.[0] ?? [];
    const metadatas = result.metadatas?.[0] ?? [];
    const documents = result.documents?.[0] ?? [];

    if (ids.length === 0) {
      return [];
    }

    // Step 2: Apply MMR algorithm
    const candidates: Array<{
      id: string;
      embedding: number[];
      distance: number;
      metadata?: VectorMetadata;
      content?: string;
    }> = [];

    for (let i = 0; i < ids.length; i++) {
      const embedding = embeddings[i];
      if (!embedding || !Array.isArray(embedding)) continue;

      candidates.push({
        id: ids[i] as string,
        embedding: embedding as number[],
        distance: distances[i] ?? 0,
        metadata: metadatas[i] as VectorMetadata | undefined,
        content: documents[i] ?? undefined,
      });
    }

    // MMR selection
    const selected: SearchResult[] = [];
    const selectedEmbeddings: number[][] = [];
    const remainingIndices = new Set(candidates.map((_, i) => i));

    while (selected.length < query.topK && remainingIndices.size > 0) {
      let bestIdx = -1;
      let bestScore = -Infinity;

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

        if (mmrScore > bestScore) {
          bestScore = mmrScore;
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

  protected override async doOptimize(_collection: string): Promise<void> {
    // ChromaDB handles optimization automatically
    this.logger.info('ChromaDB handles optimization automatically');
  }

  protected override async doVacuum(_collection: string): Promise<void> {
    // ChromaDB handles cleanup automatically
    this.logger.info('ChromaDB handles cleanup automatically');
  }

  // === Private Helpers ===

  /**
   * Get a collection from cache or load it from ChromaDB
   */
  private async getOrLoadCollection(name: string): Promise<ChromaCollection> {
    const cached = this.collections.get(name);
    if (cached) {
      return cached;
    }

    if (!this.client) {
      throw new Error('Client not initialized');
    }

    try {
      // We always supply pre-computed embeddings on upsert, so opt out
      // of chromadb v3's DefaultEmbeddingFunction (otherwise the client
      // requires the optional `@chroma-core/default-embed` peer).
      const collection = await this.client.getOrCreateCollection({
        name,
        embeddingFunction: null,
      });
      this.collections.set(name, collection);
      return collection;
    } catch (error) {
      const message = error instanceof Error ? error.message : '';
      if (message.includes('does not exist') || message.includes('not found')) {
        throw new CollectionNotFoundError(name, 'chromadb');
      }
      const cause = error instanceof Error ? error : undefined;
      throw new VectorStoreError(
        `Failed to get collection '${name}': ${message}`,
        VectorStoreErrorCode.PROVIDER_ERROR,
        'chromadb',
        cause ? { cause } : undefined,
      );
    }
  }

  /**
   * Prepare metadata for ChromaDB (flatten complex types)
   */
  private prepareMetadata(
    metadata: VectorDocument['metadata'],
  ): Record<string, string | number | boolean> {
    const result: Record<string, string | number | boolean> = {};

    for (const [key, value] of Object.entries(metadata)) {
      if (value === null || value === undefined) {
        continue;
      }

      if (typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean') {
        result[key] = value;
      } else if (Array.isArray(value)) {
        // Convert arrays to JSON strings
        result[key] = JSON.stringify(value);
      } else if (typeof value === 'object') {
        // Convert objects to JSON strings
        result[key] = JSON.stringify(value);
      }
    }

    return result;
  }

  /**
   * Parse a chromadb endpoint string (URL or host:port) into the
   * host/port/ssl triple expected by chromadb v3's ChromaClient.
   *
   * Falls back to localhost:8000 when the input is empty, a filesystem
   * path, or otherwise unparseable — the subsequent heartbeat call
   * surfaces the "no chroma server reachable" error to the caller in
   * that case, which is the correct behaviour now that v3 has dropped
   * the v1 embedded mode.
   */
  private _parseEndpoint(input: string | undefined): {
    host: string;
    port: number;
    ssl: boolean;
  } {
    const fallback = { host: 'localhost', port: 8000, ssl: false };
    if (!input) return fallback;

    // Try to parse as a full URL first (http://host:port or https://host[:port]).
    try {
      const url = new URL(input);
      const ssl = url.protocol === 'https:';
      const host = url.hostname || 'localhost';
      const port = url.port ? Number.parseInt(url.port, 10) : ssl ? 443 : 8000;
      return { host, port, ssl };
    } catch {
      // Not a URL — fall through to host:port parsing.
    }

    // Accept bare "host:port" strings.
    const match = /^([^/:]+):(\d+)$/.exec(input);
    if (match && match[1] && match[2]) {
      return { host: match[1], port: Number.parseInt(match[2], 10), ssl: false };
    }

    return fallback;
  }

  /**
   * Convert ChromaDB distance to similarity score (0-1)
   */
  private distanceToScore(distance: number): number {
    switch (this.distanceMetric) {
      case 'cosine':
        // ChromaDB returns cosine distance (0-2), convert to similarity (0-1)
        return Math.max(0, Math.min(1, (cosineDistanceToSimilarity(distance) + 1) / 2));
      case 'euclidean':
        return euclideanDistanceToSimilarity(distance);
      case 'dot_product':
        // For negative inner product, negate and normalize
        return Math.max(0, Math.min(1, 1 - distance));
      default:
        return Math.max(0, Math.min(1, 1 - distance));
    }
  }
}
