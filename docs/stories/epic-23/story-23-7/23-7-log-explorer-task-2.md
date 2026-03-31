# Task 2: Log Explorer Frontend Components

**Story:** 23-7-log-explorer
**Epic:** 23

## Task Description

Build the LogExplorerPage with live tail, full-text search, filtering, log level distribution charts, error drill-down, saved searches sidebar, and alert rule management.

## Acceptance Criteria

- Live log tail with auto-scroll, pause, and clear (last 1000 lines)
- Lucene-syntax search with search history dropdown
- Filter panel: service, level, engine ID, issue number, time range
- Log level distribution: donut chart + time series with error spike detection
- Error drill-down with grouping, stack traces, and surrounding context
- Saved searches sidebar with create/edit/delete
- Alert rule management panel with enable/disable
- Active alerts panel with acknowledge button

## Implementation Details

### Technical Requirements

- [ ] Replace placeholder `packages/dashboard/src/pages/monitoring/LogExplorerPage.tsx`:
  - MonitoringLayout with title "Log Explorer"
  - Tab navigation: Live, Search, Errors, Alerts
  - Live tab: LiveLogTail with SSE connection
  - Search tab: LogSearchBar + LogFilterPanel + LogResults
  - Errors tab: ErrorDrillDown
  - Alerts tab: AlertRulesPanel + ActiveLogAlerts

- [ ] Create `packages/dashboard/src/hooks/monitoring/useLogExplorer.ts`

- [ ] Create `packages/dashboard/src/hooks/monitoring/useLiveLogTail.ts`:
  ```typescript
  export interface UseLiveLogTailResult {
    entries: LogEntry[];        // last 1000
    connected: boolean;
    paused: boolean;
    setPaused: (paused: boolean) => void;
    clear: () => void;
    filters: { services?: string[]; levels?: string[] };
    setFilters: (filters: { services?: string[]; levels?: string[] }) => void;
  }
  ```
  - Uses `useMonitoringSSE` to connect to `/api/monitoring/logs/stream`
  - Maintains circular buffer of last 1000 entries in state
  - Pause freezes display without disconnecting
  - Clear empties the visible buffer

- [ ] Create `packages/dashboard/src/components/monitoring/logs/LiveLogTail.tsx`:
  - Virtualized list of log entries (only renders visible rows for performance)
  - Auto-scrolls to bottom when new entries arrive (unless paused)
  - Each LogLine: timestamp, level (color-coded), service, message
  - Clickable to expand full JSON context
  - Toolbar: Pause/Resume button, Clear button, connection status indicator
  - Service/level filter dropdowns in toolbar

- [ ] Create `packages/dashboard/src/components/monitoring/logs/LogLine.tsx`:
  - Timestamp in monospace font
  - Level badge: DEBUG=gray, INFO=blue, WARN=yellow, ERROR=red
  - Service name in muted color
  - Message text (truncated to 1 line, expand on click)
  - Expandable detail: LogJsonDetail component

- [ ] Create `packages/dashboard/src/components/monitoring/logs/LogJsonDetail.tsx`:
  - All Pino fields formatted as key-value pairs
  - Custom fields with syntax highlighting
  - Stack traces in monospace with StackTraceViewer

- [ ] Create `packages/dashboard/src/components/monitoring/logs/StackTraceViewer.tsx`:
  - Monospace font, preserves newlines and indentation
  - File paths rendered as `<code>` elements
  - In dev mode: clickable file paths linking to `vscode://file/path:line`

- [ ] Create `packages/dashboard/src/components/monitoring/logs/LogSearchBar.tsx`:
  - Text input supporting Lucene syntax
  - Search history dropdown (last 20, from localStorage)
  - Syntax hint tooltip explaining supported operators

- [ ] Create `packages/dashboard/src/components/monitoring/logs/LogFilterPanel.tsx`:
  - Service: multi-select (orchestrator, api, dashboard, providers, intelligence, events, cost-monitor)
  - Level: multi-select (debug, info, warn, error)
  - Engine ID: text input
  - Issue number: numeric input
  - Time range: presets + custom
  - Arbitrary key-value pair filter

- [ ] Create `packages/dashboard/src/components/monitoring/logs/LogResults.tsx`:
  - Paginated search results using DataTable
  - Total hit count and search time displayed
  - Search terms highlighted in results

- [ ] Create `packages/dashboard/src/components/monitoring/logs/LogLevelDistribution.tsx`:
  - Collapsible panel
  - Donut chart: percentage by level
  - Time series: stacked area of log counts per level
  - Red markers where error rate exceeds 2x rolling average

