/**
 * Per-User API Key Routes
 *
 * Manage API keys for individual users:
 *   POST   /api/admin/users/:id/keys       — create key (returns full key once)
 *   GET    /api/admin/users/:id/keys       — list keys (no full key)
 *   DELETE /api/admin/users/:id/keys/:keyId — revoke key
 */

import type { FastifyInstance, FastifyRequest } from 'fastify';
import type { IUserStore } from '../../persistence/user-store.js';
import type { IUserApiKeyStore } from '../../persistence/user-api-key-store.js';
import { generateApiKey, hashApiKey, getApiKeyPrefix } from '../../auth/api-key.js';
import { requireSelfOrRole } from '../../middleware/require-role.js';
import type { AuthenticatedUser } from '../../middleware/require-role.js';

export interface ApiKeyRouteOptions {
  userStore: IUserStore;
  apiKeyStore: IUserApiKeyStore;
}

export async function registerApiKeyRoutes(
  app: FastifyInstance,
  options: ApiKeyRouteOptions,
): Promise<void> {
  const { userStore, apiKeyStore } = options;

  // POST /api/admin/users/:id/keys — generate new key
  app.post('/api/admin/users/:id/keys', {
    preHandler: [requireSelfOrRole('admin')],
  }, async (request, reply) => {
    const { id } = request.params as { id: string };
    const body = request.body as { label?: string } | null;
    const label = body?.label ?? 'default';

    // Verify user exists
    const user = await userStore.getUser(id);
    if (!user) {
      return reply.status(404).send({ error: 'User not found' });
    }

    const rawKey = generateApiKey();
    const keyHash = hashApiKey(rawKey);
    const keyPrefix = getApiKeyPrefix(rawKey);

    const record = await apiKeyStore.createApiKey({
      userId: id,
      keyHash,
      keyPrefix,
      label,
    });

    const authUser = (request as FastifyRequest & { authUser: AuthenticatedUser }).authUser;
    request.log.info({
      event: 'USER.API_KEY_CREATED.SUCCESS',
      targetUserId: id,
      keyId: record.id,
      keyPrefix,
      label,
      createdBy: authUser.id,
    }, 'API key created');

    // Return the full key ONCE — it cannot be retrieved again
    return reply.status(201).send({
      id: record.id,
      key: rawKey,
      prefix: keyPrefix,
      label: record.label,
      createdAt: record.createdAt,
    });
  });

  // GET /api/admin/users/:id/keys — list keys (no full key)
  app.get('/api/admin/users/:id/keys', {
    preHandler: [requireSelfOrRole('admin')],
  }, async (request, reply) => {
    const { id } = request.params as { id: string };

    // Verify user exists
    const user = await userStore.getUser(id);
    if (!user) {
      return reply.status(404).send({ error: 'User not found' });
    }

    const keys = await apiKeyStore.listApiKeys(id);
    return reply.send({ apiKeys: keys });
  });

  // DELETE /api/admin/users/:id/keys/:keyId — revoke key
  app.delete('/api/admin/users/:id/keys/:keyId', {
    preHandler: [requireSelfOrRole('admin')],
  }, async (request, reply) => {
    const { id, keyId } = request.params as { id: string; keyId: string };

    // Verify user exists
    const user = await userStore.getUser(id);
    if (!user) {
      return reply.status(404).send({ error: 'User not found' });
    }

    try {
      await apiKeyStore.revokeApiKey(keyId, id);
    } catch {
      return reply.status(404).send({ error: 'API key not found' });
    }

    const authUser = (request as FastifyRequest & { authUser: AuthenticatedUser }).authUser;
    request.log.info({
      event: 'USER.API_KEY_REVOKED.SUCCESS',
      targetUserId: id,
      keyId,
      revokedBy: authUser.id,
    }, 'API key revoked');

    return reply.send({ ok: true });
  });
}
