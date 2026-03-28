/**
 * Admin Routes
 *
 * Registers all admin-related routes:
 * - /api/admin/health              — System health aggregation
 * - /api/admin/users               — User management (CRUD)
 * - /api/admin/users/:id/keys      — Per-user API key management
 * - /api/admin/users/invite(s)     — User invitation flow
 */

import type { FastifyInstance } from 'fastify';
import { registerAdminHealthRoutes } from './health-routes.js';
import type { AdminHealthOptions } from './health-routes.js';
import { registerAdminUserRoutes } from './user-routes.js';
import type { AdminUserRoutesOptions } from './user-routes.js';
import { registerAdminApiKeyRoutes } from './api-key-routes.js';
import type { AdminApiKeyRoutesOptions } from './api-key-routes.js';
import { registerAdminInviteRoutes } from './invite-routes.js';
import type { AdminInviteRoutesOptions } from './invite-routes.js';
import type { IUserStore } from '../../persistence/user-store.js';
import type { IUserApiKeyStore } from '../../persistence/user-api-key-store.js';
import type { IInviteStore } from '../../persistence/invite-store.js';

export interface AdminRouteOptions {
  /** PostgreSQL pool for health checks. */
  pgPool?: AdminHealthOptions['pgPool'];
  /** User store for user management routes. */
  userStore: IUserStore;
  /** API key store for per-user key management. */
  apiKeyStore: IUserApiKeyStore;
  /** Invite store for user invitations. */
  inviteStore: IInviteStore;
  /** Dashboard URL for invite links. */
  dashboardUrl: string;
}

export async function registerAdminRoutes(
  app: FastifyInstance,
  options: AdminRouteOptions,
): Promise<void> {
  const healthOptions: AdminHealthOptions = {};
  if (options.pgPool) {
    healthOptions.pgPool = options.pgPool;
  }
  registerAdminHealthRoutes(app, healthOptions);

  await registerAdminUserRoutes(app, {
    userStore: options.userStore,
    apiKeyStore: options.apiKeyStore,
  });

  await registerAdminApiKeyRoutes(app, {
    userStore: options.userStore,
    apiKeyStore: options.apiKeyStore,
  });

  await registerAdminInviteRoutes(app, {
    inviteStore: options.inviteStore,
    dashboardUrl: options.dashboardUrl,
  });
}

export type { AdminHealthOptions } from './health-routes.js';
export type { AdminUserRoutesOptions } from './user-routes.js';
export type { AdminApiKeyRoutesOptions } from './api-key-routes.js';
export type { AdminInviteRoutesOptions } from './invite-routes.js';
