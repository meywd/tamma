/**
 * Prompt Registry API Routes
 *
 * Fastify routes for managing prompt templates keyed by (role, action).
 * Supports CRUD operations, listing, and template rendering with
 * {{variable}} interpolation.
 *
 * Routes:
 *   GET  /api/prompts                          — list all role/action pairs
 *   GET  /api/prompts/:role/:action             — get a prompt template
 *   PUT  /api/prompts/:role/:action             — create/update a template
 *   POST /api/prompts/:role/:action/render      — render template with variables
 *
 * Story 12-5: Prompt Engineering Framework
 */

import type { FastifyInstance, FastifyRequest, FastifyReply } from 'fastify';
import type { PromptStore, UpsertPromptInput, RenderInput } from '../../services/prompt-store.js';

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
// Plugin
// ---------------------------------------------------------------------------

export async function registerPromptRoutes(
  app: FastifyInstance,
  store: PromptStore,
): Promise<void> {
  // ---------- GET /api/prompts ----------
  // List all registered prompt templates (summaries).
  app.get(
    '/api/prompts',
    async (_request: FastifyRequest, reply: FastifyReply) => {
      const summaries = await store.list();
      return reply.send({ templates: summaries, total: summaries.length });
    },
  );

  // ---------- GET /api/prompts/:role/:action ----------
  // Retrieve a specific prompt template by role and action.
  app.get(
    '/api/prompts/:role/:action',
    async (
      request: FastifyRequest<{ Params: RoleActionParams }>,
      reply: FastifyReply,
    ) => {
      const { role, action } = request.params;

      const template = await store.get(role, action);
      if (template === undefined) {
        return reply.status(404).send({
          error: `Prompt template not found for role="${role}", action="${action}"`,
        });
      }

      return reply.send(template);
    },
  );

  // ---------- PUT /api/prompts/:role/:action ----------
  // Create or update a prompt template.
  app.put(
    '/api/prompts/:role/:action',
    async (
      request: FastifyRequest<{ Params: RoleActionParams; Body: UpsertBody }>,
      reply: FastifyReply,
    ) => {
      const { role, action } = request.params;
      const body = request.body as UpsertBody;

      // Validate body
      if (typeof body?.template !== 'string' || body.template.length === 0) {
        return reply.status(400).send({
          error: 'Request body must include a non-empty "template" string',
        });
      }

      if (body.variables !== undefined && !Array.isArray(body.variables)) {
        return reply.status(400).send({
          error: '"variables" must be an array of strings',
        });
      }

      if (body.maxTokens !== undefined) {
        if (typeof body.maxTokens !== 'number' || body.maxTokens <= 0 || !Number.isFinite(body.maxTokens)) {
          return reply.status(400).send({
            error: '"maxTokens" must be a positive number',
          });
        }
      }

      try {
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

        const updated = await store.upsert(role, action, input);
        return reply.status(200).send(updated);
      } catch (err) {
        const message = err instanceof Error ? err.message : 'Failed to update prompt template';
        return reply.status(400).send({ error: message });
      }
    },
  );

  // ---------- POST /api/prompts/:role/:action/render ----------
  // Render a prompt template by interpolating {{variable}} placeholders.
  app.post(
    '/api/prompts/:role/:action/render',
    async (
      request: FastifyRequest<{ Params: RoleActionParams; Body: RenderBody }>,
      reply: FastifyReply,
    ) => {
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
      const result = await store.render(role, action, renderInput);

      if (result === undefined) {
        return reply.status(404).send({
          error: `Prompt template not found for role="${role}", action="${action}"`,
        });
      }

      return reply.send(result);
    },
  );
}
