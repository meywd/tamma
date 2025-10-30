# Story 5.1: Structured Logging Implementation

Status: ready-for-dev

## Story

As an **operations engineer**,
I want structured logs (JSON format) with log levels and context,
so that I can efficiently search, filter, and analyze logs in production.

## Acceptance Criteria

1. All log statements use structured logging library (Winston, Bunyan, structlog)
2. Log format: `{"timestamp": ISO8601, "level": "info/warn/error", "message": "...", "context": {...}}`
3. Context includes: correlation ID, issue number, PR number, actor ID
4. Log levels properly assigned: DEBUG (verbose details), INFO (key milestones), WARN (recoverable issues), ERROR (failures)
5. Logs written to: stdout (for container environments), file (for local development), log aggregation service (optional: Datadog, ELK)
6. Log volume under control: <10 log statements per event for typical flow
7. Sensitive data (API keys, tokens) redacted from all logs

## Tasks / Subtasks

- [ ] Task 1: Select and configure structured logging library (AC: 1)
  - [ ] Subtask 1.1: Evaluate logging libraries (Winston, Bunyan, pino, structlog)
  - [ ] Subtask 1.2: Select optimal library for performance and features
  - [ ] Subtask 1.3: Configure logging library with JSON formatter
  - [ ] Subtask 1.4: Add custom serializers for complex objects
  - [ ] Subtask 1.5: Create logging configuration schema

- [ ] Task 2: Implement standardized log format (AC: 2)
  - [ ] Subtask 2.1: Define log entry structure and fields
  - [ ] Subtask 2.2: Create JSON formatter with consistent field ordering
  - [ ] Subtask 2.3: Implement timestamp formatting (ISO8601 with milliseconds)
  - [ ] Subtask 2.4: Add log level normalization and validation
  - [ ] Subtask 2.5: Create log format validation tests

- [ ] Task 3: Implement context propagation (AC: 3)
  - [ ] Subtask 3.1: Create correlation ID propagation service
  - [ ] Subtask 3.2: Implement context-aware logger wrapper
  - [ ] Subtask 3.3: Add automatic context extraction from workflow state
  - [ ] Subtask 3.4: Create context middleware for API requests
  - [ ] Subtask 3.5: Add context validation and enrichment

- [ ] Task 4: Define and implement log levels (AC: 4)
  - [ ] Subtask 4.1: Define log level hierarchy and usage guidelines
  - [ ] Subtask 4.2: Implement level-based filtering and routing
  - [ ] Subtask 4.3: Create level-specific output formats
  - [ ] Subtask 4.4: Add dynamic level adjustment (environment-based)
  - [ ] Subtask 4.5: Create log level usage validation

- [ ] Task 5: Configure multiple output destinations (AC: 5)
  - [ ] Subtask 5.1: Implement stdout transport for container environments
  - [ ] Subtask 5.2: Create file rotation transport for local development
  - [ ] Subtask 5.3: Add external service integration (Datadog, ELK)
  - [ ] Subtask 5.4: Implement transport-specific formatting
  - [ ] Subtask 5.5: Create output destination configuration

- [ ] Task 6: Implement log volume control (AC: 6)
  - [ ] Subtask 6.1: Create log volume monitoring and metrics
  - [ ] Subtask 6.2: Implement rate limiting for verbose logging
  - [ ] Subtask 6.3: Add log sampling for high-volume scenarios
  - [ ] Subtask 6.4: Create log volume alerts and thresholds
  - [ ] Subtask 6.5: Add log volume optimization recommendations

- [ ] Task 7: Implement sensitive data redaction (AC: 7)
  - [ ] Subtask 7.1: Create sensitive data detection patterns
  - [ ] Subtask 7.2: Implement redaction middleware and serializers
  - [ ] Subtask 7.3: Add configurable redaction rules
  - [ ] Subtask 7.4: Create redaction validation and testing
  - [ ] Subtask 7.5: Add redaction audit logging

## Dev Notes

### Requirements Context Summary

**Epic 5 Foundation:** This story establishes the foundational logging infrastructure for production monitoring and debugging. Structured logging is essential for operating autonomous systems at scale and meeting compliance requirements.

**Production Requirements:** Logs must be machine-readable for automated analysis while remaining human-readable for debugging. Multiple output destinations support different deployment scenarios (containers, local development, cloud environments).

**Security Requirements:** Sensitive data must be automatically redacted to prevent accidental exposure in logs while maintaining debugging capability through secure alternatives.

### Implementation Guidance

**Logging Library Selection:**

