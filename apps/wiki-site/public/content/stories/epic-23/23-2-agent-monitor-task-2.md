---
title: "Task 2: Agent Monitor Frontend Components"
sidebar:
  order: 230
---

**Story:** 23-2-agent-monitor
**Epic:** 23

## Task Description

Build the AgentMonitorPage and all child components: AgentRoleGrid, AgentRoleCard, ProviderChainVisualization, ProviderDetailPanel, ApiKeyStatusPanel, RateLimitDashboard, AgentCostSummary, AgentCostTable, AgentCostTrendChart, and AgentLatencyPanel.

## Acceptance Criteria

- Agent role cards show status, current task, cost, budget bar, provider chain, task counts
- Provider chain visualized as horizontal pipeline with health indicators
- API key panel shows presence, redacted prefix, and live validation
- Rate limit bars show usage percentage per provider+model
- Cost summary with budget alerts, cost trend chart, and detailed breakdown table
- Latency panel with per-agent sparklines and per-provider histograms
- SSE stream for real-time status updates

## Implementation Details

### Technical Requirements

- [ ] Replace placeholder in `packages/dashboard/src/pages/monitoring/AgentMonitorPage.tsx`:
  ```typescript
  export function AgentMonitorPage(): JSX.Element;
  ```
  - Uses `MonitoringLayout` with title "Agent Monitor"
  - SSE connection to `/api/monitoring/agents/stream`
  - Contains: AgentRoleGrid, ApiKeyStatusPanel, RateLimitDashboard, AgentCostSummary, AgentCostTable, AgentCostTrendChart, AgentLatencyPanel

- [ ] Create `packages/dashboard/src/hooks/monitoring/useAgentMonitor.ts`:
  ```typescript
  export interface UseAgentMonitorResult {
    agents: AgentRoleStatus[];
    apiKeys: ApiKeyStatus[];
    rateLimits: RateLimitStatus[];
    costSummary: CostBreakdown | null;
    costTrend: DailyCost[];
    latency: LatencyStats | null;
    loading: boolean;
    error: string | null;
    validateApiKey: (provider: string) => Promise<void>;
    refresh: () => Promise<void>;
  }
  ```

- [ ] Create `packages/dashboard/src/components/monitoring/agents/AgentRoleGrid.tsx`:
  - Renders MetricGrid of AgentRoleCard components

- [ ] Create `packages/dashboard/src/components/monitoring/agents/AgentRoleCard.tsx`:
  ```typescript
  export interface AgentRoleCardProps {
    role: string;
    status: 'idle' | 'busy' | 'error' | 'disabled';
    currentTask: { issueNumber: number; taskType: string; elapsedMs: number } | null;
    phases: string[];
    providerChain: ProviderChainEntry[];
    cost: { today: number; week: number; month: number };
    budgetLimitUsd: number | null;
    budgetUsedPercent: number | null;
    taskCount: { completedToday: number; failedToday: number; successRate: number };
    configured: boolean;
  }
  ```
  - Status-colored left border: idle=gray, busy=blue, error=red, disabled=dark-gray
  - Busy: shows spinner + current task info (issue number, task type, elapsed time)
  - Not configured: red "Not Configured" badge
  - Budget bar: ProgressRing turning red at 80%
  - Compact inline ProviderChainVisualization at bottom

- [ ] Create `packages/dashboard/src/components/monitoring/agents/ProviderChainVisualization.tsx`:
  - Horizontal pipeline: provider boxes connected by arrows
  - Active provider: blue border, highlighted
  - Unhealthy (circuit open): red border, strikethrough text
  - Half-open: yellow border
  - Clicking a provider opens ProviderDetailPanel

- [ ] Create `packages/dashboard/src/components/monitoring/agents/ProviderDetailPanel.tsx`:
  - Expandable panel with: API key status, rate limit, circuit breaker state, failure count, time until circuit closes, last success/error timestamps

- [ ] Create `packages/dashboard/src/components/monitoring/agents/ApiKeyStatusPanel.tsx`:
  - Table: provider, key status (green check / red X / yellow warning), key source env var, redacted value
  - "Validate" button per provider that calls the validate endpoint
  - Shows validation result inline
  - Missing keys surfaced as critical alerts at top

