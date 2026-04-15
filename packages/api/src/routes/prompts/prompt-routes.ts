/**
 * Prompt Registry API Routes
 *
 * Tenant-scoped REST API for managing prompt templates.
 * Supports CRUD operations for tenant overrides, system defaults,
 * and template rendering with {{variable}} interpolation.
 *
 * Routes:
 *   GET    /api/prompts                          - list resolved prompts (tenant merged with system)
 *   GET    /api/prompts/system                   - list system defaults (read-only)
 *   GET    /api/prompts/system/:role/:action      - get system default
 *   PUT    /api/prompts/system/:role/:action      - update system default (platform admin only)
 *   DELETE /api/prompts/system/:role/:action      - reset system default to hardcoded (platform admin only)
 *   GET    /api/prompts/:role/:action             - get resolved prompt (tenant override or system)
 *   PUT    /api/prompts/:role/:action             - create/update tenant override
 *   DELETE /api/prompts/:role/:action             - delete tenant override
 *   POST   /api/prompts/:role/:action/render      - render prompt with variables
 *
 * Story 12-5: Prompt Engineering Framework
 * Story 27-3: Prompt Store API Endpoints
 * Story 27-7: Prompt Store Event Sourcing (userId passthrough)
 */

import type { FastifyInstance, FastifyRequest, FastifyReply } from 'fastify';
import type { IPromptStore, UpsertPromptInput, RenderInput } from '../../services/prompt-store.js';

// ---------------------------------------------------------------------------
// Route Params / Body types
// ---------------------------------------------------------------------------

interface RoleActionParams {
  role: string;
  action: string;
}

interface UpsertBody {
  template: string;
  variables?: string[];
  systemPrompt?: string;
  enableTools?: boolean;
  maxTokens?: number;
}

interface RenderBody {
  variables: Record<string, string>;
}

// ---------------------------------------------------------------------------
// Auth helper types
// ---------------------------------------------------------------------------

interface AuthUser {
  id: string;
  role: string;
  username?: string;
}

/**
 * Extract tenantId from the request.
 * Priority: request.tenantId (from tenant-context middleware)
 *         > X-Tenant-Id header (from Elsa/service-to-service)
 *         > null (resolve system defaults)
 */
function getTenantId(request: FastifyRequest): string | null {
  // 1. From tenant-context middleware (Epic 17)
  if (request.tenantId !== undefined && request.tenantId !== null) {
    return request.tenantId;
  }

  // 2. From X-Tenant-Id header (service-to-service, e.g., Elsa)
  const headerTenantId = request.headers['x-tenant-id'];
  if (typeof headerTenantId === 'string' && headerTenantId.length > 0) {
    return headerTenantId;
  }

  // 3. From query parameter (fallback)
  const query = request.query as Record<string, string | undefined>;
  const queryTenantId = query['tenantId'];
  if (typeof queryTenantId === 'string' && queryTenantId.length > 0) {
    return queryTenantId;
  }

  return null;
}

/**
 * Extract userId from the authenticated request (for event sourcing).
 */
function getUserId(request: FastifyRequest): string | undefined {
  const authUser = (request as FastifyRequest & { authUser?: AuthUser }).authUser;
  return authUser?.id;
}

/**
 * Check if the authenticated user has the 'owner' role (platform admin).
 * Returns true if auth is not configured (dev/CLI mode).
 */
function isPlatformAdmin(request: FastifyRequest): boolean {
  if (!('authUser' in request)) {
    // No auth plugin registered — dev/CLI mode, allow all
    return true;
  }
  const authUser = (request as FastifyRequest & { authUser?: AuthUser | null }).authUser;
  if (authUser === null || authUser === undefined) {
    return false;
  }
  return authUser.role === 'owner';
}

// ---------------------------------------------------------------------------
// Validation
// ---------------------------------------------------------------------------

