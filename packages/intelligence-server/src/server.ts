/**
 * Tamma Intelligence Server
 *
 * Thin Fastify sidecar that exposes @tamma/intelligence + @tamma/mcp-client
 * over HTTP so the C# API can delegate its KB endpoints without a native
 * port. Routes mirror the 30 C# /api/kb/* endpoints 1-to-1 under /kb/*.
 *
 * This module exports a `buildServer` factory (used in tests) and, when run
 * directly, starts the server on the configured port.
 */

import { fileURLToPath } from 'node:url';
import Fastify, { type FastifyInstance } from 'fastify';

import { IndexManagementService } from './services/IndexManagementService.js';
import { VectorDbManagementService } from './services/VectorDbManagementService.js';
import { RagManagementService } from './services/RagManagementService.js';
import { McpManagementService } from './services/McpManagementService.js';
import { ContextTestingService } from './services/ContextTestingService.js';
import { AnalyticsService } from './services/AnalyticsService.js';
import type { IntelligenceServicesBundle } from './types.js';

export interface BuildServerOptions {
  /**
   * Optional Fastify logger configuration. Accepts the same values Fastify
   * does: `true`, `false`, or a pino-compatible options object.
   */
  logger?: boolean | Record<string, unknown>;
  services?: IntelligenceServicesBundle;
}

interface RegisteredServices {
  index: IndexManagementService;
  vectorDb: VectorDbManagementService;
  rag: RagManagementService;
  mcp: McpManagementService;
  context: ContextTestingService;
  analytics: AnalyticsService;
}

function buildServices(bundle?: IntelligenceServicesBundle): RegisteredServices {
  return {
    index: new IndexManagementService(bundle?.indexer),
    vectorDb: new VectorDbManagementService(bundle?.vectorStore),
    rag: new RagManagementService(bundle?.ragPipeline),
    mcp: new McpManagementService(bundle?.mcpClient),
    context: new ContextTestingService(bundle?.contextAggregator),
    analytics: new AnalyticsService(bundle?.costTracker),
  };
}

/**
 * Register the 30 KB HTTP routes on a Fastify instance.
 * Exported so tests can build a server with custom mocks.
 */
