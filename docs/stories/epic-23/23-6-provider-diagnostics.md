# Story 23-6: Provider Diagnostics (Deep)

Status: planned

## Summary

Build a deep diagnostics screen for AI providers showing per-provider latency histograms, error classification, token usage analytics, model availability, cost comparison charts, and detailed API call logs. This extends the existing ProviderHealthPage with production-grade observability.

## Acceptance Criteria

### Provider Overview Grid

1. The page shows a card grid of all configured providers:
   - Providers from `IAgentsConfig.defaults.providerChain` and all role overrides
   - Providers from `PROVIDER_TYPES` constants that have been used (from diagnostics events)
2. Each provider card shows:
   - Provider name and icon
   - Health status: healthy (green), degraded (yellow), unhealthy (red)
   - Circuit breaker state: closed / open / half-open (from ProviderHealthTracker)
   - Request count last hour
   - Error count last hour
   - Average latency last hour
   - Total cost last 24h
   - Current model
3. Cards are sortable by: name, health, error rate, cost, latency.

### Latency Histogram

4. For each provider, a latency histogram shows:
   - X-axis: latency buckets (0-50ms, 50-100ms, 100-200ms, 200-500ms, 500ms-1s, 1s-2s, 2s-5s, 5s+)
   - Y-axis: request count per bucket
   - Color-coded: green (<200ms), yellow (200ms-1s), red (>1s)
   - Overlaid percentile lines: p50 (blue dashed), p95 (orange dashed), p99 (red dashed)
   - Time range selector (Last 1h, 6h, 24h, 7d)
5. A comparison mode shows multiple providers' histograms side-by-side.

### Error Classification

6. For each provider, an error breakdown table shows:
   - Error type: auth (INVALID_API_KEY), rate limit (RATE_LIMIT_EXCEEDED), timeout (TIMEOUT), network (NETWORK_ERROR), server (PROVIDER_ERROR, SERVICE_UNAVAILABLE), context (CONTEXT_TOO_LONG), quota (QUOTA_EXCEEDED)
   - Count per error type
   - Percentage of total errors
   - Last occurrence timestamp
   - Sample error message (from most recent occurrence)
   - Retryable flag (yes/no)
7. An error trend chart shows errors over time, stacked by error type.
8. A "Recent Errors" panel shows the last 20 errors with:
   - Timestamp, provider, model, error code, message, latency, retryable flag
   - Expandable detail with full error context

### Token Usage Analytics

9. Per provider, a token usage panel shows:
   - Total input tokens (today / this week / this month)
   - Total output tokens (today / this week / this month)
   - Cache read tokens (if supported)
   - Cache write tokens (if supported)
   - Average tokens per request: input / output
   - Token efficiency: output/input ratio
10. A token usage chart shows daily token consumption over the selected time range:
    - Stacked area: input tokens (blue), output tokens (orange), cached tokens (green)
    - Overlaid line: cost per day
11. Per-model token usage breakdown (if provider supports multiple models).

### Model Availability Matrix

12. A matrix table shows:
    - Rows: providers
    - Columns: model IDs
    - Cells: available (green check), unavailable (red X), unknown (gray dash)
    - Data sourced from provider `getModels()` / `getCapabilities()` where available
    - "Last checked" timestamp per cell
13. A "Refresh" button triggers a live check of model availability across all providers.

### Provider Comparison Chart

14. A comparison view allows selecting 2-4 providers and shows:
    - Cost per 1M input tokens (bar chart)
    - Cost per 1M output tokens (bar chart)
    - Average latency (bar chart)
    - Error rate percentage (bar chart)
    - Success rate percentage (bar chart)
    - Token throughput (tokens per second, bar chart)
15. A radar chart overlays all dimensions for visual comparison.

### Rate Limit Dashboard

16. Per provider:
    - Current request rate (requests/minute)
    - Rate limit ceiling (from response headers or provider docs)
    - Percentage used (progress bar)
    - Rate limit reset countdown (if available)
    - Historical rate limit hits per hour (bar chart, last 24h)
17. Rate-limited requests are counted separately from errors.

### API Call Log

18. A detailed API call log (last 100 calls per provider) shows:
    - Timestamp, provider, model, duration, input tokens, output tokens, cost, success/failure
    - Expandable to show:
      - Request metadata (traceId, issueId, agentType, taskType)
      - Response finish reason
      - Error details (if failed)
    - Filterable by: provider, model, success/failure, latency range
    - Sortable by any column
19. Call log data comes from DiagnosticsService events.

## API Endpoints Needed

