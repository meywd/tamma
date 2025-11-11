# Task 8: Unified Error Handling and Retry Logic

## Overview

This task implements standardized error handling and retry logic across all AI providers in the Tamma platform. This ensures consistent error responses, intelligent retry strategies, and robust failure recovery mechanisms.

## Acceptance Criteria

### 8.1: Create standardized error classes for all providers

- [ ] Define base error class hierarchy for provider errors
- [ ] Implement provider-specific error classes
- [ ] Add error categorization and severity levels
- [ ] Implement error context and metadata
- [ ] Create error serialization and deserialization

### 8.2: Implement provider-specific retry strategies

- [ ] Create retry strategy interfaces and implementations
- [ ] Implement exponential backoff with jitter
- [ ] Add provider-specific retry configurations
- [ ] Implement retry condition evaluation
- [ ] Add retry budget and quota management

### 8.3: Add exponential backoff and jitter

- [ ] Implement configurable backoff algorithms
- [ ] Add jitter to prevent thundering herd
- [ ] Implement adaptive backoff based on error types
- [ ] Add maximum retry limits and timeouts
- [ ] Create backoff state management

### 8.4: Create circuit breaker pattern implementation

- [ ] Implement circuit breaker state machine
- [ ] Add failure threshold and recovery mechanisms
- [ ] Implement provider-specific circuit configurations
- [ ] Add circuit breaker metrics and monitoring
- [ ] Create circuit breaker event handling

### 8.5: Add error logging and monitoring

- [ ] Implement structured error logging
- [ ] Add error aggregation and analysis
- [ ] Create error alerting and notification
- [ ] Implement error trend analysis
- [ ] Add error reporting and dashboards

## Technical Implementation

### Error Class Hierarchy

