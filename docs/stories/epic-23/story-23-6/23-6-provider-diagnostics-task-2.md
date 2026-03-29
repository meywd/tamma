# Task 2: Provider Diagnostics Frontend Components

**Story:** 23-6-provider-diagnostics
**Epic:** 23

## Task Description

Build the ProviderDiagnosticsPage with provider overview grid, latency histograms, error classification, token usage analytics, model availability matrix, provider comparison charts, rate limit dashboard, and API call log.

## Acceptance Criteria

- Provider card grid with health, circuit breaker state, stats, and cost
- SVG latency histogram with percentile overlay lines and comparison mode
- Error breakdown table with trend chart and recent errors panel
- Token usage panel with daily/weekly/monthly and stacked area chart
- Model availability matrix (provider x model grid)
- Provider comparison bar charts and radar chart
- Rate limit dashboard with progress bars and history
- Detailed API call log table with expandable rows

## Implementation Details

### Technical Requirements

- [ ] Replace placeholder `packages/dashboard/src/pages/monitoring/ProviderDiagnosticsPage.tsx`:
  - MonitoringLayout with title "Provider Diagnostics"
  - Tab navigation: Overview, Latency, Errors, Tokens, Models, Compare, Rate Limits, Call Log

- [ ] Create `packages/dashboard/src/hooks/monitoring/useProviderDiagnostics.ts`

- [ ] Create `packages/dashboard/src/components/monitoring/providers/ProviderOverviewGrid.tsx`:
  - MetricGrid of ProviderCard components
  - Sortable by name, health, error rate, cost, latency

- [ ] Create `packages/dashboard/src/components/monitoring/providers/ProviderCard.tsx`:
  - Provider name + icon, StatusBadge for health
  - Circuit breaker state indicator (closed=green, open=red, half-open=yellow)
  - Stats: request count, error count, avg latency, cost 24h, current model

- [ ] Create `packages/dashboard/src/components/monitoring/providers/LatencyHistogram.tsx`:
  - SVG bar chart: X=latency buckets, Y=request count
  - Color-coded: green (<200ms), yellow (200ms-1s), red (>1s)
  - Percentile overlay lines: p50 (blue dashed), p95 (orange), p99 (red)
  - Time range selector
  - Comparison mode: multiple providers side-by-side

- [ ] Create `packages/dashboard/src/components/monitoring/providers/ErrorClassificationTable.tsx`:
  - DataTable: Error Type, Human Label, Count, Percentage, Last Occurrence, Sample Message, Retryable
  - Color-coded severity

- [ ] Create `packages/dashboard/src/components/monitoring/providers/ErrorTrendChart.tsx`:
  - TimeSeriesChart: errors over time, stacked by error type

- [ ] Create `packages/dashboard/src/components/monitoring/providers/RecentErrorsPanel.tsx`:
  - Last 20 errors with expandable detail (full error context)
  - Columns: Timestamp, Provider, Model, Error Code, Message, Latency, Retryable

- [ ] Create `packages/dashboard/src/components/monitoring/providers/TokenUsagePanel.tsx`:
  - Input tokens, output tokens, cache read/write tokens (today/week/month)
  - Average per request, token efficiency ratio

- [ ] Create `packages/dashboard/src/components/monitoring/providers/TokenUsageChart.tsx`:
  - TimeSeriesChart stacked area: input (blue), output (orange), cached (green)
  - Overlaid line: cost per day

- [ ] Create `packages/dashboard/src/components/monitoring/providers/ModelAvailabilityMatrix.tsx`:
  - Grid: rows=providers, columns=models
  - Cells: green check (available), red X (unavailable), gray dash (unknown)
  - "Last checked" tooltip per cell
  - "Refresh" button triggers live check

- [ ] Create `packages/dashboard/src/components/monitoring/providers/ProviderComparisonChart.tsx`:
  - Select 2-4 providers via multi-select
  - Bar charts: cost/1M input, cost/1M output, avg latency, error rate, success rate, throughput
  - Side-by-side grouped bars

