# Story 5.2: Metrics Collection Infrastructure

Status: ready-for-dev

## Story

As a **product manager**,
I want metrics collected for key system behaviors and performance,
so that I can track development velocity, quality trends, and system health.

## Acceptance Criteria

1. Metrics library integrated (Prometheus client, StatsD, or similar)
2. Counter metrics: `issues_processed_total`, `prs_created_total`, `prs_merged_total`, `escalations_total`
3. Gauge metrics: `active_autonomous_loops`, `pending_approvals`, `queue_depth`
4. Histogram metrics: `issue_completion_duration_seconds`, `ai_request_duration_seconds`, `test_execution_duration_seconds`
5. Metrics exposed via HTTP endpoint: `GET /metrics` (Prometheus format)
6. Metrics include labels: provider name, Git platform, issue type, outcome (success/failure)
7. Metrics scraped by Prometheus (or pushed to metrics backend) every 15 seconds

## Tasks / Subtasks

- [ ] Task 1: Select and integrate metrics library (AC: 1)
  - [ ] Subtask 1.1: Evaluate metrics libraries (Prometheus client, StatsD, OpenTelemetry)
  - [ ] Subtask 1.2: Select optimal library for performance and ecosystem
  - [ ] Subtask 1.3: Integrate metrics library into application
  - [ ] Subtask 1.4: Configure metrics collection and aggregation
  - [ ] Subtask 1.5: Add metrics library integration tests

- [ ] Task 2: Implement counter metrics (AC: 2)
  - [ ] Subtask 2.1: Define counter metric schema and naming conventions
  - [ ] Subtask 2.2: Create counter for issues processed
  - [ ] Subtask 2.3: Create counter for PRs created and merged
  - [ ] Subtask 2.4: Create counter for escalations triggered
  - [ ] Subtask 2.5: Add counter metrics integration points

- [ ] Task 3: Implement gauge metrics (AC: 3)
  - [ ] Subtask 3.1: Define gauge metric schema and use cases
  - [ ] Subtask 3.2: Create gauge for active autonomous loops
  - [ ] Subtask 3.3: Create gauge for pending approvals
  - [ ] Subtask 3.4: Create gauge for queue depth and system load
  - [ ] Subtask 3.5: Add gauge metrics integration points

- [ ] Task 4: Implement histogram metrics (AC: 4)
  - [ ] Subtask 4.1: Define histogram metric schema and buckets
  - [ ] Subtask 4.2: Create histogram for issue completion duration
  - [ ] Subtask 4.3: Create histogram for AI request duration
  - [ ] Subtask 4.4: Create histogram for test execution duration
  - [ ] Subtask 4.5: Add histogram metrics integration points

- [ ] Task 5: Create metrics HTTP endpoint (AC: 5)
  - [ ] Subtask 5.1: Design metrics endpoint interface and format
  - [ ] Subtask 5.2: Implement Prometheus-compatible metrics format
  - [ ] Subtask 5.3: Add metrics endpoint authentication and access control
  - [ ] Subtask 5.4: Create metrics endpoint health checks
  - [ ] Subtask 5.5: Add metrics endpoint documentation

- [ ] Task 6: Implement metric labels and dimensions (AC: 6)
  - [ ] Subtask 6.1: Define label naming conventions and allowed values
  - [ ] Subtask 6.2: Add provider name labels (anthropic, openai, etc.)
  - [ ] Subtask 6.3: Add Git platform labels (github, gitlab, etc.)
  - [ ] Subtask 6.4: Add outcome labels (success, failure, timeout)
  - [ ] Subtask 6.5: Add custom labels for business metrics

- [ ] Task 7: Configure metrics collection and scraping (AC: 7)
  - [ ] Subtask 7.1: Configure Prometheus scraping interval
  - [ ] Subtask 7.2: Set up metrics backend integration
  - [ ] Subtask 7.3: Configure metric retention and aggregation
  - [ ] Subtask 7.4: Add metrics collection monitoring and alerts
  - [ ] Subtask 7.5: Create metrics collection configuration

