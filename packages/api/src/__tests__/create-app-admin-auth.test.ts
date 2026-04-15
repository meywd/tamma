/**
 * Regression tests for createApp admin route auth enforcement.
 *
 * Background: requirePermission() has an escape hatch that silently allows
 * requests through when `authUser` is not a request decorator (historically
 * used by tests without auth setup). serve.ts never registered the legacy
 * `registerAuthPlugin`, so in production createApp never decorated
 * `authUser` and unauthenticated requests to /api/admin/service-keys
 * returned 400 (body-validation error) instead of 401.
 *
 * These tests guard against that regression: createApp must produce an app
 * whose admin-scoped routes refuse unauthenticated requests with 401 — even
 * when `options.auth` is not set.
 */

import { describe, it, expect, beforeAll, afterAll } from 'vitest';
import type { FastifyInstance } from 'fastify';
import { createApp, InMemoryApiKeyStore } from '../index.js';

describe('createApp — admin route auth enforcement', () => {
  let app: FastifyInstance;

  beforeAll(async () => {
    const unifiedApiKeyStore = new InMemoryApiKeyStore();
    app = await createApp({
      admin: {
        unifiedApiKeyStore,
      },
    });
    await app.ready();
  });

  afterAll(async () => {
    await app.close();
  });

  it('POST /api/admin/service-keys without auth returns 401 (not 400)', async () => {
    const res = await app.inject({
      method: 'POST',
      url: '/api/admin/service-keys',
      headers: { 'content-type': 'application/json' },
      // Empty body is intentional: we want to prove that the auth check
      // runs BEFORE body validation, so the response must be 401, not the
      // 400 "serviceName is required" the handler would produce.
      payload: {},
    });
    expect(res.statusCode).toBe(401);
  });

  it('POST /api/admin/service-keys with valid body but no auth still returns 401', async () => {
    const res = await app.inject({
      method: 'POST',
      url: '/api/admin/service-keys',
      headers: { 'content-type': 'application/json' },
      payload: { serviceName: 'elsa-server' },
    });
    expect(res.statusCode).toBe(401);
  });

  it('GET /api/admin/service-keys without auth returns 401', async () => {
    const res = await app.inject({
      method: 'GET',
      url: '/api/admin/service-keys',
    });
    expect(res.statusCode).toBe(401);
  });
});
