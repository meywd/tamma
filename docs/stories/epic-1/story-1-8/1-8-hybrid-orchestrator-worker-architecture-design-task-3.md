# Story 1.8 Task 3: Design Shared Components and Interfaces

## Task Overview

Design shared components and interfaces that will be used by both orchestrator and worker modes, including configuration management, event emission and audit trail integration, logging infrastructure, health check endpoints, and error handling patterns. This task establishes the foundation for consistent behavior across both modes.

## Acceptance Criteria

### 3.1 Define Shared Configuration Management Approach

- [ ] Design unified configuration system for both orchestrator and worker modes
- [ ] Define configuration schema validation and type safety
- [ ] Implement environment variable override mechanisms
- [ ] Design configuration hot-reload capabilities
- [ ] Document configuration security and best practices

### 3.2 Design Event Emission and Audit Trail Integration

- [ ] Define DCB event sourcing interface for both modes
- [ ] Design event batching and flushing mechanisms
- [ ] Implement event filtering and routing capabilities
- [ ] Design event persistence and retrieval interfaces
- [ ] Document event schema and metadata standards

### 3.3 Document Logging Infrastructure and Structured Output

- [ ] Design unified logging system for both modes
- [ ] Define structured log formats and schemas
- [ ] Implement log level management and filtering
- [ ] Design log aggregation and shipping mechanisms
- [ ] Document logging best practices and standards

### 3.4 Define Health Check Endpoints and Monitoring

- [ ] Design health check interface for both modes
- [ ] Define health check categories and thresholds
- [ ] Implement health check aggregation and reporting
- [ ] Design monitoring metrics collection interfaces
- [ ] Document health check standards and conventions

### 3.5 Design Error Handling and Recovery Patterns

- [ ] Define error classification and severity levels
- [ ] Design error reporting and notification mechanisms
- [ ] Implement retry patterns with exponential backoff
- [ ] Design circuit breaker patterns for external services
- [ ] Document error handling best practices

## Implementation Details

### 3.1 Shared Configuration Management

```typescript
// packages/shared/src/config/types.ts

export interface ITammaConfig {
  mode: 'orchestrator' | 'worker' | 'standalone';
  logging: LoggingConfig;
  monitoring: MonitoringConfig;
  security: SecurityConfig;
  database?: DatabaseConfig;
  orchestrator?: OrchestratorConfig;
  worker?: WorkerConfig;
  providers: ProviderConfig[];
  platforms: PlatformConfig[];
}

export interface LoggingConfig {
  level: 'trace' | 'debug' | 'info' | 'warn' | 'error';
  format: 'json' | 'text';
  outputs: LogOutput[];
  structured: boolean;
  redaction: RedactionConfig;
  rotation: LogRotationConfig;
}

export interface LogOutput {
  type: 'stdout' | 'file' | 'http' | 'syslog';
  config: Record<string, unknown>;
}

export interface RedactionConfig {
  enabled: boolean;
  patterns: string[];
  customRedactors: CustomRedactor[];
}

export interface CustomRedactor {
  name: string;
  pattern: RegExp;
  replacement: string;
}

export interface LogRotationConfig {
  enabled: boolean;
  maxSize: string;
  maxFiles: number;
  compress: boolean;
}

export interface MonitoringConfig {
  enabled: boolean;
  metrics: MetricsConfig;
  healthChecks: HealthCheckConfig;
  tracing: TracingConfig;
}

export interface MetricsConfig {
  enabled: boolean;
  endpoint: string;
  interval: number;
  format: 'prometheus' | 'json';
  labels: Record<string, string>;
}

export interface HealthCheckConfig {
  enabled: boolean;
  endpoint: string;
  interval: number;
  timeout: number;
  checks: HealthCheckDefinition[];
}

export interface HealthCheckDefinition {
  name: string;
  type: 'http' | 'tcp' | 'database' | 'custom';
  config: Record<string, unknown>;
  timeout: number;
  interval: number;
  critical: boolean;
}

export interface TracingConfig {
  enabled: boolean;
  endpoint: string;
  sampleRate: number;
  headers: Record<string, string>;
}

export interface SecurityConfig {
  encryption: EncryptionConfig;
  authentication: AuthenticationConfig;
  authorization: AuthorizationConfig;
  audit: AuditConfig;
}

export interface EncryptionConfig {
  enabled: boolean;
  algorithm: string;
  keySource: 'environment' | 'file' | 'kms';
  keyRotation: KeyRotationConfig;
}

export interface KeyRotationConfig {
  enabled: boolean;
  interval: number;
  retention: number;
}

export interface AuthenticationConfig {
  enabled: boolean;
  methods: AuthMethod[];
  jwt: JwtConfig;
  oauth: OAuthConfig;
}

export interface AuthMethod {
  type: 'jwt' | 'oauth' | 'api_key' | 'basic';
  config: Record<string, unknown>;
}

export interface JwtConfig {
  secret: string;
  expiresIn: string;
  issuer: string;
  audience: string;
}

export interface OAuthConfig {
  providers: OAuthProvider[];
}

export interface OAuthProvider {
  name: string;
  clientId: string;
  clientSecret: string;
  redirectUri: string;
  scopes: string[];
}

export interface AuthorizationConfig {
  enabled: boolean;
  rbac: RbacConfig;
  abac: AbacConfig;
}

export interface RbacConfig {
  enabled: boolean;
  roles: Role[];
  permissions: Permission[];
}

export interface Role {
  name: string;
  permissions: string[];
}

export interface Permission {
  name: string;
  resource: string;
  actions: string[];
}

export interface AbacConfig {
  enabled: boolean;
  policies: Policy[];
}

export interface Policy {
  name: string;
  effect: 'allow' | 'deny';
  conditions: Condition[];
}

export interface Condition {
  attribute: string;
  operator: string;
  value: unknown;
}

export interface AuditConfig {
  enabled: boolean;
  events: string[];
  retention: number;
  format: 'json' | 'csv';
  storage: AuditStorageConfig;
}

export interface AuditStorageConfig {
  type: 'file' | 'database' | 's3';
  config: Record<string, unknown>;
}
```

