/**
 * Unit tests for createApp authV1 route wiring.
 *
 * Background: CreateAppOptions.authV1 conditionally registers three auth
 * route modules (register, login, password reset). The route modules
 * themselves are tested elsewhere — this file only asserts that createApp
 * actually wires them up when `authV1` is provided.
 *
 * Proof-of-wiring strategy: hit each route with an empty body. If the
 * route is registered, the handler runs its body validation and returns
 * 400. If the route is NOT registered, Fastify returns 404. The
 * 400-vs-404 distinction is the exact guard this test provides.
 */

import { describe, it, expect, beforeAll, afterAll } from 'vitest';
import type { FastifyInstance } from 'fastify';
import { createApp } from '../index.js';
import { InMemoryUserStore } from '../persistence/user-store.js';
import { InMemoryRefreshTokenStore } from '../persistence/refresh-token-store.js';
import { InMemoryPasswordResetStore } from '../persistence/password-reset-store.js';
import { InMemoryTenantMembershipStore } from '../persistence/tenant-membership-store.js';
import { LoginLockoutService } from '../auth/login-lockout.js';
import { InMemoryEmailService } from '../services/email.js';

describe('createApp — authV1 route wiring', () => {
  let app: FastifyInstance;

  beforeAll(async () => {
    const userStore = new InMemoryUserStore();
    const refreshTokenStore = new InMemoryRefreshTokenStore();
    const passwordResetStore = new InMemoryPasswordResetStore();
    const membershipStore = new InMemoryTenantMembershipStore();
    const lockoutService = new LoginLockoutService();
    const emailService = new InMemoryEmailService();

    app = await createApp({
      authV1: {
        register: { userStore, emailService },
        login: {
          userStore,
          refreshTokenStore,
          membershipStore,
          lockoutService,
          jwtSecret: 'test-secret-do-not-use-in-production',
        },
        passwordReset: {
          userStore,
          passwordResetStore,
          refreshTokenStore,
          emailService,
        },
      },
    });
    await app.ready();
  });

  afterAll(async () => {
    await app.close();
  });

  it('POST /api/v1/auth/register with empty body returns 400 (route registered)', async () => {
    const res = await app.inject({
      method: 'POST',
      url: '/api/v1/auth/register',
      headers: { 'content-type': 'application/json' },
      payload: {},
    });
    // Proof of wiring: 400 (body validation) rather than 404 (missing route).
    expect(res.statusCode).toBe(400);
  });

  it('POST /api/v1/auth/login with empty body returns 400 (route registered)', async () => {
    const res = await app.inject({
      method: 'POST',
      url: '/api/v1/auth/login',
      headers: { 'content-type': 'application/json' },
      payload: {},
    });
    expect(res.statusCode).toBe(400);
  });

  it('POST /api/v1/auth/password-reset/request with empty body returns 400 (route registered)', async () => {
    const res = await app.inject({
      method: 'POST',
      url: '/api/v1/auth/password-reset/request',
      headers: { 'content-type': 'application/json' },
      payload: {},
    });
    expect(res.statusCode).toBe(400);
  });
});
