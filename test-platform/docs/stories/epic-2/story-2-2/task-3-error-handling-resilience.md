# Task 3: Error Handling & Resilience

**Story**: 2.2 - Anthropic Claude Provider Implementation  
**Status**: ready-for-dev  
**Priority**: High

## Overview

Implement comprehensive error handling and resilience patterns for the Anthropic Claude provider, including exponential backoff retry logic, circuit breaker pattern, and custom error types. This ensures reliable operation even under adverse conditions.

## Detailed Implementation Plan

### Subtask 3.1: Implement Exponential Backoff Retry Logic

**File**: `src/providers/anthropic/resilience/retry-handler.ts`

```typescript
import { logger } from '@tamma/observability';
import { AnthropicError } from '../errors/anthropic-error';

export interface RetryOptions {
  maxAttempts: number;
  baseDelay: number;
  maxDelay: number;
  backoffMultiplier: number;
  jitter: boolean;
  retryableErrors: string[];
}

export interface RetryResult<T> {
  result: T;
  attempts: number;
  totalDelay: number;
  lastError?: Error;
}

export class RetryHandler {
  private readonly defaultOptions: RetryOptions = {
    maxAttempts: 3,
    baseDelay: 1000,
    maxDelay: 10000,
    backoffMultiplier: 2,
    jitter: true,
    retryableErrors: [
      'rate_limit_error',
      'timeout_error',
      'connection_error',
      'temporary_error',
      'overloaded_error',
    ],
  };

  async executeWithRetry<T>(
    operation: () => Promise<T>,
    options: Partial<RetryOptions> = {},
    context: Record<string, unknown> = {}
  ): Promise<RetryResult<T>> {
    const config = { ...this.defaultOptions, ...options };
    let attempts = 0;
    let totalDelay = 0;
    let lastError: Error | undefined;

    logger.debug('Starting retry operation', {
      maxAttempts: config.maxAttempts,
      baseDelay: config.baseDelay,
      context,
    });

    while (attempts < config.maxAttempts) {
      attempts++;

      try {
        const result = await operation();

        if (attempts > 1) {
          logger.info('Operation succeeded after retries', {
            attempts,
            totalDelay,
            context,
          });
        }

        return {
          result,
          attempts,
          totalDelay,
          lastError,
        };
      } catch (error) {
        lastError = error as Error;

        // Check if error is retryable
        if (!this.isRetryableError(error, config.retryableErrors)) {
          logger.debug('Error is not retryable', {
            error: error.message,
            errorCode: this.getErrorCode(error),
            attempts,
            context,
          });
          throw error;
        }

        // Check if we've exhausted attempts
        if (attempts >= config.maxAttempts) {
          logger.error('Operation failed after all retry attempts', {
            attempts,
            maxAttempts: config.maxAttempts,
            totalDelay,
            lastError: lastError.message,
            context,
          });

          throw new AnthropicError(
            'RETRY_EXHAUSTED',
            `Operation failed after ${attempts} attempts`,
            {
              cause: lastError,
              attempts,
              totalDelay,
              context,
            }
          );
        }

        // Calculate delay for next attempt
        const delay = this.calculateDelay(attempts, config);
        totalDelay += delay;

        logger.warn('Operation failed, retrying', {
          attempts,
          maxAttempts: config.maxAttempts,
          delay,
          totalDelay,
          error: lastError.message,
          errorCode: this.getErrorCode(lastError),
          context,
        });

        await this.sleep(delay);
      }
    }

    // This should never be reached, but TypeScript requires it
    throw new AnthropicError('UNEXPECTED_RETRY_STATE', 'Unexpected state in retry handler', {
      attempts,
      totalDelay,
      lastError,
    });
  }

  private isRetryableError(error: Error, retryableErrors: string[]): boolean {
    const errorCode = this.getErrorCode(error);

    // Check against known retryable error codes
    if (retryableErrors.includes(errorCode)) {
      return true;
    }

    // Check HTTP status codes
    const statusCode = this.getStatusCode(error);
    if (statusCode) {
      // 5xx errors are generally retryable
      if (statusCode >= 500 && statusCode < 600) {
        return true;
      }

      // 429 (Too Many Requests) is retryable
      if (statusCode === 429) {
        return true;
      }

      // 408 (Request Timeout) is retryable
      if (statusCode === 408) {
        return true;
      }
    }

    // Check for network-related errors
    if (this.isNetworkError(error)) {
      return true;
    }

    return false;
  }

  private getErrorCode(error: Error): string {
    // Try to extract error code from Anthropic errors
    if ('error' in error && typeof error.error === 'object') {
      const errorObj = error.error as any;
      return errorObj.type || errorObj.code || 'unknown_error';
    }

    // Try to extract from error message
    const message = error.message.toLowerCase();

    if (message.includes('rate limit')) return 'rate_limit_error';
    if (message.includes('timeout')) return 'timeout_error';
    if (message.includes('connection')) return 'connection_error';
    if (message.includes('overloaded')) return 'overloaded_error';

    return 'unknown_error';
  }

  private getStatusCode(error: Error): number | null {
    // Try to extract status code from error
    if ('status' in error && typeof error.status === 'number') {
      return error.status;
    }

    if ('response' in error && error.response && typeof error.response === 'object') {
      const response = error.response as any;
      if (typeof response.status === 'number') {
        return response.status;
      }
    }

    return null;
  }

  private isNetworkError(error: Error): boolean {
    const message = error.message.toLowerCase();

    return (
      message.includes('econnreset') ||
      message.includes('enotfound') ||
      message.includes('econnrefused') ||
      message.includes('etimedout') ||
      message.includes('network') ||
      message.includes('dns')
    );
  }

  private calculateDelay(attempt: number, config: RetryOptions): number {
    // Exponential backoff: baseDelay * (backoffMultiplier ^ (attempt - 1))
    let delay = config.baseDelay * Math.pow(config.backoffMultiplier, attempt - 1);

    // Apply maximum delay limit
    delay = Math.min(delay, config.maxDelay);

    // Add jitter if enabled
    if (config.jitter) {
      // Add random jitter between 0-25% of delay
      const jitterAmount = delay * 0.25 * Math.random();
      delay += jitterAmount;
    }

    return Math.floor(delay);
  }

  private sleep(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms));
  }

  // Static factory methods for common configurations
  static forAPIRequests(): RetryHandler {
    return new RetryHandler();
  }

  static forStreaming(): RetryHandler {
    return new RetryHandler({
      maxAttempts: 2, // Fewer retries for streaming
      baseDelay: 500,
      maxDelay: 5000,
      backoffMultiplier: 1.5,
    });
  }

  static forInitialization(): RetryHandler {
    return new RetryHandler({
      maxAttempts: 5, // More retries for initialization
      baseDelay: 2000,
      maxDelay: 30000,
      backoffMultiplier: 2,
    });
  }
}
```