```typescript
// packages/shared/src/config/config-manager.ts

export class ConfigManager implements IConfigManager {
  private config: ITammaConfig;
  private watchers: Map<string, ConfigWatcher[]> = new Map();
  private validators: ConfigValidator[] = [];
  private logger: ILogger;

  constructor(logger: ILogger) {
    this.logger = logger;
  }

  async initialize(configPath?: string): Promise<void> {
    // Load configuration from multiple sources
    const sources = await this.loadConfigSources(configPath);

    // Merge configurations with precedence
    this.config = this.mergeConfigurations(sources);

    // Validate configuration
    await this.validateConfiguration(this.config);

    // Apply environment variable overrides
    this.applyEnvironmentOverrides(this.config);

    // Setup file watching for hot reload
    if (configPath) {
      await this.setupConfigWatching(configPath);
    }

    this.logger.info('Configuration initialized', {
      mode: this.config.mode,
      sources: sources.length,
    });
  }

  get<T = unknown>(path: string, defaultValue?: T): T {
    return this.getNestedValue(this.config, path, defaultValue);
  }

  set(path: string, value: unknown): void {
    this.setNestedValue(this.config, path, value);
    this.notifyWatchers(path, value);
  }

  watch(path: string, callback: ConfigChangeCallback): () => void {
    const watcher: ConfigWatcher = { path, callback };

    if (!this.watchers.has(path)) {
      this.watchers.set(path, []);
    }

    this.watchers.get(path)!.push(watcher);

    // Return unsubscribe function
    return () => {
      const watchers = this.watchers.get(path);
      if (watchers) {
        const index = watchers.indexOf(watcher);
        if (index > -1) {
          watchers.splice(index, 1);
        }
      }
    };
  }

  addValidator(validator: ConfigValidator): void {
    this.validators.push(validator);
  }

  async reload(): Promise<void> {
    this.logger.info('Reloading configuration...');

    const oldConfig = { ...this.config };

    try {
      await this.initialize();
      this.logger.info('Configuration reloaded successfully');
    } catch (error) {
      this.config = oldConfig; // Restore old config
      this.logger.error('Configuration reload failed, restored previous config', { error });
      throw error;
    }
  }

  export(): string {
    return JSON.stringify(this.config, null, 2);
  }

  private async loadConfigSources(configPath?: string): Promise<ConfigSource[]> {
    const sources: ConfigSource[] = [];

    // 1. Default configuration
    sources.push({
      name: 'default',
      priority: 0,
      config: this.getDefaultConfig(),
    });

    // 2. Configuration file
    if (configPath) {
      try {
        const fileConfig = await this.loadConfigFile(configPath);
        sources.push({
          name: 'file',
          priority: 100,
          config: fileConfig,
        });
      } catch (error) {
        this.logger.warn('Failed to load config file', { configPath, error });
      }
    }

    // 3. Environment variables
    const envConfig = this.loadEnvironmentConfig();
    if (Object.keys(envConfig).length > 0) {
      sources.push({
        name: 'environment',
        priority: 200,
        config: envConfig,
      });
    }

    // 4. Command line arguments
    const cliConfig = this.loadCliConfig();
    if (Object.keys(cliConfig).length > 0) {
      sources.push({
        name: 'cli',
        priority: 300,
        config: cliConfig,
      });
    }

    return sources.sort((a, b) => a.priority - b.priority);
  }

  private mergeConfigurations(sources: ConfigSource[]): ITammaConfig {
    let merged = {} as ITammaConfig;

    for (const source of sources) {
      merged = this.deepMerge(merged, source.config);
    }

    return merged;
  }

  private async validateConfiguration(config: ITammaConfig): Promise<void> {
    for (const validator of this.validators) {
      const result = await validator.validate(config);

      if (!result.valid) {
        throw new Error(`Configuration validation failed: ${result.errors.join(', ')}`);
      }
    }
  }

  private applyEnvironmentOverrides(config: ITammaConfig): void {
    const overrides = this.getEnvironmentOverrides();

    for (const [path, value] of Object.entries(overrides)) {
      this.setNestedValue(config, path, value);
    }
  }

  private async setupConfigWatching(configPath: string): Promise<void> {
    const chokidar = require('chokidar');

    const watcher = chokidar.watch(configPath);

    watcher.on('change', async () => {
      this.logger.info('Configuration file changed, reloading...');
      try {
        await this.reload();
      } catch (error) {
        this.logger.error('Failed to reload configuration after file change', { error });
      }
    });
  }

  private getDefaultConfig(): ITammaConfig {
    return {
      mode: 'standalone',
      logging: {
        level: 'info',
        format: 'json',
        outputs: [{ type: 'stdout', config: {} }],
        structured: true,
        redaction: {
          enabled: true,
          patterns: ['password', 'token', 'secret', 'key'],
          customRedactors: [],
        },
        rotation: {
          enabled: false,
          maxSize: '100MB',
          maxFiles: 10,
          compress: true,
        },
      },
      monitoring: {
        enabled: true,
        metrics: {
          enabled: true,
          endpoint: '/metrics',
          interval: 30000,
          format: 'prometheus',
          labels: {},
        },
        healthChecks: {
          enabled: true,
          endpoint: '/health',
          interval: 30000,
          timeout: 5000,
          checks: [],
        },
        tracing: {
          enabled: false,
          endpoint: '',
          sampleRate: 0.1,
          headers: {},
        },
      },
      security: {
        encryption: {
          enabled: false,
          algorithm: 'aes-256-gcm',
          keySource: 'environment',
          keyRotation: {
            enabled: false,
            interval: 86400000, // 24 hours
            retention: 7, // days
          },
        },
        authentication: {
          enabled: false,
          methods: [],
          jwt: {
            secret: '',
            expiresIn: '1h',
            issuer: 'tamma',
            audience: 'tamma',
          },
          oauth: {
            providers: [],
          },
        },
        authorization: {
          enabled: false,
          rbac: {
            enabled: false,
            roles: [],
            permissions: [],
          },
          abac: {
            enabled: false,
            policies: [],
          },
        },
        audit: {
          enabled: true,
          events: ['*'],
          retention: 30, // days
          format: 'json',
          storage: {
            type: 'file',
            config: {
              path: '/var/log/tamma/audit.log',
            },
          },
        },
      },
      providers: [],
      platforms: [],
    };
  }

  private deepMerge(target: any, source: any): any {
    const result = { ...target };

    for (const key in source) {
      if (source[key] && typeof source[key] === 'object' && !Array.isArray(source[key])) {
        result[key] = this.deepMerge(result[key] || {}, source[key]);
      } else {
        result[key] = source[key];
      }
    }

    return result;
  }

  private getNestedValue(obj: any, path: string, defaultValue?: unknown): unknown {
    const keys = path.split('.');
    let current = obj;

    for (const key of keys) {
      if (current && typeof current === 'object' && key in current) {
        current = current[key];
      } else {
        return defaultValue;
      }
    }

    return current;
  }

  private setNestedValue(obj: any, path: string, value: unknown): void {
    const keys = path.split('.');
    let current = obj;

    for (let i = 0; i < keys.length - 1; i++) {
      const key = keys[i];
      if (!current[key] || typeof current[key] !== 'object') {
        current[key] = {};
      }
      current = current[key];
    }

    current[keys[keys.length - 1]] = value;
  }

  private notifyWatchers(path: string, value: unknown): void {
    const watchers = this.watchers.get(path);
    if (watchers) {
      for (const watcher of watchers) {
        try {
          watcher.callback(path, value);
        } catch (error) {
          this.logger.error('Config watcher error', { path, error });
        }
      }
    }
  }
}
```

### 3.2 Event Emission and Audit Trail Integration

