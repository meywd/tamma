/**
 * Diagnostics Ingest Routes
 *
 * Story 9-2: POST endpoint for recording diagnostics events.
 * Used by Elsa workflows and any external caller.
 */

import type { FastifyInstance } from 'fastify';
import type { IDiagnosticsStore, DiagnosticsRecordInput } from '../../services/diagnostics-store.js';

export function registerDiagnosticsIngestRoutes(
  app: FastifyInstance,
  store: IDiagnosticsStore,
): void {
  /**
   * POST /diagnostics
   * Record one or more diagnostics events.
   * Body: DiagnosticsRecordInput | DiagnosticsRecordInput[]
   */
  app.post('/diagnostics', async (request, reply) => {
    try {
      const body = request.body;
      if (!body || typeof body !== 'object') {
        return reply.status(400).send({ error: 'Request body must be a JSON object or array' });
      }

      const inputs: DiagnosticsRecordInput[] = Array.isArray(body) ? body : [body as DiagnosticsRecordInput];

      if (inputs.length === 0) {
        return reply.status(400).send({ error: 'At least one diagnostics record is required' });
      }

      const recorded = await store.insert(inputs);
      return reply.status(201).send({ recorded });
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Failed to record diagnostics';
      return reply.status(400).send({ error: message });
    }
  });
}