## Dev Notes

### Requirements Context Summary

**Epic 5 Foundation:** This story establishes the metrics collection infrastructure essential for monitoring autonomous system performance, tracking development velocity, and ensuring system health.

**Production Monitoring:** Metrics must provide visibility into system behavior for operations teams, enabling proactive monitoring, alerting, and performance optimization.

**Business Intelligence:** Metrics must capture business-relevant data like development velocity, success rates, and quality trends to support product management decisions.

### Implementation Guidance

**Metrics Library Selection:**

```typescript
// Recommended: Prometheus client for ecosystem compatibility
interface MetricsConfig {
  enabled: boolean;
  prefix: string; // Metric name prefix (e.g., 'tamma_')
  labels: {
    default: Record<string, string>; // Default labels for all metrics
    allowed: string[]; // Allowed label keys
    cardinality: number; // Max unique label values
  };
  collection: {
    interval: number; // Collection interval in seconds
    timeout: number; // Collection timeout in milliseconds
    bufferSize: number; // Internal buffer size
  };
  exposition: {
    endpoint: string; // Metrics endpoint path
    port: number; // Metrics server port
    authentication: {
      enabled: boolean;
      type: 'basic' | 'bearer' | 'none';
      credentials?: {
        username?: string;
        password?: string;
        token?: string;
      };
    };
  };
  aggregation: {
    enabled: boolean;
    rules: AggregationRule[];
  };
}

// Prometheus client configuration
const prometheusClient = require('prom-client');

const metricsConfig: MetricsConfig = {
  enabled: process.env.METRICS_ENABLED !== 'false',
  prefix: 'tamma_',
  labels: {
    default: {
      service: 'tamma',
      version: process.env.TAMMA_VERSION || '1.0.0',
      environment: process.env.NODE_ENV || 'development',
    },
    allowed: [
      'provider',
      'platform',
      'issue_type',
      'outcome',
      'error_type',
      'step',
      'actor',
      'repository',
    ],
    cardinality: 1000,
  },
  collection: {
    interval: 15, // 15 seconds
    timeout: 5000, // 5 seconds
    bufferSize: 10000,
  },
  exposition: {
    endpoint: '/metrics',
    port: parseInt(process.env.METRICS_PORT || '9090'),
    authentication: {
      enabled: process.env.METRICS_AUTH_ENABLED === 'true',
      type: 'basic',
      credentials: {
        username: process.env.METRICS_USERNAME,
        password: process.env.METRICS_PASSWORD,
      },
    },
  },
};
```

**Counter Metrics Implementation:**