```typescript
// Recommended: Pino for performance and features
interface LoggerConfig {
  level: 'debug' | 'info' | 'warn' | 'error' | 'fatal';
  format: 'json' | 'pretty';
  destination: 'stdout' | 'file' | 'service';
  redaction: {
    enabled: boolean;
    patterns: RedactionPattern[];
  };
  sampling: {
    enabled: boolean;
    rate: number; // Sample rate (0-1)
  };
  transport: {
    file?: {
      path: string;
      rotation: 'daily' | 'weekly' | 'size';
      maxSize: string;
      maxFiles: number;
    };
    service?: {
      type: 'datadog' | 'elasticsearch' | 'splunk';
      endpoint: string;
      apiKey: string;
      tags: Record<string, string>;
    };
  };
}

// Pino configuration example
const pinoConfig: pino.LoggerOptions = {
  level: process.env.LOG_LEVEL || 'info',
  formatters: {
    level: (label) => ({ level: label }),
    log: (object) => {
      // Add custom formatting here
      return object;
    },
  },
  timestamp: pino.stdTimeFunctions.isoTime,
  serializers: {
    req: pino.stdSerializers.req,
    res: pino.stdSerializers.res,
    err: pino.stdSerializers.err,
    // Custom serializers for sensitive data
    config: redactSensitiveData,
    headers: redactHeaders,
  },
};
```

**Log Entry Structure:**

```typescript
interface LogEntry {
  // Standard fields
  timestamp: string; // ISO8601 with milliseconds
  level: LogLevel; // debug, info, warn, error, fatal
  message: string; // Human-readable message
  pid: number; // Process ID
  hostname: string; // Hostname where log was generated

  // Tamma-specific fields
  service: string; // Service name (orchestrator, worker, api)
  version: string; // Tamma version
  environment: string; // Environment (dev, staging, prod)

  // Context fields
  correlationId?: string; // Workflow correlation ID
  issueId?: string; // Related issue ID
  prId?: string; // Related PR ID
  actorId?: string; // Actor (user or system component)
  stepId?: string; // Current workflow step

  // Performance fields
  duration?: number; // Operation duration in milliseconds
  memoryUsage?: number; // Memory usage in MB
  cpuUsage?: number; // CPU usage percentage

  // Error fields
  error?: {
    name: string;
    message: string;
    stack?: string;
    code?: string | number;
  };

  // Custom fields
  [key: string]: unknown; // Additional context-specific data
}

type LogLevel = 'debug' | 'info' | 'warn' | 'error' | 'fatal';
```

**Context-Aware Logger:**

```typescript
@Injectable()
export class ContextAwareLogger {
  private readonly logger: pino.Logger;
  private readonly contextStore: Map<string, unknown> = new Map();

  constructor(config: LoggerConfig) {
    this.logger = pino(this.createPinoConfig(config));
  }

  // Context management
  setContext(key: string, value: unknown): void {
    this.contextStore.set(key, value);
  }

  getContext(key?: string): unknown | Record<string, unknown> {
    if (key) {
      return this.contextStore.get(key);
    }
    return Object.fromEntries(this.contextStore);
  }

  clearContext(): void {
    this.contextStore.clear();
  }

  // Logging methods with automatic context
  debug(message: string, extra?: Record<string, unknown>): void {
    this.log('debug', message, extra);
  }

  info(message: string, extra?: Record<string, unknown>): void {
    this.log('info', message, extra);
  }

  warn(message: string, extra?: Record<string, unknown>): void {
    this.log('warn', message, extra);
  }

  error(message: string, error?: Error, extra?: Record<string, unknown>): void {
    this.log('error', message, { ...extra, error: this.serializeError(error) });
  }

  fatal(message: string, error?: Error, extra?: Record<string, unknown>): void {
    this.log('fatal', message, { ...extra, error: this.serializeError(error) });
  }

  private log(level: LogLevel, message: string, extra?: Record<string, unknown>): void {
    const context = Object.fromEntries(this.contextStore);
    const logEntry = {
      message,
      ...context,
      ...extra,
    };

    this.logger[level](logEntry);
  }

  private serializeError(error?: Error): unknown {
    if (!error) return undefined;

    return {
      name: error.name,
      message: error.message,
      stack: error.stack,
      code: (error as any).code,
    };
  }
}
```

**Sensitive Data Redaction:**

