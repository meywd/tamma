/**
 * Invite Flow Routes
 *
 * Manage user invitations:
 *   POST   /api/admin/users/invite   — create invitation (admin+)
 *   GET    /api/admin/users/invites  — list pending invitations (admin+)
 *   DELETE /api/admin/users/invites/:id — revoke invitation (admin+)
 */

import type { FastifyInstance, FastifyRequest } from 'fastify';
import type { IInviteStore } from '../../persistence/invite-store.js';
import { requireRole } from '../../middleware/require-role.js';
import type { AuthenticatedUser } from '../../middleware/require-role.js';
import { randomBytes } from 'node:crypto';

export interface InviteRouteOptions {
  inviteStore: IInviteStore;
  dashboardUrl: string;
}

/** Default invite expiry: 72 hours in milliseconds. */
const INVITE_EXPIRY_MS = 72 * 60 * 60 * 1000;

export async function registerInviteRoutes(
  app: FastifyInstance,
  options: InviteRouteOptions,
): Promise<void> {
  const { inviteStore, dashboardUrl } = options;

  // POST /api/admin/users/invite — create invitation (admin+)
  app.post('/api/admin/users/invite', {
    preHandler: [requireRole('admin')],
  }, async (request, reply) => {
    const body = request.body as { email?: string; role?: string } | null;
    const email = body?.email ?? null;
    const inviteRole = body?.role ?? 'member';
    const authUser = (request as FastifyRequest & { authUser: AuthenticatedUser }).authUser;

    if (!['owner', 'admin', 'member'].includes(inviteRole)) {
      return reply.status(400).send({ error: 'Invalid role. Must be one of: owner, admin, member' });
    }

    // Validate email format if provided
    if (email !== null && email !== undefined) {
      const emailRegex = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
      if (!emailRegex.test(email)) {
        return reply.status(400).send({ error: 'Invalid email format' });
      }
    }

    // Only owners can invite admins/owners
    if ((inviteRole === 'admin' || inviteRole === 'owner') && authUser.role !== 'owner') {
      return reply.status(403).send({ error: 'Only owners can invite admin/owner roles' });
    }

    const token = randomBytes(32).toString('base64url');
    const expiresAt = new Date(Date.now() + INVITE_EXPIRY_MS).toISOString();

    const invite = await inviteStore.createInvite({
      email,
      role: inviteRole as 'owner' | 'admin' | 'member',
      inviteToken: token,
      invitedBy: authUser.id,
      expiresAt,
    });

    const inviteLink = `${dashboardUrl}/invite/${token}`;

    return reply.status(201).send({
      id: invite.id,
      inviteLink,
      role: inviteRole,
      expiresAt,
    });
  });

  // GET /api/admin/users/invites — list pending invitations (admin+)
  app.get('/api/admin/users/invites', {
    preHandler: [requireRole('admin')],
  }, async (_request, reply) => {
    const invites = await inviteStore.listPendingInvites();
    return reply.send({ invites });
  });

  // DELETE /api/admin/users/invites/:id — revoke invitation (admin+)
  app.delete('/api/admin/users/invites/:id', {
    preHandler: [requireRole('admin')],
  }, async (request, reply) => {
    const { id } = request.params as { id: string };

    try {
      await inviteStore.revokeInvite(id);
    } catch {
      return reply.status(404).send({ error: 'Invite not found' });
    }

    return reply.send({ ok: true });
  });
}
