/**
 * Tests for database retry logic with exponential backoff
 */

import {
  retryWithBackoff,
  withDatabaseRetry,
  withTransactionRetry,
  RetryError,
  CircuitBreaker,
  DEFAULT_RETRY_OPTIONS,
  DATABASE_RETRY_OPTIONS,
  TRANSACTION_RETRY_OPTIONS,
} from '../retry-logic';

// Mock timers for testing delays
jest.useFakeTimers();

describe('Retry Logic', () => {
  afterEach(() => {
    jest.clearAllTimers();
    jest.clearAllMocks();
  });

  describe('retryWithBackoff', () => {
    it('should succeed on first attempt', async () => {
      const operation = jest.fn().mockResolvedValue('success');

      const result = await retryWithBackoff(operation);

      expect(result).toBe('success');
      expect(operation).toHaveBeenCalledTimes(1);
    });

    it('should retry on retryable errors', async () => {
      const operation = jest.fn()
        .mockRejectedValueOnce(new Error('connection timeout'))
        .mockRejectedValueOnce(new Error('connection refused'))
        .mockResolvedValue('success');

      const promise = retryWithBackoff(operation, {
        maxAttempts: 3,
        baseDelay: 100,
        jitter: false,
      });

      // Fast-forward through delays
      await jest.runAllTimersAsync();

      const result = await promise;

      expect(result).toBe('success');
      expect(operation).toHaveBeenCalledTimes(3);
    });

    it('should not retry on non-retryable errors', async () => {
      const nonRetryableError = new Error('syntax error');
      const operation = jest.fn().mockRejectedValue(nonRetryableError);

      await expect(
        retryWithBackoff(operation, {
          maxAttempts: 3,
          baseDelay: 100,
        })
      ).rejects.toThrow(RetryError);

      expect(operation).toHaveBeenCalledTimes(1);
    });

    it('should respect max attempts limit', async () => {
      const error = new Error('connection timeout');
      const operation = jest.fn().mockRejectedValue(error);

      const promise = retryWithBackoff(operation, {
        maxAttempts: 3,
        baseDelay: 100,
        jitter: false,
      });

      await jest.runAllTimersAsync();

      await expect(promise).rejects.toThrow(RetryError);
      expect(operation).toHaveBeenCalledTimes(3);
    });

    it('should apply exponential backoff', async () => {
      const error = new Error('connection timeout');
      const operation = jest.fn().mockRejectedValue(error);
      const delays: number[] = [];

      // Spy on setTimeout to capture delays
      const originalSetTimeout = global.setTimeout;
      global.setTimeout = jest.fn((fn, delay) => {
        delays.push(delay as number);
        return originalSetTimeout(fn, delay);
      }) as any;

      const promise = retryWithBackoff(operation, {
        maxAttempts: 4,
        baseDelay: 100,
        backoffMultiplier: 2,
        jitter: false,
      });

      await jest.runAllTimersAsync();

      try {
        await promise;
      } catch {
        // Expected to fail
      }

      // Verify exponential delays: 100, 200, 400
      expect(delays[0]).toBe(100);
      expect(delays[1]).toBe(200);
      expect(delays[2]).toBe(400);

      global.setTimeout = originalSetTimeout;
    });

    it('should apply jitter when enabled', async () => {
      const error = new Error('connection timeout');
      const operation = jest.fn().mockRejectedValue(error);

      const promise = retryWithBackoff(operation, {
        maxAttempts: 2,
        baseDelay: 1000,
        jitter: true,
      });

      await jest.runAllTimersAsync();

      try {
        await promise;
      } catch {
        // Expected to fail
      }

      expect(operation).toHaveBeenCalledTimes(2);
    });

    it('should respect max delay limit', async () => {
      const error = new Error('connection timeout');
      const operation = jest.fn().mockRejectedValue(error);
      const delays: number[] = [];

      const originalSetTimeout = global.setTimeout;
      global.setTimeout = jest.fn((fn, delay) => {
        delays.push(delay as number);
        return originalSetTimeout(fn, delay);
      }) as any;

      const promise = retryWithBackoff(operation, {
        maxAttempts: 5,
        baseDelay: 1000,
        maxDelay: 2000,
        backoffMultiplier: 2,
        jitter: false,
      });

      await jest.runAllTimersAsync();

      try {
        await promise;
      } catch {
        // Expected to fail
      }

      // Delays should be: 1000, 2000, 2000, 2000 (capped at maxDelay)
      expect(delays[0]).toBe(1000);
      expect(delays[1]).toBe(2000);
      expect(delays[2]).toBe(2000);
      expect(delays[3]).toBe(2000);

      global.setTimeout = originalSetTimeout;
    });

    it('should detect retryable error codes', async () => {
      const errors = [
        Object.assign(new Error('Test'), { code: 'ECONNRESET' }),
        Object.assign(new Error('Test'), { code: 'ECONNREFUSED' }),
        Object.assign(new Error('Test'), { code: 'ETIMEDOUT' }),
        Object.assign(new Error('Test'), { code: '40001' }), // Serialization failure
        Object.assign(new Error('Test'), { code: '40P01' }), // Deadlock
      ];

      for (const error of errors) {
        const operation = jest.fn()
          .mockRejectedValueOnce(error)
          .mockResolvedValue('success');

        const promise = retryWithBackoff(operation, {
          maxAttempts: 2,
          baseDelay: 10,
          jitter: false,
        });

        await jest.runAllTimersAsync();

        const result = await promise;
        expect(result).toBe('success');
        expect(operation).toHaveBeenCalledTimes(2);

        jest.clearAllMocks();
      }
    });
  });

  describe('withDatabaseRetry', () => {
    it('should use database-specific retry options', async () => {
      const operation = jest.fn()
        .mockRejectedValueOnce(new Error('connection refused'))
        .mockResolvedValue('success');

      const promise = withDatabaseRetry(operation);
      await jest.runAllTimersAsync();

      const result = await promise;

      expect(result).toBe('success');
      expect(operation).toHaveBeenCalledTimes(2);
    });

    it('should handle database-specific errors', async () => {
      const dbErrors = [
        'terminating connection due to administrator command',
        'server closed the connection unexpectedly',
        'no connection to the server',
        'could not connect to server',
      ];

      for (const errorMsg of dbErrors) {
        const operation = jest.fn()
          .mockRejectedValueOnce(new Error(errorMsg))
          .mockResolvedValue('success');

        const promise = withDatabaseRetry(operation);
        await jest.runAllTimersAsync();

        const result = await promise;

        expect(result).toBe('success');
        expect(operation).toHaveBeenCalledTimes(2);

        jest.clearAllMocks();
      }
    });
  });

  describe('withTransactionRetry', () => {
    it('should use transaction-specific retry options', async () => {
      const operation = jest.fn()
        .mockRejectedValueOnce(new Error('could not serialize access'))
        .mockResolvedValue('success');

      const promise = withTransactionRetry(operation);
      await jest.runAllTimersAsync();

      const result = await promise;

      expect(result).toBe('success');
      expect(operation).toHaveBeenCalledTimes(2);
    });

    it('should handle transaction-specific errors', async () => {
      const txErrors = [
        'could not serialize access',
        'deadlock detected',
        'tuple concurrently updated',
        'serialization failure',
        'concurrent update',
        'lock timeout',
      ];

      for (const errorMsg of txErrors) {
        const operation = jest.fn()
          .mockRejectedValueOnce(new Error(errorMsg))
          .mockResolvedValue('success');

        const promise = withTransactionRetry(operation);
        await jest.runAllTimersAsync();

        const result = await promise;

        expect(result).toBe('success');
        expect(operation).toHaveBeenCalledTimes(2);

        jest.clearAllMocks();
      }
    });
  });

  describe('CircuitBreaker', () => {
    it('should allow requests when closed', async () => {
      const cb = new CircuitBreaker(3, 60000, 30000);
      const operation = jest.fn().mockResolvedValue('success');

      const result = await cb.execute(operation);

      expect(result).toBe('success');
      expect(operation).toHaveBeenCalledTimes(1);
      expect(cb.getState()).toBe('CLOSED');
    });

    it('should trip to open state after threshold failures', async () => {
      const cb = new CircuitBreaker(3, 60000, 30000);
      const operation = jest.fn().mockRejectedValue(new Error('failure'));

      // Fail 3 times to trip the breaker
      for (let i = 0; i < 3; i++) {
        try {
          await cb.execute(operation);
        } catch {
          // Expected to fail
        }
      }

      expect(operation).toHaveBeenCalledTimes(3);
      expect(cb.getState()).toBe('OPEN');

      // Next request should be rejected immediately
      await expect(cb.execute(operation)).rejects.toThrow('Circuit breaker is OPEN');
      expect(operation).toHaveBeenCalledTimes(3); // No additional call
    });

    it('should transition to half-open after reset timeout', async () => {
      const cb = new CircuitBreaker(2, 60000, 100); // Short reset timeout for testing
      const operation = jest.fn()
        .mockRejectedValue(new Error('failure'))
        .mockRejectedValue(new Error('failure'))
        .mockResolvedValue('success');

      // Trip the breaker
      for (let i = 0; i < 2; i++) {
        try {
          await cb.execute(operation);
        } catch {
          // Expected
        }
      }

      expect(cb.getState()).toBe('OPEN');

      // Wait for reset timeout
      await new Promise(resolve => setTimeout(resolve, 150));

      // Should transition to half-open and allow request
      const result = await cb.execute(operation);

      expect(result).toBe('success');
      expect(cb.getState()).toBe('CLOSED');
    });

    it('should remain open if half-open request fails', async () => {
      const cb = new CircuitBreaker(2, 60000, 100);
      const operation = jest.fn().mockRejectedValue(new Error('failure'));

      // Trip the breaker
      for (let i = 0; i < 2; i++) {
        try {
          await cb.execute(operation);
        } catch {
          // Expected
        }
      }

      expect(cb.getState()).toBe('OPEN');

      // Wait for reset timeout
      await new Promise(resolve => setTimeout(resolve, 150));

      // Half-open request fails
      try {
        await cb.execute(operation);
      } catch {
        // Expected
      }

      expect(cb.getState()).toBe('OPEN');
    });

    it('should reset state manually', () => {
      const cb = new CircuitBreaker(2, 60000, 30000);

      // Manually set to open (simulating failures)
      cb['state'] = 'OPEN';
      cb['failures'] = 5;

      cb.reset();

      expect(cb.getState()).toBe('CLOSED');
      expect(cb['failures']).toBe(0);
    });
  });

  describe('RetryError', () => {
    it('should contain failure details', async () => {
      const lastError = new Error('Final failure');
      const errors = [
        new Error('First failure'),
        new Error('Second failure'),
        lastError,
      ];

      const operation = jest.fn();
      errors.forEach(error => {
        operation.mockRejectedValueOnce(error);
      });

      const promise = retryWithBackoff(operation, {
        maxAttempts: 3,
        baseDelay: 10,
        jitter: false,
      });

      await jest.runAllTimersAsync();

      try {
        await promise;
        fail('Should have thrown RetryError');
      } catch (error) {
        expect(error).toBeInstanceOf(RetryError);
        const retryError = error as RetryError;
        expect(retryError.attempts).toBe(3);
        expect(retryError.lastError).toBe(lastError);
        expect(retryError.errors).toHaveLength(3);
        expect(retryError.totalDelay).toBeGreaterThan(0);
      }
    });
  });
});