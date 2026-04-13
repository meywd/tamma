/**
 * Agent Config API Routes (Story 9-1)
 *
 * Provides tenant-scoped CRUD for agent configuration stored in Postgres.
 *
 * Routes:
 *   GET  /api/v1/agents/config         — resolved config for the authenticated account
 *   PUT  /api/v1/agents/config         — upsert account-level config
 *   POST /api/v1/agents/config/validate — validate a config payload without saving
 *
 * Rate limiting: 100 req/min read, 30 req/min write.
 */

import type { FastifyInstance, FastifyRequest, FastifyReply } from 'fastify';

import { validateAgentsConfig, validateSecurityConfig, TammaError } from '@tamma/shared';
import type { IAgentsConfig, SecurityConfig } from '@tamma/shared';

import type { IAgentConfigStore, AgentConfigDocument } from '../../persistence/agent-config-store.js';

// ---------------------------------------------------------------------------
// Request / response types
// ---------------------------------------------------------------------------

interface PutBody {
  config?: IAgentsConfig;
  security?: SecurityConfig;
}

interface ValidateBody {
  config?: IAgentsConfig;
  security?: SecurityConfig;
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

function getUserId(request: FastifyRequest): string | null {
  const authUser = (request as FastifyRequest & { authUser?: AuthUser | null }).authUser;
  return authUser?.id ?? null;
}

/**
 * Validate an AgentConfigDocument (both agents + security).
 * Returns an array of error messages. Empty = valid.
 */
function validateConfigDocument(doc: Partial<AgentConfigDocument>): string[] {
  const errors: string[] = [];

  if (doc.agents !== undefined) {
    try {
      validateAgentsConfig(doc.agents);
    } catch (err) {
      if (err instanceof TammaError) {
        errors.push(err.message);
      } else if (err instanceof Error) {
        errors.push(err.message);
      } else {
        errors.push('Invalid agents config');
      }
    }
  }

  if (doc.security !== undefined) {
    try {
      validateSecurityConfig(doc.security);
    } catch (err) {
      if (err instanceof TammaError) {
        errors.push(err.message);
      } else if (err instanceof Error) {
        errors.push(err.message);
      } else {
        errors.push('Invalid security config');
      }
    }
  }

  return errors;
}

// ---------------------------------------------------------------------------
// Route registration
// ---------------------------------------------------------------------------

export interface AgentConfigRouteOptions {
  store: IAgentConfigStore;
}

export async function registerAgentConfigRoutes(
  app: FastifyInstance,
  options: AgentConfigRouteOptions,
): Promise<void> {
  const { store } = options;

  // GET /config — resolved config for the authenticated account
  app.get(
    '/config',
    {
      config: {
        rateLimit: { max: 100, timeWindow: '1 minute' },
      },
    },
    async (request: FastifyRequest, reply: FastifyReply) => {
      const accountId = getAccountId(request);
      const resolved = await store.resolve(accountId);

      return reply.send({
        config: resolved.config.agents,
        security: resolved.config.security,
        source: resolved.source,
        version: resolved.version,
      });
    },
  );

  // PUT /config — upsert account-level config
  app.put(
    '/config',
    {
      config: {
        rateLimit: { max: 30, timeWindow: '1 minute' },
      },
    },
    async (request: FastifyRequest, reply: FastifyReply) => {
      const body = request.body as PutBody | null | undefined;

      if (!body || (body.config === undefined && body.security === undefined)) {
        return reply.status(400).send({
          error: 'Request body must contain at least one of: config, security',
        });
      }

      // Build the document to save. Start from existing resolved config.
      const accountId = getAccountId(request);
      const existing = await store.resolve(accountId);

      const doc: AgentConfigDocument = {
        agents: body.config ?? existing.config.agents,
        security: body.security ?? existing.config.security,
      };

      // Validate before saving
      const errors = validateConfigDocument(doc);
      if (errors.length > 0) {
        return reply.status(400).send({ error: 'Validation failed', errors });
      }

      const userId = getUserId(request);
      const saved = await store.upsert(accountId, doc, userId);

      return reply.send({
        config: saved.config.agents,
        security: saved.config.security,
        version: saved.version,
      });
    },
  );

  // POST /config/validate — validate without saving
  app.post(
    '/config/validate',
    {
      config: {
        rateLimit: { max: 100, timeWindow: '1 minute' },
      },
    },
    async (request: FastifyRequest, reply: FastifyReply) => {
      const body = request.body as ValidateBody | null | undefined;

      if (!body || (body.config === undefined && body.security === undefined)) {
        return reply.status(400).send({
          error: 'Request body must contain at least one of: config, security',
        });
      }

      // Map body keys to AgentConfigDocument shape for validation
      const toValidate: Partial<AgentConfigDocument> = {};
      if (body.config !== undefined) {
        toValidate.agents = body.config;
      }
      if (body.security !== undefined) {
        toValidate.security = body.security;
      }

      const errors = validateConfigDocument(toValidate);

      return reply.send({
        valid: errors.length === 0,
        errors,
      });
    },
  );
}
