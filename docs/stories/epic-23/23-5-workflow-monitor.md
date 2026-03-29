# Story 23-5: Workflow Monitor

Status: planned

## Summary

Build a workflow monitoring screen showing all active and historical workflow instances, their current phase, duration, cost, linked issues/PRs, a Gantt-style timeline, and queue depth. Integrates with both the TammaEngine event store and ELSA workflow instances.

## Acceptance Criteria

### Active Workflows Table

1. The page shows a table of all active workflow instances:
   - Columns: Workflow ID, Issue #, Issue Title, Current Phase, Status, Duration, Cost USD, Engine ID, Started At
   - Status values: running (blue), paused (yellow), awaiting_approval (orange), error (red), completed (green), cancelled (gray)
   - Duration is live-updating (elapsed since start)
   - Cost shows accumulated cost for this workflow
2. Each row has action buttons:
   - "View Details" expands the detail panel
   - "Pause" / "Resume" (for running workflows)
   - "Retry" (for errored workflows)
   - "Cancel" (stops the workflow)
3. Clicking an issue number links to the GitHub issue URL.
4. Clicking a workflow ID navigates to the detail view.

### Workflow Detail View

5. The detail view for a single workflow shows:
   - Header: Issue title, number, labels, GitHub link
   - Current state in the engine state machine (highlighted in the phase diagram)
   - Development plan summary (if generated)
   - Branch name (linked to GitHub)
   - PR number and URL (if created)
   - Full event history for this workflow (filtered from event store)
   - Cost breakdown: per-phase cost (analysis, planning, implementation)
   - Duration per phase (time spent in each EngineState)
   - Error details with stack trace (if in error state)

### Workflow Timeline (Gantt Chart)

6. A Gantt-style visualization shows workflow phases as horizontal bars:
   - X-axis: time
   - Y-axis: one row per active workflow (or per phase within a workflow)
   - Bar segments colored by phase:
     - SELECTING_ISSUE (light blue)
     - ANALYZING (blue)
     - PLANNING (indigo)
     - AWAITING_APPROVAL (orange)
     - IMPLEMENTING (purple)
     - CREATING_PR (teal)
     - MONITORING (yellow)
     - MERGING (green)
     - ERROR (red overlay)
   - Hover shows phase name, duration, start/end timestamps
   - Click navigates to workflow detail
7. The timeline auto-scrolls to keep the current time visible.
8. Completed workflows fade to 50% opacity.

### Workflow Success/Failure Metrics

9. A metrics panel at the top shows:
   - Total workflows: completed / failed / in-progress / cancelled
   - Success rate: percentage (with trend arrow vs. previous period)
   - Average completion time: formatted as "Xh Xm"
   - Average cost per workflow: USD
   - Fastest completion: time + issue link
   - Most expensive workflow: cost + issue link
10. A daily chart shows workflow count over time: stacked bars of completed (green) vs failed (red).

### Queue Depth

11. A queue status panel shows:
    - Issues waiting (matching `issueLabels`, not assigned): count and list
    - Issues in progress (assigned to bot): count and list
    - Estimated time to drain queue: based on average completion time * queued count
    - Queue trend: chart of queue depth over last 7 days
12. If the queue is empty, show "No issues waiting" with a green check.
13. If queue depth exceeds 10, show a yellow warning.

### ELSA Workflow Integration

14. An "ELSA Workflows" tab shows:
    - Workflow definitions synced from ELSA (from existing `/api/workflows/definitions`)
    - Per definition: name, version, instance count, last executed
    - Instance list per definition (from existing `/api/workflows/instances`)
    - Instance status with live updates via SSE (existing `/api/workflows/instances/:id/events`)
15. ELSA instances are linked to TammaEngine events where possible (matched by workflow ID in event data).

### Failed Workflow Panel

16. A dedicated "Failed Workflows" section shows:
    - All workflows currently in ERROR state
    - Error message and type
    - Phase where failure occurred
    - Time since failure
    - "Retry" button that sends a `{ type: 'start' }` command to the engine
    - "Dismiss" button that acknowledges the error and resets the engine to IDLE
17. Failed workflows persist until explicitly dismissed or retried.

## API Endpoints Needed

