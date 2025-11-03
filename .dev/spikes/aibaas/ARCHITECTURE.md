# AI Benchmarking as a Service (AIBaaS) - Architecture Document

**Status**: Draft
**Version**: 1.0.0
**Date**: 2024-11-01
**Author**: Tamma Development Team
**Related**: ADR-004 (AI Benchmarking Service Evolution)

---

## Table of Contents

1. [Executive Summary](#executive-summary)
2. [Vision & Goals](#vision--goals)
3. [System Architecture](#system-architecture)
4. [Data Model](#data-model)
5. [Benchmark Suite Design](#benchmark-suite-design)
6. [UI/UX Enhancements](#uiux-enhancements)
7. [API Design](#api-design)
8. [Real-Time Infrastructure](#real-time-infrastructure)
9. [Background Job System](#background-job-system)
10. [Security & Privacy](#security--privacy)
11. [Scalability Strategy](#scalability-strategy)
12. [Deployment Architecture](#deployment-architecture)
13. [Monitoring & Observability](#monitoring--observability)
14. [Implementation Roadmap](#implementation-roadmap)
15. [Cost Analysis](#cost-analysis)
16. [Success Metrics](#success-metrics)

---

## Executive Summary

**AIBaaS** (AI Benchmarking as a Service) is a continuous, web-based platform for monitoring and comparing AI model performance across standardized development workflow scenarios. It transforms Tamma's existing CLI-based benchmarking tool into a production-grade service that serves both internal needs (continuous provider monitoring) and external users (public leaderboards, API access).

### Current State

- ✅ CLI-based benchmark tool (`run-benchmark.ts`)
- ✅ Multi-provider support (5+ providers)
- ✅ Automated scoring (objective, 0-10 scale)
- ✅ 4 standardized scenarios (issue analysis, code gen, test gen, code review)
- ✅ GitHub Actions integration
- ✅ Statistical analysis (mean, stddev, confidence)

### Competitive Intelligence

**Based on analysis of 17+ benchmarks** (Aider, LiveBench, LiveCodeBench Pro, MASK, SimpleBench, VirologyTest, Vectara HHEM, HLE, ARC-AGI, Cybench, and others), AIBaaS has **unique competitive advantages**:

- **ONLY** benchmark combining: real-time monitoring + cost tracking + latency tracking + historical data + API access
- **ONLY** developer-focused benchmark with human percentile rankings and contamination prevention
- **ONLY** service offering alerting, custom scenarios, and SLA monitoring for AI code quality

### Target State

- 🎯 Web-based dashboard with real-time leaderboards
- 🎯 REST + GraphQL APIs for programmatic access
- 🎯 Continuous monitoring with scheduled benchmark runs
- 🎯 Historical trend analysis with TimescaleDB
- 🎯 Alerting system for performance degradation
- 🎯 Public service with tiered access (Free/Pro/Enterprise)
- 🎯 Revenue generation ($29/month Pro tier)

### Key Differentiators

| Feature | ChatBotArena | HuggingFace | AIBaaS |
|---------|-------------|-------------|--------|
| Developer Tasks | ❌ | ⚠️ Academic | ✅ Real-world |
| Cost Analysis | ❌ | ❌ | ✅ Per-task pricing |
| Continuous Monitoring | ❌ | ❌ | ✅ Scheduled runs |
| Latency Tracking | ❌ | ❌ | ✅ Response time |
| Historical Trends | ❌ | ❌ | ✅ TimescaleDB |
| API Access | ❌ | ⚠️ Limited | ✅ REST + GraphQL |

---

## Vision & Goals

### Primary Goals

1. **Internal Use (Tamma)**:
   - Continuous monitoring of AI provider quality/cost/latency
   - Automated alerts when provider performance degrades
   - Data-driven provider selection for autonomous workflows
   - Historical analysis for cost optimization

2. **External Use (Public)**:
   - Real-time leaderboards for AI model comparison
   - API access for integration into developer tools
   - Custom benchmark scenarios for specific use cases
   - Transparent, objective provider comparisons

3. **Revenue Generation**:
   - Free tier: Public leaderboard, 7-day history
   - Pro tier: $29/month - API access, custom scenarios, 90-day history
   - Enterprise tier: Custom pricing - White-label, SLA, unlimited history

### Non-Goals (Out of Scope)

- ❌ Training custom AI models
- ❌ Hosting AI models
- ❌ Chat interface for end-users
- ❌ Academic benchmark datasets (GLUE, SuperGLUE, etc.)
- ❌ Non-developer use cases (creative writing, image generation)

---

## System Architecture

### High-Level Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                        User-Facing Layer                         │
├─────────────────────────────────────────────────────────────────┤
│                                                                   │
│  ┌────────────────┐    ┌────────────────┐    ┌──────────────┐  │
│  │   Web UI       │    │   REST API     │    │  GraphQL API │  │
│  │  (Next.js 15)  │◄───┤   (Fastify)    │◄───┤   (Apollo)   │  │
│  │  React 19      │    │   TypeScript   │    │   Server     │  │
│  └────────────────┘    └────────────────┘    └──────────────┘  │
│         │                      │                      │          │
│         │                      │                      │          │
│         └──────────────────────┼──────────────────────┘          │
│                                ▼                                  │
│                    ┌───────────────────────┐                     │
│                    │   Service Layer       │                     │
│                    │   (Business Logic)    │                     │
│                    └───────────────────────┘                     │
│                                │                                  │
│         ┌──────────────────────┼──────────────────────┐          │
│         ▼                      ▼                      ▼          │
│  ┌──────────────┐    ┌──────────────┐    ┌──────────────┐      │
│  │  Benchmark   │    │  Analytics   │    │   Alerting   │      │
│  │  Service     │    │  Service     │    │   Service    │      │
│  └──────────────┘    └──────────────┘    └──────────────┘      │
│         │                      │                      │          │
│         └──────────────────────┼──────────────────────┘          │
│                                ▼                                  │
├─────────────────────────────────────────────────────────────────┤
│                         Data Layer                               │
├─────────────────────────────────────────────────────────────────┤
│                                                                   │
│  ┌────────────────────────────────────────────────────────┐     │
│  │              PostgreSQL 17 + TimescaleDB               │     │
│  │  ┌───────────┐  ┌──────────┐  ┌──────────────────┐    │     │
│  │  │ Providers │  │  Models  │  │  Benchmark Runs  │    │     │
│  │  └───────────┘  └──────────┘  └──────────────────┘    │     │
│  │  ┌───────────┐  ┌──────────┐  ┌──────────────────┐    │     │
│  │  │  Alerts   │  │  Users   │  │  API Requests    │    │     │
│  │  └───────────┘  └──────────┘  └──────────────────┘    │     │
│  └────────────────────────────────────────────────────────┘     │
│                                                                   │
│  ┌─────────────┐    ┌──────────────┐    ┌──────────────┐       │
│  │   Redis     │    │   BullMQ     │    │  S3 Storage  │       │
│  │  (Cache +   │    │  (Job Queue) │    │  (Archives)  │       │
│  │   PubSub)   │    │              │    │              │       │
│  └─────────────┘    └──────────────┘    └──────────────┘       │
│                                                                   │
├─────────────────────────────────────────────────────────────────┤
│                     Background Workers                           │
├─────────────────────────────────────────────────────────────────┤
│                                                                   │
│  ┌────────────────┐  ┌────────────────┐  ┌────────────────┐    │
│  │  Benchmark     │  │  Model         │  │  Analytics     │    │
│  │  Runner        │  │  Discovery     │  │  Processor     │    │
│  │  Worker        │  │  Worker        │  │  Worker        │    │
│  └────────────────┘  └────────────────┘  └────────────────┘    │
│                                                                   │
│  ┌────────────────┐  ┌────────────────┐  ┌────────────────┐    │
│  │  Alert         │  │  Report        │  │  Cleanup       │    │
│  │  Processor     │  │  Generator     │  │  Worker        │    │
│  │  Worker        │  │  Worker        │  │  Worker        │    │
│  └────────────────┘  └────────────────┘  └────────────────┘    │
│                                                                   │
└─────────────────────────────────────────────────────────────────┘
```

### Component Responsibilities

#### Web UI (Next.js 15 + React 19)

**Purpose**: User-facing dashboard for viewing leaderboards, trends, and managing API keys

**Features**:
- Public leaderboard with real-time updates
- Historical performance charts (Recharts + D3.js)
- Provider comparison tools
- User authentication (Auth0/Clerk)
- API key management
- Custom scenario builder (Pro tier)
- Alert configuration

**Tech Stack**:
```typescript
{
  framework: "Next.js 15 (App Router)",
  ui: "React 19 + Tailwind CSS 4",
  charts: "Recharts + D3.js",
  state: "Zustand",
  realtime: "React Query + SSE",
  auth: "Auth0 or Clerk"
}
```

#### REST API (Fastify 5.x)

**Purpose**: Primary API for programmatic access

**Endpoints**:
- `GET /api/v1/providers` - List all providers
- `GET /api/v1/models` - List all models
- `GET /api/v1/leaderboard` - Get current rankings
- `GET /api/v1/models/:id/history` - Historical data
- `POST /api/v1/benchmarks/run` - Trigger benchmark
- `GET /api/v1/benchmarks/:id/status` - Run status (SSE)
- `GET /api/v1/alerts` - List alerts
- `GET /api/v1/trends/cost` - Cost analysis

**Tech Stack**:
```typescript
{
  framework: "Fastify 5.x",
  validation: "Zod",
  auth: "JWT + API keys",
  docs: "OpenAPI 3.1 (Swagger)",
  rateLimit: "fastify-rate-limit"
}
```

#### GraphQL API (Apollo Server 4)

**Purpose**: Flexible querying for complex data relationships

**Schema Highlights**:
```graphql
type Query {
  providers: [Provider!]!
  models(filter: ModelFilter): [Model!]!
  leaderboard(scenario: String!, period: TimePeriod!): [LeaderboardEntry!]!
  compareModels(ids: [ID!]!, scenarios: [String!]!): ComparisonResult!
}

type Subscription {
  benchmarkProgress(runId: ID!): BenchmarkProgress!
  leaderboardUpdates(scenario: String!): LeaderboardEntry!
}
```

#### Benchmark Service

**Purpose**: Core benchmarking logic (migrated from CLI tool)

**Responsibilities**:
- Execute benchmark runs across providers
- Score responses using automated criteria
- Track token usage and costs
- Measure response latency
- Handle API errors and retries
- Emit events for real-time updates

**Migration Strategy**:
```typescript
// Current: CLI-based
class BatchRunner {
  async run(config: BatchConfig): Promise<BatchResults> {
    // Execute tests synchronously
  }
}

// Future: Service-based
class BenchmarkService {
  async enqueueBenchmark(config: BenchmarkConfig): Promise<string> {
    // Add job to BullMQ queue, return runId
  }

  async getBenchmarkStatus(runId: string): Promise<BenchmarkStatus> {
    // Query job status from BullMQ
  }

  async streamBenchmarkProgress(runId: string): AsyncIterable<ProgressUpdate> {
    // Subscribe to Redis pub/sub for real-time updates
  }
}
```

#### Analytics Service

**Purpose**: Compute aggregated metrics and trends

**Responsibilities**:
- Calculate daily/weekly/monthly aggregates
- Detect performance degradation
- Identify cost spikes
- Compute confidence intervals
- Generate recommendations

**Queries**:
```sql
-- Average score over time (TimescaleDB)
SELECT
  time_bucket('1 day', time) AS day,
  model_id,
  scenario_id,
  AVG(total_score) AS avg_score,
  STDDEV(total_score) AS std_dev,
  COUNT(*) AS test_count
FROM benchmark_runs
WHERE time > NOW() - INTERVAL '30 days'
GROUP BY day, model_id, scenario_id;

-- Detect degradation (>2 std dev drop)
WITH recent AS (
  SELECT model_id, scenario_id, AVG(total_score) AS recent_avg
  FROM benchmark_runs
  WHERE time > NOW() - INTERVAL '7 days'
  GROUP BY model_id, scenario_id
),
baseline AS (
  SELECT model_id, scenario_id, AVG(total_score) AS baseline_avg, STDDEV(total_score) AS baseline_stddev
  FROM benchmark_runs
  WHERE time BETWEEN NOW() - INTERVAL '30 days' AND NOW() - INTERVAL '7 days'
  GROUP BY model_id, scenario_id
)
SELECT
  r.model_id,
  r.scenario_id,
  b.baseline_avg,
  r.recent_avg,
  (b.baseline_avg - r.recent_avg) AS degradation
FROM recent r
JOIN baseline b ON r.model_id = b.model_id AND r.scenario_id = b.scenario_id
WHERE r.recent_avg < (b.baseline_avg - 2 * b.baseline_stddev);
```

#### Alerting Service

**Purpose**: Monitor for anomalies and notify users

**Alert Types**:
- **Performance Degradation**: Score drops >2 std dev
- **Cost Spike**: Cost increases >50% week-over-week
- **Failure Rate**: Error rate >10% in 24h
- **Latency Spike**: P95 latency increases >100%
- **New Model Detected**: Provider releases new model

**Notification Channels**:
- Webhooks (POST to user-provided URL)
- Email (SendGrid/Postmark)
- Slack/Discord (via webhooks)
- In-app notifications

---

## Data Model

### Database Schema (PostgreSQL 17 + TimescaleDB)

#### Core Tables

```sql
-- Providers (e.g., Anthropic, OpenAI, Google)
CREATE TABLE providers (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name VARCHAR(100) NOT NULL UNIQUE, -- 'anthropic', 'openai', 'google'
  display_name VARCHAR(200) NOT NULL, -- 'Anthropic Claude', 'OpenAI GPT'
  website_url VARCHAR(500),
  documentation_url VARCHAR(500),
  is_active BOOLEAN DEFAULT true,
  created_at TIMESTAMPTZ DEFAULT NOW(),
  updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX idx_providers_name ON providers(name);

-- Models (e.g., claude-4.5-sonnet, gpt-4o)
CREATE TABLE models (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  provider_id UUID REFERENCES providers(id) ON DELETE CASCADE,
  name VARCHAR(200) NOT NULL, -- 'claude-4.5-sonnet', 'gpt-4o'
  display_name VARCHAR(300),
  is_free BOOLEAN DEFAULT false,
  is_paid BOOLEAN DEFAULT true,
  context_window_tokens INTEGER,
  input_cost_per_mtok DECIMAL(10, 6),
  output_cost_per_mtok DECIMAL(10, 6),

  -- Discovery metadata
  detected_at TIMESTAMPTZ DEFAULT NOW(),
  last_seen_at TIMESTAMPTZ DEFAULT NOW(),
  is_deprecated BOOLEAN DEFAULT false,
  deprecation_date TIMESTAMPTZ,

  created_at TIMESTAMPTZ DEFAULT NOW(),
  updated_at TIMESTAMPTZ DEFAULT NOW(),

  UNIQUE(provider_id, name)
);

CREATE INDEX idx_models_provider ON models(provider_id);
CREATE INDEX idx_models_free ON models(is_free) WHERE is_free = true;
CREATE INDEX idx_models_active ON models(is_deprecated) WHERE is_deprecated = false;

-- Scenarios (e.g., code-generation, test-generation)
CREATE TABLE scenarios (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name VARCHAR(100) NOT NULL UNIQUE, -- 'code-generation', 'issue-analysis'
  display_name VARCHAR(200),
  description TEXT,
  prompt TEXT NOT NULL,
  scoring_criteria JSONB, -- Detailed criteria for automated scoring
  is_active BOOLEAN DEFAULT true,
  created_at TIMESTAMPTZ DEFAULT NOW(),
  updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX idx_scenarios_name ON scenarios(name);

-- Benchmark runs (TimescaleDB hypertable for time-series data)
CREATE TABLE benchmark_runs (
  time TIMESTAMPTZ NOT NULL,
  run_id UUID NOT NULL,
  provider_id UUID REFERENCES providers(id),
  model_id UUID REFERENCES models(id),
  scenario_id UUID REFERENCES scenarios(id),
  iteration INTEGER,

  -- Scores (0-10 scale)
  total_score DECIMAL(4, 2) CHECK (total_score >= 0 AND total_score <= 10),
  score_breakdown JSONB, -- Per-criterion scores
  confidence VARCHAR(20), -- 'high', 'medium', 'low'

  -- Token metrics
  input_tokens INTEGER,
  output_tokens INTEGER,
  total_tokens INTEGER,

  -- Performance metrics
  response_time_ms INTEGER,
  estimated_cost DECIMAL(10, 8),

  -- Request metadata
  api_version VARCHAR(50),
  temperature DECIMAL(3, 2),
  max_tokens INTEGER,

  -- Response data
  response_text TEXT,
  response_metadata JSONB,

  -- Status
  status VARCHAR(20) CHECK (status IN ('success', 'failed', 'timeout', 'error')),
  error_message TEXT,
  error_code VARCHAR(50),

  PRIMARY KEY (time, run_id)
);

-- Convert to hypertable (TimescaleDB)
SELECT create_hypertable('benchmark_runs', 'time');

-- Indexes
CREATE INDEX idx_benchmark_runs_model ON benchmark_runs(model_id, time DESC);
CREATE INDEX idx_benchmark_runs_scenario ON benchmark_runs(scenario_id, time DESC);
CREATE INDEX idx_benchmark_runs_status ON benchmark_runs(status, time DESC);

-- Retention policy (keep 1 year of raw data)
SELECT add_retention_policy('benchmark_runs', INTERVAL '1 year');
```

#### Enhanced Tables for Competitive Intelligence

```sql
-- Human developer baselines (VirologyTest approach)
CREATE TABLE developer_baselines (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  anonymous_id VARCHAR(50) UNIQUE NOT NULL,  -- Anonymized identifier
  seniority VARCHAR(20) CHECK (seniority IN ('junior', 'mid', 'senior', 'staff')),
  tech_stack VARCHAR(100)[],  -- Array: ['typescript', 'react', 'node']
  years_experience INTEGER,

  -- Aggregated performance
  total_tasks_completed INTEGER DEFAULT 0,
  avg_score DECIMAL(4, 2),
  avg_time_per_task INTEGER,  -- Seconds

  -- Distribution
  score_p25 DECIMAL(4, 2),
  score_p50 DECIMAL(4, 2),
  score_p75 DECIMAL(4, 2),
  score_p95 DECIMAL(4, 2),

  created_at TIMESTAMPTZ DEFAULT NOW(),
  updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX idx_dev_baselines_seniority ON developer_baselines(seniority);

-- Individual developer task results
CREATE TABLE developer_task_results (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  developer_id UUID REFERENCES developer_baselines(id) ON DELETE CASCADE,
  scenario_id UUID REFERENCES scenarios(id),

  score DECIMAL(4, 2) CHECK (score >= 0 AND score <= 10),
  time_spent INTEGER,  -- Seconds
  attempts INTEGER DEFAULT 1,
  completed_at TIMESTAMPTZ DEFAULT NOW(),

  PRIMARY KEY (developer_id, scenario_id, completed_at)
);

CREATE INDEX idx_dev_results_scenario ON developer_task_results(scenario_id);

-- Contamination tracking
CREATE TABLE contamination_metadata (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  model_id UUID REFERENCES models(id),
  scenario_id UUID REFERENCES scenarios(id),

  is_contaminated BOOLEAN DEFAULT false,
  contamination_window_days INTEGER,  -- Days between scenario release and model training
  confidence VARCHAR(20) CHECK (confidence IN ('certain', 'likely', 'possible', 'unlikely')),

  notes TEXT,
  detected_at TIMESTAMPTZ DEFAULT NOW(),

  UNIQUE(model_id, scenario_id)
);

CREATE INDEX idx_contamination_flagged ON contamination_metadata(is_contaminated) WHERE is_contaminated = true;

-- Adversarial test tracking (SimpleBench approach)
CREATE TABLE adversarial_tests (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  scenario_id UUID REFERENCES scenarios(id),

  test_type VARCHAR(50) CHECK (test_type IN ('trick_question', 'ambiguous_spec', 'impossible_requirement', 'contradictory_spec')),
  is_public BOOLEAN DEFAULT false,  -- 20% public, 80% private
  expected_behavior VARCHAR(50) CHECK (expected_behavior IN ('refuse', 'clarify', 'partial_solve', 'detect_issue')),

  difficulty_elo INTEGER DEFAULT 1500,
  pass_rate DECIMAL(5, 2),  -- Track how many models pass

  created_at TIMESTAMPTZ DEFAULT NOW(),
  rotated_at TIMESTAMPTZ  -- When private test was refreshed
);

CREATE INDEX idx_adversarial_public ON adversarial_tests(is_public);

-- Confidence intervals (MASK approach)
CREATE TABLE model_confidence_intervals (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  model_id UUID REFERENCES models(id),
  scenario_id UUID REFERENCES scenarios(id),
  time_period VARCHAR(20),  -- '7d', '30d', '90d', '1y'

  avg_score DECIMAL(4, 2),
  ci_lower DECIMAL(4, 2),  -- 95% CI lower bound
  ci_upper DECIMAL(4, 2),  -- 95% CI upper bound
  std_dev DECIMAL(4, 2),
  sample_size INTEGER,

  calculated_at TIMESTAMPTZ DEFAULT NOW(),

  UNIQUE(model_id, scenario_id, time_period)
);

CREATE INDEX idx_model_ci_period ON model_confidence_intervals(time_period);
```

#### Aggregated Views (Materialized for Performance)

```sql
-- Daily aggregates (refreshed hourly)
CREATE MATERIALIZED VIEW model_performance_daily AS
SELECT
  time_bucket('1 day', time) AS day,
  provider_id,
  model_id,
  scenario_id,

  -- Score statistics
  AVG(total_score) AS avg_score,
  STDDEV(total_score) AS stddev_score,
  MIN(total_score) AS min_score,
  MAX(total_score) AS max_score,
  PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY total_score) AS median_score,

  -- Performance statistics
  AVG(response_time_ms) AS avg_response_time,
  PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY response_time_ms) AS p95_response_time,

  -- Cost statistics
  SUM(estimated_cost) AS total_cost,
  AVG(estimated_cost) AS avg_cost,

  -- Volume
  COUNT(*) AS test_count,
  SUM(CASE WHEN status = 'success' THEN 1 ELSE 0 END) AS success_count,
  SUM(CASE WHEN status = 'failed' THEN 1 ELSE 0 END) AS failure_count,

  -- Confidence
  CASE
    WHEN STDDEV(total_score) < 1.0 THEN 'high'
    WHEN STDDEV(total_score) < 2.0 THEN 'medium'
    ELSE 'low'
  END AS confidence
FROM benchmark_runs
WHERE status = 'success'
GROUP BY day, provider_id, model_id, scenario_id;

CREATE UNIQUE INDEX ON model_performance_daily (day DESC, model_id, scenario_id);

-- Refresh policy (every hour)
CREATE OR REPLACE FUNCTION refresh_model_performance_daily()
RETURNS void AS $$
BEGIN
  REFRESH MATERIALIZED VIEW CONCURRENTLY model_performance_daily;
END;
$$ LANGUAGE plpgsql;

-- TimescaleDB continuous aggregate (alternative, more efficient)
CREATE MATERIALIZED VIEW model_performance_hourly
WITH (timescaledb.continuous) AS
SELECT
  time_bucket('1 hour', time) AS hour,
  model_id,
  scenario_id,
  AVG(total_score) AS avg_score,
  STDDEV(total_score) AS stddev_score,
  AVG(response_time_ms) AS avg_response_time,
  SUM(estimated_cost) AS total_cost,
  COUNT(*) AS test_count
FROM benchmark_runs
WHERE status = 'success'
GROUP BY hour, model_id, scenario_id;

-- Automatically refresh every 10 minutes
SELECT add_continuous_aggregate_policy('model_performance_hourly',
  start_offset => INTERVAL '1 day',
  end_offset => INTERVAL '10 minutes',
  schedule_interval => INTERVAL '10 minutes');
```

#### User & Auth Tables

```sql
-- Users (for public service)
CREATE TABLE users (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  email VARCHAR(255) UNIQUE NOT NULL,
  username VARCHAR(100) UNIQUE,
  full_name VARCHAR(200),

  -- Auth
  auth_provider VARCHAR(50), -- 'auth0', 'clerk', 'email'
  auth_provider_id VARCHAR(255),

  -- API access
  api_key VARCHAR(100) UNIQUE,
  api_key_hash VARCHAR(255), -- bcrypt hash for security

  -- Tier
  tier VARCHAR(20) DEFAULT 'free' CHECK (tier IN ('free', 'pro', 'enterprise')),
  rate_limit_per_hour INTEGER DEFAULT 100,

  -- Subscription
  stripe_customer_id VARCHAR(100),
  subscription_status VARCHAR(20), -- 'active', 'cancelled', 'past_due'
  subscription_end_date TIMESTAMPTZ,

  -- Metadata
  created_at TIMESTAMPTZ DEFAULT NOW(),
  last_active_at TIMESTAMPTZ,
  is_active BOOLEAN DEFAULT true
);

CREATE INDEX idx_users_email ON users(email);
CREATE INDEX idx_users_api_key ON users(api_key_hash);
CREATE INDEX idx_users_tier ON users(tier);

-- API request tracking (TimescaleDB hypertable)
CREATE TABLE api_requests (
  time TIMESTAMPTZ NOT NULL,
  user_id UUID REFERENCES users(id),
  request_id UUID NOT NULL DEFAULT gen_random_uuid(),

  endpoint VARCHAR(200),
  method VARCHAR(10),
  status_code INTEGER,
  response_time_ms INTEGER,

  ip_address INET,
  user_agent TEXT,

  PRIMARY KEY (time, request_id)
);

SELECT create_hypertable('api_requests', 'time');
CREATE INDEX idx_api_requests_user ON api_requests(user_id, time DESC);

-- Retention policy (keep 90 days)
SELECT add_retention_policy('api_requests', INTERVAL '90 days');
```

#### Alerts & Monitoring Tables

```sql
-- Performance alerts
CREATE TABLE performance_alerts (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  model_id UUID REFERENCES models(id),
  scenario_id UUID REFERENCES scenarios(id),

  alert_type VARCHAR(50) CHECK (alert_type IN (
    'degradation', 'cost_spike', 'failure_rate',
    'latency_spike', 'new_model'
  )),
  severity VARCHAR(20) CHECK (severity IN ('low', 'medium', 'high', 'critical')),

  threshold_value DECIMAL(10, 2),
  actual_value DECIMAL(10, 2),
  message TEXT,

  triggered_at TIMESTAMPTZ DEFAULT NOW(),
  resolved_at TIMESTAMPTZ,
  acknowledged BOOLEAN DEFAULT false,
  acknowledged_by UUID REFERENCES users(id),
  acknowledged_at TIMESTAMPTZ
);

CREATE INDEX idx_alerts_model ON performance_alerts(model_id, triggered_at DESC);
CREATE INDEX idx_alerts_unresolved ON performance_alerts(resolved_at) WHERE resolved_at IS NULL;

-- User alert subscriptions
CREATE TABLE alert_subscriptions (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id UUID REFERENCES users(id),
  provider_id UUID REFERENCES providers(id),
  model_id UUID REFERENCES models(id),
  scenario_id UUID REFERENCES scenarios(id),

  alert_types VARCHAR(50)[], -- Array of alert types

  notification_channels JSONB, -- {email: true, webhook: "https://...", slack: "..."}

  created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX idx_alert_subs_user ON alert_subscriptions(user_id);
```

---

## Benchmark Suite Design

### Finalized 7-Category Suite

Based on competitive analysis of 17+ benchmarks, we've designed a comprehensive suite that combines practical developer workflows with contamination-free evaluation:

| Category | Weight | Description | Inspiration | Tasks (MVP→Full) |
|----------|--------|-------------|------------|------------------|
| **Code Generation** | 30% | Generate code from natural language | Aider, SWE-bench | 25 → 80 |
| **Code Review** | 25% | Identify bugs, suggest improvements | SimpleBench, Cybench | 25 → 60 |
| **Refactoring** | 15% | Improve code structure without breaking tests | Aider refactoring | 10 → 30 |
| **Debugging** | 10% | Fix failing tests with error output | Aider Pass@2, LiveCodeBench | 10 → 25 |
| **Security** | 10% | Detect vulnerabilities (OWASP Top 10) | Cybench | 5 → 25 |
| **Architecture** | 5% | Design patterns, system design | HLE, ARC-AGI | 5 → 15 |
| **Documentation** | 5% | Generate docs, explain code | Aider, VideoMMMU | 5 → 10 |
| **TOTAL** | 100% | - | - | **85 → 245** |

### Scoring Methodology (Enhanced)

#### Overall Score (0-10 scale with Percentiles)

**Formula**:
```
Overall Score =
  (0.30 × Code Generation) +
  (0.25 × Code Review) +
  (0.15 × Refactoring) +
  (0.10 × Debugging) +
  (0.10 × Security) +
  (0.05 × Architecture) +
  (0.05 × Documentation)
```

**Statistical Rigor** (from MASK):
- Bootstrap 95% confidence intervals
- Statistical ranking: models with overlapping CIs share rank
- Prevents misleading precision (e.g., "8.5 vs 8.4" when CIs overlap)

#### Per-Category Scoring (0-10 scale)

**Formula** (refined from Aider/LiveBench):
```
Category Score =
  (0.50 × Accuracy) +        # Tests pass, correct output
  (0.20 × Confidence) +       # Model certainty calibration (from MASK)
  (0.15 × Efficiency) +       # Token usage, latency (from Aider)
  (0.10 × Robustness) +       # Adversarial cases (from SimpleBench)
  (0.05 × Style)              # Code quality, best practices
```

#### Human Percentile Rankings (from VirologyTest)

**Methodology**:
1. Recruit 350 developers stratified by seniority:
   - 100 junior (0-2 years)
   - 100 mid-level (2-5 years)
   - 100 senior (5-10 years)
   - 50 staff+ (10+ years)

2. **Individualized testing** (developers answer tasks in their tech stack):
   - Backend devs: API routes, database queries
   - Frontend devs: UI components, state management
   - Full-stack devs: Mixed scenarios

3. Compare model scores to developer scores on same tasks

4. Report: **"Claude 4.5 performs better than 85% of mid-level developers [80-89% CI]"**

**Percentile Formula**:
```python
# Bootstrap 95% CI for percentile
def bootstrap_percentile_ci(model_scores, dev_scores, n_bootstrap=1000):
    percentiles = []
    for _ in range(n_bootstrap):
        indices = np.random.choice(len(dev_scores), len(dev_scores), replace=True)
        outperformed = np.sum(model_scores > dev_scores[indices])
        percentiles.append((outperformed / len(dev_scores)) * 100)

    return (
        np.mean(percentiles),
        np.percentile(percentiles, 2.5),  # Lower CI bound
        np.percentile(percentiles, 97.5)  # Upper CI bound
    )
```

### Contamination Prevention Strategy

**Three-Layer Defense** (from LiveBench + MASK + SimpleBench):

#### Layer 1: Post-Training Data Sources (LiveBench approach)

**Monthly Updates** (1st of each month):
```
Week 1: Collect new problems from post-training sources
  - GitHub issues created after model cutoff (>30 days)
  - LeetCode/Codeforces contests from past 30 days
  - CVEs published after cutoff dates
  - Open-source project bugs (recent)

Week 2: Curate and validate
  - Remove ambiguous problems
  - Add test cases
  - Validate with senior devs

Week 3: Run evaluations on existing models
  - Automated benchmark runs
  - Statistical analysis

Week 4: Update leaderboard, publish report
  - Release new version (e.g., aibaas_v2025_12)
  - Blog post with findings
```

#### Layer 2: Release Date Tracking (MASK approach)

**Database Schema**:
```sql
ALTER TABLE models ADD COLUMN training_cutoff_date DATE;
ALTER TABLE scenarios ADD COLUMN release_date DATE;

-- Contamination detection query
SELECT
  m.name AS model,
  s.name AS scenario,
  CASE WHEN s.release_date < m.training_cutoff_date
    THEN 'CONTAMINATED'
    ELSE 'CLEAN'
  END AS status
FROM models m
CROSS JOIN scenarios s
WHERE s.release_date < m.training_cutoff_date;
```

**UI Highlighting**:
- Red row background for contaminated results
- Warning tooltip: "⚠️ This model may have seen this problem during training"

#### Layer 3: Private Test Set (SimpleBench approach)

**80% Private, 20% Public**:
```
Total: 245 tasks
  - Public: 49 tasks (shown on website, used for debugging)
  - Private: 196 tasks (kept secret, used for leaderboard)

Quarterly Rotation:
  - Refresh 25% of private tasks (49 tasks)
  - Prevents memorization
```

**Overfitting Penalty**:
```python
def calculate_overfitting_penalty(public_score, private_score):
    if public_score > private_score:
        penalty = (public_score - private_score) / public_score
        return max(0, private_score - (penalty * 10))  # Deduct from final score
    return private_score
```

### Update Frequency & Versioning

**Monthly Releases** (based on LiveBench precedent):

| Version | Release Date | Tasks Added | Notes |
|---------|-------------|-------------|-------|
| `aibaas_v2025_11` | Nov 1, 2025 | 85 (MVP) | Initial release |
| `aibaas_v2025_12` | Dec 1, 2025 | +30 (115 total) | Add Debugging category |
| `aibaas_v2026_01` | Jan 1, 2026 | +40 (155 total) | Add Security category |
| `aibaas_v2026_02` | Feb 1, 2026 | +45 (200 total) | Add Architecture, Docs |
| `aibaas_v2026_03` | Mar 1, 2026 | +45 (245 total) | Full suite complete |

**Problem Versioning API**:
```bash
# Query specific version
GET /api/v1/leaderboard?version=v2025_12

# Compare versions
GET /api/v1/leaderboard/compare?from=v2025_11&to=v2025_12
```

### Enhanced Evaluation Features

#### Iterative Refinement (from Aider)

**Pass@1 vs Pass@2**:
```typescript
interface BenchmarkResult {
  attemptNumber: 1 | 2;
  score: number;
  improvement?: number;  // Score delta from attempt 1
}

// Example
{
  scenario: "code-generation",
  pass1: 7.5,   // Initial attempt
  pass2: 8.5,   // After seeing error output
  improvement: 1.0,  // +13% improvement
  recoveryCapability: "high"  // Categorize recovery ability
}
```

#### Adversarial Robustness (from SimpleBench)

**Trick Questions** (200 total, 80% private):
```typescript
interface AdversarialTest {
  id: string;
  category: "trick_question" | "ambiguous_spec" | "intentional_bug";
  publicVisibility: boolean;  // Only 20% public
  expectedBehavior: "refuse" | "clarify" | "partial_solve";
}
```

**Example Adversarial Scenarios**:
- **Impossible Requirements**: "Sort this list in O(1) time"
  - **Good response**: "Impossible. Sorting requires O(n log n) minimum."
  - **Bad response**: [Generates nonsense algorithm]

- **Contradictory Specs**: "Make this function pure AND modify global state"
  - **Good response**: "These requirements contradict. Please clarify."
  - **Bad response**: [Generates contradictory code]

### Human Baseline Collection

**Recruitment Strategy** (from VirologyTest):

| Seniority | Count | Recruitment Channel | Tasks Per Dev |
|-----------|-------|---------------------|---------------|
| Junior (0-2yr) | 100 | Bootcamps, LinkedIn | 25 tasks |
| Mid (2-5yr) | 100 | Dev communities, Twitter | 35 tasks |
| Senior (5-10yr) | 100 | Referrals, conferences | 45 tasks |
| Staff+ (10+yr) | 50 | Direct outreach | 50 tasks |

**Compensation**:
- $50/hour for task completion
- $100 bonus for top 10% performers
- Total budget: ~$75k for 350 developers

**Data Collection**:
```typescript
interface DeveloperBaseline {
  developerId: string;
  seniority: "junior" | "mid" | "senior" | "staff";
  techStack: string[];  // ["typescript", "react", "node"]
  taskResults: {
    scenarioId: string;
    score: number;
    timeSpent: number;  // Seconds
    attempts: number;
  }[];
}
```

---

## UI/UX Enhancements

### Competitive Intelligence: Best Patterns from 17 Benchmarks

Based on analysis of Aider, LiveBench, LiveCodeBench Pro, MASK, and others, we've identified key UI/UX patterns to adopt:

#### 1. Time-Window Slider (from LiveBench)

**Purpose**: Filter problems by release date to exclude contaminated data

**Implementation**:
```jsx
<DateRangeSlider
  min="2025-01-01"
  max="2025-12-31"
  value={[startDate, endDate]}
  onChange={handleDateChange}
/>
<p>{filteredTaskCount} tasks selected in current time window</p>
```

**Visual Design**:
```
[====|====================|====]
 Jan 2025              Dec 2025
 "245 tasks selected"
```

#### 2. Cost Visualization Bars (from Aider)

**Purpose**: Make cost comparison "immediately obvious" (Aider's words)

**Implementation**:
```jsx
// Cost bars with $10 ticks
<CostBar
  model="Claude 4.5"
  cost={0.08}
  maxCost={0.20}
  ticks={[0, 0.05, 0.10, 0.15, 0.20]}
/>
```

**Visual Design**:
```
GPT-4o     [████████████████████] $0.15/task
Claude 4.5 [████████████] $0.08/task
Gemini 2.5 [██████] $0.03/task
```

**Color Coding**:
- 🟢 Green: <$0.01 (budget-friendly)
- 🟡 Yellow: $0.01-$0.05 (moderate)
- 🔴 Red: >$0.05 (expensive)

#### 3. Statistical Ranking with Confidence Intervals (from MASK)

**Purpose**: Prevent misleading precision, show statistical significance

**Implementation**:
```sql
-- Rank by statistical significance
WITH ranked AS (
  SELECT
    model_id,
    avg_score,
    ci_lower,
    ci_upper,
    COUNT(DISTINCT m2.id) + 1 AS rank
  FROM model_performance m1
  LEFT JOIN model_performance m2
    ON m2.ci_lower > m1.ci_upper
  GROUP BY m1.id
)
SELECT * FROM ranked ORDER BY rank, avg_score DESC;
```

**Visual Design**:
```
Rank 1: claude-sonnet-4-5 (8.5 ± 0.3)
Rank 1: gpt-5 (8.4 ± 0.4)  ← Overlapping CI, shared rank
Rank 3: gemini-2-5 (8.1 ± 0.2)
```

#### 4. Elo Ratings and Percentiles (from LiveCodeBench Pro)

**Purpose**: Human-comparable difficulty ratings

**Implementation**:
```python
# Bayesian MAP Elo estimator
from trueskill import Rating, rate_1vs1

def update_task_elo(task_elo, model_result):
    """
    task_elo: Current Elo rating of task
    model_result: (model_elo, passed: bool)
    """
    task_rating = Rating(mu=task_elo)
    model_rating = Rating(mu=model_result[0])

    if model_result[1]:  # Model passed
        new_task, new_model = rate_1vs1(model_rating, task_rating)
    else:  # Model failed
        new_task, new_model = rate_1vs1(task_rating, model_rating)

    return new_task.mu
```

**Difficulty Tiers**:
- Easy: ≤1500 Elo (50th percentile)
- Medium: 1500-2000 Elo (50-90th percentile)
- Hard: 2000-2500 Elo (90-99th percentile)
- Expert: >2500 Elo (99th+ percentile)

**Visual Design**:
```
Claude 4.5: 2250 Elo (99.2nd percentile)
GPT-5: 2180 Elo (98.8th percentile)
```

#### 5. Contamination Highlighting (from LiveBench + MASK)

**Visual Indicators**:
- 🔴 Red row background
- ⚠️ Warning icon with tooltip
- Expandable details

**Tooltip Text**:
```
⚠️ Potential Contamination Warning

This model was evaluated after the public release of this benchmark version,
allowing potential access to problems and solutions.

Model Release: Oct 15, 2025
Problem Release: Sep 1, 2025
Contamination Window: 44 days
```

### New Interactive Features

#### Cost-Performance Scatter Plot

**Purpose**: Visualize quality vs. cost tradeoff

**Implementation** (D3.js + Recharts):
```jsx
<ScatterChart width={800} height={400}>
  <XAxis dataKey="cost" label="Cost per Task ($)" />
  <YAxis dataKey="score" label="Quality Score (0-10)" />
  <Scatter data={models} fill="#8884d8">
    {models.map((model, index) => (
      <Cell key={`cell-${index}`} fill={getProviderColor(model.provider)} />
    ))}
  </Scatter>
  <Tooltip content={<CustomTooltip />} />
</ScatterChart>
```

**Visual Design**:
```
Quality (0-10)
   10 |
      |
    8 |        ● Claude 4.5 ($0.08)
      |    ● GPT-5 ($0.12)
    6 |                  ● Gemini 2.5 ($0.03)
      |              ● DeepSeek R1 ($0.01)
    4 |
      |_________________________________________
        $0.00      $0.05      $0.10      $0.15
                 Cost per Task
```

#### Head-to-Head Comparison

**Purpose**: Detailed side-by-side model comparison

**UI Design**:
```
┌────────────────────────────────────────────────────────────┐
│  Compare: [Claude 4.5 ▼]  vs  [GPT-5 ▼]                   │
├────────────────────────────────────────────────────────────┤
│  Category          │ Claude 4.5 │ GPT-5   │ Winner        │
│  ─────────────────┼────────────┼─────────┼──────────────  │
│  Code Generation  │ 9.2        │ 8.9     │ Claude (+0.3) │
│  Code Review      │ 8.5        │ 9.1     │ GPT-5 (+0.6)  │
│  Refactoring      │ 8.1        │ 7.5     │ Claude (+0.6) │
│  Security         │ 8.9        │ 8.3     │ Claude (+0.6) │
│  ─────────────────┼────────────┼─────────┼──────────────  │
│  **Overall**      │ **8.5**    │ **8.4** │ **Tie (CI overlap)** │
│  **Cost**         │ $0.08      │ $0.12   │ Claude (33% cheaper) │
│  **Latency**      │ 3.2s       │ 2.8s    │ GPT-5 (14% faster) │
└────────────────────────────────────────────────────────────┘

**Recommendation**: Use Claude 4.5 for security-critical tasks,
GPT-5 for real-time code review.
```

#### Historical Trends (Time-Travel)

**Purpose**: Show model evolution over time

**Implementation**:
```jsx
<LineChart data={historicalData}>
  <XAxis dataKey="date" />
  <YAxis domain={[0, 10]} label="Score (0-10)" />
  <Line dataKey="claude_sonnet_4_5" stroke="#ff7f50" />
  <Line dataKey="gpt_5" stroke="#4caf50" />
  <Line dataKey="gemini_2_5" stroke="#2196f3" />
  <Tooltip />
  <Legend />
</LineChart>
```

**Visual Design**:
```
Score (0-10)
   10 |                                        ● Sonnet 4.5
      |                         ● Sonnet 3.7  (Oct 2025)
    8 |           ● Sonnet 3.5  (Feb 2025)
      |  ● Sonnet 3.0 (Jun 2024)
    6 |
      |_______________________________________________________
      Jun 2024    Dec 2024    Jun 2025    Dec 2025

**Improvement Rate**: +0.6 points per 4 months
**Projected**: 9.1 (Feb 2026), 9.7 (Jun 2026)

[Alert: GPT-4.1 quality dropped 5% in last 30 days]
```

### Responsive Design Patterns

**Mobile-First Approach**:

| Screen Size | Columns Shown | Features |
|-------------|---------------|----------|
| Mobile (<640px) | Rank, Model, Overall | Tap row to expand |
| Tablet (640-1024px) | + Easy/Medium/Hard scores | Swipe for more |
| Desktop (>1024px) | Full table + charts | Hover tooltips |

**Progressive Disclosure**:
- **Level 1** (Collapsed): Rank, Model, Overall Score
- **Level 2** (Expanded): Category breakdown, radar chart
- **Level 3** (Drill-down): Task-level results, error analysis

---

## API Design

### Enhanced API Endpoints

#### Human Baseline Endpoints

```typescript
// GET /api/v1/baselines/human
// Returns human developer performance data
interface HumanBaselineResponse {
  seniority: "junior" | "mid" | "senior" | "staff";
  taskCount: number;
  avgScore: number;
  scoreDistribution: {
    p25: number;
    p50: number;
    p75: number;
    p95: number;
  };
  topPerformers: {
    anonymousId: string;
    score: number;
    techStack: string[];
  }[];
}
```

#### Contamination Check Endpoint

```typescript
// GET /api/v1/contamination/check?model={id}&scenario={id}
interface ContaminationCheckResponse {
  contaminated: boolean;
  modelReleaseDate: string;
  scenarioReleaseDate: string;
  windowDays: number;
  warning: string;
}
```

#### Percentile Calculation Endpoint

```typescript
// GET /api/v1/percentiles?model={id}&seniority={tier}
interface PercentileResponse {
  model: string;
  seniority: "junior" | "mid" | "senior" | "staff";
  percentile: number;
  ciLower: number;
  ciUpper: number;
  interpretation: string;  // "Better than 85% of mid-level developers"
}
```

### Updated GraphQL Schema

```graphql
type Model {
  id: ID!
  name: String!
  provider: Provider!

  # Enhanced fields
  trainingCutoffDate: Date!
  releaseDate: Date!
  eloRating: Int!
  percentileRank: Float!

  # Human comparison
  humanPercentile(seniority: Seniority!): PercentileComparison!

  # Contamination check
  contaminationStatus(scenario: ID!): ContaminationStatus!
}

type PercentileComparison {
  percentile: Float!
  ciLower: Float!
  ciUpper: Float!
  interpretation: String!
  seniority: Seniority!
}

type ContaminationStatus {
  contaminated: Boolean!
  warning: String
  modelReleaseDate: Date!
  scenarioReleaseDate: Date!
}

enum Seniority {
  JUNIOR
  MID
  SENIOR
  STAFF
}
```

---

## API Design

### REST API (Fastify 5.x)

#### Authentication

```typescript
// API Key authentication
interface AuthenticatedRequest extends FastifyRequest {
  user: {
    id: string;
    tier: 'free' | 'pro' | 'enterprise';
    rateLimitPerHour: number;
  };
}

async function authenticateApiKey(request: FastifyRequest, reply: FastifyReply) {
  const apiKey = request.headers['x-api-key'];

  if (!apiKey) {
    throw new UnauthorizedError('API key required');
  }

  // Query user from database
  const user = await db.query(
    'SELECT id, tier, rate_limit_per_hour FROM users WHERE api_key_hash = $1 AND is_active = true',
    [await bcrypt.hash(apiKey, 10)]
  );

  if (!user) {
    throw new UnauthorizedError('Invalid API key');
  }

  // Check rate limit
  const requestCount = await redis.incr(`ratelimit:${user.id}:${getCurrentHour()}`);
  if (requestCount > user.rateLimitPerHour) {
    throw new RateLimitError('Rate limit exceeded');
  }

  request.user = user;
}
```

#### Core Endpoints

```typescript
// GET /api/v1/providers
// List all AI providers with current stats
app.get('/api/v1/providers', {
  schema: {
    response: {
      200: ProvidersResponseSchema
    }
  }
}, async (request, reply) => {
  const providers = await db.query(`
    SELECT
      p.id, p.name, p.display_name, p.website_url,
      COUNT(DISTINCT m.id) AS model_count,
      AVG(d.avg_score) AS avg_score
    FROM providers p
    LEFT JOIN models m ON p.id = m.provider_id
    LEFT JOIN model_performance_daily d ON m.id = d.model_id
    WHERE p.is_active = true
    GROUP BY p.id
    ORDER BY avg_score DESC NULLS LAST
  `);

  return { providers };
});

// GET /api/v1/leaderboard
// Get current leaderboard for scenario
app.get('/api/v1/leaderboard', {
  schema: {
    querystring: {
      type: 'object',
      properties: {
        scenario: { type: 'string' },
        period: { type: 'string', enum: ['1d', '7d', '30d', '90d'] }
      },
      required: ['scenario']
    },
    response: {
      200: LeaderboardResponseSchema
    }
  }
}, async (request, reply) => {
  const { scenario, period = '7d' } = request.query;

  const days = parseInt(period);

  const rankings = await db.query(`
    SELECT
      p.name AS provider,
      m.name AS model,
      m.display_name,
      AVG(d.avg_score) AS score,
      STDDEV(d.avg_score) AS stddev,
      AVG(d.avg_cost) AS cost,
      AVG(d.avg_response_time) AS response_time,
      SUM(d.test_count) AS test_count,
      CASE
        WHEN STDDEV(d.avg_score) < 1.0 THEN 'high'
        WHEN STDDEV(d.avg_score) < 2.0 THEN 'medium'
        ELSE 'low'
      END AS confidence
    FROM model_performance_daily d
    JOIN models m ON d.model_id = m.id
    JOIN providers p ON m.provider_id = p.id
    JOIN scenarios s ON d.scenario_id = s.id
    WHERE s.name = $1
      AND d.day > NOW() - INTERVAL '${days} days'
    GROUP BY p.name, m.id
    ORDER BY score DESC
    LIMIT 50
  `, [scenario]);

  return {
    scenario,
    period,
    rankings: rankings.map((r, i) => ({ rank: i + 1, ...r }))
  };
});

// GET /api/v1/models/:modelId/history
// Get historical performance data
app.get('/api/v1/models/:modelId/history', {
  schema: {
    params: {
      type: 'object',
      properties: {
        modelId: { type: 'string', format: 'uuid' }
      }
    },
    querystring: {
      type: 'object',
      properties: {
        period: { type: 'string', enum: ['7d', '30d', '90d', '1y'] }
      }
    }
  }
}, async (request, reply) => {
  const { modelId } = request.params;
  const { period = '30d' } = request.query;

  const timeSeries = await db.query(`
    SELECT
      day,
      scenario_id,
      avg_score,
      avg_cost,
      avg_response_time,
      test_count
    FROM model_performance_daily
    WHERE model_id = $1
      AND day > NOW() - INTERVAL '${period}'
    ORDER BY day ASC
  `, [modelId]);

  return { modelId, timeSeries };
});

// POST /api/v1/benchmarks/run
// Trigger new benchmark run (authenticated, Pro tier+)
app.post('/api/v1/benchmarks/run', {
  preHandler: [authenticateApiKey],
  schema: {
    body: BenchmarkRunRequestSchema,
    response: {
      202: BenchmarkRunResponseSchema
    }
  }
}, async (request: AuthenticatedRequest, reply) => {
  if (request.user.tier === 'free') {
    throw new ForbiddenError('Pro tier required for custom benchmarks');
  }

  const { providers, models, scenarios, iterations, webhookUrl } = request.body;

  // Create benchmark job
  const runId = uuidv7();
  await benchmarkQueue.add('run-benchmark', {
    runId,
    userId: request.user.id,
    providers,
    models,
    scenarios,
    iterations,
    webhookUrl
  });

  return reply.code(202).send({
    runId,
    status: 'queued',
    statusUrl: `/api/v1/benchmarks/${runId}/status`
  });
});

// GET /api/v1/benchmarks/:runId/status
// Server-Sent Events for real-time progress
app.get('/api/v1/benchmarks/:runId/status', async (request, reply) => {
  const { runId } = request.params;

  // Set SSE headers
  reply.raw.setHeader('Content-Type', 'text/event-stream');
  reply.raw.setHeader('Cache-Control', 'no-cache');
  reply.raw.setHeader('Connection', 'keep-alive');

  // Subscribe to Redis pub/sub
  const subscriber = redis.duplicate();
  await subscriber.subscribe(`benchmark:progress:${runId}`);

  subscriber.on('message', (channel, message) => {
    const data = JSON.parse(message);
    reply.raw.write(`data: ${JSON.stringify(data)}\n\n`);

    // Close stream when complete
    if (data.status === 'completed' || data.status === 'failed') {
      subscriber.unsubscribe();
      subscriber.quit();
      reply.raw.end();
    }
  });

  request.raw.on('close', () => {
    subscriber.unsubscribe();
    subscriber.quit();
  });
});

// GET /api/v1/alerts
// Get active alerts
app.get('/api/v1/alerts', {
  preHandler: [authenticateApiKey],
  schema: {
    querystring: {
      type: 'object',
      properties: {
        status: { type: 'string', enum: ['active', 'resolved', 'all'] }
      }
    }
  }
}, async (request: AuthenticatedRequest, reply) => {
  const { status = 'active' } = request.query;

  let whereClause = '';
  if (status === 'active') whereClause = 'WHERE resolved_at IS NULL';
  else if (status === 'resolved') whereClause = 'WHERE resolved_at IS NOT NULL';

  const alerts = await db.query(`
    SELECT
      a.id,
      p.name AS provider,
      m.name AS model,
      s.name AS scenario,
      a.alert_type,
      a.severity,
      a.message,
      a.triggered_at,
      a.resolved_at
    FROM performance_alerts a
    JOIN models m ON a.model_id = m.id
    JOIN providers p ON m.provider_id = p.id
    LEFT JOIN scenarios s ON a.scenario_id = s.id
    ${whereClause}
    ORDER BY triggered_at DESC
    LIMIT 100
  `);

  return { alerts };
});

// GET /api/v1/trends/cost
// Cost trend analysis
app.get('/api/v1/trends/cost', {
  schema: {
    querystring: {
      type: 'object',
      properties: {
        providers: { type: 'string' }, // Comma-separated
        period: { type: 'string', enum: ['30d', '90d', '1y'] }
      }
    }
  }
}, async (request, reply) => {
  const { providers, period = '30d' } = request.query;
  const providerList = providers?.split(',') || [];

  let providerFilter = '';
  if (providerList.length > 0) {
    providerFilter = `AND p.name = ANY($1)`;
  }

  const costData = await db.query(`
    SELECT
      p.name AS provider,
      d.day,
      SUM(d.total_cost) AS total_cost,
      AVG(d.avg_cost) AS avg_cost_per_task,
      SUM(d.test_count) AS test_count
    FROM model_performance_daily d
    JOIN models m ON d.model_id = m.id
    JOIN providers p ON m.provider_id = p.id
    WHERE d.day > NOW() - INTERVAL '${period}'
      ${providerFilter}
    GROUP BY p.name, d.day
    ORDER BY p.name, d.day ASC
  `, providerList.length > 0 ? [providerList] : []);

  // Group by provider
  const byProvider = {};
  for (const row of costData) {
    if (!byProvider[row.provider]) {
      byProvider[row.provider] = { name: row.provider, timeSeries: [] };
    }
    byProvider[row.provider].timeSeries.push({
      date: row.day,
      totalCost: row.total_cost,
      avgCostPerTask: row.avg_cost_per_task,
      testCount: row.test_count
    });
  }

  return { providers: Object.values(byProvider) };
});
```

### GraphQL API (Apollo Server 4)

```typescript
// Schema definition
const typeDefs = gql`
  scalar DateTime
  scalar JSONObject

  enum TimePeriod {
    DAY_1
    DAY_7
    DAY_30
    DAY_90
    YEAR_1
  }

  enum Confidence {
    HIGH
    MEDIUM
    LOW
  }

  enum AlertType {
    DEGRADATION
    COST_SPIKE
    FAILURE_RATE
    LATENCY_SPIKE
    NEW_MODEL
  }

  enum Severity {
    LOW
    MEDIUM
    HIGH
    CRITICAL
  }

  type Provider {
    id: ID!
    name: String!
    displayName: String!
    websiteUrl: String
    models: [Model!]!
    averagePerformance(period: TimePeriod!): PerformanceMetrics
  }

  type Model {
    id: ID!
    name: String!
    displayName: String
    provider: Provider!
    isFree: Boolean!
    isPaid: Boolean!
    contextWindow: Int
    pricing: PricingInfo!
    currentPerformance: PerformanceMetrics!
    historicalPerformance(period: TimePeriod!): [TimeSeriesPoint!]!
    compareTo(modelIds: [ID!]!, scenarios: [String!]): ComparisonResult!
  }

  type PricingInfo {
    inputCostPerMTok: Float
    outputCostPerMTok: Float
  }

  type PerformanceMetrics {
    avgScore: Float!
    stdDevScore: Float
    confidence: Confidence!
    avgCost: Float!
    avgResponseTime: Int!
    successRate: Float!
    testCount: Int!
    lastTested: DateTime
  }

  type TimeSeriesPoint {
    timestamp: DateTime!
    avgScore: Float!
    avgCost: Float!
    avgResponseTime: Int!
    testCount: Int!
  }

  type LeaderboardEntry {
    rank: Int!
    model: Model!
    score: Float!
    confidence: Confidence!
    cost: Float!
    responseTime: Int!
    trend: String
  }

  type ComparisonResult {
    models: [Model!]!
    scenarios: [Scenario!]!
    matrix: [[Float!]!]!
    winner: Model
  }

  type Scenario {
    id: ID!
    name: String!
    displayName: String
    description: String
  }

  type Alert {
    id: ID!
    model: Model
    scenario: Scenario
    type: AlertType!
    severity: Severity!
    message: String!
    triggeredAt: DateTime!
    resolvedAt: DateTime
    acknowledged: Boolean!
  }

  type Query {
    providers: [Provider!]!
    provider(id: ID!): Provider
    models(filter: ModelFilter): [Model!]!
    model(id: ID!): Model
    leaderboard(scenario: String!, period: TimePeriod!): [LeaderboardEntry!]!
    compareModels(modelIds: [ID!]!, scenarios: [String!]!): ComparisonResult!
    alerts(status: String): [Alert!]!
    costAnalysis(providers: [String!], period: TimePeriod!): CostAnalysis!
  }

  input ModelFilter {
    providers: [String!]
    isFree: Boolean
    isPaid: Boolean
  }

  type CostAnalysis {
    providers: [ProviderCostData!]!
    summary: CostSummary!
  }

  type ProviderCostData {
    provider: String!
    timeSeries: [CostTimeSeriesPoint!]!
    projections: CostProjections!
  }

  type CostTimeSeriesPoint {
    date: DateTime!
    totalCost: Float!
    avgCostPerTask: Float!
    testCount: Int!
  }

  type CostProjections {
    nextMonth: Float!
    nextQuarter: Float!
  }

  type CostSummary {
    totalCost: Float!
    avgCostPerTask: Float!
    cheapestProvider: String!
    mostExpensiveProvider: String!
  }

  type Subscription {
    benchmarkProgress(runId: ID!): BenchmarkProgress!
    leaderboardUpdates(scenario: String!): LeaderboardEntry!
    newAlerts: Alert!
  }

  type BenchmarkProgress {
    runId: ID!
    status: String!
    progress: ProgressInfo!
    results: JSONObject
  }

  type ProgressInfo {
    completed: Int!
    total: Int!
    current: CurrentTest
  }

  type CurrentTest {
    provider: String!
    model: String!
    scenario: String!
  }
`;

// Resolvers
const resolvers = {
  Query: {
    providers: async () => {
      return await db.query('SELECT * FROM providers WHERE is_active = true');
    },

    model: async (_, { id }) => {
      return await db.query('SELECT * FROM models WHERE id = $1', [id]);
    },

    leaderboard: async (_, { scenario, period }) => {
      const days = periodToDays(period);
      // SQL query similar to REST endpoint
      return await getLeaderboard(scenario, days);
    },

    compareModels: async (_, { modelIds, scenarios }) => {
      // Fetch performance data for all model+scenario combinations
      const matrix = [];
      for (const modelId of modelIds) {
        const row = [];
        for (const scenario of scenarios) {
          const perf = await getModelPerformance(modelId, scenario);
          row.push(perf.avgScore);
        }
        matrix.push(row);
      }

      // Determine winner (highest average score)
      const avgScores = matrix.map(row => avg(row));
      const winnerIndex = avgScores.indexOf(Math.max(...avgScores));

      return {
        models: await db.query('SELECT * FROM models WHERE id = ANY($1)', [modelIds]),
        scenarios: await db.query('SELECT * FROM scenarios WHERE name = ANY($1)', [scenarios]),
        matrix,
        winner: await db.query('SELECT * FROM models WHERE id = $1', [modelIds[winnerIndex]])
      };
    }
  },

  Model: {
    provider: async (model) => {
      return await db.query('SELECT * FROM providers WHERE id = $1', [model.provider_id]);
    },

    currentPerformance: async (model) => {
      return await db.query(`
        SELECT
          AVG(avg_score) AS avgScore,
          STDDEV(avg_score) AS stdDevScore,
          AVG(avg_cost) AS avgCost,
          AVG(avg_response_time) AS avgResponseTime,
          SUM(success_count) / SUM(test_count) AS successRate,
          SUM(test_count) AS testCount
        FROM model_performance_daily
        WHERE model_id = $1
          AND day > NOW() - INTERVAL '7 days'
      `, [model.id]);
    },

    historicalPerformance: async (model, { period }) => {
      const days = periodToDays(period);
      return await db.query(`
        SELECT
          day AS timestamp,
          avg_score AS avgScore,
          avg_cost AS avgCost,
          avg_response_time AS avgResponseTime,
          test_count AS testCount
        FROM model_performance_daily
        WHERE model_id = $1
          AND day > NOW() - INTERVAL '${days} days'
        ORDER BY day ASC
      `, [model.id]);
    }
  },

  Subscription: {
    benchmarkProgress: {
      subscribe: (_, { runId }) => {
        return pubsub.asyncIterator([`BENCHMARK_PROGRESS_${runId}`]);
      }
    },

    leaderboardUpdates: {
      subscribe: (_, { scenario }) => {
        return pubsub.asyncIterator([`LEADERBOARD_${scenario}`]);
      }
    },

    newAlerts: {
      subscribe: () => {
        return pubsub.asyncIterator(['NEW_ALERT']);
      }
    }
  }
};
```

---

## Real-Time Infrastructure

### Server-Sent Events (SSE) for Progress Updates

```typescript
// Benchmark worker publishes progress to Redis
async function runBenchmark(job: Job<BenchmarkJobData>) {
  const { runId, providers, models, scenarios, iterations } = job.data;

  const totalTests = providers.length * models.length * scenarios.length * iterations;
  let completed = 0;

  for (const provider of providers) {
    for (const model of models) {
      for (const scenario of scenarios) {
        for (let i = 0; i < iterations; i++) {
          // Publish progress
          await redis.publish(`benchmark:progress:${runId}`, JSON.stringify({
            status: 'running',
            progress: {
              completed,
              total: totalTests,
              current: { provider, model, scenario }
            }
          }));

          // Execute test
          const result = await executeTest(provider, model, scenario);

          // Save result to database
          await saveResult(result);

          completed++;
        }
      }
    }
  }

  // Publish completion
  await redis.publish(`benchmark:progress:${runId}`, JSON.stringify({
    status: 'completed',
    progress: { completed: totalTests, total: totalTests }
  }));
}
```

### WebSocket for Dashboard Real-Time Updates

```typescript
import { Server } from 'socket.io';

const io = new Server(httpServer, {
  cors: { origin: process.env.FRONTEND_URL }
});

io.on('connection', (socket) => {
  console.log('Client connected:', socket.id);

  // Subscribe to leaderboard updates
  socket.on('subscribe:leaderboard', ({ scenario }) => {
    socket.join(`leaderboard:${scenario}`);
  });

  // Subscribe to alerts
  socket.on('subscribe:alerts', ({ modelId }) => {
    socket.join(`alerts:${modelId}`);
  });

  socket.on('disconnect', () => {
    console.log('Client disconnected:', socket.id);
  });
});

// Emit updates when new benchmark results arrive
eventEmitter.on('benchmark:completed', async (result) => {
  // Recompute leaderboard
  const newRankings = await computeLeaderboard(result.scenario);

  // Emit to all subscribers
  io.to(`leaderboard:${result.scenario}`).emit('leaderboard:update', {
    scenario: result.scenario,
    rankings: newRankings
  });
});

// Emit alerts
eventEmitter.on('alert:triggered', (alert) => {
  io.to(`alerts:${alert.modelId}`).emit('alert:new', alert);
});
```

---

## Background Job System

### BullMQ Job Queue

```typescript
import { Queue, Worker, QueueScheduler } from 'bullmq';

// Job queues
const benchmarkQueue = new Queue('benchmark', { connection: redisConnection });
const analyticsQueue = new Queue('analytics', { connection: redisConnection });
const alertQueue = new Queue('alerts', { connection: redisConnection });

// Scheduled jobs
const scheduler = new QueueScheduler('benchmark', { connection: redisConnection });

// Schedule hourly benchmark runs
await benchmarkQueue.add(
  'scheduled-benchmark',
  {
    providers: ['anthropic', 'openai', 'google'],
    scenarios: ['code-generation', 'test-generation'],
    iterations: 3
  },
  {
    repeat: { cron: '0 * * * *' } // Every hour
  }
);

// Schedule daily model discovery
await benchmarkQueue.add(
  'discover-models',
  {},
  {
    repeat: { cron: '0 2 * * *' } // 2 AM daily
  }
);

// Workers
const benchmarkWorker = new Worker('benchmark', async (job) => {
  switch (job.name) {
    case 'run-benchmark':
      return await runBenchmark(job);
    case 'scheduled-benchmark':
      return await runScheduledBenchmark(job);
    case 'discover-models':
      return await discoverNewModels(job);
  }
}, { connection: redisConnection, concurrency: 5 });

const analyticsWorker = new Worker('analytics', async (job) => {
  switch (job.name) {
    case 'compute-aggregates':
      return await computeAggregates(job);
    case 'detect-degradation':
      return await detectPerformanceDegradation(job);
    case 'generate-report':
      return await generateReport(job);
  }
}, { connection: redisConnection, concurrency: 10 });

const alertWorker = new Worker('alerts', async (job) => {
  switch (job.name) {
    case 'trigger-alert':
      return await triggerAlert(job);
    case 'send-notification':
      return await sendNotification(job);
  }
}, { connection: redisConnection, concurrency: 20 });
```

### Job Implementations

```typescript
async function runBenchmark(job: Job<BenchmarkJobData>): Promise<void> {
  const { runId, userId, providers, models, scenarios, iterations, webhookUrl } = job.data;

  // Use existing BatchRunner from CLI tool
  const runner = new BatchRunner({
    providers,
    models,
    scenarios,
    iterations,
    outputDir: `/tmp/benchmarks/${runId}`
  });

  try {
    const results = await runner.run();

    // Save results to database
    for (const result of results.results) {
      await db.query(`
        INSERT INTO benchmark_runs (
          time, run_id, provider_id, model_id, scenario_id,
          total_score, score_breakdown, input_tokens, output_tokens,
          response_time_ms, estimated_cost, status
        ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)
      `, [
        new Date(),
        runId,
        await getProviderId(result.provider),
        await getModelId(result.model),
        await getScenarioId(result.scenario),
        result.score.total,
        JSON.stringify(result.score),
        result.metrics.inputTokens,
        result.metrics.outputTokens,
        result.metrics.responseTimeMs,
        result.metrics.estimatedCost,
        'success'
      ]);
    }

    // Trigger webhook if provided
    if (webhookUrl) {
      await fetch(webhookUrl, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ runId, status: 'completed', results })
      });
    }

    // Trigger analytics jobs
    await analyticsQueue.add('compute-aggregates', { runId });
    await analyticsQueue.add('detect-degradation', { runId });

  } catch (error) {
    logger.error('Benchmark failed', { error, runId });

    // Publish failure event
    await redis.publish(`benchmark:progress:${runId}`, JSON.stringify({
      status: 'failed',
      error: error.message
    }));

    throw error;
  }
}

async function discoverNewModels(job: Job): Promise<void> {
  const providers = await getAvailableProviders();

  for (const provider of providers) {
    try {
      // Fetch models from provider API
      const models = await provider.getModels();

      for (const model of models) {
        // Check if model already exists
        const existing = await db.query(
          'SELECT id FROM models WHERE provider_id = $1 AND name = $2',
          [provider.id, model]
        );

        if (!existing) {
          // New model detected!
          await db.query(`
            INSERT INTO models (provider_id, name, detected_at, last_seen_at)
            VALUES ($1, $2, NOW(), NOW())
          `, [provider.id, model]);

          logger.info('New model detected', { provider: provider.name, model });

          // Trigger alert
          await alertQueue.add('trigger-alert', {
            type: 'new_model',
            severity: 'low',
            message: `New model detected: ${provider.name}/${model}`,
            modelId: await getModelId(model)
          });

          // Auto-benchmark new model
          await benchmarkQueue.add('run-benchmark', {
            runId: uuidv7(),
            providers: [provider.name],
            models: [model],
            scenarios: ['code-generation', 'test-generation'],
            iterations: 3
          });
        } else {
          // Update last_seen_at
          await db.query(
            'UPDATE models SET last_seen_at = NOW() WHERE provider_id = $1 AND name = $2',
            [provider.id, model]
          );
        }
      }
    } catch (error) {
      logger.error('Model discovery failed', { error, provider: provider.name });
    }
  }
}

async function detectPerformanceDegradation(job: Job): Promise<void> {
  // Query for degraded models (>2 std dev drop)
  const degraded = await db.query(`
    WITH recent AS (
      SELECT model_id, scenario_id, AVG(total_score) AS recent_avg
      FROM benchmark_runs
      WHERE time > NOW() - INTERVAL '7 days'
      GROUP BY model_id, scenario_id
    ),
    baseline AS (
      SELECT
        model_id,
        scenario_id,
        AVG(total_score) AS baseline_avg,
        STDDEV(total_score) AS baseline_stddev
      FROM benchmark_runs
      WHERE time BETWEEN NOW() - INTERVAL '30 days' AND NOW() - INTERVAL '7 days'
      GROUP BY model_id, scenario_id
    )
    SELECT
      r.model_id,
      r.scenario_id,
      b.baseline_avg,
      r.recent_avg,
      (b.baseline_avg - r.recent_avg) AS degradation
    FROM recent r
    JOIN baseline b ON r.model_id = b.model_id AND r.scenario_id = b.scenario_id
    WHERE r.recent_avg < (b.baseline_avg - 2 * b.baseline_stddev)
  `);

  for (const row of degraded) {
    // Check if alert already triggered
    const existingAlert = await db.query(`
      SELECT id FROM performance_alerts
      WHERE model_id = $1
        AND scenario_id = $2
        AND alert_type = 'degradation'
        AND resolved_at IS NULL
    `, [row.model_id, row.scenario_id]);

    if (!existingAlert) {
      // Trigger new alert
      await db.query(`
        INSERT INTO performance_alerts (
          model_id, scenario_id, alert_type, severity,
          threshold_value, actual_value, message
        ) VALUES ($1, $2, 'degradation', 'high', $3, $4, $5)
      `, [
        row.model_id,
        row.scenario_id,
        row.baseline_avg,
        row.recent_avg,
        `Performance degraded by ${row.degradation.toFixed(2)} points`
      ]);

      // Send notifications
      await alertQueue.add('send-notification', {
        modelId: row.model_id,
        scenarioId: row.scenario_id,
        alertType: 'degradation',
        message: `Performance degraded by ${row.degradation.toFixed(2)} points`
      });
    }
  }
}
```

---

## Security & Privacy

### API Key Security

```typescript
import bcrypt from 'bcrypt';
import crypto from 'crypto';

// Generate API key
function generateApiKey(): string {
  return `sk_${crypto.randomBytes(32).toString('hex')}`;
}

// Hash API key for storage
async function hashApiKey(apiKey: string): Promise<string> {
  return await bcrypt.hash(apiKey, 12);
}

// Verify API key
async function verifyApiKey(apiKey: string, hash: string): Promise<boolean> {
  return await bcrypt.compare(apiKey, hash);
}

// Store user with hashed API key
async function createUser(email: string): Promise<{ apiKey: string }> {
  const apiKey = generateApiKey();
  const apiKeyHash = await hashApiKey(apiKey);

  await db.query(`
    INSERT INTO users (email, api_key_hash)
    VALUES ($1, $2)
  `, [email, apiKeyHash]);

  // Return plain API key once (user must save it)
  return { apiKey };
}
```

### Provider Credential Isolation

```typescript
import crypto from 'crypto';

const ENCRYPTION_KEY = Buffer.from(process.env.ENCRYPTION_KEY, 'hex'); // 32 bytes
const ALGORITHM = 'aes-256-gcm';

// Encrypt provider API key
function encryptApiKey(apiKey: string): {
  encryptedData: string;
  iv: string;
  authTag: string;
} {
  const iv = crypto.randomBytes(16);
  const cipher = crypto.createCipheriv(ALGORITHM, ENCRYPTION_KEY, iv);

  let encrypted = cipher.update(apiKey, 'utf8', 'hex');
  encrypted += cipher.final('hex');

  return {
    encryptedData: encrypted,
    iv: iv.toString('hex'),
    authTag: cipher.getAuthTag().toString('hex')
  };
}

// Decrypt provider API key
function decryptApiKey(
  encryptedData: string,
  iv: string,
  authTag: string
): string {
  const decipher = crypto.createDecipheriv(
    ALGORITHM,
    ENCRYPTION_KEY,
    Buffer.from(iv, 'hex')
  );

  decipher.setAuthTag(Buffer.from(authTag, 'hex'));

  let decrypted = decipher.update(encryptedData, 'hex', 'utf8');
  decrypted += decipher.final('utf8');

  return decrypted;
}

// Store user-provided provider credentials
async function storeProviderCredential(
  userId: string,
  providerId: string,
  apiKey: string
): Promise<void> {
  const { encryptedData, iv, authTag } = encryptApiKey(apiKey);

  await db.query(`
    INSERT INTO provider_credentials (user_id, provider_id, encrypted_api_key, iv, auth_tag)
    VALUES ($1, $2, $3, $4, $5)
    ON CONFLICT (user_id, provider_id) DO UPDATE
    SET encrypted_api_key = $3, iv = $4, auth_tag = $5, updated_at = NOW()
  `, [userId, providerId, encryptedData, iv, authTag]);
}
```

### Rate Limiting

```typescript
import rateLimit from '@fastify/rate-limit';

// Global rate limit (per IP)
app.register(rateLimit, {
  max: 1000, // 1000 requests
  timeWindow: '1 hour',
  redis: redisClient
});

// Tier-based rate limiting (per user)
async function checkUserRateLimit(userId: string): Promise<void> {
  const user = await db.query('SELECT tier, rate_limit_per_hour FROM users WHERE id = $1', [userId]);

  const key = `ratelimit:user:${userId}:${getCurrentHour()}`;
  const count = await redis.incr(key);

  if (count === 1) {
    await redis.expire(key, 3600); // Expire after 1 hour
  }

  if (count > user.rate_limit_per_hour) {
    throw new RateLimitError(`Rate limit exceeded: ${user.tier} tier allows ${user.rate_limit_per_hour} requests/hour`);
  }
}
```

---

## Scalability Strategy

### Horizontal Scaling

```typescript
// Stateless API servers (scale horizontally)
// - Load balanced via Nginx/HAProxy
// - Session stored in Redis (shared state)
// - No local file storage (use S3)

// Database connection pooling
const pool = new Pool({
  host: process.env.DB_HOST,
  database: process.env.DB_NAME,
  max: 20, // Max connections per instance
  idleTimeoutMillis: 30000
});

// Worker pool auto-scaling
const workerPool = new WorkerPool({
  queue: benchmarkQueue,
  minWorkers: 2,
  maxWorkers: 50,
  scaleUpThreshold: 100, // Queue length
  scaleDownThreshold: 10
});
```

### Caching Strategy

```typescript
import Redis from 'ioredis';

const redis = new Redis(process.env.REDIS_URL);

// Cache leaderboard (TTL: 5 minutes)
async function getLeaderboard(scenario: string, period: string): Promise<LeaderboardEntry[]> {
  const cacheKey = `leaderboard:${scenario}:${period}`;

  // Try cache first
  const cached = await redis.get(cacheKey);
  if (cached) {
    return JSON.parse(cached);
  }

  // Query database
  const rankings = await db.query(/* SQL */);

  // Cache for 5 minutes
  await redis.setex(cacheKey, 300, JSON.stringify(rankings));

  return rankings;
}

// Cache provider/model metadata (TTL: 1 hour)
async function getProvider(id: string): Promise<Provider> {
  const cacheKey = `provider:${id}`;

  const cached = await redis.get(cacheKey);
  if (cached) return JSON.parse(cached);

  const provider = await db.query(/* SQL */);
  await redis.setex(cacheKey, 3600, JSON.stringify(provider));

  return provider;
}

// Invalidate cache on updates
eventEmitter.on('benchmark:completed', async (result) => {
  // Invalidate leaderboard cache for affected scenario
  await redis.del(`leaderboard:${result.scenario}:*`);
});
```

### Database Optimization

```sql
-- Partitioning (TimescaleDB automatic)
-- Data automatically partitioned by time chunks (default: 7 days)

-- Compression (TimescaleDB)
ALTER TABLE benchmark_runs SET (
  timescaledb.compress,
  timescaledb.compress_segmentby = 'model_id, scenario_id'
);

-- Compress chunks older than 7 days
SELECT add_compression_policy('benchmark_runs', INTERVAL '7 days');

-- Continuous aggregates (pre-computed materialized views)
-- See model_performance_hourly example above

-- Index optimization
CREATE INDEX CONCURRENTLY idx_benchmark_runs_composite
ON benchmark_runs(model_id, scenario_id, time DESC)
WHERE status = 'success';

-- Analyze query performance
EXPLAIN ANALYZE SELECT /* query */;
```

---

## Deployment Architecture

### Infrastructure (Kubernetes / Fly.io)

```yaml
# Kubernetes deployment
apiVersion: apps/v1
kind: Deployment
metadata:
  name: aibaas-api
spec:
  replicas: 3
  selector:
    matchLabels:
      app: aibaas-api
  template:
    metadata:
      labels:
        app: aibaas-api
    spec:
      containers:
      - name: api
        image: registry.example.com/aibaas-api:latest
        ports:
        - containerPort: 3000
        env:
        - name: DATABASE_URL
          valueFrom:
            secretKeyRef:
              name: aibaas-secrets
              key: database-url
        - name: REDIS_URL
          valueFrom:
            secretKeyRef:
              name: aibaas-secrets
              key: redis-url
        resources:
          requests:
            memory: "256Mi"
            cpu: "250m"
          limits:
            memory: "512Mi"
            cpu: "500m"
        livenessProbe:
          httpGet:
            path: /health
            port: 3000
          initialDelaySeconds: 30
          periodSeconds: 10
        readinessProbe:
          httpGet:
            path: /ready
            port: 3000
          initialDelaySeconds: 10
          periodSeconds: 5
---
apiVersion: v1
kind: Service
metadata:
  name: aibaas-api
spec:
  selector:
    app: aibaas-api
  ports:
  - protocol: TCP
    port: 80
    targetPort: 3000
  type: LoadBalancer
---
# Worker deployment
apiVersion: apps/v1
kind: Deployment
metadata:
  name: aibaas-worker
spec:
  replicas: 5
  selector:
    matchLabels:
      app: aibaas-worker
  template:
    metadata:
      labels:
        app: aibaas-worker
    spec:
      containers:
      - name: worker
        image: registry.example.com/aibaas-worker:latest
        env:
        - name: DATABASE_URL
          valueFrom:
            secretKeyRef:
              name: aibaas-secrets
              key: database-url
        - name: REDIS_URL
          valueFrom:
            secretKeyRef:
              name: aibaas-secrets
              key: redis-url
        resources:
          requests:
            memory: "512Mi"
            cpu: "500m"
          limits:
            memory: "1Gi"
            cpu: "1000m"
```

### Alternative: Fly.io Deployment

```toml
# fly.toml
app = "aibaas"
primary_region = "iad"

[build]
  dockerfile = "Dockerfile"

[env]
  NODE_ENV = "production"

[http_service]
  internal_port = 3000
  force_https = true
  auto_stop_machines = true
  auto_start_machines = true
  min_machines_running = 2

  [[http_service.checks]]
    interval = "10s"
    grace_period = "5s"
    method = "GET"
    path = "/health"
    protocol = "http"

[[services]]
  protocol = "tcp"
  internal_port = 3000

  [[services.ports]]
    port = 80
    handlers = ["http"]

  [[services.ports]]
    port = 443
    handlers = ["tls", "http"]

[scaling]
  min_machines = 2
  max_machines = 10

# Postgres (managed)
[postgres]
  app = "aibaas-db"
  size = "shared-cpu-2x"

# Redis (managed)
[redis]
  app = "aibaas-redis"
  size = "shared-cpu-1x"
```

---

## Monitoring & Observability

### Metrics (Prometheus + Grafana)

```typescript
import { register, Counter, Histogram, Gauge } from 'prom-client';

// API request metrics
const apiRequestDuration = new Histogram({
  name: 'http_request_duration_seconds',
  help: 'Duration of HTTP requests in seconds',
  labelNames: ['method', 'route', 'status_code']
});

const apiRequestTotal = new Counter({
  name: 'http_requests_total',
  help: 'Total number of HTTP requests',
  labelNames: ['method', 'route', 'status_code']
});

// Benchmark metrics
const benchmarkDuration = new Histogram({
  name: 'benchmark_duration_seconds',
  help: 'Duration of benchmark runs',
  labelNames: ['provider', 'model', 'scenario']
});

const benchmarkScore = new Histogram({
  name: 'benchmark_score',
  help: 'Benchmark quality score',
  labelNames: ['provider', 'model', 'scenario'],
  buckets: [0, 2, 4, 6, 8, 10]
});

const activeWorkers = new Gauge({
  name: 'active_workers',
  help: 'Number of active benchmark workers'
});

// Middleware to track metrics
app.addHook('onRequest', async (request, reply) => {
  request.startTime = Date.now();
});

app.addHook('onResponse', async (request, reply) => {
  const duration = (Date.now() - request.startTime) / 1000;

  apiRequestDuration
    .labels(request.method, request.routerPath, reply.statusCode.toString())
    .observe(duration);

  apiRequestTotal
    .labels(request.method, request.routerPath, reply.statusCode.toString())
    .inc();
});

// Expose metrics endpoint
app.get('/metrics', async (request, reply) => {
  reply.type('text/plain');
  return await register.metrics();
});
```

### Logging (Pino + Loki)

```typescript
import pino from 'pino';

const logger = pino({
  level: process.env.LOG_LEVEL || 'info',
  formatters: {
    level: (label) => {
      return { level: label };
    }
  },
  transport: {
    target: 'pino-loki',
    options: {
      batching: true,
      interval: 5,
      host: process.env.LOKI_URL,
      labels: {
        app: 'aibaas',
        environment: process.env.NODE_ENV
      }
    }
  }
});

// Structured logging
logger.info({
  event: 'benchmark_started',
  runId: '123',
  provider: 'anthropic',
  model: 'claude-4.5-sonnet'
}, 'Benchmark run started');

// Error logging
logger.error({
  error: err,
  runId: '123'
}, 'Benchmark failed');
```

### Alerting (PagerDuty / Sentry)

```typescript
import * as Sentry from '@sentry/node';

Sentry.init({
  dsn: process.env.SENTRY_DSN,
  environment: process.env.NODE_ENV,
  tracesSampleRate: 0.1
});

// Error tracking
app.setErrorHandler(async (error, request, reply) => {
  Sentry.captureException(error, {
    contexts: {
      request: {
        url: request.url,
        method: request.method,
        headers: request.headers
      }
    }
  });

  logger.error({ error }, 'Request failed');

  reply.status(500).send({ error: 'Internal server error' });
});
```

---

## Implementation Roadmap

### Phased Rollout Strategy (Based on Competitive Analysis)

**Inspired by**: Aider (manual → automated), LiveBench (monthly updates), LiveCodeBench Pro (continuous), MASK (public/private splits)

**Rollout Philosophy**:
1. **Ship Fast**: MVP in 4 weeks (before competitors)
2. **Validate Early**: Beta test with 10 users in Month 3
3. **Monetize Quickly**: Launch Pro tier by Month 6
4. **Build Moat**: Accumulate historical data (compound advantage)

### Phase 1: MVP (Weeks 1-4) — Internal Beta

**Goal**: Validate core value prop with internal users

**Features**:
- ✅ 50 benchmark tasks (Code Generation: 25, Code Review: 25)
- ✅ 5 models (Claude 4.5, GPT-5, Gemini 2.5, DeepSeek R1, Llama 3.3)
- ✅ Basic scoring (0-10 scale, accuracy-only)
- ✅ Manual benchmark runs (weekly)
- ✅ Static leaderboard (HTML table)
- ✅ Human baseline (10 developers, 1 tier: mid-level)

**Tech Stack**:
```typescript
{
  frontend: "Next.js 15 + Tailwind CSS 4 + shadcn/ui",
  backend: "Fastify 5.x + PostgreSQL 17 + TimescaleDB",
  testing: "Vitest 3.x",
  deployment: "Vercel (frontend) + Railway (backend)"
}
```

**Database Setup**:
- [x] Week 1: PostgreSQL 17 + TimescaleDB schema
  - Core tables (providers, models, scenarios, benchmark_runs)
  - Human baseline tables
  - Migration scripts
  - Seed data (5 providers, 5 models, 50 scenarios)

**Backend API**:
- [x] Week 2: REST API MVP
  - Core endpoints (`/providers`, `/models`, `/leaderboard`)
  - Authentication (API keys)
  - Rate limiting (1000 req/hour per IP)

**Background Jobs**:
- [x] Week 3: BullMQ setup
  - Migrate BatchRunner from CLI to job-based
  - Manual benchmark run trigger
  - Results storage in PostgreSQL

**Frontend Dashboard**:
- [x] Week 4: Next.js 15 setup
  - Basic leaderboard UI (sortable table)
  - No charts yet (just numbers)
  - Deploy to Vercel staging

**Success Criteria**:
- ✅ 10 internal users provide feedback
- ✅ Human baseline validates tasks (80%+ accuracy)
- ✅ Models differentiate (>2 point spread)
- ✅ Benchmark runs complete in <4 hours
- ✅ Leaderboard loads in <1s

**Timeline**: 4 weeks
**Team**: 2 engineers (full-time)
**Budget**: $2k (compute, APIs, 10 developers × $50/hr × 4 hrs = $2k)

**Deliverables**:
- ✅ Internal dashboard at https://benchmark.tamma.internal
- ✅ PostgreSQL database with 30 days retention
- ✅ Benchmark completion report (PDF)

### Phase 2: Public Beta (Weeks 5-12) — Launch to Early Adopters

**Goal**: Attract early adopters, generate buzz

**Features**:
- ✅ 150 benchmark tasks (add Refactoring: 50, Debugging: 25, Security: 25)
- ✅ 8 models (add GPT-4.1, Codex, Qwen 2.5)
- ✅ Advanced scoring (accuracy, confidence, efficiency, robustness, style)
- ✅ Automated runs (hourly via GitHub Actions)
- ✅ Interactive leaderboard (filters, sorting, comparison)
- ✅ Basic API (REST, read-only, 1k req/month free)
- ✅ Human baselines (50 developers, 3 tiers: junior/mid/senior)
- ✅ Percentile ranks ("better than X% of developers")
- ✅ Cost + latency tracking
- ✅ Time-window slider (contamination filtering)

**Tech Stack Additions**:
- [ ] Week 5-6: TimescaleDB enhancements
  - Continuous aggregates (hourly rollups)
  - Compression (7-day window)
  - Retention policies
- [ ] Week 7-8: Interactive UI
  - Recharts for cost visualization
  - D3.js for scatter plots
  - Radar charts (category breakdown)
  - Time-window slider component
- [ ] Week 9-10: Human baseline collection
  - Recruit 50 developers (Bootcamps, LinkedIn, Twitter)
  - Task assignment platform
  - Results aggregation
  - Percentile calculation
- [ ] Week 11-12: Public launch prep
  - Blog post draft
  - Landing page copy
  - SEO optimization
  - Email waitlist setup

**Marketing**:
- Blog post: "We benchmarked 8 AI providers — here's what we found"
- Reddit: r/MachineLearning, r/programming
- Twitter: Tweet thread from @TammaAI
- Email: Early waitlist (500 signups target)

**Success Criteria**:
- ✅ 1,000 monthly active users
- ✅ 50 API signups (free tier)
- ✅ 5 blog mentions/citations
- ✅ <5s P95 leaderboard load time
- ✅ 99.5% API uptime

**Timeline**: 8 weeks
**Team**: 3 engineers (2 full-time, 1 contractor)
**Budget**: $8k (compute $2k, APIs $2k, 50 devs × $50/hr × 4 hrs = $10k total)

**Deliverables**:
- ✅ Public website at https://aibaas.io
- ✅ Free tier (1k API requests/month, 7-day history)
- ✅ Public leaderboard with 8 models
- ✅ Human baseline data (50 developers)

### Phase 3: Pro Tier Launch (Weeks 13-24) — Monetization

**Goal**: Launch Pro tier, validate $49/mo pricing

**Features**:
- ✅ 300 benchmark tasks (add Architecture: 25, Documentation: 25)
- ✅ Alerting system (Slack, email, webhooks)
- ✅ Historical data (6 months retention)
- ✅ Custom scenarios (Pro tier, user-uploaded tasks)
- ✅ Full API (REST + GraphQL, 100k req/month)
- ✅ Confidence intervals (bootstrap 95% CI)
- ✅ Contamination detection (release date tracking)
- ✅ Private test set (80% hidden, quarterly rotation)
- ✅ Overfitting penalty
- ✅ Monthly reports (email, PDF)

**Tech Stack Additions**:
- [ ] Week 13-14: Monetization infrastructure
  - Stripe integration (subscriptions)
  - Billing dashboard
  - Usage tracking
  - Invoice generation
- [ ] Week 15-16: Alerting system
  - Slack API integration
  - SendGrid email templates
  - Webhook service
  - Alert configuration UI
- [ ] Week 17-18: Advanced features
  - GraphQL API (Apollo Server 4)
  - Custom scenario upload
  - Private test set management
  - Confidence interval calculation
- [ ] Week 19-20: Contamination prevention
  - Release date tracking
  - Contamination detection queries
  - UI highlighting (red rows, warnings)
  - Problem versioning (aibaas_v2025_12)
- [ ] Week 21-22: Pro features polish
  - Monthly report generator
  - Historical trend charts
  - Head-to-head comparison
  - Cost-performance scatter plots
- [ ] Week 23-24: Marketing & launch
  - Product Hunt launch
  - Hacker News "Show HN"
  - Blog post series
  - Outreach to AI startups

**Pricing**:
- **Free**: Public leaderboard, 1k API req/month, 7-day history
- **Pro ($49/mo)**: Alerts, custom scenarios, 100k API req/month, 90-day history
- **Enterprise ($499/mo)**: Private benchmarks, SLA monitoring, 1M API req/month, unlimited history

**Marketing**:
- Launch announcement: "AIBaaS Pro — real-time AI quality monitoring for teams"
- Product Hunt launch (aim for #1 Product of the Day)
- Hacker News "Show HN: AIBaaS Pro"
- Outreach to AI startups (Anthropic/OpenAI/Google customers)
- Case study: "How [Company] saved $10k/month by switching AI providers"

**Success Criteria**:
- ✅ 50 Pro customers ($2,450 MRR)
- ✅ 5 Enterprise pilots ($2,495 MRR)
- ✅ 10,000 monthly active users (free tier)
- ✅ <2% churn (Pro tier)
- ✅ 4.5+ star rating (user reviews)

**Timeline**: 12 weeks
**Team**: 4 engineers (3 full-time, 1 contractor)
**Budget**: $25k (compute $5k, APIs $3k, 50 additional devs for baselines $10k, marketing $5k, operations $2k)

**Deliverables**:
- ✅ Pro tier at $49/month
- ✅ First paying customers
- ✅ Revenue: $2.5k+ MRR
- ✅ Human baselines (100 developers total, 4 tiers)
- ✅ Contamination prevention system operational

### Phase 4: Enterprise & Scale (Months 7-12) — Industry Standard

**Goal**: Become the de facto AI quality benchmark (like Lighthouse for web performance)

**Features**:
- ✅ Enterprise tier features
  - Private benchmarks (company-specific tasks)
  - SLA monitoring (uptime, latency, error rates)
  - Dedicated support (Slack channel)
  - Custom model support (proprietary models)
  - White-label option (rebrand leaderboard)
- ✅ Expanded test suite
  - 500+ tasks (refresh 25% quarterly)
  - Multilingual support (Python, JavaScript, Go, Rust)
  - Domain-specific benchmarks (fintech, healthcare, legal)
- ✅ Advanced analytics
  - Model recommendation engine (ML-powered)
  - Cost optimization suggestions
  - Latency predictions
  - Performance forecasting (ARIMA models)
- ✅ Community features
  - User-submitted tasks (voting system)
  - Leaderboard embeds (iframe widgets)
  - API integrations (Zapier, n8n)
  - Open-source SDK (Python, TypeScript, Go)

**Marketing Strategy**:
- **Content Marketing**:
  - Monthly blog posts ("State of AI Code Quality")
  - Quarterly whitepapers (PDF downloads)
  - Webinars with AI providers
  - Podcast sponsorships (Software Engineering Daily, Practical AI)
- **SEO Domination**:
  - Target keywords: "AI model comparison", "AI code quality", "AI benchmark"
  - Backlinks from 20+ AI blogs/publications
  - Guest posts on major engineering blogs
- **Partnerships**:
  - Integration with Anthropic Console (embed AIBaaS leaderboard)
  - Partnership with OpenAI Eval team (cross-promotion)
  - Featured in Google AI Studio
- **Events**:
  - Conference talks (NeurIPS, ICML, ICLR)
  - Workshops at developer conferences
  - Sponsor hackathons (prize: 1-year Pro subscription)

**Success Criteria**:
- ✅ 500 Pro customers ($24.5k MRR)
- ✅ 20 Enterprise customers ($10k MRR)
- ✅ 100k monthly active users
- ✅ 50+ blog citations
- ✅ Mentioned in 3+ research papers
- ✅ <1% churn (annual contracts)

**Timeline**: 6 months
**Team**: 6 engineers (4 full-time, 2 contractors)
**Budget**: $150k (compute $30k, APIs $20k, 250 devs for expanded baselines $50k, marketing $30k, conferences/events $10k, operations $10k)

**Deliverables**:
- ✅ Enterprise tier at $499/month
- ✅ Revenue: $35k+ MRR ($420k ARR)
- ✅ Industry recognition (conference talks, research citations)
- ✅ 500+ task benchmark suite
- ✅ Open-source SDK (Python, TypeScript, Go)

---

## Cost Analysis

### Infrastructure Costs (Monthly)

| Service | Tier | Cost |
|---------|------|------|
| **Fly.io / GKE** | 3 API instances | $30-60 |
| **PostgreSQL** | Managed DB (20GB) | $25-50 |
| **TimescaleDB** | Extension (included) | $0 |
| **Redis** | Managed (2GB) | $15-30 |
| **S3 / R2** | 100GB storage | $5-10 |
| **Cloudflare** | CDN + DNS | $0-20 |
| **Auth0 / Clerk** | 1K users | $0-25 |
| **SendGrid** | Email (10K/month) | $0-15 |
| **Sentry** | Error tracking | $0-26 |
| **Total** | | **$75-236/month** |

### AI Provider Costs (Internal Testing)

| Provider | Tests/Day | Cost/Test | Monthly Cost |
|----------|-----------|-----------|--------------|
| Anthropic | 100 | $0.01 | $30 |
| OpenAI | 100 | $0.012 | $36 |
| Google | 100 | $0.005 | $15 |
| OpenRouter | 50 | $0.008 | $12 |
| Ollama | Unlimited | $0 | $0 |
| **Total** | | | **$93/month** |

### Total Operating Cost

- **Infrastructure**: $75-236/month
- **AI testing**: $93/month
- **Total**: **$168-329/month**

### Revenue Projections

| Tier | Price | Users | MRR |
|------|-------|-------|-----|
| Free | $0 | 1000 | $0 |
| Pro | $29 | 100 | $2,900 |
| Enterprise | $500 | 5 | $2,500 |
| **Total** | | **1105** | **$5,400** |

**Profit**: $5,400 - $329 = **$5,071/month** 🎉

---

## Success Metrics

### Technical Metrics

- **API Uptime**: >99.9% (measured via UptimeRobot)
- **API Latency**: P95 <500ms (measured via Prometheus)
- **Job Queue Throughput**: >100 jobs/hour
- **Database Query Performance**: P95 <100ms
- **Error Rate**: <0.1% of requests
- **Benchmark Run Frequency**: Every hour (automated)
- **Leaderboard Update Latency**: <30 minutes after benchmark completion

### Business Metrics (Updated with Competitive Analysis Insights)

#### User Acquisition (Revised Targets)

**Phase 1 (Month 1-3)**: Internal Beta → Public Beta
- Month 1: 100 beta testers (internal)
- Month 2: 1,000 signups (public launch)
- Month 3: 5,000 signups (Product Hunt, Hacker News)

**Phase 2 (Month 4-6)**: Pro Tier Launch
- Month 4: 10,000 signups
- Month 5: 15,000 signups
- Month 6: 25,000 signups (Pro tier launch)

**Phase 3 (Month 7-12)**: Enterprise & Scale
- Month 9: 50,000 signups
- Month 12: 100,000 signups (industry standard status)

#### Conversion Rates

**Free → Pro**:
- Target: 5-10% (industry standard SaaS)
- Month 6: 50 Pro customers (1,000 free users × 5% = 50)
- Month 12: 500 Pro customers (10,000 free users × 5% = 500)

**Pro → Enterprise**:
- Target: 2-5% (high-touch sales)
- Month 6: 2-3 Enterprise pilots
- Month 12: 20 Enterprise customers (500 Pro × 4% = 20)

#### Revenue (Updated Projections)

**Monthly Recurring Revenue (MRR)**:
```
Month 1:  $0 (internal beta)
Month 3:  $0 (public beta, free tier only)
Month 6:  $2,450 (50 Pro × $49/mo)
Month 9:  $15,000 (250 Pro × $49/mo + 10 Enterprise × $499/mo)
Month 12: $34,500 (500 Pro × $49/mo + 20 Enterprise × $499/mo)
```

**Annual Recurring Revenue (ARR)**: $414k by Month 12

**Lifetime Value (LTV)**:
- Pro: $49/mo × 24 months average retention = $1,176
- Enterprise: $499/mo × 36 months average retention = $17,964

**Customer Acquisition Cost (CAC)**:
- Pro: $100 (organic SEO, content marketing)
- Enterprise: $2,000 (outbound sales, conferences)

**LTV/CAC Ratio**:
- Pro: $1,176 / $100 = 11.8x (healthy: >3x)
- Enterprise: $17,964 / $2,000 = 9x (healthy: >3x)

#### Retention & Churn

**Churn Targets**:
- Free tier: 10-20% monthly churn (expected for freemium)
- Pro tier: <2% monthly churn (<24% annual)
- Enterprise: <1% monthly churn (<12% annual, contracts)

**Retention Strategies**:
- Email nurture campaigns (weekly tips, updates)
- Usage alerts ("You're close to your API limit")
- Feature announcements (new models, categories)
- Community engagement (user-submitted tasks, voting)

#### Engagement Metrics

**Daily Active Users (DAU)**:
- Free tier: 20% DAU/MAU ratio
- Pro tier: 50% DAU/MAU ratio (higher stakes)
- Enterprise: 70% DAU/MAU ratio (mission-critical)

**API Calls/Day**:
- Month 1: 1,000 calls/day (internal beta)
- Month 6: 50,000 calls/day (Pro tier launch)
- Month 12: 500,000 calls/day (100k users)

**Average Session Duration**:
- Free tier: 3-5 minutes (leaderboard browsing)
- Pro tier: 10-15 minutes (alerts, custom scenarios)
- Enterprise: 20-30 minutes (deep analytics, reports)

### Product Metrics

**Leaderboard Views**:
- Month 3: 10,000 views/month (public launch)
- Month 6: 50,000 views/month (SEO traction)
- Month 12: 200,000 views/month (industry standard)

**API Documentation Views**:
- Month 3: 2,000 views/month
- Month 6: 10,000 views/month (Pro tier docs)
- Month 12: 50,000 views/month (developer adoption)

**Custom Scenarios Created** (Pro tier):
- Month 6: 50 scenarios (10 Pro customers × 5 scenarios)
- Month 12: 500 scenarios (100 active Pro customers × 5 scenarios)

**Alerts Triggered**:
- Month 6: 500 alerts/month (50 Pro customers)
- Month 12: 10,000 alerts/month (500 Pro + 20 Enterprise)

### Competitive Moat KPIs (New Section)

Based on competitive analysis, these metrics measure our defensibility:

#### Data Moat (Compound Advantage)

**Historical Data Accumulation**:
- Month 6: 180 days × 24 hours × 8 models = 34,560 benchmark runs
- Month 12: 365 days × 24 hours × 12 models = 105,120 benchmark runs
- **Why it matters**: Competitors starting later cannot replicate this historical context

**Human Baseline Data**:
- Month 6: 100 developer baselines (3 tiers)
- Month 12: 350 developer baselines (4 tiers)
- **Why it matters**: Expensive and time-consuming to replicate ($50-75k investment)

#### Network Effects

**User-Submitted Tasks**:
- Month 6: 50 community tasks (10% of total)
- Month 12: 150 community tasks (30% of total)
- **Why it matters**: More users → more tasks → more value for all users

**Leaderboard Citations**:
- Month 6: 5 blog posts citing our leaderboard
- Month 12: 50 blog posts + 3 research papers
- **Why it matters**: Becomes canonical reference (like GitHub stars)

#### Brand Authority

**Industry Recognition**:
- Month 6: Featured in 1 major publication (TechCrunch, VentureBeat)
- Month 12: Cited in 3 research papers, 2 conference talks
- **Why it matters**: "AIBaaS" becomes synonymous with "AI benchmarking"

**SEO Dominance**:
- Month 6: Rank #1 for "AI model comparison"
- Month 12: Rank #1 for 10 keywords (AI benchmark, code quality, etc.)
- **Why it matters**: Organic traffic compounds over time

#### Switching Costs

**API Integrations**:
- Month 6: 10 Pro customers using API in CI/CD pipelines
- Month 12: 100 Pro + Enterprise customers with deep integrations
- **Why it matters**: Painful to migrate once embedded in workflows

**Custom Scenarios**:
- Month 6: 50 proprietary scenarios (Pro tier)
- Month 12: 500 proprietary scenarios locked into platform
- **Why it matters**: Cannot export custom tasks easily

### Growth Multipliers

**Viral Loops**:
- **Leaderboard embeds**: 50 websites embed our leaderboard by Month 12
- **API SDK**: 10,000 downloads of Python SDK by Month 12
- **Open-source community**: 500 GitHub stars by Month 12

**Partnerships**:
- Month 6: 1 partnership (e.g., Anthropic Console integration)
- Month 12: 5 partnerships (OpenAI, Google AI Studio, etc.)
- **Why it matters**: Distribution through partner channels

**Content Marketing ROI**:
- Month 6: 10 blog posts → 5,000 organic visits/month
- Month 12: 50 blog posts → 50,000 organic visits/month
- **Conversion**: 2% of organic visitors sign up → 1,000 signups/month by Month 12

---

## Appendices

### A. Technology Stack Reference

```typescript
const TECH_STACK = {
  backend: {
    runtime: 'Node.js 22 LTS',
    framework: 'Fastify 5.x',
    language: 'TypeScript 5.7+',
    graphql: 'Apollo Server 4',
    jobs: 'BullMQ',
    validation: 'Zod',
    testing: 'Vitest',
    logging: 'Pino'
  },
  database: {
    primary: 'PostgreSQL 17',
    timeSeries: 'TimescaleDB extension',
    cache: 'Redis 7',
    orm: 'Drizzle ORM or raw SQL'
  },
  frontend: {
    framework: 'Next.js 15 (App Router)',
    ui: 'React 19',
    styling: 'Tailwind CSS 4',
    charts: 'Recharts + D3.js',
    realtime: 'React Query + SSE',
    state: 'Zustand'
  },
  infrastructure: {
    deployment: 'Fly.io or GKE',
    cdn: 'Cloudflare',
    storage: 'R2 or S3',
    monitoring: 'Grafana + Prometheus',
    logging: 'Loki',
    errors: 'Sentry',
    alerting: 'PagerDuty (optional)'
  },
  auth: {
    service: 'Auth0 or Clerk',
    apiKeys: 'Custom (bcrypt hashed)',
    oauth: 'GitHub, Google'
  },
  payments: {
    service: 'Stripe',
    webhooks: 'Stripe webhooks for subscriptions'
  }
};
```

### B. References

- [TimescaleDB Documentation](https://docs.timescale.com/)
- [BullMQ Documentation](https://docs.bullmq.io/)
- [Fastify Documentation](https://www.fastify.io/)
- [Next.js 15 Documentation](https://nextjs.org/docs)
- [Apollo Server Documentation](https://www.apollographql.com/docs/apollo-server/)

---

**Document Status**: Updated with Competitive Intelligence
**Version**: 2.0.0
**Date**: 2025-11-01
**Updates**:
- Added 7-category benchmark suite (from 4 scenarios)
- Added human percentile rankings (VirologyTest approach)
- Added contamination prevention strategy (LiveBench + MASK + SimpleBench)
- Added confidence intervals and statistical ranking (MASK approach)
- Added cost visualization and Elo ratings (Aider + LiveCodeBench Pro)
- Revised implementation roadmap with phased rollout
- Updated success metrics with competitive moat KPIs (LTV/CAC, data moat, network effects)
- Enhanced data model with human baselines, contamination tracking, adversarial tests
- Added UI/UX enhancements from 17+ benchmark analysis

**Approved By**: Pending review

**Related Documents**:
- `.dev/spikes/aibaas/COMPETITIVE-ANALYSIS.md` - Full competitive analysis synthesis
- `.dev/spikes/aibaas/benchmark-research/` - 8 benchmark deep dives (Aider, LiveBench, LiveCodeBench Pro, MASK, SimpleBench, VirologyTest, HLE, Domain-Specific)

---
