/**
 * Database retry logic with exponential backoff
 * Provides resilient database operations with automatic retry on transient failures
 */

import { metrics, createChildLogger } from '../observability/logger';

export interface RetryOptions {
  maxAttempts: number;
  baseDelay: number;
  maxDelay: number;
  backoffMultiplier: number;
  jitter: boolean;
  retryableErrors: string[];
}

export const DEFAULT_RETRY_OPTIONS: RetryOptions = {
  maxAttempts: 5,
  baseDelay: 1000, // 1 second
  maxDelay: 30000, // 30 seconds
  backoffMultiplier: 2,
  jitter: true,
  retryableErrors: [
    'connection timeout',
    'connection refused',
    'connection lost',
    'connection reset',
    'timeout expired',
    'database is locked',
    'too many connections',
    'connection pool exhausted',
    'ECONNRESET',
    'ECONNREFUSED',
    'ETIMEDOUT',
    'ENOTFOUND',
    'EHOSTUNREACH',
    'ENETUNREACH',
    'EPIPE',
    'EAI_AGAIN',
  ],
};

// Database-specific retry options
export const DATABASE_RETRY_OPTIONS: RetryOptions = {
  ...DEFAULT_RETRY_OPTIONS,
  maxAttempts: 3,
  baseDelay: 500,
  maxDelay: 5000,
  retryableErrors: [
    ...DEFAULT_RETRY_OPTIONS.retryableErrors,
    'terminating connection due to administrator command',
    'server closed the connection unexpectedly',
    'no connection to the server',
    'could not connect to server',
  ],
};

// Transaction-specific retry options (for serialization failures)
export const TRANSACTION_RETRY_OPTIONS: RetryOptions = {
  ...DEFAULT_RETRY_OPTIONS,
  maxAttempts: 5,
  baseDelay: 100,
  maxDelay: 2000,
  retryableErrors: [
    ...DEFAULT_RETRY_OPTIONS.retryableErrors,
    'could not serialize access',
    'deadlock detected',
    'tuple concurrently updated',
    'serialization failure',
    'concurrent update',
    'lock timeout',
  ],
};

export class RetryError extends Error {
  public readonly attempts: number;
  public readonly totalDelay: number;
  public readonly lastError: Error;
  public readonly errors: Error[];

  constructor(
    message: string,
    attempts: number,
    totalDelay: number,
    lastError: Error,
    errors: Error[] = []
  ) {
    super(message);
    this.name = 'RetryError';
    this.attempts = attempts;
    this.totalDelay = totalDelay;
    this.lastError = lastError;
    this.errors = errors;
  }
}

/**
 * Determines if an error is retryable based on error patterns
 */
function isRetryableError(error: Error, retryableErrors: string[]): boolean {
  const errorMessage = error.message?.toLowerCase() || '';
  const errorCode = (error as any).code?.toLowerCase() || '';
  const errorName = error.name?.toLowerCase() || '';

  // Check against retryable error patterns
  for (const pattern of retryableErrors) {
    const patternLower = pattern.toLowerCase();
    if (
      errorMessage.includes(patternLower) ||
      errorCode === patternLower ||
      errorName.includes(patternLower)
    ) {
      return true;
    }
  }

  // Check for specific error codes
  const retryableCodes = [
    'ECONNRESET',
    'ECONNREFUSED',
    'ETIMEDOUT',
    'ENOTFOUND',
    'EHOSTUNREACH',
    'ENETUNREACH',
    'EPIPE',
    'EAI_AGAIN',
    '08006', // PostgreSQL connection_failure
    '08001', // PostgreSQL sqlclient_unable_to_establish_sqlconnection
    '08004', // PostgreSQL sqlserver_rejected_establishment_of_sqlconnection
    '57P03', // PostgreSQL cannot_connect_now
    '40001', // PostgreSQL serialization_failure
    '40P01', // PostgreSQL deadlock_detected
  ];

  if (retryableCodes.includes((error as any).code)) {
    return true;
  }

  // Check for timeout errors
  if (
    errorMessage.includes('timeout') ||
    errorMessage.includes('timed out') ||
    errorCode === 'TIMEOUT'
  ) {
    return true;
  }

  return false;
}

/**
 * Calculates the delay for the next retry attempt
 */
function calculateDelay(
  attempt: number,
  options: RetryOptions
): number {
  let delay = Math.min(
    options.baseDelay * Math.pow(options.backoffMultiplier, attempt - 1),
    options.maxDelay
  );

  // Add jitter to prevent thundering herd
  if (options.jitter) {
    // Random jitter between 0.5x and 1.5x the calculated delay
    delay = delay * (0.5 + Math.random());
  }

  return Math.round(delay);
}

/**
 * Sleep for specified milliseconds
 */
function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/**
 * Retry an operation with exponential backoff
 */
