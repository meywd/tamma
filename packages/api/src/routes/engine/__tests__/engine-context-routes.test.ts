/**
 * Engine Context Route Tests
 *
 * Tests the context storage and retrieval endpoints:
 *   POST /api/engine/store-context
 *   GET  /api/engine/context/:issueNumber
 *   POST /api/engine/query-context
 *
 * Story 6-11: Context API Wiring
 */

import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import Fastify from 'fastify';
import type { FastifyInstance } from 'fastify';
import { registerEngineContextRoutes } from '../engine-context-routes.js';

describe('Engine Context Routes', () => {
  let app: FastifyInstance;

  beforeEach(async () => {
    app = Fastify({ logger: false });
    await registerEngineContextRoutes(app);
    await app.ready();
  });

  afterEach(async () => {
    await app.close();
  });

  // -----------------------------------------------------------------------
  // POST /api/engine/store-context
  // -----------------------------------------------------------------------

  describe('POST /api/engine/store-context', () => {
    it('stores context and returns contextIds', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/store-context',
        payload: {
          repository: 'owner/repo',
          issueNumber: 42,
          findings: {
            dev: 'Developer analysis findings',
            qa: { tests: ['unit', 'integration'], coverage: 85 },
            security: 'No critical vulnerabilities found',
          },
        },
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.contextIds).toHaveLength(3);
      expect(body.storedAt).toBeDefined();
      expect(typeof body.storedAt).toBe('string');
    });

    it('rejects missing repository (400)', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/store-context',
        payload: {
          issueNumber: 42,
          findings: { dev: 'test' },
        },
      });

      expect(response.statusCode).toBe(400);
    });

    it('rejects missing findings (400)', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/store-context',
        payload: {
          repository: 'owner/repo',
          issueNumber: 42,
        },
      });

      expect(response.statusCode).toBe(400);
    });

    it('rejects empty findings (400)', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/store-context',
        payload: {
          repository: 'owner/repo',
          issueNumber: 42,
          findings: {},
        },
      });

      expect(response.statusCode).toBe(400);
    });

    it('rejects negative issueNumber (400)', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/store-context',
        payload: {
          repository: 'owner/repo',
          issueNumber: -1,
          findings: { dev: 'test' },
        },
      });

      expect(response.statusCode).toBe(400);
    });

    it('overwrites existing context for same repo+issue', async () => {
      // Store first context
      await app.inject({
        method: 'POST',
        url: '/api/engine/store-context',
        payload: {
          repository: 'owner/repo',
          issueNumber: 10,
          findings: { dev: 'first' },
        },
      });

      // Store second context for same issue
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/store-context',
        payload: {
          repository: 'owner/repo',
          issueNumber: 10,
          findings: { dev: 'second', security: 'new' },
        },
      });

      expect(response.statusCode).toBe(200);
      expect(response.json().contextIds).toHaveLength(2);

      // Retrieve to verify overwrite
      const getResponse = await app.inject({
        method: 'GET',
        url: '/api/engine/context/10?repository=owner/repo',
      });

      expect(getResponse.json().findings.dev).toBe('second');
    });
  });

  // -----------------------------------------------------------------------
  // GET /api/engine/context/:issueNumber
  // -----------------------------------------------------------------------

  describe('GET /api/engine/context/:issueNumber', () => {
    it('retrieves stored context by exact repo+issue', async () => {
      // Store context first
      await app.inject({
        method: 'POST',
        url: '/api/engine/store-context',
        payload: {
          repository: 'owner/repo',
          issueNumber: 99,
          findings: { dev: 'analysis result' },
        },
      });

      const response = await app.inject({
        method: 'GET',
        url: '/api/engine/context/99?repository=owner/repo',
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.findings.dev).toBe('analysis result');
      expect(body.contextIds).toHaveLength(1);
      expect(body.storedAt).toBeDefined();
    });

    it('retrieves context by issueNumber scan (no repository param)', async () => {
      await app.inject({
        method: 'POST',
        url: '/api/engine/store-context',
        payload: {
          repository: 'owner/repo',
          issueNumber: 77,
          findings: { qa: 'quality check' },
        },
      });

      const response = await app.inject({
        method: 'GET',
        url: '/api/engine/context/77',
      });

      expect(response.statusCode).toBe(200);
      expect(response.json().findings.qa).toBe('quality check');
    });

    it('returns 404 when context does not exist', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/engine/context/999?repository=owner/repo',
      });

      expect(response.statusCode).toBe(404);
    });

    it('returns 400 for invalid issueNumber', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/engine/context/abc',
      });

      expect(response.statusCode).toBe(400);
    });
  });

  // -----------------------------------------------------------------------
  // POST /api/engine/query-context
  // -----------------------------------------------------------------------

  describe('POST /api/engine/query-context', () => {
    it('returns relevant chunks from stored context', async () => {
      // Store context
      await app.inject({
        method: 'POST',
        url: '/api/engine/store-context',
        payload: {
          repository: 'owner/repo',
          issueNumber: 50,
          findings: {
            dev: 'Authentication flow uses JWT tokens with refresh mechanism',
            security: 'Rate limiting is properly configured on all API endpoints',
            qa: 'All edge cases for login flow are covered by integration tests',
          },
        },
      });

      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/query-context',
        payload: {
          repository: 'owner/repo',
          issueNumber: 50,
          query: 'authentication JWT',
        },
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.chunks).toBeInstanceOf(Array);
      expect(body.chunks.length).toBeGreaterThan(0);
      expect(body.totalTokens).toBeGreaterThan(0);

      // The dev finding about JWT should score highest
      expect(body.chunks[0].role).toBe('dev');
    });

    it('filters by role when specified', async () => {
      await app.inject({
        method: 'POST',
        url: '/api/engine/store-context',
        payload: {
          repository: 'owner/repo',
          issueNumber: 51,
          findings: {
            dev: 'Developer findings here',
            security: 'Security findings here',
          },
        },
      });

      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/query-context',
        payload: {
          repository: 'owner/repo',
          issueNumber: 51,
          query: 'findings',
          role: 'security',
        },
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.chunks).toHaveLength(1);
      expect(body.chunks[0].role).toBe('security');
    });

    it('returns 404 when no context exists', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/query-context',
        payload: {
          repository: 'owner/nonexistent',
          issueNumber: 1,
          query: 'test',
        },
      });

      expect(response.statusCode).toBe(404);
    });

    it('rejects missing query (400)', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/query-context',
        payload: {
          repository: 'owner/repo',
          issueNumber: 1,
        },
      });

      expect(response.statusCode).toBe(400);
    });

    it('respects maxTokens budget', async () => {
      // Store context with large content
      const longContent = 'A '.repeat(5000); // ~10k chars ~ 2500 tokens
      await app.inject({
        method: 'POST',
        url: '/api/engine/store-context',
        payload: {
          repository: 'owner/repo',
          issueNumber: 52,
          findings: {
            dev: longContent,
            security: longContent,
          },
        },
      });

      const response = await app.inject({
        method: 'POST',
        url: '/api/engine/query-context',
        payload: {
          repository: 'owner/repo',
          issueNumber: 52,
          query: 'A',
          maxTokens: 100,
        },
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      // With 100 token budget, it should not include all chunks
      expect(body.totalTokens).toBeLessThanOrEqual(100);
    });
  });
});
