# Epic Technical Specification: Observability & Production Readiness

Date: 2025-10-28
Author: meywd
Epic ID: 5
Status: Draft

---

## Overview

Epic 5 implements the Observability & Production Readiness layer that transforms Tamma from a functional prototype into a production-ready autonomous development platform. Building on Epic 1's multi-provider foundation, Epic 2's workflow orchestration, Epic 3's quality gates, and Epic 4's event sourcing, this epic delivers the **operational excellence** capabilities required for confident deployment: structured logging with Pino (5x faster than Winston), Prometheus metrics collection, real-time dashboards with Server-Sent Events (<500ms refresh), intelligent alerting, user feedback loops, comprehensive integration testing, and complete documentation.

Unlike traditional observability stacks that bolt monitoring onto existing systems, Tamma's observability is **deeply integrated from the start**, leveraging Epic 4's event sourcing foundation to provide unprecedented visibility into autonomous decision-making. Every AI provider selection, every quality gate retry, every escalation is not only logged but also queryable via the Event Trail Exploration UI (Story 5.5), enabling developers to answer questions like "Why did the AI choose approach B for issue #123?" or "What caused the build to fail on attempt 2?" The real-time dashboards (Stories 5.3-5.4) transform raw metrics into actionable insights: system health indicators prevent outages, development velocity charts identify bottlenecks, and feedback trends guide product roadmap decisions.

Epic 5 addresses the critical gap between "it works on my machine" and "it's ready for alpha users": Story 5.8 establishes integration testing with >80% code coverage, Stories 5.9a-5.9d provide comprehensive documentation (installation, usage, API reference, searchable docs site) reviewed by external beta testers, and Story 5.10 prepares multi-arch Docker images and release artifacts for the v0.1.0-alpha launch. The feedback collection system (Story 5.7) closes the loop with users, capturing satisfaction ratings and improvement suggestions directly after PR merges, ensuring Tamma evolves based on real-world usage rather than assumptions.

## MVP Scope Clarification

**Epic 5 has PARTIAL MVP scope** - essential debugging/monitoring capabilities are MVP critical, while UI dashboards are optional for self-maintenance validation:

**MVP CRITICAL (10 stories):**
- **Story 5.1 (Logging)**: Essential for debugging stuck workflows when Tamma works on itself. Structured logs enable diagnosis of autonomous loop failures.
- **Story 5.2 (Metrics)**: Essential for monitoring autonomous loop health. Tracks completion rates, escalation rates, quality metrics critical for self-maintenance validation.
- **Story 5.6 (Basic Alerts)**: CLI output, email, Slack webhooks sufficient for MVP. Full dashboard integration optional. Detects and responds to escalations/errors.
- **Story 5.8 (Integration Tests)**: Essential for validating self-maintenance. Comprehensive test suite ensures Tamma's self-implemented changes don't break core functionality.
- **Story 5.9a (Installation & Setup Documentation)**: Essential for alpha release - users must know how to install Tamma (npm, Docker, binaries).
- **Story 5.9b (Usage & Configuration Documentation)**: Essential for alpha release - users must know how to configure and run Tamma (CLI commands, config reference, provider setup).
- **Story 5.9c (API Reference Documentation)**: Essential for self-maintenance - Tamma may need to reference REST API, webhooks, event schema when implementing new features.
- **Story 5.9d (Full Documentation Website)**: Essential for alpha release - replaces "Coming Soon" marketing site with comprehensive searchable docs.
- **Story 5.10 (Alpha Release)**: Essential for MVP launch, includes self-maintenance validation milestone.

**MVP OPTIONAL (5 stories - Post-MVP):**
- **Story 5.3 (Health Dashboard UI)**: CLI-based monitoring and log tailing sufficient for MVP. UI provides better UX but not required for self-maintenance validation.
- **Story 5.4 (Velocity Dashboard UI)**: CLI-based metrics queries and log analysis sufficient for MVP. Velocity charts provide better visualization but not required.
- **Story 5.5 (Event Trail UI)**: Event query API (Epic 4 Story 4.7) provides programmatic access sufficient for MVP debugging. UI improves UX but not critical.
- **Story 5.7 (Feedback Collection)**: User feedback valuable for post-MVP improvement but not required for self-maintenance validation.
- **Story 5.9e (Video Walkthrough)**: Nice-to-have for alpha release but not required for self-maintenance validation. Can be created by community post-MVP.

**Rationale**: Self-maintenance goal requires debugging capabilities (logs, metrics, alerts, tests) but NOT visual dashboards. CLI/log-based monitoring sufficient for MVP. UI dashboards deferred to post-MVP for better resource allocation.

## Objectives and Scope

**In Scope:**

