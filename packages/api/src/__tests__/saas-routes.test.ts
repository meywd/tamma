/**
 * SaaS Routes Tests
 *
 * Tests for LLM proxy, workflow status, workflow result, and key rotation.
 */

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import Fastify from 'fastify';
import type { FastifyInstance } from 'fastify';
import { registerSaaSRoutes } from '../routes/saas/index.js';
import { generateApiKey, hashApiKey, getApiKeyPrefix } from '../auth/api-key.js';
import { InMemoryInstallationStore } from '../persistence/installation-store.js';
import { InMemoryWorkflowStore } from '../persistence/workflow-store.js';

function createTestApp() {
  const store = new InMemoryInstallationStore();
  const workflowStore = new InMemoryWorkflowStore();

  const validKey = generateApiKey();
  const validKeyHash = hashApiKey(validKey);
  const validKeyPrefix = getApiKeyPrefix(validKey);

  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const mockCreateOctokit = vi.fn().mockResolvedValue({
    rest: {
      actions: {
        getRepoPublicKey: vi.fn().mockResolvedValue({
          data: { key_id: 'key-id', key: 'ug/7DBrOP4EIKNbrqwfx5OtBw1eSrkQOrAQVtOTh8kc=' },
        }),
        createOrUpdateRepoSecret: vi.fn().mockResolvedValue(undefined),
      },
    },
  } as any);

  return {
    store,
    workflowStore,
    validKey,
    validKeyHash,
    validKeyPrefix,
    mockCreateOctokit,
  };
}

describe('SaaS Routes - LLM Proxy', () => {
  let app: FastifyInstance;
  let validKey: string;

  beforeEach(async () => {
    const ctx = createTestApp();
    validKey = ctx.validKey;

    await ctx.store.upsertInstallation({
      installationId: 1,
      accountLogin: 'test-org',
      accountType: 'Organization',
      appId: 1,
      permissions: {},
      suspendedAt: null,
      apiKeyHash: ctx.validKeyHash,
      apiKeyPrefix: ctx.validKeyPrefix,
      apiKeyEncrypted: null,
    });

    app = Fastify({ logger: false });
    await app.register(
      async (instance) => {
        await registerSaaSRoutes(instance, {
          installationStore: ctx.store,
          workflowStore: ctx.workflowStore,
          createOctokit: ctx.mockCreateOctokit,
        });
      },
      { prefix: '' },
    );
    await app.ready();
  });

  afterEach(async () => {
    await app.close();
  });

  it('POST /api/v1/llm/chat returns stub response for valid request', async () => {
    const response = await app.inject({
      method: 'POST',
      url: '/api/v1/llm/chat',
      headers: { authorization: `Bearer ${validKey}` },
      payload: {
        messages: [{ role: 'user', content: 'Hello' }],
      },
    });

    expect(response.statusCode).toBe(200);
    const body = response.json();
    expect(body.model).toBe('stub');
    expect(body.choices).toHaveLength(1);
    expect(body.choices[0].message.role).toBe('assistant');
    expect(body.meta.stub).toBe(true);
  });

  it('POST /api/v1/llm/chat accepts optional model and temperature', async () => {
    const response = await app.inject({
      method: 'POST',
      url: '/api/v1/llm/chat',
      headers: { authorization: `Bearer ${validKey}` },
      payload: {
        model: 'claude-3.5-sonnet',
        messages: [{ role: 'user', content: 'Hello' }],
        maxTokens: 1000,
        temperature: 0.7,
      },
    });

    expect(response.statusCode).toBe(200);
    const body = response.json();
    expect(body.model).toBe('claude-3.5-sonnet');
    expect(body.meta.maxTokens).toBe(1000);
    expect(body.meta.temperature).toBe(0.7);
  });

  it('POST /api/v1/llm/chat rejects empty messages', async () => {
    const response = await app.inject({
      method: 'POST',
      url: '/api/v1/llm/chat',
      headers: { authorization: `Bearer ${validKey}` },
      payload: {
        messages: [],
      },
    });

    expect(response.statusCode).toBe(400);
  });

  it('POST /api/v1/llm/chat rejects without auth', async () => {
    const response = await app.inject({
      method: 'POST',
      url: '/api/v1/llm/chat',
      payload: {
        messages: [{ role: 'user', content: 'Hello' }],
      },
    });

    expect(response.statusCode).toBe(401);
  });
});

