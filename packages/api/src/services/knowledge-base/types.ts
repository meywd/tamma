/**
 * Local interface definitions for KB service dependencies.
 *
 * These define what the API services need from external packages
 * (@tamma/intelligence, @tamma/mcp-client, @tamma/cost-monitor)
 * without importing their types directly. This avoids requiring
 * those packages to be built for typecheck to pass.
 */

// ---------------------------------------------------------------------------
// Indexer (from @tamma/intelligence/indexer)
// ---------------------------------------------------------------------------

export interface ICodebaseIndexer {
  indexProject(projectPath: string, options?: { fullReindex?: boolean }): Promise<void>;
  updateIndex?(projectPath: string): Promise<void>;
  stop?(): void;
  getIndexStatus?(): { status: string; filesIndexed: number; chunksCreated: number; lastIndexedAt?: string };
  configure?(config: Record<string, unknown>): void;
  on?(event: string, handler: (...args: unknown[]) => void): void;
}

// ---------------------------------------------------------------------------
// Vector Store (from @tamma/intelligence/vector-store)
// ---------------------------------------------------------------------------

export interface IVectorStoreService {
  listCollections(): Promise<string[]>;
  getCollectionStats?(name: string): Promise<{ vectorCount: number; dimensions: number; storageBytes: number }>;
  createCollection(name: string, options?: { dimensions?: number }): Promise<void>;
  deleteCollection(name: string): Promise<void>;
  search(collection: string, query: { text?: string; vector?: number[]; limit?: number }): Promise<Array<{ id: string; score: number; content: string; metadata?: Record<string, unknown> }>>;
  hybridSearch?(collection: string, query: { text: string; limit?: number }): Promise<Array<{ id: string; score: number; content: string; metadata?: Record<string, unknown> }>>;
  getStorageUsage?(): Promise<{ totalBytes: number; collections: number }>;
}

// ---------------------------------------------------------------------------
// RAG Pipeline (from @tamma/intelligence/rag)
// ---------------------------------------------------------------------------

export interface IRAGPipeline {
  retrieve(query: string, options?: Record<string, unknown>): Promise<{ chunks: Array<{ content: string; source: string; score: number; metadata?: Record<string, unknown> }>; cached: boolean; durationMs: number }>;
  getCacheStats?(): { hits: number; misses: number; size: number };
  getFeedbackOverview?(): { totalFeedback: number; averageRelevance: number };
  configure?(config: Record<string, unknown>): void;
}

// ---------------------------------------------------------------------------
// MCP Client (from @tamma/mcp-client)
// ---------------------------------------------------------------------------

export interface IMCPClientService {
  listServers(): Array<{ name: string; status: string; transport: string; url?: string }>;
  connectServer(name: string): Promise<void>;
  disconnectServer(name: string): Promise<void>;
  listTools(serverName: string): Promise<Array<{ name: string; description?: string; inputSchema?: Record<string, unknown> }>>;
  invokeTool(serverName: string, toolName: string, args?: Record<string, unknown>): Promise<{ success: boolean; content: unknown; error?: string }>;
  getServerLogs?(serverName: string, limit?: number): Array<{ timestamp: string; level: 'error' | 'warn' | 'info' | 'debug'; message: string }>;
}

// ---------------------------------------------------------------------------
// Context Aggregator (from @tamma/intelligence/context)
// ---------------------------------------------------------------------------

export interface IContextAggregator {
  getContext(query: string, options?: Record<string, unknown>): Promise<{
    chunks: Array<{ content: string; source: string; score: number; metadata?: Record<string, unknown> }>;
    totalTokens: number;
    durationMs: number;
    sources: Array<{ name: string; chunks: number; durationMs: number }>;
  }>;
}

// ---------------------------------------------------------------------------
// Cost Tracker (from @tamma/cost-monitor)
// ---------------------------------------------------------------------------

export interface ICostTracker {
  getUsage(period?: { start: string; end: string }): { totalTokens: number; totalRequests: number; byProvider: Record<string, number>; byModel: Record<string, number> };
  getTotalCost(period?: { start: string; end: string }): number;
  getAggregate?(period?: { start: string; end: string }): { byProvider: Record<string, number>; byModel: Record<string, number> };
}
