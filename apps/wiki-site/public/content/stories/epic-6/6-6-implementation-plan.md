---
title: "Story 6-6: Knowledge Base Management UI - Implementation Plan"
sidebar:
  order: 60
---

## Overview

This implementation plan details the technical approach for building the Knowledge Base Management UI, a web-based dashboard for managing the Tamma knowledge base infrastructure including vector database status, RAG pipeline configuration, MCP server connections, indexing jobs, and context retrieval testing.

## Package Location

### Primary Packages

| Package | Purpose |
|---------|---------|
| `@tamma/dashboard` | React frontend components and pages |
| `@tamma/api` | Fastify REST API endpoints |
| `@tamma/shared` | Shared types, interfaces, and contracts |

### Package Dependencies (from Epic 6)

```
@tamma/dashboard
  └── @tamma/shared (types)

@tamma/api
  ├── @tamma/shared (types)
  ├── @tamma/orchestrator (context aggregator access)
  └── Epic 6 service packages:
      ├── Story 6-1: @tamma/indexer
      ├── Story 6-2: @tamma/vector-store
      ├── Story 6-3: @tamma/rag
      ├── Story 6-4: @tamma/mcp-client
      └── Story 6-5: @tamma/context-aggregator
```

---

## Files to Create/Modify

### 1. Shared Types (`packages/shared/src`)

#### New Files

```
packages/shared/src/
├── types/
│   └── knowledge-base/
│       ├── index.ts              # Barrel export
│       ├── index-types.ts        # Index management types
│       ├── vector-db-types.ts    # Vector database types
│       ├── rag-types.ts          # RAG pipeline types
│       ├── mcp-types.ts          # MCP server types
│       ├── context-types.ts      # Context testing types
│       └── analytics-types.ts    # Analytics & reporting types
└── contracts/
    └── knowledge-base/
        ├── index.ts              # Barrel export
        ├── IIndexService.ts      # Index management interface
        ├── IVectorDBService.ts   # Vector DB service interface
        ├── IRAGService.ts        # RAG pipeline interface
        ├── IMCPService.ts        # MCP management interface
        └── IContextService.ts    # Context testing interface
```

#### Modified Files

```
packages/shared/src/
├── types/index.ts          # Add knowledge-base export
└── contracts/index.ts      # Add knowledge-base export
```

### 2. API Endpoints (`packages/api/src`)

#### New Files

```
packages/api/src/
├── routes/
│   └── knowledge-base/
│       ├── index.ts              # Route registration
│       ├── index-routes.ts       # /api/knowledge-base/index/*
│       ├── vector-db-routes.ts   # /api/knowledge-base/vector-db/*
│       ├── rag-routes.ts         # /api/knowledge-base/rag/*
│       ├── mcp-routes.ts         # /api/knowledge-base/mcp/*
│       ├── context-routes.ts     # /api/knowledge-base/context/*
│       └── analytics-routes.ts   # /api/knowledge-base/analytics/*
├── services/
│   └── knowledge-base/
│       ├── index.ts              # Service barrel export
│       ├── IndexManagementService.ts
│       ├── VectorDBManagementService.ts
│       ├── RAGManagementService.ts
│       ├── MCPManagementService.ts
│       ├── ContextTestingService.ts
│       └── AnalyticsService.ts
└── schemas/
    └── knowledge-base/
        ├── index.ts              # Schema barrel export
        └── validation-schemas.ts # Request/response validation
```

#### Modified Files

```
packages/api/src/
└── index.ts                # Register knowledge-base routes
```

### 3. Dashboard Components (`packages/dashboard/src`)

#### New Files

