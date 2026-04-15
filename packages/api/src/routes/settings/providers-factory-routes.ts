/**
 * Provider Factory Routes
 *
 * Story 9-4: Provider Factory API
 *
 * Exposes AgentProviderFactory via session-based API endpoints.
 * Elsa workflows call these instead of maintaining factory logic in C#.
 */

import type { FastifyInstance } from 'fastify';
import type { AgentTaskConfig } from '@tamma/providers';
import type { IProviderSessionService, CreateSessionInput } from '../../services/provider-session.js';

const UUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function isValidHandle(handle: string): boolean {
  return UUID_PATTERN.test(handle);
}

export function registerProviderFactoryRoutes(
  app: FastifyInstance,
  sessionService: IProviderSessionService,
): void {
  /**
   * POST /providers/create
   * Create a provider session.
   * Body: { provider, model?, apiKeyRef?, config? }
   * Returns: { handle, provider, model }
   */
  app.post('/providers/create', async (request, reply) => {
    try {
      const body = request.body;
      if (!body || typeof body !== 'object' || Array.isArray(body)) {
        return reply.status(400).send({ error: 'Request body must be a JSON object' });
      }

      const input = body as CreateSessionInput;
      if (!input.provider || typeof input.provider !== 'string') {
        return reply.status(400).send({ error: 'provider is required and must be a string' });
      }

      const result = await sessionService.create(input);
      return reply.status(201).send(result);
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Failed to create provider session';
      return reply.status(400).send({ error: message });
    }
  });

  /**
   * POST /providers/:handle/execute
   * Execute a task on a provider identified by handle.
   * Body: AgentTaskConfig
   * Returns: AgentTaskResult
   */
  app.post('/providers/:handle/execute', async (request, reply) => {
    const { handle } = request.params as { handle: string };

    if (!isValidHandle(handle)) {
      return reply.status(400).send({ error: 'Invalid session handle format' });
    }

    try {
      const body = request.body;
      if (!body || typeof body !== 'object' || Array.isArray(body)) {
        return reply.status(400).send({ error: 'Request body must be a JSON object' });
      }

      const config = body as AgentTaskConfig;
      if (!config.prompt || typeof config.prompt !== 'string') {
        return reply.status(400).send({ error: 'prompt is required and must be a string' });
      }

      const result = await sessionService.execute(handle, config);
      return reply.send(result);
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Failed to execute task';
      if (message.includes('Session not found')) {
        return reply.status(404).send({ error: message });
      }
      return reply.status(500).send({ error: message });
    }
  });

  /**
   * DELETE /providers/:handle
   * Dispose a provider and remove the session.
   * Returns: { disposed: true }
   */
  app.delete('/providers/:handle', async (request, reply) => {
    const { handle } = request.params as { handle: string };

    if (!isValidHandle(handle)) {
      return reply.status(400).send({ error: 'Invalid session handle format' });
    }

    const disposed = await sessionService.dispose(handle);
    if (!disposed) {
      return reply.status(404).send({ error: `Session not found: ${handle}` });
    }
    return reply.send({ disposed: true });
  });

  /**
   * GET /providers/sessions
   * List active provider sessions.
   */
  app.get('/providers/sessions', async (_request, reply) => {
    const sessions = sessionService.listSessions();
    return reply.send({ sessions, count: sessions.length });
  });
}