### Subtask 3.2: Add Circuit Breaker Pattern

**File**: `src/providers/anthropic/resilience/circuit-breaker.ts`

```typescript
import { logger } from '@tamma/observability';
import { EventEmitter } from 'events';

export interface CircuitBreakerOptions {
  failureThreshold: number;
  resetTimeout: number;
  monitoringPeriod: number;
  expectedRecoveryTime: number;
}

export enum CircuitState {
  CLOSED = 'closed',
  OPEN = 'open',
  HALF_OPEN = 'half_open',
}

export interface CircuitBreakerMetrics {
  state: CircuitState;
  failures: number;
  successes: number;
  lastFailureTime?: number;
  lastSuccessTime?: number;
  totalRequests: number;
}

export class CircuitBreaker extends EventEmitter {
  private state: CircuitState = CircuitState.CLOSED;
  private failures: number = 0;
  private successes: number = 0;
  private lastFailureTime?: number;
  private lastSuccessTime?: number;
  private totalRequests: number = 0;
  private resetTimer?: NodeJS.Timeout;

  constructor(private options: CircuitBreakerOptions) {
    super();

    logger.debug('Circuit breaker initialized', {
      failureThreshold: options.failureThreshold,
      resetTimeout: options.resetTimeout,
      monitoringPeriod: options.monitoringPeriod,
    });
  }

  async execute<T>(operation: () => Promise<T>, context: Record<string, unknown> = {}): Promise<T> {
    this.totalRequests++;

    if (this.state === CircuitState.OPEN) {
      if (this.shouldAttemptReset()) {
        this.transitionToHalfOpen();
      } else {
        const error = new AnthropicError(
          'CIRCUIT_BREAKER_OPEN',
          'Circuit breaker is open - operation blocked',
          {
            state: this.state,
            failures: this.failures,
            timeUntilReset: this.getTimeUntilReset(),
            context,
          }
        );

        logger.warn('Circuit breaker blocked operation', {
          state: this.state,
          failures: this.failures,
          context,
        });

        throw error;
      }
    }

    try {
      const result = await operation();
      this.onSuccess();
      return result;
    } catch (error) {
      this.onFailure();
      throw error;
    }
  }

  private onSuccess(): void {
    this.successes++;
    this.lastSuccessTime = Date.now();

    if (this.state === CircuitState.HALF_OPEN) {
      this.transitionToClosed();
    }

    logger.debug('Circuit breaker operation succeeded', {
      state: this.state,
      successes: this.successes,
      failures: this.failures,
    });
  }

  private onFailure(): void {
    this.failures++;
    this.lastFailureTime = Date.now();

    if (this.state === CircuitState.CLOSED) {
      if (this.failures >= this.options.failureThreshold) {
        this.transitionToOpen();
      }
    } else if (this.state === CircuitState.HALF_OPEN) {
      this.transitionToOpen();
    }

    logger.warn('Circuit breaker operation failed', {
      state: this.state,
      failures: this.failures,
      threshold: this.options.failureThreshold,
    });
  }

  private transitionToOpen(): void {
    this.state = CircuitState.OPEN;

    // Set reset timer
    this.resetTimer = setTimeout(() => {
      this.transitionToHalfOpen();
    }, this.options.resetTimeout);

    logger.warn('Circuit breaker opened', {
      failures: this.failures,
      resetTimeout: this.options.resetTimeout,
    });

    this.emit('stateChanged', {
      from: CircuitState.CLOSED,
      to: CircuitState.OPEN,
      failures: this.failures,
    });
  }

  private transitionToClosed(): void {
    this.state = CircuitState.CLOSED;
    this.failures = 0;
    this.successes = 0;

    if (this.resetTimer) {
      clearTimeout(this.resetTimer);
      this.resetTimer = undefined;
    }

    logger.info('Circuit breaker closed', {
      totalRequests: this.totalRequests,
    });

    this.emit('stateChanged', {
      from: CircuitState.HALF_OPEN,
      to: CircuitState.CLOSED,
    });
  }

  private transitionToHalfOpen(): void {
    this.state = CircuitState.HALF_OPEN;

    if (this.resetTimer) {
      clearTimeout(this.resetTimer);
      this.resetTimer = undefined;
    }

    logger.info('Circuit breaker half-open', {
      failures: this.failures,
    });

    this.emit('stateChanged', {
      from: CircuitState.OPEN,
      to: CircuitState.HALF_OPEN,
    });
  }

  private shouldAttemptReset(): boolean {
    return this.lastFailureTime
      ? Date.now() - this.lastFailureTime >= this.options.resetTimeout
      : false;
  }

  private getTimeUntilReset(): number {
    if (!this.lastFailureTime) {
      return 0;
    }

    const elapsed = Date.now() - this.lastFailureTime;
    return Math.max(0, this.options.resetTimeout - elapsed);
  }

  getMetrics(): CircuitBreakerMetrics {
    return {
      state: this.state,
      failures: this.failures,
      successes: this.successes,
      lastFailureTime: this.lastFailureTime,
      lastSuccessTime: this.lastSuccessTime,
      totalRequests: this.totalRequests,
    };
  }

  getState(): CircuitState {
    return this.state;
  }

  forceOpen(): void {
    if (this.state !== CircuitState.OPEN) {
      this.transitionToOpen();
    }
  }

  forceClose(): void {
    if (this.state !== CircuitState.CLOSED) {
      this.transitionToClosed();
    }
  }

  reset(): void {
    this.transitionToClosed();
  }

  destroy(): void {
    if (this.resetTimer) {
      clearTimeout(this.resetTimer);
    }
    this.removeAllListeners();
  }

  // Static factory methods
  static forAPIRequests(): CircuitBreaker {
    return new CircuitBreaker({
      failureThreshold: 5, // 5 failures in 60s
      resetTimeout: 300000, // 5 minutes
      monitoringPeriod: 60000, // 1 minute
      expectedRecoveryTime: 60000, // 1 minute
    });
  }

  static forStreaming(): CircuitBreaker {
    return new CircuitBreaker({
      failureThreshold: 3, // More sensitive for streaming
      resetTimeout: 120000, // 2 minutes
      monitoringPeriod: 30000, // 30 seconds
      expectedRecoveryTime: 30000, // 30 seconds
    });
  }
}
```