```
packages/dashboard/src/
├── pages/
│   └── knowledge-base/
│       ├── index.tsx             # Main KB dashboard page
│       ├── IndexPage.tsx         # /knowledge-base/index
│       ├── VectorDBPage.tsx      # /knowledge-base/vector-db
│       ├── RAGPage.tsx           # /knowledge-base/rag
│       ├── MCPPage.tsx           # /knowledge-base/mcp
│       ├── ContextTestPage.tsx   # /knowledge-base/test
│       └── AnalyticsPage.tsx     # /knowledge-base/analytics
├── components/
│   └── knowledge-base/
│       ├── index.ts              # Component barrel export
│       │
│       ├── dashboard/
│       │   ├── QuickStatusPanel.tsx
│       │   ├── StatusCard.tsx
│       │   └── DashboardLayout.tsx
│       │
│       ├── index-management/
│       │   ├── IndexStatusCard.tsx
│       │   ├── IndexingHistoryTable.tsx
│       │   ├── IndexConfigEditor.tsx
│       │   └── PatternEditor.tsx
│       │
│       ├── vector-db/
│       │   ├── CollectionList.tsx
│       │   ├── CollectionStats.tsx
│       │   ├── VectorSearchTest.tsx
│       │   └── StorageMetrics.tsx
│       │
│       ├── rag/
│       │   ├── RAGConfigPanel.tsx
│       │   ├── SourceWeightsEditor.tsx
│       │   ├── RAGTestInterface.tsx
│       │   └── RAGMetricsChart.tsx
│       │
│       ├── mcp/
│       │   ├── MCPServerCard.tsx
│       │   ├── MCPServerList.tsx
│       │   ├── ToolList.tsx
│       │   ├── ToolInvokePanel.tsx
│       │   └── ServerLogViewer.tsx
│       │
│       ├── context-testing/
│       │   ├── ContextTestInterface.tsx
│       │   ├── ContextViewer.tsx
│       │   ├── ChunkCard.tsx
│       │   ├── SourceContributionChart.tsx
│       │   └── FeedbackControls.tsx
│       │
│       ├── config/
│       │   ├── ConfigEditor.tsx
│       │   ├── ConfigDiffViewer.tsx
│       │   └── ConfigVersionHistory.tsx
│       │
│       └── analytics/
│           ├── UsageChart.tsx
│           ├── QualityMetrics.tsx
│           ├── CostBreakdown.tsx
│           └── TokenUsageReport.tsx
│
├── hooks/
│   └── knowledge-base/
│       ├── useIndexStatus.ts
│       ├── useVectorDB.ts
│       ├── useRAGConfig.ts
│       ├── useMCPServers.ts
│       ├── useContextTest.ts
│       └── useKBAnalytics.ts
│
├── services/
│   └── knowledge-base/
│       └── api-client.ts         # API client for KB endpoints
│
└── stores/
    └── knowledge-base/
        └── store.ts              # State management (Zustand/Context)
```

#### Modified Files

```
packages/dashboard/src/
├── index.tsx                # Add router and KB routes
├── App.tsx                  # Add navigation to KB dashboard
└── package.json             # Add new dependencies
```

---

## Interfaces and Types

### Core Types (`packages/shared/src/types/knowledge-base/`)

