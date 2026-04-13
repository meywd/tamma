/**
 * Workflow Sync Routes
 *
 * Fastify plugin that bridges ELSA workflow definitions and instances:
 *   POST   /api/workflows/definitions          - upsert a definition
 *   GET    /api/workflows/definitions          - list all definitions
 *   POST   /api/workflows/instances            - register an instance
 *   PUT    /api/workflows/instances/:id        - update instance state
 *   GET    /api/workflows/instances            - list (paginated)
 *   GET    /api/workflows/instances/:id/events - SSE stream for an instance
 *   POST   /api/workflows/instances/:id/cancel - cancel a running instance (admin+)
 *   DELETE /api/workflows/instances/:id        - delete an instance (owner only)
 *
 * RBAC enforcement:
 *   GET  (list/view)  -> requires 'workflows:view'   (member, admin, owner)
 *   POST/PUT (manage) -> requires 'workflows:manage'  (admin, owner)
 *   DELETE             -> requires 'workflows:delete'  (owner only)
 */

import { z } from 'zod';
import type { FastifyInstance } from 'fastify';
import type {
  IWorkflowStore,
  WorkflowDefinition,
  WorkflowInstance,
} from '../../persistence/workflow-store.js';
import { requirePermission } from '../../auth/require-permission.js';

// ---------------------------------------------------------------------------
// Zod Schemas
// ---------------------------------------------------------------------------

const WorkflowDefinitionSchema = z.object({
  id: z.string().min(1),
  name: z.string().min(1),
  version: z.number().optional(),
  description: z.string().optional(),
  activities: z.array(z.unknown()).optional(),
  syncedAt: z.number().optional(),
});

const WorkflowInstanceCreateSchema = z.object({
  id: z.string().optional(),
  definitionId: z.string().min(1),
  status: z.string().optional(),
  currentActivity: z.string().optional(),
  variables: z.record(z.unknown()).optional(),
  createdAt: z.number().optional(),
  updatedAt: z.number().optional(),
});

const WorkflowInstanceUpdateSchema = z.object({
  status: z.string().optional(),
  currentActivity: z.string().optional(),
  variables: z.record(z.unknown()).optional(),
});

export interface WorkflowRouteOptions {
  store: IWorkflowStore;
}

