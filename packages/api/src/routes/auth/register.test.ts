/**
 * Tests for registration routes (Story 18-1).
 */

import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import Fastify from 'fastify';
import type { FastifyInstance } from 'fastify';
import { InMemoryUserStore } from '../../persistence/user-store.js';
import { InMemoryEmailService } from '../../services/email.js';
import { registerRegistrationRoutes } from './register.js';

describe('Registration Routes', () => {
  let app: FastifyInstance;
  let userStore: InMemoryUserStore;
  let emailService: InMemoryEmailService;

  beforeEach(async () => {
    app = Fastify({ logger: false });
    userStore = new InMemoryUserStore();
    emailService = new InMemoryEmailService();

    await registerRegistrationRoutes(app, { userStore, emailService });
    await app.ready();
  });

  afterEach(async () => {
    await app.close();
  });

  describe('POST /api/v1/auth/register', () => {
    it('should register a new user', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/register',
        payload: {
          email: 'alice@example.com',
          password: 'StrongPass1',
          name: 'Alice',
        },
      });

      expect(res.statusCode).toBe(201);
      const body = res.json();
      expect(body.email).toBe('alice@example.com');
      expect(body.message).toBe('Verification email sent');
      expect(body.id).toBeDefined();
    });

    it('should send a verification email', async () => {
      await app.inject({
        method: 'POST',
        url: '/api/v1/auth/register',
        payload: {
          email: 'bob@example.com',
          password: 'StrongPass1',
          name: 'Bob',
        },
      });

      // Small delay to let fire-and-forget email send
      await new Promise((resolve) => setTimeout(resolve, 50));

      const emails = emailService.getEmailsTo('bob@example.com');
      expect(emails.length).toBe(1);
      expect(emails[0]!.subject).toContain('Verify');
    });

    it('should return 400 for missing fields', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/register',
        payload: { email: 'test@test.com' },
      });

      expect(res.statusCode).toBe(400);
    });

    it('should return 400 for invalid email', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/register',
        payload: {
          email: 'not-an-email',
          password: 'StrongPass1',
          name: 'Test',
        },
      });

      expect(res.statusCode).toBe(400);
    });

    it('should return 400 for weak password', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/register',
        payload: {
          email: 'test@test.com',
          password: 'weak',
          name: 'Test',
        },
      });

      expect(res.statusCode).toBe(400);
      expect(res.json().error).toBe('Password too weak');
    });

    it('should return 400 for short name', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/register',
        payload: {
          email: 'test@test.com',
          password: 'StrongPass1',
          name: 'A',
        },
      });

      expect(res.statusCode).toBe(400);
    });

    it('should return 409 for duplicate email', async () => {
      await app.inject({
        method: 'POST',
        url: '/api/v1/auth/register',
        payload: {
          email: 'dup@example.com',
          password: 'StrongPass1',
          name: 'First',
        },
      });

      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/register',
        payload: {
          email: 'DUP@example.com',
          password: 'StrongPass2',
          name: 'Second',
        },
      });

      expect(res.statusCode).toBe(409);
    });

    it('should normalize email to lowercase', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/register',
        payload: {
          email: 'UPPER@EXAMPLE.COM',
          password: 'StrongPass1',
          name: 'Upper',
        },
      });

      expect(res.statusCode).toBe(201);
      expect(res.json().email).toBe('upper@example.com');
    });
  });

  describe('POST /api/v1/auth/verify-email', () => {
    it('should verify email with valid token', async () => {
      // Register a user
      await app.inject({
        method: 'POST',
        url: '/api/v1/auth/register',
        payload: {
          email: 'verify@example.com',
          password: 'StrongPass1',
          name: 'Verify',
        },
      });

      // Wait for email
      await new Promise((resolve) => setTimeout(resolve, 50));

      // Extract token from email
      const emails = emailService.getEmailsTo('verify@example.com');
      const tokenMatch = emails[0]!.text.match(/token=([a-f0-9]+)/);
      expect(tokenMatch).not.toBeNull();
      const token = tokenMatch![1]!;

      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/verify-email',
        payload: { token },
      });

      expect(res.statusCode).toBe(200);
      expect(res.json().message).toContain('verified');

      // Verify user is marked as verified
      const user = await userStore.getUserByEmail('verify@example.com');
      expect(user!.emailVerified).toBe(true);
    });

    it('should return 400 for invalid token', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/verify-email',
        payload: { token: 'invalid-token' },
      });

      expect(res.statusCode).toBe(400);
    });

    it('should return 400 for missing token', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/verify-email',
        payload: {},
      });

      expect(res.statusCode).toBe(400);
    });
  });

  describe('POST /api/v1/auth/resend-verification', () => {
    it('should return success message regardless of email existence', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/resend-verification',
        payload: { email: 'nonexistent@example.com' },
      });

      expect(res.statusCode).toBe(200);
      expect(res.json().message).toContain('If the email exists');
    });

    it('should return 400 for missing email', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/v1/auth/resend-verification',
        payload: {},
      });

      expect(res.statusCode).toBe(400);
    });
  });
});