```typescript
// index-types.ts
export interface IndexStatus {
  status: 'idle' | 'indexing' | 'error';
  lastRun: Date | null;
  filesIndexed: number;
  chunksCreated: number;
  progress?: number;
  currentFile?: string;
  error?: string;
}

export interface IndexHistory {
  id: string;
  startTime: Date;
  endTime: Date;
  filesProcessed: number;
  chunksCreated: number;
  chunksUpdated: number;
  chunksDeleted: number;
  embeddingCost: number;
  durationMs: number;
  status: 'success' | 'partial' | 'failed';
  errors: IndexError[];
}

export interface IndexError {
  filePath: string;
  error: string;
  timestamp: Date;
}

export interface IndexConfig {
  includePatterns: string[];
  excludePatterns: string[];
  chunkingConfig: ChunkingConfig;
  embeddingConfig: EmbeddingConfig;
  triggerConfig: TriggerConfig;
}

export interface ChunkingConfig {
  maxTokens: number;
  overlapTokens: number;
  preserveImports: boolean;
  groupRelatedCode: boolean;
}

export interface EmbeddingConfig {
  provider: 'openai' | 'cohere' | 'ollama';
  model: string;
  batchSize: number;
}

export interface TriggerConfig {
  gitHooks: boolean;
  watchMode: boolean;
  schedule: string | null;
}

// vector-db-types.ts
export interface CollectionInfo {
  name: string;
  vectorCount: number;
  dimensions: number;
  storageBytes: number;
  createdAt: Date;
  lastModified: Date;
}

export interface CollectionStats {
  name: string;
  vectorCount: number;
  dimensions: number;
  storageBytes: number;
  queryMetrics: QueryMetrics;
}

export interface QueryMetrics {
  totalQueries: number;
  avgLatencyMs: number;
  p95LatencyMs: number;
  p99LatencyMs: number;
  queriesPerMinute: number;
}

export interface VectorSearchResult {
  id: string;
  score: number;
  content: string;
  metadata: Record<string, unknown>;
}

export interface VectorSearchRequest {
  collection: string;
  query: string;
  topK: number;
  scoreThreshold?: number;
  filter?: Record<string, unknown>;
}

// rag-types.ts
export interface RAGConfig {
  sources: RAGSourceConfig;
  ranking: RankingConfig;
  assembly: AssemblyConfig;
  caching: CachingConfig;
}

export interface RAGSourceConfig {
  vectorDb: { enabled: boolean; weight: number; topK: number };
  keyword: { enabled: boolean; weight: number; topK: number };
  docs: { enabled: boolean; weight: number; topK: number };
  issues: { enabled: boolean; weight: number; topK: number };
}

export interface RankingConfig {
  fusionMethod: 'rrf' | 'linear' | 'learned';
  mmrLambda: number;
  recencyBoost: number;
}

export interface AssemblyConfig {
  maxTokens: number;
  format: 'xml' | 'markdown' | 'plain';
  includeScores: boolean;
}

export interface CachingConfig {
  enabled: boolean;
  ttlSeconds: number;
  maxEntries: number;
}

export interface RAGMetrics {
  totalQueries: number;
  avgLatencyMs: number;
  cacheHitRate: number;
  avgTokensRetrieved: number;
  sourceBreakdown: Record<string, number>;
}

export interface RAGTestRequest {
  query: string;
  sources?: string[];
  maxTokens?: number;
  topK?: number;
}

export interface RAGTestResult {
  queryId: string;
  chunks: RetrievedChunk[];
  assembledContext: string;
  tokenCount: number;
  latencyMs: number;
  sources: SourceAttribution[];
}

export interface RetrievedChunk {
  id: string;
  content: string;
  source: string;
  score: number;
  metadata: {
    filePath?: string;
    startLine?: number;
    endLine?: number;
    url?: string;
    date?: Date;
  };
}

export interface SourceAttribution {
  source: string;
  count: number;
  avgScore: number;
  tokensUsed: number;
}

// mcp-types.ts
export interface MCPServerInfo {
  name: string;
  status: 'connected' | 'disconnected' | 'error' | 'starting';
  transport: 'stdio' | 'sse';
  toolCount: number;
  resourceCount: number;
  lastConnected?: Date;
  error?: string;
  config: MCPServerConfig;
}

export interface MCPServerConfig {
  name: string;
  transport: 'stdio' | 'sse';
  command?: string;
  args?: string[];
  env?: Record<string, string>;
  url?: string;
  headers?: Record<string, string>;
  timeout?: number;
  enabled: boolean;
}

export interface MCPTool {
  name: string;
  description: string;
  inputSchema: JSONSchema;
  serverName: string;
}

export interface MCPResource {
  uri: string;
  name: string;
  description?: string;
  mimeType?: string;
  serverName: string;
}

export interface MCPToolInvokeRequest {
  serverName: string;
  toolName: string;
  arguments: Record<string, unknown>;
}

export interface MCPToolInvokeResult {
  success: boolean;
  content: unknown;
  error?: string;
  durationMs: number;
}

export interface MCPServerLog {
  timestamp: Date;
  level: 'info' | 'warn' | 'error' | 'debug';
  message: string;
  data?: Record<string, unknown>;
}

// context-types.ts
export interface ContextTestRequest {
  query: string;
  taskType: TaskType;
  maxTokens: number;
  sources?: ContextSource[];
  hints?: ContextHints;
  options?: ContextOptions;
}

export type TaskType = 'analysis' | 'planning' | 'implementation' | 'review' | 'testing' | 'documentation';
export type ContextSource = 'vector_db' | 'rag' | 'mcp' | 'web_search' | 'live_api';

export interface ContextHints {
  relatedFiles?: string[];
  relatedIssues?: number[];
  language?: string;
  framework?: string;
}

export interface ContextOptions {
  deduplicate?: boolean;
  compress?: boolean;
  summarize?: boolean;
  includeMetadata?: boolean;
}

export interface ContextTestResult {
  requestId: string;
  context: AssembledContext;
  sources: SourceContribution[];
  metrics: ContextMetrics;
}

export interface AssembledContext {
  text: string;
  chunks: ContextChunk[];
  tokenCount: number;
  format: 'xml' | 'markdown' | 'plain';
}

export interface ContextChunk {
  id: string;
  content: string;
  source: ContextSource;
  relevance: number;
  metadata: ChunkMetadata;
}

export interface ChunkMetadata {
  filePath?: string;
  startLine?: number;
  endLine?: number;
  language?: string;
  symbolName?: string;
}

export interface SourceContribution {
  source: ContextSource;
  chunksProvided: number;
  tokensUsed: number;
  latencyMs: number;
  cacheHit: boolean;
}

export interface ContextMetrics {
  totalLatencyMs: number;
  totalTokens: number;
  budgetUtilization: number;
  deduplicationRate: number;
  cacheHitRate: number;
}

export interface RelevanceFeedback {
  chunkId: string;
  rating: 'relevant' | 'irrelevant' | 'partially_relevant';
  comment?: string;
}

// analytics-types.ts
export interface UsageAnalytics {
  period: { start: Date; end: Date };
  totalQueries: number;
  totalTokensRetrieved: number;
  avgLatencyMs: number;
  sourceBreakdown: Record<string, SourceUsage>;
}

export interface SourceUsage {
  queries: number;
  tokensRetrieved: number;
  avgLatencyMs: number;
  cacheHitRate: number;
}

export interface QualityAnalytics {
  period: { start: Date; end: Date };
  totalFeedback: number;
  relevanceRate: number;
  avgRelevanceScore: number;
  topPerformingSources: string[];
  improvementTrend: number;
}

export interface CostAnalytics {
  period: { start: Date; end: Date };
  totalCostUsd: number;
  embeddingCostUsd: number;
  indexingCostUsd: number;
  breakdown: CostBreakdown[];
}

export interface CostBreakdown {
  category: string;
  costUsd: number;
  units: number;
  unitCostUsd: number;
}
```

