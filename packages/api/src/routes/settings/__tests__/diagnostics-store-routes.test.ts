/**
 * Diagnostics Store Routes Integration Tests
 *
 * Story 9-2: Tests for diagnostics ingest, query, report, and budget endpoints.
 */

import { describe, it, expect, beforeAll, afterAll } from 'vitest';
import { createApp } from '../../../index.js';
import { createSettingsServices } from '../index.js';
import { InMemoryDiagnosticsStore } from '../../../services/diagnostics-store.js';
import type { FastifyInstance } from 'fastify';

describe('Diagnostics Store Routes', () => {
  let app: FastifyInstance;
  let store: InMemoryDiagnosticsStore;

  beforeAll(async () => {
    store = new InMemoryDiagnosticsStore();
    const settingsServices = createSettingsServices();
    settingsServices.diagnosticsStore = store;
    app = await createApp({ settingsServices });
  });

  afterAll(async () => {
    await app.close();
  });

  // ---- POST /api/providers/diagnostics (ingest) ----

  describe('POST /api/providers/diagnostics', () => {
    it('records a single diagnostics event', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/providers/diagnostics',
        payload: {
          eventType: 'provider:complete',
          providerName: 'openrouter',
          model: 'gpt-4',
          inputTokens: 100,
          outputTokens: 50,
          costUsd: 0.01,
          success: true,
        },
      });
      expect(res.statusCode).toBe(201);
      const body = JSON.parse(res.body);
      expect(body.recorded).toBe(1);
    });

    it('records a batch of diagnostics events', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/providers/diagnostics',
        payload: [
          { eventType: 'provider:complete', providerName: 'a', success: true },
          { eventType: 'provider:error', providerName: 'b', success: false },
        ],
      });
      expect(res.statusCode).toBe(201);
      const body = JSON.parse(res.body);
      expect(body.recorded).toBe(2);
    });

    it('rejects invalid event type', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/providers/diagnostics',
        payload: {
          eventType: 'invalid',
          providerName: 'test',
          success: true,
        },
      });
      expect(res.statusCode).toBe(400);
    });

    it('rejects missing providerName', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/providers/diagnostics',
        payload: {
          eventType: 'provider:complete',
          providerName: '',
          success: true,
        },
      });
      expect(res.statusCode).toBe(400);
    });
  });

  // ---- GET /api/providers/diagnostics/query ----

  describe('GET /api/providers/diagnostics/query', () => {
    it('returns queried records', async () => {
      const res = await app.inject({
        method: 'GET',
        url: '/api/providers/diagnostics/query',
      });
      expect(res.statusCode).toBe(200);
      const body = JSON.parse(res.body);
      expect(body.items).toBeInstanceOf(Array);
      expect(typeof body.total).toBe('number');
    });

    it('respects limit parameter', async () => {
      const res = await app.inject({
        method: 'GET',
        url: '/api/providers/diagnostics/query?limit=1',
      });
      expect(res.statusCode).toBe(200);
      const body = JSON.parse(res.body);
      expect(body.items.length).toBeLessThanOrEqual(1);
    });

    it('filters by provider', async () => {
      const res = await app.inject({
        method: 'GET',
        url: '/api/providers/diagnostics/query?provider=openrouter',
      });
      expect(res.statusCode).toBe(200);
      const body = JSON.parse(res.body);
      for (const item of body.items) {
        expect(item.providerName).toBe('openrouter');
      }
    });

    it('rejects invalid limit', async () => {
      const res = await app.inject({
        method: 'GET',
        url: '/api/providers/diagnostics/query?limit=999',
      });
      expect(res.statusCode).toBe(400);
    });
  });

  // ---- GET /api/providers/diagnostics/report ----

  describe('GET /api/providers/diagnostics/report', () => {
    it('returns report grouped by provider', async () => {
      const res = await app.inject({
        method: 'GET',
        url: '/api/providers/diagnostics/report?groupBy=provider',
      });
      expect(res.statusCode).toBe(200);
      const body = JSON.parse(res.body);
      expect(body.groups).toBeInstanceOf(Array);
    });

    it('defaults to provider grouping', async () => {
      const res = await app.inject({
        method: 'GET',
        url: '/api/providers/diagnostics/report',
      });
      expect(res.statusCode).toBe(200);
    });

    it('rejects invalid groupBy', async () => {
      const res = await app.inject({
        method: 'GET',
        url: '/api/providers/diagnostics/report?groupBy=invalid',
      });
      expect(res.statusCode).toBe(400);
    });
  });

  // ---- GET /api/providers/diagnostics/budget/:accountId ----

  describe('GET /api/providers/diagnostics/budget/:accountId', () => {
    it('returns budget status', async () => {
      const res = await app.inject({
        method: 'GET',
        url: '/api/providers/diagnostics/budget/acc-1?limit=100',
      });
      expect(res.statusCode).toBe(200);
      const body = JSON.parse(res.body);
      expect(body).toHaveProperty('spent');
      expect(body).toHaveProperty('limit');
      expect(body).toHaveProperty('remaining');
      expect(body).toHaveProperty('percentUsed');
    });

    it('defaults to limit 100', async () => {
      const res = await app.inject({
        method: 'GET',
        url: '/api/providers/diagnostics/budget/acc-1',
      });
      expect(res.statusCode).toBe(200);
      const body = JSON.parse(res.body);
      expect(body.limit).toBe(100);
    });
  });

  // ---- Backward compat: existing GET /api/providers/diagnostics ----

  describe('GET /api/providers/diagnostics (backward compat)', () => {
    it('still returns in-memory events', async () => {
      const res = await app.inject({
        method: 'GET',
        url: '/api/providers/diagnostics',
      });
      expect(res.statusCode).toBe(200);
      const body = JSON.parse(res.body);
      expect(body).toBeInstanceOf(Array);
    });
  });
});