```typescript
interface RedactionPattern {
  name: string;
  pattern: RegExp;
  replacement: string | ((match: string) => string);
  enabled: boolean;
}

class SensitiveDataRedactor {
  private readonly patterns: RedactionPattern[] = [
    {
      name: 'api_key',
      pattern: /(?:api[_-]?key|apikey)[\s:=]+['"]?([a-zA-Z0-9_-]{20,})['"]?/gi,
      replacement: (match) => match.replace(/([a-zA-Z0-9_-]{20,})/, '***REDACTED***'),
      enabled: true,
    },
    {
      name: 'password',
      pattern: /(?:password|pwd|pass)[\s:=]+['"]?([^'"\s]{8,})['"]?/gi,
      replacement: '***REDACTED***',
      enabled: true,
    },
    {
      name: 'token',
      pattern: /(?:token|bearer)[\s:=]+['"]?([a-zA-Z0-9._-]{20,})['"]?/gi,
      replacement: (match) => match.replace(/([a-zA-Z0-9._-]{20,})/, '***REDACTED***'),
      enabled: true,
    },
    {
      name: 'secret',
      pattern: /(?:secret|private[_-]?key)[\s:=]+['"]?([a-zA-Z0-9_-]{20,})['"]?/gi,
      replacement: (match) => match.replace(/([a-zA-Z0-9_-]{20,})/, '***REDACTED***'),
      enabled: true,
    },
    {
      name: 'email',
      pattern: /\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,}\b/g,
      replacement: (match) => {
        const [local, domain] = match.split('@');
        const maskedLocal = local.substring(0, 2) + '***';
        return `${maskedLocal}@${domain}`;
      },
      enabled: true,
    },
  ];

  redact(obj: unknown): unknown {
    if (typeof obj === 'string') {
      return this.redactString(obj);
    }

    if (Array.isArray(obj)) {
      return obj.map((item) => this.redact(item));
    }

    if (obj && typeof obj === 'object') {
      const redacted: Record<string, unknown> = {};
      for (const [key, value] of Object.entries(obj)) {
        redacted[key] = this.redact(value);
      }
      return redacted;
    }

    return obj;
  }

  private redactString(str: string): string {
    let redacted = str;

    for (const pattern of this.patterns) {
      if (pattern.enabled) {
        redacted = redacted.replace(pattern.pattern, pattern.replacement as string);
      }
    }

    return redacted;
  }

  addPattern(pattern: RedactionPattern): void {
    this.patterns.push(pattern);
  }

  enablePattern(name: string): void {
    const pattern = this.patterns.find((p) => p.name === name);
    if (pattern) {
      pattern.enabled = true;
    }
  }

  disablePattern(name: string): void {
    const pattern = this.patterns.find((p) => p.name === name);
    if (pattern) {
      pattern.enabled = false;
    }
  }
}
```

**Transport Configuration:**

```typescript
interface TransportManager {
  createTransports(config: LoggerConfig): pino.TransportTarget[];
}

class ProductionTransportManager implements TransportManager {
  createTransports(config: LoggerConfig): pino.TransportTarget[] {
    const transports: pino.TransportTarget[] = [];

    // Always include stdout for container environments
    transports.push({
      target: 'pino/file',
      level: config.level,
      options: {
        destination: 1, // stdout
      },
    });

    // Add file transport for local development
    if (config.destination === 'file' && config.transport.file) {
      transports.push({
        target: 'pino/file',
        level: config.level,
        options: {
          destination: config.transport.file.path,
          mkdir: true,
        },
      });
    }

    // Add external service transport
    if (config.destination === 'service' && config.transport.service) {
      switch (config.transport.service.type) {
        case 'datadog':
          transports.push(this.createDatadogTransport(config.transport.service));
          break;
        case 'elasticsearch':
          transports.push(this.createElasticsearchTransport(config.transport.service));
          break;
        case 'splunk':
          transports.push(this.createSplunkTransport(config.transport.service));
          break;
      }
    }

    return transports;
  }

  private createDatadogTransport(config: any): pino.TransportTarget {
    return {
      target: 'pino-datadog',
      level: 'info',
      options: {
        apiKey: config.apiKey,
        hostname: os.hostname(),
        service: 'tamma',
        env: process.env.NODE_ENV || 'development',
        tags: Object.entries(config.tags || {}).map(([key, value]) => `${key}:${value}`),
      },
    };
  }

  private createElasticsearchTransport(config: any): pino.TransportTarget {
    return {
      target: 'pino-elasticsearch',
      level: 'info',
      options: {
        client: {
          node: config.endpoint,
          auth: {
            apiKey: config.apiKey,
          },
        },
        index: `tamma-logs-${new Date().toISOString().substring(0, 7)}`, // YYYY-MM
        type: '_doc',
      },
    };
  }
}
```

