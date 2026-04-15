/**
 * Tests for refresh token store (Story 18-2).
 */

import { describe, it, expect, beforeEach } from 'vitest';
import { InMemoryRefreshTokenStore } from './refresh-token-store.js';

describe('InMemoryRefreshTokenStore', () => {
  let store: InMemoryRefreshTokenStore;

  beforeEach(() => {
    store = new InMemoryRefreshTokenStore();
  });

  it('should create and retrieve a token by hash', async () => {
    const token = await store.createToken('user-1', 'hash-abc', '2099-01-01T00:00:00Z');
    expect(token.userId).toBe('user-1');
    expect(token.tokenHash).toBe('hash-abc');
    expect(token.revokedAt).toBeNull();

    const found = await store.getTokenByHash('hash-abc');
    expect(found).not.toBeNull();
    expect(found!.id).toBe(token.id);
  });

  it('should return null for non-existent token hash', async () => {
    expect(await store.getTokenByHash('nonexistent')).toBeNull();
  });

  it('should revoke a specific token', async () => {
    const token = await store.createToken('user-1', 'hash-1', '2099-01-01T00:00:00Z');
    await store.revokeToken(token.id);

    const found = await store.getTokenByHash('hash-1');
    expect(found!.revokedAt).not.toBeNull();
  });

  it('should revoke all tokens for a user', async () => {
    await store.createToken('user-1', 'hash-a', '2099-01-01T00:00:00Z');
    await store.createToken('user-1', 'hash-b', '2099-01-01T00:00:00Z');
    await store.createToken('user-2', 'hash-c', '2099-01-01T00:00:00Z');

    await store.revokeAllForUser('user-1');

    const tokenA = await store.getTokenByHash('hash-a');
    const tokenB = await store.getTokenByHash('hash-b');
    const tokenC = await store.getTokenByHash('hash-c');

    expect(tokenA!.revokedAt).not.toBeNull();
    expect(tokenB!.revokedAt).not.toBeNull();
    expect(tokenC!.revokedAt).toBeNull();
  });

  it('should cleanup expired tokens', async () => {
    await store.createToken('user-1', 'hash-expired', '2020-01-01T00:00:00Z');
    await store.createToken('user-1', 'hash-valid', '2099-01-01T00:00:00Z');

    const count = await store.cleanupExpired();
    expect(count).toBe(1);

    expect(await store.getTokenByHash('hash-expired')).toBeNull();
    expect(await store.getTokenByHash('hash-valid')).not.toBeNull();
  });
});
