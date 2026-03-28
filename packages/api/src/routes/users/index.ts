/**
 * User Management Routes
 *
 * Placeholder module for Story 16.2 user management routes.
 * Registers admin-level user CRUD, invite, and API key management routes.
 */

import type { FastifyInstance } from 'fastify';
import type { IUserStore } from '../../persistence/user-store.js';
import type { IUserApiKeyStore } from '../../persistence/user-api-key-store.js';
import type { IInviteStore } from '../../persistence/invite-store.js';

export interface UserManagementRouteOptions {
  userStore: IUserStore;
  apiKeyStore: IUserApiKeyStore;
  inviteStore: IInviteStore;
  dashboardUrl: string;
}

export async function registerUserManagementRoutes(
  _app: FastifyInstance,
  _options: UserManagementRouteOptions,
): Promise<void> {
  // TODO: Implement user management routes (Story 16.2)
  // Routes will include:
  //   GET    /api/admin/users
  //   PUT    /api/admin/users/:id/role
  //   DELETE /api/admin/users/:id
  //   POST   /api/admin/users/invite
  //   GET    /api/admin/api-keys
  //   POST   /api/admin/api-keys
  //   DELETE /api/admin/api-keys/:id
}
