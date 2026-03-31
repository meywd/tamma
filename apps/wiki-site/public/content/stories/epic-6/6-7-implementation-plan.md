---
title: "Story 6-7: LLM Cost Monitoring & Reporting - Implementation Plan"
sidebar:
  order: 60
---

## Overview

This document provides a detailed implementation plan for the LLM Cost Monitoring & Reporting system in Tamma. The system will track all LLM API usage across providers, calculate costs in real-time, enforce usage limits, generate reports, and send alerts when thresholds are exceeded.

---

## Package Location

### Primary Package: `@tamma/cost-monitor`

Create a new package at `/packages/cost-monitor/` to encapsulate all cost monitoring functionality. This follows the existing monorepo pattern and keeps concerns separated.

**Rationale:**
- Cost monitoring is a cross-cutting concern used by multiple packages
- The observability package (`@tamma/observability`) is currently focused on logging
- A dedicated package allows independent versioning and testing
- Will be consumed by `@tamma/providers`, `@tamma/orchestrator`, and `@tamma/dashboard`

---

## Files to Create/Modify

### New Files (in `packages/cost-monitor/src/`)

| File | Purpose |
|------|---------|
| `index.ts` | Package exports |
| `types.ts` | All interfaces and type definitions |
| `pricing-config.ts` | Default pricing configuration and utilities |
| `cost-calculator.ts` | Real-time cost calculation logic |
| `usage-tracker.ts` | Usage recording and storage |
| `limit-manager.ts` | Usage limits checking and enforcement |
| `alert-manager.ts` | Alert triggering and delivery |
| `report-generator.ts` | Report generation and scheduling |
| `cost-monitor.ts` | Main service orchestrating all components |
| `storage/index.ts` | Storage interface |
| `storage/in-memory-store.ts` | In-memory implementation for development |
| `storage/postgres-store.ts` | PostgreSQL implementation for production |

### Test Files (in `packages/cost-monitor/src/`)

| File | Purpose |
|------|---------|
| `cost-calculator.test.ts` | Unit tests for cost calculation |
| `usage-tracker.test.ts` | Unit tests for usage tracking |
| `limit-manager.test.ts` | Unit tests for limit checking |
| `alert-manager.test.ts` | Unit tests for alert logic |
| `report-generator.test.ts` | Unit tests for report generation |
| `cost-monitor.test.ts` | Integration tests for the full service |
| `cost-monitor.integration.test.ts` | E2E tests with real storage |

### Files to Modify

| File | Changes |
|------|---------|
| `packages/providers/src/types.ts` | Add `UsageMetadata` to `MessageResponse` |
| `packages/providers/src/agent-types.ts` | Extend `AgentTaskResult` with usage details |
| `packages/providers/src/claude-agent-provider.ts` | Emit usage events after task completion |
| `packages/shared/src/types/index.ts` | Add shared cost-related types |
| `packages/observability/src/index.ts` | Export cost-related logging utilities |
| `packages/dashboard/src/index.tsx` | Add cost dashboard components (Phase 5) |

---

## Interfaces and Types

### Core Types (`packages/cost-monitor/src/types.ts`)

