---
title: "Story 6-7: LLM Cost Monitoring & Reporting"
sidebar:
  order: 60
---

## User Story

As a **Tamma administrator**, I need comprehensive cost monitoring and reporting for all LLM usage so that I can track spending, set budgets, receive alerts, and optimize costs across providers and projects.

## Description

Implement a cost monitoring system that tracks all LLM API usage across providers, calculates costs in real-time, enforces usage limits, generates reports, and sends alerts when thresholds are exceeded.

## Acceptance Criteria

### AC1: Usage Tracking
- [ ] Track all LLM API calls (tokens in, tokens out, model)
- [ ] Track per-provider usage (Anthropic, OpenAI, OpenRouter, Gemini, local)
- [ ] Track per-project usage
- [ ] Track per-agent-type usage (Analyst, Planner, Implementer, etc.)
- [ ] Track per-task usage
- [ ] Store historical usage data

### AC2: Cost Calculation
- [ ] Real-time cost calculation based on provider pricing
- [ ] Support different pricing tiers (input vs output tokens)
- [ ] Support model-specific pricing
- [ ] Currency conversion support
- [ ] Cost estimation before task execution

### AC3: Usage Limits
- [ ] Global spending limits (daily, weekly, monthly)
- [ ] Per-project spending limits
- [ ] Per-provider spending limits
- [ ] Per-agent-type spending limits
- [ ] Soft limits (warning) and hard limits (block)
- [ ] Graceful degradation when limits approached

### AC4: Alerts & Notifications
- [ ] Alert when approaching limit (70%, 90%, 100%)
- [ ] Alert on unusual spending patterns
- [ ] Alert on rate limit errors
- [ ] Alert on cost anomalies
- [ ] Multi-channel delivery (CLI, webhook, email, Slack)

### AC5: Reporting
- [ ] Daily/weekly/monthly cost reports
- [ ] Cost breakdown by provider, project, agent type
- [ ] Cost trends and forecasting
- [ ] Export to CSV/JSON
- [ ] Scheduled report delivery

### AC6: Dashboard
- [ ] Real-time cost overview
- [ ] Usage graphs and charts
- [ ] Limit status indicators
- [ ] Cost comparison views
- [ ] Drill-down by dimension

### AC7: Optimization Recommendations
- [ ] Identify high-cost tasks
- [ ] Suggest cheaper model alternatives
- [ ] Detect inefficient token usage
- [ ] Recommend caching opportunities

## Technical Design

### Cost Tracking Schema

```typescript
interface UsageRecord {
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

  // Cost
  inputCostUsd: number;
  outputCostUsd: number;
  totalCostUsd: number;

  // Metadata
  latencyMs: number;
  success: boolean;
  errorCode?: string;
}

type Provider = 'anthropic' | 'openai' | 'openrouter' | 'google' | 'local' | 'claude-code';
type AgentType = 'scrum_master' | 'architect' | 'researcher' | 'analyst' | 'planner' | 'implementer' | 'reviewer' | 'tester' | 'documenter';
```

### Pricing Configuration

```typescript
interface PricingConfig {
  providers: Record<Provider, ProviderPricing>;
  lastUpdated: Date;
}

interface ProviderPricing {
  models: Record<string, ModelPricing>;
  defaultModel: string;
}

interface ModelPricing {
  inputPer1kTokens: number;   // USD
  outputPer1kTokens: number;  // USD
  contextWindow: number;
  tier?: 'standard' | 'batch' | 'cached';
}

// Example pricing (as of 2024)
const defaultPricing: PricingConfig = {
  providers: {
    anthropic: {
      models: {
        'claude-3-5-sonnet-20241022': { inputPer1kTokens: 0.003, outputPer1kTokens: 0.015, contextWindow: 200000 },
        'claude-3-opus-20240229': { inputPer1kTokens: 0.015, outputPer1kTokens: 0.075, contextWindow: 200000 },
        'claude-3-haiku-20240307': { inputPer1kTokens: 0.00025, outputPer1kTokens: 0.00125, contextWindow: 200000 },
      },
      defaultModel: 'claude-3-5-sonnet-20241022',
    },
    openai: {
      models: {
        'gpt-4o': { inputPer1kTokens: 0.005, outputPer1kTokens: 0.015, contextWindow: 128000 },
        'gpt-4o-mini': { inputPer1kTokens: 0.00015, outputPer1kTokens: 0.0006, contextWindow: 128000 },
      },
      defaultModel: 'gpt-4o',
    },
    // ... other providers
  },
  lastUpdated: new Date('2024-11-01'),
};
```

