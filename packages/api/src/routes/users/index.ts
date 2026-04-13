/**
 * User Management Routes Registration
 *
 * Registers all user management routes under /api/admin/users:
 *   - User CRUD (list, get, update role, soft delete)
 *   - Per-user API key management
 *   - User invitation flow
 *
 * All routes are rate-limited to 30 requests/minute per IP.
 */

import type { FastifyInstance } from 'fastify';
import type { IUserStore } from '../../persistence/user-store.js';
import type { IUserApiKeyStore } from '../../persistence/user-api-key-store.js';
import type { IInviteStore } from '../../persistence/invite-store.js';
import { registerUserRoutes } from './user-routes.js';
import { registerApiKeyRoutes } from './api-key-routes.js';
import { registerInviteRoutes } from './invite-routes.js';

export interface UserManagementRouteOptions {
  userStore: IUserStore;
  apiKeyStore: IUserApiKeyStore;
  inviteStore: IInviteStore;
  dashboardUrl: string;
}

/** Rate limit: 30 requests per minute per IP for user management routes. */
const USER_MGMT_RATE_LIMIT = { max: 30, timeWindow: '1 minute' };

export async function registerUserManagementRoutes(
  app: FastifyInstance,
  options: UserManagementRouteOptions,
): Promise<void> {
  // Register rate limiting scoped to user management routes
  await app.register(
    async (scoped) => {
      await scoped.register((await import('@fastify/rate-limit')).default, {
        ...USER_MGMT_RATE_LIMIT,
        keyGenerator: (request) => request.ip,
      });

      await registerUserRoutes(scoped, {
        userStore: options.userStore,
        apiKeyStore: options.apiKeyStore,
      });

      await registerApiKeyRoutes(scoped, {
        userStore: options.userStore,
        apiKeyStore: options.apiKeyStore,
      });

      await registerInviteRoutes(scoped, {
        inviteStore: options.inviteStore,
        dashboardUrl: options.dashboardUrl,
      });
    },
  );
}