```typescript
@Injectable()
export class CounterMetrics {
  private readonly counters: Map<string, prometheus.Counter<string>> = new Map();

  constructor(private config: MetricsConfig) {
    this.initializeCounters();
  }

  private initializeCounters(): void {
    // Issues processed counter
    this.counters.set(
      'issues_processed_total',
      new prometheus.Counter({
        name: `${this.config.prefix}issues_processed_total`,
        help: 'Total number of issues processed by autonomous loops',
        labelNames: ['provider', 'platform', 'issue_type', 'outcome'],
      })
    );

    // PRs created counter
    this.counters.set(
      'prs_created_total',
      new prometheus.Counter({
        name: `${this.config.prefix}prs_created_total`,
        help: 'Total number of pull requests created',
        labelNames: ['provider', 'platform', 'issue_type', 'actor'],
      })
    );

    // PRs merged counter
    this.counters.set(
      'prs_merged_total',
      new prometheus.Counter({
        name: `${this.config.prefix}prs_merged_total`,
        help: 'Total number of pull requests merged',
        labelNames: ['provider', 'platform', 'issue_type', 'merge_strategy'],
      })
    );

    // Escalations counter
    this.counters.set(
      'escalations_total',
      new prometheus.Counter({
        name: `${this.config.prefix}escalations_total`,
        help: 'Total number of escalations triggered',
        labelNames: ['escalation_type', 'severity', 'component', 'reason'],
      })
    );

    // AI requests counter
    this.counters.set(
      'ai_requests_total',
      new prometheus.Counter({
        name: `${this.config.prefix}ai_requests_total`,
        help: 'Total number of AI provider requests',
        labelNames: ['provider', 'model', 'request_type', 'outcome'],
      })
    );

    // Code changes counter
    this.counters.set(
      'code_changes_total',
      new prometheus.Counter({
        name: `${this.config.prefix}code_changes_total`,
        help: 'Total number of code changes made',
        labelNames: ['change_type', 'language', 'actor', 'file_type'],
      })
    );
  }

  incrementIssuesProcessed(labels: {
    provider: string;
    platform: string;
    issueType: string;
    outcome: 'success' | 'failure' | 'timeout';
  }): void {
    const counter = this.counters.get('issues_processed_total');
    if (counter) {
      counter.inc(this.validateLabels(labels));
    }
  }

  incrementPRsCreated(labels: {
    provider: string;
    platform: string;
    issueType: string;
    actor: 'system' | 'user';
  }): void {
    const counter = this.counters.get('prs_created_total');
    if (counter) {
      counter.inc(this.validateLabels(labels));
    }
  }

  incrementPRsMerged(labels: {
    provider: string;
    platform: string;
    issueType: string;
    mergeStrategy: 'merge' | 'squash' | 'rebase';
  }): void {
    const counter = this.counters.get('prs_merged_total');
    if (counter) {
      counter.inc(this.validateLabels(labels));
    }
  }

  incrementEscalations(labels: {
    escalationType: string;
    severity: 'low' | 'medium' | 'high' | 'critical';
    component: string;
    reason: string;
  }): void {
    const counter = this.counters.get('escalations_total');
    if (counter) {
      counter.inc(this.validateLabels(labels));
    }
  }

  incrementAIRequests(labels: {
    provider: string;
    model: string;
    requestType: string;
    outcome: 'success' | 'failure' | 'timeout';
  }): void {
    const counter = this.counters.get('ai_requests_total');
    if (counter) {
      counter.inc(this.validateLabels(labels));
    }
  }

  incrementCodeChanges(labels: {
    changeType: 'create' | 'update' | 'delete';
    language: string;
    actor: 'system' | 'user';
    fileType: string;
  }): void {
    const counter = this.counters.get('code_changes_total');
    if (counter) {
      counter.inc(this.validateLabels(labels));
    }
  }

  private validateLabels(labels: Record<string, string>): Record<string, string> {
    const validated: Record<string, string> = { ...this.config.labels.default };

    for (const [key, value] of Object.entries(labels)) {
      if (this.config.labels.allowed.includes(key)) {
        validated[key] = this.sanitizeLabelValue(value);
      }
    }

    return validated;
  }

  private sanitizeLabelValue(value: string): string {
    // Sanitize label values for Prometheus
    return value.replace(/[^a-zA-Z0-9_]/g, '_').substring(0, 200); // Prometheus limit
  }
}
```

**Gauge Metrics Implementation:**

