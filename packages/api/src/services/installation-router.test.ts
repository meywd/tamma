/**
 * Installation Router Tests
 *
 * Tests for the InstallationRouter class covering:
 * - resolve: success, unknown installation, suspended installation
 * - cache: hit, miss, TTL expiry, invalidation
 * - performance: cached lookup < 10ms
 */

import { describe, it, expect, beforeEach, vi } from 'vitest';
import { InstallationRouter } from './installation-router.js';
import { InMemoryInstallationStore } from '../persistence/installation-store.js';
import type { IGitHubInstallationStore, GitHubInstallation } from '../persistence/installation-store.js';

function createInstallation(overrides?: Partial<GitHubInstallation>): Omit<GitHubInstallation, 'createdAt' | 'updatedAt'> {
  return {
    installationId: 1001,
    accountLogin: 'test-org',
    accountType: 'Organization',
    appId: 42,
    permissions: { issues: 'write', pull_requests: 'write' },
    suspendedAt: null,
    apiKeyHash: null,
    apiKeyPrefix: null,
    apiKeyEncrypted: null,
    tenantId: '00000000-0000-0000-0000-000000000000',
    ...overrides,
  };
}

describe('InstallationRouter', () => {
  let store: InMemoryInstallationStore;
  let router: InstallationRouter;

  beforeEach(() => {
    store = new InMemoryInstallationStore();
    router = new InstallationRouter(store, { cacheTtlMs: 1000 });
  });

  // -------------------------------------------------------------------------
  // resolve
  // -------------------------------------------------------------------------

  describe('resolve', () => {
    it('returns installation data for a known installation', async () => {
      await store.upsertInstallation(createInstallation({ installationId: 1001 }));

      const result = await router.resolve(1001);

      expect(result).not.toBeNull();
      expect(result!.installation.installationId).toBe(1001);
      expect(result!.installation.accountLogin).toBe('test-org');
      expect(result!.isActive).toBe(true);
    });

    it('returns null for an unknown installation', async () => {
      const result = await router.resolve(9999);
      expect(result).toBeNull();
    });

    it('returns isActive=false for a suspended installation', async () => {
      await store.upsertInstallation(createInstallation({ installationId: 2001 }));
      await store.suspendInstallation(2001);

      const result = await router.resolve(2001);

      expect(result).not.toBeNull();
      expect(result!.isActive).toBe(false);
      expect(result!.installation.suspendedAt).not.toBeNull();
    });

    it('returns isActive=true for an unsuspended installation', async () => {
      await store.upsertInstallation(createInstallation({ installationId: 3001 }));
      await store.suspendInstallation(3001);
      await store.unsuspendInstallation(3001);

      const result = await router.resolve(3001);

      expect(result).not.toBeNull();
      expect(result!.isActive).toBe(true);
    });
  });

  // -------------------------------------------------------------------------
  // cache
  // -------------------------------------------------------------------------

  describe('cache', () => {
    it('serves from cache on the second call (cache hit)', async () => {
      await store.upsertInstallation(createInstallation({ installationId: 4001 }));

      // First call — cache miss, populates cache
      const result1 = await router.resolve(4001);
      expect(result1).not.toBeNull();

      // Spy on the store to verify it's not called again
      const getSpy = vi.spyOn(store, 'getInstallation');

      // Second call — should hit cache
      const result2 = await router.resolve(4001);
      expect(result2).not.toBeNull();
      expect(result2!.installation.installationId).toBe(4001);
      expect(getSpy).not.toHaveBeenCalled();
    });

    it('caches null results for unknown installations', async () => {
      // First call — unknown installation, caches null
      const result1 = await router.resolve(5001);
      expect(result1).toBeNull();

      const getSpy = vi.spyOn(store, 'getInstallation');

      // Second call — should hit cache (cached null)
      const result2 = await router.resolve(5001);
      expect(result2).toBeNull();
      expect(getSpy).not.toHaveBeenCalled();
    });

    it('expires cache entries after TTL', async () => {
      // Use a very short TTL
      const shortRouter = new InstallationRouter(store, { cacheTtlMs: 50 });
      await store.upsertInstallation(createInstallation({ installationId: 6001 }));

      // First call — cache miss
      await shortRouter.resolve(6001);

      const getSpy = vi.spyOn(store, 'getInstallation');

      // Wait for TTL to expire
      await new Promise((resolve) => setTimeout(resolve, 60));

      // Should fetch from store again
      await shortRouter.resolve(6001);
      expect(getSpy).toHaveBeenCalledWith(6001);
    });

    it('invalidates a specific cache entry', async () => {
      await store.upsertInstallation(createInstallation({ installationId: 7001 }));

      // Populate cache
      await router.resolve(7001);

      const getSpy = vi.spyOn(store, 'getInstallation');

      // Invalidate
      router.invalidate(7001);

      // Should fetch from store again
      await router.resolve(7001);
      expect(getSpy).toHaveBeenCalledWith(7001);
    });

    it('clearCache removes all entries', async () => {
      await store.upsertInstallation(createInstallation({ installationId: 8001 }));
      await store.upsertInstallation(createInstallation({ installationId: 8002, accountLogin: 'other' }));

      // Populate cache
      await router.resolve(8001);
      await router.resolve(8002);
      expect(router.cacheSize).toBe(2);

      // Clear cache
      router.clearCache();
      expect(router.cacheSize).toBe(0);

      // Should fetch from store again
      const getSpy = vi.spyOn(store, 'getInstallation');
      await router.resolve(8001);
      expect(getSpy).toHaveBeenCalledWith(8001);
    });

    it('reflects updated data after invalidation', async () => {
      await store.upsertInstallation(createInstallation({ installationId: 9001 }));

      // Populate cache — active
      const result1 = await router.resolve(9001);
      expect(result1!.isActive).toBe(true);

      // Suspend the installation in the store
      await store.suspendInstallation(9001);

      // Invalidate cache
      router.invalidate(9001);

      // Should now return suspended
      const result2 = await router.resolve(9001);
      expect(result2!.isActive).toBe(false);
    });
  });

  // -------------------------------------------------------------------------
  // performance
  // -------------------------------------------------------------------------

  describe('performance', () => {
    it('cached lookup completes in under 10ms', async () => {
      await store.upsertInstallation(createInstallation({ installationId: 10001 }));

      // Warm the cache
      await router.resolve(10001);

      // Measure cached lookups
      const iterations = 1000;
      const start = performance.now();
      for (let i = 0; i < iterations; i++) {
        await router.resolve(10001);
      }
      const elapsed = performance.now() - start;
      const avgMs = elapsed / iterations;

      // Each cached lookup should average well under 10ms
      expect(avgMs).toBeLessThan(10);
    });
  });

  // -------------------------------------------------------------------------
  // edge cases
  // -------------------------------------------------------------------------

  describe('edge cases', () => {
    it('uses default TTL of 60s when no options provided', () => {
      const defaultRouter = new InstallationRouter(store);
      // Just verify it doesn't throw — internal TTL defaults to 60000
      expect(defaultRouter.cacheSize).toBe(0);
    });

    it('handles concurrent resolves for the same installation', async () => {
      await store.upsertInstallation(createInstallation({ installationId: 11001 }));

      // Fire multiple concurrent resolves
      const results = await Promise.all([
        router.resolve(11001),
        router.resolve(11001),
        router.resolve(11001),
      ]);

      for (const result of results) {
        expect(result).not.toBeNull();
        expect(result!.installation.installationId).toBe(11001);
      }
    });

    it('invalidating a non-existent entry is a no-op', () => {
      // Should not throw
      router.invalidate(99999);
      expect(router.cacheSize).toBe(0);
    });
  });
});