- [ ] Create `packages/dashboard/src/components/monitoring/logs/ErrorDrillDown.tsx`:
  - Error-only logs with enriched detail
  - Error grouping: identical messages grouped with count
  - Each ErrorGroupCard: message, stack trace, service, issue context
  - Related entries: 5 before and 5 after

- [ ] Create `packages/dashboard/src/components/monitoring/logs/ErrorGroupCard.tsx`:
  - Grouped error with count badge
  - Expandable occurrence list
  - Frequency indicator (count per hour)
  - First/last occurrence timestamps

- [ ] Create `packages/dashboard/src/components/monitoring/logs/SavedSearchesSidebar.tsx`:
  - List of saved searches: name, query, created date, last used
  - Click loads query and filters
  - New/Edit/Delete actions
  - "Pin to dashboard" toggle

- [ ] Create `packages/dashboard/src/components/monitoring/logs/AlertRulesPanel.tsx`:
  - Alert rule list: name, query, threshold, window, severity, enabled
  - Create/Edit/Delete actions
  - Enable/disable toggle per rule
  - Alert rule form: name, Lucene query, threshold, window, severity, channels

- [ ] Create `packages/dashboard/src/components/monitoring/logs/ActiveLogAlerts.tsx`:
  - Currently triggered alerts: rule name, trigger count, last triggered
  - Acknowledge button per alert

- [ ] Create `packages/dashboard/src/services/monitoring/log-api-client.ts`

### Files to Create

- CREATE `packages/dashboard/src/components/monitoring/logs/LiveLogTail.tsx`
- CREATE `packages/dashboard/src/components/monitoring/logs/LogLine.tsx`
- CREATE `packages/dashboard/src/components/monitoring/logs/LogJsonDetail.tsx`
- CREATE `packages/dashboard/src/components/monitoring/logs/StackTraceViewer.tsx`
- CREATE `packages/dashboard/src/components/monitoring/logs/LogSearchBar.tsx`
- CREATE `packages/dashboard/src/components/monitoring/logs/LogFilterPanel.tsx`
- CREATE `packages/dashboard/src/components/monitoring/logs/LogResults.tsx`
- CREATE `packages/dashboard/src/components/monitoring/logs/LogLevelDistribution.tsx`
- CREATE `packages/dashboard/src/components/monitoring/logs/ErrorDrillDown.tsx`
- CREATE `packages/dashboard/src/components/monitoring/logs/ErrorGroupCard.tsx`
- CREATE `packages/dashboard/src/components/monitoring/logs/SavedSearchesSidebar.tsx`
- CREATE `packages/dashboard/src/components/monitoring/logs/AlertRulesPanel.tsx`
- CREATE `packages/dashboard/src/components/monitoring/logs/ActiveLogAlerts.tsx`
- CREATE `packages/dashboard/src/hooks/monitoring/useLogExplorer.ts`
- CREATE `packages/dashboard/src/hooks/monitoring/useLiveLogTail.ts`
- CREATE `packages/dashboard/src/services/monitoring/log-api-client.ts`

### Files to Modify

- MODIFY `packages/dashboard/src/pages/monitoring/LogExplorerPage.tsx` -- replace placeholder

### Dependencies

- Story 23-12: MonitoringLayout, DataTable, StatusBadge, TimeSeriesChart, EmptyState, ErrorBanner
- Task 1: Log API endpoints

## Testing Strategy

### Unit Tests

- [ ] LiveLogTail: auto-scrolls to bottom when not paused
- [ ] LiveLogTail: freezes on pause
- [ ] LiveLogTail: clear empties visible buffer
- [ ] LiveLogTail: respects 1000 line buffer limit
- [ ] LogLine: color-codes level correctly
- [ ] LogLine: expands on click to show detail
- [ ] LogSearchBar: debounces input, shows search history
- [ ] LogFilterPanel: all filter controls render
- [ ] LogResults: highlights search terms
- [ ] LogLevelDistribution: donut chart shows correct percentages
- [ ] ErrorDrillDown: groups identical errors
- [ ] ErrorGroupCard: shows count and expandable occurrences
- [ ] SavedSearchesSidebar: loads query on click
- [ ] AlertRulesPanel: toggle enable/disable calls API
- [ ] ActiveLogAlerts: acknowledge button calls API
- [ ] useLiveLogTail: circular buffer respects maxSize

## Completion Checklist

- [ ] All 13 child components created
- [ ] 4-tab navigation (Live, Search, Errors, Alerts)
- [ ] Live tail with virtualized rendering
- [ ] Lucene search with history
- [ ] Error grouping and context
- [ ] Saved searches per user
- [ ] Alert rules with management UI
- [ ] Tests written and passing
- [ ] TypeScript strict mode compiles