```typescript
// src/core/errors/provider-errors.ts
export abstract class ProviderError extends Error {
  public readonly code: string;
  public readonly category: ErrorCategory;
  public readonly severity: ErrorSeverity;
  public readonly retryable: boolean;
  public readonly context: ProviderErrorContext;
  public readonly timestamp: Date;
  public readonly providerId: string;
  public readonly requestId?: string;

  constructor(
    code: string,
    message: string,
    category: ErrorCategory,
    severity: ErrorSeverity = 'medium',
    retryable: boolean = false,
    context: ProviderErrorContext = {},
    providerId: string,
    requestId?: string
  ) {
    super(message);
    this.name = this.constructor.name;
    this.code = code;
    this.category = category;
    this.severity = severity;
    this.retryable = retryable;
    this.context = context;
    this.timestamp = new Date();
    this.providerId = providerId;
    this.requestId = requestId;

    // Maintains proper stack trace for where our error was thrown
    Error.captureStackTrace(this, this.constructor);
  }

  toJSON(): ProviderErrorJSON {
    return {
      name: this.name,
      code: this.code,
      message: this.message,
      category: this.category,
      severity: this.severity,
      retryable: this.retryable,
      context: this.context,
      timestamp: this.timestamp.toISOString(),
      providerId: this.providerId,
      requestId: this.requestId,
      stack: this.stack,
    };
  }

  static fromJSON(json: ProviderErrorJSON): ProviderError {
    const ErrorClass = this.getErrorClass(json.code);
    return new ErrorClass(
      json.code,
      json.message,
      json.category,
      json.severity,
      json.retryable,
      json.context,
      json.providerId,
      json.requestId
    );
  }

  private static getErrorClass(code: string): typeof ProviderError {
    const errorClasses: Record<string, typeof ProviderError> = {
      AUTHENTICATION_FAILED: AuthenticationError,
      RATE_LIMIT_EXCEEDED: RateLimitError,
      QUOTA_EXCEEDED: QuotaError,
      MODEL_NOT_FOUND: ModelNotFoundError,
      TIMEOUT: TimeoutError,
      CONNECTION_FAILED: ConnectionError,
      SERVER_ERROR: ServerError,
      VALIDATION_FAILED: ValidationError,
      INSUFFICIENT_RESOURCES: ResourceError,
      CONTENT_BLOCKED: ContentBlockedError,
      TOOL_EXECUTION_FAILED: ToolExecutionError,
    };

    return errorClasses[code] || GenericProviderError;
  }
}

// Specific error classes
export class AuthenticationError extends ProviderError {
  constructor(providerId: string, message: string, context: ProviderErrorContext = {}) {
    super(
      'AUTHENTICATION_FAILED',
      message,
      'authentication',
      'high',
      false, // Authentication errors are typically not retryable without credential changes
      context,
      providerId
    );
  }
}

export class RateLimitError extends ProviderError {
  public readonly retryAfter?: number;
  public readonly limitType?: string;

  constructor(
    providerId: string,
    message: string,
    retryAfter?: number,
    limitType?: string,
    context: ProviderErrorContext = {}
  ) {
    super(
      'RATE_LIMIT_EXCEEDED',
      message,
      'rate-limit',
      'medium',
      true, // Rate limit errors are retryable
      { ...context, retryAfter, limitType },
      providerId
    );
    this.retryAfter = retryAfter;
    this.limitType = limitType;
  }
}

export class QuotaError extends ProviderError {
  public readonly quotaType?: string;
  public readonly currentUsage?: number;
  public readonly limit?: number;

  constructor(
    providerId: string,
    message: string,
    quotaType?: string,
    currentUsage?: number,
    limit?: number,
    context: ProviderErrorContext = {}
  ) {
    super(
      'QUOTA_EXCEEDED',
      message,
      'quota',
      'high',
      false, // Quota errors are not retryable without plan changes
      { ...context, quotaType, currentUsage, limit },
      providerId
    );
    this.quotaType = quotaType;
    this.currentUsage = currentUsage;
    this.limit = limit;
  }
}

export class ModelNotFoundError extends ProviderError {
  public readonly modelId: string;

  constructor(providerId: string, modelId: string, context: ProviderErrorContext = {}) {
    super(
      'MODEL_NOT_FOUND',
      `Model not found: ${modelId}`,
      'model',
      'medium',
      false, // Model not found is not retryable
      { ...context, modelId },
      providerId
    );
    this.modelId = modelId;
  }
}

export class TimeoutError extends ProviderError {
  public readonly timeout: number;
  public readonly operation: string;

  constructor(
    providerId: string,
    operation: string,
    timeout: number,
    context: ProviderErrorContext = {}
  ) {
    super(
      'TIMEOUT',
      `Operation ${operation} timed out after ${timeout}ms`,
      'timeout',
      'medium',
      true, // Timeouts are often retryable
      { ...context, operation, timeout },
      providerId
    );
    this.timeout = timeout;
    this.operation = operation;
  }
}

export class ConnectionError extends ProviderError {
  public readonly endpoint?: string;
  public readonly cause?: Error;

  constructor(
    providerId: string,
    message: string,
    endpoint?: string,
    cause?: Error,
    context: ProviderErrorContext = {}
  ) {
    super(
      'CONNECTION_FAILED',
      message,
      'connection',
      'high',
      true, // Connection errors are typically retryable
      { ...context, endpoint, originalError: cause?.message },
      providerId
    );
    this.endpoint = endpoint;
    this.cause = cause;
  }
}

export class ServerError extends ProviderError {
  public readonly statusCode?: number;
  public readonly responseHeaders?: Record<string, string>;

  constructor(
    providerId: string,
    message: string,
    statusCode?: number,
    responseHeaders?: Record<string, string>,
    context: ProviderErrorContext = {}
  ) {
    super(
      'SERVER_ERROR',
      message,
      'server',
      'high',
      true, // Server errors are often retryable
      { ...context, statusCode, responseHeaders },
      providerId
    );
    this.statusCode = statusCode;
    this.responseHeaders = responseHeaders;
  }
}

export class ValidationError extends ProviderError {
  public readonly field?: string;
  public readonly value?: any;

  constructor(
    providerId: string,
    message: string,
    field?: string,
    value?: any,
    context: ProviderErrorContext = {}
  ) {
    super(
      'VALIDATION_FAILED',
      message,
      'validation',
      'medium',
      false, // Validation errors are not retryable
      { ...context, field, value },
      providerId
    );
    this.field = field;
    this.value = value;
  }
}

export class ResourceError extends ProviderError {
  public readonly resourceType?: string;
  public readonly currentUsage?: number;
  public readonly limit?: number;

  constructor(
    providerId: string,
    message: string,
    resourceType?: string,
    currentUsage?: number,
    limit?: number,
    context: ProviderErrorContext = {}
  ) {
    super(
      'INSUFFICIENT_RESOURCES',
      message,
      'resource',
      'medium',
      true, // Resource errors might be retryable after waiting
      { ...context, resourceType, currentUsage, limit },
      providerId
    );
    this.resourceType = resourceType;
    this.currentUsage = currentUsage;
    this.limit = limit;
  }
}

export class ContentBlockedError extends ProviderError {
  public readonly blockReason?: string;
  public readonly category?: string;

  constructor(
    providerId: string,
    message: string,
    blockReason?: string,
    category?: string,
    context: ProviderErrorContext = {}
  ) {
    super(
      'CONTENT_BLOCKED',
      message,
      'content',
      'medium',
      false, // Content blocked is not retryable with same content
      { ...context, blockReason, category },
      providerId
    );
    this.blockReason = blockReason;
    this.category = category;
  }
}

export class ToolExecutionError extends ProviderError {
  public readonly toolName?: string;
  public readonly toolArguments?: any;

  constructor(
    providerId: string,
    message: string,
    toolName?: string,
    toolArguments?: any,
    context: ProviderErrorContext = {}
  ) {
    super(
      'TOOL_EXECUTION_FAILED',
      message,
      'tool',
      'medium',
      true, // Tool execution might be retryable
      { ...context, toolName, toolArguments },
      providerId
    );
    this.toolName = toolName;
    this.toolArguments = toolArguments;
  }
}

export class GenericProviderError extends ProviderError {
  constructor(providerId: string, message: string, context: ProviderErrorContext = {}) {
    super(
      'UNKNOWN_ERROR',
      message,
      'unknown',
      'medium',
      true, // Unknown errors are often retryable
      context,
      providerId
    );
  }
}

// Type definitions
export type ErrorCategory =
  | 'authentication'
  | 'rate-limit'
  | 'quota'
  | 'model'
  | 'timeout'
  | 'connection'
  | 'server'
  | 'validation'
  | 'resource'
  | 'content'
  | 'tool'
  | 'unknown';

export type ErrorSeverity = 'low' | 'medium' | 'high' | 'critical';

export interface ProviderErrorContext {
  [key: string]: any;
  statusCode?: number;
  endpoint?: string;
  modelId?: string;
  requestId?: string;
  retryAfter?: number;
  originalError?: string;
  responseHeaders?: Record<string, string>;
}

export interface ProviderErrorJSON {
  name: string;
  code: string;
  message: string;
  category: ErrorCategory;
  severity: ErrorSeverity;
  retryable: boolean;
  context: ProviderErrorContext;
  timestamp: string;
  providerId: string;
  requestId?: string;
  stack?: string;
}
```

