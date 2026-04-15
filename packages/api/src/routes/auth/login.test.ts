/**
 * Tests for login routes (Story 18-2).
 */

import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import Fastify from 'fastify';
import type { FastifyInstance } from 'fastify';
import { InMemoryUserStore } from '../../persistence/user-store.js';
import { InMemoryRefreshTokenStore } from '../../persistence/refresh-token-store.js';
import { InMemoryTenantMembershipStore } from '../../persistence/tenant-membership-store.js';
import { LoginLockoutService } from '../../auth/login-lockout.js';
import { hashPassword } from '../../auth/password.js';
import { registerLoginRoutes } from './login.js';

describe('Login Routes', () => {
  let app: FastifyInstance;
  let userStore: InMemoryUserStore;
  let refreshTokenStore: InMemoryRefreshTokenStore;
  let membershipStore: InMemoryTenantMembershipStore;
  let lockoutService: LoginLockoutService;

  const JWT_SECRET = 'test-jwt-secret-for-testing-only';

  beforeEach(async () => {
    app = Fastify({ logger: false });
    userStore = new InMemoryUserStore();
    refreshTokenStore = new InMemoryRefreshTokenStore();
    membershipStore = new InMemoryTenantMembershipStore();
    lockoutService = new LoginLockoutService({ maxAttempts: 3, windowMs: 300000, lockoutMs: 600000 });

    await registerLoginRoutes(app, {
      userStore,
      refreshTokenStore,
      membershipStore,
      lockoutService,
      jwtSecret: JWT_SECRET,
      accessTokenExpiresIn: 900,
      refreshTokenExpiresIn: 604800,
    });

    await app.ready();
  });

  afterEach(async () => {
    await app.close();
  });

  async function createVerifiedUser(email: string, password: string): Promise<string> {
    const passwordHash = await hashPassword(password);
    const user = await userStore.createEmailUser({
      email,
      name: 'Test User',
      passwordHash,
      emailVerificationTokenHash: 'dummy',
      emailVerificationExpiresAt: '2099-01-01T00:00:00Z',
    });
    await userStore.setEmailVerified(user.id);
    return user.id;
  }

  describe('POST /api/v1/auth/login', () => {
    it('should login with valid credentials', async () => {
      await createVerifiedUser('alice@test.com', 'StrongPass1');

      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/login',
        payload: { email: 'alice@test.com', password: 'StrongPass1' },
      });

      expect(res.statusCode).toBe(200);
      const body = res.json();
      expect(body.accessToken).toBeDefined();
      expect(body.refreshToken).toBeDefined();
      expect(body.user.email).toBe('alice@test.com');
    });

    it('should set session cookie on login', async () => {
      await createVerifiedUser('cookie@test.com', 'StrongPass1');

      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/login',
        payload: { email: 'cookie@test.com', password: 'StrongPass1' },
      });

      expect(res.statusCode).toBe(200);
      const cookies = res.cookies;
      const sessionCookie = cookies.find((c: { name: string }) => c.name === 'tamma_session');
      expect(sessionCookie).toBeDefined();
    });

    it('should return 401 for invalid password', async () => {
      await createVerifiedUser('wrong@test.com', 'StrongPass1');

      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/login',
        payload: { email: 'wrong@test.com', password: 'WrongPass1' },
      });

      expect(res.statusCode).toBe(401);
      expect(res.json().error).toBe('Invalid email or password');
    });

    it('should return 401 for non-existent user', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/login',
        payload: { email: 'nobody@test.com', password: 'StrongPass1' },
      });

      expect(res.statusCode).toBe(401);
    });

    it('should return 403 for unverified user', async () => {
      const passwordHash = await hashPassword('StrongPass1');
      await userStore.createEmailUser({
        email: 'unverified@test.com',
        name: 'Unverified',
        passwordHash,
        emailVerificationTokenHash: 'dummy',
        emailVerificationExpiresAt: '2099-01-01T00:00:00Z',
      });

      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/login',
        payload: { email: 'unverified@test.com', password: 'StrongPass1' },
      });

      expect(res.statusCode).toBe(403);
      expect(res.json().error).toBe('Please verify your email');
    });

    it('should return 429 after too many failed attempts', async () => {
      await createVerifiedUser('lockout@test.com', 'StrongPass1');

      // Fail 3 times
      for (let i = 0; i < 3; i++) {
        await app.inject({
          method: 'POST',
          url: '/api/v1/auth/login',
          payload: { email: 'lockout@test.com', password: 'Wrong' + i },
        });
      }

      // Fourth attempt should be locked
      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/login',
        payload: { email: 'lockout@test.com', password: 'StrongPass1' },
      });

      expect(res.statusCode).toBe(429);
    });

    it('should return 400 for missing fields', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/login',
        payload: { email: 'test@test.com' },
      });

      expect(res.statusCode).toBe(400);
    });
  });

  describe('POST /api/v1/auth/refresh', () => {
    it('should refresh tokens', async () => {
      await createVerifiedUser('refresh@test.com', 'StrongPass1');

      // Login first
      const loginRes = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/login',
        payload: { email: 'refresh@test.com', password: 'StrongPass1' },
      });
      const { refreshToken } = loginRes.json();

      // Refresh
      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/refresh',
        payload: { refreshToken },
      });

      expect(res.statusCode).toBe(200);
      const body = res.json();
      expect(body.accessToken).toBeDefined();
      expect(body.refreshToken).toBeDefined();
      // New refresh token should be different
      expect(body.refreshToken).not.toBe(refreshToken);
    });

    it('should reject reuse of old refresh token', async () => {
      await createVerifiedUser('reuse@test.com', 'StrongPass1');

      const loginRes = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/login',
        payload: { email: 'reuse@test.com', password: 'StrongPass1' },
      });
      const { refreshToken } = loginRes.json();

      // First refresh — success
      await app.inject({
        method: 'POST',
        url: '/api/v1/auth/refresh',
        payload: { refreshToken },
      });

      // Second use of same token — should fail (token already revoked)
      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/refresh',
        payload: { refreshToken },
      });

      expect(res.statusCode).toBe(401);
    });

    it('should return 400 for missing refresh token', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/refresh',
        payload: {},
      });

      expect(res.statusCode).toBe(400);
    });

    it('should return 401 for invalid refresh token', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/refresh',
        payload: { refreshToken: 'invalid-token' },
      });

      expect(res.statusCode).toBe(401);
    });
  });

  describe('POST /api/v1/auth/logout', () => {
    it('should logout and clear cookie', async () => {
      await createVerifiedUser('logout@test.com', 'StrongPass1');

      const loginRes = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/login',
        payload: { email: 'logout@test.com', password: 'StrongPass1' },
      });
      const { refreshToken } = loginRes.json();

      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/logout',
        payload: { refreshToken },
      });

      expect(res.statusCode).toBe(200);
      expect(res.json().ok).toBe(true);
    });

    it('should succeed even without refresh token', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/logout',
        payload: {},
      });

      expect(res.statusCode).toBe(200);
    });
  });
});
