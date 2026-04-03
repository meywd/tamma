/**
 * Engine Context Routes
 *
 * Endpoints called by C# Elsa workflow activities to store and retrieve
 * contextual findings for development cycles.
 *
 * Routes:
 *   POST /api/engine/store-context   — store findings JSON for an issue
 *   GET  /api/engine/context/:issueNumber — retrieve stored context by issue number
 *   POST /api/engine/query-context   — simplified RAG query over stored context
 *
 * Storage: in-memory Map keyed by `${repository}:${issueNumber}`.
 * For production, swap to PostgreSQL cycle_context table.
 *
 * Story 6-11: Context API Wiring
 */

import { z } from 'zod';
import type { FastifyInstance, FastifyRequest, FastifyReply } from 'fastify';

// ---------------------------------------------------------------------------
// Zod Schemas
// ---------------------------------------------------------------------------

const StoreContextBodySchema = z.object({
  repository: z.string().min(1),
  issueNumber: z.number().int().positive(),
  findings: z.record(z.string(), z.unknown()).refine(
    (val) => Object.keys(val).length > 0,
    { message: 'findings must have at least one key' },
  ),
});

const QueryContextBodySchema = z.object({
  repository: z.string().min(1),
  issueNumber: z.number().int().positive(),
  query: z.string().min(1),
  role: z.string().optional(),
  maxTokens: z.number().int().positive().optional(),
});

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

interface StoredContext {
  id: string;
  repository: string;
  issueNumber: number;
  findings: Record<string, unknown>;
  contextIds: string[];
  storedAt: string;
}

interface StoreContextResponse {
  contextIds: string[];
  storedAt: string;
}

interface QueryContextResponse {
  chunks: Array<{ content: string; role: string; score: number }>;
  totalTokens: number;
}

// ---------------------------------------------------------------------------
// In-memory store
// ---------------------------------------------------------------------------

/** Simple in-memory context store. Keyed by "repository:issueNumber". */
const contextStore = new Map<string, StoredContext>();

function contextKey(repository: string, issueNumber: number): string {
  return `${repository}:${issueNumber}`;
}

function generateId(): string {
  return `ctx-${Date.now()}-${Math.random().toString(36).slice(2, 10)}`;
}

// ---------------------------------------------------------------------------
// Plugin
// ---------------------------------------------------------------------------

export async function registerEngineContextRoutes(
  fastify: FastifyInstance,
): Promise<void> {
  // ---------- POST /api/engine/store-context ----------
  fastify.post(
    '/api/engine/store-context',
    async (
      request: FastifyRequest<{
        Body: { repository: string; issueNumber: number; findings: Record<string, unknown> };
      }>,
      reply: FastifyReply,
    ) => {
      const parsed = StoreContextBodySchema.safeParse(request.body);
      if (!parsed.success) {
        return reply.status(400).send({ error: parsed.error.message });
      }

      const { repository, issueNumber, findings } = parsed.data;
      const key = contextKey(repository, issueNumber);

      // Generate context IDs — one per finding role
      const contextIds = Object.keys(findings).map(() => generateId());

      const storedAt = new Date().toISOString();
      const stored: StoredContext = {
        id: generateId(),
        repository,
        issueNumber,
        findings,
        contextIds,
        storedAt,
      };

      contextStore.set(key, stored);

      // Evict oldest entry if map exceeds max size
      if (contextStore.size > 10_000) {
        const firstKey = contextStore.keys().next().value;
        if (firstKey !== undefined) contextStore.delete(firstKey);
      }

      fastify.log.info(
        { repository, issueNumber, contextIdCount: contextIds.length },
        'Context stored',
      );

      const response: StoreContextResponse = { contextIds, storedAt };
      return reply.send(response);
    },
  );

  // ---------- GET /api/engine/context/:issueNumber ----------
  fastify.get(
    '/api/engine/context/:issueNumber',
    async (
      request: FastifyRequest<{
        Params: { issueNumber: string };
        Querystring: { repository?: string };
      }>,
      reply: FastifyReply,
    ) => {
      const issueNumber = parseInt(request.params.issueNumber, 10);
      if (Number.isNaN(issueNumber) || issueNumber <= 0) {
        return reply.status(400).send({ error: 'Invalid issueNumber parameter' });
      }

      const repository = request.query.repository ?? '';

      // If repository is specified, do an exact lookup
      if (repository) {
        const stored = contextStore.get(contextKey(repository, issueNumber));
        if (stored === undefined) {
          return reply.status(404).send({ error: 'Context not found' });
        }
        return reply.send({
          findings: stored.findings,
          contextIds: stored.contextIds,
          storedAt: stored.storedAt,
        });
      }

      // Otherwise scan all entries for this issueNumber
      for (const stored of contextStore.values()) {
        if (stored.issueNumber === issueNumber) {
          return reply.send({
            findings: stored.findings,
            contextIds: stored.contextIds,
            storedAt: stored.storedAt,
          });
        }
      }

      return reply.status(404).send({ error: 'Context not found' });
    },
  );

  // ---------- POST /api/engine/query-context ----------
  fastify.post(
    '/api/engine/query-context',
    async (
      request: FastifyRequest<{
        Body: {
          repository: string;
          issueNumber: number;
          query: string;
          role?: string;
          maxTokens?: number;
        };
      }>,
      reply: FastifyReply,
    ) => {
      const parsed = QueryContextBodySchema.safeParse(request.body);
      if (!parsed.success) {
        return reply.status(400).send({ error: parsed.error.message });
      }

      const { repository, issueNumber, query, role, maxTokens } = parsed.data;
      const key = contextKey(repository, issueNumber);
      const stored = contextStore.get(key);

      if (stored === undefined) {
        return reply.status(404).send({ error: 'No context found for this issue' });
      }

      // Simplified RAG: filter findings by role and do basic text matching
      const chunks: Array<{ content: string; role: string; score: number }> = [];
      const queryLower = query.toLowerCase();

      for (const [findingRole, findingData] of Object.entries(stored.findings)) {
        // If a role filter is specified, only include matching roles
        if (role !== undefined && findingRole !== role) {
          continue;
        }

        const content = typeof findingData === 'string'
          ? findingData
          : JSON.stringify(findingData, null, 2);

        // Basic relevance scoring: check if query terms appear in content
        const contentLower = content.toLowerCase();
        const queryTerms = queryLower.split(/\s+/);
        const matchCount = queryTerms.filter((term) => contentLower.includes(term)).length;
        const score = queryTerms.length > 0 ? matchCount / queryTerms.length : 0;

        chunks.push({ content, role: findingRole, score });
      }

      // Sort by score descending
      chunks.sort((a, b) => b.score - a.score);

      // Apply maxTokens budget (rough estimate: 4 chars per token)
      const tokenBudget = maxTokens ?? 4000;
      let totalTokens = 0;
      const budgetedChunks: typeof chunks = [];

      for (const chunk of chunks) {
        const chunkTokens = Math.ceil(chunk.content.length / 4);
        if (totalTokens + chunkTokens > tokenBudget) {
          break;
        }
        totalTokens += chunkTokens;
        budgetedChunks.push(chunk);
      }

      fastify.log.info(
        { repository, issueNumber, chunksReturned: budgetedChunks.length, totalTokens },
        'Context query completed',
      );

      const response: QueryContextResponse = {
        chunks: budgetedChunks,
        totalTokens,
      };
      return reply.send(response);
    },
  );
}