```typescript
/**
 * LLM Provider identifiers
 */
export type Provider =
  | 'anthropic'
  | 'openai'
  | 'openrouter'
  | 'google'
  | 'local'
  | 'claude-code';

/**
 * Agent types in the Tamma system
 */
export type AgentType =
  | 'scrum_master'
  | 'architect'
  | 'researcher'
  | 'analyst'
  | 'planner'
  | 'implementer'
  | 'reviewer'
  | 'tester'
  | 'documenter';

/**
 * Task types for categorization
 */
export type TaskType =
  | 'analysis'
  | 'planning'
  | 'implementation'
  | 'review'
  | 'testing'
  | 'documentation'
  | 'research';

/**
 * Individual usage record for a single LLM call
 */
export interface UsageRecord {
  id: string;
  timestamp: Date;

  // Context
  projectId: string;
  engineId: string;
  agentType: AgentType;
  taskId: string;
  taskType: TaskType;

  // Provider details
  provider: Provider;
  model: string;

  // Usage metrics
  inputTokens: number;
  outputTokens: number;
  totalTokens: number;
  cacheReadTokens?: number;
  cacheWriteTokens?: number;

  // Cost
  inputCostUsd: number;
  outputCostUsd: number;
  totalCostUsd: number;

  // Metadata
  latencyMs: number;
  success: boolean;
  errorCode?: string;
  traceId?: string;
}

/**
 * Aggregated usage data
 */
export interface UsageAggregate {
  dimension: string;
  dimensionValue: string;
  totalCostUsd: number;
  totalInputTokens: number;
  totalOutputTokens: number;
  totalCalls: number;
  successRate: number;
  avgLatencyMs: number;
  period: {
    start: Date;
    end: Date;
  };
}

/**
 * Filter criteria for usage queries
 */
export interface UsageFilter {
  startDate?: Date;
  endDate?: Date;
  projectId?: string;
  engineId?: string;
  agentType?: AgentType;
  taskType?: TaskType;
  provider?: Provider;
  model?: string;
  success?: boolean;
}

/**
 * Grouping dimension for aggregation
 */
export type GroupByDimension =
  | 'provider'
  | 'model'
  | 'project'
  | 'agent_type'
  | 'task_type'
  | 'hour'
  | 'day'
  | 'week'
  | 'month';
```

### Pricing Types

```typescript
/**
 * Pricing tier for different usage patterns
 */
export type PricingTier = 'standard' | 'batch' | 'cached';

/**
 * Pricing for a specific model
 */
export interface ModelPricing {
  inputPer1kTokens: number;   // USD per 1000 input tokens
  outputPer1kTokens: number;  // USD per 1000 output tokens
  contextWindow: number;       // Maximum context size
  tier?: PricingTier;
  cacheReadPer1kTokens?: number;
  cacheWritePer1kTokens?: number;
}

/**
 * Provider pricing configuration
 */
export interface ProviderPricing {
  models: Record<string, ModelPricing>;
  defaultModel: string;
}

/**
 * Complete pricing configuration
 */
export interface PricingConfig {
  providers: Record<Provider, ProviderPricing>;
  lastUpdated: Date;
  currency: string;
}
```

### Limit Types

```typescript
/**
 * Scope for usage limits
 */
export type LimitScope =
  | 'global'
  | 'project'
  | 'provider'
  | 'agent_type'
  | 'model';

/**
 * Time period for limits
 */
export type LimitPeriod = 'daily' | 'weekly' | 'monthly';

/**
 * Action to take when limit is reached
 */
export type LimitAction = 'warn' | 'throttle' | 'block';

/**
 * Usage limit definition
 */
export interface UsageLimit {
  id: string;
  name: string;
  scope: LimitScope;
  scopeId?: string;  // Project ID, provider name, etc.

  period: LimitPeriod;
  limitUsd: number;

  softThreshold: number;  // e.g., 0.7 for 70%
  hardThreshold: number;  // e.g., 1.0 for 100%

  action: LimitAction;
  enabled: boolean;

  createdAt: Date;
  updatedAt: Date;
}

/**
 * Context for checking limits
 */
export interface LimitContext {
  projectId?: string;
  provider?: Provider;
  agentType?: AgentType;
  model?: string;
  estimatedCostUsd?: number;
}

/**
 * Result of a limit check
 */
export interface LimitCheckResult {
  allowed: boolean;
  currentUsageUsd: number;
  limitUsd: number;
  percentUsed: number;
  warnings: string[];
  triggeredLimits: UsageLimit[];
  recommendedAction: 'proceed' | 'use_cheaper_model' | 'wait' | 'abort';
  suggestedAlternatives?: {
    model: string;
    estimatedSavings: number;
  }[];
}
```

### Alert Types

