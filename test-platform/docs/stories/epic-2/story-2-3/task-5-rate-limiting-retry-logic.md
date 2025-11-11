# Task 5: Rate Limiting and Retry Logic

## Overview

Implement robust rate limiting and retry logic for OpenAI API calls to handle rate limits, network failures, and temporary service issues gracefully while maintaining optimal performance.

## Objectives

- Implement intelligent rate limiting to respect OpenAI API limits
- Add exponential backoff retry logic for failed requests
- Handle different types of rate limits (request-level, token-level, organization-level)
- Provide circuit breaker pattern for service protection

## Implementation Steps

### Subtask 5.1: Implement Rate Limiting Logic

**Description**: Create a sophisticated rate limiting system that tracks and respects OpenAI's various rate limits across different dimensions.

**Implementation Details**:

1. **Create Rate Limiter Interface**:

```typescript
// packages/providers/src/interfaces/rate-limiter.interface.ts
export interface IRateLimiter {
  checkLimit(request: RateLimitRequest): Promise<RateLimitResult>;
  recordUsage(request: RateLimitRequest): Promise<void>;
  getWaitTime(request: RateLimitRequest): Promise<number>;
  resetLimits(): Promise<void>;
  getLimitInfo(): RateLimitInfo;
}

export interface RateLimitRequest {
  userId?: string;
  organizationId?: string;
  model: string;
  requestType: 'chat' | 'completion' | 'embedding' | 'image';
  estimatedTokens?: number;
}

export interface RateLimitResult {
  allowed: boolean;
  waitTime: number;
  remainingRequests: number;
  remainingTokens: number;
  resetTime: Date;
  limitType: 'requests' | 'tokens' | 'none';
}

export interface RateLimitInfo {
  requestsPerMinute: number;
  tokensPerMinute: number;
  requestsPerDay: number;
  currentUsage: {
    requestsInLastMinute: number;
    tokensInLastMinute: number;
    requestsToday: number;
  };
  nextResetTime: Date;
}

export interface RateLimitConfig {
  requestsPerMinute: number;
  tokensPerMinute: number;
  requestsPerDay: number;
  burstAllowance: number;
  organizationLimits?: {
    [organizationId: string]: Partial<RateLimitConfig>;
  };
  modelLimits?: {
    [model: string]: Partial<RateLimitConfig>;
  };
}
```

2. **Implement OpenAI Rate Limiter**:

```typescript
// packages/providers/src/openai/openai-rate-limiter.ts
import {
  IRateLimiter,
  RateLimitRequest,
  RateLimitResult,
  RateLimitInfo,
  RateLimitConfig,
} from '../interfaces/rate-limiter.interface';
import { EventEmitter } from 'events';

interface UsageRecord {
  timestamp: number;
  requests: number;
  tokens: number;
  userId?: string;
  organizationId?: string;
  model: string;
}

export class OpenAIRateLimiter extends EventEmitter implements IRateLimiter {
  private config: RateLimitConfig;
  private usageHistory: UsageRecord[] = [];
  private readonly WINDOW_SIZE_MS = 60 * 1000; // 1 minute
  private readonly DAY_SIZE_MS = 24 * 60 * 60 * 1000; // 1 day

  constructor(config?: Partial<RateLimitConfig>) {
    super();
    this.config = {
      requestsPerMinute: 3500, // OpenAI default for paid accounts
      tokensPerMinute: 90000, // OpenAI default for paid accounts
      requestsPerDay: 10000, // Conservative daily limit
      burstAllowance: 10, // Allow burst of 10 requests
      ...config,
    };

    // Clean up old usage records periodically
    setInterval(() => this.cleanupOldRecords(), 10000);
  }

  async checkLimit(request: RateLimitRequest): Promise<RateLimitResult> {
    const now = Date.now();
    const effectiveConfig = this.getEffectiveConfig(request);

    // Get usage in the last minute
    const recentUsage = this.getUsageInWindow(now - this.WINDOW_SIZE_MS, request);
    const dailyUsage = this.getUsageInWindow(now - this.DAY_SIZE_MS, request);

    // Check request rate limit
    if (recentUsage.requests >= effectiveConfig.requestsPerMinute) {
      const waitTime = this.calculateWaitTime(recentUsage.timestamp, this.WINDOW_SIZE_MS);
      return {
        allowed: false,
        waitTime,
        remainingRequests: 0,
        remainingTokens: Math.max(0, effectiveConfig.tokensPerMinute - recentUsage.tokens),
        resetTime: new Date(recentUsage.timestamp + this.WINDOW_SIZE_MS),
        limitType: 'requests',
      };
    }

    // Check token rate limit
    if (
      request.estimatedTokens &&
      recentUsage.tokens + request.estimatedTokens > effectiveConfig.tokensPerMinute
    ) {
      const waitTime = this.calculateWaitTime(recentUsage.timestamp, this.WINDOW_SIZE_MS);
      return {
        allowed: false,
        waitTime,
        remainingRequests: Math.max(0, effectiveConfig.requestsPerMinute - recentUsage.requests),
        remainingTokens: 0,
        resetTime: new Date(recentUsage.timestamp + this.WINDOW_SIZE_MS),
        limitType: 'tokens',
      };
    }

    // Check daily limit
    if (dailyUsage.requests >= effectiveConfig.requestsPerDay) {
      const tomorrow = new Date();
      tomorrow.setDate(tomorrow.getDate() + 1);
      tomorrow.setHours(0, 0, 0, 0);

      return {
        allowed: false,
        waitTime: tomorrow.getTime() - now,
        remainingRequests: 0,
        remainingTokens: Math.max(0, effectiveConfig.tokensPerMinute - recentUsage.tokens),
        resetTime: tomorrow,
        limitType: 'requests',
      };
    }

    // Request is allowed
    return {
      allowed: true,
      waitTime: 0,
      remainingRequests: effectiveConfig.requestsPerMinute - recentUsage.requests,
      remainingTokens: request.estimatedTokens
        ? Math.max(
            0,
            effectiveConfig.tokensPerMinute - recentUsage.tokens - request.estimatedTokens
          )
        : effectiveConfig.tokensPerMinute - recentUsage.tokens,
      resetTime: new Date(now + this.WINDOW_SIZE_MS),
      limitType: 'none',
    };
  }

  async recordUsage(request: RateLimitRequest): Promise<void> {
    const now = Date.now();
    const record: UsageRecord = {
      timestamp: now,
      requests: 1,
      tokens: request.estimatedTokens || 0,
      userId: request.userId,
      organizationId: request.organizationId,
      model: request.model,
    };

    this.usageHistory.push(record);
    this.emit('usageRecorded', record);
  }

  async getWaitTime(request: RateLimitRequest): Promise<number> {
    const limitResult = await this.checkLimit(request);
    return limitResult.waitTime;
  }

  async resetLimits(): Promise<void> {
    this.usageHistory = [];
    this.emit('limitsReset');
  }

  getLimitInfo(): RateLimitInfo {
    const now = Date.now();
    const recentUsage = this.getUsageInWindow(now - this.WINDOW_SIZE_MS);
    const dailyUsage = this.getUsageInWindow(now - this.DAY_SIZE_MS);

    return {
      requestsPerMinute: this.config.requestsPerMinute,
      tokensPerMinute: this.config.tokensPerMinute,
      requestsPerDay: this.config.requestsPerDay,
      currentUsage: {
        requestsInLastMinute: recentUsage.requests,
        tokensInLastMinute: recentUsage.tokens,
        requestsToday: dailyUsage.requests,
      },
      nextResetTime: new Date(now + this.WINDOW_SIZE_MS),
    };
  }

  private getEffectiveConfig(request: RateLimitRequest): RateLimitConfig {
    let effectiveConfig = { ...this.config };

    // Apply organization-specific limits
    if (request.organizationId && this.config.organizationLimits?.[request.organizationId]) {
      effectiveConfig = {
        ...effectiveConfig,
        ...this.config.organizationLimits[request.organizationId],
      };
    }

    // Apply model-specific limits
    if (this.config.modelLimits?.[request.model]) {
      effectiveConfig = {
        ...effectiveConfig,
        ...this.config.modelLimits[request.model],
      };
    }

    return effectiveConfig;
  }

  private getUsageInWindow(startTime: number, request?: RateLimitRequest): UsageRecord {
    const recordsInWindow = this.usageHistory.filter(
      (record) =>
        record.timestamp >= startTime &&
        (!request ||
          (record.userId === request.userId && record.organizationId === request.organizationId))
    );

    return {
      timestamp: Math.max(...recordsInWindow.map((r) => r.timestamp), startTime),
      requests: recordsInWindow.reduce((sum, r) => sum + r.requests, 0),
      tokens: recordsInWindow.reduce((sum, r) => sum + r.tokens, 0),
    };
  }

  private calculateWaitTime(lastRequestTime: number, windowSize: number): number {
    const now = Date.now();
    const windowEnd = lastRequestTime + windowSize;
    return Math.max(0, windowEnd - now);
  }

  private cleanupOldRecords(): void {
    const now = Date.now();
    const cutoffTime = now - this.DAY_SIZE_MS;

    const beforeCount = this.usageHistory.length;
    this.usageHistory = this.usageHistory.filter((record) => record.timestamp >= cutoffTime);
    const afterCount = this.usageHistory.length;

    if (beforeCount !== afterCount) {
      this.emit('recordsCleaned', { removed: beforeCount - afterCount });
    }
  }
}
```

3. **Add Rate Limit Response Parser**:

