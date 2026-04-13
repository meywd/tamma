/**
 * Admin Service Key CRUD Routes.
 *
 * Provides endpoints for platform operators to manage service-to-service
 * API keys. All routes are gated by requirePermission('settings:manage')
 * (owner role only).
 *
 * Endpoints:
 *   POST   /api/admin/service-keys           — create a new service key
 *   GET    /api/admin/service-keys           — list all service keys
 *   POST   /api/admin/service-keys/:id/rotate — rotate with 24h grace period
 *   DELETE /api/admin/service-keys/:id       — immediate revoke
 */

import type { FastifyInstance } from 'fastify';
import { generateApiKey, hashApiKey, getApiKeyPrefix } from '../../auth/api-key.js';
import type { IApiKeyStore } from '../../persistence/api-key-store.js';
import { requirePermission } from '../../auth/require-permission.js';

export interface ServiceKeyRouteOptions {
  apiKeyStore: IApiKeyStore;
}

export async function registerServiceKeyRoutes(
  app: FastifyInstance,
  options: ServiceKeyRouteOptions,
): Promise<void> {
  const { apiKeyStore } = options;

  /**
   * POST /api/admin/service-keys — Create a new service key.
   *
   * Returns the raw key exactly once in the response body.
   */
  app.post<{
    Body: {
      serviceName: string;
      label?: string;
      permissions?: string[];
    };
  }>(
    '/api/admin/service-keys',
    {
      preHandler: [requirePermission('settings:manage')],
    },
    async (request, reply) => {
      const body = request.body ?? {};
      const serviceName = (body as Record<string, unknown>)['serviceName'];
      if (!serviceName || typeof serviceName !== 'string') {
        return reply.status(400).send({ error: 'serviceName is required' });
      }

      const label = typeof (body as Record<string, unknown>)['label'] === 'string'
        ? (body as Record<string, unknown>)['label'] as string
        : 'default';

      const rawPermissions = (body as Record<string, unknown>)['permissions'];
      const permissions = Array.isArray(rawPermissions)
        ? rawPermissions.filter((p): p is string => typeof p === 'string')
        : [];

      // Generate the raw key
      const rawKey = generateApiKey();
      const keyHash = hashApiKey(rawKey);
      const keyPrefix = getApiKeyPrefix(rawKey);

      const record = await apiKeyStore.createApiKey({
        scope: 'service',
        ownerId: serviceName,
        keyHash,
        keyPrefix,
        label,
        permissions,
        tenantId: null, // service keys are not tenant-scoped at creation
      });

      request.log.info(
        {
          keyId: record.id,
          serviceName,
          permissions,
          keyPrefix,
        },
        'Service key created',
      );

      return reply.status(201).send({
        id: record.id,
        serviceName: record.ownerId,
        label: record.label,
        permissions: record.permissions,
        keyPrefix: record.keyPrefix,
        createdAt: record.createdAt,
        rawKey,
        warning: 'Store this key securely. It cannot be retrieved again.',
      });
    },
  );

  /**
   * GET /api/admin/service-keys — List all service keys (without raw keys).
   */
  app.get(
    '/api/admin/service-keys',
    {
      preHandler: [requirePermission('settings:manage')],
    },
    async (_request, reply) => {
      const keys = await apiKeyStore.listByScope('service');
      const sanitized = keys.map((k) => ({
        id: k.id,
        serviceName: k.ownerId,
        label: k.label,
        permissions: k.permissions,
        keyPrefix: k.keyPrefix,
        createdAt: k.createdAt,
        lastUsedAt: k.lastUsedAt,
        revokedAt: k.revokedAt,
        rotatedFrom: k.rotatedFrom,
      }));
      return reply.send(sanitized);
    },
  );

  /**
   * POST /api/admin/service-keys/:id/rotate — Rotate a service key.
   *
   * Generates a new key; old key remains valid for 24h grace period.
   * Returns the new raw key exactly once.
   */
  app.post<{
    Params: { id: string };
  }>(
    '/api/admin/service-keys/:id/rotate',
    {
      preHandler: [requirePermission('settings:manage')],
    },
    async (request, reply) => {
      const { id } = request.params;

      const rawKey = generateApiKey();
      const keyHash = hashApiKey(rawKey);
      const keyPrefix = getApiKeyPrefix(rawKey);

      try {
        const newRecord = await apiKeyStore.rotateApiKey(id, keyHash, keyPrefix);

        request.log.info(
          {
            oldKeyId: id,
            newKeyId: newRecord.id,
            serviceName: newRecord.ownerId,
            keyPrefix,
          },
          'Service key rotated',
        );

        return reply.send({
          id: newRecord.id,
          serviceName: newRecord.ownerId,
          label: newRecord.label,
          permissions: newRecord.permissions,
          keyPrefix: newRecord.keyPrefix,
          createdAt: newRecord.createdAt,
          rotatedFrom: newRecord.rotatedFrom,
          rawKey,
          warning: 'Store this key securely. It cannot be retrieved again. Old key is valid for 24h.',
        });
      } catch (err) {
        const message = err instanceof Error ? err.message : 'Unknown error';
        if (message.includes('not found')) {
          return reply.status(404).send({ error: 'Service key not found' });
        }
        throw err;
      }
    },
  );

  /**
   * DELETE /api/admin/service-keys/:id — Immediately revoke a service key.
   */
  app.delete<{
    Params: { id: string };
  }>(
    '/api/admin/service-keys/:id',
    {
      preHandler: [requirePermission('settings:manage')],
    },
    async (request, reply) => {
      const { id } = request.params;

      try {
        await apiKeyStore.revokeApiKey(id);

        request.log.info(
          { keyId: id },
          'Service key revoked',
        );

        return reply.status(204).send();
      } catch (err) {
        const message = err instanceof Error ? err.message : 'Unknown error';
        if (message.includes('not found')) {
          return reply.status(404).send({ error: 'Service key not found' });
        }
        throw err;
      }
    },
  );
}