```typescript
/**
 * Types of cost alerts
 */
export type CostAlertType =
  | 'limit_approaching'    // Soft threshold reached
  | 'limit_warning'        // Hard threshold imminent
  | 'limit_exceeded'       // Hard threshold exceeded
  | 'spending_spike'       // Unusual increase detected
  | 'rate_limit_errors'    // Provider rate limits hit
  | 'cost_anomaly';        // Statistical anomaly

/**
 * Alert severity levels
 */
export type AlertSeverity = 'info' | 'warning' | 'critical';

/**
 * Alert status
 */
export type AlertStatus = 'active' | 'acknowledged' | 'resolved';

/**
 * Alert delivery channel
 */
export type AlertChannel = 'cli' | 'webhook' | 'email' | 'slack';

/**
 * Cost alert definition
 */
export interface CostAlert {
  id: string;
  type: CostAlertType;
  severity: AlertSeverity;

  // Context
  scope: LimitScope;
  scopeId?: string;

  // Details
  message: string;
  currentValue: number;
  threshold: number;

  // Status
  status: AlertStatus;
  createdAt: Date;
  acknowledgedAt?: Date;
  acknowledgedBy?: string;
  resolvedAt?: Date;

  // Delivery
  deliveredTo: AlertChannel[];
  deliveryErrors?: Record<AlertChannel, string>;
}

/**
 * Alert channel configuration
 */
export interface AlertChannelConfig {
  type: AlertChannel;
  enabled: boolean;
  url?: string;        // For webhook
  channel?: string;    // For Slack
  recipients?: string[]; // For email
}
```

### Report Types

```typescript
/**
 * Report format options
 */
export type ReportFormat = 'json' | 'csv' | 'pdf' | 'email';

/**
 * Report options
 */
export interface ReportOptions {
  period: LimitPeriod;
  startDate?: Date;
  endDate?: Date;
  groupBy?: GroupByDimension[];
  includeBreakdown: boolean;
  includeTrends: boolean;
  includeForecasting: boolean;
  format: ReportFormat;
}

/**
 * Report schedule configuration
 */
export interface ReportSchedule {
  id: string;
  name: string;
  cron: string;        // Cron expression
  options: ReportOptions;
  recipients: string[];
  enabled: boolean;
}

/**
 * Generated cost report
 */
export interface CostReport {
  id: string;
  generatedAt: Date;
  period: {
    start: Date;
    end: Date;
  };

  // Summary
  summary: {
    totalCostUsd: number;
    totalCalls: number;
    totalInputTokens: number;
    totalOutputTokens: number;
    avgCostPerCall: number;
    successRate: number;
  };

  // Breakdowns
  byProvider?: UsageAggregate[];
  byProject?: UsageAggregate[];
  byAgentType?: UsageAggregate[];
  byModel?: UsageAggregate[];

  // Trends
  trends?: {
    daily: { date: string; costUsd: number }[];
    weekOverWeek: number; // Percentage change
    monthOverMonth: number;
  };

  // Forecasting
  forecast?: {
    projectedMonthEndUsd: number;
    confidence: number;
    budgetStatus: 'under' | 'on_track' | 'over';
  };

  // Optimization
  recommendations?: string[];
}
```

### Service Interfaces