### Service Contracts (`packages/shared/src/contracts/knowledge-base/`)

```typescript
// IIndexService.ts
export interface IIndexService {
  getStatus(): Promise<IndexStatus>;
  triggerIndex(options?: { fullReindex?: boolean }): Promise<void>;
  getHistory(limit?: number): Promise<IndexHistory[]>;
  getConfig(): Promise<IndexConfig>;
  updateConfig(config: Partial<IndexConfig>): Promise<IndexConfig>;
  cancelIndex(): Promise<void>;
}

// IVectorDBService.ts
export interface IVectorDBService {
  listCollections(): Promise<CollectionInfo[]>;
  getCollectionStats(name: string): Promise<CollectionStats>;
  createCollection(name: string, dimensions?: number): Promise<void>;
  deleteCollection(name: string): Promise<void>;
  search(request: VectorSearchRequest): Promise<VectorSearchResult[]>;
  getStorageUsage(): Promise<{ totalBytes: number; byCollection: Record<string, number> }>;
}

// IRAGService.ts
export interface IRAGService {
  getConfig(): Promise<RAGConfig>;
  updateConfig(config: Partial<RAGConfig>): Promise<RAGConfig>;
  getMetrics(): Promise<RAGMetrics>;
  testQuery(request: RAGTestRequest): Promise<RAGTestResult>;
}

// IMCPService.ts
export interface IMCPService {
  listServers(): Promise<MCPServerInfo[]>;
  getServerStatus(name: string): Promise<MCPServerInfo>;
  startServer(name: string): Promise<void>;
  stopServer(name: string): Promise<void>;
  restartServer(name: string): Promise<void>;
  listTools(serverName?: string): Promise<MCPTool[]>;
  invokeTool(request: MCPToolInvokeRequest): Promise<MCPToolInvokeResult>;
  getServerLogs(name: string, limit?: number): Promise<MCPServerLog[]>;
}

// IContextService.ts
export interface IContextService {
  testContext(request: ContextTestRequest): Promise<ContextTestResult>;
  submitFeedback(requestId: string, feedback: RelevanceFeedback[]): Promise<void>;
  getRecentTests(limit?: number): Promise<ContextTestResult[]>;
}

// IKBAnalyticsService.ts
export interface IKBAnalyticsService {
  getUsageAnalytics(period: { start: Date; end: Date }): Promise<UsageAnalytics>;
  getQualityAnalytics(period: { start: Date; end: Date }): Promise<QualityAnalytics>;
  getCostAnalytics(period: { start: Date; end: Date }): Promise<CostAnalytics>;
}
```

