/**
 * Convention Templates Routes
 *
 * Public endpoints for listing and retrieving coding convention starter templates.
 * No authentication required — these are read-only reference data.
 *
 * Routes:
 *   GET /api/convention-templates           — list all templates (key, name, description)
 *   GET /api/convention-templates/:key      — get full template with conventions string
 */

import type { FastifyInstance, FastifyRequest, FastifyReply } from 'fastify';
import {
  listConventionTemplates,
  getConventionTemplate,
} from '../services/convention-templates.js';

export async function registerConventionTemplateRoutes(
  app: FastifyInstance,
): Promise<void> {
  // GET /api/convention-templates — list all templates
  app.get(
    '/api/convention-templates',
    async (_request: FastifyRequest, reply: FastifyReply) => {
      const templates = listConventionTemplates();
      return reply.send(templates);
    },
  );

  // GET /api/convention-templates/:key — get full template by key
  app.get(
    '/api/convention-templates/:key',
    async (
      request: FastifyRequest<{ Params: { key: string } }>,
      reply: FastifyReply,
    ) => {
      const { key } = request.params;
      const template = getConventionTemplate(key);

      if (!template) {
        return reply.status(404).send({
          error: `Convention template "${key}" not found`,
        });
      }

      return reply.send({ key, ...template });
    },
  );
}
