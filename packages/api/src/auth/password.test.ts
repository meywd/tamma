/**
 * Tests for password hashing and validation (Story 18-1).
 */

import { describe, it, expect } from 'vitest';
import { hashPassword, verifyPassword, validatePasswordStrength } from './password.js';

describe('validatePasswordStrength', () => {
  it('should accept a strong password', () => {
    const result = validatePasswordStrength('StrongPass1');
    expect(result.valid).toBe(true);
    expect(result.errors).toHaveLength(0);
  });

  it('should reject a password that is too short', () => {
    const result = validatePasswordStrength('Ab1');
    expect(result.valid).toBe(false);
    expect(result.errors).toContain('Password must be at least 8 characters');
  });

  it('should reject a password that is too long', () => {
    const result = validatePasswordStrength('A'.repeat(129) + 'a1');
    expect(result.valid).toBe(false);
    expect(result.errors).toContain('Password must be at most 128 characters');
  });

  it('should reject a password without uppercase', () => {
    const result = validatePasswordStrength('lowercase1');
    expect(result.valid).toBe(false);
    expect(result.errors).toContain('Password must contain at least one uppercase letter');
  });

  it('should reject a password without lowercase', () => {
    const result = validatePasswordStrength('UPPERCASE1');
    expect(result.valid).toBe(false);
    expect(result.errors).toContain('Password must contain at least one lowercase letter');
  });

  it('should reject a password without digit', () => {
    const result = validatePasswordStrength('NoDigitHere');
    expect(result.valid).toBe(false);
    expect(result.errors).toContain('Password must contain at least one digit');
  });

  it('should reject a common password', () => {
    const result = validatePasswordStrength('Password1');
    expect(result.valid).toBe(false);
    expect(result.errors).toContain('Password is too common');
  });

  it('should reject common passwords case-insensitively', () => {
    const result = validatePasswordStrength('PASSW0RD');
    expect(result.valid).toBe(false);
  });

  it('should return multiple errors for very weak passwords', () => {
    const result = validatePasswordStrength('ab');
    expect(result.valid).toBe(false);
    expect(result.errors.length).toBeGreaterThanOrEqual(3);
  });
});

describe('hashPassword / verifyPassword', () => {
  it('should hash and verify a password correctly', async () => {
    const password = 'MySecureP@ss1';
    const hash = await hashPassword(password);

    expect(hash).toMatch(/^scrypt:\d+:\d+:\d+:\d+:[0-9a-f]+:[0-9a-f]+$/);
    expect(await verifyPassword(password, hash)).toBe(true);
  });

  it('should reject an incorrect password', async () => {
    const hash = await hashPassword('CorrectPass1');
    expect(await verifyPassword('WrongPass1', hash)).toBe(false);
  });

  it('should produce different hashes for the same password (random salt)', async () => {
    const hash1 = await hashPassword('SamePass1');
    const hash2 = await hashPassword('SamePass1');
    expect(hash1).not.toBe(hash2);
  });

  it('should return false for malformed hash strings', async () => {
    expect(await verifyPassword('test', 'not-a-valid-hash')).toBe(false);
    expect(await verifyPassword('test', 'scrypt:bad:data')).toBe(false);
    expect(await verifyPassword('test', '')).toBe(false);
  });

  it('should handle hash with invalid scrypt parameters gracefully', async () => {
    // Invalid N parameter that would cause scrypt to throw
    expect(await verifyPassword('test', 'scrypt:0:0:0:64:aa:bb')).toBe(false);
  });
});
