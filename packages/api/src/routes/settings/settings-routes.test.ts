/**
 * Settings API Routes Integration Tests
 */

import { describe, it, expect, beforeAll, afterAll } from 'vitest';
import { createSettingsServices } from './index.js';
import { buildSettingsTestApp } from './test-utils.js';
import type { FastifyInstance } from 'fastify';

describe('Settings API Routes', () => {
  let app: FastifyInstance;

  beforeAll(async () => {
    app = await buildSettingsTestApp(createSettingsServices());
  });

  afterAll(async () => {
    await app.close();
  });

  // === Agents Config ===

  describe('GET /api/config/agents', () => {
    it('returns default agents config', async () => {
      const res = await app.inject({
        method: 'GET',
        url: '/api/config/agents',
      });
      expect(res.statusCode).toBe(200);
      const body = JSON.parse(res.body);
      expect(body.defaults).toBeDefined();
      expect(body.defaults.providerChain).toBeInstanceOf(Array);
      expect(body.defaults.providerChain.length).toBeGreaterThan(0);
    });
  });

  describe('PUT /api/config/agents', () => {
    it('updates agents config with valid data', async () => {
      const config = {
        defaults: {
          providerChain: [
            { provider: 'claude-code', model: 'claude-sonnet-4' },
            { provider: 'openrouter' },
          ],
          maxBudgetUsd: 10,
        },
      };

      const res = await app.inject({
        method: 'PUT',
        url: '/api/config/agents',
        payload: config,
      });
      expect(res.statusCode).toBe(200);
      const body = JSON.parse(res.body);
      expect(body.defaults.providerChain).toHaveLength(2);
      expect(body.defaults.maxBudgetUsd).toBe(10);
    });

    it('returns 400 for empty provider chain', async () => {
      const config = {
        defaults: { providerChain: [] },
      };

      const res = await app.inject({
        method: 'PUT',
        url: '/api/config/agents',
        payload: config,
      });
      expect(res.statusCode).toBe(400);
    });

    it('returns 400 for invalid provider name', async () => {
      const config = {
        defaults: {
          providerChain: [{ provider: '__proto__' }],
        },
      };

      const res = await app.inject({
        method: 'PUT',
        url: '/api/config/agents',
        payload: config,
      });
      expect(res.statusCode).toBe(400);
    });
  });

  // === Security Config ===

  describe('GET /api/config/security', () => {
    it('returns default security config', async () => {
      const res = await app.inject({
        method: 'GET',
        url: '/api/config/security',
      });
      expect(res.statusCode).toBe(200);
      const body = JSON.parse(res.body);
      expect(body.sanitizeContent).toBe(true);
      expect(body.blockedCommandPatterns).toBeInstanceOf(Array);
    });
  });

  describe('PUT /api/config/security', () => {
    it('updates security config', async () => {
      const config = {
        sanitizeContent: false,
        validateUrls: true,
        gateActions: true,
        maxFetchSizeBytes: 5_242_880,
        blockedCommandPatterns: ['rm\\s+-rf'],
      };

      const res = await app.inject({
        method: 'PUT',
        url: '/api/config/security',
        payload: config,
      });
      expect(res.statusCode).toBe(200);
      const body = JSON.parse(res.body);
      expect(body.sanitizeContent).toBe(false);
      expect(body.gateActions).toBe(true);
    });

    it('returns 400 for invalid regex pattern', async () => {
      const config = {
        blockedCommandPatterns: ['[invalid(regex'],
      };

      const res = await app.inject({
        method: 'PUT',
        url: '/api/config/security',
        payload: config,
      });
      expect(res.statusCode).toBe(400);
    });
  });

  // === Provider Health ===

  describe('GET /api/providers/health', () => {
    it('returns empty health status by default', async () => {
      const res = await app.inject({
        method: 'GET',
        url: '/api/providers/health',
      });
      expect(res.statusCode).toBe(200);
      const body = JSON.parse(res.body);
      expect(body).toEqual({});
    });
  });

  // === Diagnostics ===

  describe('GET /api/providers/diagnostics', () => {
    it('returns empty events by default', async () => {
      const res = await app.inject({
        method: 'GET',
        url: '/api/providers/diagnostics',
      });
      expect(res.statusCode).toBe(200);
      const body = JSON.parse(res.body);
      expect(body).toEqual([]);
    });

    it('respects limit parameter', async () => {
      const res = await app.inject({
        method: 'GET',
        url: '/api/providers/diagnostics?limit=5',
      });
      expect(res.statusCode).toBe(200);
    });
  });

  // === Prompt Templates ===

  describe('GET /api/config/prompts', () => {
    it('returns prompt templates', async () => {
      const res = await app.inject({
        method: 'GET',
        url: '/api/config/prompts',
      });
      expect(res.statusCode).toBe(200);
      const body = JSON.parse(res.body);
      expect(body.defaults).toBeDefined();
    });
  });

  describe('PUT /api/config/prompts/:role', () => {
    it('updates prompt template for a role', async () => {
      const res = await app.inject({
        method: 'PUT',
        url: '/api/config/prompts/architect',
        payload: {
          systemPrompt: 'You are a specialized architect agent.',
        },
      });
      expect(res.statusCode).toBe(200);

      // Verify the update persisted
      const getRes = await app.inject({
        method: 'GET',
        url: '/api/config/prompts',
      });
      const body = JSON.parse(getRes.body);
      expect(body.architect.systemPrompt).toBe('You are a specialized architect agent.');
    });

    it('updates defaults prompt template', async () => {
      const res = await app.inject({
        method: 'PUT',
        url: '/api/config/prompts/defaults',
        payload: {
          systemPrompt: 'Default prompt override.',
          providerPrompts: {
            'claude-code': 'Claude-specific default prompt.',
          },
        },
      });
      expect(res.statusCode).toBe(200);
    });

    it('rejects __proto__ as role name', async () => {
      const res = await app.inject({
        method: 'PUT',
        url: '/api/config/prompts/__proto__',
        payload: { systemPrompt: 'exploit' },
      });
      expect(res.statusCode).toBe(400);
      const body = JSON.parse(res.body);
      expect(body.error).toMatch(/Unknown role/);
    });

    it('rejects constructor as role name', async () => {
      const res = await app.inject({
        method: 'PUT',
        url: '/api/config/prompts/constructor',
        payload: { systemPrompt: 'exploit' },
      });
      expect(res.statusCode).toBe(400);
    });

    it('clears prompt when empty string sent', async () => {
      // First set a prompt
      await app.inject({
        method: 'PUT',
        url: '/api/config/prompts/reviewer',
        payload: { systemPrompt: 'Review all code.' },
      });

      // Now clear it
      await app.inject({
        method: 'PUT',
        url: '/api/config/prompts/reviewer',
        payload: { systemPrompt: '' },
      });

      const getRes = await app.inject({
        method: 'GET',
        url: '/api/config/prompts',
      });
      const body = JSON.parse(getRes.body);
      expect(body.reviewer.systemPrompt).toBeUndefined();
    });
  });

  // === Diagnostics type validation ===

  describe('GET /api/providers/diagnostics with invalid type', () => {
    it('returns 400 for invalid event type', async () => {
      const res = await app.inject({
        method: 'GET',
        url: '/api/providers/diagnostics?type=invalid_event',
      });
      expect(res.statusCode).toBe(400);
      const body = JSON.parse(res.body);
      expect(body.error).toMatch(/Invalid event type/);
    });

    it('accepts valid event type', async () => {
      const res = await app.inject({
        method: 'GET',
        url: '/api/providers/diagnostics?type=provider:complete',
      });
      expect(res.statusCode).toBe(200);
    });
  });
});
