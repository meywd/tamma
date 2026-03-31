# Story 23-2: Agent Monitor (Realtime)

Status: planned

## Summary

Build a real-time monitoring screen for all configured agent roles, their provider chains, operational status, cost tracking, rate limits, and API key validation. This screen is the operator's window into which AI providers are active, healthy, and within budget.

## Acceptance Criteria

### Agent Role Overview

1. The page displays a card for each configured agent role from `IAgentsConfig`:
   - `scrum_master`, `architect`, `researcher`, `analyst`, `planner`, `implementer`, `reviewer`, `tester`, `documenter`
2. Each agent role card shows:
   - Role name and description
   - Current status: idle (gray), busy (blue with spinner), error (red), disabled (dark gray)
   - Current task description (if busy): issue number, task type, elapsed time
   - Assigned workflow phase(s) from `phaseRoleMap` (e.g., implementer -> CODE_GENERATION, PR_CREATION)
   - Provider chain visualization (see below)
   - Total cost accrued: today, this week, this month
   - Budget limit and percentage used (progress bar, turns red at 80%)
   - Task count: completed today, failed today, success rate
3. Roles with NO provider chain configured are highlighted with a red "Not Configured" badge.
4. Roles that exist in `DEFAULT_PHASE_ROLE_MAP` but have no config override show "Using defaults" in gray.

### Provider Chain Visualization

5. For each agent role, the provider chain is visualized as a horizontal pipeline:
   - Each provider entry shows: provider name, model (or "default"), status icon
   - Arrows connect entries left-to-right (primary -> fallback 1 -> fallback 2)
   - Active provider (currently being used) is highlighted with a blue border
   - Unhealthy providers (circuit open) are shown with a red border and strikethrough
   - Half-open providers are shown with a yellow border
6. Clicking a provider entry expands a detail panel showing:
   - API key status: valid / expired / missing / not configured
   - Rate limit status: current usage / limit (if available from provider)
   - Circuit breaker state: closed (green) / open (red) / half-open (yellow)
   - Failure count in current window
   - Time until circuit closes (if open)
   - Last successful call timestamp
   - Last error message and timestamp

### API Key Validation

7. A dedicated "API Key Status" panel shows:
   - Each provider that requires an API key
   - Key status: configured (green check), missing (red X), expired (yellow warning)
   - Key source: env var name (e.g., `ANTHROPIC_API_KEY`), redacted value (first 4 chars + `***`)
   - "Validate" button that makes a lightweight API call to verify the key works
   - Validation result: success / invalid / rate limited / network error
8. Missing API keys are surfaced as critical alerts at the top of the page.

### Rate Limit Dashboard

9. For each provider+model combination that has been used:
   - Current rate limit usage (requests per minute)
   - Rate limit ceiling (if known from provider response headers)
   - Percentage used as a progress bar
   - Time until rate limit resets
   - Historical rate limit hits (count per hour over last 24h)
10. Providers currently being rate-limited are highlighted in yellow.

### Token Usage & Cost Tracking

11. A summary panel shows:
    - Total tokens used today: input / output / cached
    - Total cost today: per provider, per agent role
    - Budget burn rate: USD/hour averaged over last 4 hours
    - Projected daily cost based on current burn rate
    - Budget alerts: approaching limit (>70%), exceeded (>100%)
12. A cost breakdown table shows per-agent, per-provider, per-model granularity:
    - Columns: Agent Role, Provider, Model, Calls, Input Tokens, Output Tokens, Cost USD, Avg Latency
    - Sortable by any column
    - Filterable by time range
13. A cost trend chart shows daily cost over the selected time range, broken down by provider.

### Response Time Monitoring

14. Per agent role:
    - Average response time for last 10 tasks
    - p50, p95, p99 latency
    - Latency trend sparkline (last 24 data points)
15. Per provider:
    - Average API call latency
    - Latency histogram (10ms buckets)
    - Slowest calls in last hour (top 5)

## API Endpoints Needed