```typescript
/**
 * Cost estimation request
 */
export interface CostEstimateRequest {
  provider: Provider;
  model: string;
  estimatedInputTokens: number;
  estimatedOutputTokens: number;
}

/**
 * Cost estimation result
 */
export interface CostEstimate {
  provider: Provider;
  model: string;
  inputCostUsd: number;
  outputCostUsd: number;
  totalCostUsd: number;
  alternatives?: {
    model: string;
    totalCostUsd: number;
    savings: number;
  }[];
}

/**
 * Main cost monitor service interface
 */
export interface ICostMonitor {
  // Tracking
  recordUsage(usage: Omit<UsageRecord, 'id'>): Promise<UsageRecord>;

  // Queries
  getUsage(filter: UsageFilter): Promise<UsageRecord[]>;
  getAggregate(
    filter: UsageFilter,
    groupBy: GroupByDimension[]
  ): Promise<UsageAggregate[]>;

  // Limits
  checkLimit(context: LimitContext): Promise<LimitCheckResult>;
  setLimit(limit: Omit<UsageLimit, 'id' | 'createdAt' | 'updatedAt'>): Promise<UsageLimit>;
  updateLimit(id: string, updates: Partial<UsageLimit>): Promise<UsageLimit>;
  deleteLimit(id: string): Promise<void>;
  getLimits(): Promise<UsageLimit[]>;

  // Alerts
  getAlerts(filter?: { status?: AlertStatus; severity?: AlertSeverity }): Promise<CostAlert[]>;
  acknowledgeAlert(alertId: string, acknowledgedBy: string): Promise<void>;
  resolveAlert(alertId: string): Promise<void>;

  // Reports
  generateReport(options: ReportOptions): Promise<CostReport>;
  scheduleReport(schedule: Omit<ReportSchedule, 'id'>): Promise<ReportSchedule>;
  getScheduledReports(): Promise<ReportSchedule[]>;
  deleteScheduledReport(id: string): Promise<void>;

  // Estimation
  estimateCost(request: CostEstimateRequest): Promise<CostEstimate>;

  // Configuration
  updatePricing(config: Partial<PricingConfig>): Promise<void>;
  getPricing(): Promise<PricingConfig>;

  // Lifecycle
  dispose(): Promise<void>;
}

/**
 * Storage interface for cost data
 */
export interface ICostStorage {
  // Usage records
  saveUsageRecord(record: UsageRecord): Promise<void>;
  getUsageRecords(filter: UsageFilter): Promise<UsageRecord[]>;

  // Limits
  saveLimitConfig(limit: UsageLimit): Promise<void>;
  getLimitConfigs(): Promise<UsageLimit[]>;
  deleteLimitConfig(id: string): Promise<void>;

  // Alerts
  saveAlert(alert: CostAlert): Promise<void>;
  updateAlert(id: string, updates: Partial<CostAlert>): Promise<void>;
  getAlerts(filter?: { status?: AlertStatus }): Promise<CostAlert[]>;

  // Reports
  saveReportSchedule(schedule: ReportSchedule): Promise<void>;
  getReportSchedules(): Promise<ReportSchedule[]>;
  deleteReportSchedule(id: string): Promise<void>;

  // Aggregation queries
  aggregateUsage(
    filter: UsageFilter,
    groupBy: GroupByDimension[]
  ): Promise<UsageAggregate[]>;
}
```

---

## Implementation Phases

### Phase 1: Core Infrastructure (Week 1-2)

**Goal:** Establish the foundation for cost tracking and calculation.

#### Tasks:

1. **Create package structure**
   - Initialize `@tamma/cost-monitor` package
   - Set up TypeScript configuration
   - Configure dependencies

2. **Implement type definitions** (`types.ts`)
   - All interfaces defined above
   - Export from package index

3. **Implement pricing configuration** (`pricing-config.ts`)
   - Default pricing for all providers
   - Pricing lookup utilities
   - Price update mechanism

4. **Implement cost calculator** (`cost-calculator.ts`)
   - Calculate cost from token usage
   - Support cache pricing
   - Handle unknown models gracefully

5. **Implement in-memory storage** (`storage/in-memory-store.ts`)
   - Full implementation of `ICostStorage`
   - Efficient filtering and aggregation
   - Unit tests

**Deliverables:**
- Working cost calculation
- In-memory storage for development
- Full type definitions

### Phase 2: Usage Tracking (Week 2-3)

**Goal:** Record and query usage data from all providers.

#### Tasks:

1. **Implement usage tracker** (`usage-tracker.ts`)
   - Record usage events
   - Generate unique IDs
   - Validate records

2. **Integrate with providers**
   - Modify `@tamma/providers` to emit usage events
   - Update `ClaudeAgentProvider` to report detailed usage
   - Add hooks for LLM API providers

3. **Implement PostgreSQL storage** (`storage/postgres-store.ts`)
   - Schema design with proper indexes
   - Efficient aggregation queries
   - Migration scripts

4. **Create storage abstraction**
   - Factory for creating storage instances
   - Configuration-based selection

**Deliverables:**
- Full usage tracking from providers
- PostgreSQL persistence
- Query capabilities

### Phase 3: Limits & Enforcement (Week 3-4)

**Goal:** Implement spending limits and enforcement.

#### Tasks:

1. **Implement limit manager** (`limit-manager.ts`)
   - Check current usage against limits
   - Calculate remaining budget
   - Suggest alternatives when blocked

