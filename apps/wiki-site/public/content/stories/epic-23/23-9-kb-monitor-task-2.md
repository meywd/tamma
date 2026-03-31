---
title: "Task 2: Knowledge Base Monitor Frontend Components"
sidebar:
  order: 230
---

**Story:** 23-9-knowledge-base-monitor
**Epic:** 23

## Task Description

Build the KnowledgeBaseMonitorPage with tabs for Vector DB, Coverage, Freshness, RAG Pipeline, MCP Servers, and Context Window. Provides deep insight into the knowledge infrastructure health.

## Acceptance Criteria

- Vector DB collection status table with health indicators and expandable detail
- Document coverage summary with file type breakdown and directory tree map
- Index freshness panel with lag chart and auto-index status
- RAG pipeline health with latency breakdown and quality metrics
- MCP server table with connection status, tools, and reconnect button
- Context window utilization panel with stacked bar allocation chart

## Implementation Details

### Technical Requirements

- [ ] Replace placeholder `packages/dashboard/src/pages/monitoring/KnowledgeBaseMonitorPage.tsx`:
  - MonitoringLayout with title "Knowledge Base Monitor"
  - Tab navigation: Vector DB, Coverage, Freshness, RAG Pipeline, MCP, Context

- [ ] Create `packages/dashboard/src/hooks/monitoring/useKBMonitor.ts`

- [ ] Create `packages/dashboard/src/components/monitoring/kb/VectorDBStatusTable.tsx`:
  - DataTable: Collection, Documents, Dimensions, Distance Metric, Status, Size, Last Modified
  - StatusBadge per collection
  - Row click expands VectorDBCollectionDetail

- [ ] Create `packages/dashboard/src/components/monitoring/kb/VectorDBCollectionDetail.tsx`:
  - Sample documents (first 5, truncated content)
  - Embedding distribution stats
  - Query latency: avg/p50/p95/p99 using LatencyBar
  - Creation date

- [ ] Create `packages/dashboard/src/components/monitoring/kb/DocumentCoverageSummary.tsx`:
  - MetricCards: Total Documents, Total Embeddings, Coverage %, Pending, Failed
  - ProgressRing for coverage percentage
  - Pending/failed file lists (first 20 each)
  - Last full index run timestamp and duration

- [ ] Create `packages/dashboard/src/components/monitoring/kb/CoverageByFileType.tsx`:
  - DataTable: File Type, Total, Indexed, Pending, Coverage %
  - ProgressRing per row for coverage

- [ ] Create `packages/dashboard/src/components/monitoring/kb/IndexCoverageMap.tsx`:
  - Tree view showing repository directory structure
  - Color-coded: green=fully indexed, yellow=partial, red=not indexed, gray=excluded
  - Expandable directories
  - Tooltip with indexed file count

- [ ] Create `packages/dashboard/src/components/monitoring/kb/IndexFreshnessPanel.tsx`:
  - Last indexed timestamp (global and per collection)
  - Oldest un-indexed change age
  - Pending document count
  - Index lag value
  - Auto-index: enabled/disabled badge, trigger type, next scheduled run
  - IndexFreshnessChart below

- [ ] Create `packages/dashboard/src/components/monitoring/kb/IndexFreshnessChart.tsx`:
  - TimeSeriesChart: X=time (last 7 days), Y=index lag in minutes
  - Threshold line at 30 minutes
  - Points above threshold highlighted in red

- [ ] Create `packages/dashboard/src/components/monitoring/kb/RAGPipelineStatus.tsx`:
  - StatusBadge for pipeline (active/inactive/error)
  - MetricCards: Retrieval Latency, Retrievals/Hour, Avg Relevance, Cache Hit Rate
  - Source breakdown: pie chart or bar (vector_db, keyword, docs, github)

- [ ] Create `packages/dashboard/src/components/monitoring/kb/RAGQualityMetrics.tsx`:
  - Avg chunk count per query, avg token budget utilization
  - Retrieval diversity, feedback scores (thumbs up/down ratio)

- [ ] Create `packages/dashboard/src/components/monitoring/kb/RAGLatencyChart.tsx`:
  - TimeSeriesChart stacked area: query_processing, vector_search, reranking, assembly
  - Shows per-stage contribution to total latency

