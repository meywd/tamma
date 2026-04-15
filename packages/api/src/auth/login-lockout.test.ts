/**
 * Tests for login lockout service (Story 18-2).
 */

import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest';
import { LoginLockoutService } from './login-lockout.js';

describe('LoginLockoutService', () => {
  let service: LoginLockoutService;

  beforeEach(() => {
    vi.useFakeTimers();
    service = new LoginLockoutService({
      maxAttempts: 3,
      windowMs: 5 * 60 * 1000,    // 5 minutes
      lockoutMs: 10 * 60 * 1000,  // 10 minutes
    });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('should not be locked initially', () => {
    expect(service.isLocked('test@example.com')).toBe(false);
  });

  it('should not lock after fewer than max attempts', () => {
    service.recordFailedAttempt('test@example.com');
    service.recordFailedAttempt('test@example.com');
    expect(service.isLocked('test@example.com')).toBe(false);
  });

  it('should lock after max failed attempts', () => {
    service.recordFailedAttempt('test@example.com');
    service.recordFailedAttempt('test@example.com');
    const locked = service.recordFailedAttempt('test@example.com');
    expect(locked).toBe(true);
    expect(service.isLocked('test@example.com')).toBe(true);
  });

  it('should return remaining lockout time', () => {
    for (let i = 0; i < 3; i++) {
      service.recordFailedAttempt('test@example.com');
    }
    const remaining = service.getRemainingLockoutSeconds('test@example.com');
    expect(remaining).toBeGreaterThan(0);
    expect(remaining).toBeLessThanOrEqual(600); // 10 min
  });

  it('should unlock after lockout period', () => {
    for (let i = 0; i < 3; i++) {
      service.recordFailedAttempt('test@example.com');
    }
    expect(service.isLocked('test@example.com')).toBe(true);

    // Advance time past lockout period
    vi.advanceTimersByTime(11 * 60 * 1000);
    expect(service.isLocked('test@example.com')).toBe(false);
  });

  it('should reset on successful login', () => {
    service.recordFailedAttempt('test@example.com');
    service.recordFailedAttempt('test@example.com');
    service.resetAttempts('test@example.com');

    // Should be able to fail again without lockout
    service.recordFailedAttempt('test@example.com');
    service.recordFailedAttempt('test@example.com');
    expect(service.isLocked('test@example.com')).toBe(false);
  });

  it('should be case-insensitive on email', () => {
    service.recordFailedAttempt('Test@Example.COM');
    service.recordFailedAttempt('test@example.com');
    const locked = service.recordFailedAttempt('TEST@EXAMPLE.COM');
    expect(locked).toBe(true);
    expect(service.isLocked('test@example.com')).toBe(true);
  });

  it('should not count old attempts outside window', () => {
    service.recordFailedAttempt('test@example.com');
    service.recordFailedAttempt('test@example.com');

    // Advance past window
    vi.advanceTimersByTime(6 * 60 * 1000);

    // This should be treated as first attempt in new window
    const locked = service.recordFailedAttempt('test@example.com');
    expect(locked).toBe(false);
    expect(service.isLocked('test@example.com')).toBe(false);
  });

  it('should return 0 for non-locked accounts', () => {
    expect(service.getRemainingLockoutSeconds('test@example.com')).toBe(0);
  });

  it('should track separate accounts independently', () => {
    for (let i = 0; i < 3; i++) {
      service.recordFailedAttempt('alice@example.com');
    }
    expect(service.isLocked('alice@example.com')).toBe(true);
    expect(service.isLocked('bob@example.com')).toBe(false);
  });
});