---

## Implementation Phases

### Phase 1: Foundation (Week 1-2)

**Objective:** Establish shared types, API structure, and basic dashboard layout.

#### Tasks

1. **Shared Types & Contracts**
   - Create all type definitions in `@tamma/shared`
   - Define service contracts/interfaces
   - Add validation schemas (Zod)

2. **API Structure**
   - Set up Fastify route structure for knowledge-base endpoints
   - Implement stub handlers returning mock data
   - Add request/response validation

3. **Dashboard Foundation**
   - Install dependencies: `react-router-dom`, `@tanstack/react-query`, `recharts`, `tailwindcss`
   - Set up routing structure
   - Create base layout components
   - Implement navigation

4. **API Client**
   - Create typed API client for knowledge-base endpoints
   - Set up React Query hooks

#### Deliverables
- [ ] All shared types defined and exported
- [ ] API routes registered (returning mock data)
- [ ] Dashboard routing working
- [ ] Basic layout and navigation

### Phase 2: Index Management (Week 3)

**Objective:** Implement full index management UI with real service integration.

#### Tasks

1. **Index Status Dashboard**
   - `IndexStatusCard` component with real-time status
   - Progress indicator during indexing
   - Trigger re-index button

2. **Index History**
   - `IndexingHistoryTable` with pagination
   - Detail view for each indexing run
   - Error log viewer

3. **Index Configuration**
   - `IndexConfigEditor` with pattern editors
   - Chunking settings form
   - Embedding provider selection
   - Trigger configuration (schedule, hooks)

4. **API Integration**
   - Connect to Story 6-1 Codebase Indexer service
   - Implement real-time status updates via SSE
   - Handle long-running index operations

#### Deliverables
- [ ] Index status dashboard with real-time updates
- [ ] Index history with drill-down
- [ ] Configuration editor with validation
- [ ] Manual trigger functionality

### Phase 3: Vector Database Management (Week 4)

**Objective:** Complete vector database monitoring and management UI.

#### Tasks

1. **Collection Management**
   - `CollectionList` with stats overview
   - Create/delete collection modals
   - `CollectionStats` detail view

2. **Search Testing**
   - `VectorSearchTest` interactive component
   - Results viewer with scores
   - Metadata filtering UI

3. **Metrics Dashboard**
   - `StorageMetrics` visualization
   - Query performance charts
   - Historical metrics view

4. **API Integration**
   - Connect to Story 6-2 Vector Database service
   - Implement collection CRUD operations
   - Real-time metrics streaming

#### Deliverables
- [ ] Collection list with statistics
- [ ] Collection create/delete operations
- [ ] Interactive search testing
- [ ] Performance metrics dashboard

### Phase 4: RAG Pipeline Configuration (Week 5)

**Objective:** Build RAG pipeline configuration and testing UI.

#### Tasks

1. **Configuration Panel**
   - `RAGConfigPanel` main component
   - `SourceWeightsEditor` with sliders
   - Ranking parameters form
   - Token budget settings

2. **Test Interface**
   - `RAGTestInterface` with query input
   - Results visualization
   - Source attribution display

3. **Metrics Visualization**
   - `RAGMetricsChart` with historical data
   - Source breakdown pie chart
   - Cache hit rate display

4. **API Integration**
   - Connect to Story 6-3 RAG Pipeline service
   - Configuration save/load
   - Test query execution

#### Deliverables
- [ ] RAG configuration editor
- [ ] Interactive RAG query testing
- [ ] Metrics and analytics charts

### Phase 5: MCP Server Management (Week 6)

**Objective:** Implement MCP server monitoring and control UI.

#### Tasks

1. **Server Dashboard**
   - `MCPServerList` with status indicators
   - `MCPServerCard` with quick actions
   - Start/stop/restart controls

2. **Tool Browser**
   - `ToolList` with search and filter
   - Tool schema viewer
   - `ToolInvokePanel` for testing tools

3. **Logging**
   - `ServerLogViewer` with filtering
   - Real-time log streaming
   - Log level filtering

4. **API Integration**
   - Connect to Story 6-4 MCP Client service
   - Server lifecycle management
   - Tool invocation with results