### Retry Strategy Implementation

```typescript
// src/core/retry/retry-strategy.ts
export interface RetryStrategy {
  shouldRetry(error: ProviderError, attempt: number): boolean;
  getDelay(attempt: number, error?: ProviderError): number;
  getMaxAttempts(): number;
  reset(): void;
}

export class ExponentialBackoffStrategy implements RetryStrategy {
  private readonly baseDelay: number;
  private readonly maxDelay: number;
  private readonly maxAttempts: number;
  private readonly jitter: boolean;
  private readonly jitterFactor: number;
  private readonly retryableErrors: Set<string>;
  private readonly nonRetryableErrors: Set<string>;

  constructor(options: ExponentialBackoffOptions) {
    this.baseDelay = options.baseDelay || 1000;
    this.maxDelay = options.maxDelay || 30000;
    this.maxAttempts = options.maxAttempts || 3;
    this.jitter = options.jitter !== false;
    this.jitterFactor = options.jitterFactor || 0.1;
    this.retryableErrors = new Set(options.retryableErrors || []);
    this.nonRetryableErrors = new Set(options.nonRetryableErrors || []);
  }

  shouldRetry(error: ProviderError, attempt: number): boolean {
    // Check attempt limit
    if (attempt >= this.maxAttempts) {
      return false;
    }

    // Check explicit retryable flag
    if (!error.retryable) {
      return false;
    }

    // Check error code lists
    if (this.nonRetryableErrors.has(error.code)) {
      return false;
    }

    if (this.retryableErrors.size > 0 && !this.retryableErrors.has(error.code)) {
      return false;
    }

    // Check error category
    return this.isRetryableCategory(error.category);
  }

  getDelay(attempt: number, error?: ProviderError): number {
    let delay = this.baseDelay * Math.pow(2, attempt - 1);

    // Apply rate limit delay if available
    if (error?.retryAfter) {
      delay = Math.max(delay, error.retryAfter * 1000);
    }

    // Apply maximum delay limit
    delay = Math.min(delay, this.maxDelay);

    // Apply jitter
    if (this.jitter) {
      delay = this.applyJitter(delay);
    }

    return delay;
  }

  getMaxAttempts(): number {
    return this.maxAttempts;
  }

  reset(): void {
    // No state to reset for this strategy
  }

  private isRetryableCategory(category: ErrorCategory): boolean {
    const retryableCategories: Set<ErrorCategory> = new Set([
      'rate-limit',
      'timeout',
      'connection',
      'server',
      'resource',
      'tool',
    ]);

    return retryableCategories.has(category);
  }

  private applyJitter(delay: number): number {
    const jitterAmount = delay * this.jitterFactor;
    const jitter = (Math.random() - 0.5) * 2 * jitterAmount;
    return Math.max(0, delay + jitter);
  }
}

export class LinearBackoffStrategy implements RetryStrategy {
  private readonly baseDelay: number;
  private readonly maxDelay: number;
  private readonly maxAttempts: number;
  private readonly increment: number;
  private readonly jitter: boolean;

  constructor(options: LinearBackoffOptions) {
    this.baseDelay = options.baseDelay || 1000;
    this.maxDelay = options.maxDelay || 10000;
    this.maxAttempts = options.maxAttempts || 3;
    this.increment = options.increment || 1000;
    this.jitter = options.jitter !== false;
  }

  shouldRetry(error: ProviderError, attempt: number): boolean {
    return attempt < this.maxAttempts && error.retryable;
  }

  getDelay(attempt: number): number {
    let delay = Math.min(this.baseDelay + (attempt - 1) * this.increment, this.maxDelay);

    if (this.jitter) {
      delay = delay * (0.5 + Math.random() * 0.5);
    }

    return delay;
  }

  getMaxAttempts(): number {
    return this.maxAttempts;
  }

  reset(): void {
    // No state to reset
  }
}

export class FixedDelayStrategy implements RetryStrategy {
  private readonly delay: number;
  private readonly maxAttempts: number;

  constructor(options: FixedDelayOptions) {
    this.delay = options.delay || 1000;
    this.maxAttempts = options.maxAttempts || 3;
  }

  shouldRetry(error: ProviderError, attempt: number): boolean {
    return attempt < this.maxAttempts && error.retryable;
  }

  getDelay(): number {
    return this.delay;
  }

  getMaxAttempts(): number {
    return this.maxAttempts;
  }

  reset(): void {
    // No state to reset
  }
}

export class AdaptiveRetryStrategy implements RetryStrategy {
  private readonly baseStrategy: RetryStrategy;
  private readonly successThreshold: number;
  private readonly failureThreshold: number;
  private readonly windowSize: number;
  private readonly history: boolean[];

  constructor(options: AdaptiveRetryOptions) {
    this.baseStrategy = options.baseStrategy;
    this.successThreshold = options.successThreshold || 0.8;
    this.failureThreshold = options.failureThreshold || 0.5;
    this.windowSize = options.windowSize || 10;
    this.history = [];
  }

  shouldRetry(error: ProviderError, attempt: number): boolean {
    // Check base strategy first
    if (!this.baseStrategy.shouldRetry(error, attempt)) {
      return false;
    }

    // Adjust based on recent success rate
    const successRate = this.getSuccessRate();

    if (successRate < this.failureThreshold) {
      // Low success rate, be more conservative
      return attempt <= 2; // Limit retries
    }

    return true;
  }

  getDelay(attempt: number, error?: ProviderError): number {
    const baseDelay = this.baseStrategy.getDelay(attempt, error);
    const successRate = this.getSuccessRate();

    // Adjust delay based on success rate
    if (successRate < this.failureThreshold) {
      return baseDelay * 2; // Be more conservative
    } else if (successRate > this.successThreshold) {
      return baseDelay * 0.5; // Be more aggressive
    }

    return baseDelay;
  }

  getMaxAttempts(): number {
    const successRate = this.getSuccessRate();
    const baseMaxAttempts = this.baseStrategy.getMaxAttempts();

    if (successRate < this.failureThreshold) {
      return Math.max(1, Math.floor(baseMaxAttempts * 0.5));
    }

    return baseMaxAttempts;
  }

  recordSuccess(): void {
    this.history.push(true);
    this.trimHistory();
  }

  recordFailure(): void {
    this.history.push(false);
    this.trimHistory();
  }

  reset(): void {
    this.history = [];
    this.baseStrategy.reset();
  }

  private getSuccessRate(): number {
    if (this.history.length === 0) {
      return 1.0; // Assume success if no history
    }

    const successes = this.history.filter(Boolean).length;
    return successes / this.history.length;
  }

  private trimHistory(): void {
    if (this.history.length > this.windowSize) {
      this.history = this.history.slice(-this.windowSize);
    }
  }
}

// Type definitions
export interface ExponentialBackoffOptions {
  baseDelay?: number;
  maxDelay?: number;
  maxAttempts?: number;
  jitter?: boolean;
  jitterFactor?: number;
  retryableErrors?: string[];
  nonRetryableErrors?: string[];
}

export interface LinearBackoffOptions {
  baseDelay?: number;
  maxDelay?: number;
  maxAttempts?: number;
  increment?: number;
  jitter?: boolean;
}

export interface FixedDelayOptions {
  delay?: number;
  maxAttempts?: number;
}

export interface AdaptiveRetryOptions {
  baseStrategy: RetryStrategy;
  successThreshold?: number;
  failureThreshold?: number;
  windowSize?: number;
}
```

