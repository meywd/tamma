/**
 * Route-level tests for the intelligence-server sidecar.
 *
 * Each test uses Fastify's `app.inject()` (no real network socket) and a
 * mocked IntelligenceServicesBundle. The goal is to confirm every one of
 * the 30 C#-mirrored routes is wired and returns the shape we expect
 * downstream C# clients to rely on.
 */

import { describe, expect, it, vi } from 'vitest';

import { buildServer } from '../server.js';
import type {
  IContextAggregatorAdapter,
  ICostTrackerAdapter,
  IIndexer,
  IMcpClient,
  IRagPipeline,
  IVectorStoreAdapter,
  IntelligenceServicesBundle,
} from '../types.js';

const mockIndexer: IIndexer = {
  indexProject: vi.fn().mockResolvedValue(undefined),
  updateIndex: vi.fn().mockResolvedValue(undefined),
  stop: vi.fn().mockResolvedValue(undefined),
  getIndexStatus: vi.fn().mockResolvedValue({
    status: 'idle',
    filesIndexed: 42,
    chunksCreated: 100,
    lastIndexedAt: '2026-01-02T03:04:05.678Z',
  }),
  configure: vi.fn(),
};

const mockVectorStore: IVectorStoreAdapter = {
  listCollections: vi.fn().mockResolvedValue(['codebase', 'docs']),
  getCollectionStats: vi
    .fn()
    .mockResolvedValue({ vectorCount: 10, dimensions: 1536, storageBytes: 1024 }),
  createCollection: vi.fn().mockResolvedValue(undefined),
  deleteCollection: vi.fn().mockResolvedValue(undefined),
  upsert: vi.fn().mockResolvedValue(undefined),
  delete: vi.fn().mockResolvedValue(undefined),
  search: vi.fn().mockResolvedValue([
    { id: 'doc1', score: 0.95, content: 'hello world', metadata: { path: 'src/hello.ts' } },
  ]),
  hybridSearch: vi.fn().mockResolvedValue([
    { id: 'doc1', score: 0.95, content: 'hello world', metadata: { path: 'src/hello.ts' } },
  ]),
};

const mockRag: IRagPipeline = {
  retrieve: vi.fn().mockResolvedValue({
    queryId: 'q1',
    retrievedChunks: [
      { content: 'chunk-a', source: 'vectorDb', score: 0.9, metadata: { file: 'a.ts' } },
    ],
    cacheHit: false,
    latencyMs: 50,
  }),
  getCacheStats: vi.fn().mockReturnValue({ hits: 3, misses: 1, size: 4 }),
  getFeedbackOverview: vi.fn().mockReturnValue({ totalFeedback: 0, averageRelevance: 0 }),
  configure: vi.fn(),
};

const mockMcp: IMcpClient = {
  listServers: vi.fn().mockReturnValue([
    { name: 'github', status: 'connected', transport: 'stdio', url: 'ipc://github' },
    { name: 'files', status: 'disconnected', transport: 'stdio' },
  ]),
  connectServer: vi.fn().mockResolvedValue(undefined),
  disconnectServer: vi.fn().mockResolvedValue(undefined),
  listTools: vi.fn().mockResolvedValue([
    { name: 'read_file', description: 'Read a file', inputSchema: { type: 'object' } },
  ]),
  invokeTool: vi.fn().mockResolvedValue({ success: true, content: 'ok' }),
  getServerLogs: vi.fn().mockReturnValue([]),
};

const mockAggregator: IContextAggregatorAdapter = {
  getContext: vi.fn().mockResolvedValue({
    requestId: 'ctx-1',
    context: { text: 't', chunks: [], tokenCount: 0 },
    sources: [],
    metrics: { totalLatencyMs: 0, totalTokens: 0 },
  }),
};

const mockCostTracker: ICostTrackerAdapter = {
  getUsage: vi.fn().mockResolvedValue([
    { provider: 'openai', model: 'text-embedding-3-small', tokens: 100, cost: 0.0001 },
  ]),
  getTotalCost: vi.fn().mockResolvedValue(0.0001),
  getAggregate: vi.fn().mockResolvedValue({
    byProvider: { openai: 0.0001 },
    byModel: { 'text-embedding-3-small': 0.0001 },
  }),
};

const fullBundle: IntelligenceServicesBundle = {
  indexer: mockIndexer,
  vectorStore: mockVectorStore,
  ragPipeline: mockRag,
  mcpClient: mockMcp,
  contextAggregator: mockAggregator,
  costTracker: mockCostTracker,
};

