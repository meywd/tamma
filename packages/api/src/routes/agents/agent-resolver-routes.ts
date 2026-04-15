/**
 * Agent Resolver API Routes (Story 9-8)
 *
 * Exposes the unified agent resolver as REST endpoints so both
 * the TS engine (in-process) and Elsa workflows (via HTTP) use
 * identical resolution logic.
 *
 * Routes:
 *   GET  /api/v1/agents/:role/resolve      — resolve full agent config for a role
 *   POST /api/v1/agents/resolve-for-phase  — resolve agent for a workflow phase
 *
 * Rate limiting: 100 req/min read.
 */

import type { FastifyInstance, FastifyRequest, FastifyReply } from 'fastify';

import type { AgentType, WorkflowPhase } from '@tamma/shared';

import type { IAgentResolverService, ResolveForPhaseOptions } from '../../services/agent-resolver.js';

// ---------------------------------------------------------------------------
// Request types
// ---------------------------------------------------------------------------

interface ResolveForRoleParams {
  role: string;
}

interface ResolveForRoleQuery {
  projectId?: string;
  engineId?: string;
}

interface ResolveForPhaseBody {
  phase: string;
  projectId?: string;
  engineId?: string;
  taskOverrides?: {
    maxBudgetUsd?: number;
    allowedTools?: string[];
    permissionMode?: 'default' | 'bypassPermissions';
    prompt?: string;
    cwd?: string;
    model?: string;
    sessionId?: string;
  };
}

/** Shape of the authUser decoration set by the auth/JWT plugin. */
interface AuthUser {
  id: string;
  tenantId?: string;
  role?: string;
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/** Default tenant ID for CLI/self-hosted mode when no auth is present. */
const DEFAULT_ACCOUNT_ID = '00000000-0000-0000-0000-000000000000';

function getAccountId(request: FastifyRequest): string {
  const authUser = (request as FastifyRequest & { authUser?: AuthUser | null }).authUser;
  return authUser?.tenantId ?? DEFAULT_ACCOUNT_ID;
}

// ---------------------------------------------------------------------------
// Valid values
// ---------------------------------------------------------------------------

const VALID_PHASES = new Set<string>([
  'ISSUE_SELECTION',
  'CONTEXT_ANALYSIS',
  'PLAN_GENERATION',
  'CODE_GENERATION',
  'PR_CREATION',
  'CODE_REVIEW',
  'TEST_EXECUTION',
  'STATUS_MONITORING',
]);

/** Prototype pollution guard. */
const FORBIDDEN_KEYS = new Set(['__proto__', 'constructor', 'prototype']);

// ---------------------------------------------------------------------------
// Route registration
// ---------------------------------------------------------------------------

export interface AgentResolverRouteOptions {
  resolverService: IAgentResolverService;
}

export async function registerAgentResolverRoutes(
  app: FastifyInstance,
  options: AgentResolverRouteOptions,
): Promise<void> {
  const { resolverService } = options;

  // GET /:role/resolve — resolve full agent config for a role
  app.get<{
    Params: ResolveForRoleParams;
    Querystring: ResolveForRoleQuery;
  }>(
    '/:role/resolve',
    async (request: FastifyRequest, reply: FastifyReply) => {
      const { role } = request.params as ResolveForRoleParams;
      const query = request.query as ResolveForRoleQuery;

      // Validate role
      if (FORBIDDEN_KEYS.has(role)) {
        return reply.status(400).send({
          error: `Forbidden role name: "${role}"`,
        });
      }

      if (role.length === 0 || role.length > 64) {
        return reply.status(400).send({
          error: `Role name must be 1-64 characters (got ${role.length})`,
        });
      }

      const accountId = getAccountId(request);

      try {
        // Build options with conditional assignment to satisfy exactOptionalPropertyTypes
        const roleOptions: { projectId?: string; engineId?: string } = {};
        if (query.projectId !== undefined) {
          roleOptions.projectId = query.projectId;
        }
        if (query.engineId !== undefined) {
          roleOptions.engineId = query.engineId;
        }

        const result = await resolverService.resolveForRole(
          accountId,
          role as AgentType,
          roleOptions,
        );

        return reply.send(result);
      } catch (err) {
        const message = err instanceof Error ? err.message : 'Resolution failed';
        return reply.status(400).send({ error: message });
      }
    },
  );

  // POST /resolve-for-phase — resolve agent for a workflow phase
  app.post(
    '/resolve-for-phase',
    async (request: FastifyRequest, reply: FastifyReply) => {
      const body = request.body as ResolveForPhaseBody | null | undefined;

      if (!body || typeof body.phase !== 'string') {
        return reply.status(400).send({
          error: 'Request body must contain "phase" (string)',
        });
      }

      // Validate phase
      if (!VALID_PHASES.has(body.phase)) {
        return reply.status(400).send({
          error: `Invalid workflow phase: "${body.phase}". Valid phases: ${[...VALID_PHASES].join(', ')}`,
        });
      }

      const accountId = getAccountId(request);

      try {
        const phaseOptions: ResolveForPhaseOptions = {
          projectId: body.projectId,
          engineId: body.engineId,
          taskOverrides: body.taskOverrides,
        };

        const result = await resolverService.resolveForPhase(
          accountId,
          body.phase as WorkflowPhase,
          phaseOptions,
        );

        return reply.send(result);
      } catch (err) {
        const message = err instanceof Error ? err.message : 'Resolution failed';
        return reply.status(400).send({ error: message });
      }
    },
  );
}
