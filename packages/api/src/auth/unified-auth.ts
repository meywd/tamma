/**
 * Unified API Key Authentication Middleware.
 *
 * Validates any bearer token by a single lookup against the unified
 * `api_keys` table and populates `request.authPrincipal` with a
 * tagged union based on the key's scope.
 *
 * Handles:
 *   - user scope: derives tenant from the key record
 *   - installation scope: derives tenant from the key record
 *   - service scope: reads X-Tenant-Id header, validates tenant exists
 *   - rotation grace period: logs WARN for keys in grace period
 *   - audit logging: structured Pino log for every auth attempt
 */

import type { FastifyRequest, FastifyReply } from 'fastify';
import { hashApiKey, getApiKeyPrefix } from './api-key.js';
import type { AuthPrincipal } from './principal.js';
import type { IApiKeyStore } from '../persistence/api-key-store.js';
import type { ITenantStore } from '../persistence/tenant-store.js';
import type { IUserStore } from '../persistence/user-store.js';
import type { Role } from './permissions.js';

/** Dependencies for the unified auth middleware. */
export interface UnifiedAuthDeps {
  apiKeyStore: IApiKeyStore;
  tenantStore: ITenantStore;
  /** User store — used to look up the user's role for user-scope keys. */
  userStore?: IUserStore;
}

/**
 * Create a Fastify preHandler that authenticates API keys from the
 * unified api_keys table and populates request.authPrincipal.
 *
 * Usage:
 * ```ts
 * app.addHook('onRequest', authenticateApiKey(deps));
 * ```
 */
export function authenticateApiKey(deps: UnifiedAuthDeps) {
  const { apiKeyStore, tenantStore, userStore } = deps;

  return async (request: FastifyRequest, reply: FastifyReply): Promise<void> => {
    const authHeader = request.headers.authorization;

    if (!authHeader || !authHeader.startsWith('Bearer ')) {
      request.log.warn(
        { reason: 'missing-auth-header', method: request.method, path: request.url },
        'Auth failure: missing or invalid Authorization header',
      );
      reply.status(401).send({ error: 'Missing or invalid Authorization header' });
      return;
    }

    const token = authHeader.slice('Bearer '.length);
    const keyPrefix = getApiKeyPrefix(token);

    // Hash the token and look up
    const keyHash = hashApiKey(token);
    const keyRecord = await apiKeyStore.findByKeyHash(keyHash);

    if (!keyRecord) {
      request.log.warn(
        { reason: 'invalid-key', keyPrefix, method: request.method, path: request.url },
        'Auth failure: invalid API key',
      );
      reply.status(401).send({ error: 'Invalid API key' });
      return;
    }

    // Check if the key is in rotation grace period (revoked_at is set but in the future)
    if (keyRecord.revokedAt !== null) {
      const revokedAt = new Date(keyRecord.revokedAt);
      if (revokedAt > new Date()) {
        request.log.warn(
          {
            keyId: keyRecord.id,
            scope: keyRecord.scope,
            gracePeriodEnd: keyRecord.revokedAt,
          },
          'rotating-key-still-in-use',
        );
      }
    }

    // Update last_used_at asynchronously (fire and forget)
    apiKeyStore.updateLastUsed(keyRecord.id).catch(() => {
      // Silently ignore — not critical
    });

    // Build the AuthPrincipal based on scope
    let principal: AuthPrincipal;

    switch (keyRecord.scope) {
      case 'user': {
        // Look up user role if user store is available
        let role: Role = 'member';
        if (userStore) {
          const user = await userStore.getUser(keyRecord.ownerId);
          if (user) {
            role = user.role as Role;
          }
        }

        const tenantId = keyRecord.tenantId ?? '00000000-0000-0000-0000-000000000000';
        principal = {
          scope: 'user',
          keyId: keyRecord.id,
          userId: keyRecord.ownerId,
          role,
          tenantId,
        };
        break;
      }

      case 'installation': {
        const tenantId = keyRecord.tenantId ?? '00000000-0000-0000-0000-000000000000';
        principal = {
          scope: 'installation',
          keyId: keyRecord.id,
          installationId: parseInt(keyRecord.ownerId, 10),
          tenantId,
        };
        break;
      }

      case 'service': {
        // Service keys read X-Tenant-Id from the request header
        const tenantIdHeader = request.headers['x-tenant-id'];
        let tenantId: string | null = null;

        if (typeof tenantIdHeader === 'string' && tenantIdHeader.length > 0) {
          // Validate the tenant exists
          const tenant = await tenantStore.getTenant(tenantIdHeader);
          if (!tenant) {
            request.log.warn(
              { keyId: keyRecord.id, tenantId: tenantIdHeader, path: request.url },
              'Auth failure: tenant not found for X-Tenant-Id header',
            );
            reply.status(400).send({ error: 'Invalid X-Tenant-Id: tenant not found' });
            return;
          }
          tenantId = tenantIdHeader;
        }

        principal = {
          scope: 'service',
          keyId: keyRecord.id,
          serviceName: keyRecord.ownerId,
          permissions: keyRecord.permissions,
          tenantId,
        };
        break;
      }

      default: {
        request.log.warn(
          { keyId: keyRecord.id, scope: keyRecord.scope },
          'Auth failure: unknown key scope',
        );
        reply.status(401).send({ error: 'Invalid API key scope' });
        return;
      }
    }

    // Attach to request
    (request as FastifyRequest & { authPrincipal: AuthPrincipal }).authPrincipal = principal;

    // Audit log: successful auth
    request.log.info(
      {
        keyId: keyRecord.id,
        scope: keyRecord.scope,
        ownerId: keyRecord.ownerId,
        tenantId: principal.tenantId,
        method: request.method,
        path: request.url,
      },
      'Authenticated request',
    );
  };
}
