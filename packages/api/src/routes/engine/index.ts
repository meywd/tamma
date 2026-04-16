/**
 * Engine REST/SSE Routes
 *
 * Fastify plugin that exposes the TammaEngine over HTTP:
 *   POST /api/engine/command         - dispatch an EngineCommand
 *   GET  /api/engine/state           - current state snapshot (JSON)
 *   GET  /api/engine/events/state    - SSE stream of state updates
 *   GET  /api/engine/events/logs     - SSE stream of log entries
 *   GET  /api/engine/stats           - EngineStats
 *   GET  /api/engine/plan            - current plan or null
 *   GET  /api/engine/history         - paginated event history
 */

import { z } from 'zod';
import type { FastifyInstance, FastifyRequest, FastifyReply } from 'fastify';
import type { TammaEngine, EngineStats } from '@tamma/orchestrator';
import type {
  EngineState,
  IssueData,
  DevelopmentPlan,
  EngineEvent,
} from '@tamma/shared';

// ---------------------------------------------------------------------------
// Zod Schemas
// ---------------------------------------------------------------------------

const EngineCommandSchema = z.discriminatedUnion('type', [
  z.object({ type: z.literal('start') }),
  z.object({ type: z.literal('stop') }),
  z.object({ type: z.literal('pause') }),
  z.object({ type: z.literal('resume') }),
  z.object({ type: z.literal('approve') }),
  z.object({ type: z.literal('reject'), feedback: z.string().optional() }),
  z.object({ type: z.literal('skip') }),
]);

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

export type EngineCommand =
  | { type: 'start' }
  | { type: 'stop' }
  | { type: 'pause' }
  | { type: 'resume' }
  | { type: 'approve' }
  | { type: 'reject'; feedback?: string }
  | { type: 'skip' };

export interface EngineStateSnapshot {
  state: EngineState;
  issue: IssueData | null;
  plan: DevelopmentPlan | null;
  branch: string | null;
  stats: EngineStats;
  timestamp: number;
}

