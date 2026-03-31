# Story 23-7: Log Explorer (Connected to OpenSearch)

Status: planned

## Summary

Build a log exploration screen with live log tailing, full-text search using Lucene syntax via OpenSearch, filtering by service/level/workflow/issue, log level distribution charts, error drill-down with stack traces, saved searches, and configurable alert rules.

## Acceptance Criteria

### Live Log Tail

1. The page has a "Live Tail" mode that streams new log entries in real-time:
   - Connects to an SSE endpoint that forwards Pino structured log output
   - Each log line shows: timestamp, level (color-coded), service name, message, context fields
   - Level colors: DEBUG (gray), INFO (blue), WARN (yellow), ERROR (red)
   - Auto-scrolls to bottom as new entries arrive
   - "Pause" button freezes the scroll without disconnecting
   - "Clear" button clears the visible buffer (does not delete logs)
   - Buffer size: displays last 1000 lines, older lines are discarded from DOM
2. Clicking a log line expands the full JSON context:
   - All Pino fields: level, time, pid, hostname, msg, plus custom fields
   - Custom fields formatted as key-value pairs with syntax highlighting
   - Stack traces (if present in `err.stack`) rendered with monospace font and clickable file paths

### Full-Text Search

3. A search bar supports Lucene query syntax (powered by OpenSearch):
   - Simple terms: `error connection`
   - Phrases: `"connection refused"`
   - Field-specific: `service:orchestrator level:error`
   - Boolean: `error AND (timeout OR connection)`
   - Wildcards: `issue:42*`
   - Range: `time:[2026-03-29T00:00:00 TO 2026-03-29T23:59:59]`
4. Search hits are highlighted in the log output.
5. A "Search History" dropdown shows the last 20 searches.
6. Search results show: total hit count, time taken, and paginated results.

### Filtering

7. Filter panel with:
   - Service: multi-select (orchestrator, api, dashboard, providers, intelligence, events, cost-monitor)
   - Level: multi-select (debug, info, warn, error)
   - Workflow/Engine ID: text input
   - Issue number: numeric input
   - Time range: presets (Last 15m, 1h, 6h, 24h, 7d) + custom
   - Contains field: arbitrary key-value pair (e.g., `eventType:CODE.GENERATED.SUCCESS`)
8. Filters are composable with AND logic.
9. Active filters shown as removable chips.
10. Filters persist in URL query params.

### Log Level Distribution

11. A collapsible chart panel shows:
    - Donut chart: percentage of logs by level (debug/info/warn/error)
    - Time series: log count per level over the selected time range (stacked area)
    - Error spike detection: red markers on the time series where error rate exceeds 2x the rolling average
12. The distribution updates when filters change.

### Error Log Drill-Down

13. An "Errors" tab shows only error-level logs with enriched detail:
    - Error message
    - Full stack trace (collapsible, with code context if available)
    - Service that generated the error
    - Issue number and workflow context (if present)
    - Related log entries (5 before and 5 after the error, from the same service)
    - Error frequency: count of identical error messages in last hour
    - First/last occurrence timestamps
14. Error grouping: identical error messages are grouped, showing count and expandable list of occurrences.

### OpenSearch Integration

15. The log explorer queries OpenSearch via the Tamma API (not directly from the browser):
    - API proxies search requests to OpenSearch with proper authentication
    - Index pattern: `tamma-logs-*` (one index per day)
    - Mappings match Pino structured output: `level`, `time`, `service`, `msg`, `issueNumber`, `eventType`, etc.
16. A "View in OpenSearch Dashboards" link opens the corresponding query in OpenSearch Dashboards (if available at the configured URL).
17. If OpenSearch is unavailable, the log explorer falls back to in-memory log buffer from the API server.

### Saved Searches

18. Users can save search queries with a name and optional description:
    - Saved searches are stored per-user in the user store
    - A sidebar lists saved searches with: name, query, created date, last used
    - Clicking a saved search loads the query and filters
    - "Delete" and "Edit" actions on each saved search
19. Saved searches support an optional "Pin to dashboard" flag that shows them as quick-access buttons.

### Alert Rules

20. Users can create alert rules for log patterns:
    - Rule definition: name, query (Lucene syntax), threshold (count per time window), severity (info/warning/critical)
    - Time windows: 1min, 5min, 15min, 1hr
    - Action: send notification via existing AlertManager channels (cli, webhook, slack)
    - Enable/disable toggle per rule
21. Alert rules are evaluated by a background check that runs every 30 seconds.
22. An "Active Alerts" panel shows currently triggered log alerts with: rule name, trigger count, last triggered, acknowledge button.
23. Alert rules are stored in-memory with optional PostgreSQL persistence.

## API Endpoints Needed