```typescript
// packages/shared/src/events/types.ts

export interface IEventEmitter {
  initialize(config: EventConfig): Promise<void>;
  emit(event: DomainEvent): Promise<void>;
  emitBatch(events: DomainEvent[]): Promise<void>;
  subscribe(pattern: string, handler: EventHandler): () => void;
  unsubscribe(pattern: string, handler: EventHandler): void;
  getEvents(filter?: EventFilter): Promise<DomainEvent[]>;
  getEvent(eventId: string): Promise<DomainEvent | null>;
  healthCheck(): Promise<HealthCheckResult>;
  dispose(): Promise<void>;
}

export interface DomainEvent {
  id: string;
  type: string;
  timestamp: string;
  tags: Record<string, string | undefined>;
  metadata: EventMetadata;
  data: Record<string, unknown>;
}

export interface EventMetadata {
  workflowVersion: string;
  eventSource: 'system' | 'user' | 'plugin';
  userId?: string;
  sessionId?: string;
  requestId?: string;
  correlationId?: string;
  causationId?: string;
}

export interface EventFilter {
  type?: string;
  typePattern?: string;
  tags?: Record<string, string>;
  dateRange?: {
    start: string;
    end: string;
  };
  limit?: number;
  offset?: number;
}

export interface EventHandler {
  (event: DomainEvent): Promise<void> | void;
}

export interface EventConfig {
  batchSize: number;
  flushInterval: number;
  retentionDays: number;
  storage: EventStorageConfig;
  redaction: EventRedactionConfig;
}

export interface EventStorageConfig {
  type: 'database' | 'file' | 'kafka' | 's3';
  config: Record<string, unknown>;
}

export interface EventRedactionConfig {
  enabled: boolean;
  fields: string[];
  patterns: RedactionPattern[];
}

export interface RedactionPattern {
  field: string;
  pattern: RegExp;
  replacement: string;
}
```

```typescript
// packages/shared/src/events/event-emitter.ts

export class EventEmitter implements IEventEmitter {
  private config: EventConfig;
  private storage: IEventStorage;
  private subscribers: Map<string, EventHandler[]> = new Map();
  private batchBuffer: DomainEvent[] = [];
  private flushTimer: NodeJS.Timeout | null = null;
  private logger: ILogger;
  private disposed: boolean = false;

  constructor(storage: IEventStorage, logger: ILogger) {
    this.storage = storage;
    this.logger = logger;
  }

  async initialize(config: EventConfig): Promise<void> {
    this.config = config;

    // Initialize storage
    await this.storage.initialize(config.storage);

    // Setup batch flushing
    this.setupBatchFlushing();

    // Setup retention cleanup
    this.setupRetentionCleanup();

    this.logger.info('Event emitter initialized', {
      batchSize: config.batchSize,
      flushInterval: config.flushInterval,
      retentionDays: config.retentionDays,
    });
  }

  async emit(event: DomainEvent): Promise<void> {
    if (this.disposed) {
      throw new Error('Event emitter has been disposed');
    }

    // Validate event
    this.validateEvent(event);

    // Apply redaction
    const redactedEvent = this.applyRedaction(event);

    // Add to batch buffer
    this.batchBuffer.push(redactedEvent);

    // Check if batch should be flushed
    if (this.batchBuffer.length >= this.config.batchSize) {
      await this.flushBatch();
    }

    // Notify subscribers
    await this.notifySubscribers(redactedEvent);

    this.logger.debug('Event emitted', {
      eventId: event.id,
      type: event.type,
      tags: event.tags,
    });
  }

  async emitBatch(events: DomainEvent[]): Promise<void> {
    if (this.disposed) {
      throw new Error('Event emitter has been disposed');
    }

    // Validate all events
    for (const event of events) {
      this.validateEvent(event);
    }

    // Apply redaction to all events
    const redactedEvents = events.map((event) => this.applyRedaction(event));

    // Add to batch buffer
    this.batchBuffer.push(...redactedEvents);

    // Flush immediately for batch emit
    await this.flushBatch();

    // Notify subscribers for each event
    for (const event of redactedEvents) {
      await this.notifySubscribers(event);
    }

    this.logger.info('Batch of events emitted', {
      count: events.length,
      types: events.map((e) => e.type),
    });
  }

  subscribe(pattern: string, handler: EventHandler): () => void {
    if (!this.subscribers.has(pattern)) {
      this.subscribers.set(pattern, []);
    }

    this.subscribers.get(pattern)!.push(handler);

    // Return unsubscribe function
    return () => {
      const handlers = this.subscribers.get(pattern);
      if (handlers) {
        const index = handlers.indexOf(handler);
        if (index > -1) {
          handlers.splice(index, 1);
        }
      }
    };
  }

  unsubscribe(pattern: string, handler: EventHandler): void {
    const handlers = this.subscribers.get(pattern);
    if (handlers) {
      const index = handlers.indexOf(handler);
      if (index > -1) {
        handlers.splice(index, 1);
      }
    }
  }

  async getEvents(filter?: EventFilter): Promise<DomainEvent[]> {
    return await this.storage.getEvents(filter);
  }

  async getEvent(eventId: string): Promise<DomainEvent | null> {
    return await this.storage.getEvent(eventId);
  }

  async healthCheck(): Promise<HealthCheckResult> {
    try {
      // Test storage
      const storageHealth = await this.storage.healthCheck();

      // Test batch processing
      const testEvent = this.createTestEvent();
      await this.emit(testEvent);

      // Retrieve test event
      const retrieved = await this.getEvent(testEvent.id);

      return {
        healthy: storageHealth.healthy && !!retrieved,
        details: {
          storage: storageHealth,
          batchBuffer: this.batchBuffer.length,
          subscribers: this.subscribers.size,
        },
      };
    } catch (error) {
      return {
        healthy: false,
        details: { error: error.message },
      };
    }
  }

  async dispose(): Promise<void> {
    this.disposed = true;

    // Flush remaining events
    if (this.batchBuffer.length > 0) {
      await this.flushBatch();
    }

    // Clear flush timer
    if (this.flushTimer) {
      clearTimeout(this.flushTimer);
      this.flushTimer = null;
    }

    // Dispose storage
    await this.storage.dispose();

    // Clear subscribers
    this.subscribers.clear();

    this.logger.info('Event emitter disposed');
  }

  private validateEvent(event: DomainEvent): void {
    if (!event.id) {
      throw new Error('Event ID is required');
    }

    if (!event.type) {
      throw new Error('Event type is required');
    }

    if (!event.timestamp) {
      throw new Error('Event timestamp is required');
    }

    if (!event.metadata) {
      throw new Error('Event metadata is required');
    }

    // Validate timestamp format
    const timestamp = new Date(event.timestamp);
    if (isNaN(timestamp.getTime())) {
      throw new Error('Event timestamp must be a valid ISO 8601 date');
    }
  }

  private applyRedaction(event: DomainEvent): DomainEvent {
    if (!this.config.redaction.enabled) {
      return event;
    }

    const redactedEvent = JSON.parse(JSON.stringify(event));

    // Redact specific fields
    for (const field of this.config.redaction.fields) {
      this.redactField(redactedEvent, field);
    }

    // Apply pattern-based redaction
    for (const pattern of this.config.redaction.patterns) {
      this.redactByPattern(redactedEvent, pattern);
    }

    return redactedEvent;
  }

  private redactField(obj: any, field: string): void {
    const keys = field.split('.');
    let current = obj;

    for (let i = 0; i < keys.length - 1; i++) {
      if (current && typeof current === 'object' && keys[i] in current) {
        current = current[keys[i]];
      } else {
        return;
      }
    }

    if (current && typeof current === 'object' && keys[keys.length - 1] in current) {
      current[keys[keys.length - 1]] = '[REDACTED]';
    }
  }

  private redactByPattern(obj: any, pattern: RedactionPattern): void {
    const json = JSON.stringify(obj);
    const redacted = json.replace(pattern.pattern, pattern.replacement);
    Object.assign(obj, JSON.parse(redacted));
  }

  private async notifySubscribers(event: DomainEvent): Promise<void> {
    const promises: Promise<void>[] = [];

    for (const [pattern, handlers] of this.subscribers) {
      if (this.matchesPattern(event.type, pattern)) {
        for (const handler of handlers) {
          promises.push(Promise.resolve(handler(event)));
        }
      }
    }

    // Execute all handlers concurrently
    await Promise.allSettled(promises);
  }

  private matchesPattern(eventType: string, pattern: string): boolean {
    if (pattern === '*') {
      return true;
    }

    if (pattern.includes('*')) {
      const regex = new RegExp(pattern.replace(/\*/g, '.*'));
      return regex.test(eventType);
    }

    return eventType === pattern;
  }

  private setupBatchFlushing(): void {
    this.flushTimer = setInterval(async () => {
      if (this.batchBuffer.length > 0) {
        await this.flushBatch();
      }
    }, this.config.flushInterval);
  }

  private async flushBatch(): Promise<void> {
    if (this.batchBuffer.length === 0) {
      return;
    }

    const batch = [...this.batchBuffer];
    this.batchBuffer = [];

    try {
      await this.storage.storeEvents(batch);

      this.logger.debug('Event batch flushed', {
        count: batch.length,
        types: batch.map((e) => e.type),
      });
    } catch (error) {
      this.logger.error('Failed to flush event batch', {
        error,
        count: batch.length,
      });

      // Re-add events to buffer for retry
      this.batchBuffer.unshift(...batch);
    }
  }

  private setupRetentionCleanup(): void {
    // Run cleanup daily
    setInterval(
      async () => {
        try {
          const cutoffDate = new Date();
          cutoffDate.setDate(cutoffDate.getDate() - this.config.retentionDays);

          await this.storage.deleteEventsBefore(cutoffDate.toISOString());

          this.logger.info('Event retention cleanup completed', {
            cutoffDate: cutoffDate.toISOString(),
            retentionDays: this.config.retentionDays,
          });
        } catch (error) {
          this.logger.error('Event retention cleanup failed', { error });
        }
      },
      24 * 60 * 60 * 1000
    ); // 24 hours
  }

  private createTestEvent(): DomainEvent {
    return {
      id: generateUUID(),
      type: 'SYSTEM.HEALTH_CHECK.TEST',
      timestamp: new Date().toISOString(),
      tags: {},
      metadata: {
        workflowVersion: '1.0.0',
        eventSource: 'system',
      },
      data: {
        test: true,
        timestamp: new Date().toISOString(),
      },
    };
  }
}
```

