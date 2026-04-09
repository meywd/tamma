/**
 * Engine Task Route Tests
 *
 * Tests the agent task execution and cycle result endpoints:
 *   POST /api/engine/execute-task
 *   POST /api/engine/cycle-result
 *   GET  /api/engine/cycle-results
 *
 * Story 6-11: Context API Wiring
 */

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import Fastify from 'fastify';
import type { FastifyInstance } from 'fastify';
import { registerEngineTaskRoutes } from '../engine-task-routes.js';
import type { IAgentResolver } from '../engine-task-routes.js';

/** Create a mock agent resolver with configurable behavior. */
function createMockResolver(overrides?: Partial<IAgentResolver>): IAgentResolver {
  return {
    getAgentForRole: vi.fn().mockResolvedValue({
      executeTask: vi.fn().mockResolvedValue({
        success: true,
        output: 'Generated code output',
        costUsd: 0.05,
        durationMs: 1500,
      }),
    }),
    ...overrides,
  };
}

describe('Engine Task Routes', () => {
  // -----------------------------------------------------------------------
  // POST /api/engine/execute-task (with resolver)
  // -----------------------------------------------------------------------

  describe('POST /api/engine/execute-task', () => {
    let app: FastifyInstance;
    let resolver: IAgentResolver;

    beforeEach(async () => {
      resolver = createMockResolver();
      app = Fastify({ logger: false });
      await registerEngineTaskRoutes(app, {
        agentResolver: resolver,
        cwd: '/workspace',
        projectId: 'test-project',
        engineId: 'test-engine',
      });
      await app.ready();
    });

    afterEach(async () => {
      await app.close();
    });

    it('executes a task and returns result', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/execute-task',
        payload: {
          prompt: 'Write a hello world function',
          role: 'developer',
        },
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.success).toBe(true);
      expect(body.output).toBe('Generated code output');
      expect(body.costUsd).toBe(0.05);
      expect(body.durationMs).toBeGreaterThan(0);
      expect(body.tokensUsed).toBe(0); // Not yet wired
      expect(body.toolCalls).toBe(0); // Not yet wired
    });

    it('uses default role "developer" when not specified', async () => {
      await app.inject({
        method: 'POST',
        url: '/api/engine/execute-task',
        payload: { prompt: 'Do something' },
      });

      expect(resolver.getAgentForRole).toHaveBeenCalledWith('developer', {
        projectId: 'test-project',
        engineId: 'test-engine',
      });
    });

    it('passes model and maxBudgetUsd to agent', async () => {
      const mockAgent = {
        executeTask: vi.fn().mockResolvedValue({
          success: true,
          output: 'ok',
          costUsd: 0.01,
          durationMs: 100,
        }),
      };
      (resolver.getAgentForRole as ReturnType<typeof vi.fn>).mockResolvedValueOnce(mockAgent);

      await app.inject({
        method: 'POST',
        url: '/api/engine/execute-task',
        payload: {
          prompt: 'test',
          model: 'gpt-4',
          maxBudgetUsd: 0.50,
        },
      });

      expect(mockAgent.executeTask).toHaveBeenCalledWith(
        expect.objectContaining({
          prompt: 'test',
          model: 'gpt-4',
          maxBudgetUsd: 0.50,
        }),
      );
    });

    it('rejects missing prompt (400)', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/execute-task',
        payload: { role: 'developer' },
      });

      expect(response.statusCode).toBe(400);
    });

    it('returns 500 when agent throws', async () => {
      (resolver.getAgentForRole as ReturnType<typeof vi.fn>).mockRejectedValueOnce(
        new Error('Provider unavailable'),
      );

      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/execute-task',
        payload: { prompt: 'test' },
      });

      expect(response.statusCode).toBe(500);
      expect(response.json().success).toBe(false);
      expect(response.json().error).toContain('Provider unavailable');
    });

    it('includes error field when agent reports failure', async () => {
      const mockAgent = {
        executeTask: vi.fn().mockResolvedValue({
          success: false,
          output: '',
          costUsd: 0.02,
          durationMs: 200,
          error: 'Task failed validation',
        }),
      };
      (resolver.getAgentForRole as ReturnType<typeof vi.fn>).mockResolvedValueOnce(mockAgent);

      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/execute-task',
        payload: { prompt: 'test' },
      });

      expect(response.statusCode).toBe(200);
      expect(response.json().success).toBe(false);
      expect(response.json().error).toBe('Task failed validation');
    });
  });

  // -----------------------------------------------------------------------
  // POST /api/engine/execute-task (without resolver)
  // -----------------------------------------------------------------------

  describe('POST /api/engine/execute-task (no resolver)', () => {
    let app: FastifyInstance;

    beforeEach(async () => {
      app = Fastify({ logger: false });
      await registerEngineTaskRoutes(app, {});
      await app.ready();
    });

    afterEach(async () => {
      await app.close();
    });

    it('returns 503 when agent resolver is not configured', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/execute-task',
        payload: { prompt: 'test' },
      });

      expect(response.statusCode).toBe(503);
      expect(response.json().error).toContain('Agent resolver not configured');
    });
  });

  // -----------------------------------------------------------------------
  // POST /api/engine/cycle-result
  // -----------------------------------------------------------------------

  describe('POST /api/engine/cycle-result', () => {
    let app: FastifyInstance;

    beforeEach(async () => {
      app = Fastify({ logger: false });
      await registerEngineTaskRoutes(app, {});
      await app.ready();
    });

    afterEach(async () => {
      await app.close();
    });

    it('stores a cycle result', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/cycle-result',
        payload: {
          exitReason: 'success',
          issueNumber: 42,
          repository: 'owner/repo',
          durationMs: 5000,
        },
      });

      expect(response.statusCode).toBe(201);
      const body = response.json();
      expect(body.id).toBeDefined();
      expect(body.storedAt).toBeDefined();
    });

    it('stores a cycle result with error', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/cycle-result',
        payload: {
          exitReason: 'error',
          error: 'Build failed with exit code 1',
        },
      });

      expect(response.statusCode).toBe(201);
    });

    it('stores a cycle result with metadata', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/cycle-result',
        payload: {
          exitReason: 'success',
          metadata: { totalCostUsd: 0.15, stepsCompleted: 5 },
        },
      });

      expect(response.statusCode).toBe(201);
    });

    it('rejects missing exitReason (400)', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/cycle-result',
        payload: { issueNumber: 42 },
      });

      expect(response.statusCode).toBe(400);
    });
  });

  // -----------------------------------------------------------------------
  // GET /api/engine/cycle-results
  // -----------------------------------------------------------------------

  describe('GET /api/engine/cycle-results', () => {
    let app: FastifyInstance;

    beforeEach(async () => {
      app = Fastify({ logger: false });
      await registerEngineTaskRoutes(app, {});
      await app.ready();
    });

    afterEach(async () => {
      await app.close();
    });

    it('returns stored cycle results', async () => {
      // Store some results
      await app.inject({
        method: 'POST',
        url: '/api/engine/cycle-result',
        payload: { exitReason: 'success', issueNumber: 1 },
      });
      await app.inject({
        method: 'POST',
        url: '/api/engine/cycle-result',
        payload: { exitReason: 'error', issueNumber: 2 },
      });

      const response = await app.inject({
        method: 'GET',
        url: '/api/engine/cycle-results',
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.data.length).toBeGreaterThanOrEqual(2);
      expect(body.total).toBeGreaterThanOrEqual(2);
    });

    it('filters by issueNumber', async () => {
      await app.inject({
        method: 'POST',
        url: '/api/engine/cycle-result',
        payload: { exitReason: 'success', issueNumber: 100 },
      });

      const response = await app.inject({
        method: 'GET',
        url: '/api/engine/cycle-results?issueNumber=100',
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.data.every((r: { issueNumber: number }) => r.issueNumber === undefined || r.issueNumber === 100)).toBe(true);
    });

    it('respects limit parameter', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/engine/cycle-results?limit=1',
      });

      expect(response.statusCode).toBe(200);
      expect(response.json().data.length).toBeLessThanOrEqual(1);
    });
  });
});
