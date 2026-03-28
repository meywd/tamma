/**
 * User Management Routes
 *
 * CRUD operations for platform user management:
 *   GET    /api/admin/users          — list users (admin+)
 *   GET    /api/admin/users/:id      — get user detail (admin+ or self)
 *   PUT    /api/admin/users/:id/role — update role (admin+, owner-only for promotion)
 *   DELETE /api/admin/users/:id      — soft-delete (owner only)
 */

import type { FastifyInstance, FastifyRequest } from 'fastify';
import type { IUserStore } from '../../persistence/user-store.js';
import type { IUserApiKeyStore } from '../../persistence/user-api-key-store.js';
import { requireRole, requireSelfOrRole } from '../../middleware/require-role.js';
import type { AuthenticatedUser } from '../../middleware/require-role.js';

export interface UserRouteOptions {
  userStore: IUserStore;
  apiKeyStore: IUserApiKeyStore;
}

export async function registerUserRoutes(
  app: FastifyInstance,
  options: UserRouteOptions,
): Promise<void> {
  const { userStore, apiKeyStore } = options;

  // GET /api/admin/users — list all users (admin+)
  app.get('/api/admin/users', {
    preHandler: [requireRole('admin')],
  }, async (request, reply) => {
    const query = request.query as { limit?: string; offset?: string; role?: string };
    const limit = Math.min(Math.max(parseInt(query.limit ?? '50', 10) || 50, 1), 100);
    const offset = Math.max(parseInt(query.offset ?? '0', 10) || 0, 0);

    const roleFilter = query.role;
    const validRoles = new Set(['owner', 'admin', 'member']);

    const listOptions: { limit: number; offset: number; role?: 'owner' | 'admin' | 'member' } = {
      limit,
      offset,
    };
    if (roleFilter && validRoles.has(roleFilter)) {
      listOptions.role = roleFilter as 'owner' | 'admin' | 'member';
    }

    const result = await userStore.listUsers(listOptions);
    return reply.send(result);
  });

  // GET /api/admin/users/:id — get single user (admin+ or self)
  app.get('/api/admin/users/:id', {
    preHandler: [requireSelfOrRole('admin')],
  }, async (request, reply) => {
    const { id } = request.params as { id: string };
    const user = await userStore.getUser(id);

    if (!user) {
      return reply.status(404).send({ error: 'User not found' });
    }

    const installations = await userStore.getUserInstallations(id);
    const apiKeys = await apiKeyStore.listApiKeys(id);
    return reply.send({ user, installations, apiKeys });
  });

  // PUT /api/admin/users/:id/role — update role (admin+)
  app.put('/api/admin/users/:id/role', {
    preHandler: [requireRole('admin')],
  }, async (request, reply) => {
    const { id } = request.params as { id: string };
    const body = request.body as { role?: string } | null;
    const role = body?.role;
    const authUser = (request as FastifyRequest & { authUser: AuthenticatedUser }).authUser;

    if (!role || !['owner', 'admin', 'member'].includes(role)) {
      return reply.status(400).send({ error: 'Invalid role. Must be one of: owner, admin, member' });
    }

    // Only owners can promote to admin or owner
    if ((role === 'admin' || role === 'owner') && authUser.role !== 'owner') {
      return reply.status(403).send({ error: 'Only owners can promote to admin or owner' });
    }

    // Cannot change your own role
    if (id === authUser.id) {
      return reply.status(400).send({ error: 'Cannot change your own role' });
    }

    // Verify target user exists
    const targetUser = await userStore.getUser(id);
    if (!targetUser) {
      return reply.status(404).send({ error: 'User not found' });
    }

    const updated = await userStore.updateUserRole(id, role as 'owner' | 'admin' | 'member');
    return reply.send({ user: updated });
  });

  // DELETE /api/admin/users/:id — soft delete (owner only)
  app.delete('/api/admin/users/:id', {
    preHandler: [requireRole('owner')],
  }, async (request, reply) => {
    const { id } = request.params as { id: string };
    const authUser = (request as FastifyRequest & { authUser: AuthenticatedUser }).authUser;

    if (id === authUser.id) {
      return reply.status(400).send({ error: 'Cannot delete yourself' });
    }

    // Verify target user exists
    const targetUser = await userStore.getUser(id);
    if (!targetUser) {
      return reply.status(404).send({ error: 'User not found' });
    }

    // Soft-delete user and revoke all their API keys
    await userStore.deleteUser(id);
    await apiKeyStore.revokeAllForUser(id);

    return reply.send({ ok: true });
  });
}