### 3.3 Logging Infrastructure and Structured Output

```typescript
// packages/shared/src/logging/types.ts

export interface ILogger {
  initialize(config: LoggingConfig): Promise<void>;
  trace(message: string, meta?: LogMeta): void;
  debug(message: string, meta?: LogMeta): void;
  info(message: string, meta?: LogMeta): void;
  warn(message: string, meta?: LogMeta): void;
  error(message: string, error?: Error, meta?: LogMeta): void;
  child(meta: LogMeta): ILogger;
  healthCheck(): Promise<HealthCheckResult>;
  dispose(): Promise<void>;
}

export interface LogMeta {
  [key: string]: unknown;
  requestId?: string;
  userId?: string;
  sessionId?: string;
  workflowId?: string;
  taskId?: string;
  [key: string]: unknown;
}

export interface LogEntry {
  level: LogLevel;
  message: string;
  timestamp: string;
  meta: LogMeta;
  error?: ErrorInfo;
  source: LogSource;
}

export interface ErrorInfo {
  name: string;
  message: string;
  stack?: string;
  code?: string;
  [key: string]: unknown;
}

export interface LogSource {
  service: string;
  version: string;
  hostname: string;
  pid: number;
}

export type LogLevel = 'trace' | 'debug' | 'info' | 'warn' | 'error';

export interface LogOutput {
  name: string;
  write(entry: LogEntry): Promise<void>;
  healthCheck(): Promise<HealthCheckResult>;
  dispose(): Promise<void>;
}
```

