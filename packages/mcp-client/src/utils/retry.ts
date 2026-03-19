/**
 * @tamma/mcp-client
 * Retry utilities with exponential backoff
 */

/**
 * Retry options
 */
export interface RetryOptions {
  /** Maximum number of retry attempts (default: 3) */
  maxAttempts: number;
  /** Initial delay in milliseconds (default: 1000) */
  initialDelayMs: number;
  /** Maximum delay in milliseconds (default: 30000) */
  maxDelayMs: number;
  /** Backoff multiplier (default: 2) */
  backoffMultiplier: number;
  /** Add jitter to prevent thundering herd (default: true) */
  useJitter: boolean;
  /** Abort signal for cancellation */
  signal?: AbortSignal;
  /** Function to determine if an error is retryable */
  isRetryable?: (error: Error) => boolean;
  /** Callback invoked before each retry attempt */
  onRetry?: (attempt: number, error: Error, delayMs: number) => void;
}

/**
 * Default retry options
 */
export const DEFAULT_RETRY_OPTIONS: RetryOptions = {
  maxAttempts: 3,
  initialDelayMs: 1000,
  maxDelayMs: 30000,
  backoffMultiplier: 2,
  useJitter: true,
  isRetryable: () => true,
};

/**
 * Calculate delay for a specific attempt with exponential backoff
 */
export function calculateDelay(
  attempt: number,
  options: Pick<RetryOptions, 'initialDelayMs' | 'maxDelayMs' | 'backoffMultiplier' | 'useJitter'>
): number {
  // Exponential backoff: initialDelay * multiplier^attempt
  const exponentialDelay = options.initialDelayMs * Math.pow(options.backoffMultiplier, attempt - 1);

  // Cap at max delay
  const cappedDelay = Math.min(exponentialDelay, options.maxDelayMs);

  // Add jitter (random value between 0 and delay)
  if (options.useJitter) {
    const jitter = Math.random() * cappedDelay * 0.1; // 10% jitter
    return Math.floor(cappedDelay + jitter);
  }

  return Math.floor(cappedDelay);
}

/**
 * Sleep for a specified duration
 */
export function sleep(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) {
      reject(new Error('Operation aborted'));
      return;
    }

    const onAbort = () => {
      clearTimeout(timeoutId);
      reject(new Error('Operation aborted'));
    };

    const timeoutId = setTimeout(() => {
      signal?.removeEventListener('abort', onAbort);
      resolve();
    }, ms);

    signal?.addEventListener('abort', onAbort, { once: true });
  });
}

/**
 * Retry context passed to retry operations
 */
export interface RetryContext {
  attempt: number;
  maxAttempts: number;
  lastError?: Error;
}

/**
 * Execute a function with retry logic
 */
export async function withRetry<T>(
  fn: (context: RetryContext) => Promise<T>,
  options: Partial<RetryOptions> = {}
): Promise<T> {
  const opts: RetryOptions = { ...DEFAULT_RETRY_OPTIONS, ...options };

  let lastError: Error | undefined;

  for (let attempt = 1; attempt <= opts.maxAttempts; attempt++) {
    const context: RetryContext = {
      attempt,
      maxAttempts: opts.maxAttempts,
    };
    if (lastError !== undefined) context.lastError = lastError;

    try {
      // Check if aborted before attempting
      if (opts.signal?.aborted) {
        throw new Error('Operation aborted');
      }

      return await fn(context);
    } catch (error) {
      lastError = error instanceof Error ? error : new Error(String(error));

      // Check if we should retry
      const isRetryable = opts.isRetryable?.(lastError) ?? true;
      const hasMoreAttempts = attempt < opts.maxAttempts;

      if (!isRetryable || !hasMoreAttempts) {
        throw lastError;
      }

      // Calculate delay for next attempt
      const delayMs = calculateDelay(attempt, opts);

      // Invoke retry callback
      opts.onRetry?.(attempt, lastError, delayMs);

      // Wait before next attempt
      await sleep(delayMs, opts.signal);
    }
  }

  // Should never reach here, but TypeScript needs this
  throw lastError ?? new Error('Retry failed');
}

/**
 * Create a retryable version of a function
 */
export function retryable<TArgs extends unknown[], TResult>(
  fn: (...args: TArgs) => Promise<TResult>,
  options: Partial<RetryOptions> = {}
): (...args: TArgs) => Promise<TResult> {
  return (...args: TArgs) => withRetry(() => fn(...args), options);
}

/**
 * Circuit breaker state
 */
export type CircuitState = 'closed' | 'open' | 'half-open';

/**
 * Circuit breaker options
 */
export interface CircuitBreakerOptions {
  /** Number of failures before opening the circuit (default: 5) */
  failureThreshold: number;
  /** Time in ms to wait before trying again (default: 60000) */
  resetTimeoutMs: number;
  /** Number of successful calls to close the circuit (default: 1) */
  successThreshold: number;
}

/**
 * Default circuit breaker options
 */
export const DEFAULT_CIRCUIT_BREAKER_OPTIONS: CircuitBreakerOptions = {
  failureThreshold: 5,
  resetTimeoutMs: 60000,
  successThreshold: 1,
};

/**
 * Circuit breaker for protecting against cascading failures
 */
export class CircuitBreaker {
  private state: CircuitState = 'closed';
  private failureCount = 0;
  private successCount = 0;
  private lastFailureTime?: number;
  private readonly options: CircuitBreakerOptions;

  constructor(options: Partial<CircuitBreakerOptions> = {}) {
    this.options = { ...DEFAULT_CIRCUIT_BREAKER_OPTIONS, ...options };
  }

  /**
   * Get the current state of the circuit
   */
  getState(): CircuitState {
    if (this.state === 'open') {
      // Check if it's time to try again
      const now = Date.now();
      if (this.lastFailureTime && now - this.lastFailureTime >= this.options.resetTimeoutMs) {
        this.state = 'half-open';
        this.successCount = 0;
      }
    }
    return this.state;
  }

  /**
   * Check if the circuit allows requests
   */
  isAllowed(): boolean {
    return this.getState() !== 'open';
  }

  /**
   * Record a successful call
   */
  recordSuccess(): void {
    this.failureCount = 0;

    if (this.state === 'half-open') {
      this.successCount += 1;
      if (this.successCount >= this.options.successThreshold) {
        this.state = 'closed';
        this.successCount = 0;
      }
    }
  }

  /**
   * Record a failed call
   */
  recordFailure(): void {
    this.failureCount += 1;
    this.lastFailureTime = Date.now();
    this.successCount = 0;

    if (this.failureCount >= this.options.failureThreshold) {
      this.state = 'open';
    }
  }

  /**
   * Execute a function with circuit breaker protection
   */
  async execute<T>(fn: () => Promise<T>): Promise<T> {
    if (!this.isAllowed()) {
      throw new Error('Circuit breaker is open');
    }

    try {
      const result = await fn();
      this.recordSuccess();
      return result;
    } catch (error) {
      this.recordFailure();
      throw error;
    }
  }

  /**
   * Reset the circuit breaker
   */
  reset(): void {
    this.state = 'closed';
    this.failureCount = 0;
    this.successCount = 0;
    delete this.lastFailureTime;
  }
}
