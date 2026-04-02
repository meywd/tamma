/**
 * Engine Task Routes
 *
 * Endpoints called by C# Elsa workflow activities for agent task execution
 * and cycle lifecycle management.
 *
 * Routes:
 *   POST /api/engine/execute-task  — resolve agent by role and execute prompt
 *   POST /api/engine/cycle-result  — store cycle completion result, log event
 *
 * Agent resolution: When an IRoleBasedAgentResolver is provided, it resolves
 * the agent by role with full provider chain fallback. When unavailable, the
 * endpoint returns a 503 explaining the resolver is not configured.
 *
 * Story 6-11: Context API Wiring
 */

import { z } from 'zod';
import type { FastifyInstance, FastifyRequest, FastifyReply } from 'fastify';

// ---------------------------------------------------------------------------
// Local interfaces (decoupled from @tamma/providers)
// ---------------------------------------------------------------------------

/**
 * Minimal interface matching IRoleBasedAgentResolver.
 * Defined locally to avoid requiring @tamma/providers to be built for
 * typecheck to pass.
 */
export interface IAgentResolver {
  getAgentForRole(
    role: string,
    context: { projectId: string; engineId: string },
  ): Promise<IAgentExecutor>;
}

export interface IAgentExecutor {
  executeTask(
    config: { prompt: string; cwd: string; model?: string; maxBudgetUsd?: number },
  ): Promise<{ success: boolean; output: string; costUsd: number; durationMs: number; error?: string }>;
}

// ---------------------------------------------------------------------------
// Zod Schemas
// ---------------------------------------------------------------------------

const ExecuteTaskBodySchema = z.object({
  prompt: z.string().min(1),
  role: z.string().min(1).optional(),
  repository: z.string().optional(),
  enableTools: z.boolean().optional(),
  model: z.string().optional(),
  maxBudgetUsd: z.number().positive().optional(),
  cwd: z.string().optional(),
});

const CycleResultBodySchema = z.object({
  exitReason: z.string().min(1),
  issueNumber: z.number().int().optional(),
  repository: z.string().optional(),
  error: z.string().optional(),
  durationMs: z.number().optional(),
  metadata: z.record(z.string(), z.unknown()).optional(),
});

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

interface ExecuteTaskResponse {
  success: boolean;
  output: string;
  tokensUsed: number;
  costUsd: number;
  durationMs: number;
  toolCalls: number;
  error?: string;
}

interface CycleResultEntry {
  id: string;
  exitReason: string;
  issueNumber?: number;
  repository?: string;
  error?: string;
  durationMs?: number;
  metadata?: Record<string, unknown>;
  storedAt: string;
}

// ---------------------------------------------------------------------------
// In-memory stores
// ---------------------------------------------------------------------------

/** Stores cycle results for later retrieval / audit. */
const cycleResults: CycleResultEntry[] = [];

function generateId(): string {
  return `cr-${Date.now()}-${Math.random().toString(36).slice(2, 10)}`;
}

// ---------------------------------------------------------------------------
// Options
// ---------------------------------------------------------------------------

export interface EngineTaskRouteOptions {
  /**
   * Agent resolver for executing LLM tasks.
   * When undefined the execute-task endpoint returns 503.
   */
  agentResolver?: IAgentResolver;
  /** Default working directory for agent tasks. */
  cwd?: string;
  /** Project identifier for agent resolution context. */
  projectId?: string;
  /** Engine identifier for agent resolution context. */
  engineId?: string;
}

// ---------------------------------------------------------------------------
// Plugin
// ---------------------------------------------------------------------------