export interface EngineRouteOptions {
  /** The engine instance to bind to these routes. */
  engine: TammaEngine;
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function buildSnapshot(engine: TammaEngine): EngineStateSnapshot {
  return {
    state: engine.getState(),
    issue: engine.getCurrentIssue(),
    plan: engine.getCurrentPlan(),
    branch: engine.getCurrentBranch(),
    stats: engine.getStats(),
    timestamp: Date.now(),
  };
}

function sseHeaders(reply: FastifyReply): void {
  reply.raw.writeHead(200, {
    'Content-Type': 'text/event-stream',
    'Cache-Control': 'no-cache',
    Connection: 'keep-alive',
    'X-Accel-Buffering': 'no',
  });
}

function sendSSE(reply: FastifyReply, event: string, data: unknown): void {
  reply.raw.write(`event: ${event}\ndata: ${JSON.stringify(data)}\n\n`);
}

// ---------------------------------------------------------------------------
// Plugin
// ---------------------------------------------------------------------------

export async function registerEngineRoutes(
  fastify: FastifyInstance,
  opts: EngineRouteOptions,
): Promise<void> {
  const { engine } = opts;

  // ---------- POST /api/engine/command ----------
  fastify.post(
    '/api/engine/command',
    async (
      request: FastifyRequest<{ Body: EngineCommand }>,
      reply: FastifyReply,
    ) => {
      const parsed = EngineCommandSchema.safeParse(request.body);
      if (!parsed.success) {
        return reply.status(400).send({ error: parsed.error.message });
      }
      const cmd = parsed.data;

      switch (cmd.type) {
        case 'start':
          // Fire-and-forget — the engine run loop is long-lived
          engine.run().catch((err) => fastify.log.error(err, 'Engine run failed'));
          return reply.send({ ok: true, type: 'start' });

        case 'stop':
          await engine.dispose();
          return reply.send({ ok: true, type: 'stop' });

        case 'pause':
          // Pause is advisory; the engine checks a running flag each iteration.
          // Do NOT call engine.dispose() here — that tears down resources.
          // TODO: Implement engine.pause() when a proper pause mechanism exists.
          fastify.log.info('Pause requested; engine will pause at next iteration boundary');
          return reply.send({ ok: true, type: 'pause', note: 'Pause is advisory; engine will pause at next iteration boundary' });

        case 'resume':
          engine.run().catch((err) => fastify.log.error(err, 'Engine run failed'));
          return reply.send({ ok: true, type: 'resume' });

        case 'approve':
        case 'reject':
        case 'skip':
          // These need to be wired through the engine transport's approval
          // resolver, which is not yet connected to the HTTP layer.
          return reply.send({ ok: false, type: cmd.type, error: 'Approval commands not yet wired to engine transport' });

        default:
          return reply.status(400).send({ error: `Unknown type: ${(cmd as EngineCommand).type}` });
      }
    },
  );

  // ---------- GET /api/engine/state ----------
  fastify.get('/api/engine/state', async (_request, reply) => {
    const snapshot = buildSnapshot(engine);
    return reply.send(snapshot);
  });

  // ---------- GET /api/engine/events/state ----------
  fastify.get('/api/engine/events/state', async (_request, reply) => {
    reply.hijack();
    sseHeaders(reply);

    // Send current state immediately
    sendSSE(reply, 'state', buildSnapshot(engine));

    // Poll and push state changes every second
    const interval = setInterval(() => {
      try {
        sendSSE(reply, 'state', buildSnapshot(engine));
      } catch {
        clearInterval(interval);
        clearInterval(heartbeat);
      }
    }, 1000);

    // Heartbeat to keep connection alive through reverse proxies
    const heartbeat = setInterval(() => {
      try {
        reply.raw.write(':heartbeat\n\n');
      } catch {
        clearInterval(interval);
        clearInterval(heartbeat);
      }
    }, 15_000);

    // Clean up when the client disconnects
    reply.raw.on('close', () => {
      clearInterval(interval);
      clearInterval(heartbeat);
    });
  });

  // ---------- GET /api/engine/events/logs ----------
  fastify.get('/api/engine/events/logs', async (_request, reply) => {
    reply.hijack();
    sseHeaders(reply);

    // Stream event-store entries as they arrive (poll-based).
    // Use array index instead of timestamps to avoid same-millisecond races.
    let lastSeenIndex = 0;

    const interval = setInterval(() => {
      try {
        const store = engine.getEventStore();
        if (store === undefined) return;

        const events = store.getEvents(engine.getTenantId());
        if (events.length > lastSeenIndex) {
          const newEvents = events.slice(lastSeenIndex);
          for (const evt of newEvents) {
            sendSSE(reply, 'log', evt);
          }
          lastSeenIndex = events.length;
        }
      } catch {
        clearInterval(interval);
        clearInterval(heartbeat);
      }
    }, 500);

    // Heartbeat to keep connection alive through reverse proxies
    const heartbeat = setInterval(() => {
      try {
        reply.raw.write(':heartbeat\n\n');
      } catch {
        clearInterval(interval);
        clearInterval(heartbeat);
      }
    }, 15_000);

    reply.raw.on('close', () => {
      clearInterval(interval);
      clearInterval(heartbeat);
    });
  });

  // ---------- GET /api/engine/stats ----------
  fastify.get('/api/engine/stats', async (_request, reply) => {
    return reply.send(engine.getStats());
  });

  // ---------- GET /api/engine/plan ----------
  fastify.get('/api/engine/plan', async (_request, reply) => {
    const plan = engine.getCurrentPlan();
    return reply.send(plan ?? null);
  });

  // ---------- GET /api/engine/history ----------
  fastify.get(
    '/api/engine/history',
    async (
      request: FastifyRequest<{
        Querystring: { page?: string; pageSize?: string; issueNumber?: string };
      }>,
      reply: FastifyReply,
    ) => {
      const store = engine.getEventStore();
      if (store === undefined) {
        return reply.send({ data: [], total: 0, page: 1, pageSize: 50 });
      }

      const page = Math.max(1, parseInt(request.query.page ?? '1', 10) || 1);
      const pageSize = Math.min(200, Math.max(1, parseInt(request.query.pageSize ?? '50', 10) || 50));
      const issueNumber = request.query.issueNumber
        ? parseInt(request.query.issueNumber, 10)
        : undefined;

      const allEvents: EngineEvent[] = store.getEvents(engine.getTenantId(), issueNumber);
      const total = allEvents.length;
      const start = (page - 1) * pageSize;
      const data = allEvents.slice(start, start + pageSize);

      return reply.send({ data, total, page, pageSize });
    },
  );
}