```typescript
@Injectable()
export class GaugeMetrics {
  private readonly gauges: Map<string, prometheus.Gauge<string>> = new Map();

  constructor(private config: MetricsConfig) {
    this.initializeGauges();
  }

  private initializeGauges(): void {
    // Active autonomous loops gauge
    this.gauges.set(
      'active_autonomous_loops',
      new prometheus.Gauge({
        name: `${this.config.prefix}active_autonomous_loops`,
        help: 'Number of currently active autonomous loops',
        labelNames: ['mode', 'environment'],
      })
    );

    // Pending approvals gauge
    this.gauges.set(
      'pending_approvals',
      new prometheus.Gauge({
        name: `${this.config.prefix}pending_approvals`,
        help: 'Number of pending user approvals',
        labelNames: ['approval_type', 'priority'],
      })
    );

    // Queue depth gauge
    this.gauges.set(
      'queue_depth',
      new prometheus.Gauge({
        name: `${this.config.prefix}queue_depth`,
        help: 'Current depth of processing queues',
        labelNames: ['queue_type', 'priority'],
      })
    );

    // System resources gauge
    this.gauges.set(
      'system_resources',
      new prometheus.Gauge({
        name: `${this.config.prefix}system_resources`,
        help: 'System resource utilization',
        labelNames: ['resource_type', 'unit'],
      })
    );

    // Active connections gauge
    this.gauges.set(
      'active_connections',
      new prometheus.Gauge({
        name: `${this.config.prefix}active_connections`,
        help: 'Number of active connections',
        labelNames: ['connection_type', 'provider'],
      })
    );
  }

  setActiveAutonomousLoops(
    value: number,
    labels: {
      mode: 'orchestrator' | 'worker' | 'cli';
      environment?: string;
    }
  ): void {
    const gauge = this.gauges.get('active_autonomous_loops');
    if (gauge) {
      gauge.set(this.validateLabels(labels), value);
    }
  }

  setPendingApprovals(
    value: number,
    labels: {
      approvalType: string;
      priority: 'low' | 'medium' | 'high' | 'critical';
    }
  ): void {
    const gauge = this.gauges.get('pending_approvals');
    if (gauge) {
      gauge.set(this.validateLabels(labels), value);
    }
  }

  setQueueDepth(
    value: number,
    labels: {
      queueType: string;
      priority: 'low' | 'medium' | 'high';
    }
  ): void {
    const gauge = this.gauges.get('queue_depth');
    if (gauge) {
      gauge.set(this.validateLabels(labels), value);
    }
  }

  setSystemResources(
    value: number,
    labels: {
      resourceType: 'cpu' | 'memory' | 'disk' | 'network';
      unit: string;
    }
  ): void {
    const gauge = this.gauges.get('system_resources');
    if (gauge) {
      gauge.set(this.validateLabels(labels), value);
    }
  }

  setActiveConnections(
    value: number,
    labels: {
      connectionType: string;
      provider: string;
    }
  ): void {
    const gauge = this.gauges.get('active_connections');
    if (gauge) {
      gauge.set(this.validateLabels(labels), value);
    }
  }

  private validateLabels(labels: Record<string, string>): Record<string, string> {
    const validated: Record<string, string> = {};

    for (const [key, value] of Object.entries(labels)) {
      if (this.config.labels.allowed.includes(key)) {
        validated[key] = this.sanitizeLabelValue(value);
      }
    }

    return validated;
  }

  private sanitizeLabelValue(value: string): string {
    return value.replace(/[^a-zA-Z0-9_]/g, '_').substring(0, 200);
  }
}
```

**Histogram Metrics Implementation:**

