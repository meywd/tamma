# Task 1: Workflow Monitor API Routes & Services

**Story:** 23-5-workflow-monitor
**Epic:** 23

## Task Description

Create backend API routes and services for the workflow monitor: active workflow listing, single workflow detail with event history, phase timeline for Gantt chart, success/failure metrics, queue depth, daily counts, failed workflows, and workflow control (retry/cancel).

## Acceptance Criteria

- `GET /api/monitoring/workflows/active` returns all active workflows across all engines
- `GET /api/monitoring/workflows/:id` returns single workflow detail with event history
- `GET /api/monitoring/workflows/:id/timeline` returns phase-by-phase timeline
- `GET /api/monitoring/workflows/metrics` returns success/failure rates, avg time, avg cost
- `GET /api/monitoring/workflows/queue` returns queue depth and waiting issues
- `GET /api/monitoring/workflows/queue/trend` returns queue depth over time
- `GET /api/monitoring/workflows/daily-counts` returns daily completed/failed counts
- `GET /api/monitoring/workflows/failed` returns all currently failed workflows
- `POST /api/monitoring/workflows/:id/retry` retries a failed workflow
- `POST /api/monitoring/workflows/:id/cancel` cancels a running workflow
- `GET /api/monitoring/workflows/stream` SSE stream of workflow state changes

## Implementation Details

### Technical Requirements

- [ ] Create `packages/api/src/routes/monitoring/workflow-routes.ts`:
  ```typescript
  export function registerWorkflowMonitoringRoutes(
    app: FastifyInstance,
    workflowService: WorkflowMonitorService,
    queueService: QueueDepthService,
  ): void;
  ```
  - Retry: dispatches `{ type: 'start' }` to engine via engine command API
  - Cancel: dispatches `{ type: 'stop' }` to engine
  - Both require `settings:manage` permission

- [ ] Create `packages/api/src/services/monitoring/workflow-monitor-service.ts`:
  ```typescript
  export interface ActiveWorkflow {
    workflowId: string;         // engine ID
    issueNumber: number | null;
    issueTitle: string | null;
    issueUrl: string | null;
    currentPhase: string;       // EngineState
    status: 'running' | 'paused' | 'awaiting_approval' | 'error' | 'completed' | 'cancelled';
    duration: number;           // ms since start
    costUsd: number;
    engineId: string;
    startedAt: string;
    branchName: string | null;
    prNumber: number | null;
    prUrl: string | null;
  }

  export interface WorkflowDetail extends ActiveWorkflow {
    events: EngineEvent[];
    plan: string | null;
    costBreakdown: Record<string, number>;   // phase -> cost
    durationPerPhase: Record<string, number>; // phase -> ms
    errorDetails: { message: string; stack?: string } | null;
  }

  export interface WorkflowPhaseTimeline {
    workflowId: string;
    phases: {
      phase: string;
      startedAt: number;
      endedAt: number | null;   // null if current phase
      durationMs: number;
    }[];
  }

  export interface WorkflowMetrics {
    total: number;
    completed: number;
    failed: number;
    inProgress: number;
    cancelled: number;
    successRate: number;
    successRateTrend: 'up' | 'down' | 'flat';
    avgCompletionTimeMs: number;
    avgCostUsd: number;
    fastest: { timeMs: number; issueNumber: number } | null;
    mostExpensive: { costUsd: number; issueNumber: number } | null;
  }

  export class WorkflowMonitorService {
    constructor(deps: {
      engineRegistry: EngineRegistry;
      costTracker: ICostTracker | null;
    });

    async getActiveWorkflows(): Promise<ActiveWorkflow[]>;
    async getWorkflowDetail(id: string): Promise<WorkflowDetail | null>;
    async getWorkflowTimeline(id: string): Promise<WorkflowPhaseTimeline | null>;
    async getMetrics(options?: { since?: number; until?: number }): Promise<WorkflowMetrics>;
    async getDailyCounts(options?: { since?: number; until?: number }): Promise<{ date: string; completed: number; failed: number }[]>;
    async getFailedWorkflows(): Promise<ActiveWorkflow[]>;
    async retryWorkflow(id: string): Promise<void>;
    async cancelWorkflow(id: string): Promise<void>;
  }
  ```
  - Active workflows = engines not in IDLE state
  - Phase timeline reconstructed from STATE_TRANSITION events: each transition marks start of new phase
  - Metrics: computed from event history (completed = ISSUE_CLOSED events, failed = ERROR_OCCURRED)
  - Duration per phase: time between consecutive STATE_TRANSITION events
  - Cost per phase: aggregate cost events within each phase's time window

- [ ] Create `packages/api/src/services/monitoring/queue-depth-service.ts`:
  ```typescript
  export interface QueueStatus {
    waiting: number;
    waitingIssues: { number: number; title: string; url: string }[];
    inProgress: number;
    inProgressIssues: { number: number; title: string; url: string }[];
    estimatedDrainTimeMs: number | null;
  }

  export class QueueDepthService {
    constructor(deps: {
      configService: ConfigService;
      engineRegistry: EngineRegistry;
      // gitPlatform only if available
    });

    async getQueueStatus(): Promise<QueueStatus>;
    async getQueueTrend(options?: { since?: number; until?: number }): Promise<{ timestamp: number; depth: number }[]>;
  }
  ```
  - Queue: issues matching configured `issueLabels` not assigned to bot
  - In progress: issues assigned to bot (from engine state)
  - Estimated drain time: `(waiting * avgCompletionTimeMs) / activeEngineCount`
  - Queue trend: stored in in-memory buffer, sampled every 60s
  - Caches git platform call with 60s TTL to avoid rate limiting

### Files to Create

- CREATE `packages/api/src/routes/monitoring/workflow-routes.ts`
- CREATE `packages/api/src/services/monitoring/workflow-monitor-service.ts`
- CREATE `packages/api/src/services/monitoring/queue-depth-service.ts`
- CREATE `packages/api/src/routes/monitoring/__tests__/workflow-routes.test.ts`
- CREATE `packages/api/src/services/monitoring/__tests__/workflow-monitor-service.test.ts`
- CREATE `packages/api/src/services/monitoring/__tests__/queue-depth-service.test.ts`

### Files to Modify

- MODIFY `packages/api/src/routes/monitoring/index.ts` -- register workflow routes

### Dependencies

- Story 23-11: route registration, SSE helpers, time-buckets
- EngineRegistry (existing), IEventStore (existing), CostTracker (existing)
- ConfigService for label configuration

## Testing Strategy

### Unit Tests

- [ ] WorkflowMonitorService: active workflows maps from engine registry
- [ ] WorkflowMonitorService: timeline reconstructed from STATE_TRANSITION events
- [ ] WorkflowMonitorService: metrics computed correctly from event history
- [ ] WorkflowMonitorService: retry dispatches start command to engine
- [ ] WorkflowMonitorService: cancel dispatches stop command to engine
- [ ] QueueDepthService: counts waiting and in-progress issues
- [ ] QueueDepthService: estimated drain time calculation
- [ ] QueueDepthService: caches git platform calls for 60s
- [ ] Workflow routes: retry/cancel require settings:manage permission

## Completion Checklist

- [ ] All 11 API endpoints implemented
- [ ] Workflow aggregation from EngineRegistry
- [ ] Phase timeline from STATE_TRANSITION events
- [ ] Queue depth with caching
- [ ] Retry/cancel workflow control
- [ ] Tests written and passing
- [ ] TypeScript strict mode compiles
