/**
 * User Management Routes Registration
 *
 * Registers all user management routes under /api/admin/users:
 *   - User CRUD (list, get, update role, soft delete)
 *   - Per-user API key management
 *   - User invitation flow
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

export async function registerUserManagementRoutes(
  app: FastifyInstance,
  options: UserManagementRouteOptions,
): Promise<void> {
  await registerUserRoutes(app, {
    userStore: options.userStore,
    apiKeyStore: options.apiKeyStore,
  });

  await registerApiKeyRoutes(app, {
    userStore: options.userStore,
    apiKeyStore: options.apiKeyStore,
  });

  await registerInviteRoutes(app, {
    inviteStore: options.inviteStore,
    dashboardUrl: options.dashboardUrl,
  });
}