describe('SaaS Routes - Workflow Status', () => {
  let app: FastifyInstance;
  let validKey: string;
  let workflowStore: InMemoryWorkflowStore;

  beforeEach(async () => {
    const ctx = createTestApp();
    validKey = ctx.validKey;
    workflowStore = ctx.workflowStore;

    await ctx.store.upsertInstallation({
      installationId: 1,
      accountLogin: 'test-org',
      accountType: 'Organization',
      appId: 1,
      permissions: {},
      suspendedAt: null,
      apiKeyHash: ctx.validKeyHash,
      apiKeyPrefix: ctx.validKeyPrefix,
      apiKeyEncrypted: null,
    });

    app = Fastify({ logger: false });
    await app.register(
      async (instance) => {
        await registerSaaSRoutes(instance, {
          installationStore: ctx.store,
          workflowStore: ctx.workflowStore,
          createOctokit: ctx.mockCreateOctokit,
        });
      },
      { prefix: '' },
    );
    await app.ready();
  });

  afterEach(async () => {
    await app.close();
  });

  it('POST /api/v1/workflows/:id/status updates a workflow', async () => {
    // Create a workflow instance first
    await workflowStore.createInstance({
      id: 'wf-1',
      definitionId: 'def-1',
      tenantId: '00000000-0000-0000-0000-000000000000',
      status: 'running',
      variables: {},
      createdAt: Date.now(),
      updatedAt: Date.now(),
    });

    const response = await app.inject({
      method: 'POST',
      url: '/api/v1/workflows/wf-1/status',
      headers: { authorization: `Bearer ${validKey}` },
      payload: {
        status: 'running',
        step: 'code-generation',
        progress: 50,
        message: 'Generating code...',
      },
    });

    expect(response.statusCode).toBe(200);
    const body = response.json();
    expect(body.ok).toBe(true);
    expect(body.workflowId).toBe('wf-1');
    expect(body.step).toBe('code-generation');
  });

  it('POST /api/v1/workflows/:id/status returns 404 for missing workflow', async () => {
    const response = await app.inject({
      method: 'POST',
      url: '/api/v1/workflows/nonexistent/status',
      headers: { authorization: `Bearer ${validKey}` },
      payload: {
        status: 'running',
        step: 'analysis',
      },
    });

    expect(response.statusCode).toBe(404);
  });

  it('POST /api/v1/workflows/:id/status rejects invalid body', async () => {
    const response = await app.inject({
      method: 'POST',
      url: '/api/v1/workflows/wf-1/status',
      headers: { authorization: `Bearer ${validKey}` },
      payload: {},
    });

    expect(response.statusCode).toBe(400);
  });
});

describe('SaaS Routes - Workflow Result', () => {
  let app: FastifyInstance;
  let validKey: string;
  let workflowStore: InMemoryWorkflowStore;

  beforeEach(async () => {
    const ctx = createTestApp();
    validKey = ctx.validKey;
    workflowStore = ctx.workflowStore;

    await ctx.store.upsertInstallation({
      installationId: 1,
      accountLogin: 'test-org',
      accountType: 'Organization',
      appId: 1,
      permissions: {},
      suspendedAt: null,
      apiKeyHash: ctx.validKeyHash,
      apiKeyPrefix: ctx.validKeyPrefix,
      apiKeyEncrypted: null,
    });

    app = Fastify({ logger: false });
    await app.register(
      async (instance) => {
        await registerSaaSRoutes(instance, {
          installationStore: ctx.store,
          workflowStore: ctx.workflowStore,
          createOctokit: ctx.mockCreateOctokit,
        });
      },
      { prefix: '' },
    );
    await app.ready();
  });

  afterEach(async () => {
    await app.close();
  });

  it('POST /api/v1/workflows/:id/result finalizes a workflow', async () => {
    await workflowStore.createInstance({
      id: 'wf-1',
      definitionId: 'def-1',
      tenantId: '00000000-0000-0000-0000-000000000000',
      status: 'running',
      variables: {},
      createdAt: Date.now(),
      updatedAt: Date.now(),
    });

    const response = await app.inject({
      method: 'POST',
      url: '/api/v1/workflows/wf-1/result',
      headers: { authorization: `Bearer ${validKey}` },
      payload: {
        status: 'completed',
        prNumber: 42,
        duration: 12345,
      },
    });

    expect(response.statusCode).toBe(200);
    const body = response.json();
    expect(body.ok).toBe(true);
    expect(body.status).toBe('completed');
    expect(body.duration).toBe(12345);
  });

  it('POST /api/v1/workflows/:id/result handles failed status', async () => {
    await workflowStore.createInstance({
      id: 'wf-2',
      definitionId: 'def-1',
      tenantId: '00000000-0000-0000-0000-000000000000',
      status: 'running',
      variables: {},
      createdAt: Date.now(),
      updatedAt: Date.now(),
    });

    const response = await app.inject({
      method: 'POST',
      url: '/api/v1/workflows/wf-2/result',
      headers: { authorization: `Bearer ${validKey}` },
      payload: {
        status: 'failed',
        error: 'Build failed',
        duration: 5000,
      },
    });

    expect(response.statusCode).toBe(200);
    const body = response.json();
    expect(body.status).toBe('failed');
  });

  it('POST /api/v1/workflows/:id/result returns 404 for missing workflow', async () => {
    const response = await app.inject({
      method: 'POST',
      url: '/api/v1/workflows/nonexistent/result',
      headers: { authorization: `Bearer ${validKey}` },
      payload: {
        status: 'completed',
        duration: 1000,
      },
    });

    expect(response.statusCode).toBe(404);
  });

  it('POST /api/v1/workflows/:id/result rejects invalid status', async () => {
    const response = await app.inject({
      method: 'POST',
      url: '/api/v1/workflows/wf-1/result',
      headers: { authorization: `Bearer ${validKey}` },
      payload: {
        status: 'invalid-status',
        duration: 1000,
      },
    });

    expect(response.statusCode).toBe(400);
  });
});

