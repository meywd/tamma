/**
 * Dashboard Data Routes
 *
 * Fastify plugin exposing aggregated data for the Tamma dashboard:
 *   GET /api/dashboard/summary    - high-level stats
 *   GET /api/dashboard/engines    - connected engine instances
 *   GET /api/dashboard/workflows  - definitions with instance counts
 */

import type { FastifyInstance } from 'fastify';
import type { EngineRegistry } from '../../engine-registry.js';
import type { IWorkflowStore } from '../../persistence/workflow-store.js';

export interface DashboardRouteOptions {
  engineRegistry: EngineRegistry;
  workflowStore: IWorkflowStore;
}

export async function registerDashboardRoutes(
  fastify: FastifyInstance,
  opts: DashboardRouteOptions,
): Promise<void> {
  const { engineRegistry, workflowStore } = opts;

  // ------------------------------------------------------------------
  // GET /api/dashboard/summary
  // ------------------------------------------------------------------
  fastify.get('/api/dashboard/summary', async (_request, reply) => {
    const engines = engineRegistry.list();
    const definitions = await workflowStore.listDefinitions();

    // Gather the most recent engine events for "recentEvents"
    const recentEvents: unknown[] = [];
    for (const info of engines) {
      const engine = engineRegistry.get(info.id);
      if (engine === undefined) continue;
      const store = engine.getEventStore();
      if (store === undefined) continue;
      const events = store.getEvents(engine.getTenantId());
      // Take last 10 events per engine
      recentEvents.push(
        ...events.slice(-10).map((e) => ({ ...e, engineId: info.id })),
      );
    }

    // Sort descending by timestamp, keep top 20
    recentEvents.sort((a: any, b: any) => (b.timestamp ?? 0) - (a.timestamp ?? 0));

    return reply.send({
      engineCount: engines.length,
      workflowDefinitions: definitions.length,
      recentEvents: recentEvents.slice(0, 20),
    });
  });

  // ------------------------------------------------------------------
  // GET /api/dashboard/engines
  // ------------------------------------------------------------------
  fastify.get('/api/dashboard/engines', async (_request, reply) => {
    const engines = engineRegistry.list();
    return reply.send(engines);
  });

  // ------------------------------------------------------------------
  // GET /api/dashboard/workflows
  // ------------------------------------------------------------------
  fastify.get('/api/dashboard/workflows', async (_request, reply) => {
    const definitions = await workflowStore.listDefinitions();

    const result = await Promise.all(
      definitions.map(async (def) => {
        const instances = await workflowStore.listInstances({
          definitionId: def.id,
          page: 1,
          pageSize: 0, // we only need the total count
        });

        return {
          ...def,
          instanceCount: instances.total,
        };
      }),
    );

    return reply.send(result);
  });
}
