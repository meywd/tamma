/**
 * Invite Flow Routes Tests
 *
 * Tests invitation lifecycle:
 *   POST   /api/admin/users/invite
 *   GET    /api/admin/users/invites
 *   DELETE /api/admin/users/invites/:id
 */

import { describe, it, expect, beforeEach } from 'vitest';
import Fastify from 'fastify';
import type { FastifyInstance } from 'fastify';
import { Writable } from 'node:stream';
import { registerInviteRoutes } from '../invite-routes.js';
import { InMemoryInviteStore } from '../../../persistence/invite-store.js';

/** Collects structured log lines for audit verification. */
function createLogCollector(): { stream: Writable; lines: Record<string, unknown>[] } {
  const lines: Record<string, unknown>[] = [];
  const stream = new Writable({
    write(chunk: Buffer, _encoding: string, callback: () => void) {
      try {
        lines.push(JSON.parse(chunk.toString()) as Record<string, unknown>);
      } catch {
        // ignore non-JSON lines
      }
      callback();
    },
  });
  return { stream, lines };
}

function createTestApp(authUser: { id: string; role: string } | null = null, logStream?: Writable) {
  const appOptions: Record<string, unknown> = {};
  if (logStream) {
    appOptions['logger'] = { stream: logStream, level: 'info' };
  }
  const app = Fastify(appOptions);
  const inviteStore = new InMemoryInviteStore();

  app.decorateRequest('authUser', null);

  if (authUser) {
    app.addHook('onRequest', async (request) => {
      (request as unknown as { authUser: typeof authUser }).authUser = authUser;
    });
  }

  return { app, inviteStore };
}

const DASHBOARD_URL = 'https://app.tamma.dev';

async function setupRoutes(app: FastifyInstance, inviteStore: InstanceType<typeof InMemoryInviteStore>) {
  await registerInviteRoutes(app, { inviteStore, dashboardUrl: DASHBOARD_URL });
  await app.ready();
}