- **Story 5.1:** Structured Logging Implementation - Pino-based JSON logging with log levels (DEBUG/INFO/WARN/ERROR), correlation IDs, sensitive data redaction, stdout/file/aggregation outputs, <10 logs per event volume control
- **Story 5.2:** Metrics Collection Infrastructure - Prometheus client integration, counter metrics (issues_processed, prs_created, prs_merged, escalations), gauge metrics (active_loops, pending_approvals, queue_depth), histogram metrics (completion_duration, ai_request_duration, test_duration), /metrics HTTP endpoint with 15s scrape interval
- **Story 5.3:** Real-Time Dashboard - System Health - Web dashboard at /dashboard with active loops, pending approvals, recent escalations, current issue progress, 10s WebSocket/SSE auto-refresh, status indicators (healthy/degraded/critical), <2s initial load
- **Story 5.4:** Real-Time Dashboard - Development Velocity - /dashboard/velocity with line charts (issues/day, time-to-merge trends), bar charts (PR success rates), filters (date range, labels, providers), key metrics (throughput, cycle time, quality), PNG/PDF export, mobile-responsive
- **Story 5.5:** Event Trail Exploration UI - /dashboard/events with event list (timestamp, type, actor, summary), filtering (date, type, correlation, issue), full-text search, expand row for JSON details, "Follow correlation ID" action, pagination (100/page with infinite scroll)
- **Story 5.6:** Alert System for Critical Issues - Triggers (escalation after 3 retries, uncaught exceptions, API rate limits, event store failures), channels (CLI, webhook, email), alert payload (severity, title, description, correlation ID, suggested action), rate limiting (5/min), history storage, custom rule configuration
- **Story 5.7:** Feedback Collection System - Post-PR-merge rating prompt (thumbs up/down), negative feedback text collection, database storage (timestamp, correlation ID, rating, comment), /dashboard/feedback with satisfaction trends and keyword analysis, CSV export, privacy-respecting (no PII without consent)
- **Story 5.8:** Integration Testing Suite - E2E tests with mock AI provider and Git API, scenarios (happy path, build failure retry, test failure escalation, ambiguous requirements), CI/CD pipeline integration, event sequence validation, <5min full suite runtime, >80% code coverage target, event trail assertions
- **Story 5.9a:** Installation & Setup Documentation - Installation instructions for npm (@tamma/cli), Docker (docker-compose, standalone container), binaries (Windows/macOS/Linux), source build (git clone + pnpm install), prerequisites (Node.js 22, PostgreSQL 17), initial configuration wizard, troubleshooting common installation issues
- **Story 5.9b:** Usage & Configuration Documentation - CLI command reference (tamma run, tamma init, tamma config), configuration file reference (all options with examples), environment variable mapping, deployment modes (CLI, service, web, worker), provider setup guides (Anthropic Claude, OpenAI, GitHub, GitLab), webhook integration setup, troubleshooting guide (common errors, debug mode, log analysis)
- **Story 5.9c:** API Reference Documentation - REST API endpoints (/api/v1/tasks, /api/v1/config, /webhooks/*), event schema (Epic 4 format), metrics endpoints (/metrics OpenMetrics spec), webhook payload formats (GitHub, GitLab JSON schemas), authentication (JWT tokens), error responses, rate limiting, versioning strategy
- **Story 5.9d:** Full Documentation Website - GitHub Pages or Cloudflare Pages hosting, searchable documentation (Algolia DocSearch or similar), navigation organized by sections (Getting Started, Configuration, API, Troubleshooting), architecture diagrams (C4 model: context, containers, components), tutorials and guides (First Autonomous PR, CI/CD Integration, Self-Hosting), replaces Story 1-12 marketing site with full docs, external beta tester review for clarity
- **Story 5.9e:** Video Walkthrough (OPTIONAL) - 5-10 minute screen recording demonstrating complete autonomous loop (issue selection → plan approval → PR creation → merge), side-by-side views showing logs/metrics/event trail in real-time, published to YouTube/Vimeo with embedded player in docs site
- **Story 5.10:** Alpha Release Preparation - Release checklist validation (ACs met, tests passing, docs complete, security review), build artifacts (Docker multi-arch, binaries for Windows/macOS/Linux, source tarball), release notes (features, limitations, breaking changes, upgrade path), GitHub v0.1.0-alpha release with prerelease tag, announcement materials, telemetry consent opt-in

**Out of Scope:**

- Production-grade monitoring infrastructure setup (Grafana, Alertmanager, ELK stack hosting) - users deploy their own
- Custom dashboard widget framework - plugin system deferred to post-alpha
- Machine learning-based anomaly detection - simple threshold-based alerts sufficient for MVP
- Multi-tenant observability isolation - single-tenant deployment assumed for alpha
- Distributed tracing with OpenTelemetry - correlation IDs sufficient for MVP, full tracing deferred
- Real-user monitoring (RUM) for web dashboard - focus on backend observability for alpha
- Cost tracking and billing integration - telemetry collected but billing deferred
- A/B testing framework for autonomous loop variations - future optimization feature
- Mobile app for dashboard (iOS/Android) - responsive web sufficient for alpha
- Advanced analytics (cohort analysis, retention curves) - basic metrics and trends only for MVP

## System Architecture Alignment

Epic 5 implements the `packages/observability` and `packages/dashboard` packages as defined in Architecture section 4.3 (Project Structure), establishing the production-readiness layer with structured logging via **Pino 9.6+** (5x faster than Winston with zero-copy JSON serialization), Prometheus metrics collection via prom-client, and real-time web dashboards using **React 19** with Tailwind CSS v4 (section 2.1 Technology Stack). The observability package provides a unified logger interface that wraps Pino configuration, ensuring consistent log formatting across all packages while supporting multiple transports (stdout for containers, file for local dev, aggregation service connectors for Datadog/ELK).

The dashboard package delivers a **Server-Sent Events (SSE)** architecture for <500ms real-time updates (Architecture section 2.3 Real-Time Observability), avoiding WebSocket overhead while maintaining simple HTTP-based connections. The Fastify 5.2+ backend (section 2.1) exposes `/api/v1/stream` for SSE and `/metrics` for Prometheus scraping (OpenMetrics format). Dashboard routes (`/dashboard`, `/dashboard/velocity`, `/dashboard/events`, `/dashboard/feedback`) leverage React Server Components for initial page load performance and client-side React 19 for interactive charts using **Recharts** (lightweight, composable charting library with tree-shaking support).

Epic 5 integrates deeply with Epic 4's event sourcing foundation: Story 5.5 (Event Trail Exploration UI) consumes Epic 4's Event Query API (`/api/v1/events`), enabling developers to filter, search, and explore events through a web interface rather than curl commands. Story 5.7 (Feedback Collection) stores user ratings in the same PostgreSQL database used for Epic 4's event store, ensuring feedback correlates with specific development cycles via correlation IDs. The integration testing suite (Story 5.8) validates the complete system stack from Epic 1 (AI providers) through Epic 4 (event capture) to Epic 5 (metrics collection), ensuring end-to-end observability.

The alert system (Story 5.6) leverages Epic 4's event stream as the data source: escalation events (EscalationTriggeredEvent) automatically trigger critical alerts, event store write failures detected via health checks generate alerts, and custom alert rules query the event store for anomaly patterns (e.g., "more than 5 EscalationTriggeredEvents in 10 minutes"). Alert delivery uses webhook POSTs (configurable URL), CLI output (if running interactively), and email via configured SMTP (optional). Rate limiting (5 alerts/min) prevents alert storms during cascading failures.

The documentation system (Stories 5.9a-5.9d) generates API reference documentation (5.9c) from OpenAPI/Swagger specs defined in Fastify route handlers (decorators for response schemas), ensuring docs stay synchronized with implementation. C4 architecture diagrams included in the full documentation website (5.9d) visualize the Epic 1-5 layered architecture, with Epic 5 positioned as the horizontal observability plane intersecting all layers. Installation (5.9a) and usage guides (5.9b) provide step-by-step instructions for all deployment modes. The optional video walkthrough (5.9e) demonstrates the complete autonomous loop (issue → PR merge) with side-by-side dashboard views showing logs, metrics, and event trail in real-time.

Alpha release artifacts (Story 5.10) use Docker multi-stage builds for minimal image sizes (~150MB compressed), esbuild for bundling with tree-shaking to eliminate unused code, and GitHub Actions for CI/CD pipeline automation (matrix builds for amd64/arm64). Telemetry consent follows GDPR best practices: opt-in via CLI flag (`--telemetry-opt-in`), clear consent language describing collected data (metrics only, no code/credentials), and local-only mode for privacy-sensitive deployments (all telemetry disabled).

## Detailed Design

### Services and Modules

**1. Logger Service (`packages/observability/src/logger.ts`)**

*Responsibilities:*
- Provide unified logging interface wrapping Pino configuration
- Support multiple log levels (DEBUG, INFO, WARN, ERROR)
- Automatic correlation ID injection from context
- Sensitive data redaction (API keys, tokens, passwords)
- Multiple transport configuration (stdout, file, aggregation service)

*Interface:*
```typescript
interface LoggerInterface {
  debug(message: string, context?: Record<string, unknown>): void;
  info(message: string, context?: Record<string, unknown>): void;
  warn(message: string, context?: Record<string, unknown>): void;
  error(message: string, error?: Error, context?: Record<string, unknown>): void;
  child(bindings: Record<string, unknown>): LoggerInterface;
}

interface LogContext {
  correlationId?: string;
  issueId?: string;
  prId?: string;
  actorId?: string;
  provider?: string;
  [key: string]: unknown;
}
```

*Configuration:*
```typescript
interface LoggerConfig {
  level: 'debug' | 'info' | 'warn' | 'error';
  transports: Array<{
    type: 'stdout' | 'file' | 'aggregation';
    config?: {
      filePath?: string;           // For file transport
      aggregationUrl?: string;     // For Datadog/ELK
      apiKey?: string;             // For aggregation service
    };
  }>;
  redaction: {
    paths: string[];               // JSON paths to redact (e.g., ['req.headers.authorization'])
    censor: string;                // Replacement text (default: '***REDACTED***')
  };
}
```

*Owner:* Epic 5 (Story 5.1)

**2. Metrics Service (`packages/observability/src/metrics.ts`)**

*Responsibilities:*
- Register Prometheus metrics (counters, gauges, histograms)
- Increment/decrement/observe metric values
- Expose /metrics endpoint in OpenMetrics format
- Support metric labels (provider, platform, outcome)

*Interface:*
```typescript
interface MetricsInterface {
  registerCounter(name: string, help: string, labels?: string[]): Counter;
  registerGauge(name: string, help: string, labels?: string[]): Gauge;
  registerHistogram(name: string, help: string, buckets?: number[], labels?: string[]): Histogram;
  getMetricsHandler(): FastifyHandler; // Returns handler for /metrics endpoint
}

// Example metrics
const issuesProcessedTotal = metrics.registerCounter(
  'tamma_issues_processed_total',
  'Total number of issues processed',
  ['provider', 'outcome']
);

const activeLoopsGauge = metrics.registerGauge(
  'tamma_active_autonomous_loops',
  'Number of currently running autonomous loops'
);

const issueCompletionDuration = metrics.registerHistogram(
  'tamma_issue_completion_duration_seconds',
  'Time from issue selection to PR merge',
  [30, 60, 120, 300, 600, 1800, 3600, 7200] // Buckets in seconds
);
```

*Owner:* Epic 5 (Story 5.2)

**3. Dashboard Backend (`packages/dashboard/src/backend/server.ts`)**

*Responsibilities:*
- Fastify server exposing dashboard routes and API endpoints
- Server-Sent Events (SSE) endpoint for real-time updates
- Authentication middleware for dashboard access
- Metrics endpoint integration
- Static file serving for React frontend

*API Endpoints:*
```typescript
// GET /dashboard - System Health UI (HTML)
// GET /dashboard/velocity - Development Velocity UI (HTML)
// GET /dashboard/events - Event Trail Exploration UI (HTML)
// GET /dashboard/feedback - Feedback Dashboard UI (HTML)

// GET /api/v1/stream - SSE endpoint for real-time updates
interface SSEMessage {
  type: 'health' | 'velocity' | 'event' | 'feedback';
  data: unknown;
  timestamp: string;
}

// GET /api/v1/dashboard/health - Current system health snapshot
interface HealthSnapshot {
  activeLoops: number;
  pendingApprovals: number;
  recentEscalations: Array<{
    timestamp: string;
    reason: string;
    correlationId: string;
  }>;
  currentIssue?: {
    issueId: number;
    title: string;
    step: string;
    estimatedTimeRemaining: number;
  };
  status: 'healthy' | 'degraded' | 'critical';
}

// GET /api/v1/dashboard/velocity?days=30
interface VelocityData {
  issuesPerDay: Array<{ date: string; count: number }>;
  avgTimeToMerge: Array<{ date: string; seconds: number }>;
  prSuccessRate: Array<{ date: string; rate: number }>;
  metrics: {
    throughput: number;      // issues/week
    cycleTime: number;       // seconds
    quality: number;         // test pass rate (0-1)
  };
}

// GET /metrics - Prometheus OpenMetrics format
```

*Owner:* Epic 5 (Stories 5.3, 5.4, 5.5)

**4. Dashboard Frontend (`packages/dashboard/src/frontend/`)**

*Responsibilities:*
- React 19 components for dashboard pages
- Recharts integration for line/bar charts
- SSE client for real-time updates
- Responsive layout with Tailwind CSS v4

*Component Structure:*
```typescript
// SystemHealthDashboard.tsx
export const SystemHealthDashboard: React.FC = () => {
  const health = useSSE<HealthSnapshot>('/api/v1/stream', 'health');

  return (
    <div className="grid grid-cols-2 gap-4">
      <MetricCard title="Active Loops" value={health.activeLoops} />
      <MetricCard title="Pending Approvals" value={health.pendingApprovals} />
      <StatusIndicator status={health.status} />
      <EscalationsList escalations={health.recentEscalations} />
      <CurrentIssueProgress issue={health.currentIssue} />
    </div>
  );
};

// VelocityDashboard.tsx
export const VelocityDashboard: React.FC = () => {
  const [dateRange, setDateRange] = useState(30);
  const velocity = useFetch<VelocityData>(`/api/v1/dashboard/velocity?days=${dateRange}`);

  return (
    <div>
      <DateRangeFilter value={dateRange} onChange={setDateRange} />
      <LineChart data={velocity.issuesPerDay} title="Issues Completed Per Day" />
      <LineChart data={velocity.avgTimeToMerge} title="Average Time to Merge" />
      <BarChart data={velocity.prSuccessRate} title="PR Success Rate" />
      <MetricsCard metrics={velocity.metrics} />
      <ExportButton format="png" />
    </div>
  );
};

// EventTrailExplorer.tsx
export const EventTrailExplorer: React.FC = () => {
  const [filters, setFilters] = useState<EventFilters>({});
  const [searchQuery, setSearchQuery] = useState('');
  const events = useFetch<EventPage>('/api/v1/events', { params: filters });

  return (
    <div>
      <FilterBar filters={filters} onChange={setFilters} />
      <SearchBar query={searchQuery} onChange={setSearchQuery} />
      <EventList events={events.events} onExpand={expandEvent} />
      <InfiniteScroll onLoadMore={loadMoreEvents} />
    </div>
  );
};
```

*Owner:* Epic 5 (Stories 5.3, 5.4, 5.5)

**5. Alert Manager (`packages/observability/src/alerts.ts`)**

*Responsibilities:*
- Monitor event stream for alert triggers
- Apply rate limiting (5 alerts/min)
- Deliver alerts to configured channels (webhook, CLI, email)
- Store alert history in database

*Interface:*
```typescript
interface AlertManagerInterface {
  registerTrigger(trigger: AlertTrigger): void;
  sendAlert(alert: Alert): Promise<void>;
  getAlertHistory(since?: Date): Promise<Alert[]>;
}

interface AlertTrigger {
  id: string;
  name: string;
  condition: (context: AlertContext) => boolean;
  severity: 'critical' | 'warning' | 'info';
}

interface Alert {
  id: string;
  severity: 'critical' | 'warning' | 'info';
  title: string;
  description: string;
  correlationId?: string;
  timestamp: string;
  suggestedAction?: string;
  metadata?: Record<string, unknown>;
}

interface AlertContext {
  eventStore: EventStoreInterface;
  metrics: MetricsInterface;
  timeWindow: number; // seconds
}

// Example triggers
const escalationTrigger: AlertTrigger = {
  id: 'escalation-triggered',
  name: 'Escalation After 3 Retries',
  severity: 'critical',
  condition: (ctx) => {
    // Triggered when EscalationTriggeredEvent emitted
    return true;
  },
};

const eventStoreFailureTrigger: AlertTrigger = {
  id: 'event-store-failure',
  name: 'Event Store Write Failure',
  severity: 'critical',
  condition: (ctx) => {
    const failures = ctx.metrics.getCounter('event_store_write_failures_total');
    return failures.value > 0;
  },
};
```

*Owner:* Epic 5 (Story 5.6)

**6. Feedback Service (`packages/observability/src/feedback.ts`)**

*Responsibilities:*
- Prompt user for feedback after PR merge
- Store feedback in database with correlation ID
- Analyze feedback for trends (keyword extraction)
- Expose feedback query API

*Interface:*
```typescript
interface FeedbackServiceInterface {
  promptFeedback(correlationId: string): Promise<void>;
  storeFeedback(feedback: Feedback): Promise<void>;
  getFeedback(query: FeedbackQuery): Promise<FeedbackPage>;
  analyzeTrends(since: Date): Promise<FeedbackAnalysis>;
}

interface Feedback {
  id: string;
  timestamp: string;
  correlationId: string;
  rating: 'positive' | 'negative';
  comment?: string;
  issueId: number;
  prId: number;
}

interface FeedbackAnalysis {
  satisfactionRate: number; // 0-1
  totalResponses: number;
  commonNegativeThemes: Array<{
    theme: string;
    count: number;
  }>;
  trendOverTime: Array<{
    date: string;
    satisfactionRate: number;
  }>;
}
```

*Owner:* Epic 5 (Story 5.7)

**7. Integration Test Runner (`tests/integration/runner.ts`)**

*Responsibilities:*
- Execute E2E test scenarios with mock AI provider and Git API
- Validate event sequences against expected patterns
- Measure test coverage and generate reports
- Run in CI/CD pipeline with <5min timeout

*Test Scenarios:*
```typescript
// Scenario 1: Happy Path
describe('Happy Path - Issue to PR Merge', () => {
  it('should complete full autonomous loop', async () => {
    const result = await runScenario('happy-path', {
      issueId: 123,
      mockAIProvider: 'claude-code',
      mockGitPlatform: 'github',
    });

    expect(result.status).toBe('success');
    expect(result.prMerged).toBe(true);
    expect(result.eventSequence).toMatchSnapshot();
  });
});

// Scenario 2: Build Failure with Retry
describe('Build Failure with Retry', () => {
  it('should retry build 3 times then succeed', async () => {
    const result = await runScenario('build-failure-retry', {
      failureCount: 2, // Fail twice, succeed on 3rd attempt
    });

    expect(result.buildAttempts).toBe(3);
    expect(result.status).toBe('success');
    expect(result.events).toContainEqual(
      expect.objectContaining({ type: 'BUILD.TRIGGERED', attemptNumber: 3 })
    );
  });
});

// Scenario 3: Test Failure with Escalation
describe('Test Failure with Escalation', () => {
  it('should escalate after 3 test failures', async () => {
    const result = await runScenario('test-failure-escalation', {
      failureType: 'test',
      failureCount: 3,
    });

    expect(result.status).toBe('escalated');
    expect(result.events).toContainEqual(
      expect.objectContaining({ type: 'ESCALATION.TRIGGERED', reason: 'retry-exhausted' })
    );
  });
});
```

*Owner:* Epic 5 (Story 5.8)

### Data Models and Contracts

**PostgreSQL Database Schema (Stories 5.6, 5.7):**

```sql
-- Feedback table (Story 5.7)
CREATE TABLE feedback (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  timestamp TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  correlation_id VARCHAR(255) NOT NULL,
  issue_id INTEGER NOT NULL,
  pr_id INTEGER NOT NULL,
  rating VARCHAR(20) NOT NULL CHECK (rating IN ('positive', 'negative')),
  comment TEXT,

  -- Indexes for querying
  created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX idx_feedback_correlation ON feedback(correlation_id);
CREATE INDEX idx_feedback_timestamp ON feedback(timestamp DESC);
CREATE INDEX idx_feedback_rating ON feedback(rating);
CREATE INDEX idx_feedback_issue ON feedback(issue_id);

-- Alerts table (Story 5.6)
CREATE TABLE alerts (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  severity VARCHAR(20) NOT NULL CHECK (severity IN ('critical', 'warning', 'info')),
  title VARCHAR(255) NOT NULL,
  description TEXT NOT NULL,
  correlation_id VARCHAR(255),
  timestamp TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  suggested_action TEXT,
  metadata JSONB DEFAULT '{}',

  -- Alert delivery tracking
  channels_delivered JSONB DEFAULT '[]',  -- ['webhook', 'cli', 'email']
  delivery_status VARCHAR(20) DEFAULT 'pending', -- pending | delivered | failed

  created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX idx_alerts_timestamp ON alerts(timestamp DESC);
CREATE INDEX idx_alerts_severity ON alerts(severity);
CREATE INDEX idx_alerts_correlation ON alerts(correlation_id);
CREATE INDEX idx_alerts_delivery ON alerts(delivery_status);
```

**Log Message Format (Story 5.1):**

```typescript
// JSON log structure (Pino format)
interface LogMessage {
  level: 10 | 20 | 30 | 40 | 50 | 60; // TRACE | DEBUG | INFO | WARN | ERROR | FATAL
  time: number;                         // Unix timestamp milliseconds
  pid: number;                          // Process ID
  hostname: string;                     // Machine hostname
  msg: string;                          // Log message

  // Context fields
  correlationId?: string;
  issueId?: string;
  prId?: string;
  actorId?: string;
  provider?: string;
  platform?: string;

  // Error details (for ERROR level)
  err?: {
    type: string;
    message: string;
    stack: string;
  };

  // Custom context
  [key: string]: unknown;
}

// Example log messages
{
  "level": 30,
  "time": 1698432000123,
  "pid": 12345,
  "hostname": "tamma-worker-1",
  "msg": "Issue selected for processing",
  "correlationId": "corr-20251028101500-123-a1b2c3",
  "issueId": "123",
  "title": "Add user authentication",
  "labels": ["enhancement", "high-priority"]
}

{
  "level": 50,
  "time": 1698432001456,
  "pid": 12345,
  "hostname": "tamma-worker-1",
  "msg": "Build failed after 3 retries",
  "correlationId": "corr-20251028101500-123-a1b2c3",
  "issueId": "123",
  "prId": "456",
  "err": {
    "type": "BuildFailureError",
    "message": "TypeScript compilation errors",
    "stack": "BuildFailureError: TypeScript compilation errors\n    at BuildOrchestrator..."
  },
  "retryCount": 3,
  "nextAction": "escalation"
}
```

**Prometheus Metrics Definitions (Story 5.2):**

```typescript
// Counter metrics
const COUNTER_METRICS = [
  {
    name: 'tamma_issues_processed_total',
    help: 'Total number of issues processed',
    labels: ['provider', 'outcome'], // outcome: success | failure | escalated
  },
  {
    name: 'tamma_prs_created_total',
    help: 'Total number of PRs created',
    labels: ['platform'], // platform: github | gitlab | gitea
  },
  {
    name: 'tamma_prs_merged_total',
    help: 'Total number of PRs successfully merged',
    labels: ['platform'],
  },
  {
    name: 'tamma_escalations_total',
    help: 'Total number of escalations triggered',
    labels: ['reason'], // reason: retry-exhausted | structural-issue | security-critical
  },
  {
    name: 'tamma_ai_requests_total',
    help: 'Total number of AI provider requests',
    labels: ['provider', 'model'], // provider: claude-code | openai-codex
  },
  {
    name: 'tamma_events_written_total',
    help: 'Total number of events written to event store',
    labels: ['type', 'backend'], // type: ISSUE.SELECTED | AI.REQUEST.STARTED, backend: postgresql | file
  },
];

// Gauge metrics
const GAUGE_METRICS = [
  {
    name: 'tamma_active_autonomous_loops',
    help: 'Number of currently running autonomous loops',
  },
  {
    name: 'tamma_pending_approvals',
    help: 'Number of approvals waiting for user response',
  },
  {
    name: 'tamma_queue_depth',
    help: 'Number of issues in the processing queue',
  },
  {
    name: 'tamma_event_store_connection_pool_active',
    help: 'Active event store database connections',
  },
];

// Histogram metrics
const HISTOGRAM_METRICS = [
  {
    name: 'tamma_issue_completion_duration_seconds',
    help: 'Time from issue selection to PR merge',
    buckets: [30, 60, 120, 300, 600, 1800, 3600, 7200, 14400], // 30s to 4h
  },
  {
    name: 'tamma_ai_request_duration_seconds',
    help: 'Time for AI provider to respond',
    buckets: [0.5, 1, 2, 5, 10, 20, 30, 60], // 0.5s to 1min
  },
  {
    name: 'tamma_test_execution_duration_seconds',
    help: 'Time to execute test suite',
    buckets: [5, 10, 30, 60, 120, 300, 600], // 5s to 10min
  },
  {
    name: 'tamma_build_duration_seconds',
    help: 'Time to complete build',
    buckets: [10, 30, 60, 120, 300, 600], // 10s to 10min
  },
];
```

**Server-Sent Events (SSE) Message Format (Stories 5.3, 5.4):**

```typescript
// SSE message structure
interface SSEMessage {
  id: string;                    // Unique message ID for reconnection
  event: string;                 // Event type: 'health' | 'velocity' | 'event' | 'feedback'
  data: string;                  // JSON-serialized payload
  retry?: number;                // Reconnection retry interval (ms)
}

// Example SSE messages
// Event type: health
event: health
id: 1698432000123
data: {"activeLoops":2,"pendingApprovals":1,"recentEscalations":[{"timestamp":"2025-10-28T10:15:00Z","reason":"retry-exhausted","correlationId":"corr-abc"}],"currentIssue":{"issueId":123,"title":"Add auth","step":"code-generation","estimatedTimeRemaining":120},"status":"healthy"}

// Event type: velocity
event: velocity
id: 1698432001456
data: {"issuesPerDay":[{"date":"2025-10-27","count":5},{"date":"2025-10-28","count":3}],"avgTimeToMerge":[{"date":"2025-10-27","seconds":3600},{"date":"2025-10-28","seconds":2400}],"prSuccessRate":[{"date":"2025-10-27","rate":0.8},{"date":"2025-10-28","rate":0.9}],"metrics":{"throughput":35,"cycleTime":2700,"quality":0.85}}

// Event type: event (new event captured)
event: event
id: 1698432002789
data: {"id":"01933d52-1a89-7000-8000-000000000001","type":"ISSUE.SELECTED","timestamp":"2025-10-28T10:15:00.123Z","actor":{"type":"system","id":"workflow-orchestrator"},"tags":{"issueId":"123","correlationId":"corr-abc"},"payload":{"issueId":123,"title":"Add user authentication"}}
```

**Dashboard API Response Formats (Stories 5.3, 5.4, 5.5):**

```typescript
// GET /api/v1/dashboard/health response
interface HealthSnapshotResponse {
  activeLoops: number;
  pendingApprovals: number;
  recentEscalations: Array<{
    id: string;
    timestamp: string;
    reason: string;
    correlationId: string;
    title: string;
  }>;
  currentIssue: {
    issueId: number;
    title: string;
    step: string;
    estimatedTimeRemaining: number; // seconds
    progress: number;                // 0-100 percentage
  } | null;
  status: 'healthy' | 'degraded' | 'critical';
  systemMetrics: {
    cpuUsage: number;       // 0-100 percentage
    memoryUsage: number;    // 0-100 percentage
    diskUsage: number;      // 0-100 percentage
  };
}

// GET /api/v1/dashboard/velocity?days=30 response
interface VelocityDataResponse {
  issuesPerDay: Array<{ date: string; count: number }>;
  avgTimeToMerge: Array<{ date: string; seconds: number }>;
  prSuccessRate: Array<{ date: string; rate: number }>; // rate: 0-1
  metrics: {
    throughput: number;      // issues/week
    cycleTime: number;       // average seconds from issue to merge
    quality: number;         // test pass rate (0-1)
  };
  topProviders: Array<{
    provider: string;
    issueCount: number;
    avgDuration: number;
  }>;
  topIssueLabels: Array<{
    label: string;
    count: number;
  }>;
}

// GET /api/v1/dashboard/feedback?days=30 response
interface FeedbackDashboardResponse {
  satisfactionRate: number;        // 0-1
  totalResponses: number;
  positiveCount: number;
  negativeCount: number;
  trendOverTime: Array<{
    date: string;
    satisfactionRate: number;
    responseCount: number;
  }>;
  commonNegativeThemes: Array<{
    theme: string;
    count: number;
    examples: string[];      // Sample comments
  }>;
  recentFeedback: Array<{
    id: string;
    timestamp: string;
    rating: 'positive' | 'negative';
    comment: string | null;
    issueId: number;
    prId: number;
  }>;
}
```

**Alert Webhook Payload (Story 5.6):**

```typescript
interface AlertWebhookPayload {
  alert: {
    id: string;
    severity: 'critical' | 'warning' | 'info';
    title: string;
    description: string;
    correlationId: string | null;
    timestamp: string;              // ISO 8601
    suggestedAction: string | null;
    metadata: Record<string, unknown>;
  };
  system: {
    hostname: string;
    environment: 'development' | 'staging' | 'production';
    version: string;
  };
}

// Example webhook POST body
{
  "alert": {
    "id": "alert-uuid-12345",
    "severity": "critical",
    "title": "Escalation After 3 Retries",
    "description": "Build failed 3 times for issue #123. Autonomous loop halted and requires human intervention.",
    "correlationId": "corr-20251028101500-123-a1b2c3",
    "timestamp": "2025-10-28T10:20:00.000Z",
    "suggestedAction": "Review build logs at /dashboard/events?correlationId=corr-20251028101500-123-a1b2c3",
    "metadata": {
      "issueId": 123,
      "prId": 456,
      "retryCount": 3,
      "lastError": "TypeScript compilation errors"
    }
  },
  "system": {
    "hostname": "tamma-orchestrator-1",
    "environment": "production",
    "version": "0.1.0-alpha"
  }
}
```

### APIs and Interfaces

**Logger API (Story 5.1):**
- `logger.info(message, context)` - Log informational messages with context
- `logger.error(message, error, context)` - Log errors with stack traces
- `logger.child(bindings)` - Create child logger with bound context (e.g., correlationId)
- Configuration via `LoggerConfig` (level, transports, redaction paths)

**Metrics API (Story 5.2):**
- `metrics.registerCounter(name, help, labels)` - Register counter metric
- `metrics.registerGauge(name, help, labels)` - Register gauge metric
- `metrics.registerHistogram(name, help, buckets, labels)` - Register histogram metric
- `GET /metrics` - Prometheus scrape endpoint (OpenMetrics format)

**Dashboard REST API (Stories 5.3-5.5):**
- `GET /api/v1/stream` - SSE endpoint for real-time updates
- `GET /api/v1/dashboard/health` - System health snapshot
- `GET /api/v1/dashboard/velocity?days=N` - Velocity data with date range filter
- `GET /api/v1/dashboard/feedback?days=N` - Feedback dashboard data
- All endpoints require JWT authentication (Epic 1 auth system)

**Alert API (Story 5.6):**
- `alertManager.registerTrigger(trigger)` - Register custom alert trigger
- `alertManager.sendAlert(alert)` - Send alert to configured channels
- `alertManager.getAlertHistory(since)` - Query alert history
- Webhook POST to configured URL with `AlertWebhookPayload`

**Feedback API (Story 5.7):**
- `feedbackService.promptFeedback(correlationId)` - Display CLI prompt after PR merge
- `feedbackService.storeFeedback(feedback)` - Store user feedback in database
- `feedbackService.analyzeTrends(since)` - Generate satisfaction trends and keyword themes
- `POST /api/v1/feedback` - REST endpoint for programmatic feedback submission

**Integration Test API (Story 5.8):**
- `runScenario(name, config)` - Execute named test scenario with mock config
- `validateEventSequence(events, expected)` - Assert event sequence matches expected pattern
- `generateCoverageReport()` - Generate Jest coverage report with >80% target

### Workflows and Sequencing

**Sequence 1: Structured Logging Initialization (Story 5.1)**
1. Load logger config from `~/.tamma/config.yaml` (level, transports, redaction)
2. Initialize Pino with base configuration (zero-copy JSON serialization)
3. Register global logger instance in DI container
4. All packages import logger via `import { logger } from '@tamma/observability'`
5. Logger automatically injects correlationId from async context when available

**Sequence 2: Metrics Collection and Scraping (Story 5.2)**
1. Initialize prom-client registry on app startup
2. Register all counter/gauge/histogram metrics with labels
3. Instrument Epic 2 workflow orchestrator to increment/observe metrics
4. Expose `/metrics` endpoint via Fastify route
5. Prometheus scrapes endpoint every 15s, stores time series data
6. Grafana queries Prometheus for dashboard visualization (user-managed)

**Sequence 3: Real-Time Dashboard Updates (Stories 5.3-5.4)**
1. User opens `/dashboard` in browser → React app loads
2. React app establishes SSE connection to `/api/v1/stream`
3. Backend emits `health` event every 10s with current system snapshot
4. React useSSE hook receives event, updates component state
5. Dashboard re-renders with new data (<500ms latency)
6. On connection loss, SSE auto-reconnects using last event ID

**Sequence 4: Event Trail Exploration (Story 5.5)**
1. User opens `/dashboard/events` → Event Trail Explorer UI loads
2. User applies filters (date range, event type, correlation ID)
3. Frontend calls `GET /api/v1/events` with filter params
4. Backend queries Epic 4 event store, returns paginated results
5. User clicks event row → Expand to show full JSON payload
6. User clicks "Follow correlation ID" → Filter by that correlation, show all related events
7. Infinite scroll loads next page when user scrolls to bottom

**Sequence 5: Alert Triggering and Delivery (Story 5.6)**
1. Epic 3 quality gate exhausts 3 retries → Emits `EscalationTriggeredEvent`
2. Alert Manager listens to event stream, detects escalation event
3. Alert Manager checks rate limit (5 alerts/min) → Allows alert
4. Alert Manager creates alert record in database
5. Alert Manager delivers alert in parallel: Webhook POST + CLI output + Email (if configured)
6. Webhook receiver (e.g., Slack, PagerDuty) processes alert and notifies team

**Sequence 6: Feedback Collection (Story 5.7)**
1. Epic 2 workflow completes PR merge → Emits `PRMergedEvent`
2. Feedback Service listens for PR merge events
3. Feedback Service prompts user in CLI: "Rate this cycle: 👍 👎"
4. User selects 👎 → Prompt follow-up: "What went wrong? [text]"
5. Feedback stored in database with correlation ID, rating, comment
6. Dashboard `/dashboard/feedback` displays satisfaction rate over time

**Sequence 7: Integration Test Execution (Story 5.8)**
1. Developer commits code → GitHub Actions triggers CI pipeline
2. CI runs `npm run test:integration` → Jest executes test scenarios
3. Test scenario spins up mock AI provider and mock Git API
4. Test runs full autonomous loop (issue → plan → code → PR → merge)
5. Test validates event sequence matches expected pattern
6. Test generates coverage report → Fails if <80% coverage
7. CI reports test results back to PR (pass/fail status check)

**Sequence 8: Alpha Release Build (Story 5.10)**
1. Release manager creates Git tag `v0.1.0-alpha`
2. GitHub Actions triggered by tag push → Multi-platform build
3. Docker multi-stage build creates images for amd64 + arm64
4. esbuild bundles frontend with tree-shaking → Minified assets
5. Binary builds for Windows/macOS/Linux using pkg or ncc
6. GitHub Actions creates release with artifacts and release notes
7. Release marked as prerelease with alpha warning

## Non-Functional Requirements

### Performance

*Source: PRD Epic 5 requirements (observability.md sections), Architecture observability patterns*

1. **Dashboard Load Time**: Initial page load <2s on typical broadband (50 Mbps), <500ms for subsequent navigation (React Server Components caching)
2. **Real-Time Update Latency**: SSE updates delivered within 500ms of metric change; dashboard reflects new events within 1s of event store write
3. **Metrics Collection Overhead**:
   - Pino logging <5% CPU overhead during normal operation (5x faster than Winston)
   - Prometheus metrics collection <2% CPU overhead
   - Combined observability stack <7% total overhead
4. **Log Write Performance**: Minimum 10,000 log entries/second sustained throughput; log buffer flush <100ms during bursts
5. **Metrics Export**: `/metrics` endpoint response time <50ms (p95), <100ms (p99) even with 100+ metrics registered
6. **Integration Test Execution**: Full integration test suite completes in <5 minutes on GitHub Actions runners; critical path tests <2 minutes
7. **Event Trail Query**: Event store queries for dashboard event trail return <1s for 1000 events, <3s for 10,000 events (leverages Epic 4 event store indexes)
8. **Alert Delivery**: Alert webhook POST completes within 5s timeout; CLI alert display <100ms; email queued within 2s
9. **Docker Build Time**: Multi-arch Docker builds complete in <10 minutes on GitHub Actions (leveraging layer caching)
10. **Bundle Size**: Frontend bundle <500KB gzipped; backend binary <50MB (esbuild tree-shaking and compression)

### Security

*Source: Architecture security section (auth.md), Epic 1 tech spec (provider credentials), GDPR compliance requirements*

1. **Dashboard Authentication**:
   - JWT-based authentication reusing Epic 1's auth infrastructure
   - Session tokens expire after 24 hours; refresh tokens valid for 7 days
   - Dashboard accessible only on localhost by default (127.0.0.1:3000); production deployment requires explicit configuration
2. **Credential Protection**:
   - All logs automatically redact secrets using patterns: API keys, tokens, passwords, webhook URLs with auth parameters
   - Redaction patterns: `/(api[_-]?key|token|password|secret)[:=]\s*[\w-]+/gi` replaced with `***REDACTED***`
   - Metrics labels NEVER include sensitive data (issue titles, PR descriptions)
3. **Telemetry Consent**:
   - Opt-in telemetry collection via `--telemetry` CLI flag or `telemetry: true` in config
   - Default: telemetry disabled
   - User explicitly prompted during `tamma init` setup wizard with GDPR-compliant consent language
   - Telemetry scope: usage metrics only (issue counts, PR counts, error rates); NO code content, NO issue/PR titles
4. **GDPR Compliance**:
   - Feedback comments stored with explicit user consent (checkbox in feedback UI)
   - Right to erasure: `tamma feedback delete --correlation-id <id>` command
   - Data retention: Feedback records deleted after 90 days (configurable via `feedback.retention_days`)
   - Privacy policy link displayed in dashboard footer
5. **Alert Webhook Security**:
   - Webhook URLs validated to prevent SSRF (no localhost, no private IP ranges unless explicitly allowed)
   - HTTPS required for production webhook URLs (development allows HTTP with warning)
   - Webhook payload signed with HMAC-SHA256 using shared secret (header: `X-Tamma-Signature`)
6. **Dependency Security**:
   - All NPM dependencies scanned with `npm audit` in CI/CD pipeline
   - Renovate bot configured for automated dependency updates
   - Critical vulnerabilities block PR merge (GitHub Actions check)
7. **Docker Image Security**:
   - Base image: `node:22-alpine` (minimal attack surface)
   - Multi-stage builds to exclude dev dependencies from final image
   - Images scanned with Trivy for CVEs before release
   - Non-root user execution in container (`USER node`)

### Reliability/Availability

*Source: Architecture reliability patterns, Epic 4 event store consistency guarantees, PRD operational requirements*

1. **Dashboard Uptime**: Target 99.5% uptime in standalone mode (allows for planned maintenance); graceful degradation when backend unavailable (static content served via React fallback)
2. **Alert Delivery Guarantees**:
   - At-least-once delivery for webhook alerts (3 retries with exponential backoff: 1s, 2s, 4s)
   - Persistent alert queue in PostgreSQL survives process restarts
   - Failed webhooks logged with full context for manual retry
   - CLI alerts MUST always succeed (fallback to stderr if stdout blocked)
3. **Graceful Degradation**:
   - If event store (Epic 4) unavailable: Dashboard displays cached metrics + warning banner; alert system continues from in-memory buffer (flushes when store recovers)
   - If Prometheus unavailable: Metrics collected in-memory with 1-hour retention; warning logged every 5 minutes
   - If PostgreSQL unavailable: Feedback submissions buffered to disk (1000 entries max); alerts buffered to disk; integration tests skipped with clear message
4. **Recovery Behavior**:
   - Automatic reconnection to PostgreSQL with exponential backoff (max 5 retries over 60s)
   - SSE connections automatically reconnect on disconnect (client-side retry with jitter)
   - Dashboard backend restarts within 10s using process manager (PM2 or systemd)
   - Zero data loss for alerts and feedback during graceful shutdown (flush buffers before exit)
5. **Integration Test Stability**:
   - Flaky test threshold: <2% flake rate over 100 runs (measured in CI)
   - Automatic retry for network-dependent tests (max 3 retries)
   - Test isolation: Each test suite uses separate mock AI provider instances to prevent cross-test interference
6. **Event Store Dependency**:
   - Epic 5 tolerates Epic 4 event store read latency up to 5s before timeout (configurable)
   - Event trail UI displays "Loading..." state during slow queries; cancellable queries on navigation
7. **Rate Limiting**:
   - Alert system: 5 alerts/minute per trigger type (prevents alert storms)
   - Feedback submissions: 10 submissions/minute per user (prevents spam)
   - Dashboard SSE: Max 100 concurrent connections (prevents resource exhaustion)
8. **Health Checks**:
   - Dashboard backend: `GET /health` returns 200 if database reachable + event store responsive (used by Docker healthcheck)
   - Orchestrator mode: Worker health checked every 30s; unresponsive workers restarted after 3 failed checks

### Observability

*Source: Architecture observability patterns, Epic 5 self-instrumentation requirements, OpenTelemetry standards*

1. **Structured Logging Coverage**:
   - ALL packages emit structured JSON logs via Pino (100% coverage target)
   - Log levels enforced: DEBUG (development only), INFO (normal operations), WARN (degraded state), ERROR (failures requiring attention)
   - Required fields in every log entry: `timestamp`, `level`, `correlationId`, `component`, `message`
   - Optional context fields: `issueId`, `prId`, `provider`, `platform`, `duration`, `error` (stack traces)
2. **Correlation IDs**:
   - Every autonomous loop execution assigned unique correlation ID (UUID v4)
   - Correlation ID propagated through all services: AI provider calls, Git platform API calls, event store writes, dashboard updates, alerts
   - Async context (AsyncLocalStorage) maintains correlation ID throughout request lifecycle
   - SSE messages include correlation ID for client-side log correlation
3. **Metrics Exposure**:
   - Prometheus `/metrics` endpoint exposed on port 9090 (configurable)
   - All metrics follow OpenMetrics standard naming: `tamma_<subsystem>_<metric>_<unit>`
   - Histogram buckets optimized for observed latencies: [0.01, 0.05, 0.1, 0.5, 1, 2, 5, 10, 30] seconds
   - Metric cardinality limit: <1000 unique label combinations per metric (prevents cardinality explosion)
4. **Health Checks**:
   - Liveness probe: `GET /health/live` returns 200 if process running (Docker uses for container health)
   - Readiness probe: `GET /health/ready` returns 200 if database connected + event store responsive + AI provider configured
   - Startup probe: `GET /health/startup` returns 200 once initialization complete (allows slow startup)
5. **Trace Sampling** (Future-Ready):
   - Structured logs include `traceId` field (currently matches correlationId)
   - Prepared for OpenTelemetry tracing integration in future epics (headers reserved: `traceparent`, `tracestate`)
   - Sampling rate: 100% for errors, 10% for successful operations (reduces overhead)
6. **Dashboard Self-Monitoring**:
   - Dashboard backend exposes own metrics: `tamma_dashboard_requests_total`, `tamma_dashboard_sse_connections`, `tamma_dashboard_query_duration_seconds`
   - Dashboard frontend logs errors to backend via `POST /api/v1/logs/frontend` (browser console errors captured)
   - Frontend performance metrics: Core Web Vitals (LCP, FID, CLS) tracked and displayed in admin panel
7. **Integration Test Observability**:
   - Test runner logs to `tests/integration/logs/test-run-<timestamp>.log` (retained for 30 days)
   - Test metrics collected: `tamma_tests_executed_total{status="passed|failed|skipped"}`, `tamma_test_duration_seconds{suite}`
   - Failed test screenshots captured in `tests/integration/screenshots/` (if applicable)
8. **Alert System Self-Monitoring**:
   - Alert delivery tracked: `tamma_alerts_delivered_total{channel="webhook|cli|email", status="success|failure"}`
   - Alert queue depth gauge: `tamma_alerts_queued` (triggers own alert if >100)
   - Webhook response times logged with full request/response context

## Dependencies and Integrations

*Source: Architecture technology stack, Epic 1-4 tech specs, NPM ecosystem*

### NPM Dependencies (New for Epic 5)

**Observability & Logging (Stories 5.1, 5.2):**
- `pino@9.6.0` - High-performance structured logging (5x faster than Winston)
- `pino-pretty@11.2.0` - Human-readable log formatting for development
- `pino-http@10.2.0` - Fastify integration for HTTP request logging
- `prom-client@15.1.3` - Prometheus metrics collection and exposition (OpenMetrics format)
- `express-prom-bundle@7.0.0` - Prometheus middleware for Express/Fastify

**Dashboard Backend (Story 5.3):**
- `fastify@5.2.0` - High-performance web framework (already in Epic 1 stack)
- `@fastify/jwt@9.0.0` - JWT authentication (reusing Epic 1 auth infrastructure)
- `@fastify/cors@10.0.0` - CORS middleware for dashboard API
- `@fastify/static@8.0.0` - Static file serving for React SPA
- `sse@1.1.0` OR `fastify-sse-v2@5.1.0` - Server-Sent Events support for real-time updates

**Dashboard Frontend (Story 5.4):**
- `react@19.0.0` - UI framework with React Server Components support
- `react-dom@19.0.0` - React DOM rendering
- `@tanstack/react-query@5.62.8` - Data fetching and caching (replaces Redux/Zustand)
- `recharts@2.15.0` - Chart library for metrics visualization (velocity, health dashboards)
- `tailwindcss@4.0.0` - Utility-first CSS framework
- `lucide-react@0.468.0` - Icon library (tree-shakeable, optimized)
- `vite@6.0.5` - Frontend build tool (faster than webpack)
- `@vitejs/plugin-react@4.3.4` - Vite React plugin with Fast Refresh

**Event Trail UI (Story 5.5):**
- `@tanstack/react-virtual@3.10.8` - Virtualized list rendering for large event trails (performance optimization)
- `date-fns@4.1.0` - Date formatting and manipulation (lighter than moment.js)

**Alert System (Story 5.6):**
- `nodemailer@6.9.16` - Email delivery for alert channel
- `axios@1.7.9` - HTTP client for webhook alert delivery (retry logic)
- `jsonwebtoken@9.0.2` - JWT signing for webhook HMAC (already in Epic 1)

**Integration Testing (Story 5.8):**
- `jest@29.7.0` - Test framework (already in stack)
- `@types/jest@29.5.14` - TypeScript types for Jest
- `ts-jest@29.2.5` - TypeScript preprocessor for Jest
- `supertest@7.0.0` - HTTP assertion library for API testing
- `nock@13.5.7` - HTTP mocking library for external API calls (GitHub, GitLab)
- `testcontainers@10.16.0` - Docker container management for integration tests (PostgreSQL)

**Alpha Release (Story 5.10):**
- `esbuild@0.24.2` - Fast bundler for production builds (tree-shaking)
- `pkg@5.8.1` OR `@vercel/ncc@0.38.3` - Binary packaging for standalone executables
- `husky@9.1.7` - Git hooks for pre-commit linting/testing
- `lint-staged@15.2.11` - Run linters on staged files only
- `@typescript-eslint/parser@8.18.1` - TypeScript ESLint parser
- `@typescript-eslint/eslint-plugin@8.18.1` - TypeScript ESLint rules
- `prettier@3.4.2` - Code formatter

### Epic 1-4 Component Dependencies

**Epic 1 (Foundation & Core Infrastructure):**
- `@tamma/providers` - AI provider abstraction (IAIProvider interface, ClaudeCodeProvider)
  - **Used by**: Integration tests to mock AI interactions
  - **Integration point**: Logger wraps provider API calls for observability
- `@tamma/platforms` - Git platform abstraction (IGitPlatform interface, GitHub/GitLab implementations)
  - **Used by**: Integration tests to verify end-to-end GitHub/GitLab workflows
  - **Integration point**: Metrics track PR creation/merge counts per platform
- `@tamma/config` - Configuration management
  - **Used by**: Logger config, metrics config, dashboard config, alert webhook URLs
  - **Integration point**: Centralized config validation and hot-reload
- `@tamma/auth` - Authentication infrastructure (JWT tokens, session management)
  - **Used by**: Dashboard authentication, webhook signature verification
  - **Integration point**: Reuses Epic 1 JWT middleware for dashboard routes

**Epic 2 (Autonomous Development Workflow):**
- `@tamma/orchestrator` - Workflow orchestration engine
  - **Used by**: Integration tests to execute full autonomous loop (issue → PR)
  - **Integration point**: Metrics track issue processing counts, PR creation counts, completion duration
- `@tamma/workflow` - Issue selection, plan generation, implementation modules
  - **Used by**: Alert system triggers on escalation events, feedback collected after PR merge
  - **Integration point**: Logger instruments all workflow steps with correlation IDs

**Epic 3 (Intelligence & Quality Enhancement):**
- `@tamma/quality` - Build automation, test execution, retry logic
  - **Used by**: Integration tests to verify quality gate behavior
  - **Integration point**: Alerts triggered on exhausted retries (3 failures), metrics track test pass rates
- `@tamma/research` - Research capability for unfamiliar concepts
  - **Used by**: Dashboard displays research queries in event trail
  - **Integration point**: Logger captures research context and results
- `@tamma/analysis` - Ambiguity detection, static analysis, security scanning
  - **Used by**: Dashboard displays analysis results, alerts on security findings
  - **Integration point**: Metrics track ambiguity scores, static analysis violations

**Epic 4 (Event Sourcing & Time-Travel):**
- `@tamma/events` - Event store backend (PostgreSQL event stream)
  - **Used by**: Event trail UI queries events, alert system listens to event stream
  - **Integration point**: ALL observability events written to event store (audit trail)
- `@tamma/replay` - Black-box replay for debugging
  - **Used by**: Integration tests replay failed scenarios
  - **Integration point**: Dashboard event trail links to replay functionality

### External System Dependencies

**PostgreSQL 17+** (Epic 4 Event Store + Epic 5 Tables):
- Connection: `postgresql://${POSTGRES_USER}:${POSTGRES_PASSWORD}@${POSTGRES_HOST}:${POSTGRES_PORT}/${POSTGRES_DB}`
- Epic 5 Tables: `feedback`, `alerts` (schemas defined in Data Models section)
- Epic 4 Tables: `events`, `event_metadata` (inherited from Epic 4 tech spec)
- Environment Variables: `DATABASE_URL`, `POSTGRES_HOST`, `POSTGRES_PORT`, `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD`

**Prometheus (Metrics Collection):**
- Metrics exposition: Tamma exposes `/metrics` endpoint on port 9090
- Prometheus scrapes endpoint every 15s (configurable via `prometheus.yml`)
- Optional: Grafana dashboard for visualization (not in alpha scope, documented for future)
- Environment Variables: `METRICS_PORT=9090`, `METRICS_ENABLED=true`

**AI Provider (Epic 1 Abstraction):**
- Claude Code (default): MCP protocol integration
- Environment Variables: `AI_PROVIDER=claude-code`, `ANTHROPIC_API_KEY` (if using API mode)
- Used by integration tests with mock provider (no real API calls)

**Git Platforms (Epic 1 Abstraction):**
- GitHub: REST API v3 + GraphQL API v4
  - Environment Variables: `GITHUB_TOKEN`, `GITHUB_OWNER`, `GITHUB_REPO`
  - Integration tests use `nock` to mock GitHub API responses
- GitLab: REST API v4
  - Environment Variables: `GITLAB_TOKEN`, `GITLAB_PROJECT_ID`
  - Integration tests use `nock` to mock GitLab API responses

**SMTP Server (Alert Email Channel):**
- Connection: `smtp://${SMTP_USER}:${SMTP_PASSWORD}@${SMTP_HOST}:${SMTP_PORT}`
- Environment Variables: `SMTP_HOST`, `SMTP_PORT`, `SMTP_USER`, `SMTP_PASSWORD`, `ALERT_EMAIL_FROM`, `ALERT_EMAIL_TO`
- Optional: Uses `nodemailer` with TLS encryption
- Fallback: If SMTP unavailable, email alerts logged with WARN level (no failure)

**Webhook Endpoints (Alert Webhook Channel):**
- User-configured webhook URLs in `~/.tamma/config.yaml` under `alerts.webhooks[]`
- HMAC signature header: `X-Tamma-Signature: sha256=<hex>`
- Timeout: 5 seconds per webhook POST
- Retry: 3 attempts with exponential backoff (1s, 2s, 4s)

**Docker (Alpha Release Packaging):**
- Base image: `node:22-alpine` (official Node.js LTS)
- Multi-arch builds: `linux/amd64`, `linux/arm64`
- Docker Hub / GitHub Container Registry for image distribution
- Environment Variables: All above variables passed via `docker run -e` or `.env` file

## Acceptance Criteria (Authoritative)

*Source: Epic 5 stories from epics.md (lines 759-956), validated against PRD requirements*

### Story 5.1: Structured Logging Implementation

1. All log statements use structured logging library (Winston, Bunyan, structlog) → **IMPLEMENTATION**: Pino 9.6+ (5x faster than Winston)
2. Log format: `{"timestamp": ISO8601, "level": "info/warn/error", "message": "...", "context": {...}}`
3. Context includes: correlation ID, issue number, PR number, actor ID
4. Log levels properly assigned: DEBUG (verbose details), INFO (key milestones), WARN (recoverable issues), ERROR (failures)
5. Logs written to: stdout (for container environments), file (for local development), log aggregation service (optional: Datadog, ELK)
6. Log volume under control: <10 log statements per event for typical flow
7. Sensitive data (API keys, tokens) redacted from all logs

### Story 5.2: Metrics Collection Infrastructure

1. Metrics library integrated (Prometheus client, StatsD, or similar) → **IMPLEMENTATION**: prom-client@15.1.3
2. Counter metrics: `issues_processed_total`, `prs_created_total`, `prs_merged_total`, `escalations_total`
3. Gauge metrics: `active_autonomous_loops`, `pending_approvals`, `queue_depth`
4. Histogram metrics: `issue_completion_duration_seconds`, `ai_request_duration_seconds`, `test_execution_duration_seconds`
5. Metrics exposed via HTTP endpoint: `GET /metrics` (Prometheus format)
6. Metrics include labels: provider name, Git platform, issue type, outcome (success/failure)
7. Metrics scraped by Prometheus (or pushed to metrics backend) every 15 seconds

### Story 5.3: Real-Time Dashboard - System Health

1. Web dashboard accessible at `http://localhost:3000/dashboard` (or configured port)
2. Dashboard displays: active loops count, pending approvals count, recent escalations list
3. Dashboard displays: current issue being processed, step in autonomous loop, estimated time remaining
4. Dashboard auto-refreshes every 10 seconds via WebSocket or SSE → **IMPLEMENTATION**: SSE with <500ms latency
5. Dashboard loads in <2 seconds on initial page load
6. Dashboard includes system status indicator: 🟢 Healthy, 🟡 Degraded, 🔴 Critical
7. Dashboard works in modern browsers (Chrome, Firefox, Safari, Edge)

### Story 5.4: Real-Time Dashboard - Development Velocity

1. Dashboard page: `http://localhost:3000/dashboard/velocity`
2. Charts display: issues completed per day (last 30 days), average time-to-merge (last 30 days), PR success rate (first-time merge vs. retry)
3. Charts include filters: date range, issue labels, AI provider
4. Charts use line charts for time series, bar charts for comparisons
5. Dashboard calculates key metrics: throughput (issues/week), cycle time (issue-to-merge duration), quality (test pass rate)
6. Dashboard exports charts as PNG or PDF for reporting
7. Dashboard responsive for mobile viewing (stakeholder reviews on-the-go)

### Story 5.5: Event Trail Exploration UI

1. Dashboard page: `http://localhost:3000/dashboard/events`
2. Event list displays: timestamp, event type, actor, summary (first 100 chars of payload)
3. Event list supports filtering: date range, event type, correlation ID, issue number
4. Event list supports full-text search across event payloads
5. Clicking event row expands full event details (JSON formatted)
6. Event list supports "Follow correlation ID" action to load all related events
7. Event list pagination (100 events per page) with infinite scroll → **IMPLEMENTATION**: @tanstack/react-virtual for virtualization

### Story 5.6: Alert System for Critical Issues

1. Alert triggers: escalation after 3 retries, system error (uncaught exception), API rate limit hit, event store write failure
2. Alert channels: CLI output (if running), webhook (POST to configured URL), email (if configured)
3. Alert payload includes: severity (critical/warning/info), title, description, correlation ID, timestamp, suggested action
4. Alert rate limiting: no more than 5 alerts per minute (prevent spam)
5. Alert history stored in database for review
6. Alert delivery tested with mock webhook endpoint
7. Alert system supports configuration of custom alert rules

### Story 5.7: Feedback Collection System

1. After PR merge, system prompts: "Rate this autonomous development cycle: 👍 👎"
2. If user selects 👎, system asks: "What went wrong? [free text]"
3. Feedback stored in database with: timestamp, correlation ID, rating, comment
4. Feedback visible in dashboard: `http://localhost:3000/dashboard/feedback`
5. Dashboard shows: satisfaction rate over time, common negative feedback themes (via keyword analysis)
6. Feedback export to CSV for analysis in external tools
7. Feedback system respects user privacy (no PII collection without consent)

### Story 5.8: Integration Testing Suite

1. Integration tests use real AI provider (mock mode) and mock Git platform API
2. Test scenarios: happy path (issue → plan → code → PR → merge), build failure with retry, test failure with escalation, ambiguous requirements with clarifying questions
3. Tests run in CI/CD pipeline on every PR
4. Tests validate: correct event sequence, proper error handling, retry limits enforced, escalation triggered
5. Tests complete in <5 minutes for full suite
6. Test coverage report shows >80% code coverage
7. Tests include assertions on event trail contents (verify all events captured)

### Story 5.9a: Installation & Setup Documentation

1. Installation instructions for npm package: `npm install -g @tamma/cli` with prerequisites (Node.js 22+, npm 10+)
2. Installation instructions for Docker: `docker pull tamma/tamma:latest` and `docker-compose.yml` example with PostgreSQL
3. Installation instructions for binaries: Download from GitHub Releases for Windows (.exe), macOS (.dmg/.pkg), Linux (.deb/.rpm/.tar.gz)
4. Installation instructions for source build: `git clone`, `pnpm install`, `pnpm build` with prerequisites
5. Prerequisites documented: Node.js 22 LTS, PostgreSQL 17+, Redis (optional), API keys (Anthropic Claude, GitHub)
6. Initial configuration wizard documented: `tamma init` command creates `~/.tamma/config.yaml` interactively
7. Troubleshooting common installation issues: Node.js version mismatch, PostgreSQL connection failures, permission errors

### Story 5.9b: Usage & Configuration Documentation

1. CLI command reference: `tamma run`, `tamma init`, `tamma config`, `tamma start` (service mode), `tamma --help`
2. Configuration file reference: all config options documented with examples, default values, validation rules
3. Environment variable mapping: `TAMMA_MODE`, `ANTHROPIC_API_KEY`, `GITHUB_TOKEN`, `DATABASE_URL`, etc.
4. Deployment modes documented: CLI (interactive), Service (background daemon), Web (REST API), Worker (CI/CD)
5. Provider setup guides: Anthropic Claude API key generation, OpenAI setup, GitHub token scopes, GitLab access tokens
6. Webhook integration setup: GitHub webhook URL, secret configuration, GitLab webhook token, event filtering
7. Troubleshooting guide: common errors (API authentication failures, provider rate limits), debug mode (`TAMMA_LOG_LEVEL=debug`), log analysis (grep patterns for stuck workflows)

### Story 5.9c: API Reference Documentation

1. REST API endpoints documented: `/api/v1/tasks`, `/api/v1/tasks/:id`, `/api/v1/config`, `/webhooks/github`, `/webhooks/gitlab`
2. Event schema documented: Epic 4 event format, all event types, payload structures, examples
3. Metrics endpoints documented: `/metrics` OpenMetrics spec, counter metrics, gauge metrics, histogram metrics
4. Webhook payload formats: GitHub webhook JSON schemas (issue assignment, PR events), GitLab webhook schemas (issue hooks, MR hooks)
5. Authentication documented: JWT token generation, token expiration, refresh tokens, API key authentication
6. Error responses documented: HTTP status codes, error JSON format, common error scenarios
7. API versioning strategy: `/api/v1`, `/api/v2`, deprecation policy, backward compatibility guarantees

### Story 5.9d: Full Documentation Website

1. Documentation hosted on GitHub Pages or Cloudflare Pages with custom domain (docs.tamma.dev or similar)
2. Searchable documentation: Algolia DocSearch integration or similar (fuzzy search, instant results)
3. Navigation organized by sections: Getting Started, Installation, Configuration, Usage, API Reference, Troubleshooting, Architecture
4. Architecture diagrams included: C4 model diagrams (Context, Containers, Components), Epic 1-5 layered architecture visualization
5. Tutorials and guides: "First Autonomous PR" walkthrough, "CI/CD Integration" guide, "Self-Hosting with Docker" guide
6. Replaces Story 1-12 marketing website: domain redirect from tamma.dev to docs.tamma.dev, "Coming Soon" replaced with full docs
7. Documentation reviewed by external beta tester for clarity: feedback incorporated via GitHub Issues (`feedback/documentation` label)

### Story 5.9e: Video Walkthrough (OPTIONAL)

1. Screen recording created: 5-10 minute video demonstrating complete autonomous loop
2. Video demonstrates: `tamma start` → issue selection → plan approval → PR creation → CI/CD checks → PR merge
3. Video includes: side-by-side views showing logs, metrics dashboard, event trail in real-time
4. Video published: YouTube or Vimeo with public access, embedded player in documentation website
5. Video includes: voiceover narration explaining each step, on-screen annotations for key concepts
6. Video optimized: 1080p resolution, <100MB file size, closed captions/subtitles for accessibility

### Story 5.10: Alpha Release Preparation

1. Release checklist completed: all acceptance criteria met, integration tests passing, documentation complete, security review passed
2. Release artifacts built: Docker image (multi-arch: amd64, arm64), binary releases (Windows, macOS, Linux), source tarball
3. Release notes drafted: features included, known limitations, breaking changes, upgrade path
4. GitHub release created with version tag (v0.1.0-alpha), release notes, artifact downloads
5. Release announcement prepared for: project README, Discord/Slack channels, mailing list
6. Telemetry consent mechanism implemented (opt-in for usage data collection)
7. Alpha release tagged as "prerelease" with warning: "Not production-ready, breaking changes expected"

**Total Acceptance Criteria: 70 (7 per story × 10 stories)**

## Traceability Mapping

*Maps each acceptance criterion to implementing component/module/service from Detailed Design*

### Story 5.1 → Logger Service (`packages/observability/src/logger.ts`)

| AC | Component | Module/Function |
|----|-----------|-----------------|
| 5.1.1 | Logger Service | `LoggerFactory.create()` with Pino 9.6+ |
| 5.1.2 | Logger Service | Pino base configuration: `formatters.log` returns JSON with ISO8601 timestamp |
| 5.1.3 | Logger Service | `logger.child({ correlationId, issueId, prId, actorId })` bindings |
| 5.1.4 | Logger Service | `logger.debug()`, `logger.info()`, `logger.warn()`, `logger.error()` methods |
| 5.1.5 | Logger Service | Pino transports: `pino.destination(1)` (stdout), `pino.destination('/var/log/tamma.log')` (file), optional Datadog transport |
| 5.1.6 | Logger Service | Log level guards: `if (logger.isLevelEnabled('debug'))` to skip expensive operations |
| 5.1.7 | Logger Service | Pino `redact` option: `redact: ['apiKey', 'token', 'password', 'authorization']` |

### Story 5.2 → Metrics Service (`packages/observability/src/metrics.ts`)

| AC | Component | Module/Function |
|----|-----------|-----------------|
| 5.2.1 | Metrics Service | `MetricsFactory.create()` with prom-client@15.1.3 |
| 5.2.2 | Metrics Service | `metrics.registerCounter('tamma_issues_processed_total', ...)`, `prs_created_total`, `prs_merged_total`, `escalations_total` |
| 5.2.3 | Metrics Service | `metrics.registerGauge('tamma_active_autonomous_loops', ...)`, `pending_approvals`, `queue_depth` |
| 5.2.4 | Metrics Service | `metrics.registerHistogram('tamma_issue_completion_duration_seconds', ...)`, `ai_request_duration_seconds`, `test_execution_duration_seconds` |
| 5.2.5 | Metrics Service | `metrics.getMetricsHandler()` returns Fastify route handler: `GET /metrics` → `register.metrics()` (OpenMetrics format) |
| 5.2.6 | Metrics Service | Counter/Histogram labels: `.labels({ provider, platform, issueType, outcome })` |
| 5.2.7 | External Integration | Prometheus `scrape_configs` targets `http://localhost:9090/metrics` every 15s |

### Story 5.3 → Dashboard Backend + Frontend (`packages/dashboard/`)

| AC | Component | Module/Function |
|----|-----------|-----------------|
| 5.3.1 | Dashboard Backend | Fastify server listens on port 3000: `server.listen({ port: config.dashboard.port })` |
| 5.3.2 | Dashboard Frontend | React components: `<SystemHealth>` queries `/api/v1/dashboard/health` → displays active_loops, pending_approvals, recent_escalations |
| 5.3.3 | Dashboard Frontend | `<CurrentIssueCard>` displays: issueId, currentStep, estimatedTimeRemaining from SSE stream |
| 5.3.4 | Dashboard Backend | SSE endpoint: `GET /api/v1/stream` pushes updates every 500ms via `fastify-sse-v2` |
| 5.3.5 | Dashboard Frontend | Vite build optimization: code splitting, tree-shaking, Brotli compression → <2s load time |
| 5.3.6 | Dashboard Frontend | `<SystemStatusIndicator>` computes status from health metrics: healthy (all green), degraded (1+ yellow), critical (1+ red) |
| 5.3.7 | Dashboard Frontend | Tested in Chrome 120+, Firefox 121+, Safari 17+, Edge 120+ (browser compatibility matrix) |

### Story 5.4 → Dashboard Frontend (`packages/dashboard/src/frontend/pages/Velocity.tsx`)

| AC | Component | Module/Function |
|----|-----------|-----------------|
| 5.4.1 | Dashboard Backend | Route: `GET /dashboard/velocity` serves React SPA; API: `GET /api/v1/dashboard/velocity` |
| 5.4.2 | Dashboard Frontend | Recharts components: `<LineChart data={issuesPerDay}>`, `<BarChart data={timeToMerge}>`, `<PieChart data={prSuccessRate}>` |
| 5.4.3 | Dashboard Frontend | Filter UI: `<DateRangePicker>`, `<LabelFilter>`, `<ProviderFilter>` → updates query params |
| 5.4.4 | Dashboard Frontend | Recharts `<LineChart>` for time series, `<BarChart>` for comparisons |
| 5.4.5 | Dashboard Backend | API computes: throughput = `COUNT(issues) / 7`, cycle time = `AVG(merged_at - created_at)`, quality = `COUNT(tests_passed) / COUNT(tests_total)` |
| 5.4.6 | Dashboard Frontend | Export buttons: `<ExportPNG>` uses `html-to-image`, `<ExportPDF>` uses `jspdf` |
| 5.4.7 | Dashboard Frontend | Tailwind CSS responsive classes: `grid-cols-1 md:grid-cols-2 lg:grid-cols-3` |

### Story 5.5 → Dashboard Frontend (`packages/dashboard/src/frontend/pages/EventTrail.tsx`)

| AC | Component | Module/Function |
|----|-----------|-----------------|
| 5.5.1 | Dashboard Backend | Route: `GET /dashboard/events` serves React SPA; API: `GET /api/v1/events` queries Epic 4 event store |
| 5.5.2 | Dashboard Frontend | `<EventListRow>` displays: `formatDate(timestamp)`, `eventType`, `actor`, `truncate(payload, 100)` |
| 5.5.3 | Dashboard Frontend | Filter UI: `<DateRangePicker>`, `<EventTypeFilter>`, `<CorrelationIdInput>`, `<IssueNumberInput>` |
| 5.5.4 | Dashboard Backend | API: `GET /api/v1/events?search={query}` uses PostgreSQL `ts_vector` full-text search on `payload` column |
| 5.5.5 | Dashboard Frontend | `<EventListRow onClick={() => setExpanded(!expanded)}>` → shows `<JSONViewer data={event}>` |
| 5.5.6 | Dashboard Frontend | `<FollowCorrelationButton onClick={() => navigate(`/events?correlationId=${event.correlationId}`)}` |
| 5.5.7 | Dashboard Frontend | `@tanstack/react-virtual` for virtualized list: `useVirtualizer({ count: totalEvents, estimateSize: () => 80 })` + infinite scroll via `@tanstack/react-query` `useInfiniteQuery` |

### Story 5.6 → Alert Manager (`packages/observability/src/alerts.ts`)

| AC | Component | Module/Function |
|----|-----------|-----------------|
| 5.6.1 | Alert Manager | Event listeners: `eventBus.on('EscalationTriggeredEvent')`, `process.on('uncaughtException')`, `provider.on('rateLimitHit')`, `eventStore.on('writeFailed')` |
| 5.6.2 | Alert Manager | Delivery channels: `CLIAlertChannel.send()`, `WebhookAlertChannel.send()` (axios POST), `EmailAlertChannel.send()` (nodemailer) |
| 5.6.3 | Alert Manager | Alert payload interface: `{ severity: 'critical'\|'warning'\|'info', title, description, correlationId, timestamp, suggestedAction }` |
| 5.6.4 | Alert Manager | Rate limiter: `RateLimiter.checkLimit('alert', 5, 60000)` → throws if >5 alerts/min |
| 5.6.5 | Alert Manager | PostgreSQL table: `INSERT INTO alerts (severity, title, ...) VALUES (...)` |
| 5.6.6 | Integration Test | `tests/integration/alerts.spec.ts` uses `nock('http://webhook-test.example.com').post('/alert')` |
| 5.6.7 | Alert Manager | `AlertRuleEngine.registerRule({ condition, action })` for custom rules |

### Story 5.7 → Feedback Service (`packages/observability/src/feedback.ts`)

| AC | Component | Module/Function |
|----|-----------|-----------------|
| 5.7.1 | Feedback Service | Event listener: `eventBus.on('PRMergedEvent', feedbackService.promptFeedback)` → CLI prompt: `inquirer.prompt([{ type: 'list', choices: ['👍', '👎'] }])` |
| 5.7.2 | Feedback Service | Conditional prompt: `if (rating === '👎') { await inquirer.prompt([{ type: 'input', message: 'What went wrong?' }]) }` |
| 5.7.3 | Feedback Service | PostgreSQL: `INSERT INTO feedback (timestamp, correlationId, rating, comment) VALUES (...)` |
| 5.7.4 | Dashboard Backend | Route: `GET /dashboard/feedback`; API: `GET /api/v1/feedback` queries feedback table |
| 5.7.5 | Dashboard Frontend | `<SatisfactionChart>` computes satisfaction rate: `COUNT(rating='👍') / COUNT(*)` over time; keyword analysis via simple tokenization + frequency count |
| 5.7.6 | Dashboard Backend | API: `GET /api/v1/feedback/export` returns CSV: `Content-Type: text/csv; Content-Disposition: attachment` |
| 5.7.7 | Feedback Service | GDPR compliance: Consent checkbox in feedback prompt; `tamma feedback delete --correlation-id <id>` command |

### Story 5.8 → Integration Test Runner (`tests/integration/runner.ts`)

| AC | Component | Module/Function |
|----|-----------|-----------------|
| 5.8.1 | Integration Test | Jest setup: `beforeAll(() => { mockAIProvider = new MockClaudeCodeProvider(); mockGitHub = nock('https://api.github.com') })` |
| 5.8.2 | Integration Test | Test suites: `tests/integration/happy-path.spec.ts`, `build-failure-retry.spec.ts`, `test-failure-escalation.spec.ts`, `ambiguous-requirements.spec.ts` |
| 5.8.3 | GitHub Actions | `.github/workflows/ci.yml`: `- run: pnpm test:integration` on every PR |
| 5.8.4 | Integration Test | Assertions: `expect(events).toHaveLength(expectedCount)`, `expect(retryCount).toBe(3)`, `expect(alerts).toContainEqual({ type: 'escalation' })` |
| 5.8.5 | GitHub Actions | Timeout: `timeout-minutes: 5` in workflow; Jest config: `testTimeout: 300000` (5 min) |
| 5.8.6 | Jest Coverage | Jest config: `collectCoverage: true, coverageThreshold: { global: { lines: 80 } }` → fails if <80% |
| 5.8.7 | Integration Test | Event trail assertions: `expect(eventStore.query({ correlationId })).resolves.toMatchSnapshot()` |

### Story 5.9a → Installation Documentation (`docs/installation/`) (Content Deliverable)

| AC | Component | Deliverable |
|----|-----------|-------------|
| 5.9a.1 | Documentation | `docs/installation/npm.md`: NPM installation (`npm install -g @tamma/cli`), prerequisites (Node.js 22+, npm 10+) |
| 5.9a.2 | Documentation | `docs/installation/docker.md`: Docker installation (`docker pull tamma/tamma:latest`), `docker-compose.yml` example with PostgreSQL/Redis |
| 5.9a.3 | Documentation | `docs/installation/binaries.md`: Binary downloads from GitHub Releases, platform-specific instructions (Windows .exe, macOS .dmg/.pkg, Linux .deb/.rpm/.tar.gz) |
| 5.9a.4 | Documentation | `docs/installation/source.md`: Source build instructions (`git clone`, `pnpm install`, `pnpm build`) |
| 5.9a.5 | Documentation | `docs/installation/prerequisites.md`: Node.js 22 LTS, PostgreSQL 17+, Redis (optional), API keys (Anthropic Claude, GitHub) |
| 5.9a.6 | Documentation | `docs/installation/initial-setup.md`: Configuration wizard (`tamma init`), creates `~/.tamma/config.yaml` interactively |
| 5.9a.7 | Documentation | `docs/installation/troubleshooting.md`: Node.js version mismatch, PostgreSQL connection failures, permission errors |

### Story 5.9b → Usage Documentation (`docs/usage/`) (Content Deliverable)

| AC | Component | Deliverable |
|----|-----------|-------------|
| 5.9b.1 | Documentation | `docs/usage/cli-commands.md`: `tamma run`, `tamma init`, `tamma config`, `tamma start`, `tamma --help` |
| 5.9b.2 | Documentation | `docs/usage/configuration.md`: All config options with examples, default values, validation rules |
| 5.9b.3 | Documentation | `docs/usage/environment-variables.md`: `TAMMA_MODE`, `ANTHROPIC_API_KEY`, `GITHUB_TOKEN`, `DATABASE_URL` mapping |
| 5.9b.4 | Documentation | `docs/usage/deployment-modes.md`: CLI (interactive), Service (daemon), Web (REST API), Worker (CI/CD) |
| 5.9b.5 | Documentation | `docs/usage/providers/`: Anthropic Claude setup, OpenAI setup, GitHub token scopes, GitLab access tokens (separate files per provider) |
| 5.9b.6 | Documentation | `docs/usage/webhooks.md`: GitHub webhook URL/secret, GitLab webhook token, event filtering |
| 5.9b.7 | Documentation | `docs/usage/troubleshooting.md`: API auth failures, provider rate limits, debug mode (`TAMMA_LOG_LEVEL=debug`), log analysis |

### Story 5.9c → API Documentation (`docs/api/`) (Content Deliverable)

| AC | Component | Deliverable |
|----|-----------|-------------|
| 5.9c.1 | Documentation | `docs/api/rest-api.md`: `/api/v1/tasks`, `/api/v1/tasks/:id`, `/api/v1/config`, `/webhooks/github`, `/webhooks/gitlab` endpoints with request/response examples |
| 5.9c.2 | Documentation | `docs/api/event-schema.md`: Epic 4 event format, all event types, payload structures, JSON examples |
| 5.9c.3 | Documentation | `docs/api/metrics.md`: `/metrics` endpoint, OpenMetrics spec, counter/gauge/histogram metrics reference |
| 5.9c.4 | Documentation | `docs/api/webhooks.md`: GitHub webhook JSON schemas (issue assignment, PR events), GitLab webhook schemas (issue hooks, MR hooks) |
| 5.9c.5 | Documentation | `docs/api/authentication.md`: JWT token generation, token expiration, refresh tokens, API key authentication |
| 5.9c.6 | Documentation | `docs/api/errors.md`: HTTP status codes, error JSON format, common error scenarios |
| 5.9c.7 | Documentation | `docs/api/versioning.md`: API versioning strategy (`/api/v1`, `/api/v2`), deprecation policy, backward compatibility |

### Story 5.9d → Documentation Website (Content + Infrastructure Deliverable)

| AC | Component | Deliverable |
|----|-----------|-------------|
| 5.9d.1 | Infrastructure | GitHub Pages or Cloudflare Pages deployment, custom domain (docs.tamma.dev) with SSL certificate |
| 5.9d.2 | Infrastructure | Algolia DocSearch integration: `docsearch.config.json`, search indexing, instant fuzzy search |
| 5.9d.3 | Documentation | Navigation structure: Getting Started, Installation, Configuration, Usage, API, Troubleshooting, Architecture sections |
| 5.9d.4 | Documentation | `docs/architecture/`: C4 diagrams (Mermaid or PlantUML), Context/Containers/Components diagrams, Epic 1-5 layered architecture |
| 5.9d.5 | Documentation | Tutorials: `docs/tutorials/first-pr.md`, `docs/tutorials/cicd-integration.md`, `docs/tutorials/self-hosting.md` |
| 5.9d.6 | Infrastructure | Domain redirect: tamma.dev → docs.tamma.dev, replace Story 1-12 "Coming Soon" with full docs |
| 5.9d.7 | Documentation | External beta tester review: feedback incorporated via GitHub Issues (`feedback/documentation` label) |

### Story 5.9e → Video Walkthrough (OPTIONAL) (Media Deliverable)

| AC | Component | Deliverable |
|----|-----------|-------------|
| 5.9e.1 | Video | Screen recording (OBS Studio): 5-10 minute video, 1080p resolution, MP4 format |
| 5.9e.2 | Video | Demo flow: `tamma start` → issue selection → plan approval → PR creation → CI/CD checks → PR merge |
| 5.9e.3 | Video | Split-screen layout: terminal logs (left), metrics dashboard (top-right), event trail (bottom-right) |
| 5.9e.4 | Infrastructure | YouTube or Vimeo upload: public access, embedded player in `docs/getting-started/video.md` |
| 5.9e.5 | Video | Voiceover narration: audio quality >128kbps, noise reduction, on-screen annotations for key concepts |
| 5.9e.6 | Video | Optimization: <100MB file size, closed captions/subtitles (VTT format), accessibility compliant |

### Story 5.10 → Alpha Release (`scripts/release.sh`, `.github/workflows/release.yml`)

| AC | Component | Module/Function |
|----|-----------|-----------------|
| 5.10.1 | Release Script | `scripts/pre-release-checklist.sh`: Runs all acceptance criteria validation scripts, integration tests, documentation linter |
| 5.10.2 | GitHub Actions | `.github/workflows/release.yml`: Docker build (`docker buildx build --platform linux/amd64,linux/arm64`), Binary build (esbuild + pkg), Tarball (`tar -czf tamma-v0.1.0-alpha.tar.gz`) |
| 5.10.3 | Release Script | `scripts/generate-release-notes.sh`: Parses `CHANGELOG.md`, extracts features/limitations/breaking changes, generates Markdown |
| 5.10.4 | GitHub Actions | `gh release create v0.1.0-alpha --notes-file release-notes.md --prerelease tamma-*.tar.gz tamma-*.exe tamma-macos tamma-linux` |
| 5.10.5 | Release Script | `scripts/prepare-announcements.sh`: Templates for README badge, Discord message, mailing list email |
| 5.10.6 | Telemetry Service | `packages/observability/src/telemetry.ts`: CLI flag `--telemetry`, Config option `telemetry: { enabled: false }`, Opt-in prompt during `tamma init` |
| 5.10.7 | GitHub Release | GitHub API: `PATCH /repos/{owner}/{repo}/releases/{id}` with `prerelease: true`, release notes include "⚠️ Alpha Warning: Not production-ready..." banner |

### Cross-Cutting Traceability

**Logger Service** instruments ALL components:
- Epic 1: `@tamma/providers` (AI provider calls), `@tamma/platforms` (Git API calls)
- Epic 2: `@tamma/orchestrator` (workflow steps), `@tamma/workflow` (issue selection, plan generation)
- Epic 3: `@tamma/quality` (build/test execution), `@tamma/analysis` (static analysis results)
- Epic 4: `@tamma/events` (event store writes), `@tamma/replay` (replay execution)

**Metrics Service** tracks ALL workflows:
- Counter increments on: issue processing, PR creation, escalations, errors
- Histogram records on: AI request duration, test execution duration, completion duration
- Gauge updates on: active loops, pending approvals, queue depth

**Dashboard** displays data from ALL epics:
- Epic 1: Provider selection, platform configuration
- Epic 2: Issue list, PR status
- Epic 3: Quality gate results, test pass rates
- Epic 4: Event trail exploration, time-travel replay links
- Epic 5: System health, velocity metrics, feedback, alerts

**Integration Tests** validate ALL epics end-to-end:
- Epic 1 + 2 + 3 + 4 + 5: Full autonomous loop (issue → PR merge → feedback)

## Risks, Assumptions, Open Questions

### Risks

**R1: Dashboard Performance Degradation with Large Event Trails**
- **Risk**: Event trail UI may become slow when querying >100,000 events from Epic 4 event store
- **Likelihood**: Medium (likely in production after months of operation)
- **Impact**: High (dashboard becomes unusable, defeats observability purpose)
- **Mitigation**:
  - Implement pagination with max 100 events per page (AC 5.5.7)
  - Use `@tanstack/react-virtual` for virtualized rendering (reduces DOM nodes)
  - Add query timeout (5s) with cancellation on navigation (NFR Reliability #6)
  - Create PostgreSQL indexes on frequently queried columns: `timestamp DESC`, `correlationId`, `event_type` (Epic 4 responsibility)
  - Consider projection table for dashboard-specific queries if needed

**R2: Alert Storm During Cascading Failures**
- **Risk**: Single failure (e.g., PostgreSQL outage) triggers multiple alert types simultaneously, overwhelming webhook/email channels
- **Likelihood**: Medium (common in distributed systems)
- **Impact**: Medium (operators desensitized to alerts, critical alerts missed)
- **Mitigation**:
  - Rate limiting: 5 alerts/min per trigger type (AC 5.6.4)
  - Alert deduplication: Suppress duplicate alerts within 5-minute window
  - Alert priority: Critical alerts bypass rate limit (max 3 critical/min)
  - Circuit breaker: Stop sending alerts if webhook fails 10 consecutive times (prevents alert loop)

**R3: Integration Test Flakiness in CI/CD Pipeline**
- **Risk**: Network-dependent tests (webhook POSTs, SSE connections) fail intermittently in GitHub Actions runners
- **Likelihood**: High (common with integration tests)
- **Impact**: Medium (blocks PRs, slows development velocity)
- **Mitigation**:
  - Use `nock` to mock all external HTTP calls (no real network in tests)
  - Use `testcontainers` for PostgreSQL (isolated, reproducible database state)
  - Automatic retry for network-dependent tests (max 3 retries, AC 5.8.1)
  - Measure flake rate: Target <2% over 100 runs (NFR Reliability #5)
  - Tag flaky tests with `@flaky` for investigation

**R4: Frontend Bundle Size Bloat**
- **Risk**: Dashboard frontend bundle exceeds 500KB target due to dependency creep (Recharts, React, Tailwind)
- **Likelihood**: Medium (common in React apps)
- **Impact**: Low (slower dashboard load times, but not critical)
- **Mitigation**:
  - Vite code splitting and tree-shaking (automatic)
  - Lazy load heavy components: `const VelocityPage = React.lazy(() => import('./pages/Velocity'))`
  - Analyze bundle with `vite-bundle-visualizer` in CI (fail if >500KB gzipped)
  - Use lightweight alternatives: `date-fns` instead of `moment.js` (already planned)
  - Brotli compression (smaller than gzip, 10-15% reduction)

**R5: GDPR Compliance Gaps in Feedback Collection**
- **Risk**: Feedback system may violate GDPR if PII inadvertently collected without consent
- **Likelihood**: Low (design includes consent, but implementation errors possible)
- **Impact**: High (legal/regulatory consequences, reputation damage)
- **Mitigation**:
  - Explicit consent checkbox in feedback prompt (AC 5.7.7)
  - Right to erasure command: `tamma feedback delete --correlation-id <id>` (AC 5.7.7)
  - Data retention policy: 90-day automatic deletion (configurable, NFR Security #4)
  - Privacy policy review by legal team before alpha release
  - Feedback text scanning for common PII patterns (email addresses, phone numbers) → warning displayed

### Assumptions

**A1: PostgreSQL 17+ Available in All Deployment Environments**
- **Assumption**: Users deploying Tamma have PostgreSQL 17+ accessible (self-hosted or cloud RDS/Cloud SQL)
- **Validation**: Document PostgreSQL requirement prominently in installation guide (Story 5.9a - Installation Documentation)
- **Fallback**: If not met, Docker Compose includes PostgreSQL container (recommended for alpha)

**A2: SSE Sufficient for Real-Time Updates (No WebSocket Needed)**
- **Assumption**: Server-Sent Events provide adequate real-time performance (<500ms latency) for dashboard updates; full WebSocket bidirectional not required
- **Validation**: Performance testing in Story 5.3 validates SSE latency meets <500ms target (NFR Performance #2)
- **Fallback**: If SSE insufficient, upgrade to WebSocket in post-alpha iteration (Story 5.3 architecture allows swap)

**A3: Prometheus Self-Hosted by Operations Teams**
- **Assumption**: Operations teams will deploy Prometheus themselves to scrape `/metrics` endpoint; Tamma does NOT bundle Prometheus
- **Validation**: Document Prometheus setup in user guide with example `prometheus.yml` config (Story 5.9b - Usage Documentation)
- **Fallback**: Metrics still collected in-memory even without Prometheus (1-hour retention, NFR Reliability #3)

**A4: Alpha Users Accept CLI-Only Feedback Collection**
- **Assumption**: Alpha release feedback collection via CLI prompt sufficient; web-based feedback form not needed for MVP
- **Validation**: User research with beta testers confirms CLI acceptable for alpha phase
- **Fallback**: If feedback adoption low, add dashboard-based feedback form in post-alpha (Story 5.7 extension)

**A5: Recharts Performance Adequate for Dashboard Charts**
- **Assumption**: Recharts library handles 30-day time series charts (typically 30-100 data points) without performance issues
- **Validation**: Performance testing with 1000+ data points to stress test
- **Fallback**: If Recharts slow, switch to lightweight alternative (Chart.js, Victory, or custom D3.js)

**A6: >80% Test Coverage Achievable Without E2E Browser Tests**
- **Assumption**: Integration tests (Jest + Supertest + Nock + Testcontainers) achieve >80% coverage without Playwright/Cypress E2E tests
- **Validation**: Coverage report in Story 5.8 (AC 5.8.6) validates threshold met
- **Fallback**: If <80%, add minimal E2E tests for critical user flows (dashboard navigation)

### Open Questions

**Q1: Should Dashboard Support Multiple Concurrent Orchestrators?**
- **Question**: If user runs multiple orchestrator instances (multi-region, high availability), should dashboard aggregate metrics across all instances or display per-instance?
- **Impact**: Affects dashboard data model and API design
- **Decision Needed By**: Story 5.3 implementation start
- **Recommendation**: MVP displays single orchestrator (simplest); post-alpha adds multi-orchestrator support

**Q2: How to Handle Dashboard Access Control in Multi-User Deployments?**
- **Question**: Should dashboard support user roles (admin, developer, viewer) or single shared access for alpha?
- **Impact**: Affects JWT authentication, dashboard feature visibility
- **Decision Needed By**: Story 5.3 implementation start
- **Recommendation**: Single shared access for alpha (JWT token shared within team); add RBAC post-alpha if requested

**Q3: What Alert Severity Thresholds Trigger Email vs. Webhook?**
- **Question**: Should email channel be reserved for `critical` severity only, or also send `warning` level alerts?
- **Impact**: Affects alert routing logic and user notification frequency
- **Decision Needed By**: Story 5.6 implementation start
- **Recommendation**: Email for `critical` only (avoid inbox spam); webhook receives all severities (can filter downstream)

**Q4: Should Event Trail Support Event Replay from Dashboard UI?**
- **Question**: Epic 4 provides `tamma replay` CLI command; should dashboard add "Replay from this event" button in event trail UI?
- **Impact**: Adds complexity to dashboard; requires backend integration with Epic 4 replay engine
- **Decision Needed By**: Story 5.5 implementation start
- **Recommendation**: Defer to post-alpha; Story 5.5 includes "Replay correlation ID" link that opens CLI command template for copy/paste

**Q5: How to Collect Telemetry Without Backend Server?**
- **Question**: Telemetry collection (AC 5.10.6) requires opt-in, but where to send telemetry data if Tamma is CLI tool?
- **Impact**: Affects telemetry infrastructure design
- **Decision Needed By**: Story 5.10 implementation start
- **Recommendation**: Telemetry writes to local JSON file (`~/.tamma/telemetry.json`); separate opt-in utility uploads to cloud endpoint periodically (e.g., weekly cron job or manual `tamma telemetry upload`)

**Q6: Should Dashboard Frontend Be Server-Side Rendered (SSR) or Client-Side Rendered (CSR)?**
- **Question**: React 19 supports Server Components; should dashboard use SSR for faster initial load or CSR for simpler deployment?
- **Impact**: Affects Vite build config, Fastify integration, deployment complexity
- **Decision Needed By**: Story 5.3/5.4 implementation start
- **Recommendation**: CSR for alpha (simpler); SSR adds complexity without major benefit for authenticated dashboard

### Dependencies on External Decisions

**D1: Epic 4 Event Store Query Performance (Blocking)**
- **Dependency**: Story 5.5 Event Trail UI depends on Epic 4's event query API performance (<1s for 1000 events)
- **Owner**: Epic 4 implementation team
- **Resolution Path**: If Epic 4 query slow, Story 5.5 adds aggressive caching (React Query 5-minute stale time) or projection table

**D2: Epic 3 Escalation Event Schema (Blocking)**
- **Dependency**: Story 5.6 Alert System depends on Epic 3 emitting `EscalationTriggeredEvent` with specific payload structure
- **Owner**: Epic 3 implementation team
- **Resolution Path**: Coordinate event schema in Epic 3 tech spec; validate with integration test in Story 5.8

**D3: Epic 2 PR Merge Event Timing (Blocking)**
- **Dependency**: Story 5.7 Feedback Collection depends on Epic 2 emitting `PRMergedEvent` at correct lifecycle point (after merge completes, before next issue selected)
- **Owner**: Epic 2 implementation team
- **Resolution Path**: Validate event ordering in Epic 2 workflow; feedback prompt must appear before autonomous loop continues

## Test Strategy Summary

*Comprehensive testing approach for Epic 5 to achieve >80% coverage and ensure production readiness*

### Unit Testing Strategy

**Scope**: Individual functions, classes, and modules in isolation

**Target Coverage**: >90% for core observability logic (logger, metrics, alerts, feedback services)

**Key Test Suites**:

1. **Logger Service Tests** (`packages/observability/src/__tests__/logger.spec.ts`)
   - Test log level filtering (DEBUG/INFO/WARN/ERROR)
   - Test correlation ID propagation via `logger.child()`
   - Test secret redaction for API keys, tokens, passwords
   - Test log output format (JSON structure validation)
   - Test multiple transport destinations (stdout, file, Datadog)
   - Mock: `pino` transports, filesystem writes

2. **Metrics Service Tests** (`packages/observability/src/__tests__/metrics.spec.ts`)
   - Test counter increment operations
   - Test gauge set/inc/dec operations
   - Test histogram observe with custom buckets
   - Test metric label combinations (provider, platform, outcome)
   - Test `/metrics` endpoint returns OpenMetrics format
   - Mock: `prom-client` registry

3. **Alert Manager Tests** (`packages/observability/src/__tests__/alerts.spec.ts`)
   - Test rate limiting (max 5 alerts/min)
   - Test alert deduplication logic
   - Test webhook delivery with retry (3 attempts, exponential backoff)
   - Test email delivery with nodemailer
   - Test CLI alert output formatting
   - Mock: axios (webhook POSTs), nodemailer SMTP transport

4. **Feedback Service Tests** (`packages/observability/src/__tests__/feedback.spec.ts`)
   - Test feedback prompt after PRMergedEvent
   - Test negative feedback triggers follow-up comment prompt
   - Test feedback persistence to PostgreSQL
   - Test GDPR compliance (consent, right to erasure)
   - Mock: inquirer prompts, PostgreSQL client

**Test Execution**: `pnpm test:unit` (runs Jest with coverage report)

### Integration Testing Strategy

**Scope**: End-to-end workflows involving multiple components and external dependencies

**Target Coverage**: >80% overall (combining unit + integration)

**Key Test Suites** (Story 5.8):

1. **Happy Path Test** (`tests/integration/happy-path.spec.ts`)
   - **Scenario**: Issue selection → Plan approval → Code generation → Tests pass → PR created → PR merged → Feedback collected
   - **Validates**: Full autonomous loop with all observability touchpoints
   - **Assertions**:
     - Correlation ID consistent across all events (logger, metrics, event store)
     - Metrics incremented: `issues_processed_total`, `prs_created_total`, `prs_merged_total`
     - Event trail contains expected events (IssueSelectedEvent, PRCreatedEvent, PRMergedEvent)
     - Feedback prompt appears after PR merge
     - Logs written for each workflow step
   - **Duration**: ~30 seconds

2. **Build Failure with Retry Test** (`tests/integration/build-failure-retry.spec.ts`)
   - **Scenario**: Build fails 3 times → Escalation triggered → Alert sent
   - **Validates**: Epic 3 quality gates trigger alerts correctly
   - **Assertions**:
     - Retry counter incremented 3 times
     - EscalationTriggeredEvent emitted after 3rd failure
     - Alert webhook POSTed with correct severity (critical)
     - Alert stored in PostgreSQL alerts table
     - Metrics: `escalations_total` counter incremented
   - **Duration**: ~20 seconds

3. **Test Failure with Escalation Test** (`tests/integration/test-failure-escalation.spec.ts`)
   - **Scenario**: Test suite fails → Retry 3 times → Escalation → Manual intervention required
   - **Validates**: Test execution observability and alert delivery
   - **Assertions**:
     - Test execution logs captured (PASS/FAIL status)
     - Histogram metric: `test_execution_duration_seconds` recorded
     - Alert email queued (if SMTP configured)
     - Dashboard event trail displays escalation
   - **Duration**: ~25 seconds

4. **Ambiguous Requirements Test** (`tests/integration/ambiguous-requirements.spec.ts`)
   - **Scenario**: Ambiguity detected → Clarifying questions asked → User provides input → Workflow continues
   - **Validates**: Epic 3 intelligence layer observability
   - **Assertions**:
     - AmbiguityDetectedEvent emitted with ambiguity score
     - Dashboard displays clarifying questions in event trail
     - Metrics: Ambiguity score tracked (future metric)
     - Logs capture user responses
   - **Duration**: ~15 seconds

5. **Dashboard API Test** (`tests/integration/dashboard-api.spec.ts`)
   - **Scenario**: Dashboard queries health, velocity, events, feedback endpoints
   - **Validates**: Dashboard backend REST API and SSE
   - **Assertions**:
     - `GET /api/v1/dashboard/health` returns 200 with expected schema
     - `GET /api/v1/dashboard/velocity` computes metrics correctly
     - `GET /api/v1/events?correlationId=<id>` returns filtered events
     - `GET /api/v1/stream` establishes SSE connection and pushes updates
     - `GET /api/v1/feedback` returns feedback with pagination
   - **Duration**: ~10 seconds
   - **Mock**: Epic 4 event store (in-memory), PostgreSQL (testcontainers)

6. **Alert Webhook Delivery Test** (`tests/integration/alert-webhook.spec.ts`)
   - **Scenario**: Alert triggered → Webhook POST sent → Retries on failure
   - **Validates**: Alert delivery with retry logic
   - **Assertions**:
     - Webhook POST includes correct payload structure
     - HMAC signature header present and valid
     - Retry logic triggers on 500/timeout (3 retries, exponential backoff)
     - Failed webhook logged with full context
   - **Duration**: ~15 seconds
   - **Mock**: `nock` for webhook endpoint (simulate success/failure)

**Test Execution**: `pnpm test:integration` (runs in CI/CD on every PR)

**Test Environment Setup**:
- PostgreSQL: `testcontainers` starts PostgreSQL 17 container
- AI Provider: `MockClaudeCodeProvider` (no real API calls)
- Git Platform: `nock` mocks GitHub/GitLab API responses
- File System: Temporary directories for logs/feedback

### Performance Testing Strategy

**Scope**: Validate NFR performance targets

**Key Performance Tests**:

1. **Dashboard Load Time Test** (`tests/performance/dashboard-load.spec.ts`)
   - **Target**: Initial page load <2s on 50 Mbps connection
   - **Method**: Lighthouse CI in GitHub Actions, measure LCP (Largest Contentful Paint)
   - **Pass Criteria**: LCP <2s, bundle size <500KB gzipped

2. **SSE Latency Test** (`tests/performance/sse-latency.spec.ts`)
   - **Target**: SSE updates delivered within 500ms of metric change
   - **Method**: Emit metric update, measure time until SSE message received by client
   - **Pass Criteria**: p95 latency <500ms over 100 samples

3. **Metrics Endpoint Performance Test** (`tests/performance/metrics-endpoint.spec.ts`)
   - **Target**: `/metrics` endpoint response <50ms (p95), <100ms (p99)
   - **Method**: Artillery load test with 100 concurrent requests
   - **Pass Criteria**: p95 <50ms, p99 <100ms, no timeouts

4. **Event Trail Query Performance Test** (`tests/performance/event-trail-query.spec.ts`)
   - **Target**: Query 1000 events in <1s, 10,000 events in <3s
   - **Method**: Seed event store with 10,000 events, measure query times
   - **Pass Criteria**: 1K events <1s, 10K events <3s (Epic 4 indexes required)

5. **Integration Test Suite Duration Test**
   - **Target**: Full suite completes in <5 minutes
   - **Method**: CI/CD pipeline timeout enforcement
   - **Pass Criteria**: GitHub Actions job completes in <5 min

**Test Execution**: `pnpm test:performance` (runs in CI/CD on release candidates)

### Security Testing Strategy

**Scope**: Validate NFR security requirements

**Key Security Tests**:

1. **Secret Redaction Test** (`tests/security/secret-redaction.spec.ts`)
   - **Validates**: Logs never expose API keys, tokens, passwords
   - **Method**: Inject secrets into log context, verify redaction patterns applied
   - **Pass Criteria**: All secret patterns replaced with `***REDACTED***`

2. **JWT Authentication Test** (`tests/security/dashboard-auth.spec.ts`)
   - **Validates**: Dashboard requires valid JWT token for API access
   - **Method**: Attempt API requests without token, with expired token, with invalid signature
   - **Pass Criteria**: 401 Unauthorized returned for invalid auth

3. **Webhook SSRF Protection Test** (`tests/security/webhook-ssrf.spec.ts`)
   - **Validates**: Webhook URLs validated to prevent SSRF attacks
   - **Method**: Attempt to configure webhook URLs pointing to localhost, private IPs
   - **Pass Criteria**: Configuration rejected with clear error message

4. **GDPR Compliance Test** (`tests/security/gdpr-compliance.spec.ts`)
   - **Validates**: Feedback system respects right to erasure, consent requirements
   - **Method**: Submit feedback, verify consent recorded, execute deletion command, verify data removed
   - **Pass Criteria**: Data fully deleted within 1s of deletion command

5. **Dependency Vulnerability Scan**
   - **Validates**: No critical CVEs in NPM dependencies
   - **Method**: `npm audit` in CI/CD pipeline
   - **Pass Criteria**: Zero critical or high vulnerabilities (blocks PR merge)

**Test Execution**: `pnpm test:security` (runs in CI/CD on every PR)

### Manual Testing Strategy

**Scope**: User experience validation not easily automated

**Manual Test Cases** (Story 5.9 documentation validation):

1. **Dashboard Visual Inspection**
   - Verify dashboard layout responsive on mobile, tablet, desktop
   - Verify charts render correctly in Chrome, Firefox, Safari, Edge
   - Verify color scheme meets accessibility standards (WCAG AA contrast ratios)
   - Verify loading states display properly during slow queries

2. **Feedback Collection UX**
   - Verify CLI feedback prompt appears at correct time (after PR merge)
   - Verify feedback prompt is non-intrusive (user can skip)
   - Verify negative feedback follow-up prompt is clear and respectful

3. **Alert Webhook Testing**
   - Configure real webhook endpoint (e.g., Slack incoming webhook)
   - Trigger escalation scenario, verify Slack message received
   - Verify webhook payload is well-formatted for common tools (Slack, PagerDuty, Discord)

4. **Installation Documentation Review** (Story 5.9a)
   - External beta tester attempts installation following docs (npm, Docker, binaries, source)
   - Tester reports gaps/errors in installation instructions
   - Verify initial configuration wizard works as documented

5. **Usage Documentation Review** (Story 5.9b)
   - External beta tester follows usage guides for all deployment modes
   - Tester attempts provider setup following guides
   - Tester attempts common troubleshooting scenarios using docs

6. **API Documentation Review** (Story 5.9c)
   - External beta tester reviews REST API reference
   - Tester tests API endpoints using documentation examples
   - Verify event schema and webhook payload examples are accurate

7. **Documentation Website Review** (Story 5.9d - AC 5.9d.7)
   - External beta tester reviews full documentation website
   - Tester verifies search functionality works correctly
   - Tester navigates through all sections, reports navigation issues

**Test Execution**: Stories 5.9a-5.9d include manual testing checklists completed before alpha release

### Test Coverage Goals

**Overall Coverage Target**: >80% (AC 5.8.6)

**Coverage by Package**:
- `packages/observability`: >90% (core functionality)
- `packages/dashboard/backend`: >85% (REST API)
- `packages/dashboard/frontend`: >70% (React components, harder to test)
- `tests/integration`: 100% (all integration tests must pass)

**Coverage Enforcement**:
- Jest config: `coverageThreshold: { global: { lines: 80, branches: 75, functions: 80, statements: 80 } }`
- CI/CD: Build fails if coverage below threshold
- Coverage report uploaded to Codecov (public badge in README)

### Test Data Management

**Test Fixtures**: `tests/fixtures/`
- Sample events for event trail testing
- Sample metrics data for dashboard charts
- Sample feedback records for feedback UI testing

**Test Database Seeding**:
- `testcontainers` PostgreSQL starts with empty schema
- Each test suite seeds required data in `beforeAll()` hook
- Data isolated per test suite (no cross-test pollution)

**Mock AI Provider Responses**:
- `tests/mocks/claude-code-responses.json`: Canned AI responses for common scenarios
- Mock provider returns consistent, deterministic responses (enables snapshot testing)

### Continuous Integration

**GitHub Actions Workflow** (`.github/workflows/ci.yml`):

```yaml
name: CI
on: [push, pull_request]
jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: pnpm/action-setup@v2
      - uses: actions/setup-node@v4
        with: {node-version: 22}
      - run: pnpm install
      - run: pnpm test:unit --coverage
      - run: pnpm test:integration
      - run: pnpm test:security
      - run: npm audit --audit-level=high
      - uses: codecov/codecov-action@v4
        with: {file: ./coverage/coverage-final.json}
```

**Test Execution Order**:
1. Unit tests (fastest, fail fast)
2. Security tests (critical, must pass)
3. Integration tests (slower, comprehensive)
4. Performance tests (on release branch only, not every PR)

**Flaky Test Management**:
- Tag flaky tests with `@flaky` in test name
- CI retries flaky tests automatically (max 3 retries)
- Weekly report on flaky tests for investigation
- Target: <2% flake rate over 100 runs (NFR Reliability #5)