### Cost Monitor Service

```typescript
interface ICostMonitor {
  // Tracking
  recordUsage(usage: UsageRecord): Promise<void>;

  // Queries
  getUsage(filter: UsageFilter): Promise<UsageRecord[]>;
  getAggregate(filter: UsageFilter, groupBy: GroupByDimension[]): Promise<UsageAggregate[]>;

  // Limits
  checkLimit(context: LimitContext): Promise<LimitCheckResult>;
  setLimit(limit: UsageLimit): Promise<void>;
  getLimits(): Promise<UsageLimit[]>;

  // Alerts
  getAlertStatus(): Promise<AlertStatus[]>;
  acknowledgeAlert(alertId: string): Promise<void>;

  // Reports
  generateReport(options: ReportOptions): Promise<CostReport>;
  scheduleReport(schedule: ReportSchedule): Promise<void>;

  // Estimation
  estimateCost(request: CostEstimateRequest): Promise<CostEstimate>;
}

interface UsageLimit {
  id: string;
  name: string;
  scope: LimitScope;
  scopeId?: string;  // Project ID, provider name, etc.

  period: 'daily' | 'weekly' | 'monthly';
  limitUsd: number;

  softThreshold: number;  // e.g., 0.7 for 70%
  hardThreshold: number;  // e.g., 1.0 for 100%

  action: 'warn' | 'throttle' | 'block';
  enabled: boolean;
}

type LimitScope = 'global' | 'project' | 'provider' | 'agent_type' | 'model';

interface LimitCheckResult {
  allowed: boolean;
  currentUsage: number;
  limit: number;
  percentUsed: number;
  warnings: string[];
  recommendedAction?: 'proceed' | 'use_cheaper_model' | 'wait' | 'abort';
}
```

### Alert Configuration

```typescript
interface CostAlert {
  id: string;
  type: CostAlertType;
  severity: 'info' | 'warning' | 'critical';

  // Context
  scope: LimitScope;
  scopeId?: string;

  // Details
  message: string;
  currentValue: number;
  threshold: number;

  // Status
  status: 'active' | 'acknowledged' | 'resolved';
  createdAt: Date;
  acknowledgedAt?: Date;
  acknowledgedBy?: string;
}

type CostAlertType =
  | 'limit_approaching'    // 70% of limit
  | 'limit_warning'        // 90% of limit
  | 'limit_exceeded'       // 100% of limit
  | 'spending_spike'       // Unusual increase
  | 'rate_limit_errors'    // Provider rate limits
  | 'cost_anomaly';        // Statistical anomaly
```

### Dashboard Components