describe('Invite Routes', () => {
  describe('POST /api/admin/users/invite', () => {
    it('admin can create an invite with member role', async () => {
      const { app, inviteStore } = createTestApp({ id: 'admin-1', role: 'admin' });
      await setupRoutes(app, inviteStore);

      const res = await app.inject({
        method: 'POST',
        url: '/api/admin/users/invite',
        payload: { email: 'new@example.com', role: 'member' },
      });

      expect(res.statusCode).toBe(201);
      const body = res.json();
      expect(body.id).toBeDefined();
      expect(body.inviteLink).toMatch(/^https:\/\/app\.tamma\.dev\/invite\//);
      expect(body.role).toBe('member');
      expect(body.expiresAt).toBeDefined();
    });

    it('owner can create an invite with admin role', async () => {
      const { app, inviteStore } = createTestApp({ id: 'owner-1', role: 'owner' });
      await setupRoutes(app, inviteStore);

      const res = await app.inject({
        method: 'POST',
        url: '/api/admin/users/invite',
        payload: { role: 'admin' },
      });

      expect(res.statusCode).toBe(201);
      expect(res.json().role).toBe('admin');
    });

    it('admin cannot invite admin/owner roles', async () => {
      const { app, inviteStore } = createTestApp({ id: 'admin-1', role: 'admin' });
      await setupRoutes(app, inviteStore);

      const res = await app.inject({
        method: 'POST',
        url: '/api/admin/users/invite',
        payload: { role: 'admin' },
      });

      expect(res.statusCode).toBe(403);
      expect(res.json().error).toContain('Only owners');
    });

    it('defaults to member role when none specified', async () => {
      const { app, inviteStore } = createTestApp({ id: 'admin-1', role: 'admin' });
      await setupRoutes(app, inviteStore);

      const res = await app.inject({
        method: 'POST',
        url: '/api/admin/users/invite',
        payload: {},
      });

      expect(res.statusCode).toBe(201);
      expect(res.json().role).toBe('member');
    });

    it('rejects invalid role', async () => {
      const { app, inviteStore } = createTestApp({ id: 'admin-1', role: 'admin' });
      await setupRoutes(app, inviteStore);

      const res = await app.inject({
        method: 'POST',
        url: '/api/admin/users/invite',
        payload: { role: 'superadmin' },
      });

      expect(res.statusCode).toBe(400);
    });

    it('rejects invalid email format', async () => {
      const { app, inviteStore } = createTestApp({ id: 'admin-1', role: 'admin' });
      await setupRoutes(app, inviteStore);

      const res = await app.inject({
        method: 'POST',
        url: '/api/admin/users/invite',
        payload: { email: 'not-an-email' },
      });

      expect(res.statusCode).toBe(400);
      expect(res.json().error).toContain('email');
    });

    it('handles adversarial email input without hanging (ReDoS protection)', async () => {
      const { app, inviteStore } = createTestApp({ id: 'admin-1', role: 'admin' });
      await setupRoutes(app, inviteStore);

      // This input causes catastrophic backtracking with polynomial/exponential regexes
      const malicious = '!@' + '!.'.repeat(50);

      const start = Date.now();
      const res = await app.inject({
        method: 'POST',
        url: '/api/admin/users/invite',
        payload: { email: malicious },
      });
      const elapsed = Date.now() - start;

      expect(res.statusCode).toBe(400);
      // Must respond in under 100ms, not hang for seconds
      expect(elapsed).toBeLessThan(100);
    });

    it('member cannot create invites', async () => {
      const { app, inviteStore } = createTestApp({ id: 'member-1', role: 'member' });
      await setupRoutes(app, inviteStore);

      const res = await app.inject({
        method: 'POST',
        url: '/api/admin/users/invite',
        payload: { role: 'member' },
      });

      expect(res.statusCode).toBe(403);
    });

    it('unauthenticated request returns 401', async () => {
      const { app, inviteStore } = createTestApp(null);
      await setupRoutes(app, inviteStore);

      const res = await app.inject({
        method: 'POST',
        url: '/api/admin/users/invite',
        payload: { role: 'member' },
      });

      expect(res.statusCode).toBe(401);
    });
  });

  describe('GET /api/admin/users/invites', () => {
    let app: FastifyInstance;
    let inviteStore: InstanceType<typeof InMemoryInviteStore>;

    beforeEach(async () => {
      const ctx = createTestApp({ id: 'admin-1', role: 'admin' });
      app = ctx.app;
      inviteStore = ctx.inviteStore;
      await setupRoutes(app, inviteStore);
    });

    it('lists pending invitations', async () => {
      // Create an invite first
      await app.inject({
        method: 'POST',
        url: '/api/admin/users/invite',
        payload: { email: 'alice@example.com', role: 'member' },
      });

      const res = await app.inject({
        method: 'GET',
        url: '/api/admin/users/invites',
      });

      expect(res.statusCode).toBe(200);
      const body = res.json();
      expect(body.invites).toHaveLength(1);
      expect(body.invites[0].email).toBe('alice@example.com');
    });

    it('returns empty array when no invites', async () => {
      const res = await app.inject({
        method: 'GET',
        url: '/api/admin/users/invites',
      });

      expect(res.statusCode).toBe(200);
      expect(res.json().invites).toEqual([]);
    });
  });

  describe('DELETE /api/admin/users/invites/:id', () => {
    it('revokes an invitation', async () => {
      const { app, inviteStore } = createTestApp({ id: 'admin-1', role: 'admin' });
      await setupRoutes(app, inviteStore);

      // Create an invite
      const createRes = await app.inject({
        method: 'POST',
        url: '/api/admin/users/invite',
        payload: { email: 'revoke-me@example.com' },
      });
      const inviteId = createRes.json().id;

      // Revoke it
      const revokeRes = await app.inject({
        method: 'DELETE',
        url: `/api/admin/users/invites/${inviteId}`,
      });
      expect(revokeRes.statusCode).toBe(200);
      expect(revokeRes.json().ok).toBe(true);

      // Verify it's gone
      const listRes = await app.inject({
        method: 'GET',
        url: '/api/admin/users/invites',
      });
      expect(listRes.json().invites).toHaveLength(0);
    });

    it('returns 404 for nonexistent invite', async () => {
      const { app, inviteStore } = createTestApp({ id: 'admin-1', role: 'admin' });
      await setupRoutes(app, inviteStore);

      const res = await app.inject({
        method: 'DELETE',
        url: '/api/admin/users/invites/nonexistent',
      });
      expect(res.statusCode).toBe(404);
    });
  });

  describe('Audit logging', () => {
    it('emits USER.INVITED.SUCCESS on invite creation', async () => {
      const { stream, lines } = createLogCollector();
      const { app, inviteStore } = createTestApp({ id: 'admin-1', role: 'admin' }, stream);
      await setupRoutes(app, inviteStore);

      await app.inject({
        method: 'POST',
        url: '/api/admin/users/invite',
        payload: { email: 'new@example.com', role: 'member' },
      });

      const auditLine = lines.find((l) => l['event'] === 'USER.INVITED.SUCCESS');
      expect(auditLine).toBeDefined();
      expect(auditLine!['inviteId']).toBeDefined();
      expect(auditLine!['email']).toBe('new@example.com');
      expect(auditLine!['role']).toBe('member');
      expect(auditLine!['invitedBy']).toBe('admin-1');
      // Must not log the invite token
      expect(auditLine).not.toHaveProperty('token');
      expect(auditLine).not.toHaveProperty('inviteToken');
    });

    it('does not include email in audit log when null', async () => {
      const { stream, lines } = createLogCollector();
      const { app, inviteStore } = createTestApp({ id: 'admin-1', role: 'admin' }, stream);
      await setupRoutes(app, inviteStore);

      await app.inject({
        method: 'POST',
        url: '/api/admin/users/invite',
        payload: { role: 'member' },
      });

      const auditLine = lines.find((l) => l['event'] === 'USER.INVITED.SUCCESS');
      expect(auditLine).toBeDefined();
      expect(auditLine).not.toHaveProperty('email');
    });

    it('emits USER.INVITE_REVOKED.SUCCESS on invite revocation', async () => {
      const { stream, lines } = createLogCollector();
      const { app, inviteStore } = createTestApp({ id: 'admin-1', role: 'admin' }, stream);
      await setupRoutes(app, inviteStore);

      const createRes = await app.inject({
        method: 'POST',
        url: '/api/admin/users/invite',
        payload: { email: 'revoke@example.com' },
      });
      const inviteId = createRes.json().id;

      await app.inject({
        method: 'DELETE',
        url: `/api/admin/users/invites/${inviteId}`,
      });

      const auditLine = lines.find((l) => l['event'] === 'USER.INVITE_REVOKED.SUCCESS');
      expect(auditLine).toBeDefined();
      expect(auditLine!['inviteId']).toBe(inviteId);
      expect(auditLine!['revokedBy']).toBe('admin-1');
    });
  });
});