**Log Volume Control:**

```typescript
class LogVolumeController {
  private readonly logCounts: Map<string, number> = new Map();
  private readonly timeWindows: Map<string, number> = new Map();
  private readonly maxLogsPerMinute = 1000;
  private readonly samplingThreshold = 500;

  shouldLog(level: LogLevel, message: string): boolean {
    const key = `${level}:${message.substring(0, 50)}`;
    const now = Date.now();
    const windowStart = now - 60000; // 1 minute ago

    // Clean old entries
    this.cleanupOldEntries(windowStart);

    // Check volume limits
    const currentCount = this.logCounts.get(key) || 0;
    if (currentCount > this.maxLogsPerMinute) {
      return false;
    }

    // Apply sampling if over threshold
    if (currentCount > this.samplingThreshold) {
      return Math.random() < 0.1; // 10% sample rate
    }

    // Increment counter
    this.logCounts.set(key, currentCount + 1);
    this.timeWindows.set(key, now);

    return true;
  }

  private cleanupOldEntries(cutoffTime: number): void {
    for (const [key, timestamp] of this.timeWindows.entries()) {
      if (timestamp < cutoffTime) {
        this.logCounts.delete(key);
        this.timeWindows.delete(key);
      }
    }
  }

  getMetrics(): LogVolumeMetrics {
    return {
      totalLogsPerMinute: Array.from(this.logCounts.values()).reduce(
        (sum, count) => sum + count,
        0
      ),
      uniqueMessages: this.logCounts.size,
      topMessages: this.getTopMessages(10),
    };
  }

  private getTopMessages(limit: number): Array<{ message: string; count: number }> {
    return Array.from(this.logCounts.entries())
      .map(([message, count]) => ({ message, count }))
      .sort((a, b) => b.count - a.count)
      .slice(0, limit);
  }
}
```

### Technical Specifications

**Performance Requirements:**

- Log serialization: <1ms per log entry
- Context propagation: <0.1ms per operation
- Redaction processing: <2ms per log entry
- Memory overhead: <50MB for logging infrastructure

**Reliability Requirements:**

- Log delivery success rate: >99.9%
- Context propagation accuracy: >99.9%
- Redaction effectiveness: >95% sensitive data detection
- Transport failover: Automatic fallback to stdout

**Security Requirements:**

- Sensitive data redaction: >95% accuracy
- Log access control: Role-based permissions
- Log integrity: Tamper detection and prevention
- Data retention: Configurable retention policies

**Integration Requirements:**

- Multiple transports: stdout, file, external services
- Configuration management: Environment-based configuration
- Monitoring integration: Log volume and error metrics
- Container compatibility: Structured JSON output for log aggregators

### Dependencies

**Internal Dependencies:**

- Story 4.1: Event schema design (provides event context)
- Story 1.5-7: System configuration management (provides logging config)
- All workflow stories (provide logging integration points)

**External Dependencies:**

- Structured logging library (Pino recommended)
- Log aggregation service clients (Datadog, Elasticsearch)
- Sensitive data detection patterns
- File rotation utilities

### Risks and Mitigations

| Risk                            | Severity | Mitigation                                   |
| ------------------------------- | -------- | -------------------------------------------- |
| Performance impact from logging | Medium   | Async logging, sampling, rate limiting       |
| Sensitive data leakage          | High     | Comprehensive redaction patterns, validation |
| Log volume overload             | Medium   | Rate limiting, sampling, volume monitoring   |
| Transport failures              | Medium   | Multiple transports, failover mechanisms     |

### Success Metrics

- [ ] Log serialization performance: <1ms per entry
- [ ] Context propagation accuracy: >99.9%
- [ ] Sensitive data redaction: >95% accuracy
- [ ] Log delivery success rate: >99.9%
- [ ] Memory overhead: <50MB total

## Related

- Related story: `docs/stories/5-2-metrics-collection-infrastructure.md`
- Related story: `docs/stories/4-1-event-schema-design.md`
- Related story: `docs/stories/1.5-7-system-configuration-management.md`
- Technical specification: `docs/tech-spec-epic-5.md`
- Architecture: `docs/architecture.md` (Observability section)

## References

- [Structured Logging Best Practices](https://12factor.net/logs)
- [Pino Logging Library](https://getpino.io/)
- [Log Aggregation Patterns](https://www.elastic.co/guide/en/elasticsearch/reference/current/logs.html)
- [Sensitive Data Protection](https://owasp.org/www-project-cheat-sheets/cheatsheets/Logging_Cheat_Sheet.html)