async function makeApp(bundle: IntelligenceServicesBundle = fullBundle) {
  const app = await buildServer({ services: bundle });
  await app.ready();
  return app;
}

describe('intelligence-server — health', () => {
  it('GET /health returns ok', async () => {
    const app = await makeApp();
    const res = await app.inject({ method: 'GET', url: '/health' });
    expect(res.statusCode).toBe(200);
    expect(res.json()).toEqual({ status: 'ok' });
    await app.close();
  });
});

describe('intelligence-server — index routes (6)', () => {
  it('GET /kb/index/status returns live status when indexer is configured', async () => {
    const app = await makeApp();
    const res = await app.inject({ method: 'GET', url: '/kb/index/status' });
    expect(res.statusCode).toBe(200);
    const body = res.json();
    expect(body.status).toBe('idle');
    expect(body.indexed).toBe(42);
    expect(body.lastRun).toBe('2026-01-02T03:04:05.678Z');
    await app.close();
  });

  it('POST /kb/index/trigger returns 200 and launches the indexer', async () => {
    const app = await makeApp();
    const res = await app.inject({
      method: 'POST',
      url: '/kb/index/trigger',
      payload: { fullReindex: true, repositoryPath: '/tmp/repo' },
    });
    expect(res.statusCode).toBe(200);
    expect(res.json().message).toContain('Indexing triggered');
    expect(mockIndexer.indexProject).toHaveBeenCalled();
    await app.close();
  });

  it('GET /kb/index/config includes defaults', async () => {
    const app = await makeApp();
    const res = await app.inject({ method: 'GET', url: '/kb/index/config' });
    expect(res.statusCode).toBe(200);
    const body = res.json();
    expect(body.configured).toBe(true);
    expect(body.chunkingConfig.maxTokens).toBeGreaterThan(0);
    await app.close();
  });

  it('PUT /kb/index/config merges patch and calls indexer.configure', async () => {
    const app = await makeApp();
    const res = await app.inject({
      method: 'PUT',
      url: '/kb/index/config',
      payload: { includePatterns: ['**/*.rs'] },
    });
    expect(res.statusCode).toBe(200);
    expect(res.json().includePatterns).toContain('**/*.rs');
    expect(mockIndexer.configure).toHaveBeenCalled();
    await app.close();
  });

  it('GET /kb/index/stats returns document + chunk counts', async () => {
    const app = await makeApp();
    const res = await app.inject({ method: 'GET', url: '/kb/index/stats' });
    expect(res.statusCode).toBe(200);
    const body = res.json();
    expect(body.documents).toBe(42);
    expect(body.chunks).toBe(100);
    await app.close();
  });

  it('DELETE /kb/index clears the index', async () => {
    const app = await makeApp();
    const res = await app.inject({ method: 'DELETE', url: '/kb/index' });
    expect(res.statusCode).toBe(200);
    expect(res.json().message).toMatch(/cleared/i);
    await app.close();
  });
});

describe('intelligence-server — vector-db routes (6)', () => {
  it('GET /kb/vector-db/status reports ready when store is wired', async () => {
    const app = await makeApp();
    const res = await app.inject({ method: 'GET', url: '/kb/vector-db/status' });
    expect(res.statusCode).toBe(200);
    expect(res.json().status).toBe('ready');
    await app.close();
  });

  it('POST /kb/vector-db/search delegates to hybridSearch', async () => {
    const app = await makeApp();
    const res = await app.inject({
      method: 'POST',
      url: '/kb/vector-db/search',
      payload: { collection: 'codebase', query: 'hello', topK: 5 },
    });
    expect(res.statusCode).toBe(200);
    expect(res.json().results).toHaveLength(1);
    expect(mockVectorStore.hybridSearch).toHaveBeenCalled();
    await app.close();
  });

  it('POST /kb/vector-db/upsert reports count', async () => {
    const app = await makeApp();
    const res = await app.inject({
      method: 'POST',
      url: '/kb/vector-db/upsert',
      payload: {
        collection: 'codebase',
        documents: [
          { id: 'd1', embedding: [0.1, 0.2], content: 'hi' },
          { id: 'd2', embedding: [0.3, 0.4], content: 'bye' },
        ],
      },
    });
    expect(res.statusCode).toBe(200);
    expect(res.json().count).toBe(2);
    await app.close();
  });

  it('DELETE /kb/vector-db/delete removes by ids', async () => {
    const app = await makeApp();
    const res = await app.inject({
      method: 'DELETE',
      url: '/kb/vector-db/delete',
      payload: { collection: 'codebase', ids: ['d1'] },
    });
    expect(res.statusCode).toBe(200);
    expect(mockVectorStore.delete).toHaveBeenCalledWith('codebase', ['d1']);
    await app.close();
  });

  it('GET /kb/vector-db/collections lists them with stats', async () => {
    const app = await makeApp();
    const res = await app.inject({ method: 'GET', url: '/kb/vector-db/collections' });
    expect(res.statusCode).toBe(200);
    expect(res.json()).toHaveLength(2);
    expect(res.json()[0].name).toBe('codebase');
    await app.close();
  });

  it('GET /kb/vector-db/stats aggregates across collections', async () => {
    const app = await makeApp();
    const res = await app.inject({ method: 'GET', url: '/kb/vector-db/stats' });
    expect(res.statusCode).toBe(200);
    const body = res.json();
    expect(body.totalVectors).toBe(20); // 2 collections x 10 vectors each
    expect(body.dimensions).toBe(1536);
    await app.close();
  });
});