```typescript
// packages/shared/src/logging/logger.ts

export class Logger implements ILogger {
  private config: LoggingConfig;
  private outputs: LogOutput[] = [];
  private redactors: LogRedactor[] = [];
  private baseMeta: LogMeta;
  private source: LogSource;
  private disposed: boolean = false;

  constructor(baseMeta: LogMeta = {}) {
    this.baseMeta = baseMeta;
    this.source = {
      service: 'tamma',
      version: '1.0.0',
      hostname: require('os').hostname(),
      pid: process.pid,
    };
  }

  async initialize(config: LoggingConfig): Promise<void> {
    this.config = config;

    // Initialize outputs
    await this.initializeOutputs();

    // Initialize redactors
    await this.initializeRedactors();

    // Set log level
    this.setLogLevel(config.level);

    this.info('Logger initialized', {
      level: config.level,
      format: config.format,
      outputs: config.outputs.length,
    });
  }

  trace(message: string, meta: LogMeta = {}): void {
    this.log('trace', message, meta);
  }

  debug(message: string, meta: LogMeta = {}): void {
    this.log('debug', message, meta);
  }

  info(message: string, meta: LogMeta = {}): void {
    this.log('info', message, meta);
  }

  warn(message: string, meta: LogMeta = {}): void {
    this.log('warn', message, meta);
  }

  error(message: string, error?: Error, meta: LogMeta = {}): void {
    const errorInfo = error ? this.serializeError(error) : undefined;
    this.log('error', message, meta, errorInfo);
  }

  child(meta: LogMeta): ILogger {
    return new Logger({ ...this.baseMeta, ...meta });
  }

  async healthCheck(): Promise<HealthCheckResult> {
    try {
      const results = await Promise.allSettled(this.outputs.map((output) => output.healthCheck()));

      const healthy = results.every(
        (result) => result.status === 'fulfilled' && result.value.healthy
      );

      return {
        healthy,
        details: {
          outputs: this.outputs.length,
          results: results.map((result, index) => ({
            output: this.outputs[index].name,
            status: result.status,
            details: result.status === 'fulfilled' ? result.value : result.reason,
          })),
        },
      };
    } catch (error) {
      return {
        healthy: false,
        details: { error: error.message },
      };
    }
  }

  async dispose(): Promise<void> {
    this.disposed = true;

    // Dispose all outputs
    await Promise.allSettled(this.outputs.map((output) => output.dispose()));

    this.outputs = [];
    this.info('Logger disposed');
  }

  private log(level: LogLevel, message: string, meta: LogMeta = {}, error?: ErrorInfo): void {
    if (this.disposed) {
      return;
    }

    // Check log level
    if (!this.shouldLog(level)) {
      return;
    }

    // Create log entry
    const entry: LogEntry = {
      level,
      message,
      timestamp: new Date().toISOString(),
      meta: { ...this.baseMeta, ...meta },
      error,
      source: this.source,
    };

    // Apply redaction
    const redactedEntry = this.applyRedaction(entry);

    // Format and write to outputs
    this.writeToOutputs(redactedEntry);
  }

  private shouldLog(level: LogLevel): boolean {
    const levels = ['trace', 'debug', 'info', 'warn', 'error'];
    const currentLevelIndex = levels.indexOf(this.config.level);
    const messageLevelIndex = levels.indexOf(level);

    return messageLevelIndex >= currentLevelIndex;
  }

  private async initializeOutputs(): Promise<void> {
    for (const outputConfig of this.config.outputs) {
      try {
        const output = await this.createOutput(outputConfig);
        this.outputs.push(output);
      } catch (error) {
        console.error(`Failed to initialize log output ${outputConfig.type}:`, error);
      }
    }
  }

  private async createOutput(config: LogOutput): Promise<LogOutput> {
    switch (config.type) {
      case 'stdout':
        return new StdoutOutput(config.config);
      case 'file':
        return new FileOutput(config.config);
      case 'http':
        return new HttpOutput(config.config);
      case 'syslog':
        return new SyslogOutput(config.config);
      default:
        throw new Error(`Unknown log output type: ${config.type}`);
    }
  }

  private async initializeRedactors(): Promise<void> {
    if (!this.config.redaction.enabled) {
      return;
    }

    // Add default redactors
    for (const pattern of this.config.redaction.patterns) {
      this.redactors.push(new PatternRedactor(pattern));
    }

    // Add custom redactors
    for (const custom of this.config.redaction.customRedactors) {
      this.redactors.push(new CustomRedactor(custom));
    }
  }

  private applyRedaction(entry: LogEntry): LogEntry {
    let redacted = JSON.parse(JSON.stringify(entry));

    for (const redactor of this.redactors) {
      redacted = redactor.redact(redacted);
    }

    return redacted;
  }

  private writeToOutputs(entry: LogEntry): void {
    for (const output of this.outputs) {
      // Write asynchronously to avoid blocking
      setImmediate(async () => {
        try {
          await output.write(entry);
        } catch (error) {
          console.error(`Failed to write to log output ${output.name}:`, error);
        }
      });
    }
  }

  private serializeError(error: Error): ErrorInfo {
    return {
      name: error.name,
      message: error.message,
      stack: error.stack,
      code: (error as any).code,
    };
  }

  private setLogLevel(level: LogLevel): void {
    // Set process.env.LOG_LEVEL for compatibility with external libraries
    process.env.LOG_LEVEL = level;
  }
}
```

### 3.4 Health Check Endpoints and Monitoring

```typescript
// packages/shared/src/health/types.ts

export interface IHealthChecker {
  initialize(config: HealthCheckConfig): Promise<void>;
  addCheck(check: HealthCheck): void;
  removeCheck(name: string): void;
  checkHealth(): Promise<HealthStatus>;
  startMonitoring(): Promise<void>;
  stopMonitoring(): Promise<void>;
  healthCheck(): Promise<HealthCheckResult>;
}

export interface HealthCheck {
  name: string;
  check: () => Promise<HealthCheckResult>;
  interval?: number;
  timeout?: number;
  critical?: boolean;
}

export interface HealthStatus {
  status: 'healthy' | 'unhealthy' | 'degraded';
  timestamp: string;
  uptime: number;
  version: string;
  checks: HealthCheckResult[];
  summary?: HealthSummary;
}

export interface HealthCheckResult {
  name: string;
  status: 'healthy' | 'unhealthy';
  details?: Record<string, unknown>;
  duration?: number;
  error?: string;
}

export interface HealthSummary {
  total: number;
  healthy: number;
  unhealthy: number;
  critical: number;
}

export interface HealthCheckConfig {
  enabled: boolean;
  endpoint: string;
  interval: number;
  timeout: number;
  checks: HealthCheckDefinition[];
}
```