function validateUpsertBody(body: unknown, reply: FastifyReply): body is UpsertBody {
  const b = body as UpsertBody | undefined;

  if (typeof b?.template !== 'string' || b.template.length === 0) {
    reply.status(400).send({
      error: 'Request body must include a non-empty "template" string',
    });
    return false;
  }

  if (b.template.length > 500_000) {
    reply.status(400).send({
      error: 'Template exceeds maximum size of 500,000 characters',
    });
    return false;
  }

  if (b.variables !== undefined && !Array.isArray(b.variables)) {
    reply.status(400).send({
      error: '"variables" must be an array of strings',
    });
    return false;
  }

  if (b.maxTokens !== undefined) {
    if (typeof b.maxTokens !== 'number' || b.maxTokens <= 0 || !Number.isFinite(b.maxTokens)) {
      reply.status(400).send({
        error: '"maxTokens" must be a positive number',
      });
      return false;
    }
  }

  return true;
}

function buildUpsertInput(body: UpsertBody): UpsertPromptInput {
  const input: UpsertPromptInput = {
    template: body.template,
  };
  if (body.variables !== undefined) {
    input.variables = body.variables;
  }
  if (body.systemPrompt !== undefined) {
    input.systemPrompt = body.systemPrompt;
  }
  if (body.enableTools !== undefined) {
    input.enableTools = body.enableTools;
  }
  if (body.maxTokens !== undefined) {
    input.maxTokens = body.maxTokens;
  }
  return input;
}

// ---------------------------------------------------------------------------
// Plugin
// ---------------------------------------------------------------------------

