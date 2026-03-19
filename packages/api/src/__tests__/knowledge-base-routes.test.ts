/**
 * Knowledge Base API Routes Tests
 *
 * Integration tests for all Knowledge Base management API endpoints.
 * Services are created without real dependencies, so they return
 * empty/zero state (graceful degradation).
 */

import { describe, it, expect, beforeAll, afterAll } from 'vitest';
import Fastify from 'fastify';
import type { FastifyInstance } from 'fastify';
import { registerKnowledgeBaseRoutes, createKBServices } from '../routes/knowledge-base/index.js';

describe('Knowledge Base API Routes', () => {
  let app: FastifyInstance;

  beforeAll(async () => {
    app = Fastify({ logger: false });
    const services = createKBServices();
    await registerKnowledgeBaseRoutes(app, services);
    await app.ready();
  });

  afterAll(async () => {
    await app.close();
  });

  // === Index Management ===

  describe('Index Management', () => {
    it('GET /api/knowledge-base/index/status returns current status', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/knowledge-base/index/status',
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.status).toBe('idle');
      expect(typeof body.filesIndexed).toBe('number');
      expect(body.filesIndexed).toBe(0);
      expect(typeof body.chunksCreated).toBe('number');
    });

    it('POST /api/knowledge-base/index/trigger returns 409 without indexer', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/knowledge-base/index/trigger',
        payload: {},
      });

      // Without a real indexer, the service throws "No indexer or project path configured"
      expect(response.statusCode).toBe(409);
    });

    it('DELETE /api/knowledge-base/index/cancel returns 409 when not indexing', async () => {
      const response = await app.inject({
        method: 'DELETE',
        url: '/api/knowledge-base/index/cancel',
      });

      expect(response.statusCode).toBe(409);
    });

    it('GET /api/knowledge-base/index/history returns empty history', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/knowledge-base/index/history',
      });

      expect(response.statusCode).toBe(200);
      expect(response.json()).toEqual([]);
    });

    it('GET /api/knowledge-base/index/config returns configuration', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/knowledge-base/index/config',
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.includePatterns).toBeDefined();
      expect(body.excludePatterns).toBeDefined();
      expect(body.chunkingConfig).toBeDefined();
    });

    it('PUT /api/knowledge-base/index/config updates configuration', async () => {
      const response = await app.inject({
        method: 'PUT',
        url: '/api/knowledge-base/index/config',
        payload: {
          includePatterns: ['**/*.ts'],
          chunkingConfig: { maxTokens: 1000 },
        },
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.includePatterns).toContain('**/*.ts');
    });
  });

  // === Vector Database ===

  describe('Vector Database', () => {
    it('GET /api/knowledge-base/vector-db/collections returns empty list without store', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/knowledge-base/vector-db/collections',
      });

      expect(response.statusCode).toBe(200);
      const collections = response.json();
      expect(Array.isArray(collections)).toBe(true);
      expect(collections.length).toBe(0);
    });

    it('GET /api/knowledge-base/vector-db/collections/:name/stats returns 404 without store', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/knowledge-base/vector-db/collections/nonexistent/stats',
      });

      expect(response.statusCode).toBe(404);
    });

    it('GET /api/knowledge-base/vector-db/storage returns zero storage without store', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/knowledge-base/vector-db/storage',
      });

      expect(response.statusCode).toBe(200);
      const usage = response.json();
      expect(typeof usage.totalBytes).toBe('number');
      expect(usage.totalBytes).toBe(0);
      expect(usage.byCollection).toBeDefined();
    });
  });

  // === RAG Pipeline ===

  describe('RAG Pipeline', () => {
    it('GET /api/knowledge-base/rag/config returns configuration', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/knowledge-base/rag/config',
      });

      expect(response.statusCode).toBe(200);
      const config = response.json();
      expect(config.sources).toBeDefined();
      expect(config.ranking).toBeDefined();
      expect(config.assembly).toBeDefined();
      expect(config.caching).toBeDefined();
    });

    it('PUT /api/knowledge-base/rag/config updates configuration', async () => {
      const response = await app.inject({
        method: 'PUT',
        url: '/api/knowledge-base/rag/config',
        payload: { assembly: { maxTokens: 8000 } },
      });

      expect(response.statusCode).toBe(200);
    });

    it('GET /api/knowledge-base/rag/metrics returns zero metrics without pipeline', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/knowledge-base/rag/metrics',
      });

      expect(response.statusCode).toBe(200);
      const metrics = response.json();
      expect(typeof metrics.totalQueries).toBe('number');
      expect(metrics.totalQueries).toBe(0);
    });

    it('POST /api/knowledge-base/rag/test returns 500 without pipeline', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/knowledge-base/rag/test',
        payload: { query: 'How does authentication work?', topK: 5 },
      });

      expect(response.statusCode).toBeGreaterThanOrEqual(400);
    });
  });

  // === MCP Servers ===

  describe('MCP Servers', () => {
    it('GET /api/knowledge-base/mcp/servers returns empty list without client', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/knowledge-base/mcp/servers',
      });

      expect(response.statusCode).toBe(200);
      const servers = response.json();
      expect(Array.isArray(servers)).toBe(true);
      expect(servers.length).toBe(0);
    });

    it('GET /api/knowledge-base/mcp/servers/:name returns 404 for unknown', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/knowledge-base/mcp/servers/nonexistent',
      });

      expect(response.statusCode).toBe(404);
    });
  });

  // === Context Testing ===

  describe('Context Testing', () => {
    it('POST /api/knowledge-base/context/test returns error without aggregator', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/knowledge-base/context/test',
        payload: {
          query: 'How does the authentication flow work?',
          taskType: 'implementation',
          maxTokens: 4000,
          sources: ['vector_db', 'rag'],
        },
      });

      expect(response.statusCode).toBeGreaterThanOrEqual(400);
    });

    it('POST /api/knowledge-base/context/feedback submits feedback', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/knowledge-base/context/feedback',
        payload: {
          requestId: 'test-id',
          feedback: [{ chunkId: 'chunk-1', rating: 'relevant' }],
        },
      });

      expect(response.statusCode).toBe(200);
    });

    it('GET /api/knowledge-base/context/history returns empty history without aggregator', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/knowledge-base/context/history?limit=5',
      });

      expect(response.statusCode).toBe(200);
      const history = response.json();
      expect(Array.isArray(history)).toBe(true);
    });
  });

  // === Analytics ===

  describe('Analytics', () => {
    it('GET /api/knowledge-base/analytics/usage returns zero analytics without cost tracker', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/knowledge-base/analytics/usage',
      });

      expect(response.statusCode).toBe(200);
      const analytics = response.json();
      expect(analytics.period).toBeDefined();
      expect(typeof analytics.totalQueries).toBe('number');
      expect(analytics.totalQueries).toBe(0);
    });

    it('GET /api/knowledge-base/analytics/quality returns zero quality metrics', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/knowledge-base/analytics/quality',
      });

      expect(response.statusCode).toBe(200);
      const analytics = response.json();
      expect(typeof analytics.relevanceRate).toBe('number');
      expect(analytics.relevanceRate).toBe(0);
    });

    it('GET /api/knowledge-base/analytics/costs returns zero cost breakdown', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/knowledge-base/analytics/costs',
      });

      expect(response.statusCode).toBe(200);
      const analytics = response.json();
      expect(typeof analytics.totalCostUsd).toBe('number');
      expect(analytics.totalCostUsd).toBe(0);
      expect(Array.isArray(analytics.breakdown)).toBe(true);
    });
  });
});