### Subtask 3.3: Handle Rate Limiting and Quota Errors

**File**: `src/providers/anthropic/resilience/rate-limiter.ts`

```typescript
import { logger } from '@tamma/observability';
import { AnthropicError } from '../errors/anthropic-error';

export interface RateLimitConfig {
  requestsPerMinute: number;
  tokensPerMinute: number;
  requestsPerHour?: number;
  tokensPerHour?: number;
}

export interface RateLimitInfo {
  type: 'requests' | 'tokens';
  limit: number;
  remaining: number;
  resetTime: number;
  retryAfter?: number;
}

export class RateLimiter {
  private requestTimestamps: number[] = [];
  private tokenUsage: Array<{ timestamp: number; tokens: number }> = [];
  private readonly config: RateLimitConfig;

  constructor(config: RateLimitConfig) {
    this.config = config;

    logger.debug('Rate limiter initialized', {
      requestsPerMinute: config.requestsPerMinute,
      tokensPerMinute: config.tokensPerMinute,
    });
  }

  async checkRequestLimit(): Promise<void> {
    const now = Date.now();
    const oneMinuteAgo = now - 60000;

    // Clean old timestamps
    this.requestTimestamps = this.requestTimestamps.filter((timestamp) => timestamp > oneMinuteAgo);

    // Check if we're at the limit
    if (this.requestTimestamps.length >= this.config.requestsPerMinute) {
      const oldestRequest = Math.min(...this.requestTimestamps);
      const retryAfter = Math.ceil((oldestRequest + 60000 - now) / 1000);

      logger.warn('Request rate limit exceeded', {
        currentRequests: this.requestTimestamps.length,
        limit: this.config.requestsPerMinute,
        retryAfter,
      });

      throw new AnthropicError('RATE_LIMIT_EXCEEDED', 'Request rate limit exceeded', {
        type: 'requests',
        limit: this.config.requestsPerMinute,
        current: this.requestTimestamps.length,
        retryAfter,
      });
    }

    // Record this request
    this.requestTimestamps.push(now);
  }

  async checkTokenLimit(tokens: number): Promise<void> {
    const now = Date.now();
    const oneMinuteAgo = now - 60000;

    // Clean old token usage
    this.tokenUsage = this.tokenUsage.filter((usage) => usage.timestamp > oneMinuteAgo);

    // Calculate current token usage
    const currentUsage = this.tokenUsage.reduce((sum, usage) => sum + usage.tokens, 0);

    // Check if adding these tokens would exceed the limit
    if (currentUsage + tokens > this.config.tokensPerMinute) {
      const retryAfter = Math.ceil(60000 / 1000); // Up to 1 minute

      logger.warn('Token rate limit exceeded', {
        currentUsage,
        requestedTokens: tokens,
        limit: this.config.tokensPerMinute,
        retryAfter,
      });

      throw new AnthropicError('TOKEN_LIMIT_EXCEEDED', 'Token rate limit exceeded', {
        type: 'tokens',
        limit: this.config.tokensPerMinute,
        current: currentUsage,
        requested: tokens,
        retryAfter,
      });
    }

    // Record this token usage
    this.tokenUsage.push({ timestamp: now, tokens });
  }

  parseRateLimitHeaders(headers: Record<string, string>): RateLimitInfo | null {
    // Anthropic uses specific rate limit headers
    const requestsRemaining = headers['anthropic-ratelimit-requests-remaining'];
    const requestsLimit = headers['anthropic-ratelimit-requests-limit'];
    const tokensRemaining = headers['anthropic-ratelimit-tokens-remaining'];
    const tokensLimit = headers['anthropic-ratelimit-tokens-limit'];
    const resetTime = headers['anthropic-ratelimit-reset'];

    if (requestsRemaining && requestsLimit) {
      return {
        type: 'requests',
        limit: parseInt(requestsLimit, 10),
        remaining: parseInt(requestsRemaining, 10),
        resetTime: resetTime ? parseInt(resetTime, 10) : Date.now() + 60000,
      };
    }

    if (tokensRemaining && tokensLimit) {
      return {
        type: 'tokens',
        limit: parseInt(tokensLimit, 10),
        remaining: parseInt(tokensRemaining, 10),
        resetTime: resetTime ? parseInt(resetTime, 10) : Date.now() + 60000,
      };
    }

    return null;
  }

  handleRateLimitError(error: any): never {
    const retryAfter = this.extractRetryAfter(error);

    logger.warn('Rate limit error from API', {
      error: error.message,
      retryAfter,
    });

    throw new AnthropicError('API_RATE_LIMIT', 'API rate limit exceeded', {
      retryAfter,
      error: error.message,
    });
  }

  private extractRetryAfter(error: any): number | undefined {
    // Try to extract retry-after from error
    if (error.headers?.['retry-after']) {
      return parseInt(error.headers['retry-after'], 10);
    }

    if (error.error?.error?.retry_after) {
      return parseInt(error.error.error.retry_after, 10);
    }

    // Default retry after for rate limits
    return 60;
  }

  getMetrics(): {
    requestsInLastMinute: number;
    tokensInLastMinute: number;
    requestUtilization: number;
    tokenUtilization: number;
  } {
    const now = Date.now();
    const oneMinuteAgo = now - 60000;

    const recentRequests = this.requestTimestamps.filter(
      (timestamp) => timestamp > oneMinuteAgo
    ).length;

    const recentTokens = this.tokenUsage
      .filter((usage) => usage.timestamp > oneMinuteAgo)
      .reduce((sum, usage) => sum + usage.tokens, 0);

    return {
      requestsInLastMinute: recentRequests,
      tokensInLastMinute: recentTokens,
      requestUtilization: (recentRequests / this.config.requestsPerMinute) * 100,
      tokenUtilization: (recentTokens / this.config.tokensPerMinute) * 100,
    };
  }

  reset(): void {
    this.requestTimestamps = [];
    this.tokenUsage = [];

    logger.debug('Rate limiter reset');
  }
}
```