### Circuit Breaker Implementation

```typescript
// src/core/circuit-breaker/circuit-breaker.ts
export enum CircuitState {
  CLOSED = 'closed',
  OPEN = 'open',
  HALF_OPEN = 'half-open',
}

export interface CircuitBreakerConfig {
  failureThreshold: number;
  recoveryTimeout: number;
  monitoringPeriod: number;
  expectedRecoveryTime?: number;
  halfOpenMaxCalls?: number;
}

export interface CircuitBreakerMetrics {
  state: CircuitState;
  failures: number;
  successes: number;
  totalCalls: number;
  lastFailureTime?: Date;
  lastSuccessTime?: Date;
  failureRate: number;
  stateChangedAt?: Date;
}

export class CircuitBreaker {
  private readonly config: CircuitBreakerConfig;
  private readonly providerId: string;
  private state: CircuitState = CircuitState.CLOSED;
  private failures: number = 0;
  private successes: number = 0;
  private totalCalls: number = 0;
  private lastFailureTime?: Date;
  private lastSuccessTime?: Date;
  private stateChangedAt: Date = new Date();
  private halfOpenCalls: number = 0;
  private readonly listeners: Array<(state: CircuitState) => void> = [];

  constructor(providerId: string, config: CircuitBreakerConfig) {
    this.providerId = providerId;
    this.config = config;
  }

  async execute<T>(operation: () => Promise<T>): Promise<T> {
    if (this.state === CircuitState.OPEN) {
      if (this.shouldAttemptReset()) {
        this.transitionToHalfOpen();
      } else {
        throw new CircuitBreakerOpenError(
          this.providerId,
          `Circuit breaker is open for provider ${this.providerId}`
        );
      }
    }

    this.totalCalls++;

    try {
      const result = await operation();
      this.onSuccess();
      return result;
    } catch (error) {
      this.onFailure();
      throw error;
    }
  }

  getState(): CircuitState {
    return this.state;
  }

  getMetrics(): CircuitBreakerMetrics {
    return {
      state: this.state,
      failures: this.failures,
      successes: this.successes,
      totalCalls: this.totalCalls,
      lastFailureTime: this.lastFailureTime,
      lastSuccessTime: this.lastSuccessTime,
      failureRate: this.totalCalls > 0 ? this.failures / this.totalCalls : 0,
      stateChangedAt: this.stateChangedAt,
    };
  }

  reset(): void {
    this.state = CircuitState.CLOSED;
    this.failures = 0;
    this.successes = 0;
    this.totalCalls = 0;
    this.halfOpenCalls = 0;
    this.stateChangedAt = new Date();
    this.notifyListeners();
  }

  forceOpen(): void {
    this.transitionToOpen();
  }

  forceClose(): void {
    this.transitionToClosed();
  }

  onStateChange(listener: (state: CircuitState) => void): () => void {
    this.listeners.push(listener);
    return () => {
      const index = this.listeners.indexOf(listener);
      if (index >= 0) {
        this.listeners.splice(index, 1);
      }
    };
  }

  private onSuccess(): void {
    this.successes++;
    this.lastSuccessTime = new Date();

    if (this.state === CircuitState.HALF_OPEN) {
      this.transitionToClosed();
    }
  }

  private onFailure(): void {
    this.failures++;
    this.lastFailureTime = new Date();

    if (this.state === CircuitState.CLOSED) {
      if (this.shouldOpenCircuit()) {
        this.transitionToOpen();
      }
    } else if (this.state === CircuitState.HALF_OPEN) {
      this.transitionToOpen();
    }
  }

  private shouldOpenCircuit(): boolean {
    return this.failures >= this.config.failureThreshold;
  }

  private shouldAttemptReset(): boolean {
    if (!this.lastFailureTime) {
      return false;
    }

    const timeSinceLastFailure = Date.now() - this.lastFailureTime.getTime();
    return timeSinceLastFailure >= this.config.recoveryTimeout;
  }

  private transitionToOpen(): void {
    this.state = CircuitState.OPEN;
    this.stateChangedAt = new Date();
    this.halfOpenCalls = 0;
    this.notifyListeners();
  }

  private transitionToClosed(): void {
    this.state = CircuitState.CLOSED;
    this.stateChangedAt = new Date();
    this.failures = 0;
    this.successes = 0;
    this.totalCalls = 0;
    this.halfOpenCalls = 0;
    this.notifyListeners();
  }

  private transitionToHalfOpen(): void {
    this.state = CircuitState.HALF_OPEN;
    this.stateChangedAt = new Date();
    this.halfOpenCalls = 0;
    this.notifyListeners();
  }

  private notifyListeners(): void {
    for (const listener of this.listeners) {
      try {
        listener(this.state);
      } catch (error) {
        // Log error but don't let it break other listeners
        console.error('Error in circuit breaker listener:', error);
      }
    }
  }
}

export class CircuitBreakerOpenError extends Error {
  constructor(
    public readonly providerId: string,
    message: string
  ) {
    super(message);
    this.name = 'CircuitBreakerOpenError';
  }
}

export class CircuitBreakerRegistry {
  private readonly circuitBreakers: Map<string, CircuitBreaker> = new Map();
  private readonly defaultConfig: CircuitBreakerConfig;

  constructor(defaultConfig: CircuitBreakerConfig) {
    this.defaultConfig = defaultConfig;
  }

  getCircuitBreaker(providerId: string, config?: Partial<CircuitBreakerConfig>): CircuitBreaker {
    let circuitBreaker = this.circuitBreakers.get(providerId);

    if (!circuitBreaker) {
      const finalConfig = { ...this.defaultConfig, ...config };
      circuitBreaker = new CircuitBreaker(providerId, finalConfig);
      this.circuitBreakers.set(providerId, circuitBreaker);
    }

    return circuitBreaker;
  }

  getAllMetrics(): Record<string, CircuitBreakerMetrics> {
    const metrics: Record<string, CircuitBreakerMetrics> = {};

    for (const [providerId, circuitBreaker] of this.circuitBreakers) {
      metrics[providerId] = circuitBreaker.getMetrics();
    }

    return metrics;
  }

  resetAll(): void {
    for (const circuitBreaker of this.circuitBreakers.values()) {
      circuitBreaker.reset();
    }
  }
}
```