- GET /api/monitoring/agents/status -- returns per-role status, current task, cost, provider chain health
- GET /api/monitoring/agents/provider-chains -- returns full chain configuration with health overlay
- GET /api/monitoring/agents/api-keys/status -- returns API key validation status per provider (redacted)
- POST /api/monitoring/agents/api-keys/validate -- triggers live validation of a specific provider's API key
- GET /api/monitoring/agents/rate-limits -- returns rate limit status per provider+model
- GET /api/monitoring/agents/cost-summary -- returns cost breakdown by agent, provider, model with time filtering
- GET /api/monitoring/agents/cost-trend -- returns daily cost time series for chart
- GET /api/monitoring/agents/latency -- returns latency stats per agent role and provider
- GET /api/monitoring/agents/stream -- SSE stream of agent status changes

## Dashboard Components

- `AgentMonitorPage` -- page container
- `AgentRoleGrid` -- grid of AgentRoleCards
- `AgentRoleCard` -- single agent role with status, cost, chain preview
- `ProviderChainVisualization` -- horizontal pipeline of providers with health
- `ProviderDetailPanel` -- expandable detail for a single provider entry
- `ApiKeyStatusPanel` -- API key configuration and validation
- `ApiKeyStatusRow` -- single provider key status
- `RateLimitDashboard` -- rate limit status per provider
- `RateLimitBar` -- single provider rate limit progress
- `AgentCostSummary` -- cost totals with budget bars
- `AgentCostTable` -- detailed cost breakdown table
- `AgentCostTrendChart` -- daily cost time series
- `AgentLatencyPanel` -- response time stats per agent
- `ProviderLatencyHistogram` -- latency distribution chart

## Data Sources

- ConfigService.getAgentsConfig() (existing) -- agent role configuration
- HealthService.getStatus() (existing) -- provider health/circuit breaker state
- DiagnosticsService.getEvents() (existing) -- provider call/error events
- CostTracker.getAggregate() (existing) -- cost aggregation by agent, provider, model
- CostTracker.checkLimit() (existing) -- budget limit status
- EngineRegistry.list() (existing) -- active engines and their states
- ProviderHealthTracker.getStatus() (existing) -- circuit breaker per provider+model
- Environment variables (for API key presence check)

## Implementation Notes

- API key validation endpoint must NOT return the actual key. It returns: `{ provider: string, keyPresent: boolean, keyPrefix: string, valid: boolean | null, error?: string }`.
- Rate limit data: extracted from provider response headers (`x-ratelimit-remaining`, `x-ratelimit-limit`, `x-ratelimit-reset`) and stored in the DiagnosticsService events.
- "Busy" status is determined by checking if any engine in the EngineRegistry is in a state that maps to this role's phase (via `ENGINE_STATE_TO_PHASE` and `phaseRoleMap`).
- Cost data is retrieved from the existing CostTracker aggregation API, not recomputed.
- The SSE stream emits events when: engine state changes, circuit breaker state changes, cost threshold crossed.

## Files to Create

- `packages/api/src/routes/monitoring/agent-routes.ts`
- `packages/api/src/services/monitoring/agent-status-service.ts`
- `packages/api/src/services/monitoring/api-key-validator.ts`
- `packages/dashboard/src/pages/monitoring/AgentMonitorPage.tsx`
- `packages/dashboard/src/components/monitoring/agents/AgentRoleGrid.tsx`
- `packages/dashboard/src/components/monitoring/agents/AgentRoleCard.tsx`
- `packages/dashboard/src/components/monitoring/agents/ProviderChainVisualization.tsx`
- `packages/dashboard/src/components/monitoring/agents/ProviderDetailPanel.tsx`
- `packages/dashboard/src/components/monitoring/agents/ApiKeyStatusPanel.tsx`
- `packages/dashboard/src/components/monitoring/agents/RateLimitDashboard.tsx`
- `packages/dashboard/src/components/monitoring/agents/AgentCostSummary.tsx`
- `packages/dashboard/src/components/monitoring/agents/AgentCostTable.tsx`
- `packages/dashboard/src/components/monitoring/agents/AgentCostTrendChart.tsx`
- `packages/dashboard/src/components/monitoring/agents/AgentLatencyPanel.tsx`
- `packages/dashboard/src/hooks/monitoring/useAgentMonitor.ts`
- Tests for all API routes, services, and components