export async function registerEngineTaskRoutes(
  fastify: FastifyInstance,
  opts: EngineTaskRouteOptions = {},
): Promise<void> {
  const { agentResolver, cwd: defaultCwd, projectId, engineId } = opts;

  // ---------- POST /api/engine/execute-task ----------
  fastify.post(
    '/api/engine/execute-task',
    async (
      request: FastifyRequest<{
        Body: {
          prompt: string;
          role?: string;
          repository?: string;
          enableTools?: boolean;
          model?: string;
          maxBudgetUsd?: number;
          cwd?: string;
        };
      }>,
      reply: FastifyReply,
    ) => {
      const parsed = ExecuteTaskBodySchema.safeParse(request.body);
      if (!parsed.success) {
        return reply.status(400).send({ error: parsed.error.message });
      }

      const {
        prompt,
        role = 'developer',
        model,
        maxBudgetUsd,
        cwd: taskCwd,
      } = parsed.data;

      if (agentResolver === undefined) {
        return reply.status(503).send({
          error: 'Agent resolver not configured. LLM task execution is unavailable.',
        });
      }

      const startMs = Date.now();

      try {
        const agent = await agentResolver.getAgentForRole(role, {
          projectId: projectId ?? 'default',
          engineId: engineId ?? 'engine-api',
        });

        const taskConfig: Parameters<typeof agent.executeTask>[0] = {
          prompt,
          cwd: taskCwd ?? defaultCwd ?? process.cwd(),
        };
        if (model !== undefined) {
          taskConfig.model = model;
        }
        if (maxBudgetUsd !== undefined) {
          taskConfig.maxBudgetUsd = maxBudgetUsd;
        }

        const result = await agent.executeTask(taskConfig);
        const durationMs = Date.now() - startMs;

        fastify.log.info(
          {
            role,
            success: result.success,
            costUsd: result.costUsd,
            durationMs,
          },
          'Agent task executed',
        );

        const response: ExecuteTaskResponse = {
          success: result.success,
          output: result.output,
          tokensUsed: 0, // Token tracking not yet wired
          costUsd: result.costUsd,
          durationMs: result.durationMs,
          toolCalls: 0, // Tool call tracking not yet wired
        };
        if (result.error !== undefined) {
          response.error = result.error;
        }

        return reply.send(response);
      } catch (err: unknown) {
        const durationMs = Date.now() - startMs;
        const message = err instanceof Error ? err.message : String(err);

        fastify.log.error(
          { err, role, durationMs },
          'Agent task execution failed',
        );

        const response: ExecuteTaskResponse = {
          success: false,
          output: '',
          tokensUsed: 0,
          costUsd: 0,
          durationMs,
          toolCalls: 0,
          error: message,
        };
        return reply.status(500).send(response);
      }
    },
  );

  // ---------- POST /api/engine/cycle-result ----------
  fastify.post(
    '/api/engine/cycle-result',
    async (
      request: FastifyRequest<{
        Body: {
          exitReason: string;
          issueNumber?: number;
          repository?: string;
          error?: string;
          durationMs?: number;
          metadata?: Record<string, unknown>;
        };
      }>,
      reply: FastifyReply,
    ) => {
      const parsed = CycleResultBodySchema.safeParse(request.body);
      if (!parsed.success) {
        return reply.status(400).send({ error: parsed.error.message });
      }

      const { exitReason, issueNumber, repository, error, durationMs, metadata } =
        parsed.data;

      const entry: CycleResultEntry = {
        id: generateId(),
        exitReason,
        storedAt: new Date().toISOString(),
      };
      if (issueNumber !== undefined) {
        entry.issueNumber = issueNumber;
      }
      if (repository !== undefined) {
        entry.repository = repository;
      }
      if (error !== undefined) {
        entry.error = error;
      }
      if (durationMs !== undefined) {
        entry.durationMs = durationMs;
      }
      if (metadata !== undefined) {
        entry.metadata = metadata;
      }

      cycleResults.push(entry);

      // Cap in-memory store at 10 000 entries
      if (cycleResults.length > 10_000) {
        cycleResults.splice(0, cycleResults.length - 10_000);
      }

      fastify.log.info(
        { id: entry.id, exitReason, issueNumber, repository },
        'Cycle result stored',
      );

      return reply.status(201).send({
        id: entry.id,
        storedAt: entry.storedAt,
      });
    },
  );

  // ---------- GET /api/engine/cycle-results ----------
  // Bonus: read-back for dashboard / debugging
  fastify.get(
    '/api/engine/cycle-results',
    async (
      request: FastifyRequest<{
        Querystring: { issueNumber?: string; limit?: string };
      }>,
      reply: FastifyReply,
    ) => {
      const issueNumber = request.query.issueNumber
        ? parseInt(request.query.issueNumber, 10)
        : undefined;
      const limit = Math.min(200, parseInt(request.query.limit ?? '50', 10) || 50);

      let results = [...cycleResults];

      if (issueNumber !== undefined && !Number.isNaN(issueNumber)) {
        results = results.filter((r) => r.issueNumber === issueNumber);
      }

      // Return most recent first
      results.reverse();
      const data = results.slice(0, limit);

      return reply.send({ data, total: results.length });
    },
  );
}