2. **Implement enforcement points**
   - Pre-call limit checks
   - Integration with provider interfaces
   - Graceful degradation

3. **Add limit CRUD operations**
   - Create/update/delete limits
   - Validate limit configurations
   - Handle conflicts

4. **Implement throttling logic**
   - Queue requests when throttled
   - Priority-based execution
   - Timeout handling

**Deliverables:**
- Working limit enforcement
- Soft and hard limits
- Throttling capability

### Phase 4: Alerts & Notifications (Week 4-5)

**Goal:** Alert users when thresholds are exceeded or anomalies detected.

#### Tasks:

1. **Implement alert manager** (`alert-manager.ts`)
   - Alert triggering logic
   - Deduplication
   - Status management

2. **Implement alert channels**
   - CLI notifications
   - Webhook delivery
   - Email (via Resend)
   - Slack integration

3. **Implement anomaly detection**
   - Spending spike detection
   - Rate limit error monitoring
   - Statistical anomaly detection

4. **Add alert configuration**
   - Configurable thresholds
   - Channel preferences
   - Quiet hours

**Deliverables:**
- Multi-channel alert delivery
- Anomaly detection
- Configurable alerts

### Phase 5: Reporting (Week 5-6)

**Goal:** Generate comprehensive cost reports.

#### Tasks:

1. **Implement report generator** (`report-generator.ts`)
   - Report data aggregation
   - Multiple format support
   - Trend calculation

2. **Implement forecasting**
   - Linear projection
   - Budget status calculation
   - Confidence intervals

3. **Implement scheduling**
   - Cron-based scheduling
   - Report delivery
   - Schedule management

4. **Add optimization recommendations**
   - Identify high-cost tasks
   - Suggest cheaper models
   - Detect inefficiencies

**Deliverables:**
- Scheduled reports
- Forecasting
- CSV/JSON export

### Phase 6: Integration & Dashboard (Week 6-7)

**Goal:** Full integration with the Tamma system.

#### Tasks:

1. **Integrate with orchestrator**
   - Pre-task cost checks
   - Post-task usage recording
   - Budget enforcement

2. **Create dashboard components**
   - Real-time cost overview
   - Usage graphs
   - Limit status indicators

3. **CLI integration**
   - Cost summary command
   - Limit management commands
   - Report generation command

4. **Documentation**
   - Configuration guide
   - API documentation
   - Best practices

**Deliverables:**
- Full system integration
- Dashboard UI
- CLI commands

---

## Dependencies

### Internal Dependencies

| Dependency | Purpose |
|------------|---------|
| `@tamma/shared` | Common types, utilities, errors |
| `@tamma/observability` | Logging infrastructure |
| `@tamma/providers` | Provider interfaces for integration |
| `@tamma/events` | Event bus for notifications |

### External Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| `nanoid` | `^5.0.0` | ID generation |
| `dayjs` | `^1.11.0` | Date manipulation |
| `pg` | `^8.13.0` | PostgreSQL client |
| `node-cron` | `^3.0.0` | Report scheduling |
| `resend` | `^6.0.0` | Email delivery |
| `@slack/web-api` | `^7.0.0` | Slack notifications |

### System Dependencies

- PostgreSQL 15+ (production storage)
- Node.js 22+
- TypeScript 5.7+

---

## Testing Strategy

### Unit Tests

**Cost Calculator Tests:**
```typescript
describe('CostCalculator', () => {
  it('calculates cost for known models correctly');
  it('handles cache token pricing');
  it('returns zero for local models');
  it('uses default pricing for unknown models');
  it('handles currency conversion');
});
```

**Limit Manager Tests:**
```typescript
describe('LimitManager', () => {
  it('allows calls within budget');
  it('warns at soft threshold');
  it('blocks at hard threshold');
  it('suggests cheaper alternatives');
  it('handles multiple applicable limits');
  it('respects limit priority');
});
```

**Alert Manager Tests:**
```typescript
describe('AlertManager', () => {
  it('triggers alert at threshold');
  it('deduplicates repeated alerts');
  it('delivers to configured channels');
  it('handles delivery failures gracefully');
  it('detects spending spikes');
});
```

### Integration Tests