export async function registerWorkflowRoutes(
  fastify: FastifyInstance,
  opts: WorkflowRouteOptions,
): Promise<void> {
  const { store } = opts;

  // ------------------------------------------------------------------
  // POST /api/workflows/definitions  (admin+ -- manage)
  // ------------------------------------------------------------------
  fastify.post<{ Body: WorkflowDefinition }>(
    '/api/workflows/definitions',
    {
      preHandler: [requirePermission('workflows:manage')],
    },
    async (request, reply) => {
      const parsed = WorkflowDefinitionSchema.safeParse(request.body);
      if (!parsed.success) {
        return reply.status(400).send({ error: parsed.error.message });
      }
      const body = parsed.data as WorkflowDefinition;

      const existing = await store.getDefinition(body.id);
      const def = await store.upsertDefinition(body);
      return reply.status(existing ? 200 : 201).send(def);
    },
  );

  // ------------------------------------------------------------------
  // GET /api/workflows/definitions  (member+ -- view)
  // ------------------------------------------------------------------
  fastify.get('/api/workflows/definitions', {
    preHandler: [requirePermission('workflows:view')],
  }, async (_request, reply) => {
    const defs = await store.listDefinitions();
    return reply.send(defs);
  });

  // ------------------------------------------------------------------
  // POST /api/workflows/instances  (admin+ -- manage)
  // ------------------------------------------------------------------
  fastify.post<{ Body: WorkflowInstance }>(
    '/api/workflows/instances',
    {
      preHandler: [requirePermission('workflows:manage')],
    },
    async (request, reply) => {
      const parsed = WorkflowInstanceCreateSchema.safeParse(request.body);
      if (!parsed.success) {
        return reply.status(400).send({ error: parsed.error.message });
      }
      const body = parsed.data as WorkflowInstance;

      const instance = await store.createInstance(body);
      return reply.status(201).send(instance);
    },
  );

  // ------------------------------------------------------------------
  // PUT /api/workflows/instances/:id  (admin+ -- manage)
  // ------------------------------------------------------------------
  fastify.put<{ Params: { id: string }; Body: Partial<WorkflowInstance> }>(
    '/api/workflows/instances/:id',
    {
      preHandler: [requirePermission('workflows:manage')],
    },
    async (request, reply) => {
      const { id } = request.params;
      const parsed = WorkflowInstanceUpdateSchema.safeParse(request.body);
      if (!parsed.success) {
        return reply.status(400).send({ error: parsed.error.message });
      }
      // Strip id and definitionId to prevent overwriting immutable fields.
      // Build update with only defined fields.
      const update: Partial<WorkflowInstance> = {};
      if (parsed.data.status !== undefined) update.status = parsed.data.status;
      if (parsed.data.currentActivity !== undefined) update.currentActivity = parsed.data.currentActivity;
      if (parsed.data.variables !== undefined) update.variables = parsed.data.variables;

      const instance = await store.updateInstance(id, update);
      if (instance === null) {
        return reply.status(404).send({ error: 'Instance not found' });
      }

      return reply.send(instance);
    },
  );

  // ------------------------------------------------------------------
  // GET /api/workflows/instances  (member+ -- view)
  // ------------------------------------------------------------------
  fastify.get<{
    Querystring: {
      page?: string;
      pageSize?: string;
      definitionId?: string;
    };
  }>(
    '/api/workflows/instances',
    {
      preHandler: [requirePermission('workflows:view')],
    },
    async (request, reply) => {
      const page = Math.max(
        1,
        parseInt(request.query.page ?? '1', 10) || 1,
      );
      const pageSize = Math.min(
        200,
        Math.max(1, parseInt(request.query.pageSize ?? '50', 10) || 50),
      );
      const definitionId = request.query.definitionId || undefined;

      const result = await store.listInstances({
        page,
        pageSize,
        ...(definitionId !== undefined ? { definitionId } : {}),
      });
      return reply.send({ ...result, page, pageSize });
    },
  );

  // ------------------------------------------------------------------
  // POST /api/workflows/instances/:id/cancel  (admin+ -- manage)
  // ------------------------------------------------------------------
  fastify.post<{ Params: { id: string } }>(
    '/api/workflows/instances/:id/cancel',
    {
      preHandler: [requirePermission('workflows:manage')],
    },
    async (request, reply) => {
      const { id } = request.params;

      const instance = await store.getInstance(id);
      if (instance === null) {
        return reply.status(404).send({ error: 'Instance not found' });
      }

      if (instance.status === 'cancelled') {
        return reply.send({ ...instance, message: 'Instance already cancelled' });
      }

      const updated = await store.updateInstance(id, { status: 'cancelled' });

      request.log.info(
        { instanceId: id, previousStatus: instance.status },
        'Workflow instance cancelled',
      );

      return reply.send(updated);
    },
  );

  // ------------------------------------------------------------------
  // DELETE /api/workflows/instances/:id  (owner only -- delete)
  // ------------------------------------------------------------------
  fastify.delete<{ Params: { id: string } }>(
    '/api/workflows/instances/:id',
    {
      preHandler: [requirePermission('workflows:delete')],
    },
    async (request, reply) => {
      const { id } = request.params;

      const instance = await store.getInstance(id);
      if (instance === null) {
        return reply.status(404).send({ error: 'Instance not found' });
      }

      await store.deleteInstance(id);

      request.log.info(
        { instanceId: id, definitionId: instance.definitionId },
        'Workflow instance deleted',
      );

      return reply.send({ ok: true });
    },
  );

  // ------------------------------------------------------------------
  // GET /api/workflows/instances/:id/events  (SSE, member+ -- view)
  // ------------------------------------------------------------------
  fastify.get<{ Params: { id: string } }>(
    '/api/workflows/instances/:id/events',
    {
      preHandler: [requirePermission('workflows:view')],
    },
    async (request, reply) => {
      const { id } = request.params;

      const instance = await store.getInstance(id);
      if (instance === null) {
        return reply.status(404).send({ error: 'Instance not found' });
      }

      reply.hijack();

      // SSE headers
      reply.raw.writeHead(200, {
        'Content-Type': 'text/event-stream',
        'Cache-Control': 'no-cache',
        Connection: 'keep-alive',
        'X-Accel-Buffering': 'no',
      });

      // Send initial state
      reply.raw.write(
        `event: state\ndata: ${JSON.stringify(instance)}\n\n`,
      );

      // Poll for updates
      let lastUpdatedAt = instance.updatedAt;

      const interval = setInterval(async () => {
        try {
          const current = await store.getInstance(id);
          if (current !== null && current.updatedAt > lastUpdatedAt) {
            lastUpdatedAt = current.updatedAt;
            reply.raw.write(
              `event: state\ndata: ${JSON.stringify(current)}\n\n`,
            );
          }
        } catch {
          clearInterval(interval);
          clearInterval(heartbeat);
        }
      }, 1000);

      // Heartbeat to keep connection alive through reverse proxies
      const heartbeat = setInterval(() => {
        try {
          reply.raw.write(':heartbeat\n\n');
        } catch {
          clearInterval(interval);
          clearInterval(heartbeat);
        }
      }, 15_000);

      reply.raw.on('close', () => {
        clearInterval(interval);
        clearInterval(heartbeat);
      });
    },
  );
}