```typescript
@Injectable()
export class HistogramMetrics {
  private readonly histograms: Map<string, prometheus.Histogram<string>> = new Map();

  constructor(private config: MetricsConfig) {
    this.initializeHistograms();
  }

  private initializeHistograms(): void {
    // Issue completion duration histogram
    this.histograms.set(
      'issue_completion_duration_seconds',
      new prometheus.Histogram({
        name: `${this.config.prefix}issue_completion_duration_seconds`,
        help: 'Time taken to complete issues from selection to merge',
        labelNames: ['provider', 'platform', 'issue_type', 'complexity'],
        buckets: [60, 300, 900, 1800, 3600, 7200, 14400], // 1min to 4hrs
      })
    );

    // AI request duration histogram
    this.histograms.set(
      'ai_request_duration_seconds',
      new prometheus.Histogram({
        name: `${this.config.prefix}ai_request_duration_seconds`,
        help: 'Duration of AI provider requests',
        labelNames: ['provider', 'model', 'request_type', 'token_range'],
        buckets: [0.1, 0.5, 1, 2, 5, 10, 30, 60], // 100ms to 1min
      })
    );

    // Test execution duration histogram
    this.histograms.set(
      'test_execution_duration_seconds',
      new prometheus.Histogram({
        name: `${this.config.prefix}test_execution_duration_seconds`,
        help: 'Duration of test execution',
        labelNames: ['test_type', 'framework', 'language', 'result'],
        buckets: [1, 5, 10, 30, 60, 300, 600, 1800], // 1s to 30min
      })
    );

    // Code generation duration histogram
    this.histograms.set(
      'code_generation_duration_seconds',
      new prometheus.Histogram({
        name: `${this.config.prefix}code_generation_duration_seconds`,
        help: 'Duration of code generation operations',
        labelNames: ['provider', 'language', 'complexity', 'file_count'],
        buckets: [1, 5, 10, 30, 60, 120, 300, 600], // 1s to 10min
      })
    );

    // API response duration histogram
    this.histograms.set(
      'api_response_duration_seconds',
      new prometheus.Histogram({
        name: `${this.config.prefix}api_response_duration_seconds`,
        help: 'Duration of API responses',
        labelNames: ['endpoint', 'method', 'status_code', 'user_type'],
        buckets: [0.01, 0.05, 0.1, 0.5, 1, 2, 5, 10], // 10ms to 10s
      })
    );
  }

  observeIssueCompletion(
    duration: number,
    labels: {
      provider: string;
      platform: string;
      issueType: string;
      complexity: 'low' | 'medium' | 'high';
    }
  ): void {
    const histogram = this.histograms.get('issue_completion_duration_seconds');
    if (histogram) {
      histogram.observe(this.validateLabels(labels), duration);
    }
  }

  observeAIRequest(
    duration: number,
    labels: {
      provider: string;
      model: string;
      requestType: string;
      tokenRange: 'small' | 'medium' | 'large';
    }
  ): void {
    const histogram = this.histograms.get('ai_request_duration_seconds');
    if (histogram) {
      histogram.observe(this.validateLabels(labels), duration);
    }
  }

  observeTestExecution(
    duration: number,
    labels: {
      testType: string;
      framework: string;
      language: string;
      result: 'pass' | 'fail' | 'error';
    }
  ): void {
    const histogram = this.histograms.get('test_execution_duration_seconds');
    if (histogram) {
      histogram.observe(this.validateLabels(labels), duration);
    }
  }

  observeCodeGeneration(
    duration: number,
    labels: {
      provider: string;
      language: string;
      complexity: 'low' | 'medium' | 'high';
      fileCount: 'single' | 'few' | 'many';
    }
  ): void {
    const histogram = this.histograms.get('code_generation_duration_seconds');
    if (histogram) {
      histogram.observe(this.validateLabels(labels), duration);
    }
  }

  observeAPIResponse(
    duration: number,
    labels: {
      endpoint: string;
      method: string;
      statusCode: string;
      userType: string;
    }
  ): void {
    const histogram = this.histograms.get('api_response_duration_seconds');
    if (histogram) {
      histogram.observe(this.validateLabels(labels), duration);
    }
  }

  private validateLabels(labels: Record<string, string>): Record<string, string> {
    const validated: Record<string, string> = {};

    for (const [key, value] of Object.entries(labels)) {
      if (this.config.labels.allowed.includes(key)) {
        validated[key] = this.sanitizeLabelValue(value);
      }
    }

    return validated;
  }

  private sanitizeLabelValue(value: string): string {
    return value.replace(/[^a-zA-Z0-9_]/g, '_').substring(0, 200);
  }
}
```

**Metrics HTTP Endpoint:**