**Storage Tests:**
```typescript
describe('PostgresStorage', () => {
  it('persists usage records');
  it('aggregates by dimension correctly');
  it('filters by date range');
  it('handles high write volume');
});
```

**Full System Tests:**
```typescript
describe('CostMonitor Integration', () => {
  it('tracks usage from provider call to storage');
  it('enforces limits across providers');
  it('generates accurate reports');
  it('delivers alerts to all channels');
});
```

### Performance Tests

```typescript
describe('Performance', () => {
  it('handles 1000 usage records per second');
  it('aggregates 1M records in under 5 seconds');
  it('checks limits in under 10ms');
});
```

---

## Configuration

### Environment Variables

```bash
# Database
COST_MONITOR_DB_URL=postgresql://user:pass@localhost:5432/tamma

# Alert Channels
COST_ALERT_WEBHOOK_URL=https://hooks.example.com/alerts
COST_ALERT_SLACK_TOKEN=xoxb-...
COST_ALERT_SLACK_CHANNEL=#tamma-alerts
RESEND_API_KEY=re_...

# Feature Flags
COST_MONITOR_ENABLED=true
COST_MONITOR_STORAGE=postgres  # or 'memory'
```

### YAML Configuration

```yaml
# tamma.config.yaml
cost_monitoring:
  enabled: true
  storage: postgres  # or 'memory' for development

  tracking:
    retention_days: 365
    batch_size: 100
    flush_interval_ms: 5000

  pricing:
    source: builtin  # builtin | api | custom
    update_frequency: daily
    custom_overrides:
      anthropic:
        claude-3-5-sonnet-20241022:
          inputPer1kTokens: 0.003
          outputPer1kTokens: 0.015

  limits:
    - name: Global Monthly
      scope: global
      period: monthly
      limitUsd: 500
      softThreshold: 0.7
      hardThreshold: 0.95
      action: throttle
      enabled: true

    - name: Per Project Daily
      scope: project
      period: daily
      limitUsd: 50
      softThreshold: 0.8
      hardThreshold: 1.0
      action: block
      enabled: true

    - name: Implementer Budget
      scope: agent_type
      scopeId: implementer
      period: daily
      limitUsd: 20
      softThreshold: 0.9
      hardThreshold: 1.0
      action: warn
      enabled: true

  alerts:
    channels:
      - type: cli
        enabled: true
      - type: webhook
        enabled: true
        url: ${COST_ALERT_WEBHOOK_URL}
      - type: slack
        enabled: false
        channel: ${COST_ALERT_SLACK_CHANNEL}
      - type: email
        enabled: false
        recipients:
          - admin@example.com

    rules:
      - type: limit_approaching
        threshold: 0.7
        severity: info
      - type: limit_warning
        threshold: 0.9
        severity: warning
      - type: limit_exceeded
        threshold: 1.0
        severity: critical
      - type: spending_spike
        threshold: 2.0  # 2x normal
        severity: warning

  reports:
    - name: Daily Summary
      schedule: "0 9 * * *"  # 9 AM daily
      format: email
      recipients:
        - team@example.com
      options:
        period: daily
        includeBreakdown: true
        includeTrends: true
        includeForecasting: false

    - name: Weekly Detailed
      schedule: "0 9 * * 1"  # 9 AM Monday
      format: pdf
      recipients:
        - finance@example.com
      options:
        period: weekly
        includeBreakdown: true
        includeTrends: true
        includeForecasting: true
```

---

## Database Schema

### PostgreSQL Tables

