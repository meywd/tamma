/**
 * InMemoryApiKeyStore Tests
 *
 * Covers CRUD operations, rotation grace period, and scope filtering.
 */

import { describe, it, expect, beforeEach, vi } from 'vitest';
import { InMemoryApiKeyStore } from '../api-key-store.js';
import type { IApiKeyStore, CreateUnifiedApiKeyInput } from '../api-key-store.js';

describe('InMemoryApiKeyStore', () => {
  let store: IApiKeyStore;

  beforeEach(() => {
    store = new InMemoryApiKeyStore();
  });

  const makeInput = (overrides?: Partial<CreateUnifiedApiKeyInput>): CreateUnifiedApiKeyInput => ({
    scope: 'service',
    ownerId: 'elsa-server',
    keyHash: 'hash-' + Math.random().toString(36).slice(2),
    keyPrefix: 'tamma_sk_abc',
    label: 'test key',
    permissions: ['prompts:read', 'diagnostics:write'],
    tenantId: null,
    ...overrides,
  });

  describe('createApiKey()', () => {
    it('creates a key record with all fields', async () => {
      const input = makeInput();
      const record = await store.createApiKey(input);

      expect(record.id).toBeDefined();
      expect(record.scope).toBe('service');
      expect(record.ownerId).toBe('elsa-server');
      expect(record.keyHash).toBe(input.keyHash);
      expect(record.keyPrefix).toBe('tamma_sk_abc');
      expect(record.label).toBe('test key');
      expect(record.permissions).toEqual(['prompts:read', 'diagnostics:write']);
      expect(record.tenantId).toBeNull();
      expect(record.createdAt).toBeDefined();
      expect(record.lastUsedAt).toBeNull();
      expect(record.revokedAt).toBeNull();
      expect(record.rotatedFrom).toBeNull();
    });

    it('creates user-scope key with tenantId', async () => {
      const input = makeInput({
        scope: 'user',
        ownerId: 'user-123',
        tenantId: '00000000-0000-0000-0000-000000000000',
        permissions: [],
      });
      const record = await store.createApiKey(input);

      expect(record.scope).toBe('user');
      expect(record.tenantId).toBe('00000000-0000-0000-0000-000000000000');
    });

    it('defaults permissions to empty array when not provided', async () => {
      const input = makeInput({ permissions: undefined });
      const record = await store.createApiKey(input);

      expect(record.permissions).toEqual([]);
    });
  });

  describe('findByKeyHash()', () => {
    it('finds an active key by hash', async () => {
      const input = makeInput();
      await store.createApiKey(input);

      const found = await store.findByKeyHash(input.keyHash);
      expect(found).not.toBeNull();
      expect(found!.keyHash).toBe(input.keyHash);
    });

    it('returns null for unknown hash', async () => {
      const found = await store.findByKeyHash('nonexistent-hash');
      expect(found).toBeNull();
    });

    it('returns null for revoked key (past revoked_at)', async () => {
      const input = makeInput();
      const record = await store.createApiKey(input);

      await store.revokeApiKey(record.id);

      const found = await store.findByKeyHash(input.keyHash);
      expect(found).toBeNull();
    });

    it('returns key in rotation grace period (future revoked_at)', async () => {
      const input = makeInput();
      const record = await store.createApiKey(input);

      // Rotate the key — old key gets revoked_at = NOW + 24h
      await store.rotateApiKey(record.id, 'new-hash', 'new-prefix');

      // Old key should still be findable during grace period
      const found = await store.findByKeyHash(input.keyHash);
      expect(found).not.toBeNull();
      expect(found!.revokedAt).not.toBeNull();
    });
  });

  describe('revokeApiKey()', () => {
    it('immediately revokes a key', async () => {
      const input = makeInput();
      const record = await store.createApiKey(input);

      await store.revokeApiKey(record.id);

      const found = await store.findByKeyHash(input.keyHash);
      expect(found).toBeNull();
    });

    it('throws for unknown key ID', async () => {
      await expect(store.revokeApiKey('nonexistent-id')).rejects.toThrow('API key not found');
    });
  });

  describe('rotateApiKey()', () => {
    it('creates a new key with rotated_from pointing to old', async () => {
      const input = makeInput();
      const oldRecord = await store.createApiKey(input);

      const newRecord = await store.rotateApiKey(oldRecord.id, 'new-hash-123', 'tamma_sk_new');

      expect(newRecord.rotatedFrom).toBe(oldRecord.id);
      expect(newRecord.keyHash).toBe('new-hash-123');
      expect(newRecord.keyPrefix).toBe('tamma_sk_new');
      expect(newRecord.scope).toBe(oldRecord.scope);
      expect(newRecord.ownerId).toBe(oldRecord.ownerId);
      expect(newRecord.permissions).toEqual(oldRecord.permissions);
      expect(newRecord.revokedAt).toBeNull();
    });

    it('sets old key revoked_at to ~24h in the future', async () => {
      const input = makeInput();
      const oldRecord = await store.createApiKey(input);

      const beforeRotation = Date.now();
      await store.rotateApiKey(oldRecord.id, 'new-hash', 'new-prefix');

      // Re-find old key (should still be valid during grace period)
      const found = await store.findByKeyHash(input.keyHash);
      expect(found).not.toBeNull();
      expect(found!.revokedAt).not.toBeNull();

      const revokedAt = new Date(found!.revokedAt!).getTime();
      const expectedMin = beforeRotation + 23 * 60 * 60 * 1000; // ~23h
      const expectedMax = beforeRotation + 25 * 60 * 60 * 1000; // ~25h
      expect(revokedAt).toBeGreaterThan(expectedMin);
      expect(revokedAt).toBeLessThan(expectedMax);
    });

    it('new key is findable by hash', async () => {
      const input = makeInput();
      const oldRecord = await store.createApiKey(input);

      await store.rotateApiKey(oldRecord.id, 'rotated-hash', 'rotated-prefix');

      const found = await store.findByKeyHash('rotated-hash');
      expect(found).not.toBeNull();
      expect(found!.rotatedFrom).toBe(oldRecord.id);
    });

    it('throws for unknown key ID', async () => {
      await expect(
        store.rotateApiKey('nonexistent', 'hash', 'prefix'),
      ).rejects.toThrow('API key not found');
    });
  });

  describe('listByScope()', () => {
    it('returns only keys of the requested scope', async () => {
      await store.createApiKey(makeInput({ scope: 'service', ownerId: 'svc-1', keyHash: 'h1' }));
      await store.createApiKey(makeInput({ scope: 'service', ownerId: 'svc-2', keyHash: 'h2' }));
      await store.createApiKey(makeInput({ scope: 'user', ownerId: 'usr-1', keyHash: 'h3' }));
      await store.createApiKey(makeInput({ scope: 'installation', ownerId: '123', keyHash: 'h4' }));

      const serviceKeys = await store.listByScope('service');
      expect(serviceKeys).toHaveLength(2);
      expect(serviceKeys.every((k) => k.scope === 'service')).toBe(true);

      const userKeys = await store.listByScope('user');
      expect(userKeys).toHaveLength(1);

      const installKeys = await store.listByScope('installation');
      expect(installKeys).toHaveLength(1);
    });

    it('includes revoked keys in the listing', async () => {
      const input = makeInput({ keyHash: 'revoked-hash' });
      const record = await store.createApiKey(input);
      await store.revokeApiKey(record.id);

      const keys = await store.listByScope('service');
      expect(keys).toHaveLength(1);
      expect(keys[0]!.revokedAt).not.toBeNull();
    });
  });

  describe('updateLastUsed()', () => {
    it('updates the lastUsedAt timestamp', async () => {
      const input = makeInput();
      const record = await store.createApiKey(input);
      expect(record.lastUsedAt).toBeNull();

      await store.updateLastUsed(record.id);

      const found = await store.findByKeyHash(input.keyHash);
      expect(found).not.toBeNull();
      expect(found!.lastUsedAt).not.toBeNull();
    });

    it('does not throw for unknown key ID', async () => {
      // updateLastUsed silently ignores unknown keys
      await expect(store.updateLastUsed('nonexistent')).resolves.toBeUndefined();
    });
  });
});