### Subtask 3.4: Create Custom Error Types

**File**: `src/providers/anthropic/errors/anthropic-error.ts`

```typescript
import { TammaError } from '@tamma/shared/errors';

export class AnthropicError extends TammaError {
  constructor(
    code: string,
    message: string,
    context: Record<string, unknown> = {},
    retryable: boolean = false,
    severity: 'low' | 'medium' | 'high' | 'critical' = 'medium'
  ) {
    super(code, message, context, retryable, severity);
    this.name = 'AnthropicError';
  }
}

export class AnthropicAuthenticationError extends AnthropicError {
  constructor(message: string, context: Record<string, unknown> = {}) {
    super(
      'ANTHROPIC_AUTHENTICATION_ERROR',
      message,
      context,
      false, // Authentication errors are not retryable
      'high'
    );
    this.name = 'AnthropicAuthenticationError';
  }
}

export class AnthropicRateLimitError extends AnthropicError {
  constructor(
    message: string,
    public retryAfter?: number,
    context: Record<string, unknown> = {}
  ) {
    super(
      'ANTHROPIC_RATE_LIMIT_ERROR',
      message,
      { ...context, retryAfter },
      true, // Rate limit errors are retryable
      'medium'
    );
    this.name = 'AnthropicRateLimitError';
  }
}

export class AnthropicModelNotFoundError extends AnthropicError {
  constructor(model: string, availableModels: string[]) {
    super(
      'ANTHROPIC_MODEL_NOT_FOUND',
      `Model ${model} not found`,
      { model, availableModels },
      false,
      'medium'
    );
    this.name = 'AnthropicModelNotFoundError';
  }
}

export class AnthropicTokenLimitError extends AnthropicError {
  constructor(
    message: string,
    public tokenCount: number,
    public maxTokens: number,
    context: Record<string, unknown> = {}
  ) {
    super(
      'ANTHROPIC_TOKEN_LIMIT_ERROR',
      message,
      { ...context, tokenCount, maxTokens },
      false, // Token limit errors need user intervention
      'medium'
    );
    this.name = 'AnthropicTokenLimitError';
  }
}

export class AnthropicContentFilterError extends AnthropicError {
  constructor(
    message: string,
    public filterType: string,
    context: Record<string, unknown> = {}
  ) {
    super(
      'ANTHROPIC_CONTENT_FILTER_ERROR',
      message,
      { ...context, filterType },
      false, // Content filter errors are not retryable
      'medium'
    );
    this.name = 'AnthropicContentFilterError';
  }
}

export class AnthropicNetworkError extends AnthropicError {
  constructor(
    message: string,
    public statusCode?: number,
    context: Record<string, unknown> = {}
  ) {
    super(
      'ANTHROPIC_NETWORK_ERROR',
      message,
      { ...context, statusCode },
      true, // Network errors are retryable
      'medium'
    );
    this.name = 'AnthropicNetworkError';
  }
}

export class AnthropicTimeoutError extends AnthropicError {
  constructor(
    message: string,
    public timeout: number,
    context: Record<string, unknown> = {}
  ) {
    super(
      'ANTHROPIC_TIMEOUT_ERROR',
      message,
      { ...context, timeout },
      true, // Timeout errors are retryable
      'medium'
    );
    this.name = 'AnthropicTimeoutError';
  }
}

export class AnthropicInvalidRequestError extends AnthropicError {
  constructor(
    message: string,
    public parameter?: string,
    context: Record<string, unknown> = {}
  ) {
    super(
      'ANTHROPIC_INVALID_REQUEST_ERROR',
      message,
      { ...context, parameter },
      false, // Invalid request errors are not retryable
      'low'
    );
    this.name = 'AnthropicInvalidRequestError';
  }
}

export class AnthropicOverloadedError extends AnthropicError {
  constructor(message: string, context: Record<string, unknown> = {}) {
    super(
      'ANTHROPIC_OVERLOADED_ERROR',
      message,
      context,
      true, // Overloaded errors are retryable
      'high'
    );
    this.name = 'AnthropicOverloadedError';
  }
}

// Error factory function to create appropriate error type
export function createAnthropicError(error: any): AnthropicError {
  const message = error.message || 'Unknown Anthropic error';
  const statusCode = error.status || error.statusCode;
  const errorCode = error.error?.type || error.error?.error?.type;

  // Handle specific error types based on status code and error type
  if (statusCode === 401 || errorCode === 'authentication_error') {
    return new AnthropicAuthenticationError(error.error?.error?.message || message, {
      statusCode,
      errorCode,
    });
  }

  if (statusCode === 429 || errorCode === 'rate_limit_error') {
    const retryAfter = error.headers?.['retry-after'] || error.error?.error?.retry_after;
    return new AnthropicRateLimitError(error.error?.error?.message || message, retryAfter, {
      statusCode,
      errorCode,
    });
  }

  if (statusCode === 404 || errorCode === 'not_found_error') {
    // Could be model not found or other resource not found
    if (message.toLowerCase().includes('model')) {
      return new AnthropicModelNotFoundError(
        'Unknown model',
        [] // Would need to extract from context
      );
    }
    return new AnthropicInvalidRequestError(message, undefined, { statusCode, errorCode });
  }

  if (statusCode === 400 || errorCode === 'invalid_request_error') {
    return new AnthropicInvalidRequestError(
      error.error?.error?.message || message,
      error.error?.error?.param,
      { statusCode, errorCode }
    );
  }

  if (statusCode === 413 || errorCode === 'token_limit_error') {
    return new AnthropicTokenLimitError(
      error.error?.error?.message || message,
      0, // Would need to extract from context
      0, // Would need to extract from context
      { statusCode, errorCode }
    );
  }

  if (statusCode === 529 || errorCode === 'overloaded_error') {
    return new AnthropicOverloadedError(error.error?.error?.message || message, {
      statusCode,
      errorCode,
    });
  }

  // Network-related errors
  if (statusCode >= 500 || isNetworkError(error)) {
    return new AnthropicNetworkError(message, statusCode, { originalError: error });
  }

  // Timeout errors
  if (isTimeoutError(error)) {
    return new AnthropicTimeoutError(
      message,
      0, // Would need to extract from context
      { originalError: error }
    );
  }

  // Default to generic AnthropicError
  return new AnthropicError('ANTHROPIC_UNKNOWN_ERROR', message, {
    statusCode,
    errorCode,
    originalError: error,
  });
}

function isNetworkError(error: any): boolean {
  const message = error.message?.toLowerCase() || '';

  return (
    message.includes('econnreset') ||
    message.includes('enotfound') ||
    message.includes('econnrefused') ||
    message.includes('network') ||
    message.includes('dns')
  );
}

function isTimeoutError(error: any): boolean {
  const message = error.message?.toLowerCase() || '';

  return (
    message.includes('timeout') ||
    message.includes('etimedout') ||
    error.code === 'ETIMEDOUT' ||
    error.code === 'TIMEOUT'
  );
}
```