### Error Handler Implementation

```typescript
// src/core/errors/error-handler.ts
export class ProviderErrorHandler {
  private readonly retryStrategy: RetryStrategy;
  private readonly circuitBreaker: CircuitBreaker;
  private readonly logger: Logger;
  private readonly metrics: ErrorMetrics;
  private readonly config: ErrorHandlerConfig;

  constructor(providerId: string, config: ErrorHandlerConfig, logger: Logger) {
    this.config = config;
    this.logger = logger;
    this.metrics = new ErrorMetrics(providerId);
    this.retryStrategy = this.createRetryStrategy(config.retry);
    this.circuitBreaker = new CircuitBreaker(providerId, config.circuitBreaker);
  }

  async executeWithRetry<T>(operation: () => Promise<T>, context: OperationContext): Promise<T> {
    return this.circuitBreaker.execute(async () => {
      let lastError: ProviderError;
      let attempt = 1;

      while (attempt <= this.retryStrategy.getMaxAttempts()) {
        try {
          const result = await this.executeOperation(operation, context);
          this.metrics.recordSuccess();
          return result;
        } catch (error) {
          lastError = this.normalizeError(error, context);
          this.metrics.recordFailure(lastError);

          if (!this.retryStrategy.shouldRetry(lastError, attempt)) {
            throw lastError;
          }

          const delay = this.retryStrategy.getDelay(attempt, lastError);
          await this.logRetryAttempt(lastError, attempt, delay);
          await this.sleep(delay);
          attempt++;
        }
      }

      throw lastError!;
    });
  }

  async executeWithFallback<T>(
    primaryOperation: () => Promise<T>,
    fallbackOperation: () => Promise<T>,
    context: OperationContext
  ): Promise<T> {
    try {
      return await this.executeWithRetry(primaryOperation, context);
    } catch (primaryError) {
      this.logger.warn('Primary operation failed, trying fallback', {
        error: primaryError,
        context,
      });

      try {
        const result = await this.executeWithRetry(fallbackOperation, context);
        this.metrics.recordFallbackSuccess();
        return result;
      } catch (fallbackError) {
        this.metrics.recordFallbackFailure();
        throw new ProviderError(
          'FALLBACK_FAILED',
          `Both primary and fallback operations failed: ${primaryError.message}, ${fallbackError.message}`,
          'fallback',
          'high',
          false,
          { primaryError, fallbackError },
          context.providerId
        );
      }
    }
  }

  getMetrics(): ErrorMetricsData {
    return this.metrics.getMetrics();
  }

  getCircuitBreakerMetrics(): CircuitBreakerMetrics {
    return this.circuitBreaker.getMetrics();
  }

  reset(): void {
    this.metrics.reset();
    this.retryStrategy.reset();
    this.circuitBreaker.reset();
  }

  private async executeOperation<T>(
    operation: () => Promise<T>,
    context: OperationContext
  ): Promise<T> {
    const startTime = Date.now();

    try {
      const result = await operation();
      const duration = Date.now() - startTime;

      this.logger.debug('Operation succeeded', {
        operation: context.operation,
        duration,
        providerId: context.providerId,
      });

      return result;
    } catch (error) {
      const duration = Date.now() - startTime;

      this.logger.error('Operation failed', {
        operation: context.operation,
        duration,
        error: error.message,
        providerId: context.providerId,
      });

      throw error;
    }
  }

  private normalizeError(error: any, context: OperationContext): ProviderError {
    if (error instanceof ProviderError) {
      return error;
    }

    // Convert common HTTP errors
    if (error.response) {
      return this.createHttpError(error, context);
    }

    // Convert network errors
    if (error.code === 'ECONNREFUSED' || error.code === 'ENOTFOUND') {
      return new ConnectionError(
        context.providerId,
        `Network error: ${error.message}`,
        context.endpoint,
        error
      );
    }

    // Convert timeout errors
    if (error.code === 'ETIMEDOUT' || error.message.includes('timeout')) {
      return new TimeoutError(context.providerId, context.operation, context.timeout || 30000, {
        originalError: error.message,
      });
    }

    // Default to generic error
    return new GenericProviderError(context.providerId, error.message || 'Unknown error', {
      originalError: error.message,
      stack: error.stack,
    });
  }

  private createHttpError(error: any, context: OperationContext): ProviderError {
    const status = error.response?.status;
    const message = error.response?.data?.message || error.message;

    switch (status) {
      case 401:
        return new AuthenticationError(context.providerId, message);
      case 429:
        const retryAfter = error.response?.headers?.['retry-after'];
        return new RateLimitError(
          context.providerId,
          message,
          retryAfter ? parseInt(retryAfter) : undefined
        );
      case 402:
        return new QuotaError(context.providerId, message);
      case 404:
        return new ModelNotFoundError(context.providerId, context.modelId || 'unknown');
      case 422:
        return new ValidationError(context.providerId, message);
      case 500:
      case 502:
      case 503:
      case 504:
        return new ServerError(context.providerId, message, status, error.response?.headers);
      default:
        return new GenericProviderError(context.providerId, message);
    }
  }

  private createRetryStrategy(config: RetryConfig): RetryStrategy {
    switch (config.strategy) {
      case 'exponential':
        return new ExponentialBackoffStrategy(config);
      case 'linear':
        return new LinearBackoffStrategy(config);
      case 'fixed':
        return new FixedDelayStrategy(config);
      case 'adaptive':
        return new AdaptiveRetryStrategy({
          baseStrategy: new ExponentialBackoffStrategy(config),
          ...config.adaptive,
        });
      default:
        return new ExponentialBackoffStrategy(config);
    }
  }

  private async logRetryAttempt(
    error: ProviderError,
    attempt: number,
    delay: number
  ): Promise<void> {
    this.logger.warn('Retrying operation', {
      error: error.message,
      code: error.code,
      attempt,
      delay,
      maxAttempts: this.retryStrategy.getMaxAttempts(),
    });
  }

  private sleep(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms));
  }
}

export class ErrorMetrics {
  private readonly providerId: string;
  private successes: number = 0;
  private failures: number = 0;
  private fallbackSuccesses: number = 0;
  private fallbackFailures: number = 0;
  private readonly errorsByCode: Map<string, number> = new Map();
  private readonly errorsByCategory: Map<ErrorCategory, number> = new Map();
  private readonly responseTimes: number[] = [];

  constructor(providerId: string) {
    this.providerId = providerId;
  }

  recordSuccess(): void {
    this.successes++;
  }

  recordFailure(error: ProviderError): void {
    this.failures++;

    const codeCount = this.errorsByCode.get(error.code) || 0;
    this.errorsByCode.set(error.code, codeCount + 1);

    const categoryCount = this.errorsByCategory.get(error.category) || 0;
    this.errorsByCategory.set(error.category, categoryCount + 1);
  }

  recordFallbackSuccess(): void {
    this.fallbackSuccesses++;
  }

  recordFallbackFailure(): void {
    this.fallbackFailures++;
  }

  recordResponseTime(duration: number): void {
    this.responseTimes.push(duration);

    // Keep only last 1000 response times
    if (this.responseTimes.length > 1000) {
      this.responseTimes.shift();
    }
  }

  getMetrics(): ErrorMetricsData {
    const total = this.successes + this.failures;
    const successRate = total > 0 ? this.successes / total : 0;
    const failureRate = total > 0 ? this.failures / total : 0;

    return {
      providerId: this.providerId,
      successes: this.successes,
      failures: this.failures,
      fallbackSuccesses: this.fallbackSuccesses,
      fallbackFailures: this.fallbackFailures,
      successRate,
      failureRate,
      errorsByCode: Object.fromEntries(this.errorsByCode),
      errorsByCategory: Object.fromEntries(this.errorsByCategory),
      averageResponseTime: this.getAverageResponseTime(),
      p95ResponseTime: this.getP95ResponseTime(),
      p99ResponseTime: this.getP99ResponseTime(),
    };
  }

  reset(): void {
    this.successes = 0;
    this.failures = 0;
    this.fallbackSuccesses = 0;
    this.fallbackFailures = 0;
    this.errorsByCode.clear();
    this.errorsByCategory.clear();
    this.responseTimes.length = 0;
  }

  private getAverageResponseTime(): number {
    if (this.responseTimes.length === 0) return 0;
    return this.responseTimes.reduce((sum, time) => sum + time, 0) / this.responseTimes.length;
  }

  private getP95ResponseTime(): number {
    if (this.responseTimes.length === 0) return 0;
    const sorted = [...this.responseTimes].sort((a, b) => a - b);
    const index = Math.floor(sorted.length * 0.95);
    return sorted[index] || 0;
  }

  private getP99ResponseTime(): number {
    if (this.responseTimes.length === 0) return 0;
    const sorted = [...this.responseTimes].sort((a, b) => a - b);
    const index = Math.floor(sorted.length * 0.99);
    return sorted[index] || 0;
  }
}

// Type definitions
export interface OperationContext {
  providerId: string;
  operation: string;
  modelId?: string;
  endpoint?: string;
  timeout?: number;
  requestId?: string;
}

export interface ErrorHandlerConfig {
  retry: RetryConfig;
  circuitBreaker: CircuitBreakerConfig;
  logging: {
    level: string;
    includeStackTrace: boolean;
  };
}

export interface RetryConfig {
  strategy: 'exponential' | 'linear' | 'fixed' | 'adaptive';
  maxAttempts?: number;
  baseDelay?: number;
  maxDelay?: number;
  jitter?: boolean;
  retryableErrors?: string[];
  nonRetryableErrors?: string[];
  adaptive?: {
    successThreshold?: number;
    failureThreshold?: number;
    windowSize?: number;
  };
}

export interface ErrorMetricsData {
  providerId: string;
  successes: number;
  failures: number;
  fallbackSuccesses: number;
  fallbackFailures: number;
  successRate: number;
  failureRate: number;
  errorsByCode: Record<string, number>;
  errorsByCategory: Record<ErrorCategory, number>;
  averageResponseTime: number;
  p95ResponseTime: number;
  p99ResponseTime: number;
}
```

