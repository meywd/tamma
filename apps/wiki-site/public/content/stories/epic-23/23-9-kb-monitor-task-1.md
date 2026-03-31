---
title: "Task 1: Knowledge Base Monitor API Routes & Services"
sidebar:
  order: 230
---

**Story:** 23-9-knowledge-base-monitor
**Epic:** 23

## Task Description

Create backend API routes and services for the knowledge base monitor: vector DB collection health with test queries, document count and embedding coverage by file type, index freshness tracking, RAG pipeline health with retrieval latency breakdown, MCP server connection status with tool lists, and per-agent context window utilization.

## Acceptance Criteria

- `GET /api/monitoring/kb/vector-db/status` returns collection health status
- `GET /api/monitoring/kb/vector-db/:collection/detail` returns collection detail with sample docs
- `GET /api/monitoring/kb/coverage` returns document count, embedding coverage, pending/failed
- `GET /api/monitoring/kb/coverage/by-type` returns coverage by file type
- `GET /api/monitoring/kb/coverage/tree` returns directory tree with index coverage
- `GET /api/monitoring/kb/freshness` returns index freshness metrics
- `GET /api/monitoring/kb/freshness/trend` returns index lag time series
- `GET /api/monitoring/kb/rag/status` returns RAG pipeline health
- `GET /api/monitoring/kb/rag/quality` returns RAG quality metrics
- `GET /api/monitoring/kb/rag/latency` returns RAG latency breakdown
- `GET /api/monitoring/kb/mcp/servers` returns MCP server connection status
- `GET /api/monitoring/kb/mcp/:server/detail` returns server detail with tools
- `POST /api/monitoring/kb/mcp/:server/reconnect` forces reconnection
- `GET /api/monitoring/kb/context/utilization` returns per-agent context usage
- `GET /api/monitoring/kb/context/allocation` returns context budget allocation breakdown

## Implementation Details

### Technical Requirements

- [ ] Create `packages/api/src/routes/monitoring/kb-routes.ts`:
  ```typescript
  export function registerKBMonitoringRoutes(
    app: FastifyInstance,
    kbStatusService: KBStatusService,
    indexCoverageService: IndexCoverageService,
    ragMonitorService: RAGMonitorService,
  ): void;
  ```

- [ ] Create `packages/api/src/services/monitoring/kb-status-service.ts`:
  ```typescript
  export interface VectorDBCollectionStatus {
    name: string;
    documentCount: number;
    embeddingDimensions: number;
    distanceMetric: string;
    status: 'healthy' | 'degraded' | 'unavailable';
    storageSizeEstimate: number;
    lastModified: string | null;
    queryLatency: { avg: number; p50: number; p95: number; p99: number } | null;
  }

  export interface MCPServerStatus {
    name: string;
    connectionStatus: 'connected' | 'disconnected' | 'connecting';
    serverUrl: string | null;
    transportType: string | null;
    toolCount: number;
    lastCommunication: string | null;
    latencyMs: number | null;
    errorCountLastHour: number;
  }

  export interface MCPServerDetail extends MCPServerStatus {
    tools: { name: string; description: string; schemaSummary: string }[];
    recentInvocations: { toolName: string; timestamp: string; latencyMs: number; success: boolean }[];
    capabilities: { resources: boolean; prompts: boolean; tools: boolean };
    uptimeMs: number | null;
  }

  export interface ContextUtilization {
    agentRole: string;
    avgTokensUsed: number;
    maxTokens: number;
    usagePercent: number;
    budgetAllocation: Record<string, number>;  // source -> token count
    overflowCount: number;
    assemblyLatencyMs: number;
  }

  export class KBStatusService {
    constructor(deps: {
      vectorDBService: unknown;       // VectorDBManagementService
      mcpService: unknown;            // MCPManagementService
      contextService: unknown;        // ContextTestingService
      costTracker: ICostTracker | null;
      diagnosticsService: DiagnosticsService;
    });

    async getVectorDBStatus(): Promise<VectorDBCollectionStatus[]>;
    async getCollectionDetail(collection: string): Promise<{ samples: unknown[]; stats: unknown } | null>;
    async getMCPServers(): Promise<MCPServerStatus[]>;
    async getMCPServerDetail(serverName: string): Promise<MCPServerDetail | null>;
    async reconnectMCP(serverName: string): Promise<void>;
    async getContextUtilization(): Promise<ContextUtilization[]>;
    async getContextAllocation(): Promise<Record<string, Record<string, number>>>;
  }
  ```
  - Vector DB health: calls `getCollectionStats()` per collection, verifies response within 5s
  - MCP servers: reads from MCPManagementService status
  - Reconnect: calls `mcpClient.disconnect()` then `mcpClient.connect()`
  - Context utilization: from ContextAggregator running averages
  - Tool invocations from DiagnosticsService events of type `tool:complete`/`tool:error`