```typescript
// packages/providers/src/openai/rate-limit-parser.ts
import { RateLimitInfo } from '../interfaces/rate-limiter.interface';

export interface RateLimitHeaders {
  'x-ratelimit-limit-requests'?: string;
  'x-ratelimit-limit-tokens'?: string;
  'x-ratelimit-remaining-requests'?: string;
  'x-ratelimit-remaining-tokens'?: string;
  'x-ratelimit-reset-requests'?: string;
  'x-ratelimit-reset-tokens'?: string;
  'retry-after'?: string;
}

export class RateLimitParser {
  static parseHeaders(headers: RateLimitHeaders): Partial<RateLimitInfo> {
    const info: Partial<RateLimitInfo> = {};

    if (headers['x-ratelimit-limit-requests']) {
      info.requestsPerMinute = parseInt(headers['x-ratelimit-limit-requests'], 10);
    }

    if (headers['x-ratelimit-limit-tokens']) {
      info.tokensPerMinute = parseInt(headers['x-ratelimit-limit-tokens'], 10);
    }

    if (headers['x-ratelimit-remaining-requests'] && info.requestsPerMinute) {
      const remaining = parseInt(headers['x-ratelimit-remaining-requests'], 10);
      info.currentUsage = {
        ...info.currentUsage,
        requestsInLastMinute: info.requestsPerMinute - remaining,
      };
    }

    if (headers['x-ratelimit-remaining-tokens'] && info.tokensPerMinute) {
      const remaining = parseInt(headers['x-ratelimit-remaining-tokens'], 10);
      info.currentUsage = {
        ...info.currentUsage,
        tokensInLastMinute: info.tokensPerMinute - remaining,
      };
    }

    if (headers['x-ratelimit-reset-requests']) {
      const resetTime = parseInt(headers['x-ratelimit-reset-requests'], 10);
      info.nextResetTime = new Date(resetTime * 1000);
    }

    return info;
  }

  static getRetryAfter(headers: RateLimitHeaders): number | null {
    if (headers['retry-after']) {
      const seconds = parseInt(headers['retry-after'], 10);
      return isNaN(seconds) ? null : seconds * 1000;
    }
    return null;
  }

  static isRateLimitError(error: any): boolean {
    if (error?.status === 429) {
      return true;
    }

    if (error?.error?.type === 'rate_limit_exceeded') {
      return true;
    }

    if (error?.error?.code === 'rate_limit_exceeded') {
      return true;
    }

    return false;
  }
}
```

### Subtask 5.2: Implement Exponential Backoff Retry Logic

**Description**: Create intelligent retry logic with exponential backoff, jitter, and different strategies for different error types.

**Implementation Details**:

1. **Create Retry Manager Interface**:

```typescript
// packages/providers/src/interfaces/retry-manager.interface.ts
export interface IRetryManager {
  executeWithRetry<T>(operation: () => Promise<T>, context: RetryContext): Promise<T>;
  shouldRetry(error: any, attempt: number, context: RetryContext): boolean;
  calculateDelay(attempt: number, context: RetryContext): number;
  updateConfig(config: Partial<RetryConfig>): void;
}

export interface RetryContext {
  operationId: string;
  operationType: 'chat' | 'completion' | 'embedding' | 'image';
  userId?: string;
  model: string;
  priority: 'low' | 'normal' | 'high';
  timeout?: number;
}

export interface RetryConfig {
  maxAttempts: number;
  baseDelay: number;
  maxDelay: number;
  backoffMultiplier: number;
  jitter: boolean;
  retryableErrors: string[];
  retryableStatusCodes: number[];
  customRetryStrategies: Map<string, RetryStrategy>;
}

export interface RetryStrategy {
  shouldRetry(error: any, attempt: number): boolean;
  getDelay(attempt: number): number;
}

export interface RetryAttempt {
  attempt: number;
  delay: number;
  error: any;
  timestamp: number;
}
```

2. **Implement Retry Manager**:

```typescript
// packages/providers/src/openai/openai-retry-manager.ts
import {
  IRetryManager,
  RetryContext,
  RetryConfig,
  RetryStrategy,
  RetryAttempt,
} from '../interfaces/retry-manager.interface';
import { EventEmitter } from 'events';
import { RateLimitParser } from './rate-limit-parser';

export class OpenAIRetryManager extends EventEmitter implements IRetryManager {
  private config: RetryConfig;
  private retryAttempts: Map<string, RetryAttempt[]> = new Map();

  constructor(config?: Partial<RetryConfig>) {
    super();
    this.config = {
      maxAttempts: 3,
      baseDelay: 1000,
      maxDelay: 30000,
      backoffMultiplier: 2,
      jitter: true,
      retryableErrors: [
        'rate_limit_exceeded',
        'insufficient_quota',
        'model_overloaded',
        'timeout',
        'connection_error',
        'temporary_error',
      ],
      retryableStatusCodes: [429, 500, 502, 503, 504],
      customRetryStrategies: new Map([
        ['rate_limit_exceeded', new RateLimitRetryStrategy()],
        ['model_overloaded', new ModelOverloadedRetryStrategy()],
      ]),
      ...config,
    };
  }

  async executeWithRetry<T>(operation: () => Promise<T>, context: RetryContext): Promise<T> {
    const attempts: RetryAttempt[] = [];
    this.retryAttempts.set(context.operationId, attempts);

    let lastError: any;

    for (let attempt = 1; attempt <= this.config.maxAttempts; attempt++) {
      try {
        this.emit('attemptStart', { context, attempt });

        const startTime = Date.now();
        const result = await this.executeWithTimeout(operation, context.timeout);
        const duration = Date.now() - startTime;

        this.emit('attemptSuccess', { context, attempt, duration });
        this.retryAttempts.delete(context.operationId);

        return result;
      } catch (error) {
        lastError = error;
        const attemptRecord: RetryAttempt = {
          attempt,
          delay: 0,
          error,
          timestamp: Date.now(),
        };

        attempts.push(attemptRecord);

        this.emit('attemptFailed', { context, attempt, error });

        if (attempt === this.config.maxAttempts || !this.shouldRetry(error, attempt, context)) {
          this.emit('operationFailed', { context, attempts, finalError: error });
          this.retryAttempts.delete(context.operationId);
          throw error;
        }

        const delay = this.calculateDelay(attempt, context);
        attemptRecord.delay = delay;

        this.emit('retryScheduled', { context, attempt, delay });

        await this.sleep(delay);
      }
    }

    this.retryAttempts.delete(context.operationId);
    throw lastError;
  }

  shouldRetry(error: any, attempt: number, context: RetryContext): boolean {
    // Check custom retry strategies first
    const errorType = this.getErrorType(error);
    const customStrategy = this.config.customRetryStrategies.get(errorType);

    if (customStrategy) {
      return customStrategy.shouldRetry(error, attempt);
    }

    // Check status codes
    if (error?.status && this.config.retryableStatusCodes.includes(error.status)) {
      return true;
    }

    // Check error types
    if (errorType && this.config.retryableErrors.includes(errorType)) {
      return true;
    }

    // Check for network errors
    if (this.isNetworkError(error)) {
      return true;
    }

    return false;
  }

  calculateDelay(attempt: number, context: RetryContext): number {
    // Use custom strategy if available
    const lastAttempt = this.retryAttempts.get(context.operationId)?.slice(-1)[0];
    if (lastAttempt) {
      const errorType = this.getErrorType(lastAttempt.error);
      const customStrategy = this.config.customRetryStrategies.get(errorType);

      if (customStrategy) {
        return customStrategy.getDelay(attempt);
      }
    }

    // Default exponential backoff with jitter
    let delay = this.config.baseDelay * Math.pow(this.config.backoffMultiplier, attempt - 1);
    delay = Math.min(delay, this.config.maxDelay);

    if (this.config.jitter) {
      delay = this.addJitter(delay);
    }

    // Adjust for priority
    if (context.priority === 'high') {
      delay = delay * 0.5; // Reduce delay for high priority
    } else if (context.priority === 'low') {
      delay = delay * 1.5; // Increase delay for low priority
    }

    return Math.floor(delay);
  }

  updateConfig(config: Partial<RetryConfig>): void {
    this.config = { ...this.config, ...config };
    this.emit('configUpdated', this.config);
  }

  private getErrorType(error: any): string | null {
    if (error?.error?.type) {
      return error.error.type;
    }

    if (error?.error?.code) {
      return error.error.code;
    }

    if (error?.code) {
      return error.code;
    }

    if (RateLimitParser.isRateLimitError(error)) {
      return 'rate_limit_exceeded';
    }

    return null;
  }

  private isNetworkError(error: any): boolean {
    return (
      error?.code === 'ECONNRESET' ||
      error?.code === 'ECONNREFUSED' ||
      error?.code === 'ETIMEDOUT' ||
      error?.code === 'ENOTFOUND' ||
      error?.message?.includes('network') ||
      error?.message?.includes('timeout') ||
      error?.message?.includes('connection')
    );
  }

  private async executeWithTimeout<T>(operation: () => Promise<T>, timeout?: number): Promise<T> {
    if (!timeout) {
      return operation();
    }

    return Promise.race([
      operation(),
      new Promise<never>((_, reject) => {
        setTimeout(() => reject(new Error('Operation timeout')), timeout);
      }),
    ]);
  }

  private addJitter(delay: number): number {
    // Add random jitter between 0-25% of the delay
    const jitterAmount = delay * 0.25 * Math.random();
    return delay + jitterAmount;
  }

  private sleep(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms));
  }
}

// Custom retry strategies
class RateLimitRetryStrategy implements RetryStrategy {
  shouldRetry(error: any, attempt: number): boolean {
    // Always retry rate limit errors, but let the delay calculation handle timing
    return true;
  }

  getDelay(attempt: number): number {
    // For rate limits, use the retry-after header if available
    // This will be handled in the main retry logic
    return 0;
  }
}

class ModelOverloadedRetryStrategy implements RetryStrategy {
  shouldRetry(error: any, attempt: number): boolean {
    // Retry model overloaded errors up to 5 times
    return attempt <= 5;
  }

  getDelay(attempt: number): number {
    // Longer delays for model overload
    return Math.min(10000 * Math.pow(2, attempt - 1), 60000);
  }
}
```

### Subtask 5.3: Add Circuit Breaker Pattern

**Description**: Implement circuit breaker pattern to prevent cascading failures and protect the system when OpenAI API is experiencing issues.

**Implementation Details**:

1. **Create Circuit Breaker Interface**:

