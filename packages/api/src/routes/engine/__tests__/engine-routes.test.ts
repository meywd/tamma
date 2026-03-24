/**
 * Engine REST/SSE Route Tests
 *
 * Tests the engine control and state endpoints:
 * - POST /api/engine/command (start, stop, pause, resume, approve, reject, skip)
 * - GET  /api/engine/state
 * - GET  /api/engine/stats
 * - GET  /api/engine/plan
 * - GET  /api/engine/history
 */

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import Fastify from 'fastify';
import type { FastifyInstance } from 'fastify';
import { registerEngineRoutes } from '../index.js';

/** Minimal mock TammaEngine for testing routes. */
function createMockEngine() {
  return {
    getState: vi.fn().mockReturnValue('idle'),
    getCurrentIssue: vi.fn().mockReturnValue(null),
    getCurrentPlan: vi.fn().mockReturnValue(null),
    getCurrentBranch: vi.fn().mockReturnValue(null),
    getStats: vi.fn().mockReturnValue({
      issuesProcessed: 5,
      issuesSucceeded: 4,
      issuesFailed: 1,
      totalDurationMs: 60000,
    }),
    getEventStore: vi.fn().mockReturnValue({
      getEvents: vi.fn().mockReturnValue([]),
    }),
    run: vi.fn().mockResolvedValue(undefined),
    dispose: vi.fn().mockResolvedValue(undefined),
  };
}

