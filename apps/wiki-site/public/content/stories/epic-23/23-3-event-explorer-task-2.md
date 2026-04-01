---
title: "Task 2: Event Explorer Frontend Components"
sidebar:
  order: 230
---

**Story:** 23-3-event-store-explorer
**Epic:** 23

## Task Description

Build the EventExplorerPage with list/timeline views, search, filtering, event detail, frequency chart, export, and replay confirmation. Full-featured debugging tool for understanding workflow executions.

## Acceptance Criteria

- Paginated event table with color-coded event types and expandable detail
- Full-text search with operator support (quotes, exclusion, OR)
- Filter controls for event type, issue, engine, time range, success/failure
- Active filters shown as removable chips, persisted in URL query params
- Event detail panel with full JSON, related events, time delta
- Timeline visualization with zoom/pan
- Event frequency chart (stacked bar by event type)
- Export dropdown (JSON/CSV for page or all filtered)
- Replay confirmation dialog (owner-only)

## Implementation Details

### Technical Requirements

- [ ] Replace placeholder `packages/dashboard/src/pages/monitoring/EventExplorerPage.tsx`:
  - MonitoringLayout with title "Event Explorer"
  - Toggle between list view and timeline view
  - Contains: EventSearchBar, EventFilterPanel, EventFilterChips, EventTable (or EventTimeline), EventFrequencyChart, EventExportDropdown

- [ ] Create `packages/dashboard/src/hooks/monitoring/useEventExplorer.ts`:
  ```typescript
  export interface UseEventExplorerResult {
    events: EngineEvent[];
    total: number;
    page: number;
    pageSize: number;
    loading: boolean;
    error: string | null;
    filters: EventFilters;
    setFilters: (filters: Partial<EventFilters>) => void;
    search: string;
    setSearch: (search: string) => void;
    setPage: (page: number) => void;
    setPageSize: (size: number) => void;
    frequency: EventFrequencyBucket[];
    eventTypes: { type: string; count: number }[];
    selectedEvent: EngineEvent | null;
    selectEvent: (id: string | null) => void;
    relatedEvents: { previous: EngineEvent[]; next: EngineEvent[] } | null;
    exportEvents: (format: 'json' | 'csv', scope: 'page' | 'all') => Promise<void>;
    initiateReplay: (eventId: string) => Promise<void>;
  }
  ```

- [ ] Create `packages/dashboard/src/components/monitoring/events/EventSearchBar.tsx`:
  - Text input with search icon
  - Supports: "exact phrase", -exclude, term1|term2
  - Debounced (300ms) to avoid excessive API calls
  - "Search History" dropdown (last 20, from localStorage)

- [ ] Create `packages/dashboard/src/components/monitoring/events/EventFilterPanel.tsx`:
  - Event type: multi-select dropdown listing all 17 EngineEventType values
  - Issue number: numeric input
  - Engine ID: dropdown from EngineRegistry
  - Time range: preset buttons + custom picker
  - Success/failure: three-way toggle (all/success/failure)

- [ ] Create `packages/dashboard/src/components/monitoring/events/EventFilterChips.tsx`:
  - Renders active filters as removable chips above the table
  - Click X on chip removes that filter

- [ ] Create `packages/dashboard/src/components/monitoring/events/EventTable.tsx`:
  - Uses DataTable from shared primitives
  - Columns: Timestamp, Event Type, Issue #, Data Summary, Engine ID
  - Event type cells color-coded (green=success, red=failure, blue=progress, yellow=monitoring, gray=cleanup)
  - Row click expands EventDetailPanel

- [ ] Create `packages/dashboard/src/components/monitoring/events/EventDetailPanel.tsx`:
  - Full event ID, exact timestamp (ISO 8601 with ms)
  - Event type with description
  - Issue number (linked to GitHub URL if available)
  - Full JSON data payload via EventJsonViewer
  - Related events (previous/next for same issue) with navigation links
  - Time delta from previous event
  - "Copy JSON" button
  - "View Issue Timeline" link

