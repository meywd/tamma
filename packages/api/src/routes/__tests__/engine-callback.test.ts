/**
 * Engine Callback Route Tests
 *
 * Tests the ELSA engine callback endpoints:
 * - POST /api/engine/execute-task
 * - GET  /api/engine/agent-available
 * Including API key authentication.
 */

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import Fastify from 'fastify';
import type { FastifyInstance } from 'fastify';
import { engineCallbackPlugin } from '../engine-callback.js';

/** Shape matching IAgentProvider for testing. */
interface MockAgent {
  executeTask: ReturnType<typeof vi.fn>;
  isAvailable: ReturnType<typeof vi.fn>;
  dispose: ReturnType<typeof vi.fn>;
}

/** Create a mock IAgentProvider. */
function createMockAgent(overrides: Partial<MockAgent> = {}): MockAgent {
  return {
    isAvailable: vi.fn().mockResolvedValue(true),
    executeTask: vi.fn().mockResolvedValue({
      success: true,
      output: 'Task completed successfully',
      costUsd: 0.05,
      durationMs: 1234,
    }),
    dispose: vi.fn().mockResolvedValue(undefined),
    ...overrides,
  };
}

describe('Engine Callback Routes', () => {
  let app: FastifyInstance;
  let mockAgent: MockAgent;

  describe('without API key auth', () => {
    beforeEach(async () => {
      mockAgent = createMockAgent();
      app = Fastify({ logger: false });
      await app.register(engineCallbackPlugin, {
        agent: mockAgent,
        cwd: '/test/workdir',
      });
      await app.ready();
    });

    afterEach(async () => {
      await app.close();
    });

    // -------------------------------------------------------------------
    // POST /api/engine/execute-task
    // -------------------------------------------------------------------

    it('executes task successfully', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/execute-task',
        payload: { prompt: 'Analyze this code' },
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.success).toBe(true);
      expect(body.output).toBe('Task completed successfully');
      expect(body.costUsd).toBe(0.05);
      expect(body.durationMs).toBe(1234);
    });

    it('includes analysisType in prompt when provided', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/execute-task',
        payload: { prompt: 'Do something', analysisType: 'security-review' },
      });

      expect(response.statusCode).toBe(200);
      expect(mockAgent.executeTask).toHaveBeenCalledWith(
        expect.objectContaining({
          prompt: expect.stringContaining('[Analysis Type: security-review]'),
        }),
      );
    });

    it('returns 400 for empty prompt', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/execute-task',
        payload: { prompt: '' },
      });

      expect(response.statusCode).toBe(400);
    });

    it('returns 400 for missing prompt', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/execute-task',
        payload: {},
      });

      expect(response.statusCode).toBe(400);
    });

    it('returns 500 with error when agent throws', async () => {
      vi.mocked(mockAgent.executeTask).mockRejectedValueOnce(new Error('Agent crashed'));

      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/execute-task',
        payload: { prompt: 'Do something' },
      });

      expect(response.statusCode).toBe(500);
      const body = response.json();
      expect(body.success).toBe(false);
      expect(body.error).toBe('Agent crashed');
      expect(body.durationMs).toBeGreaterThanOrEqual(0);
    });

    it('passes model and maxBudgetUsd to agent config', async () => {
      await app.close();

      app = Fastify({ logger: false });
      await app.register(engineCallbackPlugin, {
        agent: mockAgent,
        cwd: '/test/workdir',
        model: 'claude-sonnet-4',
        maxBudgetUsd: 1.0,
      });
      await app.ready();

      await app.inject({
        method: 'POST',
        url: '/api/engine/execute-task',
        payload: { prompt: 'Test prompt' },
      });

      expect(mockAgent.executeTask).toHaveBeenCalledWith(
        expect.objectContaining({
          cwd: '/test/workdir',
          model: 'claude-sonnet-4',
          maxBudgetUsd: 1.0,
        }),
      );
    });

    // -------------------------------------------------------------------
    // GET /api/engine/agent-available
    // -------------------------------------------------------------------

    it('returns available true when agent is available', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/engine/agent-available',
      });

      expect(response.statusCode).toBe(200);
      expect(response.json()).toEqual({ available: true });
    });

    it('returns available false when agent throws', async () => {
      vi.mocked(mockAgent.isAvailable).mockRejectedValueOnce(new Error('Check failed'));

      const response = await app.inject({
        method: 'GET',
        url: '/api/engine/agent-available',
      });

      expect(response.statusCode).toBe(200);
      expect(response.json()).toEqual({ available: false });
    });

    it('returns available false when agent reports unavailable', async () => {
      vi.mocked(mockAgent.isAvailable).mockResolvedValueOnce(false);

      const response = await app.inject({
        method: 'GET',
        url: '/api/engine/agent-available',
      });

      expect(response.statusCode).toBe(200);
      expect(response.json()).toEqual({ available: false });
    });
  });

  // -----------------------------------------------------------------------
  // With API key auth
  // -----------------------------------------------------------------------

  describe('with API key auth', () => {
    const API_KEY = 'test-secret-key';

    beforeEach(async () => {
      mockAgent = createMockAgent();
      app = Fastify({ logger: false });
      await app.register(engineCallbackPlugin, {
        agent: mockAgent,
        cwd: '/test/workdir',
        apiKey: API_KEY,
      });
      await app.ready();
    });

    afterEach(async () => {
      await app.close();
    });

    it('accepts valid API key', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/execute-task',
        headers: { 'x-api-key': API_KEY },
        payload: { prompt: 'Test' },
      });

      expect(response.statusCode).toBe(200);
    });

    it('rejects missing API key (401)', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/execute-task',
        payload: { prompt: 'Test' },
      });

      expect(response.statusCode).toBe(401);
      expect(response.json().error).toContain('Missing');
    });

    it('rejects invalid API key (401)', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/execute-task',
        headers: { 'x-api-key': 'wrong-key' },
        payload: { prompt: 'Test' },
      });

      expect(response.statusCode).toBe(401);
      expect(response.json().error).toContain('Invalid');
    });

    it('also protects agent-available endpoint', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/engine/agent-available',
      });

      expect(response.statusCode).toBe(401);
    });

    it('agent-available works with valid key', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/engine/agent-available',
        headers: { 'x-api-key': API_KEY },
      });

      expect(response.statusCode).toBe(200);
      expect(response.json().available).toBe(true);
    });
  });
});
