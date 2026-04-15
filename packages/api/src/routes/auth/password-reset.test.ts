/**
 * Tests for password reset routes (Story 18-6).
 */

import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import Fastify from 'fastify';
import type { FastifyInstance } from 'fastify';
import { InMemoryUserStore } from '../../persistence/user-store.js';
import { InMemoryPasswordResetStore } from '../../persistence/password-reset-store.js';
import { InMemoryRefreshTokenStore } from '../../persistence/refresh-token-store.js';
import { InMemoryEmailService } from '../../services/email.js';
import { hashPassword, verifyPassword } from '../../auth/password.js';
import { registerPasswordResetRoutes } from './password-reset.js';

describe('Password Reset Routes', () => {
  let app: FastifyInstance;
  let userStore: InMemoryUserStore;
  let passwordResetStore: InMemoryPasswordResetStore;
  let refreshTokenStore: InMemoryRefreshTokenStore;
  let emailService: InMemoryEmailService;

  beforeEach(async () => {
    app = Fastify({ logger: false });
    userStore = new InMemoryUserStore();
    passwordResetStore = new InMemoryPasswordResetStore();
    refreshTokenStore = new InMemoryRefreshTokenStore();
    emailService = new InMemoryEmailService();

    await registerPasswordResetRoutes(app, {
      userStore,
      passwordResetStore,
      refreshTokenStore,
      emailService,
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

  describe('POST /api/v1/auth/password-reset/request', () => {
    it('should send a reset email for existing user', async () => {
      await createVerifiedUser('reset@test.com', 'OldPass1');

      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/password-reset/request',
        payload: { email: 'reset@test.com' },
      });

      expect(res.statusCode).toBe(200);
      expect(res.json().message).toContain('If an account');

      // Verify email was sent
      await new Promise((resolve) => setTimeout(resolve, 50));
      const emails = emailService.getEmailsTo('reset@test.com');
      expect(emails.length).toBe(1);
      expect(emails[0]!.subject).toContain('Reset');
    });

    it('should return 200 for non-existent email (no enumeration)', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/password-reset/request',
        payload: { email: 'nonexistent@test.com' },
      });

      expect(res.statusCode).toBe(200);
      expect(emailService.sentEmails).toHaveLength(0);
    });

    it('should not send email for GitHub-only users', async () => {
      await userStore.upsertUser({
        githubId: 12345,
        githubLogin: 'ghuser',
        email: 'ghonly@test.com',
        role: 'member',
      });

      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/password-reset/request',
        payload: { email: 'ghonly@test.com' },
      });

      expect(res.statusCode).toBe(200);
      expect(emailService.sentEmails).toHaveLength(0);
    });

    it('should return 400 for missing email', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/password-reset/request',
        payload: {},
      });

      expect(res.statusCode).toBe(400);
    });
  });

  describe('POST /api/v1/auth/password-reset/confirm', () => {
    it('should reset password with valid token', async () => {
      const userId = await createVerifiedUser('confirm@test.com', 'OldPass1');

      // Request reset
      await app.inject({
        method: 'POST',
        url: '/api/v1/auth/password-reset/request',
        payload: { email: 'confirm@test.com' },
      });

      await new Promise((resolve) => setTimeout(resolve, 50));

      // Extract token from email
      const emails = emailService.getEmailsTo('confirm@test.com');
      const tokenMatch = emails[0]!.text.match(/token=([a-f0-9]+)/);
      expect(tokenMatch).not.toBeNull();
      const token = tokenMatch![1]!;

      // Confirm reset
      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/password-reset/confirm',
        payload: { token, newPassword: 'NewStrong1' },
      });

      expect(res.statusCode).toBe(200);

      // Verify password was changed
      const user = await userStore.getUser(userId);
      expect(await verifyPassword('NewStrong1', user!.passwordHash!)).toBe(true);
    });

    it('should revoke all refresh tokens on password reset', async () => {
      const userId = await createVerifiedUser('revoke@test.com', 'OldPass1');

      // Create some refresh tokens
      await refreshTokenStore.createToken(userId, 'token-hash-1', '2099-01-01T00:00:00Z');
      await refreshTokenStore.createToken(userId, 'token-hash-2', '2099-01-01T00:00:00Z');

      // Request reset
      await app.inject({
        method: 'POST',
        url: '/api/v1/auth/password-reset/request',
        payload: { email: 'revoke@test.com' },
      });

      await new Promise((resolve) => setTimeout(resolve, 50));

      const emails = emailService.getEmailsTo('revoke@test.com');
      const tokenMatch = emails[0]!.text.match(/token=([a-f0-9]+)/);
      const token = tokenMatch![1]!;

      await app.inject({
        method: 'POST',
        url: '/api/v1/auth/password-reset/confirm',
        payload: { token, newPassword: 'NewStrong1' },
      });

      // Check all tokens are revoked
      const rt1 = await refreshTokenStore.getTokenByHash('token-hash-1');
      const rt2 = await refreshTokenStore.getTokenByHash('token-hash-2');
      expect(rt1!.revokedAt).not.toBeNull();
      expect(rt2!.revokedAt).not.toBeNull();
    });

    it('should reject weak new password', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/password-reset/confirm',
        payload: { token: 'abc', newPassword: 'weak' },
      });

      expect(res.statusCode).toBe(400);
      expect(res.json().error).toBe('Password too weak');
    });

    it('should return 400 for invalid token', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/password-reset/confirm',
        payload: { token: 'invalidtoken', newPassword: 'NewStrong1' },
      });

      expect(res.statusCode).toBe(400);
    });

    it('should return 400 for missing fields', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/password-reset/confirm',
        payload: {},
      });

      expect(res.statusCode).toBe(400);
    });
  });
});
