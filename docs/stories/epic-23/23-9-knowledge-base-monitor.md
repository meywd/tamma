# Story 23-9: Knowledge Base Monitor

Status: planned

## Summary

Build a monitoring screen for the knowledge base infrastructure: vector DB collections health, document counts and embedding coverage, index freshness, RAG pipeline health with retrieval latency and relevance scores, MCP server connection status, and context window utilization per agent.

## Acceptance Criteria

### Vector DB Collections Status

1. A table shows all vector DB collections (from existing `/api/knowledge-base/vector-db/collections`):
   - Collection name
   - Document count
   - Embedding dimensions
   - Distance metric (cosine, euclidean, dot_product)
   - Status: healthy (green), degraded (yellow), unavailable (red)
   - Storage size estimate
   - Last modified timestamp
2. Health status is determined by: collection exists AND responds to a test query within 5 seconds.
3. Per-collection detail (expandable):
   - Sample documents (first 5, with truncated content)
   - Embedding distribution stats (mean, std dev of vector norms)
   - Query latency: avg, p50, p95, p99 from recent queries
   - Creation date and age

### Document Count & Embedding Coverage

4. A summary panel shows:
   - Total documents across all collections
   - Total embeddings stored
   - Embedding coverage percentage: (indexed documents / total discoverable files) * 100
   - Files pending indexing: count and list (first 20)
   - Files failed to index: count, list with error messages
   - Last full index run: timestamp and duration
5. A breakdown by file type:
   - Rows: .ts, .tsx, .js, .json, .md, .yml, other
   - Columns: total files, indexed files, pending files, coverage %
6. An "Index Coverage Map" tree view shows the repository directory structure:
   - Green: fully indexed directories
   - Yellow: partially indexed
   - Red: not indexed
   - Gray: excluded (from .gitignore or index config patterns)

### Index Freshness

7. An index freshness panel shows:
   - Last indexed timestamp (global and per-collection)
   - Age of the oldest un-indexed change (since last git commit that modified indexed files)
   - Pending document count
   - Index lag: time between file modification and embedding generation
   - Auto-index status: enabled/disabled, trigger type (file watcher, git hook, scheduler)
   - Next scheduled index run (if scheduler is active)
8. An index freshness chart shows:
   - X-axis: time (last 7 days)
   - Y-axis: index lag in minutes
   - Threshold line at acceptable lag (e.g., 30 minutes)
   - Points above threshold highlighted in red

### RAG Pipeline Health

9. A RAG pipeline status panel shows:
   - Pipeline status: active / inactive / error
   - Retrieval latency: avg, p50, p95, p99
   - Retrieval count per hour (last 24h)
   - Average relevance score of retrieved chunks (0.0-1.0)
   - Cache hit rate for query cache
   - Source breakdown: vector DB, keyword, docs, GitHub (counts per source type)
10. A RAG quality metrics panel shows:
    - Average context chunk count per query
    - Average token budget utilization per query
    - Feedback scores (if feedback is collected): thumbs up/down ratio
    - Retrieval diversity: average number of unique files per query
11. A RAG latency chart shows:
    - Time series of retrieval latency (avg per hour, last 24h)
    - Stacked by RAG stage: query processing, vector search, reranking, assembly
12. A "Test RAG" interface (reusing existing RAG test from KB dashboard):
    - Input: query text
    - Output: retrieved chunks with relevance scores, sources, latency
    - Allows comparing different retrieval strategies

### MCP Server Connections

13. A table of MCP server connections shows:
    - Server name
    - Connection status: connected (green), disconnected (red), connecting (yellow)
    - Server URL / transport type
    - Available tools count
    - Last communication timestamp
    - Latency to server (ms)
    - Error count in last hour
14. Per-server detail (expandable):
    - Full tool list with: name, description, schema summary
    - Recent invocations: last 10 tool calls with latency and success/failure
    - Server capabilities (resources, prompts, tools)
    - Connection uptime since last connect
15. A "Reconnect" button per server that forces a reconnection attempt.
16. A "Test Tool" button that invokes a selected tool with test inputs (reusing existing ToolInvokePanel).

### Context Window Utilization

17. A per-agent context utilization panel shows:
    - Agent role name
    - Average context window usage: tokens used / max tokens (percentage bar)
    - Context budget allocation: how tokens are split across sources (vector DB, RAG, MCP, web search)
    - Budget overflow count: times the context exceeded the token limit (requiring truncation)
    - Context assembly latency: avg time to build the full context
18. A stacked bar chart shows context window allocation per agent:
    - X-axis: agent roles
    - Y-axis: token count
    - Segments: system prompt, issue context, RAG context, vector DB context, MCP context, web search context
    - Max line showing the context window limit
19. A context freshness indicator:
    - How recently each context source was updated
    - Stale sources (>24h old) flagged yellow; >7d flagged red

## API Endpoints Needed

