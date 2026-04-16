/**
 * Health Store Unit Tests
 *
 * Story 9-3: Tests for InMemoryHealthStore.
 */

import { describe, it, expect, beforeEach } from 'vitest';
import { InMemoryHealthStore } from './health-store.js';

describe('InMemoryHealthStore', () => {
  let store: InMemoryHealthStore;

  beforeEach(() => {
    store = new InMemoryHealthStore({ failureThreshold: 3, circuitOpenDurationMs: 60_000 });
  });

  // ---- getAll ----

  describe('getAll', () => {
    it('returns empty object when no keys tracked', async () => {
      const result = await store.getAll();
      expect(result).toEqual({});
    });

    it('returns status for all tracked keys', async () => {
      await store.recordFailure('openrouter:gpt-4');
      await store.recordFailure('zen-mcp:default');

      const result = await store.getAll();
      expect(Object.keys(result)).toHaveLength(2);
      expect(result['openrouter:gpt-4']).toBeDefined();
      expect(result['zen-mcp:default']).toBeDefined();
    });
  });

  // ---- get ----

  describe('get', () => {
    it('returns null for unknown key', async () => {
      const result = await store.get('unknown:key');
      expect(result).toBeNull();
    });

    it('returns healthy status for key with few failures', async () => {
      await store.recordFailure('openrouter:gpt-4');
      const result = await store.get('openrouter:gpt-4');
      expect(result).not.toBeNull();
      expect(result!.healthy).toBe(true);
      expect(result!.failures).toBe(1);
      expect(result!.circuitOpen).toBe(false);
    });

    it('rejects empty key', async () => {
      await expect(store.get('')).rejects.toThrow('must not be empty');
    });

    it('rejects key with invalid characters', async () => {
      await expect(store.get('key with spaces')).rejects.toThrow('invalid characters');
    });

    it('rejects overly long key', async () => {
      await expect(store.get('a'.repeat(257))).rejects.toThrow('too long');
    });
  });

  // ---- recordFailure ----

  describe('recordFailure', () => {
    it('increments failure count', async () => {
      const r1 = await store.recordFailure('openrouter:gpt-4');
      expect(r1.failures).toBe(1);
      expect(r1.circuitOpen).toBe(false);

      const r2 = await store.recordFailure('openrouter:gpt-4');
      expect(r2.failures).toBe(2);
      expect(r2.circuitOpen).toBe(false);
    });

    it('opens circuit at threshold', async () => {
      await store.recordFailure('key:1');
      await store.recordFailure('key:1');
      const r3 = await store.recordFailure('key:1');
      expect(r3.circuitOpen).toBe(true);
      expect(r3.failures).toBe(3);
    });

    it('reports circuit open in get() after threshold', async () => {
      for (let i = 0; i < 3; i++) {
        await store.recordFailure('key:1');
      }

      const status = await store.get('key:1');
      expect(status!.circuitOpen).toBe(true);
      expect(status!.healthy).toBe(false);
    });

    it('does not count non-retryable errors', async () => {
      const r1 = await store.recordFailure('key:1', { retryable: false });
      expect(r1.failures).toBe(0);
      expect(r1.circuitOpen).toBe(false);
    });
  });

  // ---- recordSuccess ----

  describe('recordSuccess', () => {
    it('resets circuit to closed', async () => {
      // Open the circuit
      for (let i = 0; i < 3; i++) {
        await store.recordFailure('key:1');
      }

      // Record success
      const result = await store.recordSuccess('key:1');
      expect(result.circuitOpen).toBe(false);
      expect(result.failures).toBe(0);
    });

    it('marks key as healthy after success', async () => {
      for (let i = 0; i < 3; i++) {
        await store.recordFailure('key:1');
      }

      await store.recordSuccess('key:1');

      const status = await store.get('key:1');
      expect(status!.healthy).toBe(true);
      expect(status!.circuitOpen).toBe(false);
      expect(status!.failures).toBe(0);
    });

    it('is a no-op for unknown key', async () => {
      const result = await store.recordSuccess('unknown:key');
      expect(result.circuitOpen).toBe(false);
      expect(result.failures).toBe(0);
    });
  });

  // ---- reset ----

  describe('reset', () => {
    it('removes health state for a key', async () => {
      await store.recordFailure('key:1');
      const deleted = await store.reset('key:1');
      expect(deleted).toBe(true);

      const status = await store.get('key:1');
      expect(status).toBeNull();
    });

    it('returns false for unknown key', async () => {
      const deleted = await store.reset('unknown:key');
      expect(deleted).toBe(false);
    });
  });

  // ---- circuit expiry ----

  describe('circuit expiry', () => {
    it('shows healthy when circuit open period has expired', async () => {
      // Use a very short circuit duration
      const shortStore = new InMemoryHealthStore({
        failureThreshold: 1,
        circuitOpenDurationMs: 1, // 1ms
      });

      await shortStore.recordFailure('key:1');

      // Wait a tiny bit for the circuit to expire
      await new Promise((resolve) => setTimeout(resolve, 10));

      const status = await shortStore.get('key:1');
      expect(status!.healthy).toBe(true);
      expect(status!.circuitOpen).toBe(false);
    });
  });

  // ---- syncCircuitChange ----

  describe('syncCircuitChange', () => {
    it('persists open state', async () => {
      await store.syncCircuitChange('sync:open', 'open');

      const status = await store.get('sync:open');
      expect(status).not.toBeNull();
      expect(status!.circuitOpen).toBe(true);
      expect(status!.halfOpen).toBe(false);
    });

    it('persists half-open state', async () => {
      await store.syncCircuitChange('sync:half', 'half-open');

      const status = await store.get('sync:half');
      expect(status).not.toBeNull();
      expect(status!.circuitOpen).toBe(true);
      expect(status!.halfOpen).toBe(true);
    });

    it('persists closed state (resets to healthy)', async () => {
      // First open the circuit
      await store.syncCircuitChange('sync:close', 'open');

      // Then close it
      await store.syncCircuitChange('sync:close', 'closed');

      const status = await store.get('sync:close');
      // After recordSuccess, the record exists but is healthy
      expect(status).not.toBeNull();
      expect(status!.circuitOpen).toBe(false);
      expect(status!.healthy).toBe(true);
      expect(status!.failures).toBe(0);
    });

    it('creates entry for unknown key on sync open', async () => {
      await store.syncCircuitChange('new:key', 'open');

      const status = await store.get('new:key');
      expect(status).not.toBeNull();
      expect(status!.circuitOpen).toBe(true);
    });
  });
});