export async function registerPromptRoutes(
  app: FastifyInstance,
  store: IPromptStore,
): Promise<void> {

  // ==========================================================================
  // System default routes (registered BEFORE parametric :role/:action to avoid
  // Fastify treating "system" as a :role parameter)
  // ==========================================================================

  // ---------- GET /api/prompts/system ----------
  // List all system default prompts (read-only for any authenticated user).
  app.get(
    '/api/prompts/system',
    async (_request: FastifyRequest, reply: FastifyReply) => {
      const summaries = await store.listSystemDefaults();
      return reply.send({ templates: summaries, total: summaries.length });
    },
  );

  // ---------- GET /api/prompts/system/:role/:action ----------
  // Get a specific system default prompt.
  app.get(
    '/api/prompts/system/:role/:action',
    async (
      request: FastifyRequest<{ Params: RoleActionParams }>,
      reply: FastifyReply,
    ) => {
      const { role, action } = request.params;
      const template = await store.getSystemDefault(role, action);
      if (template === undefined) {
        return reply.status(404).send({
          error: `System default not found for role="${role}", action="${action}"`,
        });
      }
      return reply.send(template);
    },
  );

  // ---------- PUT /api/prompts/system/:role/:action ----------
  // Update a system default prompt (platform admin only).
  app.put(
    '/api/prompts/system/:role/:action',
    async (
      request: FastifyRequest<{ Params: RoleActionParams; Body: UpsertBody }>,
      reply: FastifyReply,
    ) => {
      if (!isPlatformAdmin(request)) {
        return reply.status(403).send({
          error: 'Only platform administrators can modify system defaults',
        });
      }

      const { role, action } = request.params;
      const body = request.body as UpsertBody;
      if (!validateUpsertBody(body, reply)) return;

      try {
        const input = buildUpsertInput(body);
        const userId = getUserId(request);
        const updated = await store.upsertSystemDefault(role, action, input, userId);
        return reply.status(200).send(updated);
      } catch (err) {
        const message = err instanceof Error ? err.message : 'Failed to update system default';
        return reply.status(400).send({ error: message });
      }
    },
  );

  // ---------- DELETE /api/prompts/system/:role/:action ----------
  // Reset a system default to the hardcoded value (platform admin only).
  app.delete(
    '/api/prompts/system/:role/:action',
    async (
      request: FastifyRequest<{ Params: RoleActionParams }>,
      reply: FastifyReply,
    ) => {
      if (!isPlatformAdmin(request)) {
        return reply.status(403).send({
          error: 'Only platform administrators can reset system defaults',
        });
      }

      const { role, action } = request.params;
      const userId = getUserId(request);
      const restored = await store.resetSystemDefault(role, action, userId);
      if (restored === undefined) {
        return reply.status(404).send({
          error: `No hardcoded default exists for role="${role}", action="${action}"`,
        });
      }
      return reply.send(restored);
    },
  );

  // ==========================================================================
  // Tenant-scoped routes
  // ==========================================================================

  // ---------- GET /api/prompts ----------
  // List all resolved prompts for the current tenant (merged with system defaults).
  app.get(
    '/api/prompts',
    async (request: FastifyRequest, reply: FastifyReply) => {
      const tenantId = getTenantId(request);
      const summaries = await store.list(tenantId);
      return reply.send({ templates: summaries, total: summaries.length });
    },
  );

  // ---------- GET /api/prompts/:role/:action ----------
  // Get the resolved prompt for the current tenant (override if exists, else system default).
  app.get(
    '/api/prompts/:role/:action',
    async (
      request: FastifyRequest<{ Params: RoleActionParams }>,
      reply: FastifyReply,
    ) => {
      const tenantId = getTenantId(request);
      const { role, action } = request.params;

      const template = await store.get(tenantId, role, action);
      if (template === undefined) {
        return reply.status(404).send({
          error: `Prompt template not found for role="${role}", action="${action}"`,
        });
      }

      return reply.send(template);
    },
  );

  // ---------- PUT /api/prompts/:role/:action ----------
  // Create or update a tenant override for the current tenant.
  app.put(
    '/api/prompts/:role/:action',
    async (
      request: FastifyRequest<{ Params: RoleActionParams; Body: UpsertBody }>,
      reply: FastifyReply,
    ) => {
      const tenantId = getTenantId(request);
      const { role, action } = request.params;
      const body = request.body as UpsertBody;

      if (!validateUpsertBody(body, reply)) return;

      try {
        const input = buildUpsertInput(body);
        const userId = getUserId(request);
        const updated = await store.upsert(tenantId, role, action, input, userId);
        return reply.status(200).send(updated);
      } catch (err) {
        const message = err instanceof Error ? err.message : 'Failed to update prompt template';
        return reply.status(400).send({ error: message });
      }
    },
  );

  // ---------- DELETE /api/prompts/:role/:action ----------
  // Delete a tenant override (falls back to system default).
  app.delete(
    '/api/prompts/:role/:action',
    async (
      request: FastifyRequest<{ Params: RoleActionParams }>,
      reply: FastifyReply,
    ) => {
      const tenantId = getTenantId(request);
      if (tenantId === null) {
        return reply.status(400).send({
          error: 'Cannot delete system default via this endpoint. Use DELETE /api/prompts/system/:role/:action instead.',
        });
      }

      const { role, action } = request.params;
      const userId = getUserId(request);
      const deleted = await store.delete(tenantId, role, action, userId);
      if (!deleted) {
        return reply.status(404).send({
          error: `No tenant override found for role="${role}", action="${action}"`,
        });
      }

      return reply.status(204).send();
    },
  );

  // ---------- POST /api/prompts/:role/:action/render ----------
  // Render a prompt template by interpolating {{variable}} placeholders.
  // Tenant-scoped: resolves tenant override first, then system default.
  app.post(
    '/api/prompts/:role/:action/render',
    async (
      request: FastifyRequest<{ Params: RoleActionParams; Body: RenderBody }>,
      reply: FastifyReply,
    ) => {
      const tenantId = getTenantId(request);
      const { role, action } = request.params;
      const body = request.body as RenderBody;

      // Validate body
      if (body?.variables === undefined || body.variables === null || typeof body.variables !== 'object' || Array.isArray(body.variables)) {
        return reply.status(400).send({
          error: 'Request body must include a "variables" object (key-value pairs)',
        });
      }

      // Validate all values are strings
      for (const [key, value] of Object.entries(body.variables)) {
        if (typeof value !== 'string') {
          return reply.status(400).send({
            error: `Variable "${key}" must be a string value`,
          });
        }
      }

      const renderInput: RenderInput = { variables: body.variables };
      const result = await store.render(tenantId, role, action, renderInput);

      if (result === undefined) {
        return reply.status(404).send({
          error: `Prompt template not found for role="${role}", action="${action}"`,
        });
      }

      return reply.send(result);
    },
  );
}
