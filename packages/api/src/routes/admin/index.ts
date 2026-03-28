/**
 * Admin Routes
 *
 * Registers admin-only routes that are NOT already covered by
 * registerUserManagementRoutes() from routes/users/:
 *
 * - GET /api/admin/health — System health aggregation across all infrastructure services
 *
 * User management routes (/api/admin/users/*, keys, invites) are registered
 * via the `userManagement` option in createApp and live in routes/users/.
 */

import type { FastifyInstance } from 'fastify';
import { registerAdminHealthRoutes } from './health-routes.js';
import type { AdminHealthOptions } from './health-routes.js';

export interface AdminRouteOptions {
  /** PostgreSQL pool for health checks. */
  pgPool?: AdminHealthOptions['pgPool'];
}

export async function registerAdminRoutes(
  app: FastifyInstance,
  options?: AdminRouteOptions,
): Promise<void> {
  const healthOptions: AdminHealthOptions = {};
  if (options?.pgPool) {
    healthOptions.pgPool = options.pgPool;
  }
  registerAdminHealthRoutes(app, healthOptions);
}

export type { AdminHealthOptions } from './health-routes.js';