## Testing Strategy

### Unit Tests

```typescript
// tests/core/errors/provider-errors.test.ts
describe('ProviderError', () => {
  it('should create error with proper properties', () => {
    const error = new RateLimitError('test-provider', 'Rate limit exceeded', 60, 'requests');

    expect(error.code).toBe('RATE_LIMIT_EXCEEDED');
    expect(error.category).toBe('rate-limit');
    expect(error.severity).toBe('medium');
    expect(error.retryable).toBe(true);
    expect(error.retryAfter).toBe(60);
    expect(error.limitType).toBe('requests');
  });

  it('should serialize to JSON correctly', () => {
    const error = new AuthenticationError('test-provider', 'Invalid token');
    const json = error.toJSON();

    expect(json.code).toBe('AUTHENTICATION_FAILED');
    expect(json.category).toBe('authentication');
    expect(json.retryable).toBe(false);
    expect(json.providerId).toBe('test-provider');
  });

  it('should deserialize from JSON correctly', () => {
    const json = {
      name: 'RateLimitError',
      code: 'RATE_LIMIT_EXCEEDED',
      message: 'Rate limit exceeded',
      category: 'rate-limit' as ErrorCategory,
      severity: 'medium' as ErrorSeverity,
      retryable: true,
      context: { retryAfter: 60 },
      timestamp: '2023-01-01T00:00:00.000Z',
      providerId: 'test-provider',
    };

    const error = ProviderError.fromJSON(json);

    expect(error).toBeInstanceOf(RateLimitError);
    expect(error.code).toBe('RATE_LIMIT_EXCEEDED');
    expect(error.retryAfter).toBe(60);
  });
});

// tests/core/retry/retry-strategy.test.ts
describe('ExponentialBackoffStrategy', () => {
  let strategy: ExponentialBackoffStrategy;

  beforeEach(() => {
    strategy = new ExponentialBackoffStrategy({
      baseDelay: 1000,
      maxDelay: 10000,
      maxAttempts: 3,
      jitter: false,
    });
  });

  it('should determine retry eligibility correctly', () => {
    const retryableError = new RateLimitError('test', 'Rate limit');
    const nonRetryableError = new AuthenticationError('test', 'Invalid auth');

    expect(strategy.shouldRetry(retryableError, 1)).toBe(true);
    expect(strategy.shouldRetry(retryableError, 3)).toBe(false);
    expect(strategy.shouldRetry(nonRetryableError, 1)).toBe(false);
  });

  it('should calculate exponential backoff correctly', () => {
    expect(strategy.getDelay(1)).toBe(1000);
    expect(strategy.getDelay(2)).toBe(2000);
    expect(strategy.getDelay(3)).toBe(4000);
  });

  it('should respect maximum delay', () => {
    const delay = strategy.getDelay(10); // Would be 512000 without max
    expect(delay).toBe(10000);
  });

  it('should apply jitter when enabled', () => {
    const jitterStrategy = new ExponentialBackoffStrategy({
      baseDelay: 1000,
      jitter: true,
      jitterFactor: 0.1,
    });

    const delay1 = jitterStrategy.getDelay(1);
    const delay2 = jitterStrategy.getDelay(1);

    expect(delay1).not.toBe(delay2);
    expect(delay1).toBeGreaterThanOrEqual(900);
    expect(delay1).toBeLessThanOrEqual(1100);
  });
});

// tests/core/circuit-breaker/circuit-breaker.test.ts
describe('CircuitBreaker', () => {
  let circuitBreaker: CircuitBreaker;

  beforeEach(() => {
    circuitBreaker = new CircuitBreaker('test-provider', {
      failureThreshold: 3,
      recoveryTimeout: 5000,
      monitoringPeriod: 10000,
    });
  });

  it('should start in closed state', () => {
    expect(circuitBreaker.getState()).toBe(CircuitState.CLOSED);
  });

  it('should open after failure threshold', async () => {
    const failingOperation = () => Promise.reject(new Error('Test error'));

    for (let i = 0; i < 3; i++) {
      try {
        await circuitBreaker.execute(failingOperation);
      } catch (error) {
        // Expected
      }
    }

    expect(circuitBreaker.getState()).toBe(CircuitState.OPEN);
  });

  it('should reject calls when open', async () => {
    circuitBreaker.forceOpen();

    const operation = () => Promise.resolve('success');

    await expect(circuitBreaker.execute(operation)).rejects.toThrow('Circuit breaker is open');
  });

  it('should transition to half-open after recovery timeout', async () => {
    circuitBreaker.forceOpen();

    // Mock time passage
    jest.useFakeTimers();
    jest.advanceTimersByTime(6000);

    const operation = () => Promise.resolve('success');
    await expect(circuitBreaker.execute(operation)).resolves.toBe('success');

    expect(circuitBreaker.getState()).toBe(CircuitState.CLOSED);

    jest.useRealTimers();
  });
});
```