- GET /api/monitoring/kb/vector-db/status -- vector DB collections with health status
- GET /api/monitoring/kb/vector-db/:collection/detail -- collection detail with samples and stats
- GET /api/monitoring/kb/coverage -- document count, embedding coverage, pending/failed files
- GET /api/monitoring/kb/coverage/by-type -- coverage breakdown by file type
- GET /api/monitoring/kb/coverage/tree -- directory tree with index coverage
- GET /api/monitoring/kb/freshness -- index freshness metrics
- GET /api/monitoring/kb/freshness/trend -- index lag time series
- GET /api/monitoring/kb/rag/status -- RAG pipeline health metrics
- GET /api/monitoring/kb/rag/quality -- RAG quality metrics (relevance, diversity, feedback)
- GET /api/monitoring/kb/rag/latency -- RAG latency breakdown time series
- GET /api/monitoring/kb/mcp/servers -- MCP server connection status
- GET /api/monitoring/kb/mcp/:server/detail -- single MCP server detail with tools and recent invocations
- POST /api/monitoring/kb/mcp/:server/reconnect -- force MCP server reconnection
- GET /api/monitoring/kb/context/utilization -- per-agent context window usage
- GET /api/monitoring/kb/context/allocation -- context budget allocation breakdown

## Dashboard Components

- `KnowledgeBaseMonitorPage` -- page container with tabs
- `VectorDBStatusTable` -- collection status table
- `VectorDBCollectionDetail` -- expandable collection detail
- `DocumentCoverageSummary` -- total documents, embeddings, coverage %
- `CoverageByFileType` -- file type breakdown table
- `IndexCoverageMap` -- directory tree with color-coded coverage
- `IndexFreshnessPanel` -- freshness metrics with age indicators
- `IndexFreshnessChart` -- index lag time series
- `RAGPipelineStatus` -- pipeline health metrics
- `RAGQualityMetrics` -- relevance, diversity, feedback scores
- `RAGLatencyChart` -- latency breakdown time series
- `MCPServerTable` -- MCP connection status table
- `MCPServerDetail` -- expandable server detail with tools
- `ContextUtilizationPanel` -- per-agent context usage
- `ContextAllocationChart` -- stacked bar chart of context window allocation
- `ContextFreshnessIndicator` -- source freshness indicators

## Data Sources

- VectorDBManagementService (existing) -- collections, stats, storage, search
- IndexManagementService (existing) -- index status, configuration, history
- RAGManagementService (existing) -- RAG pipeline status, test queries
- MCPManagementService (existing) -- MCP server status, tool lists
- ContextTestingService (existing) -- context assembly testing
- AnalyticsService (existing) -- usage analytics, quality metrics, cost
- CostTracker (existing) -- token usage per agent
- Intelligence package: CodebaseIndexer, ContextAggregator, RAGPipeline

## Implementation Notes

- Most data is available from existing KB service endpoints. This story creates monitoring-specific aggregation views.
- Vector DB health check: call `getCollectionStats()` per collection and verify it responds within 5s.
- Embedding coverage: compare indexed file count from the indexer's metadata against file discovery results.
- Index freshness: compare the latest event timestamp in the index history against the current time.
- RAG latency breakdown: instrument the RAGPipeline to emit timing for each stage (query_processing, vector_search, reranking, assembly). Store in DiagnosticsService events.
- MCP reconnection: call `mcpClient.disconnect()` then `mcpClient.connect()` on the named server.
- Context window utilization: the ContextAggregator already tracks token budget allocation. Expose the running averages.
- Directory tree for index coverage: use the file discovery module to get the repo file tree, then cross-reference with indexed files.

## Files to Create

- `packages/api/src/routes/monitoring/kb-routes.ts`
- `packages/api/src/services/monitoring/kb-status-service.ts`
- `packages/api/src/services/monitoring/index-coverage-service.ts`
- `packages/api/src/services/monitoring/rag-monitor-service.ts`
- `packages/dashboard/src/pages/monitoring/KnowledgeBaseMonitorPage.tsx`
- `packages/dashboard/src/components/monitoring/kb/VectorDBStatusTable.tsx`
- `packages/dashboard/src/components/monitoring/kb/VectorDBCollectionDetail.tsx`
- `packages/dashboard/src/components/monitoring/kb/DocumentCoverageSummary.tsx`
- `packages/dashboard/src/components/monitoring/kb/CoverageByFileType.tsx`
- `packages/dashboard/src/components/monitoring/kb/IndexCoverageMap.tsx`
- `packages/dashboard/src/components/monitoring/kb/IndexFreshnessPanel.tsx`
- `packages/dashboard/src/components/monitoring/kb/IndexFreshnessChart.tsx`
- `packages/dashboard/src/components/monitoring/kb/RAGPipelineStatus.tsx`
- `packages/dashboard/src/components/monitoring/kb/RAGQualityMetrics.tsx`
- `packages/dashboard/src/components/monitoring/kb/RAGLatencyChart.tsx`
- `packages/dashboard/src/components/monitoring/kb/MCPServerTable.tsx`
- `packages/dashboard/src/components/monitoring/kb/MCPServerDetail.tsx`
- `packages/dashboard/src/components/monitoring/kb/ContextUtilizationPanel.tsx`
- `packages/dashboard/src/components/monitoring/kb/ContextAllocationChart.tsx`
- `packages/dashboard/src/hooks/monitoring/useKBMonitor.ts`
- Tests for all API routes, services, and components