- [ ] Create `packages/dashboard/src/components/monitoring/events/EventJsonViewer.tsx`:
  - Syntax-highlighted JSON with collapsible nested objects
  - Click to expand/collapse object/array nodes
  - Key names in one color, string values in another, numbers in another

- [ ] Create `packages/dashboard/src/components/monitoring/events/EventTimeline.tsx`:
  - SVG horizontal timeline: X=time, Y=grouped by issue
  - Points colored by event type
  - State transition events connected with lines
  - Scroll to zoom, drag to pan (using SVG transforms)
  - Hover tooltip with event summary
  - Click navigates to event detail

- [ ] Create `packages/dashboard/src/components/monitoring/events/EventFrequencyChart.tsx`:
  - Collapsible panel above the table
  - Stacked bar chart: events per time bucket by type
  - Auto-scales bucket width: per-minute for <1h, per-hour for <24h, per-day for >24h
  - Respects current filter state

- [ ] Create `packages/dashboard/src/components/monitoring/events/EventExportDropdown.tsx`:
  - Dropdown with 4 options: JSON/CSV for current page, JSON/CSV for all filtered
  - Warning for >10,000 events
  - Downloads file with contextual filename: `tamma-events-issue-42-2026-03-29.json`

- [ ] Create `packages/dashboard/src/components/monitoring/events/ReplayConfirmDialog.tsx`:
  - Modal dialog explaining replay behavior
  - Confirm/Cancel buttons
  - Owner-only visibility

- [ ] Create `packages/dashboard/src/services/monitoring/event-api-client.ts`

### Files to Create

- CREATE `packages/dashboard/src/components/monitoring/events/EventSearchBar.tsx`
- CREATE `packages/dashboard/src/components/monitoring/events/EventFilterPanel.tsx`
- CREATE `packages/dashboard/src/components/monitoring/events/EventFilterChips.tsx`
- CREATE `packages/dashboard/src/components/monitoring/events/EventTable.tsx`
- CREATE `packages/dashboard/src/components/monitoring/events/EventDetailPanel.tsx`
- CREATE `packages/dashboard/src/components/monitoring/events/EventJsonViewer.tsx`
- CREATE `packages/dashboard/src/components/monitoring/events/EventTimeline.tsx`
- CREATE `packages/dashboard/src/components/monitoring/events/EventFrequencyChart.tsx`
- CREATE `packages/dashboard/src/components/monitoring/events/EventExportDropdown.tsx`
- CREATE `packages/dashboard/src/components/monitoring/events/ReplayConfirmDialog.tsx`
- CREATE `packages/dashboard/src/hooks/monitoring/useEventExplorer.ts`
- CREATE `packages/dashboard/src/services/monitoring/event-api-client.ts`

### Files to Modify

- MODIFY `packages/dashboard/src/pages/monitoring/EventExplorerPage.tsx` -- replace placeholder

### Dependencies

- Story 23-12: MonitoringLayout, DataTable, EmptyState, ErrorBanner
- Task 1: Event query API endpoints

## Testing Strategy

### Unit Tests

- [ ] EventSearchBar: debounces input, stores search history
- [ ] EventFilterPanel: multi-select type filter, time range presets
- [ ] EventFilterChips: renders chips, removes filter on click
- [ ] EventTable: color-codes event types correctly
- [ ] EventTable: row click expands detail panel
- [ ] EventDetailPanel: shows JSON, related events, copy button
- [ ] EventJsonViewer: collapses/expands nested objects
- [ ] EventTimeline: renders SVG with points grouped by issue
- [ ] EventFrequencyChart: stacked bars with correct counts
- [ ] EventExportDropdown: triggers download with correct filename
- [ ] ReplayConfirmDialog: shows only for owner role
- [ ] useEventExplorer: fetches events on mount and on filter change

## Completion Checklist

- [ ] All 10 child components created
- [ ] Search with operator support
- [ ] Filter state persisted in URL
- [ ] List and timeline views toggle
- [ ] Export works for JSON and CSV
- [ ] Replay dialog for owner-only
- [ ] Tests written and passing
- [ ] TypeScript strict mode compiles