- [ ] Create `packages/dashboard/src/components/monitoring/kb/MCPServerTable.tsx`:
  - DataTable: Server, Status, URL, Transport, Tools, Last Communication, Latency, Errors
  - StatusBadge: connected=green, disconnected=red, connecting=yellow
  - "Reconnect" button per server
  - Row click expands MCPServerDetail

- [ ] Create `packages/dashboard/src/components/monitoring/kb/MCPServerDetail.tsx`:
  - Full tool list: name, description, schema summary
  - Recent invocations: last 10 tool calls with latency and success
  - Server capabilities
  - Uptime since last connect

- [ ] Create `packages/dashboard/src/components/monitoring/kb/ContextUtilizationPanel.tsx`:
  - Per agent: ProgressRing for token usage %, overflow count, assembly latency
  - DataTable: Agent, Avg Tokens Used, Max Tokens, Usage %, Overflow Count

- [ ] Create `packages/dashboard/src/components/monitoring/kb/ContextAllocationChart.tsx`:
  - Stacked bar chart: X=agent roles, Y=token count
  - Segments: system prompt, issue context, RAG, vector DB, MCP, web search
  - Max line showing context window limit
  - ContextFreshnessIndicator below each bar

- [ ] Create `packages/dashboard/src/services/monitoring/kb-api-client.ts`

### Files to Create

- CREATE `packages/dashboard/src/components/monitoring/kb/VectorDBStatusTable.tsx`
- CREATE `packages/dashboard/src/components/monitoring/kb/VectorDBCollectionDetail.tsx`
- CREATE `packages/dashboard/src/components/monitoring/kb/DocumentCoverageSummary.tsx`
- CREATE `packages/dashboard/src/components/monitoring/kb/CoverageByFileType.tsx`
- CREATE `packages/dashboard/src/components/monitoring/kb/IndexCoverageMap.tsx`
- CREATE `packages/dashboard/src/components/monitoring/kb/IndexFreshnessPanel.tsx`
- CREATE `packages/dashboard/src/components/monitoring/kb/IndexFreshnessChart.tsx`
- CREATE `packages/dashboard/src/components/monitoring/kb/RAGPipelineStatus.tsx`
- CREATE `packages/dashboard/src/components/monitoring/kb/RAGQualityMetrics.tsx`
- CREATE `packages/dashboard/src/components/monitoring/kb/RAGLatencyChart.tsx`
- CREATE `packages/dashboard/src/components/monitoring/kb/MCPServerTable.tsx`
- CREATE `packages/dashboard/src/components/monitoring/kb/MCPServerDetail.tsx`
- CREATE `packages/dashboard/src/components/monitoring/kb/ContextUtilizationPanel.tsx`
- CREATE `packages/dashboard/src/components/monitoring/kb/ContextAllocationChart.tsx`
- CREATE `packages/dashboard/src/hooks/monitoring/useKBMonitor.ts`
- CREATE `packages/dashboard/src/services/monitoring/kb-api-client.ts`

### Files to Modify

- MODIFY `packages/dashboard/src/pages/monitoring/KnowledgeBaseMonitorPage.tsx` -- replace placeholder

### Dependencies

- Story 23-12: MonitoringLayout, MetricCard, MetricGrid, DataTable, TimeSeriesChart, ProgressRing, LatencyBar, StatusBadge, EmptyState
- Task 1: KB monitoring API endpoints

## Testing Strategy

### Unit Tests

- [ ] VectorDBStatusTable: renders collections with correct status badges
- [ ] DocumentCoverageSummary: coverage percentage ProgressRing
- [ ] CoverageByFileType: renders per-type breakdowns
- [ ] IndexCoverageMap: tree renders with correct colors
- [ ] IndexFreshnessChart: threshold line at 30 minutes
- [ ] RAGPipelineStatus: shows active/inactive/error status
- [ ] RAGLatencyChart: stacked area sums to total latency
- [ ] MCPServerTable: reconnect button calls API
- [ ] MCPServerDetail: tool list renders
- [ ] ContextUtilizationPanel: overflow count highlighted
- [ ] ContextAllocationChart: stacked bars sum correctly
- [ ] useKBMonitor: fetches per-tab data

## Completion Checklist

- [ ] All 14 child components created
- [ ] 6-tab navigation
- [ ] Vector DB health with detail expansion
- [ ] Coverage map with directory tree
- [ ] RAG pipeline monitoring
- [ ] MCP server management
- [ ] Context window visualization
- [ ] Tests written and passing
- [ ] TypeScript strict mode compiles
