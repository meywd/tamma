/**
 * Provider Factory Routes Integration Tests
 *
 * Story 9-4: Tests for provider session create/execute/dispose endpoints.
 */

import { describe, it, expect, beforeAll, afterAll, vi } from 'vitest';
import { createSettingsServices } from '../index.js';
import { buildSettingsTestApp } from '../test-utils.js';
import { ProviderSessionService } from '../../../services/provider-session.js';
import type { IAgentProviderFactory, IAgentProvider } from '@tamma/providers';
import type { AgentTaskResult } from '@tamma/shared';
import type { FastifyInstance } from 'fastify';

function createMockProvider(): IAgentProvider {
  return {
    executeTask: vi.fn().mockResolvedValue({
      success: true,
      output: 'test output',
      costUsd: 0,
      durationMs: 100,
    } satisfies AgentTaskResult),
    isAvailable: vi.fn().mockResolvedValue(true),
    dispose: vi.fn().mockResolvedValue(undefined),
  };
}

function createMockFactory(): IAgentProviderFactory {
  return {
    create: vi.fn().mockResolvedValue(createMockProvider()),
    register: vi.fn(),
    dispose: vi.fn().mockResolvedValue(undefined),
  };
}

describe('Provider Factory Routes', () => {
  let app: FastifyInstance;
  let sessionService: ProviderSessionService;

  beforeAll(async () => {
    const factory = createMockFactory();
    sessionService = new ProviderSessionService(factory, { autoCleanup: false });
    const settingsServices = createSettingsServices();
    settingsServices.providerSessionService = sessionService;
    app = await buildSettingsTestApp(settingsServices);
  });

  afterAll(async () => {
    await sessionService.disposeAll();
    await app.close();
  });

  // ---- POST /api/providers/providers/create ----

  describe('POST /api/providers/providers/create', () => {
    it('creates a session and returns a handle', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/providers/providers/create',
        payload: { provider: 'claude-code' },
      });
      expect(res.statusCode).toBe(201);
      const body = JSON.parse(res.body);
      expect(body.handle).toBeTruthy();
      expect(body.provider).toBe('claude-code');
      expect(body.model).toBe('default');
    });

    it('creates a session with model', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/providers/providers/create',
        payload: { provider: 'openrouter', model: 'gpt-4' },
      });
      expect(res.statusCode).toBe(201);
      const body = JSON.parse(res.body);
      expect(body.model).toBe('gpt-4');
    });

    it('rejects missing provider', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/providers/providers/create',
        payload: {},
      });
      expect(res.statusCode).toBe(400);
    });

    it('rejects non-object body', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/providers/providers/create',
        payload: 'not-an-object',
        headers: { 'content-type': 'text/plain' },
      });
      // Fastify returns 415 or 400 for content-type mismatch
      expect(res.statusCode).toBeGreaterThanOrEqual(400);
    });
  });

  // ---- POST /api/providers/providers/:handle/execute ----

  describe('POST /api/providers/providers/:handle/execute', () => {
    it('executes a task on a valid session', async () => {
      // Create a session first
      const createRes = await app.inject({
        method: 'POST',
        url: '/api/providers/providers/create',
        payload: { provider: 'claude-code' },
      });
      const { handle } = JSON.parse(createRes.body);

      const res = await app.inject({
        method: 'POST',
        url: `/api/providers/providers/${handle}/execute`,
        payload: { prompt: 'Implement feature X', workingDirectory: '/tmp/test' },
      });
      expect(res.statusCode).toBe(200);
      const body = JSON.parse(res.body);
      expect(body.success).toBe(true);
      expect(body.output).toBe('test output');
    });

    it('returns 404 for unknown handle', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/providers/providers/00000000-0000-0000-0000-000000000000/execute',
        payload: { prompt: 'test', workingDirectory: '/tmp' },
      });
      expect(res.statusCode).toBe(404);
    });

    it('rejects invalid handle format', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/providers/providers/not-a-uuid/execute',
        payload: { prompt: 'test', workingDirectory: '/tmp' },
      });
      expect(res.statusCode).toBe(400);
    });

    it('rejects missing prompt', async () => {
      const createRes = await app.inject({
        method: 'POST',
        url: '/api/providers/providers/create',
        payload: { provider: 'claude-code' },
      });
      const { handle } = JSON.parse(createRes.body);

      const res = await app.inject({
        method: 'POST',
        url: `/api/providers/providers/${handle}/execute`,
        payload: {},
      });
      expect(res.statusCode).toBe(400);
    });
  });

  // ---- DELETE /api/providers/providers/:handle ----

  describe('DELETE /api/providers/providers/:handle', () => {
    it('disposes a session', async () => {
      const createRes = await app.inject({
        method: 'POST',
        url: '/api/providers/providers/create',
        payload: { provider: 'claude-code' },
      });
      const { handle } = JSON.parse(createRes.body);

      const res = await app.inject({
        method: 'DELETE',
        url: `/api/providers/providers/${handle}`,
      });
      expect(res.statusCode).toBe(200);
      const body = JSON.parse(res.body);
      expect(body.disposed).toBe(true);
    });

    it('returns 404 for unknown handle', async () => {
      const res = await app.inject({
        method: 'DELETE',
        url: '/api/providers/providers/00000000-0000-0000-0000-000000000000',
      });
      expect(res.statusCode).toBe(404);
    });
  });

  // ---- GET /api/providers/providers/sessions ----

  describe('GET /api/providers/providers/sessions', () => {
    it('lists active sessions', async () => {
      const res = await app.inject({
        method: 'GET',
        url: '/api/providers/providers/sessions',
      });
      expect(res.statusCode).toBe(200);
      const body = JSON.parse(res.body);
      expect(body.sessions).toBeInstanceOf(Array);
      expect(typeof body.count).toBe('number');
    });
  });
});