- [ ] Create `packages/dashboard/src/components/monitoring/providers/ProviderRadarChart.tsx`:
  - SVG radar/spider chart overlaying all dimensions
  - One polygon per selected provider, semi-transparent fill

- [ ] Create `packages/dashboard/src/components/monitoring/providers/RateLimitPanel.tsx`:
  - Per provider: current rate, ceiling, ProgressRing, reset countdown
  - Historical rate limit hits per hour bar chart

- [ ] Create `packages/dashboard/src/components/monitoring/providers/ApiCallLog.tsx`:
  - DataTable: Timestamp, Provider, Model, Duration, Input Tokens, Output Tokens, Cost, Success
  - Expandable row: traceId, issueId, agentType, taskType, finishReason, error details
  - Filterable by provider, model, success/failure, latency range

- [ ] Create `packages/dashboard/src/services/monitoring/provider-api-client.ts`

### Files to Create

- CREATE `packages/dashboard/src/components/monitoring/providers/ProviderOverviewGrid.tsx`
- CREATE `packages/dashboard/src/components/monitoring/providers/ProviderCard.tsx`
- CREATE `packages/dashboard/src/components/monitoring/providers/LatencyHistogram.tsx`
- CREATE `packages/dashboard/src/components/monitoring/providers/ErrorClassificationTable.tsx`
- CREATE `packages/dashboard/src/components/monitoring/providers/ErrorTrendChart.tsx`
- CREATE `packages/dashboard/src/components/monitoring/providers/RecentErrorsPanel.tsx`
- CREATE `packages/dashboard/src/components/monitoring/providers/TokenUsagePanel.tsx`
- CREATE `packages/dashboard/src/components/monitoring/providers/TokenUsageChart.tsx`
- CREATE `packages/dashboard/src/components/monitoring/providers/ModelAvailabilityMatrix.tsx`
- CREATE `packages/dashboard/src/components/monitoring/providers/ProviderComparisonChart.tsx`
- CREATE `packages/dashboard/src/components/monitoring/providers/ProviderRadarChart.tsx`
- CREATE `packages/dashboard/src/components/monitoring/providers/RateLimitPanel.tsx`
- CREATE `packages/dashboard/src/components/monitoring/providers/ApiCallLog.tsx`
- CREATE `packages/dashboard/src/hooks/monitoring/useProviderDiagnostics.ts`
- CREATE `packages/dashboard/src/services/monitoring/provider-api-client.ts`

### Files to Modify

- MODIFY `packages/dashboard/src/pages/monitoring/ProviderDiagnosticsPage.tsx` -- replace placeholder

### Dependencies

- Story 23-12: MonitoringLayout, MetricCard, MetricGrid, StatusBadge, DataTable, TimeSeriesChart, ProgressRing, LatencyBar
- Task 1: Provider diagnostics API endpoints

## Testing Strategy

### Unit Tests

- [ ] ProviderCard: renders correct health status and circuit state
- [ ] LatencyHistogram: renders SVG bars with correct heights
- [ ] LatencyHistogram: percentile lines positioned correctly
- [ ] ErrorClassificationTable: sorts and filters correctly
- [ ] TokenUsageChart: renders stacked area with overlaid line
- [ ] ModelAvailabilityMatrix: renders grid with correct symbols
- [ ] ProviderComparisonChart: renders grouped bars for selected providers
- [ ] ProviderRadarChart: renders SVG polygon per provider
- [ ] ApiCallLog: expandable rows show full details
- [ ] useProviderDiagnostics: fetches overview on mount

## Completion Checklist

- [ ] All 13 child components created
- [ ] 8-tab navigation
- [ ] SVG histogram and radar charts
- [ ] Model availability matrix with refresh
- [ ] Provider comparison tool
- [ ] API call log with filters
- [ ] Tests written and passing
- [ ] TypeScript strict mode compiles