describe('Engine Routes', () => {
  let app: FastifyInstance;
  let engine: ReturnType<typeof createMockEngine>;

  beforeEach(async () => {
    engine = createMockEngine();
    app = Fastify({ logger: false });

    await app.register(
      async (instance) => {
        await registerEngineRoutes(instance, { engine: engine as any });
      },
      { prefix: '' },
    );
    await app.ready();
  });

  afterEach(async () => {
    await app.close();
  });

  // -----------------------------------------------------------------------
  // POST /api/engine/command
  // -----------------------------------------------------------------------

  describe('POST /api/engine/command', () => {
    it('accepts start command', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/command',
        payload: { type: 'start' },
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.ok).toBe(true);
      expect(body.type).toBe('start');
      expect(engine.run).toHaveBeenCalled();
    });

    it('accepts stop command', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/command',
        payload: { type: 'stop' },
      });

      expect(response.statusCode).toBe(200);
      expect(response.json().type).toBe('stop');
      expect(engine.dispose).toHaveBeenCalled();
    });

    it('accepts pause command without calling dispose', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/command',
        payload: { type: 'pause' },
      });

      expect(response.statusCode).toBe(200);
      expect(response.json().type).toBe('pause');
      // Pause must NOT call dispose — that would tear down resources
      expect(engine.dispose).not.toHaveBeenCalled();
    });

    it('accepts resume command', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/command',
        payload: { type: 'resume' },
      });

      expect(response.statusCode).toBe(200);
      expect(response.json().type).toBe('resume');
      expect(engine.run).toHaveBeenCalled();
    });

    it('handles approval commands (not yet wired)', async () => {
      for (const type of ['approve', 'reject', 'skip']) {
        const response = await app.inject({
          method: 'POST',
          url: '/api/engine/command',
          payload: { type },
        });

        expect(response.statusCode).toBe(200);
        const body = response.json();
        expect(body.ok).toBe(false);
        expect(body.error).toContain('not yet wired');
      }
    });

    it('accepts reject command with feedback without 400', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/command',
        payload: { type: 'reject', feedback: 'Code quality is insufficient' },
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.type).toBe('reject');
      // Verify feedback field in payload doesn't cause validation error
      expect(body.ok).toBe(false); // Not yet wired
    });

    it('rejects invalid command type (400)', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/command',
        payload: { type: 'explode' },
      });

      expect(response.statusCode).toBe(400);
    });

    it('rejects missing body (400)', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/command',
        payload: {},
      });

      expect(response.statusCode).toBe(400);
    });
  });

  // -----------------------------------------------------------------------
  // GET /api/engine/state
  // -----------------------------------------------------------------------

  describe('GET /api/engine/state', () => {
    it('returns current state snapshot', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/engine/state',
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.state).toBe('idle');
      expect(body.issue).toBeNull();
      expect(body.plan).toBeNull();
      expect(body.branch).toBeNull();
      expect(body.stats).toBeDefined();
      expect(body.timestamp).toBeDefined();
    });

    it('reflects engine state changes', async () => {
      engine.getState.mockReturnValue('processing');
      engine.getCurrentIssue.mockReturnValue({ number: 42, title: 'Test' });
      engine.getCurrentBranch.mockReturnValue('tamma/issue-42');

      const response = await app.inject({
        method: 'GET',
        url: '/api/engine/state',
      });

      const body = response.json();
      expect(body.state).toBe('processing');
      expect(body.issue).toEqual({ number: 42, title: 'Test' });
      expect(body.branch).toBe('tamma/issue-42');
    });
  });

  // -----------------------------------------------------------------------
  // GET /api/engine/stats
  // -----------------------------------------------------------------------

  describe('GET /api/engine/stats', () => {
    it('returns engine statistics', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/engine/stats',
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.issuesProcessed).toBe(5);
      expect(body.issuesSucceeded).toBe(4);
      expect(body.issuesFailed).toBe(1);
    });
  });

  // -----------------------------------------------------------------------
  // GET /api/engine/plan
  // -----------------------------------------------------------------------

  describe('GET /api/engine/plan', () => {
    it('returns null when no plan exists', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/engine/plan',
      });

      expect(response.statusCode).toBe(200);
      expect(response.json()).toBeNull();
    });

    it('returns current plan when one exists', async () => {
      const plan = { steps: ['analyze', 'generate', 'test'], currentStep: 0 };
      engine.getCurrentPlan.mockReturnValue(plan);

      const response = await app.inject({
        method: 'GET',
        url: '/api/engine/plan',
      });

      expect(response.statusCode).toBe(200);
      expect(response.json()).toEqual(plan);
    });
  });

  // -----------------------------------------------------------------------
  // GET /api/engine/history
  // -----------------------------------------------------------------------

  describe('GET /api/engine/history', () => {
    it('returns empty when no events exist', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/engine/history',
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.data).toEqual([]);
      expect(body.total).toBe(0);
      expect(body.page).toBe(1);
      expect(body.pageSize).toBe(50);
    });

    it('returns events when event store has data', async () => {
      const events = [
        { id: '1', type: 'CODE.GENERATED.SUCCESS', timestamp: Date.now() },
        { id: '2', type: 'TEST.RUN.SUCCESS', timestamp: Date.now() },
      ];
      engine.getEventStore.mockReturnValue({
        getEvents: vi.fn().mockReturnValue(events),
      });

      const response = await app.inject({
        method: 'GET',
        url: '/api/engine/history',
      });

      const body = response.json();
      expect(body.data).toHaveLength(2);
      expect(body.total).toBe(2);
    });

    it('supports pagination', async () => {
      const events = Array.from({ length: 10 }, (_, i) => ({
        id: String(i),
        type: 'EVENT',
        timestamp: Date.now(),
      }));
      engine.getEventStore.mockReturnValue({
        getEvents: vi.fn().mockReturnValue(events),
      });

      const response = await app.inject({
        method: 'GET',
        url: '/api/engine/history?page=2&pageSize=3',
      });

      const body = response.json();
      expect(body.data).toHaveLength(3);
      expect(body.total).toBe(10);
      expect(body.page).toBe(2);
      expect(body.pageSize).toBe(3);
    });

    it('returns empty data when event store is undefined', async () => {
      engine.getEventStore.mockReturnValue(undefined);

      const response = await app.inject({
        method: 'GET',
        url: '/api/engine/history',
      });

      expect(response.json().data).toEqual([]);
    });

    it('filters by issueNumber', async () => {
      engine.getEventStore.mockReturnValue({
        getEvents: vi.fn().mockImplementation((issueNumber?: number) => {
          if (issueNumber === 42) return [{ id: '1', type: 'EVENT', timestamp: Date.now() }];
          return [];
        }),
      });

      const response = await app.inject({
        method: 'GET',
        url: '/api/engine/history?issueNumber=42',
      });

      expect(response.json().data).toHaveLength(1);
    });
  });
});