- [ ] Create `packages/api/src/services/monitoring/index-coverage-service.ts`:
  ```typescript
  export interface CoverageOverview {
    totalDocuments: number;
    totalEmbeddings: number;
    coveragePercent: number;
    pendingCount: number;
    pendingFiles: string[];       // first 20
    failedCount: number;
    failedFiles: { path: string; error: string }[];
    lastFullIndexRun: string | null;
    lastIndexDuration: number | null;
  }

  export interface CoverageByFileType {
    fileType: string;
    totalFiles: number;
    indexedFiles: number;
    pendingFiles: number;
    coveragePercent: number;
  }

  export interface CoverageTreeNode {
    path: string;
    type: 'directory' | 'file';
    status: 'indexed' | 'partial' | 'not-indexed' | 'excluded';
    children?: CoverageTreeNode[];
  }

  export interface IndexFreshness {
    lastIndexedAt: string | null;
    perCollection: Record<string, string>;   // collection -> last indexed
    oldestUnindexedAge: number | null;        // ms since oldest unindexed change
    pendingDocumentCount: number;
    indexLagMs: number | null;
    autoIndexEnabled: boolean;
    triggerType: string | null;
    nextScheduledRun: string | null;
  }

  export class IndexCoverageService {
    constructor(deps: {
      indexManagementService: unknown;  // IndexManagementService
      analyticsService: unknown;       // AnalyticsService
    });

    async getCoverage(): Promise<CoverageOverview>;
    async getCoverageByType(): Promise<CoverageByFileType[]>;
    async getCoverageTree(): Promise<CoverageTreeNode>;
    async getFreshness(): Promise<IndexFreshness>;
    async getFreshnessTrend(options?: { since?: number; until?: number }): Promise<{ timestamp: number; lagMs: number }[]>;
  }
  ```

- [ ] Create `packages/api/src/services/monitoring/rag-monitor-service.ts`:
  ```typescript
  export interface RAGPipelineStatus {
    status: 'active' | 'inactive' | 'error';
    retrievalLatency: { avg: number; p50: number; p95: number; p99: number };
    retrievalCountPerHour: number[];  // last 24 entries
    avgRelevanceScore: number;
    cacheHitRate: number;
    sourceBreakdown: Record<string, number>;  // 'vector_db' | 'keyword' | 'docs' | 'github' -> count
  }

  export interface RAGQualityMetrics {
    avgChunkCount: number;
    avgTokenBudgetUtilization: number;
    retrievalDiversity: number;
    feedbackScores: { thumbsUp: number; thumbsDown: number } | null;
  }

  export class RAGMonitorService {
    constructor(deps: {
      ragService: unknown;            // RAGManagementService
      analyticsService: unknown;
      diagnosticsService: DiagnosticsService;
    });

    async getStatus(): Promise<RAGPipelineStatus>;
    async getQuality(): Promise<RAGQualityMetrics>;
    async getLatencyBreakdown(options?: { since?: number; until?: number }): Promise<{
      timestamp: number;
      queryProcessing: number;
      vectorSearch: number;
      reranking: number;
      assembly: number;
    }[]>;
  }
  ```

### Files to Create

- CREATE `packages/api/src/routes/monitoring/kb-routes.ts`
- CREATE `packages/api/src/services/monitoring/kb-status-service.ts`
- CREATE `packages/api/src/services/monitoring/index-coverage-service.ts`
- CREATE `packages/api/src/services/monitoring/rag-monitor-service.ts`
- CREATE `packages/api/src/routes/monitoring/__tests__/kb-routes.test.ts`
- CREATE `packages/api/src/services/monitoring/__tests__/kb-status-service.test.ts`
- CREATE `packages/api/src/services/monitoring/__tests__/index-coverage-service.test.ts`
- CREATE `packages/api/src/services/monitoring/__tests__/rag-monitor-service.test.ts`

### Files to Modify

- MODIFY `packages/api/src/routes/monitoring/index.ts` -- register KB routes

### Dependencies

- Story 23-11: route registration
- VectorDBManagementService, IndexManagementService, RAGManagementService, MCPManagementService, ContextTestingService, AnalyticsService (existing KB services)
- DiagnosticsService (existing)

## Testing Strategy

### Unit Tests

- [ ] KBStatusService: vector DB health determined by response time
- [ ] KBStatusService: MCP server status from management service
- [ ] KBStatusService: reconnect calls disconnect then connect
- [ ] KBStatusService: context utilization aggregated correctly
- [ ] IndexCoverageService: coverage percentage computed correctly
- [ ] IndexCoverageService: pending/failed files listed
- [ ] IndexCoverageService: coverage tree reflects directory structure
- [ ] IndexCoverageService: freshness lag computed from timestamps
- [ ] RAGMonitorService: pipeline status from RAG service
- [ ] RAGMonitorService: latency breakdown aggregated from diagnostics
- [ ] KB routes: all 15 endpoints return expected structures
- [ ] KB routes: reconnect POST works for valid server name

## Completion Checklist

- [ ] All 15 API endpoints implemented
- [ ] Vector DB health with test query timing
- [ ] Coverage computation with file type breakdown
- [ ] Directory tree with coverage status
- [ ] RAG pipeline metrics and latency breakdown
- [ ] MCP server monitoring with reconnect
- [ ] Context window utilization tracking
- [ ] Tests written and passing
- [ ] TypeScript strict mode compiles
