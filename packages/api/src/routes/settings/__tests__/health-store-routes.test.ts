/**
 * Health Store Routes Integration Tests
 *
 * Story 9-3: Tests for health provider endpoints.
 */

import { describe, it, expect, beforeAll, afterAll } from 'vitest';
import { createSettingsServices, registerSettingsRoutes } from '../index.js';
import { InMemoryHealthStore } from '../../../services/health-store.js';
import type { FastifyInstance } from 'fastify';
import Fastify from 'fastify';

describe('Health Store Routes', () => {
  let app: FastifyInstance;
  let store: InMemoryHealthStore;

  beforeAll(async () => {
    store = new InMemoryHealthStore({ failureThreshold: 3 });
    const settingsServices = createSettingsServices();
    settingsServices.healthStore = store;
    app = Fastify({ logger: false });
    app.decorateRequest('authUser', null);
    // Stub auth as owner — auth enforcement is tested in create-app-admin-auth.test.ts
    app.addHook('onRequest', async (request) => {
      (request as unknown as {
        authUser: { id: string; role: string; username: string };
      }).authUser = { id: 'test-owner', role: 'owner', username: 'test' };
    });
    await registerSettingsRoutes(app, settingsServices);
  });

  afterAll(async () => {
    await app.close();
  });

  // ---- GET /api/providers/health/providers ----

  describe('GET /api/providers/health/providers', () => {
    it('returns empty status initially', async () => {
      const res = await app.inject({
        method: 'GET',
        url: '/api/providers/health/providers',
      });
      expect(res.statusCode).toBe(200);
      const body = JSON.parse(res.body);
      expect(body).toEqual({});
    });
  });

  // ---- GET /api/providers/health/providers/:key ----

  describe('GET /api/providers/health/providers/:key', () => {
    it('returns healthy for unknown key', async () => {
      const res = await app.inject({
        method: 'GET',
        url: '/api/providers/health/providers/unknown:key',
      });
      expect(res.statusCode).toBe(200);
      const body = JSON.parse(res.body);
      expect(body.healthy).toBe(true);
      expect(body.circuitOpen).toBe(false);
    });

    it('rejects empty key', async () => {
      const res = await app.inject({
        method: 'GET',
        url: '/api/providers/health/providers/%20',
      });
      // The key " " has invalid characters
      expect(res.statusCode).toBe(400);
    });
  });

  // ---- POST /api/providers/health/providers/:key/failure ----

  describe('POST /api/providers/health/providers/:key/failure', () => {
    it('records a failure and returns status', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/providers/health/providers/openrouter:gpt-4/failure',
        payload: { error: 'timeout' },
      });
      expect(res.statusCode).toBe(200);
      const body = JSON.parse(res.body);
      expect(body.failures).toBe(1);
      expect(body.circuitOpen).toBe(false);
    });

    it('opens circuit after threshold failures', async () => {
      const key = 'test-circuit:model';

      // Record failures up to threshold
      for (let i = 0; i < 3; i++) {
        await app.inject({
          method: 'POST',
          url: `/api/providers/health/providers/${key}/failure`,
          payload: {},
        });
      }

      const res = await app.inject({
        method: 'GET',
        url: `/api/providers/health/providers/${key}`,
      });
      const body = JSON.parse(res.body);
      expect(body.circuitOpen).toBe(true);
      expect(body.healthy).toBe(false);
    });
  });

  // ---- POST /api/providers/health/providers/:key/success ----

  describe('POST /api/providers/health/providers/:key/success', () => {
    it('records success and closes circuit', async () => {
      const key = 'success-test:model';

      // First open the circuit
      for (let i = 0; i < 3; i++) {
        await app.inject({
          method: 'POST',
          url: `/api/providers/health/providers/${key}/failure`,
          payload: {},
        });
      }

      // Record success
      const res = await app.inject({
        method: 'POST',
        url: `/api/providers/health/providers/${key}/success`,
      });
      expect(res.statusCode).toBe(200);
      const body = JSON.parse(res.body);
      expect(body.circuitOpen).toBe(false);
      expect(body.failures).toBe(0);
    });
  });

  // ---- POST /api/providers/health/providers/:key/reset ----

  describe('POST /api/providers/health/providers/:key/reset', () => {
    it('resets health state for a key', async () => {
      const key = 'reset-test:model';
      await app.inject({
        method: 'POST',
        url: `/api/providers/health/providers/${key}/failure`,
        payload: {},
      });

      const res = await app.inject({
        method: 'POST',
        url: `/api/providers/health/providers/${key}/reset`,
      });
      expect(res.statusCode).toBe(200);
      const body = JSON.parse(res.body);
      expect(body.reset).toBe(true);
    });

    it('returns false for unknown key', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/providers/health/providers/nonexistent:key/reset',
      });
      expect(res.statusCode).toBe(200);
      const body = JSON.parse(res.body);
      expect(body.reset).toBe(false);
    });
  });

  // ---- Backward compat: existing GET /api/providers/health ----

  describe('GET /api/providers/health (backward compat)', () => {
    it('still returns in-memory health status', async () => {
      const res = await app.inject({
        method: 'GET',
        url: '/api/providers/health',
      });
      expect(res.statusCode).toBe(200);
      const body = JSON.parse(res.body);
      expect(typeof body).toBe('object');
    });
  });
});