export async function retryWithBackoff<T>(
  operation: () => Promise<T>,
  options: Partial<RetryOptions> = {},
  context: Record<string, any> = {}
): Promise<T> {
  const opts = { ...DEFAULT_RETRY_OPTIONS, ...options };
  const errors: Error[] = [];
  let lastError: Error;
  let totalDelay = 0;

  const retryLogger = createChildLogger({
    component: 'retry-logic',
    ...context
  });

  for (let attempt = 1; attempt <= opts.maxAttempts; attempt++) {
    try {
      // Record attempt metric
      metrics.incrementCounter('database.retry.attempt', {
        attempt: attempt.toString(),
        ...context,
      });

      const startTime = Date.now();
      const result = await operation();
      const duration = Date.now() - startTime;

      // Record success metric
      metrics.recordHistogram('database.operation.duration', duration, {
        status: 'success',
        attempt: attempt.toString(),
        ...context,
      });

      if (attempt > 1) {
        retryLogger.info('Operation succeeded after retry', {
          attempt,
          totalDelay,
          duration,
        });

        metrics.incrementCounter('database.retry.success', {
          attempt: attempt.toString(),
          ...context,
        });
      }

      return result;
    } catch (error) {
      lastError = error as Error;
      errors.push(lastError);

      const isRetryable = isRetryableError(lastError, opts.retryableErrors);

      retryLogger.debug('Operation failed', {
        attempt,
        maxAttempts: opts.maxAttempts,
        isRetryable,
        error: lastError.message,
        errorCode: (lastError as any).code,
      });

      // Record failure metric
      metrics.incrementCounter('database.retry.failure', {
        attempt: attempt.toString(),
        retryable: isRetryable.toString(),
        errorCode: (lastError as any).code || 'unknown',
        ...context,
      });

      if (!isRetryable || attempt === opts.maxAttempts) {
        if (!isRetryable) {
          retryLogger.error('Operation failed with non-retryable error', {
            attempt,
            error: lastError.message,
            errorCode: (lastError as any).code,
            stack: lastError.stack,
          });
        } else {
          retryLogger.error('Operation failed after max retry attempts', {
            attempt,
            maxAttempts: opts.maxAttempts,
            totalDelay,
            error: lastError.message,
            errorCode: (lastError as any).code,
          });
        }

        throw new RetryError(
          `Operation failed after ${attempt} attempt(s): ${lastError.message}`,
          attempt,
          totalDelay,
          lastError,
          errors
        );
      }

      // Calculate and apply backoff delay
      const delay = calculateDelay(attempt, opts);
      totalDelay += delay;

      retryLogger.warn('Operation failed, retrying with backoff', {
        attempt,
        maxAttempts: opts.maxAttempts,
        delay,
        totalDelay,
        error: lastError.message,
        errorCode: (lastError as any).code,
      });

      await sleep(delay);
    }
  }

  // Should never reach here, but TypeScript needs this
  throw new RetryError(
    `Operation failed after ${opts.maxAttempts} attempts`,
    opts.maxAttempts,
    totalDelay,
    lastError!,
    errors
  );
}

/**
 * Database-specific retry wrapper with optimized settings
 */
export function withDatabaseRetry<T>(
  operation: () => Promise<T>,
  context: Record<string, any> = {}
): Promise<T> {
  return retryWithBackoff(
    operation,
    DATABASE_RETRY_OPTIONS,
    { ...context, retryType: 'database' }
  );
}

/**
 * Transaction retry wrapper for handling serialization failures
 */
export function withTransactionRetry<T>(
  operation: () => Promise<T>,
  context: Record<string, any> = {}
): Promise<T> {
  return retryWithBackoff(
    operation,
    TRANSACTION_RETRY_OPTIONS,
    { ...context, retryType: 'transaction' }
  );
}

/**
 * Circuit breaker pattern for protecting against cascading failures
 */
export class CircuitBreaker {
  private failures = 0;
  private lastFailureTime = 0;
  private state: 'CLOSED' | 'OPEN' | 'HALF_OPEN' = 'CLOSED';

  constructor(
    private readonly threshold: number = 5,
    _timeout: number = 60000, // 1 minute - reserved for future use
    private readonly resetTimeout: number = 30000 // 30 seconds
  ) {
    // timeout parameter reserved for future implementation
  }

  async execute<T>(
    operation: () => Promise<T>,
    context: Record<string, any> = {}
  ): Promise<T> {
    const cbLogger = createChildLogger({
      component: 'circuit-breaker',
      ...context
    });

    // Check if circuit is open
    if (this.state === 'OPEN') {
      const timeSinceLastFailure = Date.now() - this.lastFailureTime;

      if (timeSinceLastFailure > this.resetTimeout) {
        this.state = 'HALF_OPEN';
        cbLogger.info('Circuit breaker transitioning to HALF_OPEN state');
      } else {
        cbLogger.warn('Circuit breaker is OPEN, rejecting request');
        metrics.incrementCounter('circuit_breaker.rejected', context);
        throw new Error('Circuit breaker is OPEN');
      }
    }

    try {
      const result = await operation();

      // Reset on success
      if (this.state === 'HALF_OPEN') {
        this.state = 'CLOSED';
        this.failures = 0;
        cbLogger.info('Circuit breaker reset to CLOSED state');
      }

      metrics.incrementCounter('circuit_breaker.success', context);
      return result;
    } catch (error) {
      this.failures++;
      this.lastFailureTime = Date.now();

      if (this.failures >= this.threshold) {
        this.state = 'OPEN';
        cbLogger.error('Circuit breaker tripped to OPEN state', {
          failures: this.failures,
          threshold: this.threshold,
        });
        metrics.incrementCounter('circuit_breaker.tripped', context);
      }

      metrics.incrementCounter('circuit_breaker.failure', context);
      throw error;
    }
  }

  getState(): string {
    return this.state;
  }

  reset(): void {
    this.state = 'CLOSED';
    this.failures = 0;
    this.lastFailureTime = 0;
  }
}

// Export a default circuit breaker instance
export const defaultCircuitBreaker = new CircuitBreaker();