describe('SaaS Routes - Key Rotation', () => {
  let app: FastifyInstance;
  let validKey: string;
  let store: InMemoryInstallationStore;

  beforeEach(async () => {
    const ctx = createTestApp();
    validKey = ctx.validKey;
    store = ctx.store;

    await ctx.store.upsertInstallation({
      installationId: 1,
      accountLogin: 'test-org',
      accountType: 'Organization',
      appId: 1,
      permissions: {},
      suspendedAt: null,
      apiKeyHash: ctx.validKeyHash,
      apiKeyPrefix: ctx.validKeyPrefix,
      apiKeyEncrypted: null,
    });

    // Add some repos
    await ctx.store.setRepos(1, [
      { repoId: 100, owner: 'test-org', name: 'repo1', fullName: 'test-org/repo1' },
      { repoId: 101, owner: 'test-org', name: 'repo2', fullName: 'test-org/repo2' },
    ]);

    app = Fastify({ logger: false });
    await app.register(
      async (instance) => {
        await registerSaaSRoutes(instance, {
          installationStore: ctx.store,
          workflowStore: ctx.workflowStore,
          createOctokit: ctx.mockCreateOctokit,
        });
      },
      { prefix: '' },
    );
    await app.ready();
  });

  afterEach(async () => {
    await app.close();
  });

  it('POST /api/v1/installations/:id/rotate-key generates new key', async () => {
    const response = await app.inject({
      method: 'POST',
      url: '/api/v1/installations/1/rotate-key',
      headers: { authorization: `Bearer ${validKey}` },
    });

    expect(response.statusCode).toBe(200);
    const body = response.json();
    expect(body.ok).toBe(true);
    expect(body.installationId).toBe(1);
    expect(body.keyPrefix).toBeDefined();
    expect(body.keyPrefix.startsWith('tamma_sk_')).toBe(true);
    expect(body.provisioning.total).toBe(2);
    expect(body.provisioning.success).toBe(2);
  });

  it('POST /api/v1/installations/:id/rotate-key updates the key hash in store', async () => {
    const oldInstallation = await store.getInstallation(1);
    const oldHash = oldInstallation?.apiKeyHash;

    await app.inject({
      method: 'POST',
      url: '/api/v1/installations/1/rotate-key',
      headers: { authorization: `Bearer ${validKey}` },
    });

    const updatedInstallation = await store.getInstallation(1);
    expect(updatedInstallation?.apiKeyHash).not.toBe(oldHash);
    expect(updatedInstallation?.apiKeyHash).toBeDefined();
  });

  it('POST /api/v1/installations/:id/rotate-key returns 404 for unknown installation', async () => {
    const response = await app.inject({
      method: 'POST',
      url: '/api/v1/installations/999/rotate-key',
      headers: { authorization: `Bearer ${validKey}` },
    });

    expect(response.statusCode).toBe(404);
  });

  it('POST /api/v1/installations/:id/rotate-key returns 403 for suspended installation', async () => {
    await store.suspendInstallation(1);

    // We need a non-suspended installation's key to authenticate
    const otherKey = generateApiKey();
    const otherHash = hashApiKey(otherKey);
    const otherPrefix = getApiKeyPrefix(otherKey);

    await store.upsertInstallation({
      installationId: 2,
      accountLogin: 'other-org',
      accountType: 'Organization',
      appId: 1,
      permissions: {},
      suspendedAt: null,
      apiKeyHash: otherHash,
      apiKeyPrefix: otherPrefix,
      apiKeyEncrypted: null,
    });

    const response = await app.inject({
      method: 'POST',
      url: '/api/v1/installations/1/rotate-key',
      headers: { authorization: `Bearer ${otherKey}` },
    });

    expect(response.statusCode).toBe(403);
  });

  it('POST /api/v1/installations/:id/rotate-key returns 400 for invalid ID', async () => {
    const response = await app.inject({
      method: 'POST',
      url: '/api/v1/installations/abc/rotate-key',
      headers: { authorization: `Bearer ${validKey}` },
    });

    expect(response.statusCode).toBe(400);
  });
});