```typescript
// packages/shared/src/health/health-checker.ts

export class HealthChecker implements IHealthChecker {
  private config: HealthCheckConfig;
  private checks: Map<string, HealthCheck> = new Map();
  private monitoringInterval: NodeJS.Timeout | null = null;
  private lastResults: Map<string, HealthCheckResult> = new Map();
  private logger: ILogger;

  constructor(logger: ILogger) {
    this.logger = logger;
  }

  async initialize(config: HealthCheckConfig): Promise<void> {
    this.config = config;

    // Add default checks
    this.addDefaultChecks();

    // Add configured checks
    for (const checkDef of config.checks) {
      await this.addCheckFromDefinition(checkDef);
    }

    this.logger.info('Health checker initialized', {
      checks: this.checks.size,
      interval: config.interval,
    });
  }

  addCheck(check: HealthCheck): void {
    this.checks.set(check.name, check);
    this.logger.debug('Health check added', { name: check.name });
  }

  removeCheck(name: string): void {
    this.checks.delete(name);
    this.lastResults.delete(name);
    this.logger.debug('Health check removed', { name });
  }

  async checkHealth(): Promise<HealthStatus> {
    const results: HealthCheckResult[] = [];
    const promises: Promise<HealthCheckResult>[] = [];

    // Execute all health checks
    for (const check of this.checks.values()) {
      promises.push(this.executeCheck(check));
    }

    const checkResults = await Promise.allSettled(promises);

    // Process results
    for (const result of checkResults) {
      if (result.status === 'fulfilled') {
        results.push(result.value);
        this.lastResults.set(result.value.name, result.value);
      } else {
        const errorResult: HealthCheckResult = {
          name: 'unknown',
          status: 'unhealthy',
          error: result.reason.message,
        };
        results.push(errorResult);
      }
    }

    // Calculate overall status
    const status = this.calculateOverallStatus(results);
    const summary = this.calculateSummary(results);

    return {
      status,
      timestamp: new Date().toISOString(),
      uptime: process.uptime(),
      version: '1.0.0',
      checks: results,
      summary,
    };
  }

  async startMonitoring(): Promise<void> {
    if (this.monitoringInterval) {
      return;
    }

    this.monitoringInterval = setInterval(async () => {
      try {
        const status = await this.checkHealth();
        this.logger.debug('Health check completed', {
          status: status.status,
          healthy: status.summary?.healthy || 0,
          unhealthy: status.summary?.unhealthy || 0,
        });
      } catch (error) {
        this.logger.error('Health check monitoring failed', { error });
      }
    }, this.config.interval);

    this.logger.info('Health monitoring started', {
      interval: this.config.interval,
    });
  }

  async stopMonitoring(): Promise<void> {
    if (this.monitoringInterval) {
      clearInterval(this.monitoringInterval);
      this.monitoringInterval = null;
    }

    this.logger.info('Health monitoring stopped');
  }

  async healthCheck(): Promise<HealthCheckResult> {
    try {
      const status = await this.checkHealth();
      return {
        name: 'health_checker',
        status: status.status === 'healthy' ? 'healthy' : 'unhealthy',
        details: {
          totalChecks: status.checks.length,
          healthyChecks: status.summary?.healthy || 0,
          unhealthyChecks: status.summary?.unhealthy || 0,
        },
      };
    } catch (error) {
      return {
        name: 'health_checker',
        status: 'unhealthy',
        error: error.message,
      };
    }
  }

  private async executeCheck(check: HealthCheck): Promise<HealthCheckResult> {
    const startTime = Date.now();

    try {
      const timeout = check.timeout || this.config.timeout;
      const result = await this.withTimeout(check.check(), timeout);

      return {
        name: check.name,
        status: result.status,
        details: result.details,
        duration: Date.now() - startTime,
        error: result.error,
      };
    } catch (error) {
      return {
        name: check.name,
        status: 'unhealthy',
        duration: Date.now() - startTime,
        error: error.message,
      };
    }
  }

  private async withTimeout<T>(promise: Promise<T>, timeoutMs: number): Promise<T> {
    const timeoutPromise = new Promise<never>((_, reject) => {
      setTimeout(() => reject(new Error(`Health check timed out after ${timeoutMs}ms`)), timeoutMs);
    });

    return Promise.race([promise, timeoutPromise]);
  }

  private calculateOverallStatus(
    results: HealthCheckResult[]
  ): 'healthy' | 'unhealthy' | 'degraded' {
    const healthy = results.filter((r) => r.status === 'healthy').length;
    const critical = results.filter((r) => {
      const check = this.checks.get(r.name);
      return check?.critical && r.status === 'unhealthy';
    }).length;

    if (critical > 0) {
      return 'unhealthy';
    }

    if (healthy === results.length) {
      return 'healthy';
    }

    return 'degraded';
  }

  private calculateSummary(results: HealthCheckResult[]): HealthSummary {
    const total = results.length;
    const healthy = results.filter((r) => r.status === 'healthy').length;
    const unhealthy = results.filter((r) => r.status === 'unhealthy').length;
    const critical = results.filter((r) => {
      const check = this.checks.get(r.name);
      return check?.critical && r.status === 'unhealthy';
    }).length;

    return { total, healthy, unhealthy, critical };
  }

  private addDefaultChecks(): void {
    // Memory check
    this.addCheck({
      name: 'memory',
      critical: true,
      check: async () => {
        const usage = process.memoryUsage();
        const totalMemory = require('os').totalmem();
        const usagePercent = (usage.heapUsed / totalMemory) * 100;

        if (usagePercent > 90) {
          return {
            name: 'memory',
            status: 'unhealthy',
            details: { usagePercent, heapUsed: usage.heapUsed },
          };
        }

        return {
          name: 'memory',
          status: 'healthy',
          details: { usagePercent, heapUsed: usage.heapUsed },
        };
      },
    });

    // CPU check
    this.addCheck({
      name: 'cpu',
      critical: false,
      check: async () => {
        const cpus = require('os').cpus();
        const loadAvg = require('os').loadavg();
        const cpuCount = cpus.length;
        const loadPercent = (loadAvg[0] / cpuCount) * 100;

        if (loadPercent > 95) {
          return {
            name: 'cpu',
            status: 'unhealthy',
            details: { loadPercent, loadAvg: loadAvg[0] },
          };
        }

        return {
          name: 'cpu',
          status: 'healthy',
          details: { loadPercent, loadAvg: loadAvg[0] },
        };
      },
    });

    // Disk space check
    this.addCheck({
      name: 'disk',
      critical: true,
      check: async () => {
        const fs = require('fs').promises;
        const stats = await fs.statfs('.');

        const total = stats.blocks * stats.bsize;
        const free = stats.bavail * stats.bsize;
        const usedPercent = ((total - free) / total) * 100;

        if (usedPercent > 95) {
          return {
            name: 'disk',
            status: 'unhealthy',
            details: { usedPercent, free, total },
          };
        }

        return {
          name: 'disk',
          status: 'healthy',
          details: { usedPercent, free, total },
        };
      },
    });
  }

  private async addCheckFromDefinition(definition: HealthCheckDefinition): Promise<void> {
    let check: HealthCheck;

    switch (definition.type) {
      case 'http':
        check = await this.createHttpCheck(definition);
        break;
      case 'tcp':
        check = await this.createTcpCheck(definition);
        break;
      case 'database':
        check = await this.createDatabaseCheck(definition);
        break;
      case 'custom':
        check = await this.createCustomCheck(definition);
        break;
      default:
        throw new Error(`Unknown health check type: ${definition.type}`);
    }

    this.addCheck(check);
  }

  private async createHttpCheck(definition: HealthCheckDefinition): Promise<HealthCheck> {
    const config = definition.config as HttpCheckConfig;

    return {
      name: definition.name,
      critical: definition.critical,
      timeout: definition.timeout,
      interval: definition.interval,
      check: async () => {
        const response = await fetch(config.url, {
          method: 'GET',
          timeout: definition.timeout || this.config.timeout,
        });

        if (response.ok) {
          return {
            name: definition.name,
            status: 'healthy',
            details: { statusCode: response.status, url: config.url },
          };
        }

        return {
          name: definition.name,
          status: 'unhealthy',
          details: { statusCode: response.status, url: config.url },
          error: `HTTP ${response.status}`,
        };
      },
    };
  }

  private async createTcpCheck(definition: HealthCheckDefinition): Promise<HealthCheck> {
    const config = definition.config as TcpCheckConfig;

    return {
      name: definition.name,
      critical: definition.critical,
      timeout: definition.timeout,
      interval: definition.interval,
      check: async () => {
        return new Promise((resolve) => {
          const net = require('net');
          const socket = new net.Socket();

          socket.setTimeout(definition.timeout || this.config.timeout);

          socket.on('connect', () => {
            socket.destroy();
            resolve({
              name: definition.name,
              status: 'healthy',
              details: { host: config.host, port: config.port },
            });
          });

          socket.on('error', (error: Error) => {
            resolve({
              name: definition.name,
              status: 'unhealthy',
              details: { host: config.host, port: config.port },
              error: error.message,
            });
          });

          socket.on('timeout', () => {
            socket.destroy();
            resolve({
              name: definition.name,
              status: 'unhealthy',
              details: { host: config.host, port: config.port },
              error: 'Connection timeout',
            });
          });

          socket.connect(config.port, config.host);
        });
      },
    };
  }

  private async createDatabaseCheck(definition: HealthCheckDefinition): Promise<HealthCheck> {
    const config = definition.config as DatabaseCheckConfig;

    return {
      name: definition.name,
      critical: definition.critical,
      timeout: definition.timeout,
      interval: definition.interval,
      check: async () => {
        // Implementation would depend on database type
        // This is a placeholder for PostgreSQL
        try {
          const { Pool } = require('pg');
          const pool = new Pool({
            host: config.host,
            port: config.port,
            database: config.database,
            user: config.user,
            password: config.password,
            connectionTimeoutMillis: definition.timeout || this.config.timeout,
          });

          const result = await pool.query('SELECT 1');
          await pool.end();

          return {
            name: definition.name,
            status: 'healthy',
            details: { host: config.host, database: config.database },
          };
        } catch (error) {
          return {
            name: definition.name,
            status: 'unhealthy',
            details: { host: config.host, database: config.database },
            error: error.message,
          };
        }
      },
    };
  }

  private async createCustomCheck(definition: HealthCheckDefinition): Promise<HealthCheck> {
    const config = definition.config as CustomCheckConfig;

    return {
      name: definition.name,
      critical: definition.critical,
      timeout: definition.timeout,
      interval: definition.interval,
      check: async () => {
        try {
          const result = await config.checkFunction();
          return {
            name: definition.name,
            status: result.healthy ? 'healthy' : 'unhealthy',
            details: result.details,
          };
        } catch (error) {
          return {
            name: definition.name,
            status: 'unhealthy',
            error: error.message,
          };
        }
      },
    };
  }
}
```

