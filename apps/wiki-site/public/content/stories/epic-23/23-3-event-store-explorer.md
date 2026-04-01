---
title: "Story 23-3: Event Store Explorer"
sidebar:
  order: 230
---

Status: planned

## Summary

Build a full-featured explorer for the DCB event store that allows operators to search, filter, visualize, and export all engine events. This is the primary debugging tool for understanding what happened during any workflow execution.

## Acceptance Criteria

### Event List View

1. The page shows a paginated table of all events from the IEventStore:
   - Columns: Timestamp, Event Type, Issue #, Data Summary, Engine ID
   - Default sort: newest first
   - Page size: 25/50/100 (selectable)
   - Total count shown in footer
2. Each row is clickable to expand the full event detail (see below).
3. Event type cells are color-coded:
   - Green: success events (PLAN_APPROVED, IMPLEMENTATION_COMPLETED, PR_MERGED, ISSUE_CLOSED)
   - Red: failure events (IMPLEMENTATION_FAILED, ERROR_OCCURRED, PLAN_REJECTED)
   - Blue: progress events (ISSUE_SELECTED, ISSUE_ANALYZED, PLAN_GENERATED, BRANCH_CREATED, PR_CREATED)
   - Yellow: monitoring events (STATE_TRANSITION, CI_CHECK_STARTED)
   - Gray: cleanup events (BRANCH_DELETED)

### Full-Text Search

4. A search bar supports full-text search across:
   - Event type
   - Issue number
   - Data field values (stringified JSON)
   - Engine ID
5. Search is performed client-side for the current page and server-side for cross-page results.
6. Search results highlight matching terms in the table.
7. Search supports basic operators: exact match with quotes, exclusion with `-`, OR with `|`.

### Filtering

8. Filter controls include:
   - Event type: multi-select dropdown listing all 17 EngineEventType values
   - Issue number: numeric input
   - Engine ID: dropdown populated from EngineRegistry
   - Time range: preset buttons (Last 1h, 6h, 24h, 7d) + custom date range picker
   - Success/failure: toggle for success-only, failure-only, all
9. Active filters are shown as removable chips above the table.
10. Filter state is persisted in URL query params so links can be shared.

### Event Detail View

11. Clicking an event row expands an inline detail panel showing:
    - Full event ID (UUID)
    - Exact timestamp (ISO 8601 with millisecond precision)
    - Event type with description
    - Issue number (linked to GitHub if URL available)
    - Full JSON data payload (syntax-highlighted, collapsible nested objects)
    - Related events: previous and next events for the same issue (with navigation links)
    - Time delta from previous event (e.g., "+2.3s")
12. A "Copy JSON" button copies the full event as formatted JSON to clipboard.
13. A "View Issue Timeline" link navigates to the timeline view filtered to this issue.

### Event Timeline Visualization

14. A timeline view (toggle from list view) shows events as points on a horizontal time axis:
    - X-axis: time
    - Y-axis: grouped by issue number
    - Point color: matches event type color coding
    - Point size: proportional to data payload size
    - Hover shows event summary tooltip
    - Click navigates to event detail
15. Zoom: scroll to zoom in/out on time axis; drag to pan.
16. State transition events are connected with lines showing the workflow progression.

### Event Frequency Chart

17. A collapsible panel above the table shows event frequency:
    - Bar chart: events per time bucket (auto-scales: per minute for <1h, per hour for <24h, per day for >24h)
    - Stacked by event type
    - Hover shows exact count per type
    - The chart respects the current filter state

### Replay Capability

18. A "Replay from here" button on any event:
    - Shows a confirmation dialog explaining that replay will re-process events from this point
    - Creates a new engine event with type `REPLAY_INITIATED` and data `{ fromEventId: string }`
    - Does NOT actually re-execute -- it marks the replay point for a future engine feature
    - The button is only visible to owner-role users
19. Replay audit: all replay initiations are logged as events themselves.

### Export

20. An "Export" dropdown offers:
    - Export current page as JSON
    - Export current page as CSV
    - Export all filtered results as JSON (with warning for >10,000 events)
    - Export all filtered results as CSV
