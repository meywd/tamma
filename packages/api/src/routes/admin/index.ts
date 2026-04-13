/**
 * Admin Routes
 *
 * Registers admin-only routes that are NOT already covered by
 * registerUserManagementRoutes() from routes/users/:
 *
 * - GET /api/admin/health — System health aggregation across all infrastructure services
 * - POST/GET/DELETE /api/admin/service-keys — Service-to-service API key management
 *
 * User management routes (/api/admin/users/*, keys, invites) are registered
 * via the `userManagement` option in createApp and live in routes/users/.
 */

import type { FastifyInstance } from 'fastify';
import { registerAdminHealthRoutes } from './health-routes.js';
import type { AdminHealthOptions } from './health-routes.js';
import { registerServiceKeyRoutes } from './service-keys.js';
import type { IApiKeyStore } from '../../persistence/api-key-store.js';

export interface AdminRouteOptions {
  /** PostgreSQL pool for health checks. */
  pgPool?: AdminHealthOptions['pgPool'];
  /** Unified API key store for service key management. */
  unifiedApiKeyStore?: IApiKeyStore;
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

  // Service key management routes (only if store is provided)
  if (options?.unifiedApiKeyStore) {
    await registerServiceKeyRoutes(app, { apiKeyStore: options.unifiedApiKeyStore });
  }
}

export type { AdminHealthOptions } from './health-routes.js';
export type { ServiceKeyRouteOptions } from './service-keys.js';