```sql
-- Usage records table
CREATE TABLE cost_usage_records (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  timestamp TIMESTAMPTZ NOT NULL DEFAULT NOW(),

  -- Context
  project_id VARCHAR(255) NOT NULL,
  engine_id VARCHAR(255) NOT NULL,
  agent_type VARCHAR(50) NOT NULL,
  task_id VARCHAR(255) NOT NULL,
  task_type VARCHAR(50) NOT NULL,

  -- Provider
  provider VARCHAR(50) NOT NULL,
  model VARCHAR(255) NOT NULL,

  -- Tokens
  input_tokens INTEGER NOT NULL,
  output_tokens INTEGER NOT NULL,
  total_tokens INTEGER NOT NULL,
  cache_read_tokens INTEGER,
  cache_write_tokens INTEGER,

  -- Cost
  input_cost_usd DECIMAL(10, 6) NOT NULL,
  output_cost_usd DECIMAL(10, 6) NOT NULL,
  total_cost_usd DECIMAL(10, 6) NOT NULL,

  -- Metadata
  latency_ms INTEGER NOT NULL,
  success BOOLEAN NOT NULL,
  error_code VARCHAR(100),
  trace_id VARCHAR(255),

  -- Indexes
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_usage_timestamp ON cost_usage_records(timestamp);
CREATE INDEX idx_usage_project ON cost_usage_records(project_id);
CREATE INDEX idx_usage_provider ON cost_usage_records(provider);
CREATE INDEX idx_usage_agent_type ON cost_usage_records(agent_type);
CREATE INDEX idx_usage_composite ON cost_usage_records(timestamp, project_id, provider);

-- Limits table
CREATE TABLE cost_limits (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name VARCHAR(255) NOT NULL,
  scope VARCHAR(50) NOT NULL,
  scope_id VARCHAR(255),
  period VARCHAR(20) NOT NULL,
  limit_usd DECIMAL(10, 2) NOT NULL,
  soft_threshold DECIMAL(3, 2) NOT NULL,
  hard_threshold DECIMAL(3, 2) NOT NULL,
  action VARCHAR(20) NOT NULL,
  enabled BOOLEAN NOT NULL DEFAULT true,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Alerts table
CREATE TABLE cost_alerts (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  type VARCHAR(50) NOT NULL,
  severity VARCHAR(20) NOT NULL,
  scope VARCHAR(50) NOT NULL,
  scope_id VARCHAR(255),
  message TEXT NOT NULL,
  current_value DECIMAL(10, 6) NOT NULL,
  threshold DECIMAL(10, 6) NOT NULL,
  status VARCHAR(20) NOT NULL DEFAULT 'active',
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  acknowledged_at TIMESTAMPTZ,
  acknowledged_by VARCHAR(255),
  resolved_at TIMESTAMPTZ,
  delivered_to JSONB NOT NULL DEFAULT '[]',
  delivery_errors JSONB
);

CREATE INDEX idx_alerts_status ON cost_alerts(status);
CREATE INDEX idx_alerts_created ON cost_alerts(created_at);

-- Report schedules table
CREATE TABLE cost_report_schedules (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name VARCHAR(255) NOT NULL,
  cron VARCHAR(100) NOT NULL,
  options JSONB NOT NULL,
  recipients JSONB NOT NULL,
  enabled BOOLEAN NOT NULL DEFAULT true,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  last_run_at TIMESTAMPTZ
);
```

---

## Success Metrics

| Metric | Target |
|--------|--------|
| Cost tracking accuracy | 100% (verified against provider bills) |
| Usage recording latency | < 50ms (p99) |
| Limit check latency | < 10ms (p99) |
| Alert delivery latency | < 1 minute |
| Report generation time | < 30 seconds |
| Dashboard data freshness | < 5 seconds |
| No unexpected budget overruns | 0 incidents |
| Cost optimization through insights | 20% reduction |

---

## Risks and Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| Pricing data becomes stale | Inaccurate cost tracking | Auto-update mechanism + manual override |
| High write volume impacts performance | Storage latency | Batching + async writes |
| Alert fatigue from too many notifications | Users ignore alerts | Deduplication + severity levels |
| Provider API changes token counting | Cost miscalculation | Provider-specific adapters + validation |
| Storage costs grow with data volume | Infrastructure costs | Retention policies + aggregation |

---

## Future Enhancements

1. **Cost Optimization AI** - Use ML to suggest optimal model selection
2. **Team-based budgets** - Support for organizational hierarchy
3. **Real-time streaming** - WebSocket updates for dashboard
4. **API for external tools** - REST API for cost data
5. **Multi-currency support** - Display costs in user's currency
6. **Cost allocation** - Chargeback to teams/projects
7. **Predictive alerts** - Alert before limits are exceeded
8. **Integration with billing** - Sync with provider billing APIs