#### Deliverables
- [ ] Server status dashboard
- [ ] Start/stop/restart functionality
- [ ] Tool browser and invocation
- [ ] Log viewer with real-time streaming

### Phase 6: Context Testing Interface (Week 7)

**Objective:** Build the unified context testing and feedback UI.

#### Tasks

1. **Test Interface**
   - `ContextTestInterface` main component
   - Query input with options
   - Task type and source selection

2. **Results Viewer**
   - `ContextViewer` with chunk display
   - `ChunkCard` with metadata
   - Query highlighting in results

3. **Feedback System**
   - `FeedbackControls` per chunk
   - Relevance rating buttons
   - Comment input

4. **Source Analysis**
   - `SourceContributionChart` visualization
   - Token usage breakdown
   - Latency comparison

5. **API Integration**
   - Connect to Story 6-5 Context Aggregator
   - Execute context queries
   - Submit feedback data

#### Deliverables
- [ ] Interactive context query testing
- [ ] Rich results viewer with highlighting
- [ ] Feedback submission system
- [ ] Source contribution visualization

### Phase 7: Analytics & Configuration Editor (Week 8)

**Objective:** Complete analytics dashboard and global configuration editor.

#### Tasks

1. **Analytics Dashboard**
   - `UsageChart` time series
   - `QualityMetrics` display
   - `CostBreakdown` visualization
   - `TokenUsageReport` table

2. **Configuration Editor**
   - `ConfigEditor` with YAML/JSON support
   - `ConfigDiffViewer` for changes
   - `ConfigVersionHistory` with rollback

3. **Export/Import**
   - Configuration export to file
   - Configuration import with validation
   - Version comparison

4. **Finalization**
   - Error boundary implementation
   - Loading states polish
   - Responsive design verification

#### Deliverables
- [ ] Analytics dashboard with all metrics
- [ ] Configuration editor with validation
- [ ] Version history and diff viewer
- [ ] Export/import functionality

---

## Dependencies

### External Dependencies (Dashboard)

```json
{
  "dependencies": {
    "react": "^18.3.1",
    "react-dom": "^18.3.1",
    "react-router-dom": "^6.20.0",
    "@tanstack/react-query": "^5.0.0",
    "zustand": "^4.4.0",
    "recharts": "^2.10.0",
    "tailwindcss": "^3.4.0",
    "@headlessui/react": "^1.7.0",
    "@heroicons/react": "^2.1.0",
    "date-fns": "^3.0.0",
    "zod": "^3.22.0",
    "highlight.js": "^11.9.0",
    "react-syntax-highlighter": "^15.5.0"
  },
  "devDependencies": {
    "@testing-library/react": "^14.1.0",
    "@testing-library/jest-dom": "^6.1.0",
    "msw": "^2.0.0",
    "@vitejs/plugin-react": "^4.2.0"
  }
}
```

### External Dependencies (API)

```json
{
  "dependencies": {
    "fastify": "^5.2.0",
    "@fastify/cors": "^10.0.1",
    "@fastify/helmet": "^12.0.1",
    "@fastify/websocket": "^8.3.0",
    "zod": "^3.22.0"
  }
}
```

### Internal Dependencies (Epic 6 Services)

| Dependency | Story | Status | Required APIs |
|------------|-------|--------|---------------|
| Codebase Indexer | 6-1 | Planned | `ICodebaseIndexer` |
| Vector Store | 6-2 | Planned | `IVectorStore` |
| RAG Pipeline | 6-3 | Planned | `IRAGPipeline` |
| MCP Client | 6-4 | Planned | `IMCPClient` |
| Context Aggregator | 6-5 | Planned | `IContextAggregator` |

---

## Testing Strategy

### Unit Tests

#### Dashboard Components
```typescript
// Component test example
describe('IndexStatusCard', () => {
  it('displays idle status correctly', () => {
    render(<IndexStatusCard status={mockIdleStatus} onTriggerIndex={vi.fn()} />);
    expect(screen.getByText('Idle')).toBeInTheDocument();
  });

  it('shows progress during indexing', () => {
    render(<IndexStatusCard status={mockIndexingStatus} onTriggerIndex={vi.fn()} />);
    expect(screen.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '45');
  });

  it('calls onTriggerIndex when button clicked', async () => {
    const onTrigger = vi.fn();
    render(<IndexStatusCard status={mockIdleStatus} onTriggerIndex={onTrigger} />);
    await userEvent.click(screen.getByText('Re-index'));
    expect(onTrigger).toHaveBeenCalled();
  });
});
```

