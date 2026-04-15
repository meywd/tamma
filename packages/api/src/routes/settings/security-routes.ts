/**
 * Security configuration routes.
 *
 * Story 9-7: Extended with sanitization service endpoints.
 *
 * Backward-compatible: The original GET/PUT /security endpoints (in-memory config)
 * are still available. New endpoints operate on the persistent sanitization store.
 */

import type { FastifyInstance, FastifyRequest } from 'fastify';
import type { ConfigService } from '../../services/settings/ConfigService.js';
import type { ISanitizationStore, SanitizationRulesInput } from '../../services/sanitization-store.js';

/**
 * Extract account ID from request for tenant scoping.
 */
function getAccountId(request: FastifyRequest): string | null {
  const user = request.user as unknown;
  if (user && typeof user === 'object' && 'accountId' in user) {
    const accountId = (user as Record<string, unknown>)['accountId'];
    if (typeof accountId === 'string') return accountId;
  }

  // Dev fallback
  if (process.env['NODE_ENV'] !== 'production') {
    const header = request.headers['x-account-id'];
    if (typeof header === 'string' && header.length > 0) return header;
  }

  return null;
}

export function registerSecurityRoutes(
  app: FastifyInstance,
  service: ConfigService,
  sanitizationStore?: ISanitizationStore,
): void {
  // --- Original in-memory config endpoints (backward-compatible) ---
  app.get('/security', async (_request, reply) => {
    const config = await service.getSecurityConfig();
    return reply.send(config);
  });

  app.put('/security', async (request, reply) => {
    try {
      const body = request.body as import('@tamma/shared').SecurityConfig;
      const updated = await service.updateSecurityConfig(body);
      return reply.send(updated);
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Invalid configuration';
      return reply.status(400).send({ error: message });
    }
  });

  // --- New sanitization store endpoints (9-7) ---
  if (!sanitizationStore) return;

  /**
   * POST /sanitize
   * Sanitize content using the account's configured rules.
   * Body: { content: string, direction: 'input' | 'output' }
   * Returns: { result: string, warnings: string[] }
   */
  app.post('/sanitize', async (request, reply) => {
    try {
      const body = request.body;
      if (!body || typeof body !== 'object' || Array.isArray(body)) {
        return reply.status(400).send({ error: 'Request body must be a JSON object' });
      }

      const { content, direction } = body as { content?: unknown; direction?: unknown };

      if (typeof content !== 'string') {
        return reply.status(400).send({ error: 'content must be a string' });
      }
      if (direction !== 'input' && direction !== 'output') {
        return reply.status(400).send({ error: 'direction must be "input" or "output"' });
      }

      const accountId = getAccountId(request);
      const result = await sanitizationStore.sanitize(accountId, content, direction);
      return reply.send(result);
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Sanitization failed';
      return reply.status(500).send({ error: message });
    }
  });

  /**
   * GET /sanitize/rules
   * Get sanitization rules for the authenticated account.
   */
  app.get('/sanitize/rules', async (request, reply) => {
    const accountId = getAccountId(request);
    const rules = await sanitizationStore.getRules(accountId);
    return reply.send(rules);
  });

  /**
   * PUT /sanitize/rules
   * Update sanitization rules for the authenticated account.
   * Body: Partial<SanitizationRulesInput>
   */
  app.put('/sanitize/rules', async (request, reply) => {
    try {
      const body = request.body;
      if (!body || typeof body !== 'object' || Array.isArray(body)) {
        return reply.status(400).send({ error: 'Request body must be a JSON object' });
      }

      const accountId = getAccountId(request);
      const input = body as SanitizationRulesInput;
      const rules = await sanitizationStore.upsertRules(accountId, input);
      return reply.send(rules);
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Invalid sanitization rules';
      return reply.status(400).send({ error: message });
    }
  });
}