## Integration with Main Provider

**Update to**: `src/providers/anthropic/anthropic-claude-provider.ts`

```typescript
// Add these imports and update the provider
import { RetryHandler } from './resilience/retry-handler';
import { CircuitBreaker } from './resilience/circuit-breaker';
import { RateLimiter } from './resilience/rate-limiter';
import { createAnthropicError } from './errors/anthropic-error';

export class AnthropicClaudeProvider implements IAIProvider {
  private readonly retryHandler: RetryHandler;
  private readonly circuitBreaker: CircuitBreaker;
  private readonly rateLimiter: RateLimiter;

  constructor(config: AnthropicProviderConfig) {
    // ... existing constructor code ...

    // Initialize resilience components
    this.retryHandler = RetryHandler.forAPIRequests();
    this.circuitBreaker = CircuitBreaker.forAPIRequests();
    this.rateLimiter = new RateLimiter(
      config.rateLimitConfig || {
        requestsPerMinute: 50,
        tokensPerMinute: 40000,
      }
    );
  }

  async sendMessage(
    request: MessageRequest,
    options: StreamOptions = {}
  ): Promise<AsyncIterable<MessageChunk>> {
    return this.circuitBreaker.execute(
      async () => {
        return this.retryHandler.executeWithRetry(
          async () => {
            // Check rate limits
            await this.rateLimiter.checkRequestLimit();

            // Estimate tokens for rate limiting
            const estimatedTokens = this.estimateTokens(request);
            await this.rateLimiter.checkTokenLimit(estimatedTokens);

            try {
              // Create and return stream
              const baseStream = await this.messageStream.createStream(
                this.client,
                request,
                options
              );

              return baseStream;
            } catch (error) {
              throw createAnthropicError(error);
            }
          },
          {},
          { model: request.model }
        );
      },
      { model: request.model }
    );
  }

  private estimateTokens(request: MessageRequest): number {
    // Simple token estimation - in production, use a proper tokenizer
    const text =
      JSON.stringify(request.messages) +
      (request.system || '') +
      JSON.stringify(request.tools || []);

    // Rough estimate: ~4 characters per token
    return Math.ceil(text.length / 4);
  }

  async dispose(): Promise<void> {
    this.circuitBreaker.destroy();
    this.isInitialized = false;
    logger.info('Anthropic Claude provider disposed');
  }
}
```