#### API Routes
```typescript
// Route test example
describe('GET /api/knowledge-base/index/status', () => {
  it('returns current index status', async () => {
    const response = await app.inject({
      method: 'GET',
      url: '/api/knowledge-base/index/status',
    });

    expect(response.statusCode).toBe(200);
    expect(response.json()).toMatchObject({
      status: expect.stringMatching(/idle|indexing|error/),
      filesIndexed: expect.any(Number),
    });
  });
});
```

#### Custom Hooks
```typescript
// Hook test example
describe('useIndexStatus', () => {
  it('fetches index status on mount', async () => {
    const { result } = renderHook(() => useIndexStatus(), { wrapper: QueryWrapper });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(result.current.data?.status).toBeDefined();
  });
});
```

### Integration Tests

```typescript
// Integration test example
describe('Index Management Flow', () => {
  beforeAll(async () => {
    await startTestServer();
    await seedTestData();
  });

  it('triggers index and shows progress', async () => {
    render(<IndexPage />, { wrapper: TestProviders });

    // Click trigger button
    await userEvent.click(screen.getByText('Start Indexing'));

    // Wait for status update
    await waitFor(() => {
      expect(screen.getByText(/indexing/i)).toBeInTheDocument();
    });

    // Verify progress updates
    await waitFor(() => {
      expect(screen.getByRole('progressbar')).toBeInTheDocument();
    }, { timeout: 5000 });
  });
});
```

### E2E Tests (Playwright)

```typescript
// E2E test example
test.describe('Knowledge Base Dashboard', () => {
  test('complete context testing workflow', async ({ page }) => {
    await page.goto('/knowledge-base');

    // Navigate to context test
    await page.click('text=Context Test');

    // Enter query
    await page.fill('[data-testid="query-input"]', 'How does authentication work?');

    // Select options
    await page.check('[data-testid="source-vector-db"]');
    await page.check('[data-testid="source-rag"]');

    // Execute test
    await page.click('text=Test Query');

    // Wait for results
    await expect(page.locator('[data-testid="context-results"]')).toBeVisible();

    // Verify chunks displayed
    await expect(page.locator('[data-testid="chunk-card"]')).toHaveCount.greaterThan(0);

    // Submit feedback
    await page.click('[data-testid="feedback-relevant"]');
    await expect(page.locator('text=Feedback submitted')).toBeVisible();
  });
});
```

### Test Coverage Requirements

| Category | Target Coverage |
|----------|-----------------|
| Components | > 80% |
| Hooks | > 90% |
| API Routes | > 90% |
| Services | > 85% |
| Utils | > 95% |

---

## Configuration

### Environment Variables

```env
# API Configuration
TAMMA_API_PORT=3001
TAMMA_API_HOST=0.0.0.0

# Service URLs (for API to connect to backend services)
INDEXER_SERVICE_URL=http://localhost:3010
VECTOR_DB_SERVICE_URL=http://localhost:3011
RAG_SERVICE_URL=http://localhost:3012
MCP_SERVICE_URL=http://localhost:3013
CONTEXT_SERVICE_URL=http://localhost:3014

# Dashboard Configuration
VITE_API_BASE_URL=http://localhost:3001/api

# Feature Flags
TAMMA_KB_ANALYTICS_ENABLED=true
TAMMA_KB_CONFIG_VERSIONING_ENABLED=true
TAMMA_KB_REALTIME_UPDATES_ENABLED=true
```

### Dashboard Configuration

```typescript
// packages/dashboard/src/config/knowledge-base.ts
export const kbConfig = {
  // Polling intervals
  statusPollIntervalMs: 5000,
  metricsPollIntervalMs: 30000,
  logsPollIntervalMs: 2000,

  // UI defaults
  defaultTaskType: 'implementation' as TaskType,
  defaultMaxTokens: 4000,
  defaultTopK: 10,

  // Pagination
  indexHistoryPageSize: 20,
  logsPageSize: 100,
  testHistoryPageSize: 10,

  // Charts
  metricsHistoryDays: 30,
  chartRefreshIntervalMs: 60000,

  // Features
  features: {
    realTimeUpdates: true,
    configVersioning: true,
    feedbackCollection: true,
    costAnalytics: true,
  },
};
```