21. JSON export includes the full event objects. CSV export flattens the data field to top-level columns.
22. Export filenames include the filter context: `tamma-events-issue-42-2026-03-29.json`

## API Endpoints Needed

- GET /api/monitoring/events -- paginated event list with query params: `page`, `pageSize`, `type[]`, `issueNumber`, `engineId`, `since`, `until`, `search`, `successOnly`, `failureOnly`
- GET /api/monitoring/events/:id -- single event detail
- GET /api/monitoring/events/:id/related -- previous and next events for the same issue
- GET /api/monitoring/events/frequency -- event count per time bucket, query params: `bucketSize`, `since`, `until`, `type[]`
- GET /api/monitoring/events/types -- returns all event types with counts (for filter dropdown)
- GET /api/monitoring/events/export -- streaming export (JSON or CSV based on Accept header), same filters as list endpoint
- POST /api/monitoring/events/replay -- marks a replay point, body: `{ fromEventId: string }`

## Dashboard Components

- `EventExplorerPage` -- page container with list/timeline toggle
- `EventSearchBar` -- full-text search with operator support
- `EventFilterPanel` -- filter controls (type, issue, engine, time, status)
- `EventFilterChips` -- active filter display with remove buttons
- `EventTable` -- paginated sortable table of events
- `EventRow` -- single event row with color coding
- `EventDetailPanel` -- expanded inline event detail
- `EventJsonViewer` -- syntax-highlighted JSON with collapsible sections
- `EventTimeline` -- horizontal timeline visualization
- `EventTimelinePoint` -- single event point on timeline
- `EventFrequencyChart` -- stacked bar chart of event frequency
- `EventExportDropdown` -- export format selector
- `ReplayConfirmDialog` -- confirmation dialog for replay

## Data Sources

- IEventStore.getEvents() (existing) -- all engine events
- EngineRegistry.list() (existing) -- engine IDs for filter dropdown
- New: aggregation query on events for frequency chart

## Implementation Notes

- The existing `GET /api/engine/history` endpoint returns paginated events for a single engine. The new monitoring endpoint aggregates across ALL engines in the registry.
- Full-text search: for in-memory event store, use `JSON.stringify()` on each event and `String.includes()`. For PostgreSQL-backed event store (future), use `to_tsvector` / `plainto_tsquery`.
- Timeline visualization uses SVG with CSS transforms for zoom/pan. No external library.
- CSV export uses a streaming approach: write headers, then one row per event, to avoid loading all events into memory.
- Event detail "related events" query walks backward and forward in the event array filtered by issueNumber.

## Files to Create

- `packages/api/src/routes/monitoring/event-routes.ts`
- `packages/api/src/services/monitoring/event-query-service.ts`
- `packages/api/src/services/monitoring/event-export-service.ts`
- `packages/dashboard/src/pages/monitoring/EventExplorerPage.tsx`
- `packages/dashboard/src/components/monitoring/events/EventSearchBar.tsx`
- `packages/dashboard/src/components/monitoring/events/EventFilterPanel.tsx`
- `packages/dashboard/src/components/monitoring/events/EventFilterChips.tsx`
- `packages/dashboard/src/components/monitoring/events/EventTable.tsx`
- `packages/dashboard/src/components/monitoring/events/EventRow.tsx`
- `packages/dashboard/src/components/monitoring/events/EventDetailPanel.tsx`
- `packages/dashboard/src/components/monitoring/events/EventJsonViewer.tsx`
- `packages/dashboard/src/components/monitoring/events/EventTimeline.tsx`
- `packages/dashboard/src/components/monitoring/events/EventFrequencyChart.tsx`
- `packages/dashboard/src/components/monitoring/events/EventExportDropdown.tsx`
- `packages/dashboard/src/components/monitoring/events/ReplayConfirmDialog.tsx`
- `packages/dashboard/src/hooks/monitoring/useEventExplorer.ts`
- Tests for all API routes, services, and key components