export function registerKbRoutes(app: FastifyInstance, services: RegisteredServices): void {
  // ── Health ──
  app.get('/health', async () => ({ status: 'ok' }));

  // ── Index (6) ──
  app.get('/kb/index/status', async () => services.index.getStatus());
  app.post('/kb/index/trigger', async (req, reply) => {
    try {
      const body = (req.body ?? {}) as Parameters<
        IndexManagementService['triggerIndex']
      >[0];
      return await services.index.triggerIndex(body);
    } catch (err) {
      return reply
        .status(409)
        .send({ error: err instanceof Error ? err.message : 'Unknown error' });
    }
  });
  app.get('/kb/index/config', async () => services.index.getConfig());
  app.put('/kb/index/config', async (req) => {
    const body = (req.body ?? {}) as Parameters<
      IndexManagementService['updateConfig']
    >[0];
    return services.index.updateConfig(body);
  });
  app.get('/kb/index/stats', async () => services.index.getStats());
  app.delete('/kb/index', async () => services.index.clear());

  // ── Vector DB (6) ──
  app.get('/kb/vector-db/status', async () => services.vectorDb.getStatus());
  app.post('/kb/vector-db/search', async (req) => {
    const body = (req.body ?? {}) as Parameters<
      VectorDbManagementService['search']
    >[0];
    return services.vectorDb.search(body);
  });
  app.post('/kb/vector-db/upsert', async (req) => {
    const body = (req.body ?? {}) as Parameters<
      VectorDbManagementService['upsert']
    >[0];
    return services.vectorDb.upsert(body);
  });
  app.delete('/kb/vector-db/delete', async (req) => {
    const body = (req.body ?? {}) as Parameters<
      VectorDbManagementService['delete']
    >[0];
    return services.vectorDb.delete(body);
  });
  app.get('/kb/vector-db/collections', async () => services.vectorDb.listCollections());
  app.get('/kb/vector-db/stats', async () => services.vectorDb.getStats());

  // ── RAG (4) ──
  app.get('/kb/rag/config', async () => services.rag.getConfig());
  app.put('/kb/rag/config', async (req) => {
    const body = (req.body ?? {}) as Parameters<RagManagementService['updateConfig']>[0];
    return services.rag.updateConfig(body);
  });
  app.post('/kb/rag/query', async (req) => {
    const body = (req.body ?? {}) as Parameters<RagManagementService['query']>[0];
    return services.rag.query(body);
  });
  app.get('/kb/rag/metrics', async () => services.rag.getMetrics());

  // ── MCP (8) ──
  app.get('/kb/mcp/servers', async () => services.mcp.listServers());
  app.get('/kb/mcp/servers/:id', async (req) => {
    const { id } = req.params as { id: string };
    return services.mcp.getServer(id);
  });
  app.post('/kb/mcp/servers/:id/start', async (req) => {
    const { id } = req.params as { id: string };
    return services.mcp.startServer(id);
  });
  app.post('/kb/mcp/servers/:id/stop', async (req) => {
    const { id } = req.params as { id: string };
    return services.mcp.stopServer(id);
  });
  app.get('/kb/mcp/config', async () => services.mcp.getConfig());
  app.put('/kb/mcp/config', async (req) => {
    const body = (req.body ?? {}) as Parameters<McpManagementService['updateConfig']>[0];
    return services.mcp.updateConfig(body);
  });
  app.get('/kb/mcp/tools', async (req) => {
    const query = req.query as { serverName?: string };
    return services.mcp.listTools(query.serverName);
  });
  app.post('/kb/mcp/tools/invoke', async (req) => {
    const body = (req.body ?? {}) as Parameters<McpManagementService['invokeTool']>[0];
    return services.mcp.invokeTool(body);
  });

  // ── Context (3) ──
  app.get('/kb/context/history', async (req) => {
    const q = req.query as { limit?: string };
    const limit = q.limit ? Number.parseInt(q.limit, 10) : 50;
    return services.context.getHistory(Number.isFinite(limit) ? limit : 50);
  });
  app.post('/kb/context/feedback', async (req) => {
    const body = (req.body ?? {}) as Parameters<
      ContextTestingService['submitFeedback']
    >[0];
    return services.context.submitFeedback(body);
  });
  app.get('/kb/context/config', async () => services.context.getConfig());

  // ── Analytics (3) ──
  app.get('/kb/analytics', async (req) => {
    const q = req.query as { start?: string; end?: string };
    const period: { start?: string; end?: string } = {};
    if (q.start !== undefined) period.start = q.start;
    if (q.end !== undefined) period.end = q.end;
    return services.analytics.getAnalytics(period);
  });
  app.get('/kb/analytics/usage', async (req) => {
    const q = req.query as { start?: string; end?: string };
    const period: { start?: string; end?: string } = {};
    if (q.start !== undefined) period.start = q.start;
    if (q.end !== undefined) period.end = q.end;
    return services.analytics.getUsage(period);
  });
  app.get('/kb/analytics/costs', async (req) => {
    const q = req.query as { start?: string; end?: string };
    const period: { start?: string; end?: string } = {};
    if (q.start !== undefined) period.start = q.start;
    if (q.end !== undefined) period.end = q.end;
    return services.analytics.getCosts(period);
  });
}

export async function buildServer(opts: BuildServerOptions = {}): Promise<FastifyInstance> {
  const loggerOpt =
    opts.logger ?? {
      name: 'intelligence-server',
      level: process.env['LOG_LEVEL'] ?? 'info',
    };
  const app = Fastify({ logger: loggerOpt });
  const services = buildServices(opts.services);
  registerKbRoutes(app, services);
  return app;
}

export async function startServer(opts: {
  port?: number;
  host?: string;
  services?: IntelligenceServicesBundle;
} = {}): Promise<FastifyInstance> {
  const app = await buildServer({ ...(opts.services ? { services: opts.services } : {}) });
  const port = opts.port ?? Number.parseInt(process.env['INTELLIGENCE_PORT'] ?? '4100', 10);
  const host = opts.host ?? process.env['INTELLIGENCE_HOST'] ?? '0.0.0.0';
  await app.listen({ port, host });
  return app;
}

// Entry point: only run when invoked directly (not when imported by tests).
if (import.meta.url === `file://${process.argv[1]}` || process.argv[1] === fileURLToPath(import.meta.url)) {
  startServer().catch((err) => {
    // eslint-disable-next-line no-console
    console.error('Failed to start intelligence-server:', err);
    process.exit(1);
  });
}