### 3.5 Error Handling and Recovery Patterns

```typescript
// packages/shared/src/errors/types.ts

export interface IErrorHandler {
  handleError(error: Error, context: ErrorContext): Promise<ErrorHandlingResult>;
  addHandler(pattern: ErrorPattern, handler: ErrorHandler): void;
  removeHandler(pattern: ErrorPattern): void;
  getClassification(error: Error): ErrorClassification;
}

export interface ErrorContext {
  component: string;
  operation: string;
  requestId?: string;
  userId?: string;
  metadata?: Record<string, unknown>;
}

export interface ErrorHandlingResult {
  handled: boolean;
  action: 'retry' | 'ignore' | 'escalate' | 'recover';
  retryDelay?: number;
  maxRetries?: number;
  escalationLevel?: 'low' | 'medium' | 'high' | 'critical';
  message?: string;
}

export interface ErrorPattern {
  name: string;
  type?: string;
  code?: string;
  message?: RegExp;
  severity?: ErrorSeverity;
}

export interface ErrorHandler {
  (error: Error, context: ErrorContext): Promise<ErrorHandlingResult>;
}

export interface ErrorClassification {
  severity: ErrorSeverity;
  category: ErrorCategory;
  retryable: boolean;
  userVisible: boolean;
  escalationRequired: boolean;
}

export type ErrorSeverity = 'low' | 'medium' | 'high' | 'critical';

export type ErrorCategory =
  | 'network'
  | 'authentication'
  | 'authorization'
  | 'validation'
  | 'business'
  | 'system'
  | 'timeout'
  | 'rate_limit'
  | 'resource_exhaustion'
  | 'configuration'
  | 'unknown';
```

