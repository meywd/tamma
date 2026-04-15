/**
 * Diagnostics routes.
 *
 * Story 9-2: Extended with persistent store query, report, and budget endpoints.
 *
 * Backward-compatible: The original GET /diagnostics endpoint (in-memory events)
 * is still available. New endpoints operate on the persistent store.
 */

import type { FastifyInstance, FastifyRequest } from 'fastify';
import type { DiagnosticsService } from '../../services/settings/DiagnosticsService.js';
import type { IDiagnosticsStore, DiagnosticsReportOptions } from '../../services/diagnostics-store.js';
import type { DiagnosticsEventType } from '@tamma/shared';

const VALID_EVENT_TYPES = new Set<string>([
  'tool:invoke',
  'tool:complete',
  'tool:error',
  'provider:call',
  'provider:complete',
  'provider:error',
]);

const VALID_GROUP_BY = new Set(['provider', 'model', 'agentType']);

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

export function registerDiagnosticsRoutes(
  app: FastifyInstance,
  service: DiagnosticsService,
  store?: IDiagnosticsStore,
): void {
  // --- Original in-memory event list (backward-compatible) ---
  app.get('/diagnostics', async (request, reply) => {
    const query = request.query as {
      limit?: string;
      type?: string;
      since?: string;
    };

    const options: {
      limit?: number;
      type?: DiagnosticsEventType;
      since?: number;
    } = {};

    if (query.limit) {
      const limit = parseInt(query.limit, 10);
      if (Number.isFinite(limit) && limit > 0) {
        options.limit = Math.min(limit, 200);
      }
    }

    if (query.type) {
      if (!VALID_EVENT_TYPES.has(query.type)) {
        return reply.status(400).send({ error: `Invalid event type: ${query.type}` });
      }
      options.type = query.type as DiagnosticsEventType;
    }

    if (query.since) {
      const since = parseInt(query.since, 10);
      if (Number.isFinite(since)) {
        options.since = since;
      }
    }

    const events = await service.getEvents(options);
    return reply.send(events);
  });

  // --- New persistent store endpoints (9-2) ---
  if (!store) return;

  /**
   * GET /diagnostics/query
   * Query diagnostics from the persistent store with filters.
   */
  app.get('/diagnostics/query', async (request, reply) => {
    const query = request.query as {
      provider?: string;
      model?: string;
      from?: string;
      to?: string;
      limit?: string;
      offset?: string;
    };

    const accountId = getAccountId(request);

    const limit = query.limit ? parseInt(query.limit, 10) : 50;
    const offset = query.offset ? parseInt(query.offset, 10) : 0;

    if (!Number.isFinite(limit) || limit < 1 || limit > 200) {
      return reply.status(400).send({ error: 'limit must be between 1 and 200' });
    }
    if (!Number.isFinite(offset) || offset < 0) {
      return reply.status(400).send({ error: 'offset must be non-negative' });
    }

    const queryOpts: import('../../services/diagnostics-store.js').DiagnosticsQueryOptions = {
      limit,
      offset,
    };
    if (accountId !== null) queryOpts.accountId = accountId;
    if (query.provider !== undefined) queryOpts.provider = query.provider;
    if (query.model !== undefined) queryOpts.model = query.model;
    if (query.from !== undefined) queryOpts.from = query.from;
    if (query.to !== undefined) queryOpts.to = query.to;

    const result = await store.query(queryOpts);

    return reply.send(result);
  });

  /**
   * GET /diagnostics/report
   * Generate aggregated cost/usage report.
   */
  app.get('/diagnostics/report', async (request, reply) => {
    const query = request.query as {
      from?: string;
      to?: string;
      groupBy?: string;
    };

    const accountId = getAccountId(request);
    const groupBy = query.groupBy ?? 'provider';

    if (!VALID_GROUP_BY.has(groupBy)) {
      return reply.status(400).send({
        error: `Invalid groupBy value: ${groupBy}. Must be one of: provider, model, agentType`,
      });
    }

    const options: DiagnosticsReportOptions = {
      accountId,
      groupBy: groupBy as 'provider' | 'model' | 'agentType',
    };
    if (query.from) options.from = query.from;
    if (query.to) options.to = query.to;

    const groups = await store.report(options);
    return reply.send({ groups });
  });

  /**
   * GET /diagnostics/budget/:accountId
   * Check current budget status against limits.
   */
  app.get('/diagnostics/budget/:accountId', async (request, reply) => {
    const { accountId } = request.params as { accountId: string };
    const query = request.query as { limit?: string };

    if (!accountId || accountId.length === 0) {
      return reply.status(400).send({ error: 'accountId is required' });
    }

    const limitUsd = query.limit ? parseFloat(query.limit) : 100;
    if (!Number.isFinite(limitUsd) || limitUsd <= 0) {
      return reply.status(400).send({ error: 'limit must be a positive number' });
    }

    const budget = await store.getBudget(accountId, limitUsd);
    return reply.send(budget);
  });
}