- GET /api/monitoring/logs/search -- proxied OpenSearch query, params: `q` (Lucene), `service[]`, `level[]`, `engineId`, `issueNumber`, `since`, `until`, `page`, `pageSize`
- GET /api/monitoring/logs/stream -- SSE stream of new log entries, params: `service[]`, `level[]`
- GET /api/monitoring/logs/distribution -- log level distribution, params: `since`, `until`, `bucketSize`
- GET /api/monitoring/logs/errors -- error-only logs with grouping, params: `since`, `until`, `service`, `limit`
- GET /api/monitoring/logs/errors/:hash/occurrences -- individual occurrences of a grouped error
- GET /api/monitoring/logs/errors/:hash/context -- surrounding log entries for an error
- GET /api/monitoring/logs/opensearch-link -- returns URL to OpenSearch Dashboards for a query
- GET /api/monitoring/logs/saved-searches -- returns user's saved searches
- POST /api/monitoring/logs/saved-searches -- creates a saved search
- PUT /api/monitoring/logs/saved-searches/:id -- updates a saved search
- DELETE /api/monitoring/logs/saved-searches/:id -- deletes a saved search
- GET /api/monitoring/logs/alert-rules -- returns all log alert rules
- POST /api/monitoring/logs/alert-rules -- creates a log alert rule
- PUT /api/monitoring/logs/alert-rules/:id -- updates a log alert rule
- DELETE /api/monitoring/logs/alert-rules/:id -- deletes a log alert rule
- GET /api/monitoring/logs/active-alerts -- returns currently triggered alerts
- POST /api/monitoring/logs/active-alerts/:id/acknowledge -- acknowledges a triggered alert

## Dashboard Components

- `LogExplorerPage` -- page container with tabs (Live, Search, Errors, Alerts)
- `LiveLogTail` -- real-time log stream viewer
- `LogLine` -- single log entry with expandable detail
- `LogJsonDetail` -- expanded JSON context view
- `StackTraceViewer` -- formatted stack trace with file links
- `LogSearchBar` -- Lucene-syntax search input with history dropdown
- `LogFilterPanel` -- service/level/engine/issue filters
- `LogResults` -- paginated search results
- `LogLevelDistribution` -- donut + time series chart of log levels
- `ErrorDrillDown` -- error-only view with grouping and context
- `ErrorGroupCard` -- grouped error with count and expandable occurrences
- `OpenSearchLink` -- external link to OpenSearch Dashboards
- `SavedSearchesSidebar` -- list of saved searches
- `SavedSearchForm` -- create/edit saved search
- `AlertRulesPanel` -- log alert rule management
- `AlertRuleForm` -- create/edit alert rule
- `ActiveLogAlerts` -- currently triggered alert list

## Data Sources

- OpenSearch `tamma-logs-*` indices (primary) -- full log data with full-text search
- In-memory log buffer in API server (fallback) -- recent logs when OpenSearch unavailable
- Pino structured logging output (existing) -- log format
- UserStore (existing) -- saved searches per user
- AlertManager (existing, `@tamma/cost-monitor`) -- alert delivery channels

## Implementation Notes

- OpenSearch query proxy: the API server constructs OpenSearch query DSL from the Lucene query string. Use the `query_string` query type which accepts Lucene syntax natively.
- Log streaming SSE: the API server subscribes to a Pino transport stream. When Pino writes a log, it is forwarded to all connected SSE clients. Use a pub/sub pattern to fan out to multiple connections.
- Fallback mode: when OpenSearch is unhealthy (from admin health check), the search endpoint falls back to in-memory grep over the last N log entries.
- Error grouping: hash the error message (first 200 chars) to create a group key. Store group metadata in memory.
- Alert rule evaluation: a setInterval loop queries OpenSearch for each rule's query within its time window. If count exceeds threshold, fire the alert.
- Pino log shipping to OpenSearch: use `pino-opensearch` transport (already a dependency) or a custom Pino destination that writes to OpenSearch bulk API.
- Stack trace file paths: render as `<code>` elements. Clickable links are possible in development mode (linking to VS Code `vscode://file/path:line`).

## Files to Create

- `packages/api/src/routes/monitoring/log-routes.ts`
- `packages/api/src/services/monitoring/opensearch-proxy.ts`
- `packages/api/src/services/monitoring/log-stream-service.ts`
- `packages/api/src/services/monitoring/log-alert-evaluator.ts`
- `packages/api/src/services/monitoring/saved-search-store.ts`
- `packages/dashboard/src/pages/monitoring/LogExplorerPage.tsx`
- `packages/dashboard/src/components/monitoring/logs/LiveLogTail.tsx`
- `packages/dashboard/src/components/monitoring/logs/LogLine.tsx`
- `packages/dashboard/src/components/monitoring/logs/LogJsonDetail.tsx`
- `packages/dashboard/src/components/monitoring/logs/StackTraceViewer.tsx`
- `packages/dashboard/src/components/monitoring/logs/LogSearchBar.tsx`
- `packages/dashboard/src/components/monitoring/logs/LogFilterPanel.tsx`
- `packages/dashboard/src/components/monitoring/logs/LogResults.tsx`
- `packages/dashboard/src/components/monitoring/logs/LogLevelDistribution.tsx`
- `packages/dashboard/src/components/monitoring/logs/ErrorDrillDown.tsx`
- `packages/dashboard/src/components/monitoring/logs/ErrorGroupCard.tsx`
- `packages/dashboard/src/components/monitoring/logs/SavedSearchesSidebar.tsx`
- `packages/dashboard/src/components/monitoring/logs/AlertRulesPanel.tsx`
- `packages/dashboard/src/components/monitoring/logs/ActiveLogAlerts.tsx`
- `packages/dashboard/src/hooks/monitoring/useLogExplorer.ts`
- `packages/dashboard/src/hooks/monitoring/useLiveLogTail.ts`
- Tests for all API routes, services, and components