## Dependencies

### Internal Dependencies

- `@tamma/shared/errors` - Base TammaError class
- `@tamma/observability` - Logging utilities
- Story 2.1 error handling patterns

### External Dependencies

- Node.js EventEmitter for circuit breaker events

## Testing Strategy

### Unit Tests

```typescript
// tests/providers/anthropic/resilience/retry-handler.test.ts
describe('RetryHandler', () => {
  describe('executeWithRetry', () => {
    it('should succeed on first attempt');
    it('should retry on retryable errors');
    it('should not retry on non-retryable errors');
    it('should respect max attempts');
    it('should apply exponential backoff');
    it('should add jitter when enabled');
  });
});

// tests/providers/anthropic/resilience/circuit-breaker.test.ts
describe('CircuitBreaker', () => {
  describe('state transitions', () => {
    it('should open after failure threshold');
    it('should close after successful reset');
    it('should attempt half-open after timeout');
  });

  describe('execution', () => {
    it('should block operations when open');
    it('should allow operations when closed');
    it('should handle half-open state correctly');
  });
});
```

### Integration Tests

```typescript
// tests/providers/anthropic/resilience/integration.test.ts
describe('Resilience Integration', () => {
  it('should handle real API rate limits');
  it('should recover from network failures');
  it('should respect circuit breaker thresholds');
  it('should retry with appropriate backoff');
});
```