```typescript
// packages/providers/src/interfaces/circuit-breaker.interface.ts
export interface ICircuitBreaker {
  execute<T>(operation: () => Promise<T>, context: CircuitBreakerContext): Promise<T>;
  getState(): CircuitBreakerState;
  reset(): void;
  forceOpen(): void;
  forceHalfOpen(): void;
  onStateChange(callback: (state: CircuitBreakerState) => void): void;
}

export interface CircuitBreakerContext {
  operationId: string;
  operationType: string;
  timeout?: number;
}

export enum CircuitBreakerState {
  CLOSED = 'closed',
  OPEN = 'open',
  HALF_OPEN = 'half_open',
}

export interface CircuitBreakerConfig {
  failureThreshold: number;
  recoveryTimeout: number;
  monitoringPeriod: number;
  expectedRecoveryTime: number;
  halfOpenMaxCalls: number;
}
```

2. **Implement Circuit Breaker**:

```typescript
// packages/providers/src/openai/openai-circuit-breaker.ts
import {
  ICircuitBreaker,
  CircuitBreakerContext,
  CircuitBreakerState,
  CircuitBreakerConfig,
} from '../interfaces/circuit-breaker.interface';
import { EventEmitter } from 'events';

interface CallResult {
  success: boolean;
  duration: number;
  timestamp: number;
  error?: any;
}

export class OpenAICircuitBreaker extends EventEmitter implements ICircuitBreaker {
  private state: CircuitBreakerState = CircuitBreakerState.CLOSED;
  private failureCount: number = 0;
  private lastFailureTime: number = 0;
  private callHistory: CallResult[] = [];
  private halfOpenCalls: number = 0;
  private stateChangeCallbacks: ((state: CircuitBreakerState) => void)[] = [];

  constructor(private config: CircuitBreakerConfig) {
    super();

    // Clean up old call history periodically
    setInterval(() => this.cleanupCallHistory(), 60000);
  }

  async execute<T>(operation: () => Promise<T>, context: CircuitBreakerContext): Promise<T> {
    if (this.state === CircuitBreakerState.OPEN) {
      if (this.shouldAttemptReset()) {
        this.transitionToHalfOpen();
      } else {
        throw new Error('Circuit breaker is OPEN - operation blocked');
      }
    }

    if (this.state === CircuitBreakerState.HALF_OPEN) {
      if (this.halfOpenCalls >= this.config.halfOpenMaxCalls) {
        throw new Error('Circuit breaker is HALF_OPEN - max calls exceeded');
      }
      this.halfOpenCalls++;
    }

    const startTime = Date.now();

    try {
      const result = await this.executeWithTimeout(operation, context.timeout);
      const duration = Date.now() - startTime;

      this.recordSuccess(duration);
      return result;
    } catch (error) {
      const duration = Date.now() - startTime;
      this.recordFailure(error, duration);
      throw error;
    }
  }

  getState(): CircuitBreakerState {
    return this.state;
  }

  reset(): void {
    this.state = CircuitBreakerState.CLOSED;
    this.failureCount = 0;
    this.lastFailureTime = 0;
    this.callHistory = [];
    this.halfOpenCalls = 0;
    this.emit('stateChanged', this.state);
    this.notifyStateChange();
  }

  forceOpen(): void {
    this.transitionToOpen();
  }

  forceHalfOpen(): void {
    this.transitionToHalfOpen();
  }

  onStateChange(callback: (state: CircuitBreakerState) => void): void {
    this.stateChangeCallbacks.push(callback);
  }

  private recordSuccess(duration: number): void {
    const result: CallResult = {
      success: true,
      duration,
      timestamp: Date.now(),
    };

    this.callHistory.push(result);

    if (this.state === CircuitBreakerState.HALF_OPEN) {
      this.transitionToClosed();
    } else if (this.state === CircuitBreakerState.CLOSED) {
      // Reset failure count on success in closed state
      this.failureCount = 0;
    }

    this.emit('callSuccess', { duration, state: this.state });
  }

  private recordFailure(error: any, duration: number): void {
    const result: CallResult = {
      success: false,
      duration,
      timestamp: Date.now(),
      error,
    };

    this.callHistory.push(result);
    this.failureCount++;
    this.lastFailureTime = Date.now();

    if (this.state === CircuitBreakerState.HALF_OPEN) {
      this.transitionToOpen();
    } else if (this.state === CircuitBreakerState.CLOSED) {
      if (this.shouldOpenCircuit()) {
        this.transitionToOpen();
      }
    }

    this.emit('callFailure', {
      error,
      duration,
      state: this.state,
      failureCount: this.failureCount,
    });
  }

  private shouldOpenCircuit(): boolean {
    // Check failure threshold
    if (this.failureCount >= this.config.failureThreshold) {
      return true;
    }

    // Check failure rate in monitoring period
    const recentCalls = this.getRecentCalls(this.config.monitoringPeriod);
    if (recentCalls.length === 0) return false;

    const failureRate = recentCalls.filter((call) => !call.success).length / recentCalls.length;
    return failureRate >= 0.5; // 50% failure rate
  }

  private shouldAttemptReset(): boolean {
    return Date.now() - this.lastFailureTime >= this.config.recoveryTimeout;
  }

  private transitionToOpen(): void {
    if (this.state !== CircuitBreakerState.OPEN) {
      this.state = CircuitBreakerState.OPEN;
      this.halfOpenCalls = 0;
      this.emit('stateChanged', this.state);
      this.notifyStateChange();
    }
  }

  private transitionToHalfOpen(): void {
    if (this.state !== CircuitBreakerState.HALF_OPEN) {
      this.state = CircuitBreakerState.HALF_OPEN;
      this.halfOpenCalls = 0;
      this.emit('stateChanged', this.state);
      this.notifyStateChange();
    }
  }

  private transitionToClosed(): void {
    if (this.state !== CircuitBreakerState.CLOSED) {
      this.state = CircuitBreakerState.CLOSED;
      this.failureCount = 0;
      this.halfOpenCalls = 0;
      this.emit('stateChanged', this.state);
      this.notifyStateChange();
    }
  }

  private getRecentCalls(periodMs: number): CallResult[] {
    const cutoff = Date.now() - periodMs;
    return this.callHistory.filter((call) => call.timestamp >= cutoff);
  }

  private cleanupCallHistory(): void {
    const cutoff = Date.now() - this.config.monitoringPeriod * 2;
    const beforeCount = this.callHistory.length;
    this.callHistory = this.callHistory.filter((call) => call.timestamp >= cutoff);

    if (beforeCount !== this.callHistory.length) {
      this.emit('historyCleaned', { removed: beforeCount - this.callHistory.length });
    }
  }

  private notifyStateChange(): void {
    for (const callback of this.stateChangeCallbacks) {
      try {
        callback(this.state);
      } catch (error) {
        this.emit('callbackError', error);
      }
    }
  }

  private async executeWithTimeout<T>(operation: () => Promise<T>, timeout?: number): Promise<T> {
    if (!timeout) {
      return operation();
    }

    return Promise.race([
      operation(),
      new Promise<never>((_, reject) => {
        setTimeout(() => reject(new Error('Circuit breaker operation timeout')), timeout);
      }),
    ]);
  }
}
```

