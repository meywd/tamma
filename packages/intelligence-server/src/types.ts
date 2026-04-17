/**
 * Narrow interface definitions for intelligence-server service dependencies.
 *
 * These mirror the narrow interfaces used by the deleted packages/api KB
 * services. The sidecar services depend on these rather than on concrete
 * @tamma/intelligence classes so they are easy to unit-test with plain
 * object mocks (no real ChromaDB / RAG infrastructure needed).
 *
 * The thin adapter that maps a real IVectorStore / IRAGPipeline / etc. from
 * @tamma/intelligence to these interfaces lives in `./adapters.ts`.
 */

// ---------------------------------------------------------------------------
// Indexer (codebase indexing)
// ---------------------------------------------------------------------------

export interface IIndexer {
  indexProject(projectPath: string, options?: { fullReindex?: boolean }): Promise<void>;
  updateIndex?(projectPath: string, changedFiles?: string[]): Promise<void>;
  stop?(): Promise<void>;
  getIndexStatus?(): Promise<{
    status: string;
    filesIndexed: number;
    chunksCreated: number;
    lastIndexedAt?: string;
  }>;
  configure?(config: Record<string, unknown>): void;
  on?(event: string, handler: (...args: unknown[]) => void): void;
}

// ---------------------------------------------------------------------------
// Vector Store
// ---------------------------------------------------------------------------

export interface IVectorStoreAdapter {
  listCollections(): Promise<string[]>;
  getCollectionStats?(name: string): Promise<{
    vectorCount: number;
    dimensions: number;
    storageBytes: number;
  }>;
  createCollection(name: string, options?: { dimensions?: number }): Promise<void>;
  deleteCollection(name: string): Promise<void>;
  upsert(collection: string, documents: Array<{
    id: string;
    embedding: number[];
    content?: string;
    metadata?: Record<string, unknown>;
  }>): Promise<void>;
  delete(collection: string, ids: string[]): Promise<void>;
  search(collection: string, query: {
    text?: string;
    vector?: number[];
    limit?: number;
  }): Promise<Array<{
    id: string;
    score: number;
    content: string;
    metadata?: Record<string, unknown>;
  }>>;
  hybridSearch?(collection: string, query: {
    text: string;
    limit?: number;
  }): Promise<Array<{
    id: string;
    score: number;
    content: string;
    metadata?: Record<string, unknown>;
  }>>;
  getStorageUsage?(): Promise<{ totalBytes: number; collections: number }>;
}

// ---------------------------------------------------------------------------
// RAG Pipeline
// ---------------------------------------------------------------------------

export interface IRagPipeline {
  retrieve(
    query: { text: string; maxResults?: number; sources?: string[] },
    options?: Record<string, unknown>,
  ): Promise<{
    queryId: string;
    retrievedChunks: Array<{
      content: string;
      source: string;
      score: number;
      metadata?: Record<string, unknown>;
    }>;
    cacheHit: boolean;
    latencyMs: number;
  }>;
  getCacheStats?(): { hits: number; misses: number; size: number };
  getFeedbackOverview?(): { totalFeedback: number; averageRelevance: number };
  configure?(config: Record<string, unknown>): void;
}

// ---------------------------------------------------------------------------
// MCP Client
// ---------------------------------------------------------------------------

export interface IMcpClient {
  listServers(): Array<{ name: string; status: string; transport: string; url?: string }>;
  connectServer(name: string): Promise<void>;
  disconnectServer(name: string): Promise<void>;
  listTools(serverName: string): Promise<Array<{
    name: string;
    description?: string;
    inputSchema?: Record<string, unknown>;
  }>>;
  invokeTool(
    serverName: string,
    toolName: string,
    args?: Record<string, unknown>,
  ): Promise<{ success: boolean; content: unknown; error?: string }>;
  getServerLogs?(serverName: string, limit?: number): Array<{
    timestamp: string;
    level: 'error' | 'warn' | 'info' | 'debug';
    message: string;
  }>;
}

// ---------------------------------------------------------------------------
// Context Aggregator
// ---------------------------------------------------------------------------

export interface IContextAggregatorAdapter {
  getContext(
    request: { query: string; taskType?: string; maxTokens?: number },
    options?: Record<string, unknown>,
  ): Promise<{
    requestId: string;
    context: {
      text: string;
      chunks: Array<{
        content: string;
        source: string;
        score: number;
        metadata?: Record<string, unknown>;
      }>;
      tokenCount: number;
    };
    sources: Array<{ name: string; chunks: number; durationMs: number }>;
    metrics: { totalLatencyMs: number; totalTokens: number };
  }>;
}

// ---------------------------------------------------------------------------
// Cost Tracker (for analytics)
// ---------------------------------------------------------------------------

export interface ICostTrackerAdapter {
  getUsage(period?: { start: string; end: string }): Promise<Array<{
    provider: string;
    model: string;
    tokens: number;
    cost: number;
  }>>;
  getTotalCost(period?: { start: string; end: string }): Promise<number>;
  getAggregate?(period?: { start: string; end: string }): Promise<{
    byProvider: Record<string, number>;
    byModel: Record<string, number>;
  }>;
}

// ---------------------------------------------------------------------------
// Service bundle passed to the server factory
// ---------------------------------------------------------------------------

export interface IntelligenceServicesBundle {
  indexer?: IIndexer;
  vectorStore?: IVectorStoreAdapter;
  ragPipeline?: IRagPipeline;
  mcpClient?: IMcpClient;
  contextAggregator?: IContextAggregatorAdapter;
  costTracker?: ICostTrackerAdapter;
}
