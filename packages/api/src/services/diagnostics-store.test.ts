/**
 * Diagnostics Store Unit Tests
 *
 * Story 9-2: Tests for InMemoryDiagnosticsStore.
 */

import { describe, it, expect, beforeEach } from 'vitest';
import {
  InMemoryDiagnosticsStore,
  type DiagnosticsRecordInput,
} from './diagnostics-store.js';

function makeInput(overrides: Partial<DiagnosticsRecordInput> = {}): DiagnosticsRecordInput {
  return {
    eventType: 'provider:complete',
    providerName: 'openrouter',
    success: true,
    ...overrides,
  };
}

describe('InMemoryDiagnosticsStore', () => {
  let store: InMemoryDiagnosticsStore;

  beforeEach(() => {
    store = new InMemoryDiagnosticsStore();
  });

  // ---- insert ----

  describe('insert', () => {
    it('inserts a single record', async () => {
      const count = await store.insert([makeInput()]);
      expect(count).toBe(1);
    });

    it('inserts a batch of records', async () => {
      const inputs = [
        makeInput({ providerName: 'a' }),
        makeInput({ providerName: 'b' }),
        makeInput({ providerName: 'c' }),
      ];
      const count = await store.insert(inputs);
      expect(count).toBe(3);
    });

    it('returns 0 for empty batch', async () => {
      const count = await store.insert([]);
      expect(count).toBe(0);
    });

    it('rejects batch exceeding max size', async () => {
      const inputs = Array.from({ length: 101 }, () => makeInput());
      await expect(store.insert(inputs)).rejects.toThrow('Batch size');
    });

    it('rejects invalid event type', async () => {
      await expect(store.insert([makeInput({ eventType: 'invalid' })]))
        .rejects.toThrow('Invalid event type');
    });

    it('rejects empty provider name', async () => {
      await expect(store.insert([makeInput({ providerName: '' })]))
        .rejects.toThrow('providerName is required');
    });

    it('rejects overly long provider name', async () => {
      await expect(store.insert([makeInput({ providerName: 'a'.repeat(129) })]))
        .rejects.toThrow('providerName too long');
    });

    it('accepts all valid event types', async () => {
      const types = ['tool:invoke', 'tool:complete', 'tool:error', 'provider:call', 'provider:complete', 'provider:error'];
      for (const eventType of types) {
        const count = await store.insert([makeInput({ eventType })]);
        expect(count).toBe(1);
      }
    });
  });

  // ---- query ----

  describe('query', () => {
    it('returns empty result initially', async () => {
      const result = await store.query({});
      expect(result.items).toEqual([]);
      expect(result.total).toBe(0);
    });

    it('returns inserted records', async () => {
      await store.insert([makeInput(), makeInput()]);
      const result = await store.query({});
      expect(result.items).toHaveLength(2);
      expect(result.total).toBe(2);
    });

    it('filters by provider', async () => {
      await store.insert([
        makeInput({ providerName: 'openrouter' }),
        makeInput({ providerName: 'zen-mcp' }),
        makeInput({ providerName: 'openrouter' }),
      ]);
      const result = await store.query({ provider: 'openrouter' });
      expect(result.items).toHaveLength(2);
      expect(result.total).toBe(2);
    });

    it('filters by model', async () => {
      await store.insert([
        makeInput({ model: 'gpt-4' }),
        makeInput({ model: 'claude-3' }),
      ]);
      const result = await store.query({ model: 'gpt-4' });
      expect(result.items).toHaveLength(1);
    });

    it('filters by accountId', async () => {
      await store.insert([
        makeInput({ accountId: 'acc-1' }),
        makeInput({ accountId: 'acc-2' }),
        makeInput({ accountId: 'acc-1' }),
      ]);
      const result = await store.query({ accountId: 'acc-1' });
      expect(result.items).toHaveLength(2);
    });

    it('respects limit and offset', async () => {
      await store.insert(Array.from({ length: 10 }, (_, i) =>
        makeInput({ model: `model-${i}` }),
      ));

      const result = await store.query({ limit: 3, offset: 2 });
      expect(result.items).toHaveLength(3);
      expect(result.total).toBe(10);
    });

    it('records have correct structure', async () => {
      await store.insert([makeInput({
        providerName: 'openrouter',
        model: 'gpt-4',
        inputTokens: 100,
        outputTokens: 50,
        latencyMs: 500,
        costUsd: 0.01,
        success: true,
        agentType: 'implementer',
      })]);

      const result = await store.query({});
      const record = result.items[0]!;
      expect(record.id).toBeTruthy();
      expect(record.providerName).toBe('openrouter');
      expect(record.model).toBe('gpt-4');
      expect(record.inputTokens).toBe(100);
      expect(record.outputTokens).toBe(50);
      expect(record.latencyMs).toBe(500);
      expect(record.costUsd).toBe(0.01);
      expect(record.success).toBe(true);
      expect(record.agentType).toBe('implementer');
      expect(record.createdAt).toBeTruthy();
    });
  });

  // ---- report ----

  describe('report', () => {
    it('returns empty groups for empty store', async () => {
      const result = await store.report({ groupBy: 'provider' });
      expect(result).toEqual([]);
    });

    it('groups by provider', async () => {
      await store.insert([
        makeInput({ providerName: 'openrouter', costUsd: 0.01, inputTokens: 100, outputTokens: 50, latencyMs: 200 }),
        makeInput({ providerName: 'openrouter', costUsd: 0.02, inputTokens: 200, outputTokens: 100, latencyMs: 300, success: false }),
        makeInput({ providerName: 'zen-mcp', costUsd: 0.05, inputTokens: 500, outputTokens: 250, latencyMs: 400 }),
      ]);

      const result = await store.report({ groupBy: 'provider' });
      expect(result).toHaveLength(2);

      const openrouter = result.find((g) => g.key === 'openrouter');
      expect(openrouter).toBeDefined();
      expect(openrouter!.count).toBe(2);
      expect(openrouter!.totalCost).toBeCloseTo(0.03);
      expect(openrouter!.totalTokens).toBe(450);
      expect(openrouter!.avgLatency).toBe(250);
      expect(openrouter!.errorRate).toBe(0.5);
    });

    it('groups by model', async () => {
      await store.insert([
        makeInput({ model: 'gpt-4', costUsd: 0.10 }),
        makeInput({ model: 'gpt-4', costUsd: 0.20 }),
        makeInput({ model: 'claude-3', costUsd: 0.05 }),
      ]);

      const result = await store.report({ groupBy: 'model' });
      expect(result).toHaveLength(2);

      const gpt4 = result.find((g) => g.key === 'gpt-4');
      expect(gpt4).toBeDefined();
      expect(gpt4!.count).toBe(2);
      expect(gpt4!.totalCost).toBeCloseTo(0.30);
    });

    it('groups by agentType', async () => {
      await store.insert([
        makeInput({ agentType: 'implementer' }),
        makeInput({ agentType: 'reviewer' }),
        makeInput({ agentType: 'implementer' }),
      ]);

      const result = await store.report({ groupBy: 'agentType' });
      expect(result).toHaveLength(2);

      const impl = result.find((g) => g.key === 'implementer');
      expect(impl!.count).toBe(2);
    });
  });

  // ---- getBudget ----

  describe('getBudget', () => {
    it('returns zero spent when no records exist', async () => {
      const budget = await store.getBudget('acc-1', 100);
      expect(budget.spent).toBe(0);
      expect(budget.limit).toBe(100);
      expect(budget.remaining).toBe(100);
      expect(budget.percentUsed).toBe(0);
    });

    it('calculates budget correctly', async () => {
      await store.insert([
        makeInput({ accountId: 'acc-1', costUsd: 25 }),
        makeInput({ accountId: 'acc-1', costUsd: 15 }),
        makeInput({ accountId: 'acc-2', costUsd: 50 }), // Different account
      ]);

      const budget = await store.getBudget('acc-1', 100);
      expect(budget.spent).toBe(40);
      expect(budget.limit).toBe(100);
      expect(budget.remaining).toBe(60);
      expect(budget.percentUsed).toBe(40);
    });

    it('caps remaining at zero when over budget', async () => {
      await store.insert([
        makeInput({ accountId: 'acc-1', costUsd: 120 }),
      ]);

      const budget = await store.getBudget('acc-1', 100);
      expect(budget.spent).toBe(120);
      expect(budget.remaining).toBe(0);
      expect(budget.percentUsed).toBe(120);
    });
  });
});
