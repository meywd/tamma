---
title: "Task 2: Workflow Monitor Frontend Components"
sidebar:
  order: 230
---

**Story:** 23-5-workflow-monitor
**Epic:** 23

## Task Description

Build the WorkflowMonitorPage with tabs for Active, Timeline (Gantt), Metrics, Queue, ELSA, and Failed workflows. Includes real-time updates via SSE, workflow control actions, and Gantt-style phase visualization.

## Acceptance Criteria

- Active workflows table with live-updating duration, action buttons (pause/resume/retry/cancel)
- Workflow detail view with phase diagram, event history, cost breakdown
- Gantt chart showing phase bars color-coded by workflow state
- Metrics panel with success rate, avg time, avg cost, daily chart
- Queue status with estimated drain time and trend chart
- ELSA workflows tab with definitions and instances
- Failed workflows panel with retry/dismiss actions
- SSE stream for real-time state change updates

## Implementation Details

### Technical Requirements

- [ ] Replace placeholder `packages/dashboard/src/pages/monitoring/WorkflowMonitorPage.tsx`:
  - MonitoringLayout with title "Workflow Monitor"
  - Tab navigation: Active, Timeline, Metrics, Queue, ELSA, Failed
  - SSE connection to `/api/monitoring/workflows/stream`

- [ ] Create `packages/dashboard/src/hooks/monitoring/useWorkflowMonitor.ts`

- [ ] Create `packages/dashboard/src/components/monitoring/workflows/ActiveWorkflowsTable.tsx`:
  - DataTable columns: Workflow ID, Issue #, Issue Title, Phase, Status, Duration (live), Cost, Engine, Started At
  - Status color: running=blue, paused=yellow, awaiting_approval=orange, error=red, completed=green, cancelled=gray
  - Duration updates every second for running workflows
  - Action buttons: View Details, Pause/Resume, Retry (error only), Cancel
  - Issue number links to GitHub URL

- [ ] Create `packages/dashboard/src/components/monitoring/workflows/WorkflowDetailView.tsx`:
  - Header: issue title, number, labels, GitHub link
  - WorkflowPhaseDiagram showing current state highlighted
  - Development plan summary
  - Branch name and PR link
  - Event history table (filtered from event store)
  - Cost breakdown per phase
  - Duration per phase
  - Error details with stack trace if in error state

- [ ] Create `packages/dashboard/src/components/monitoring/workflows/WorkflowPhaseDiagram.tsx`:
  - Horizontal state machine visualization
  - States: SELECTING_ISSUE -> ANALYZING -> PLANNING -> AWAITING_APPROVAL -> IMPLEMENTING -> CREATING_PR -> MONITORING -> MERGING
  - Current state highlighted with color + pulse
  - Completed states have checkmark
  - Error state shows red indicator

- [ ] Create `packages/dashboard/src/components/monitoring/workflows/WorkflowGanttChart.tsx`:
  - SVG Gantt chart
  - X-axis: time, Y-axis: one row per workflow (or per phase within a workflow)
  - Phase bars colored: SELECTING_ISSUE=light-blue, ANALYZING=blue, PLANNING=indigo, AWAITING_APPROVAL=orange, IMPLEMENTING=purple, CREATING_PR=teal, MONITORING=yellow, MERGING=green, ERROR=red overlay
  - Hover: phase name, duration, start/end timestamps
  - Click: navigate to workflow detail
  - Auto-scrolls to keep current time visible
  - Completed workflows fade to 50% opacity

- [ ] Create `packages/dashboard/src/components/monitoring/workflows/WorkflowMetricsPanel.tsx`:
  - MetricCards: Total Workflows, Success Rate (with trend), Avg Completion Time, Avg Cost, Fastest, Most Expensive

- [ ] Create `packages/dashboard/src/components/monitoring/workflows/WorkflowDailyChart.tsx`:
  - Stacked bar chart: completed (green) vs failed (red) per day