- GET /api/monitoring/workflows/active -- returns all active workflow instances across all engines
- GET /api/monitoring/workflows/:id -- returns single workflow detail with event history
- GET /api/monitoring/workflows/:id/timeline -- returns phase-by-phase timeline for Gantt chart
- GET /api/monitoring/workflows/metrics -- returns success/failure rates, avg time, avg cost
- GET /api/monitoring/workflows/queue -- returns queue depth and waiting issues
- GET /api/monitoring/workflows/queue/trend -- returns queue depth over time
- GET /api/monitoring/workflows/daily-counts -- returns daily workflow counts (completed/failed)
- GET /api/monitoring/workflows/failed -- returns all currently failed workflows
- POST /api/monitoring/workflows/:id/retry -- retries a failed workflow
- POST /api/monitoring/workflows/:id/cancel -- cancels a running workflow
- GET /api/monitoring/workflows/stream -- SSE stream of workflow state changes

## Dashboard Components

- `WorkflowMonitorPage` -- page container with tabs (Active, Timeline, Metrics, Queue, ELSA, Failed)
- `ActiveWorkflowsTable` -- table of active workflows with actions
- `WorkflowDetailView` -- full detail for a single workflow
- `WorkflowPhaseDiagram` -- state machine visualization with current state highlighted
- `WorkflowGanttChart` -- Gantt-style timeline of workflow phases
- `WorkflowGanttBar` -- single workflow bar with phase segments
- `WorkflowMetricsPanel` -- success rate, avg time, avg cost metrics
- `WorkflowDailyChart` -- stacked bar chart of daily completed/failed
- `QueueStatusPanel` -- queue depth with waiting issues
- `QueueTrendChart` -- queue depth over time
- `ElsaWorkflowsTab` -- ELSA definition and instance browser
- `FailedWorkflowPanel` -- failed workflows with retry/dismiss
- `FailedWorkflowCard` -- single failed workflow

## Data Sources

- EngineRegistry.list() (existing) -- engine states and stats
- IEventStore.getEvents() (existing) -- workflow event history
- Engine.getState(), getCurrentIssue(), getCurrentPlan(), getCurrentBranch() (existing) -- current workflow state
- IWorkflowStore.listDefinitions(), listInstances() (existing) -- ELSA workflows
- CostTracker.getAggregate() (existing) -- per-workflow cost
- IGitPlatform.listIssues() (existing) -- queue depth (issues matching labels)

## Implementation Notes

- "Active workflows" = engines in any state except IDLE. Each engine represents one workflow.
- Phase timeline is reconstructed from STATE_TRANSITION events in the event store: each transition marks the start of a new phase.
- Queue depth requires calling the git platform API to list issues with the configured labels. Cache this with a 60s TTL to avoid rate limiting.
- The Gantt chart uses SVG rectangles positioned by time. Each phase bar starts at the STATE_TRANSITION event timestamp and ends at the next transition.
- Retry: dispatches `{ type: 'start' }` to the engine via the existing engine command endpoint.
- Cancel: dispatches `{ type: 'stop' }` to the engine.
- "Estimated time to drain" = (queue depth * average completion time) / (number of active engines).

## Files to Create

- `packages/api/src/routes/monitoring/workflow-routes.ts`
- `packages/api/src/services/monitoring/workflow-monitor-service.ts`
- `packages/api/src/services/monitoring/queue-depth-service.ts`
- `packages/dashboard/src/pages/monitoring/WorkflowMonitorPage.tsx`
- `packages/dashboard/src/components/monitoring/workflows/ActiveWorkflowsTable.tsx`
- `packages/dashboard/src/components/monitoring/workflows/WorkflowDetailView.tsx`
- `packages/dashboard/src/components/monitoring/workflows/WorkflowPhaseDiagram.tsx`
- `packages/dashboard/src/components/monitoring/workflows/WorkflowGanttChart.tsx`
- `packages/dashboard/src/components/monitoring/workflows/WorkflowMetricsPanel.tsx`
- `packages/dashboard/src/components/monitoring/workflows/WorkflowDailyChart.tsx`
- `packages/dashboard/src/components/monitoring/workflows/QueueStatusPanel.tsx`
- `packages/dashboard/src/components/monitoring/workflows/QueueTrendChart.tsx`
- `packages/dashboard/src/components/monitoring/workflows/ElsaWorkflowsTab.tsx`
- `packages/dashboard/src/components/monitoring/workflows/FailedWorkflowPanel.tsx`
- `packages/dashboard/src/hooks/monitoring/useWorkflowMonitor.ts`
- Tests for all API routes, services, and components