## Risk Mitigation

### Technical Risks

1. **Cascading Failures**: Circuit breaker not preventing failures
   - Mitigation: Proper threshold configuration, monitoring
2. **Excessive Retries**: Retry logic causing additional load
   - Mitigation: Configurable retry limits, backoff with jitter
3. **Rate Limit Evasion**: Rate limiter not preventing API limits
   - Mitigation: Conservative limits, header-based adjustments

### Operational Risks

1. **False Positives**: Circuit breaker opening unnecessarily
   - Mitigation: Appropriate thresholds, monitoring, manual override
2. **Recovery Delays**: Circuit breaker staying open too long
   - Mitigation: Configurable timeouts, half-open state
3. **Resource Exhaustion**: Retry attempts consuming resources
   - Mitigation: Resource limits, proper cleanup

## Deliverables

1. **Retry Handler**: Exponential backoff with jitter
2. **Circuit Breaker**: Failure threshold and recovery logic
3. **Rate Limiter**: Request and token rate limiting
4. **Custom Errors**: Comprehensive error type hierarchy
5. **Integration**: Updated provider with resilience
6. **Unit Tests**: Full test coverage for all components
7. **Integration Tests**: Real API resilience validation
8. **Documentation**: Error handling and resilience guide

## Success Criteria

- [ ] Implements exponential backoff retry with jitter
- [ ] Circuit breaker prevents cascading failures
- [ ] Rate limiting respects API constraints
- [ ] Custom error types provide clear diagnostics
- [ ] All resilience patterns work together seamlessly
- [ ] Comprehensive test coverage
- [ ] Performance under failure conditions
- [ ] Monitoring and observability for resilience metrics

## File Structure

```
src/providers/anthropic/resilience/
├── retry-handler.ts           # Exponential backoff logic
├── circuit-breaker.ts         # Circuit breaker pattern
├── rate-limiter.ts           # Rate limiting implementation
└── index.ts                  # Public exports

src/providers/anthropic/errors/
├── anthropic-error.ts        # Custom error types
└── index.ts                  # Public exports

tests/providers/anthropic/resilience/
├── retry-handler.test.ts
├── circuit-breaker.test.ts
├── rate-limiter.test.ts
└── integration.test.ts
```

This task provides comprehensive error handling and resilience patterns that ensure the Anthropic provider operates reliably even under adverse conditions, with proper monitoring, recovery mechanisms, and graceful degradation.