- [ ] Create `packages/dashboard/src/components/monitoring/workflows/QueueStatusPanel.tsx`:
  - Waiting count + issue list, In-progress count + issue list
  - Estimated drain time
  - Green "No issues waiting" when queue empty
  - Yellow warning when queue > 10

- [ ] Create `packages/dashboard/src/components/monitoring/workflows/QueueTrendChart.tsx`:
  - TimeSeriesChart of queue depth over last 7 days

- [ ] Create `packages/dashboard/src/components/monitoring/workflows/ElsaWorkflowsTab.tsx`:
  - Workflow definitions from existing `/api/workflows/definitions`
  - Per definition: name, version, instance count, last executed
  - Instance list per definition from existing `/api/workflows/instances`

- [ ] Create `packages/dashboard/src/components/monitoring/workflows/FailedWorkflowPanel.tsx`:
  - List of failed workflows with: error message, phase, time since failure
  - "Retry" and "Dismiss" buttons per workflow

- [ ] Create `packages/dashboard/src/services/monitoring/workflow-api-client.ts`

### Files to Create

- CREATE `packages/dashboard/src/components/monitoring/workflows/ActiveWorkflowsTable.tsx`
- CREATE `packages/dashboard/src/components/monitoring/workflows/WorkflowDetailView.tsx`
- CREATE `packages/dashboard/src/components/monitoring/workflows/WorkflowPhaseDiagram.tsx`
- CREATE `packages/dashboard/src/components/monitoring/workflows/WorkflowGanttChart.tsx`
- CREATE `packages/dashboard/src/components/monitoring/workflows/WorkflowMetricsPanel.tsx`
- CREATE `packages/dashboard/src/components/monitoring/workflows/WorkflowDailyChart.tsx`
- CREATE `packages/dashboard/src/components/monitoring/workflows/QueueStatusPanel.tsx`
- CREATE `packages/dashboard/src/components/monitoring/workflows/QueueTrendChart.tsx`
- CREATE `packages/dashboard/src/components/monitoring/workflows/ElsaWorkflowsTab.tsx`
- CREATE `packages/dashboard/src/components/monitoring/workflows/FailedWorkflowPanel.tsx`
- CREATE `packages/dashboard/src/hooks/monitoring/useWorkflowMonitor.ts`
- CREATE `packages/dashboard/src/services/monitoring/workflow-api-client.ts`

### Files to Modify

- MODIFY `packages/dashboard/src/pages/monitoring/WorkflowMonitorPage.tsx` -- replace placeholder

### Dependencies

- Story 23-12: MonitoringLayout, MetricCard, MetricGrid, DataTable, TimeSeriesChart, StatusBadge, EmptyState
- Task 1: Workflow API endpoints
- Existing ELSA workflow API endpoints

## Testing Strategy

### Unit Tests

- [ ] ActiveWorkflowsTable: live-updating duration increments
- [ ] ActiveWorkflowsTable: action buttons visible per status
- [ ] WorkflowPhaseDiagram: current state highlighted
- [ ] WorkflowGanttChart: phase bars positioned correctly by time
- [ ] WorkflowGanttChart: color coding matches phase
- [ ] WorkflowMetricsPanel: renders all metric cards
- [ ] QueueStatusPanel: shows warning for queue > 10
- [ ] QueueStatusPanel: shows "No issues waiting" when empty
- [ ] FailedWorkflowPanel: retry button calls retry endpoint
- [ ] FailedWorkflowPanel: dismiss button calls cancel endpoint
- [ ] useWorkflowMonitor: fetches data and handles SSE updates

## Completion Checklist

- [ ] All 10 child components created
- [ ] Tab navigation between 6 sections
- [ ] Gantt chart with phase coloring
- [ ] Real-time updates via SSE
- [ ] Workflow control actions (retry/cancel)
- [ ] ELSA integration
- [ ] Tests written and passing
- [ ] TypeScript strict mode compiles
