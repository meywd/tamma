/**
 * Tests for password reset token store (Story 18-6).
 */

import { describe, it, expect, beforeEach } from 'vitest';
import { InMemoryPasswordResetStore } from './password-reset-store.js';

describe('InMemoryPasswordResetStore', () => {
  let store: InMemoryPasswordResetStore;

  beforeEach(() => {
    store = new InMemoryPasswordResetStore();
  });

  it('should create and retrieve a reset token by hash', async () => {
    const token = await store.createResetToken('user-1', 'hash-abc', '2099-01-01T00:00:00Z');
    expect(token.userId).toBe('user-1');
    expect(token.tokenHash).toBe('hash-abc');
    expect(token.consumedAt).toBeNull();

    const found = await store.getResetTokenByHash('hash-abc');
    expect(found).not.toBeNull();
    expect(found!.id).toBe(token.id);
  });

  it('should return null for non-existent token hash', async () => {
    expect(await store.getResetTokenByHash('nonexistent')).toBeNull();
  });

  it('should consume a reset token', async () => {
    const token = await store.createResetToken('user-1', 'hash-1', '2099-01-01T00:00:00Z');
    await store.consumeResetToken(token.id);

    const found = await store.getResetTokenByHash('hash-1');
    expect(found!.consumedAt).not.toBeNull();
  });

  it('should cleanup expired tokens', async () => {
    await store.createResetToken('user-1', 'hash-expired', '2020-01-01T00:00:00Z');
    await store.createResetToken('user-1', 'hash-valid', '2099-01-01T00:00:00Z');

    const count = await store.cleanupExpired();
    expect(count).toBe(1);

    expect(await store.getResetTokenByHash('hash-expired')).toBeNull();
    expect(await store.getResetTokenByHash('hash-valid')).not.toBeNull();
  });
});
