/**
 * Unified Auth Middleware Tests
 *
 * Tests authenticateApiKey for all three scopes (user, installation, service),
 * tenant validation, rotation grace period, and audit logging.
 */

import { describe, it, expect, beforeAll, afterAll, beforeEach, vi } from 'vitest';
import type { FastifyInstance } from 'fastify';
import { InMemoryApiKeyStore } from '../../persistence/api-key-store.js';
import { InMemoryTenantStore } from '../../persistence/tenant-store.js';
import { InMemoryUserStore } from '../../persistence/user-store.js';
import { hashApiKey, generateApiKey, getApiKeyPrefix } from '../api-key.js';
import { authenticateApiKey } from '../unified-auth.js';
import type { AuthPrincipal } from '../principal.js';

describe('authenticateApiKey middleware', () => {
  let app: FastifyInstance;
  let apiKeyStore: InMemoryApiKeyStore;
  let tenantStore: InMemoryTenantStore;
  let userStore: InMemoryUserStore;

  const DEFAULT_TENANT_ID = '00000000-0000-0000-0000-000000000000';

  beforeAll(async () => {
    const Fastify = (await import('fastify')).default;

    apiKeyStore = new InMemoryApiKeyStore();
    tenantStore = new InMemoryTenantStore();
    userStore = new InMemoryUserStore();

    app = Fastify({ logger: false });

    // Decorate request
    app.decorateRequest('authPrincipal', null);

    // Register the unified auth middleware on a test prefix
    app.addHook('onRequest', authenticateApiKey({ apiKeyStore, tenantStore, userStore }));

    // Test endpoint that returns the principal
    app.get('/test/principal', async (request) => {
      const principal = (request as typeof request & { authPrincipal: AuthPrincipal }).authPrincipal;
      return principal;
    });

    await app.ready();
  });

  afterAll(async () => {
    await app.close();
  });

  // ----------------------------------------------------------------
  // Missing/invalid auth header
  // ----------------------------------------------------------------

  it('returns 401 when no Authorization header', async () => {
    const res = await app.inject({ method: 'GET', url: '/test/principal' });
    expect(res.statusCode).toBe(401);
    expect(JSON.parse(res.body).error).toContain('Missing');
  });

  it('returns 401 when Authorization header is not Bearer', async () => {
    const res = await app.inject({
      method: 'GET',
      url: '/test/principal',
      headers: { authorization: 'Basic abc123' },
    });
    expect(res.statusCode).toBe(401);
  });

  it('returns 401 for unknown API key', async () => {
    const res = await app.inject({
      method: 'GET',
      url: '/test/principal',
      headers: { authorization: 'Bearer tamma_sk_nonexistent_key_value' },
    });
    expect(res.statusCode).toBe(401);
    expect(JSON.parse(res.body).error).toBe('Invalid API key');
  });

  // ----------------------------------------------------------------
  // User scope
  // ----------------------------------------------------------------

  it('authenticates user-scope key and builds correct principal', async () => {
    const rawKey = generateApiKey();
    const keyHash = hashApiKey(rawKey);
    const keyPrefix = getApiKeyPrefix(rawKey);

    // Create a user in the user store
    const createdUser = await userStore.upsertUser({
      githubId: 12345,
      githubLogin: 'testuser',
      email: 'test@example.com',
      role: 'admin',
    });
    const userId = createdUser.id;

    await apiKeyStore.createApiKey({
      scope: 'user',
      ownerId: userId,
      keyHash,
      keyPrefix,
      label: 'test',
      tenantId: DEFAULT_TENANT_ID,
    });

    const res = await app.inject({
      method: 'GET',
      url: '/test/principal',
      headers: { authorization: `Bearer ${rawKey}` },
    });

    expect(res.statusCode).toBe(200);
    const principal = JSON.parse(res.body);
    expect(principal.scope).toBe('user');
    expect(principal.userId).toBe(userId);
    expect(principal.role).toBe('admin');
    expect(principal.tenantId).toBe(DEFAULT_TENANT_ID);
  });

  it('defaults user role to member when user not found in store', async () => {
    const rawKey = generateApiKey();
    const keyHash = hashApiKey(rawKey);
    const keyPrefix = getApiKeyPrefix(rawKey);

    await apiKeyStore.createApiKey({
      scope: 'user',
      ownerId: 'unknown-user-id',
      keyHash,
      keyPrefix,
      label: 'test',
      tenantId: DEFAULT_TENANT_ID,
    });

    const res = await app.inject({
      method: 'GET',
      url: '/test/principal',
      headers: { authorization: `Bearer ${rawKey}` },
    });

    expect(res.statusCode).toBe(200);
    const principal = JSON.parse(res.body);
    expect(principal.scope).toBe('user');
    expect(principal.role).toBe('member');
  });

  // ----------------------------------------------------------------
  // Installation scope
  // ----------------------------------------------------------------

  it('authenticates installation-scope key and builds correct principal', async () => {
    const rawKey = generateApiKey();
    const keyHash = hashApiKey(rawKey);
    const keyPrefix = getApiKeyPrefix(rawKey);

    await apiKeyStore.createApiKey({
      scope: 'installation',
      ownerId: '42',
      keyHash,
      keyPrefix,
      label: 'GitHub App',
      tenantId: DEFAULT_TENANT_ID,
    });

    const res = await app.inject({
      method: 'GET',
      url: '/test/principal',
      headers: { authorization: `Bearer ${rawKey}` },
    });

    expect(res.statusCode).toBe(200);
    const principal = JSON.parse(res.body);
    expect(principal.scope).toBe('installation');
    expect(principal.installationId).toBe(42);
    expect(principal.tenantId).toBe(DEFAULT_TENANT_ID);
  });

  // ----------------------------------------------------------------
  // Service scope
  // ----------------------------------------------------------------

  it('authenticates service-scope key without X-Tenant-Id', async () => {
    const rawKey = generateApiKey();
    const keyHash = hashApiKey(rawKey);
    const keyPrefix = getApiKeyPrefix(rawKey);

    await apiKeyStore.createApiKey({
      scope: 'service',
      ownerId: 'elsa-server',
      keyHash,
      keyPrefix,
      label: 'Elsa',
      permissions: ['prompts:read'],
      tenantId: null,
    });

    const res = await app.inject({
      method: 'GET',
      url: '/test/principal',
      headers: { authorization: `Bearer ${rawKey}` },
    });

    expect(res.statusCode).toBe(200);
    const principal = JSON.parse(res.body);
    expect(principal.scope).toBe('service');
    expect(principal.serviceName).toBe('elsa-server');
    expect(principal.permissions).toEqual(['prompts:read']);
    expect(principal.tenantId).toBeNull();
  });

  it('authenticates service-scope key with valid X-Tenant-Id', async () => {
    const rawKey = generateApiKey();
    const keyHash = hashApiKey(rawKey);
    const keyPrefix = getApiKeyPrefix(rawKey);

    await apiKeyStore.createApiKey({
      scope: 'service',
      ownerId: 'elsa-server',
      keyHash,
      keyPrefix,
      label: 'Elsa',
      permissions: ['prompts:read'],
      tenantId: null,
    });

    const res = await app.inject({
      method: 'GET',
      url: '/test/principal',
      headers: {
        authorization: `Bearer ${rawKey}`,
        'x-tenant-id': DEFAULT_TENANT_ID,
      },
    });

    expect(res.statusCode).toBe(200);
    const principal = JSON.parse(res.body);
    expect(principal.scope).toBe('service');
    expect(principal.tenantId).toBe(DEFAULT_TENANT_ID);
  });

  it('rejects service-scope key with invalid X-Tenant-Id (tenant not found)', async () => {
    const rawKey = generateApiKey();
    const keyHash = hashApiKey(rawKey);
    const keyPrefix = getApiKeyPrefix(rawKey);

    await apiKeyStore.createApiKey({
      scope: 'service',
      ownerId: 'elsa-server',
      keyHash,
      keyPrefix,
      label: 'Elsa',
      permissions: ['prompts:read'],
      tenantId: null,
    });

    const res = await app.inject({
      method: 'GET',
      url: '/test/principal',
      headers: {
        authorization: `Bearer ${rawKey}`,
        'x-tenant-id': 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
      },
    });

    expect(res.statusCode).toBe(400);
    expect(JSON.parse(res.body).error).toContain('tenant not found');
  });

  // ----------------------------------------------------------------
  // Rotation grace period
  // ----------------------------------------------------------------

  it('allows rotated key during grace period and key still resolves', async () => {
    const rawKey = generateApiKey();
    const keyHash = hashApiKey(rawKey);
    const keyPrefix = getApiKeyPrefix(rawKey);

    const record = await apiKeyStore.createApiKey({
      scope: 'service',
      ownerId: 'elsa-server',
      keyHash,
      keyPrefix,
      label: 'Elsa',
      permissions: ['prompts:read'],
      tenantId: null,
    });

    // Rotate the key
    const newRawKey = generateApiKey();
    const newKeyHash = hashApiKey(newRawKey);
    const newKeyPrefix = getApiKeyPrefix(newRawKey);
    await apiKeyStore.rotateApiKey(record.id, newKeyHash, newKeyPrefix);

    // Old key should still work during grace period
    const res = await app.inject({
      method: 'GET',
      url: '/test/principal',
      headers: { authorization: `Bearer ${rawKey}` },
    });

    expect(res.statusCode).toBe(200);
    const principal = JSON.parse(res.body);
    expect(principal.scope).toBe('service');
    expect(principal.serviceName).toBe('elsa-server');
  });

  it('new rotated key works immediately', async () => {
    const rawKey = generateApiKey();
    const keyHash = hashApiKey(rawKey);
    const keyPrefix = getApiKeyPrefix(rawKey);

    const record = await apiKeyStore.createApiKey({
      scope: 'service',
      ownerId: 'elsa-server',
      keyHash,
      keyPrefix,
      label: 'Elsa',
      permissions: ['diagnostics:write'],
      tenantId: null,
    });

    const newRawKey = generateApiKey();
    const newKeyHash = hashApiKey(newRawKey);
    const newKeyPrefix = getApiKeyPrefix(newRawKey);
    await apiKeyStore.rotateApiKey(record.id, newKeyHash, newKeyPrefix);

    // New key should work
    const res = await app.inject({
      method: 'GET',
      url: '/test/principal',
      headers: { authorization: `Bearer ${newRawKey}` },
    });

    expect(res.statusCode).toBe(200);
    const principal = JSON.parse(res.body);
    expect(principal.scope).toBe('service');
    expect(principal.permissions).toEqual(['diagnostics:write']);
  });

  // ----------------------------------------------------------------
  // Revoked key
  // ----------------------------------------------------------------

  it('rejects immediately revoked key', async () => {
    const rawKey = generateApiKey();
    const keyHash = hashApiKey(rawKey);
    const keyPrefix = getApiKeyPrefix(rawKey);

    const record = await apiKeyStore.createApiKey({
      scope: 'service',
      ownerId: 'elsa-server',
      keyHash,
      keyPrefix,
      label: 'Elsa',
      permissions: ['prompts:read'],
      tenantId: null,
    });

    await apiKeyStore.revokeApiKey(record.id);

    const res = await app.inject({
      method: 'GET',
      url: '/test/principal',
      headers: { authorization: `Bearer ${rawKey}` },
    });

    expect(res.statusCode).toBe(401);
    expect(JSON.parse(res.body).error).toBe('Invalid API key');
  });

  // ----------------------------------------------------------------
  // User / installation scope: X-Tenant-Id is ignored
  // ----------------------------------------------------------------

  it('ignores X-Tenant-Id for user-scope keys', async () => {
    const rawKey = generateApiKey();
    const keyHash = hashApiKey(rawKey);
    const keyPrefix = getApiKeyPrefix(rawKey);

    await apiKeyStore.createApiKey({
      scope: 'user',
      ownerId: 'user-abc',
      keyHash,
      keyPrefix,
      label: 'test',
      tenantId: DEFAULT_TENANT_ID,
    });

    // Try to override tenant via header — should be ignored
    const res = await app.inject({
      method: 'GET',
      url: '/test/principal',
      headers: {
        authorization: `Bearer ${rawKey}`,
        'x-tenant-id': 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
      },
    });

    expect(res.statusCode).toBe(200);
    const principal = JSON.parse(res.body);
    expect(principal.scope).toBe('user');
    // Tenant derived from the key record, not the header
    expect(principal.tenantId).toBe(DEFAULT_TENANT_ID);
  });
});