- GET /api/monitoring/providers/overview -- returns per-provider summary (health, stats, cost)
- GET /api/monitoring/providers/:name/latency -- returns latency histogram data
- GET /api/monitoring/providers/:name/errors -- returns error classification breakdown
- GET /api/monitoring/providers/:name/errors/recent -- returns last N errors with details
- GET /api/monitoring/providers/:name/errors/trend -- returns error count over time by type
- GET /api/monitoring/providers/:name/tokens -- returns token usage analytics
- GET /api/monitoring/providers/:name/tokens/trend -- returns daily token usage time series
- GET /api/monitoring/providers/models -- returns model availability matrix
- POST /api/monitoring/providers/models/refresh -- triggers live model availability check
- GET /api/monitoring/providers/compare -- query params: `providers[]`, returns comparison data
- GET /api/monitoring/providers/:name/rate-limits -- returns rate limit status
- GET /api/monitoring/providers/:name/rate-limits/history -- returns historical rate limit hits
- GET /api/monitoring/providers/calls -- returns API call log, query params: `provider`, `model`, `success`, `limit`, `since`

## Dashboard Components

- `ProviderDiagnosticsPage` -- page container with tabs
- `ProviderOverviewGrid` -- grid of provider summary cards
- `ProviderCard` -- single provider card with stats
- `LatencyHistogram` -- SVG histogram with percentile lines
- `LatencyComparisonView` -- side-by-side histograms
- `ErrorClassificationTable` -- error type breakdown
- `ErrorTrendChart` -- errors over time by type
- `RecentErrorsPanel` -- last N errors with expandable detail
- `TokenUsagePanel` -- token counts with daily/weekly/monthly
- `TokenUsageChart` -- stacked area chart of token consumption
- `ModelAvailabilityMatrix` -- provider x model availability grid
- `ProviderComparisonChart` -- bar chart comparison
- `ProviderRadarChart` -- radar chart overlay
- `RateLimitPanel` -- per-provider rate limit status
- `RateLimitHistoryChart` -- historical rate limit hits
- `ApiCallLog` -- detailed call log table

## Data Sources

- ProviderHealthTracker.getStatus() (existing) -- circuit breaker state
- DiagnosticsService.getEvents() (existing) -- tool/provider events with tokens, latency, errors
- CostTracker.getAggregate() (existing) -- cost per provider/model
- CostTracker.getUsage() (existing) -- individual usage records for call log
- IAgentsConfig (existing) -- configured provider chains
- PROVIDER_ERROR_CODES (existing) -- error classification categories

## Implementation Notes

- Latency histogram bins are computed in the time-buckets utility from story 23-11.
- Error classification maps ProviderError.code to human-readable categories.
- Token usage analytics are aggregated from CostTracker usage records (which already track input/output/cache tokens).
- Model availability: for providers that implement `getModels()` (IAIProvider), call it. For CLI agents, report capability flags from `CLIAgentCapabilities`.
- Rate limit extraction: add a hook to the diagnostics event recording that captures `x-ratelimit-*` headers from provider responses. Store in DiagnosticsService alongside existing events.
- API call log is a view over DiagnosticsService events of type `provider:complete` and `provider:error`.
- Comparison data is computed by the server: query CostTracker for each provider and aggregate.

## Files to Create

- `packages/api/src/routes/monitoring/provider-routes.ts`
- `packages/api/src/services/monitoring/provider-diagnostics-service.ts`
- `packages/api/src/services/monitoring/model-availability-service.ts`
- `packages/dashboard/src/pages/monitoring/ProviderDiagnosticsPage.tsx`
- `packages/dashboard/src/components/monitoring/providers/ProviderOverviewGrid.tsx`
- `packages/dashboard/src/components/monitoring/providers/ProviderCard.tsx`
- `packages/dashboard/src/components/monitoring/providers/LatencyHistogram.tsx`
- `packages/dashboard/src/components/monitoring/providers/ErrorClassificationTable.tsx`
- `packages/dashboard/src/components/monitoring/providers/ErrorTrendChart.tsx`
- `packages/dashboard/src/components/monitoring/providers/RecentErrorsPanel.tsx`
- `packages/dashboard/src/components/monitoring/providers/TokenUsagePanel.tsx`
- `packages/dashboard/src/components/monitoring/providers/TokenUsageChart.tsx`
- `packages/dashboard/src/components/monitoring/providers/ModelAvailabilityMatrix.tsx`
- `packages/dashboard/src/components/monitoring/providers/ProviderComparisonChart.tsx`
- `packages/dashboard/src/components/monitoring/providers/ProviderRadarChart.tsx`
- `packages/dashboard/src/components/monitoring/providers/RateLimitPanel.tsx`
- `packages/dashboard/src/components/monitoring/providers/ApiCallLog.tsx`
- `packages/dashboard/src/hooks/monitoring/useProviderDiagnostics.ts`
- Tests for all API routes, services, and components