## Success Metrics

### Error Handling Metrics

- Error classification accuracy: 95%+
- Retry success rate: 80%+
- Circuit breaker effectiveness: 90%+
- Error logging completeness: 100%
- Error recovery time: < 5s

### Performance Metrics

- Error handling overhead: < 5ms
- Retry delay accuracy: 95%+
- Circuit breaker response time: < 1ms
- Metrics collection overhead: < 1%
- Memory usage: < 10MB

### Reliability Metrics

- Error handling coverage: 100%
- Retry logic correctness: 100%
- Circuit breaker reliability: 99.9%
- Error reporting accuracy: 95%+
- Test coverage: 95%+

## Dependencies

### External Dependencies

- No external dependencies required

### Internal Dependencies

- `@tamma/shared/types`: Shared type definitions
- `@tamma/core/logging`: Logging utilities
- `@tamma/core/metrics`: Metrics collection

## Security Considerations

### Error Information Security

- Sanitize error messages to prevent information leakage
- Remove sensitive data from error context
- Implement error rate limiting to prevent enumeration
- Log errors securely without exposing credentials

### Retry Security

- Prevent infinite retry loops
- Implement retry budgets to prevent abuse
- Add circuit breaker to prevent cascading failures
- Monitor retry patterns for anomalies

## Deliverables

1. **Error Classes** (`src/core/errors/provider-errors.ts`)
2. **Retry Strategies** (`src/core/retry/retry-strategy.ts`)
3. **Circuit Breaker** (`src/core/circuit-breaker/circuit-breaker.ts`)
4. **Error Handler** (`src/core/errors/error-handler.ts`)
5. **Error Metrics** (`src/core/errors/error-metrics.ts`)
6. **Unit Tests** (`tests/core/errors/`, `tests/core/retry/`, `tests/core/circuit-breaker/`)
7. **Integration Tests** (`tests/core/error-handling-integration.test.ts`)
8. **Documentation** (`docs/core/error-handling.md`)

This implementation provides comprehensive error handling and retry logic across all AI providers, ensuring robust failure recovery, intelligent retry strategies, and detailed error monitoring while maintaining system stability and performance.
