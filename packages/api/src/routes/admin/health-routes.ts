/**
 * Admin Health Routes
 *
 * GET /api/admin/health — Pings all infrastructure services and returns
 * aggregated health status. Requires admin or owner role.
 */

import type { FastifyInstance, FastifyRequest, FastifyReply } from 'fastify';

interface ServiceCheck {
  name: string;
  status: 'healthy' | 'unhealthy' | 'unknown';
  responseTime: number | null;
  checkedAt: string;
  details?: string;
}

interface HealthResult {
  services: ServiceCheck[];
  checkedAt: string;
}

async function checkHttpService(name: string, url: string): Promise<ServiceCheck> {
  const start = Date.now();
  try {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 5_000);

    const response = await fetch(url, {
      method: 'GET',
      signal: controller.signal,
    });

    clearTimeout(timeout);
    const responseTime = Date.now() - start;

    const result: ServiceCheck = {
      name,
      status: response.ok ? 'healthy' : 'unhealthy',
      responseTime,
      checkedAt: new Date().toISOString(),
    };
    if (!response.ok) {
      result.details = `HTTP ${response.status}`;
    }
    return result;
  } catch (err) {
    return {
      name,
      status: 'unhealthy' as const,
      responseTime: Date.now() - start,
      checkedAt: new Date().toISOString(),
      details: err instanceof Error ? err.message : 'Connection failed',
    };
  }
}

export interface AdminHealthOptions {
  /** PostgreSQL pool to test connectivity. */
  pgPool?: { query: (text: string) => Promise<unknown> };
}

export function registerAdminHealthRoutes(
  app: FastifyInstance,
  options?: AdminHealthOptions,
): void {
  app.get(
    '/api/admin/health',
    async (request: FastifyRequest, reply: FastifyReply) => {
      // Role check — require admin or owner via JWT cookie
      try {
        const decoded = await request.jwtVerify<{
          id: string;
          username: string;
          role: string;
        }>();
        if (decoded.role !== 'admin' && decoded.role !== 'owner') {
          return reply.status(403).send({ error: 'Admin or owner role required' });
        }
      } catch {
        return reply.status(401).send({ error: 'Not authenticated' });
      }

      // Run all health checks in parallel
      const checks: Promise<ServiceCheck>[] = [
        // Tamma API itself (self-check)
        (async (): Promise<ServiceCheck> => ({
          name: 'Tamma API',
          status: 'healthy',
          responseTime: 0,
          checkedAt: new Date().toISOString(),
        }))(),

        // PostgreSQL
        (async (): Promise<ServiceCheck> => {
          if (!options?.pgPool) {
            return {
              name: 'PostgreSQL',
              status: 'unknown',
              responseTime: null,
              checkedAt: new Date().toISOString(),
              details: 'No database pool configured',
            };
          }
          const start = Date.now();
          try {
            await options.pgPool.query('SELECT 1');
            return {
              name: 'PostgreSQL',
              status: 'healthy',
              responseTime: Date.now() - start,
              checkedAt: new Date().toISOString(),
            };
          } catch (err) {
            return {
              name: 'PostgreSQL',
              status: 'unhealthy',
              responseTime: Date.now() - start,
              checkedAt: new Date().toISOString(),
              details: err instanceof Error ? err.message : 'Connection failed',
            };
          }
        })(),

        // ELSA Server
        checkHttpService(
          'ELSA Server',
          process.env['ELSA_SERVER_URL']
            ? `${process.env['ELSA_SERVER_URL']}/health`
            : 'http://elsa-server:5000/health',
        ),

        // OpenSearch
        checkHttpService(
          'OpenSearch',
          process.env['OPENSEARCH_URL']
            ? `${process.env['OPENSEARCH_URL']}/_cluster/health`
            : 'http://opensearch:9200/_cluster/health',
        ),

        // RabbitMQ Management API
        checkHttpService(
          'RabbitMQ',
          process.env['RABBITMQ_MANAGEMENT_URL']
            ? `${process.env['RABBITMQ_MANAGEMENT_URL']}/api/health/checks/alarms`
            : 'http://rabbitmq:15672/api/health/checks/alarms',
        ),

        // ChromaDB
        checkHttpService(
          'ChromaDB',
          process.env['CHROMADB_URL']
            ? `${process.env['CHROMADB_URL']}/api/v2/heartbeat`
            : 'http://chromadb:8000/api/v2/heartbeat',
        ),
      ];

      const results = await Promise.all(checks);

      const healthResult: HealthResult = {
        services: results,
        checkedAt: new Date().toISOString(),
      };

      return reply.send(healthResult);
    },
  );
}