3. **Integrate with OpenAI Provider**:

```typescript
// packages/providers/src/openai/openai-provider.ts (updated sections)
import { OpenAIRateLimiter } from './openai-rate-limiter';
import { OpenAIRetryManager } from './openai-retry-manager';
import { OpenAICircuitBreaker } from './openai-circuit-breaker';

export class OpenAIProvider implements IAIProvider {
  private rateLimiter: OpenAIRateLimiter;
  private retryManager: OpenAIRetryManager;
  private circuitBreaker: OpenAICircuitBreaker;

  constructor(private config: OpenAIProviderConfig) {
    this.rateLimiter = new OpenAIRateLimiter(config.rateLimit);
    this.retryManager = new OpenAIRetryManager(config.retry);
    this.circuitBreaker = new OpenAICircuitBreaker(
      config.circuitBreaker || {
        failureThreshold: 5,
        recoveryTimeout: 60000,
        monitoringPeriod: 300000,
        expectedRecoveryTime: 120000,
        halfOpenMaxCalls: 3,
      }
    );

    this.setupEventHandlers();
  }

  async sendMessage(request: MessageRequest): Promise<AsyncIterable<MessageChunk>> {
    const context: RetryContext = {
      operationId: request.id,
      operationType: 'chat',
      userId: request.userId,
      model: request.model,
      priority: request.priority || 'normal',
      timeout: request.timeout,
    };

    return this.retryManager.executeWithRetry(async () => {
      // Check rate limits first
      const rateLimitResult = await this.rateLimiter.checkLimit({
        userId: request.userId,
        organizationId: request.organizationId,
        model: request.model,
        requestType: 'chat',
        estimatedTokens: this.estimateTokens(request),
      });

      if (!rateLimitResult.allowed) {
        throw new Error(`Rate limit exceeded. Wait ${rateLimitResult.waitTime}ms`);
      }

      // Execute through circuit breaker
      return this.circuitBreaker.execute(
        async () => {
          const response = await this.client.chat.completions.create({
            model: request.model,
            messages: request.messages,
            stream: true,
            temperature: request.temperature,
            max_tokens: request.maxTokens,
            ...request.options,
          });

          // Record usage after successful API call
          await this.rateLimiter.recordUsage({
            userId: request.userId,
            organizationId: request.organizationId,
            model: request.model,
            requestType: 'chat',
            estimatedTokens: this.estimateTokens(request),
          });

          return this.processStream(response);
        },
        {
          operationId: request.id,
          operationType: 'chat',
          timeout: request.timeout,
        }
      );
    }, context);
  }

  private setupEventHandlers(): void {
    // Rate limiter events
    this.rateLimiter.on('usageRecorded', (record) => {
      logger.debug('Usage recorded', { record });
    });

    // Retry manager events
    this.retryManager.on('attemptFailed', ({ context, attempt, error }) => {
      logger.warn('Retry attempt failed', {
        operationId: context.operationId,
        attempt,
        error: error.message,
      });
    });

    // Circuit breaker events
    this.circuitBreaker.on('stateChanged', (state) => {
      logger.info('Circuit breaker state changed', { state });
    });

    this.circuitBreaker.on('callFailure', ({ error, failureCount }) => {
      logger.error('Circuit breaker call failure', {
        error: error.message,
        failureCount,
      });
    });
  }

  private estimateTokens(request: MessageRequest): number {
    // Rough estimation - in production, use the token counter
    const totalChars = request.messages.reduce((sum, msg) => {
      return (
        sum +
        (typeof msg.content === 'string' ? msg.content.length : JSON.stringify(msg.content).length)
      );
    }, 0);
    return Math.ceil(totalChars / 4);
  }
}
```

