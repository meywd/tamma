/**
 * Provider health routes.
 *
 * Story 9-3: Extended with persistent health store endpoints.
 *
 * Backward-compatible: The original GET /health endpoint (in-memory tracker)
 * is still available. New endpoints operate on the persistent store.
 */

import type { FastifyInstance } from 'fastify';
import type { HealthService } from '../../services/settings/HealthService.js';
import type { IHealthStore, RecordFailureInput } from '../../services/health-store.js';

const KEY_PATTERN = /^[a-zA-Z0-9._\-:/]+$/;
const MAX_KEY_LENGTH = 256;

function validateKeyParam(key: string): string | null {
  if (!key || key.length === 0) return 'key must not be empty';
  if (key.length > MAX_KEY_LENGTH) return `key too long (max ${MAX_KEY_LENGTH})`;
  if (!KEY_PATTERN.test(key)) return 'key contains invalid characters';
  return null;
}

export function registerHealthRoutes(
  app: FastifyInstance,
  service: HealthService,
  store?: IHealthStore,
): void {
  // --- Original in-memory endpoint (backward-compatible) ---
  app.get('/health', async (_request, reply) => {
    const status = await service.getStatus();
    return reply.send(status);
  });

  // --- New persistent store endpoints (9-3) ---
  if (!store) return;

  /**
   * GET /health/providers
   * Returns health status for all tracked provider+model keys.
   */
  app.get('/health/providers', async (_request, reply) => {
    const status = await store.getAll();
    return reply.send(status);
  });

  /**
   * GET /health/providers/:key
   * Returns health status for a specific key.
   */
  app.get('/health/providers/:key', async (request, reply) => {
    const { key } = request.params as { key: string };

    const error = validateKeyParam(key);
    if (error) {
      return reply.status(400).send({ error });
    }

    const status = await store.get(key);
    if (!status) {
      // Unknown keys are considered healthy (no failures recorded)
      return reply.send({
        healthy: true,
        failures: 0,
        circuitOpen: false,
        circuitOpenUntil: null,
        halfOpen: false,
      });
    }
    return reply.send(status);
  });

  /**
   * POST /health/providers/:key/failure
   * Record a failure for a key. May open the circuit.
   */
  app.post('/health/providers/:key/failure', async (request, reply) => {
    const { key } = request.params as { key: string };

    const error = validateKeyParam(key);
    if (error) {
      return reply.status(400).send({ error });
    }

    const body = (request.body ?? {}) as RecordFailureInput;
    const result = await store.recordFailure(key, body);
    return reply.send(result);
  });

  /**
   * POST /health/providers/:key/success
   * Record a success. Closes the circuit.
   */
  app.post('/health/providers/:key/success', async (request, reply) => {
    const { key } = request.params as { key: string };

    const error = validateKeyParam(key);
    if (error) {
      return reply.status(400).send({ error });
    }

    const result = await store.recordSuccess(key);
    return reply.send(result);
  });

  /**
   * POST /health/providers/:key/reset
   * Manually reset circuit breaker (admin only).
   */
  app.post('/health/providers/:key/reset', async (request, reply) => {
    const { key } = request.params as { key: string };

    const error = validateKeyParam(key);
    if (error) {
      return reply.status(400).send({ error });
    }

    const wasReset = await store.reset(key);
    return reply.send({ reset: wasReset });
  });
}