describe('intelligence-server — rag routes (4)', () => {
  it('GET /kb/rag/config returns defaults with enabled=true when pipeline wired', async () => {
    const app = await makeApp();
    const res = await app.inject({ method: 'GET', url: '/kb/rag/config' });
    expect(res.statusCode).toBe(200);
    expect(res.json().enabled).toBe(true);
    await app.close();
  });

  it('PUT /kb/rag/config merges patch', async () => {
    const app = await makeApp();
    const res = await app.inject({
      method: 'PUT',
      url: '/kb/rag/config',
      payload: { ranking: { mmrLambda: 0.3 } },
    });
    expect(res.statusCode).toBe(200);
    expect(res.json().ranking.mmrLambda).toBe(0.3);
    expect(mockRag.configure).toHaveBeenCalled();
    await app.close();
  });

  it('POST /kb/rag/query returns retrieved chunks', async () => {
    const app = await makeApp();
    const res = await app.inject({
      method: 'POST',
      url: '/kb/rag/query',
      payload: { query: 'how to add a provider', topK: 3 },
    });
    expect(res.statusCode).toBe(200);
    const body = res.json();
    expect(body.queryId).toBe('q1');
    expect(body.sources).toHaveLength(1);
    await app.close();
  });

  it('GET /kb/rag/metrics returns computed cache hit rate', async () => {
    const app = await makeApp();
    // Make one query so queryCount > 0
    await app.inject({
      method: 'POST',
      url: '/kb/rag/query',
      payload: { query: 'x' },
    });
    const res = await app.inject({ method: 'GET', url: '/kb/rag/metrics' });
    expect(res.statusCode).toBe(200);
    const body = res.json();
    expect(body.queries).toBeGreaterThanOrEqual(1);
    expect(body.cacheHitRate).toBeCloseTo(0.75, 2);
    await app.close();
  });
});

describe('intelligence-server — mcp routes (8)', () => {
  it('GET /kb/mcp/servers lists configured servers', async () => {
    const app = await makeApp();
    const res = await app.inject({ method: 'GET', url: '/kb/mcp/servers' });
    expect(res.statusCode).toBe(200);
    expect(res.json()).toHaveLength(2);
    expect(res.json()[0].name).toBe('github');
    await app.close();
  });

  it('GET /kb/mcp/servers/:id returns one server', async () => {
    const app = await makeApp();
    const res = await app.inject({ method: 'GET', url: '/kb/mcp/servers/github' });
    expect(res.statusCode).toBe(200);
    expect(res.json().name).toBe('github');
    expect(res.json().status).toBe('connected');
    await app.close();
  });

  it('POST /kb/mcp/servers/:id/start connects the server', async () => {
    const app = await makeApp();
    const res = await app.inject({
      method: 'POST',
      url: '/kb/mcp/servers/github/start',
    });
    expect(res.statusCode).toBe(200);
    expect(mockMcp.connectServer).toHaveBeenCalledWith('github');
    await app.close();
  });

  it('POST /kb/mcp/servers/:id/stop disconnects the server', async () => {
    const app = await makeApp();
    const res = await app.inject({
      method: 'POST',
      url: '/kb/mcp/servers/files/stop',
    });
    expect(res.statusCode).toBe(200);
    expect(mockMcp.disconnectServer).toHaveBeenCalledWith('files');
    await app.close();
  });

  it('GET /kb/mcp/config returns servers from the client', async () => {
    const app = await makeApp();
    const res = await app.inject({ method: 'GET', url: '/kb/mcp/config' });
    expect(res.statusCode).toBe(200);
    expect(res.json().servers).toHaveLength(2);
    await app.close();
  });

  it('PUT /kb/mcp/config records supplied servers', async () => {
    const app = await makeApp();
    const res = await app.inject({
      method: 'PUT',
      url: '/kb/mcp/config',
      payload: { servers: [{ name: 'x', transport: 'stdio', enabled: true }] },
    });
    expect(res.statusCode).toBe(200);
    expect(res.json().servers[0].name).toBe('x');
    await app.close();
  });

  it('GET /kb/mcp/tools lists tools', async () => {
    const app = await makeApp();
    const res = await app.inject({
      method: 'GET',
      url: '/kb/mcp/tools?serverName=github',
    });
    expect(res.statusCode).toBe(200);
    expect(res.json()[0].name).toBe('read_file');
    await app.close();
  });

  it('POST /kb/mcp/tools/invoke runs a tool', async () => {
    const app = await makeApp();
    const res = await app.inject({
      method: 'POST',
      url: '/kb/mcp/tools/invoke',
      payload: { serverName: 'github', toolName: 'read_file', arguments: { path: 'README.md' } },
    });
    expect(res.statusCode).toBe(200);
    expect(res.json().success).toBe(true);
    expect(mockMcp.invokeTool).toHaveBeenCalledWith(
      'github',
      'read_file',
      { path: 'README.md' },
    );
    await app.close();
  });
});