## Files to Create

1. **Core Interfaces**:
   - `packages/providers/src/interfaces/rate-limiter.interface.ts`
   - `packages/providers/src/interfaces/retry-manager.interface.ts`
   - `packages/providers/src/interfaces/circuit-breaker.interface.ts`

2. **OpenAI Implementation**:
   - `packages/providers/src/openai/openai-rate-limiter.ts`
   - `packages/providers/src/openai/openai-retry-manager.ts`
   - `packages/providers/src/openai/openai-circuit-breaker.ts`
   - `packages/providers/src/openai/rate-limit-parser.ts`

3. **Updated Files**:
   - `packages/providers/src/openai/openai-provider.ts` (integrate all components)

## Testing Requirements

### Unit Tests

```typescript
// packages/providers/src/__tests__/openai-rate-limiter.test.ts
describe('OpenAIRateLimiter', () => {
  let rateLimiter: OpenAIRateLimiter;

  beforeEach(() => {
    rateLimiter = new OpenAIRateLimiter({
      requestsPerMinute: 10,
      tokensPerMinute: 1000,
      requestsPerDay: 100,
    });
  });

  describe('checkLimit', () => {
    it('should allow requests within limits', async () => {
      const request: RateLimitRequest = {
        model: 'gpt-3.5-turbo',
        requestType: 'chat',
        estimatedTokens: 100,
      };

      const result = await rateLimiter.checkLimit(request);
      expect(result.allowed).toBe(true);
      expect(result.remainingRequests).toBeGreaterThan(0);
    });

    it('should block requests exceeding rate limits', async () => {
      const request: RateLimitRequest = {
        model: 'gpt-3.5-turbo',
        requestType: 'chat',
      };

      // Exhaust the limit
      for (let i = 0; i < 10; i++) {
        await rateLimiter.recordUsage(request);
      }

      const result = await rateLimiter.checkLimit(request);
      expect(result.allowed).toBe(false);
      expect(result.waitTime).toBeGreaterThan(0);
    });
  });
});
```

```typescript
// packages/providers/src/__tests__/openai-retry-manager.test.ts
describe('OpenAIRetryManager', () => {
  let retryManager: OpenAIRetryManager;

  beforeEach(() => {
    retryManager = new OpenAIRetryManager({
      maxAttempts: 3,
      baseDelay: 100,
      maxDelay: 1000,
    });
  });

  describe('executeWithRetry', () => {
    it('should succeed on first attempt', async () => {
      const operation = jest.fn().mockResolvedValue('success');
      const context: RetryContext = {
        operationId: 'test-1',
        operationType: 'chat',
        model: 'gpt-3.5-turbo',
        priority: 'normal',
      };

      const result = await retryManager.executeWithRetry(operation, context);
      expect(result).toBe('success');
      expect(operation).toHaveBeenCalledTimes(1);
    });

    it('should retry on retryable errors', async () => {
      const operation = jest
        .fn()
        .mockRejectedValueOnce({ status: 429 })
        .mockRejectedValueOnce({ status: 500 })
        .mockResolvedValue('success');

      const context: RetryContext = {
        operationId: 'test-2',
        operationType: 'chat',
        model: 'gpt-3.5-turbo',
        priority: 'normal',
      };

      const result = await retryManager.executeWithRetry(operation, context);
      expect(result).toBe('success');
      expect(operation).toHaveBeenCalledTimes(3);
    });

    it('should not retry on non-retryable errors', async () => {
      const operation = jest.fn().mockRejectedValue({ status: 400 });
      const context: RetryContext = {
        operationId: 'test-3',
        operationType: 'chat',
        model: 'gpt-3.5-turbo',
        priority: 'normal',
      };

      await expect(retryManager.executeWithRetry(operation, context)).rejects.toThrow();
      expect(operation).toHaveBeenCalledTimes(1);
    });
  });
});
```