- [ ] Create `packages/dashboard/src/components/monitoring/agents/RateLimitDashboard.tsx`:
  - Per provider+model: ProgressRing or LatencyBar showing rate limit usage
  - Rate limit ceiling, percentage used, reset countdown
  - Yellow highlight for currently rate-limited providers

- [ ] Create `packages/dashboard/src/components/monitoring/agents/AgentCostSummary.tsx`:
  - Total tokens today (input/output/cached)
  - Total cost today per provider per agent role
  - Burn rate USD/hour, projected daily cost
  - Budget alerts at 70% and 100%

- [ ] Create `packages/dashboard/src/components/monitoring/agents/AgentCostTable.tsx`:
  - DataTable: columns Agent Role, Provider, Model, Calls, Input Tokens, Output Tokens, Cost USD, Avg Latency
  - Sortable and filterable

- [ ] Create `packages/dashboard/src/components/monitoring/agents/AgentCostTrendChart.tsx`:
  - TimeSeriesChart of daily cost, stacked by provider

- [ ] Create `packages/dashboard/src/components/monitoring/agents/AgentLatencyPanel.tsx`:
  - Per agent: avg response time, p50/p95/p99, sparkline
  - Per provider: avg API latency, latency histogram (LatencyBar)
  - Top 5 slowest calls in last hour

- [ ] Create `packages/dashboard/src/services/monitoring/agent-api-client.ts`:
  - API client functions for all 9 agent monitoring endpoints

### Files to Create

- CREATE `packages/dashboard/src/components/monitoring/agents/AgentRoleGrid.tsx`
- CREATE `packages/dashboard/src/components/monitoring/agents/AgentRoleCard.tsx`
- CREATE `packages/dashboard/src/components/monitoring/agents/ProviderChainVisualization.tsx`
- CREATE `packages/dashboard/src/components/monitoring/agents/ProviderDetailPanel.tsx`
- CREATE `packages/dashboard/src/components/monitoring/agents/ApiKeyStatusPanel.tsx`
- CREATE `packages/dashboard/src/components/monitoring/agents/RateLimitDashboard.tsx`
- CREATE `packages/dashboard/src/components/monitoring/agents/AgentCostSummary.tsx`
- CREATE `packages/dashboard/src/components/monitoring/agents/AgentCostTable.tsx`
- CREATE `packages/dashboard/src/components/monitoring/agents/AgentCostTrendChart.tsx`
- CREATE `packages/dashboard/src/components/monitoring/agents/AgentLatencyPanel.tsx`
- CREATE `packages/dashboard/src/hooks/monitoring/useAgentMonitor.ts`
- CREATE `packages/dashboard/src/services/monitoring/agent-api-client.ts`

### Files to Modify

- MODIFY `packages/dashboard/src/pages/monitoring/AgentMonitorPage.tsx` -- replace placeholder

### Dependencies

- Story 23-12: MonitoringLayout, MetricCard, MetricGrid, StatusBadge, ProgressRing, LatencyBar, DataTable, TimeSeriesChart
- Task 1: Agent monitoring API endpoints

## Testing Strategy

### Unit Tests

- [ ] AgentRoleCard: renders correct status color and spinner for busy
- [ ] AgentRoleCard: shows "Not Configured" badge when configured=false
- [ ] AgentRoleCard: budget bar turns red at 80%
- [ ] ProviderChainVisualization: renders pipeline with arrows
- [ ] ProviderChainVisualization: active provider has blue border
- [ ] ProviderChainVisualization: unhealthy provider has red border + strikethrough
- [ ] ApiKeyStatusPanel: "Validate" button calls validate endpoint
- [ ] ApiKeyStatusPanel: missing keys shown as critical alerts
- [ ] AgentCostTable: sortable columns, filterable
- [ ] AgentCostTrendChart: renders TimeSeriesChart with daily data
- [ ] useAgentMonitor: fetches all endpoints on mount

## Completion Checklist

- [ ] All 10 child components created
- [ ] AgentMonitorPage wired with MonitoringLayout
- [ ] Hook fetches all agent endpoints
- [ ] SSE stream integrated for real-time updates
- [ ] Tests written and passing
- [ ] TypeScript strict mode compiles