describe('intelligence-server — context routes (3)', () => {
  it('GET /kb/context/history returns an empty history by default', async () => {
    const app = await makeApp();
    const res = await app.inject({ method: 'GET', url: '/kb/context/history' });
    expect(res.statusCode).toBe(200);
    expect(res.json()).toEqual({ history: [] });
    await app.close();
  });

  it('POST /kb/context/feedback stores feedback', async () => {
    const app = await makeApp();
    const res = await app.inject({
      method: 'POST',
      url: '/kb/context/feedback',
      payload: { requestId: 'r1', helpful: true },
    });
    expect(res.statusCode).toBe(200);
    expect(res.json().message).toMatch(/recorded/i);
    await app.close();
  });

  it('GET /kb/context/config returns config defaults', async () => {
    const app = await makeApp();
    const res = await app.inject({ method: 'GET', url: '/kb/context/config' });
    expect(res.statusCode).toBe(200);
    expect(res.json().maxTokens).toBeGreaterThan(0);
    expect(res.json().strategy).toBeDefined();
    await app.close();
  });
});

describe('intelligence-server — analytics routes (3)', () => {
  it('GET /kb/analytics returns aggregate numbers', async () => {
    const app = await makeApp();
    const res = await app.inject({ method: 'GET', url: '/kb/analytics' });
    expect(res.statusCode).toBe(200);
    const body = res.json();
    expect(body.totalTokens).toBe(100);
    expect(body.queries).toBe(1);
    await app.close();
  });

  it('GET /kb/analytics/usage returns daily buckets', async () => {
    const app = await makeApp();
    const res = await app.inject({
      method: 'GET',
      url: '/kb/analytics/usage?start=2026-01-01T00:00:00.000Z&end=2026-01-02T00:00:00.000Z',
    });
    expect(res.statusCode).toBe(200);
    expect(res.json().daily).toHaveLength(1);
    await app.close();
  });

  it('GET /kb/analytics/costs returns breakdown with percent', async () => {
    const app = await makeApp();
    const res = await app.inject({ method: 'GET', url: '/kb/analytics/costs' });
    expect(res.statusCode).toBe(200);
    const body = res.json();
    expect(body.totalCost).toBeGreaterThan(0);
    expect(body.breakdown).toHaveLength(1);
    expect(body.breakdown[0].percent).toBeCloseTo(100, 0);
    await app.close();
  });
});

describe('intelligence-server — no-deps fallback', () => {
  it('returns stub-like empty responses when no services are wired', async () => {
    const app = await buildServer({ services: {} });
    await app.ready();

    const status = await app.inject({ method: 'GET', url: '/kb/vector-db/status' });
    expect(status.json().status).toBe('not_configured');

    const servers = await app.inject({ method: 'GET', url: '/kb/mcp/servers' });
    expect(servers.json()).toEqual([]);

    const analytics = await app.inject({ method: 'GET', url: '/kb/analytics' });
    expect(analytics.json().queries).toBe(0);

    await app.close();
  });
});