```
┌─────────────────────────────────────────────────────────────────────────────────┐
│  LLM COST MONITOR                                              February 2026   │
├─────────────────────────────────────────────────────────────────────────────────┤
│                                                                                  │
│  ┌──────────────────────────────────────────────────────────────────────────┐  │
│  │  SPENDING OVERVIEW                                                        │  │
│  │                                                                           │  │
│  │  Today        This Week      This Month     Month Forecast                │  │
│  │  $12.45       $67.89         $234.56        $312.00                       │  │
│  │  ▲ 15%        ▲ 8%           ▼ 3%           Within budget                 │  │
│  │                                                                           │  │
│  └──────────────────────────────────────────────────────────────────────────┘  │
│                                                                                  │
│  ┌──────────────────────────────────────────────────────────────────────────┐  │
│  │  LIMITS STATUS                                                            │  │
│  │                                                                           │  │
│  │  Global Monthly     ████████████████░░░░░░░░  67% ($234 / $350)          │  │
│  │  Project: repo-a    ██████████████████████░░  89% ($89 / $100)  ⚠️       │  │
│  │  Project: repo-b    ████████░░░░░░░░░░░░░░░░  34% ($34 / $100)           │  │
│  │  Anthropic          ████████████████████░░░░  82% ($164 / $200)          │  │
│  │  OpenRouter         ██████░░░░░░░░░░░░░░░░░░  28% ($28 / $100)           │  │
│  │                                                                           │  │
│  └──────────────────────────────────────────────────────────────────────────┘  │
│                                                                                  │
│  ┌────────────────────────────────┐  ┌────────────────────────────────────┐   │
│  │  BY PROVIDER                   │  │  BY AGENT TYPE                     │   │
│  │                                │  │                                    │   │
│  │  Anthropic    ████████  65%   │  │  Implementer  ████████████  52%   │   │
│  │  OpenRouter   ████      18%   │  │  Planner      ████          18%   │   │
│  │  OpenAI       ███       12%   │  │  Analyst      ███           14%   │   │
│  │  Local        █          5%   │  │  Reviewer     ██            10%   │   │
│  │                                │  │  Other        █              6%   │   │
│  │                                │  │                                    │   │
│  └────────────────────────────────┘  └────────────────────────────────────┘   │
│                                                                                  │
│  ┌──────────────────────────────────────────────────────────────────────────┐  │
│  │  RECENT ALERTS                                                            │  │
│  │                                                                           │  │
│  │  ⚠️  Project repo-a approaching limit (89%)           2 hours ago  [Ack] │  │
│  │  ℹ️  Weekly report generated                          1 day ago          │  │
│  │  ✓  Limit increased for Project repo-b               3 days ago         │  │
│  │                                                                           │  │
│  └──────────────────────────────────────────────────────────────────────────┘  │
│                                                                                  │
└─────────────────────────────────────────────────────────────────────────────────┘
```

## Configuration

```yaml
cost_monitoring:
  tracking:
    enabled: true
    retention_days: 365

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

    - name: Per Project Daily
      scope: project
      period: daily
      limitUsd: 50
      softThreshold: 0.8
      hardThreshold: 1.0
      action: block

  alerts:
    channels:
      - type: cli
        enabled: true
      - type: webhook
        enabled: true
        url: ${ALERT_WEBHOOK_URL}
      - type: slack
        enabled: false
        channel: "#tamma-alerts"

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
      recipients: ["team@example.com"]
    - name: Weekly Detailed
      schedule: "0 9 * * 1"  # 9 AM Monday
      format: pdf
      recipients: ["finance@example.com"]
```

## Dependencies

- All provider implementations (track usage)
- Alert Manager (Story 5-6)
- Dashboard infrastructure (Epic 5)
- Database for usage storage

## Testing Strategy

### Unit Tests
- Cost calculation accuracy
- Limit checking logic
- Alert triggering conditions
- Report generation

### Integration Tests
- Usage recording from providers
- Alert delivery
- Report scheduling
- Dashboard data accuracy

## Success Metrics

- Cost tracking accuracy: 100%
- Alert delivery latency < 1 minute
- No unexpected budget overruns
- 20% cost optimization through insights

## Logging Requirements

All intelligence modules MUST accept `ILogger` from `@tamma/shared/contracts` via constructor injection.

- **INFO**: Index scan started/completed (file count, duration), vector search executed (query, result count), RAG pipeline invoked, knowledge base query matched
- **DEBUG**: Chunk boundaries, embedding dimensions, similarity scores, cache hit/miss, budget allocation
- **WARN**: Embedding API rate limited, vector store connection degraded, stale index detected, budget threshold approaching
- **ERROR**: Embedding generation failed, vector store unreachable, index corruption detected, MCP tool call failed
- **Structured context**: Always include `{ repository, indexId, queryId, sourceType }` where applicable
- **Performance**: Log duration for all external API calls (embedding, vector search, MCP tool invocations)
- **Cost tracking**: Log token counts for embedding operations to support LLM cost monitoring (Story 6-7)