### API Route Configuration

```typescript
// packages/api/src/config/knowledge-base.ts
export const kbApiConfig = {
  // Rate limiting
  rateLimits: {
    indexTrigger: { max: 1, windowMs: 60000 },
    contextTest: { max: 100, windowMs: 60000 },
    toolInvoke: { max: 50, windowMs: 60000 },
  },

  // Timeouts
  timeouts: {
    indexOperation: 300000,
    vectorSearch: 10000,
    ragQuery: 15000,
    mcpToolInvoke: 30000,
    contextTest: 20000,
  },

  // Caching
  cache: {
    statusTtlMs: 5000,
    configTtlMs: 60000,
    metricsTtlMs: 30000,
  },
};
```

---

## Success Metrics

| Metric | Target | Measurement |
|--------|--------|-------------|
| Dashboard load time | < 2s | Performance monitoring |
| Real-time update latency | < 1s | SSE event timing |
| Configuration save time | < 5s | API response time |
| Context test execution | < 3s | E2E timing |
| Component test coverage | > 80% | Jest coverage report |
| E2E test pass rate | > 95% | Playwright results |
| Accessibility score | > 90 | Lighthouse audit |
| User satisfaction | > 4/5 | Usability survey |

---

## Risk Mitigation

### Technical Risks

| Risk | Mitigation |
|------|------------|
| Backend services not ready | Use mock services with realistic data |
| Real-time updates complexity | Start with polling, add SSE incrementally |
| Large data volumes in UI | Implement virtualization and pagination |
| Cross-browser compatibility | Test on Chrome, Firefox, Safari early |

### Implementation Risks

| Risk | Mitigation |
|------|------------|
| Scope creep | Fixed phase deliverables, strict prioritization |
| Integration delays | Parallel development with mock services |
| Performance issues | Performance testing at each phase |
| UX complexity | Early user testing, iterative design |

---

## Appendix: API Endpoint Summary

### Index Management
- `GET /api/knowledge-base/index/status` - Get current indexing status
- `POST /api/knowledge-base/index/trigger` - Trigger manual re-index
- `DELETE /api/knowledge-base/index/cancel` - Cancel running index
- `GET /api/knowledge-base/index/history` - Get indexing history
- `GET /api/knowledge-base/index/config` - Get index configuration
- `PUT /api/knowledge-base/index/config` - Update index configuration

### Vector Database
- `GET /api/knowledge-base/vector-db/collections` - List collections
- `POST /api/knowledge-base/vector-db/collections` - Create collection
- `GET /api/knowledge-base/vector-db/collections/:name/stats` - Get collection stats
- `DELETE /api/knowledge-base/vector-db/collections/:name` - Delete collection
- `POST /api/knowledge-base/vector-db/search` - Test similarity search
- `GET /api/knowledge-base/vector-db/storage` - Get storage usage

### RAG Pipeline
- `GET /api/knowledge-base/rag/config` - Get RAG configuration
- `PUT /api/knowledge-base/rag/config` - Update RAG configuration
- `GET /api/knowledge-base/rag/metrics` - Get RAG metrics
- `POST /api/knowledge-base/rag/test` - Test RAG query

### MCP Servers
- `GET /api/knowledge-base/mcp/servers` - List all servers
- `GET /api/knowledge-base/mcp/servers/:name` - Get server status
- `POST /api/knowledge-base/mcp/servers/:name/start` - Start server
- `POST /api/knowledge-base/mcp/servers/:name/stop` - Stop server
- `POST /api/knowledge-base/mcp/servers/:name/restart` - Restart server
- `GET /api/knowledge-base/mcp/servers/:name/tools` - List server tools
- `POST /api/knowledge-base/mcp/servers/:name/tools/:tool/invoke` - Invoke tool
- `GET /api/knowledge-base/mcp/servers/:name/logs` - Get server logs

### Context Testing
- `POST /api/knowledge-base/context/test` - Test context retrieval
- `POST /api/knowledge-base/context/feedback` - Submit relevance feedback
- `GET /api/knowledge-base/context/history` - Get test history

### Analytics
- `GET /api/knowledge-base/analytics/usage` - Get usage analytics
- `GET /api/knowledge-base/analytics/quality` - Get quality metrics
- `GET /api/knowledge-base/analytics/costs` - Get cost breakdown