```typescript
// packages/providers/src/__tests__/openai-circuit-breaker.test.ts
describe('OpenAICircuitBreaker', () => {
  let circuitBreaker: OpenAICircuitBreaker;

  beforeEach(() => {
    circuitBreaker = new OpenAICircuitBreaker({
      failureThreshold: 3,
      recoveryTimeout: 1000,
      monitoringPeriod: 5000,
      expectedRecoveryTime: 2000,
      halfOpenMaxCalls: 2,
    });
  });

  describe('execute', () => {
    it('should allow operations when closed', async () => {
      const operation = jest.fn().mockResolvedValue('success');
      const context = { operationId: 'test-1', operationType: 'chat' };

      const result = await circuitBreaker.execute(operation, context);
      expect(result).toBe('success');
      expect(circuitBreaker.getState()).toBe(CircuitBreakerState.CLOSED);
    });

    it('should open circuit after failure threshold', async () => {
      const operation = jest.fn().mockRejectedValue(new Error('API error'));
      const context = { operationId: 'test-2', operationType: 'chat' };

      // Fail 3 times to trigger circuit breaker
      for (let i = 0; i < 3; i++) {
        try {
          await circuitBreaker.execute(operation, context);
        } catch (error) {
          // Expected to fail
        }
      }

      expect(circuitBreaker.getState()).toBe(CircuitBreakerState.OPEN);
    });

    it('should block operations when open', async () => {
      const operation = jest.fn().mockResolvedValue('success');
      const context = { operationId: 'test-3', operationType: 'chat' };

      // Force open
      circuitBreaker.forceOpen();

      await expect(circuitBreaker.execute(operation, context)).rejects.toThrow(
        'Circuit breaker is OPEN'
      );
      expect(operation).not.toHaveBeenCalled();
    });
  });
});
```

### Integration Tests

```typescript
// packages/providers/src/__tests__/integration/openai-provider-resilience.test.ts
describe('OpenAI Provider Resilience Integration', () => {
  let provider: OpenAIProvider;
  let testConfig: OpenAIProviderConfig;

  beforeAll(() => {
    testConfig = {
      apiKey: process.env.OPENAI_API_KEY || 'test-key',
      rateLimit: {
        requestsPerMinute: 60, // Conservative for testing
        tokensPerMinute: 40000,
        requestsPerDay: 1000,
      },
      retry: {
        maxAttempts: 2,
        baseDelay: 500,
        maxDelay: 5000,
      },
      circuitBreaker: {
        failureThreshold: 3,
        recoveryTimeout: 30000,
        monitoringPeriod: 60000,
        expectedRecoveryTime: 10000,
        halfOpenMaxCalls: 2,
      },
    };
  });

  beforeEach(() => {
    provider = new OpenAIProvider(testConfig);
  });

  it('should handle rate limiting gracefully', async () => {
    // Make multiple rapid requests to test rate limiting
    const requests = Array.from({ length: 5 }, (_, i) => ({
      id: `rate-test-${i}`,
      userId: 'test-user',
      model: 'gpt-3.5-turbo' as const,
      messages: [{ role: 'user' as const, content: 'Say "Hello"' }],
      temperature: 0.7,
    }));

    const results = await Promise.allSettled(requests.map((req) => provider.sendMessage(req)));

    // Some should succeed, some should be rate limited
    const successes = results.filter((r) => r.status === 'fulfilled');
    const failures = results.filter((r) => r.status === 'rejected');

    expect(successes.length + failures.length).toBe(requests.length);
    expect(successes.length).toBeGreaterThan(0);
  }, 30000);
});
```

## Security Considerations

1. **Rate Limit Bypass Prevention**:
   - Implement strict rate limiting per user/organization
   - Monitor for rate limit bypass attempts
   - Use multiple rate limiting strategies (sliding window, token bucket)

2. **Retry Attack Prevention**:
   - Limit retry attempts to prevent abuse
   - Add jitter to prevent thundering herd problems
   - Monitor retry patterns for anomalies

3. **Circuit Breaker Security**:
   - Prevent circuit breaker manipulation
   - Log all state changes for audit
   - Implement manual override capabilities for administrators

## Dependencies

### New Dependencies

```json
{
  "eventemitter3": "^5.0.1"
}
```

### Dev Dependencies

```json
{
  "@types/eventemitter3": "^2.0.2"
}
```

## Monitoring and Observability

1. **Metrics to Track**:
   - Rate limit hit rates per user/model
   - Retry attempt frequencies and success rates
   - Circuit breaker state transitions
   - API response times and error rates

2. **Logging**:
   - Rate limit violations and wait times
   - Retry attempts with delays and outcomes
   - Circuit breaker state changes
   - Performance impact of resilience features

3. **Alerts**:
   - High rate limit hit rates
   - Excessive retry attempts
   - Circuit breaker openings
   - Degraded performance

## Acceptance Criteria

1. ✅ **Rate Limiting**: Accurate rate limiting respecting OpenAI API limits
2. ✅ **Retry Logic**: Intelligent retry with exponential backoff and jitter
3. ✅ **Circuit Breaker**: Reliable circuit breaker preventing cascading failures
4. ✅ **Integration**: Seamless integration with OpenAI provider
5. ✅ **Performance**: Minimal overhead on normal operations
6. ✅ **Testing**: Comprehensive unit and integration test coverage
7. ✅ **Monitoring**: Complete metrics and alerting for resilience features
8. ✅ **Configuration**: Flexible configuration for different environments
9. ✅ **Error Handling**: Graceful handling of all failure scenarios
10. ✅ **Documentation**: Clear documentation for resilience patterns

## Success Metrics

- Rate limit accuracy > 99%
- Retry success rate > 80% for retryable errors
- Circuit breaker response time < 10ms
- Zero false positives in rate limiting
- Complete audit trail for all resilience actions
- Performance overhead < 5% on normal operations