```typescript
// packages/shared/src/errors/error-handler.ts

export class ErrorHandler implements IErrorHandler {
  private handlers: Map<string, ErrorHandler> = new Map();
  private classifications: Map<string, ErrorClassification> = new Map();
  private logger: ILogger;
  private circuitBreakers: Map<string, CircuitBreaker> = new Map();

  constructor(logger: ILogger) {
    this.logger = logger;
    this.initializeDefaultHandlers();
    this.initializeDefaultClassifications();
  }

  async handleError(error: Error, context: ErrorContext): Promise<ErrorHandlingResult> {
    // Classify error
    const classification = this.getClassification(error);

    // Log error
    this.logError(error, context, classification);

    // Check circuit breaker
    const circuitBreakerKey = `${context.component}.${context.operation}`;
    const circuitBreaker = this.circuitBreakers.get(circuitBreakerKey);

    if (circuitBreaker && circuitBreaker.isOpen()) {
      return {
        handled: true,
        action: 'escalate',
        escalationLevel: 'high',
        message: 'Circuit breaker is open',
      };
    }

    // Find matching handler
    const handler = this.findHandler(error);

    if (handler) {
      try {
        const result = await handler(error, context);

        // Update circuit breaker
        if (circuitBreaker) {
          if (result.action === 'retry' || result.action === 'recover') {
            circuitBreaker.recordSuccess();
          } else {
            circuitBreaker.recordFailure();
          }
        }

        return result;
      } catch (handlerError) {
        this.logger.error('Error handler failed', {
          originalError: error.message,
          handlerError: handlerError.message,
          context,
        });
      }
    }

    // Default handling
    return this.getDefaultHandling(error, context, classification);
  }

  addHandler(pattern: ErrorPattern, handler: ErrorHandler): void {
    const key = this.getPatternKey(pattern);
    this.handlers.set(key, handler);
    this.logger.debug('Error handler added', { pattern: key });
  }

  removeHandler(pattern: ErrorPattern): void {
    const key = this.getPatternKey(pattern);
    this.handlers.delete(key);
    this.logger.debug('Error handler removed', { pattern: key });
  }

  getClassification(error: Error): ErrorClassification {
    const key = this.getErrorKey(error);

    if (this.classifications.has(key)) {
      return this.classifications.get(key)!;
    }

    // Dynamic classification
    return this.classifyError(error);
  }

  private initializeDefaultHandlers(): void {
    // Network errors
    this.addHandler(
      {
        name: 'network_timeout',
        type: 'TimeoutError',
        severity: 'medium',
      },
      async (error, context) => ({
        handled: true,
        action: 'retry',
        retryDelay: 1000,
        maxRetries: 3,
      })
    );

    this.addHandler(
      {
        name: 'network_connection_refused',
        message: /ECONNREFUSED/,
      },
      async (error, context) => ({
        handled: true,
        action: 'retry',
        retryDelay: 5000,
        maxRetries: 2,
      })
    );

    // Rate limiting
    this.addHandler(
      {
        name: 'rate_limit',
        message: /rate.*limit/i,
      },
      async (error, context) => {
        const retryAfter = this.extractRetryAfter(error);
        return {
          handled: true,
          action: 'retry',
          retryDelay: retryAfter || 60000,
          maxRetries: 1,
        };
      }
    );

    // Authentication errors
    this.addHandler(
      {
        name: 'authentication_failed',
        message: /unauthorized|authentication.*failed/i,
      },
      async (error, context) => ({
        handled: true,
        action: 'escalate',
        escalationLevel: 'medium',
        message: 'Authentication failed - check credentials',
      })
    );

    // Validation errors
    this.addHandler(
      {
        name: 'validation_error',
        type: 'ValidationError',
      },
      async (error, context) => ({
        handled: true,
        action: 'ignore',
        message: 'Validation error - user input invalid',
      })
    );
  }

  private initializeDefaultClassifications(): void {
    // Network errors
    this.addClassification({
      pattern: { name: 'network_timeout' },
      classification: {
        severity: 'medium',
        category: 'timeout',
        retryable: true,
        userVisible: true,
        escalationRequired: false,
      },
    });

    this.addClassification({
      pattern: { name: 'rate_limit' },
      classification: {
        severity: 'medium',
        category: 'rate_limit',
        retryable: true,
        userVisible: true,
        escalationRequired: false,
      },
    });

    // Authentication errors
    this.addClassification({
      pattern: { name: 'authentication_failed' },
      classification: {
        severity: 'high',
        category: 'authentication',
        retryable: false,
        userVisible: true,
        escalationRequired: true,
      },
    });

    // System errors
    this.addClassification({
      pattern: { name: 'out_of_memory' },
      classification: {
        severity: 'critical',
        category: 'resource_exhaustion',
        retryable: false,
        userVisible: false,
        escalationRequired: true,
      },
    });
  }

  private findHandler(error: Error): ErrorHandler | null {
    // Find exact match
    const exactKey = this.getErrorKey(error);
    if (this.handlers.has(exactKey)) {
      return this.handlers.get(exactKey)!;
    }

    // Find pattern match
    for (const [key, handler] of this.handlers) {
      if (this.matchesPattern(error, key)) {
        return handler;
      }
    }

    return null;
  }

  private getDefaultHandling(
    error: Error,
    context: ErrorContext,
    classification: ErrorClassification
  ): ErrorHandlingResult {
    if (classification.retryable) {
      return {
        handled: true,
        action: 'retry',
        retryDelay: 1000,
        maxRetries: 3,
      };
    }

    if (classification.escalationRequired) {
      return {
        handled: true,
        action: 'escalate',
        escalationLevel: classification.severity as any,
      };
    }

    return {
      handled: true,
      action: 'ignore',
    };
  }

  private classifyError(error: Error): ErrorClassification {
    // Network errors
    if (error.name === 'TimeoutError' || error.message.includes('timeout')) {
      return {
        severity: 'medium',
        category: 'timeout',
        retryable: true,
        userVisible: true,
        escalationRequired: false,
      };
    }

    // Rate limiting
    if (error.message.match(/rate.*limit/i)) {
      return {
        severity: 'medium',
        category: 'rate_limit',
        retryable: true,
        userVisible: true,
        escalationRequired: false,
      };
    }

    // Authentication
    if (error.message.match(/unauthorized|authentication/i)) {
      return {
        severity: 'high',
        category: 'authentication',
        retryable: false,
        userVisible: true,
        escalationRequired: true,
      };
    }

    // Default classification
    return {
      severity: 'medium',
      category: 'unknown',
      retryable: false,
      userVisible: false,
      escalationRequired: false,
    };
  }

  private logError(error: Error, context: ErrorContext, classification: ErrorClassification): void {
    const logLevel = this.getLogLevel(classification);

    this.logger[logLevel]('Error occurred', {
      error: {
        name: error.name,
        message: error.message,
        stack: error.stack,
      },
      context,
      classification,
    });
  }

  private getLogLevel(classification: ErrorClassification): 'error' | 'warn' | 'info' {
    switch (classification.severity) {
      case 'critical':
      case 'high':
        return 'error';
      case 'medium':
        return 'warn';
      case 'low':
        return 'info';
      default:
        return 'error';
    }
  }

  private getPatternKey(pattern: ErrorPattern): string {
    return `${pattern.type || '*'}:${pattern.code || '*'}:${pattern.name}`;
  }

  private getErrorKey(error: Error): string {
    return `${error.constructor.name}:${(error as any).code || '*'}:${error.name}`;
  }

  private matchesPattern(error: Error, patternKey: string): boolean {
    const [type, code, name] = patternKey.split(':');

    if (type !== '*' && error.constructor.name !== type) {
      return false;
    }

    if (code !== '*' && (error as any).code !== code) {
      return false;
    }

    if (name !== '*' && error.name !== name) {
      return false;
    }

    return true;
  }

  private extractRetryAfter(error: Error): number | null {
    const match = error.message.match(/retry after (\d+)/i);
    return match ? parseInt(match[1]) * 1000 : null;
  }

  private addClassification(config: {
    pattern: ErrorPattern;
    classification: ErrorClassification;
  }): void {
    const key = this.getPatternKey(config.pattern);
    this.classifications.set(key, config.classification);
  }
}
```

## Testing Strategy

### Unit Tests

```typescript
// packages/shared/src/__tests__/config-manager.test.ts

describe('ConfigManager', () => {
  let configManager: ConfigManager;
  let mockLogger: ILogger;

  beforeEach(() => {
    mockLogger = createMockLogger();
    configManager = new ConfigManager(mockLogger);
  });

  describe('configuration loading', () => {
    it('should load configuration from multiple sources', async () => {
      const configPath = '/tmp/test-config.json';

      await configManager.initialize(configPath);

      expect(configManager.get('mode')).toBeDefined();
      expect(configManager.get('logging.level')).toBeDefined();
    });

    it('should apply environment variable overrides', async () => {
      process.env.TAMMA_LOG_LEVEL = 'debug';

      await configManager.initialize();

      expect(configManager.get('logging.level')).toBe('debug');

      delete process.env.TAMMA_LOG_LEVEL;
    });
  });

  describe('configuration watching', () => {
    it('should notify watchers on configuration changes', async () => {
      await configManager.initialize();

      const callback = jest.fn();
      configManager.watch('logging.level', callback);

      configManager.set('logging.level', 'warn');

      expect(callback).toHaveBeenCalledWith('logging.level', 'warn');
    });
  });
});
```

## Completion Checklist

- [ ] Define shared configuration management approach
- [ ] Design event emission and audit trail integration
- [ ] Document logging infrastructure and structured output
- [ ] Define health check endpoints and monitoring
- [ ] Design error handling and recovery patterns
- [ ] Create comprehensive TypeScript interfaces and types
- [ ] Implement shared components with all required methods
- [ ] Add comprehensive error handling and logging
- [ ] Create unit tests for all shared components
- [ ] Add integration tests for component interactions
- [ ] Document shared component usage patterns
- [ ] Create examples and best practices documentation

## Dependencies

- Task 1: Orchestrator Mode Architecture (for usage patterns)
- Task 2: Worker Mode Architecture (for usage patterns)
- Task 4: Sequence Diagrams and Workflows (for visual documentation)
- Task 5: State Persistence and Recovery Strategy (for data management)
- Task 6: Integration Points and APIs (for external interfaces)

## Estimated Time

**Configuration Management**: 3-4 days
**Event Emission and Audit Trail**: 4-5 days
**Logging Infrastructure**: 3-4 days
**Health Check and Monitoring**: 3-4 days
**Error Handling and Recovery**: 3-4 days
**Implementation and Testing**: 4-5 days
**Total**: 20-26 days
