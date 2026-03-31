/**
 * Tests for the /api/config/providers routes.
 */

import { describe, it, expect, beforeEach, vi } from 'vitest';
import Fastify from 'fastify';
import type { FastifyInstance } from 'fastify';
import { ConfigService } from '../../../services/settings/ConfigService.js';
import { InMemoryUserStore } from '../../../persistence/user-store.js';
import { registerProvidersRoutes } from '../providers-routes.js';

describe('Providers Routes', () => {
  let app: FastifyInstance;
  let store: InMemoryUserStore;
  let service: ConfigService;
  let userId: string;

  beforeEach(async () => {
    store = new InMemoryUserStore();
    service = new ConfigService(undefined, undefined, null, store, null);

    const user = await store.upsertUser({
      githubId: 1001,
      githubLogin: 'test-user',
      email: null,
      role: 'member',
    });
    userId = user.id;

    app = Fastify();
    await app.register(async (instance) => {
      registerProvidersRoutes(instance, service);
    }, { prefix: '/api/config' });
    await app.ready();
  });

  describe('GET /api/config/providers', () => {
    it('returns 401 without auth', async () => {
      const response = await app.inject({ method: 'GET', url: '/api/config/providers' });
      expect(response.statusCode).toBe(401);
    });

    it('returns empty providers for a new user', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/config/providers',
        headers: { 'x-user-id': userId },
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.providers).toEqual({});
    });

    it('returns previously saved providers', async () => {
      await store.updateUserSettings(userId, {
        providers: { anthropic: { apiKey: 'sk-test' } },
      });

      const response = await app.inject({
        method: 'GET',
        url: '/api/config/providers',
        headers: { 'x-user-id': userId },
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.providers.anthropic.apiKey).toBe('sk-test');
    });
  });

  describe('PUT /api/config/providers', () => {
    it('returns 401 without auth', async () => {
      const response = await app.inject({
        method: 'PUT',
        url: '/api/config/providers',
        payload: { providers: { anthropic: {} } },
      });
      expect(response.statusCode).toBe(401);
    });

    it('updates and returns providers config', async () => {
      const response = await app.inject({
        method: 'PUT',
        url: '/api/config/providers',
        headers: { 'x-user-id': userId, 'content-type': 'application/json' },
        payload: {
          providers: {
            anthropic: { apiKey: 'sk-new', defaultModel: 'claude-opus-4-6' },
          },
          maxBudgetUsd: 5.0,
        },
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.providers.anthropic.apiKey).toBe('sk-new');
      expect(body.maxBudgetUsd).toBe(5.0);
    });

    it('returns 400 for empty providers', async () => {
      const response = await app.inject({
        method: 'PUT',
        url: '/api/config/providers',
        headers: { 'x-user-id': userId, 'content-type': 'application/json' },
        payload: { providers: {} },
      });

      expect(response.statusCode).toBe(400);
      const body = response.json();
      expect(body.error).toContain('At least one provider');
    });

    it('returns 400 for invalid provider name', async () => {
      const response = await app.inject({
        method: 'PUT',
        url: '/api/config/providers',
        headers: { 'x-user-id': userId, 'content-type': 'application/json' },
        payload: { providers: { 'INVALID NAME': {} } },
      });

      expect(response.statusCode).toBe(400);
    });

    it('returns 400 for non-object body', async () => {
      const response = await app.inject({
        method: 'PUT',
        url: '/api/config/providers',
        headers: { 'x-user-id': userId, 'content-type': 'application/json' },
        payload: '"not an object"',
      });

      expect(response.statusCode).toBe(400);
    });
  });
});