```typescript
@Controller('/metrics')
@UseGuards(AuthGuard)
export class MetricsController {
  constructor(
    private counterMetrics: CounterMetrics,
    private gaugeMetrics: GaugeMetrics,
    private histogramMetrics: HistogramMetrics
  ) {}

  @Get()
  @Header('Content-Type', 'text/plain; version=0.0.4')
  getMetrics(): string {
    // Return all metrics in Prometheus format
    return prometheus.register.metrics();
  }

  @Get('/health')
  getHealth(): MetricsHealth {
    return {
      status: 'healthy',
      timestamp: new Date().toISOString(),
      metrics: {
        counters: this.getCounterCount(),
        gauges: this.getGaugeCount(),
        histograms: this.getHistogramCount(),
      },
      collection: {
        lastCollection: new Date().toISOString(),
        interval: 15,
        bufferSize: 10000,
      },
    };
  }

  private getCounterCount(): number {
    return prometheus.register.getMetricsAsJSON().filter((metric) => metric.type === 'counter')
      .length;
  }

  private getGaugeCount(): number {
    return prometheus.register.getMetricsAsJSON().filter((metric) => metric.type === 'gauge')
      .length;
  }

  private getHistogramCount(): number {
    return prometheus.register.getMetricsAsJSON().filter((metric) => metric.type === 'histogram')
      .length;
  }
}
```

### Technical Specifications

**Performance Requirements:**

- Metric collection overhead: <1ms per metric
- Memory usage: <100MB for metrics infrastructure
- HTTP endpoint response: <100ms for typical metrics export
- Label cardinality: <1000 unique label combinations

**Reliability Requirements:**

- Metric collection success rate: >99.9%
- Metrics endpoint availability: >99.9%
- Data accuracy: >99.9% metric accuracy
- Failover handling: Graceful degradation on failures

**Security Requirements:**

- Metrics endpoint authentication: Configurable auth
- Access control: Role-based metrics access
- Data privacy: No sensitive data in metrics
- Rate limiting: Prevent metrics endpoint abuse

**Integration Requirements:**

- Prometheus compatibility: Standard exposition format
- Multiple backends: Prometheus, StatsD, OpenTelemetry
- Configuration management: Environment-based configuration
- Monitoring integration: Alerting and dashboard support

### Dependencies

**Internal Dependencies:**

- Story 4.1: Event schema design (provides event context for metrics)
- Story 1.5-7: System configuration management (provides metrics config)
- All workflow stories (provide metrics integration points)

**External Dependencies:**

- Prometheus client library
- Metrics collection service
- HTTP server for metrics endpoint
- Authentication middleware

### Risks and Mitigations

| Risk                       | Severity | Mitigation                           |
| -------------------------- | -------- | ------------------------------------ |
| High cardinality metrics   | Medium   | Label cardinality limits, monitoring |
| Performance impact         | Medium   | Async collection, sampling           |
| Metrics endpoint abuse     | Low      | Authentication, rate limiting        |
| Metric collection failures | Medium   | Failover handling, monitoring        |

### Success Metrics

- [ ] Collection overhead: <1ms per metric
- [ ] Metrics endpoint response: <100ms
- [ ] Memory usage: <100MB total
- [ ] Collection success rate: >99.9%
- [ ] Label cardinality: <1000 combinations

## Related

- Related story: `docs/stories/5-1-structured-logging-implementation.md`
- Related story: `docs/stories/5-3-real-time-dashboard-system-health.md`
- Related story: `docs/stories/4-1-event-schema-design.md`
- Technical specification: `docs/tech-spec-epic-5.md`
- Architecture: `docs/architecture.md` (Observability section)

## References

- [Prometheus Best Practices](https://prometheus.io/docs/practices/)
- [Metrics Naming Conventions](https://prometheus.io/docs/practices/naming/)
- [OpenTelemetry Specification](https://opentelemetry.io/docs/reference/specification/)
- [Systems Monitoring Patterns](https://www.oreilly.com/library/view/monitoring-distributed/9781492033431/)
