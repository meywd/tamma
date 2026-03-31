---
title: "Task 1: Agent Monitor API Routes & Services"
sidebar:
  order: 230
---

**Story:** 23-2-agent-monitor
**Epic:** 23

## Task Description

Create the backend API routes and services for the agent monitor: agent status aggregation, provider chain health, API key validation, rate limit tracking, cost summary, and latency stats. Serves 9 endpoints under `/api/monitoring/agents/*`.

## Acceptance Criteria

- `GET /api/monitoring/agents/status` returns per-role status with current task, cost, and provider chain health
- `GET /api/monitoring/agents/provider-chains` returns full chain config with health overlay
- `GET /api/monitoring/agents/api-keys/status` returns API key validation status per provider (redacted)
- `POST /api/monitoring/agents/api-keys/validate` triggers live validation of a specific provider's API key
- `GET /api/monitoring/agents/rate-limits` returns rate limit status per provider+model
- `GET /api/monitoring/agents/cost-summary` returns cost breakdown with time filtering
- `GET /api/monitoring/agents/cost-trend` returns daily cost time series
- `GET /api/monitoring/agents/latency` returns latency stats per agent role and provider
- `GET /api/monitoring/agents/stream` SSE stream of agent status changes

## Implementation Details

### Technical Requirements

- [ ] Create `packages/api/src/routes/monitoring/agent-routes.ts`:
  ```typescript
  export function registerAgentMonitoringRoutes(
    app: FastifyInstance,
    agentStatusService: AgentStatusService,
    apiKeyValidator: ApiKeyValidator,
    metricsCollector: MetricsCollector,
  ): void;
  ```
  - All 9 route handlers delegating to service methods
  - SSE stream emits on engine state changes, circuit breaker state changes, cost threshold crossings
  - API key validate endpoint: `POST /agents/api-keys/validate` body `{ provider: string }`

- [ ] Create `packages/api/src/services/monitoring/agent-status-service.ts`:
  ```typescript
  export interface AgentRoleStatus {
    role: string;
    description: string;
    status: 'idle' | 'busy' | 'error' | 'disabled';
    currentTask: {
      issueNumber: number;
      taskType: string;
      elapsedMs: number;
    } | null;
    phases: string[];             // from phaseRoleMap
    providerChain: ProviderChainEntry[];
    cost: { today: number; week: number; month: number };
    budgetLimitUsd: number | null;
    budgetUsedPercent: number | null;
    taskCount: { completedToday: number; failedToday: number; successRate: number };
    configured: boolean;           // false if no provider chain
  }

  export interface ProviderChainEntry {
    provider: string;
    model: string | null;
    isActive: boolean;             // currently being used
    circuitState: 'closed' | 'open' | 'half-open';
    healthy: boolean;
  }

  export class AgentStatusService {
    constructor(deps: {
      configService: ConfigService;
      engineRegistry: EngineRegistry;
      healthService: HealthService;
      diagnosticsService: DiagnosticsService;
      costTracker: ICostTracker | null;
    });

    async getAgentStatuses(): Promise<AgentRoleStatus[]>;
    async getProviderChains(): Promise<Record<string, ProviderChainEntry[]>>;
    async getCostSummary(options?: { since?: number; until?: number }): Promise<CostBreakdown>;
    async getCostTrend(options?: { since?: number; until?: number }): Promise<DailyCost[]>;
    async getLatencyStats(): Promise<LatencyStats>;
    async getRateLimits(): Promise<RateLimitStatus[]>;
  }
  ```
  - Agent role list from `IAgentsConfig`: scrum_master, architect, researcher, analyst, planner, implementer, reviewer, tester, documenter
  - "busy" status: determined by checking EngineRegistry for engines in states mapping to this role's phase
  - Cost data from CostTracker.getAggregate() filtered by agent role
  - Latency from DiagnosticsService events of type `provider:complete`

- [ ] Create `packages/api/src/services/monitoring/api-key-validator.ts`:
  ```typescript
  export interface ApiKeyStatus {
    provider: string;
    keyPresent: boolean;
    keyPrefix: string;            // first 4 chars + "***"
    valid: boolean | null;        // null = not checked yet
    error: string | null;
    lastValidated: string | null;
  }

  export class ApiKeyValidator {
    constructor(deps: { configService: ConfigService });
    async getKeyStatuses(): Promise<ApiKeyStatus[]>;
    async validateKey(provider: string): Promise<ApiKeyStatus>;
  }
  ```
  - `getKeyStatuses()` scans configured provider chains for API key references
  - Checks `process.env[apiKeyRef]` for presence
  - Returns redacted prefix only (never full key)
  - `validateKey()` attempts a lightweight API call (e.g., list models) to verify the key works

### Files to Create

- CREATE `packages/api/src/routes/monitoring/agent-routes.ts`
- CREATE `packages/api/src/services/monitoring/agent-status-service.ts`
- CREATE `packages/api/src/services/monitoring/api-key-validator.ts`
- CREATE `packages/api/src/routes/monitoring/__tests__/agent-routes.test.ts`
- CREATE `packages/api/src/services/monitoring/__tests__/agent-status-service.test.ts`
- CREATE `packages/api/src/services/monitoring/__tests__/api-key-validator.test.ts`

### Files to Modify

- MODIFY `packages/api/src/routes/monitoring/index.ts` -- register agent monitoring routes
- MODIFY `packages/api/src/routes/monitoring/types.ts` -- add agent services to MonitoringServices

### Dependencies

- Story 23-11: route registration, SSE helpers, types
- ConfigService (existing), EngineRegistry (existing), HealthService (existing)
- DiagnosticsService (existing), CostTracker (existing)

## Testing Strategy

### Unit Tests

- [ ] AgentStatusService: returns status for all 9 agent roles
- [ ] AgentStatusService: busy status when engine in matching phase
- [ ] AgentStatusService: unconfigured role flagged as configured=false
- [ ] AgentStatusService: cost breakdown correctly aggregated
- [ ] ApiKeyValidator: returns redacted key prefix (never full key)
- [ ] ApiKeyValidator: keyPresent=false when env var not set
- [ ] ApiKeyValidator: validateKey catches API errors gracefully
- [ ] Agent routes: GET /agents/status returns expected structure
- [ ] Agent routes: POST /agents/api-keys/validate triggers validation

## Completion Checklist

- [ ] All 9 API endpoints implemented
- [ ] Agent status aggregation from multiple services
- [ ] API key validation never exposes full keys
- [ ] SSE stream for real-time updates
- [ ] Tests written and passing
- [ ] TypeScript strict mode compiles
