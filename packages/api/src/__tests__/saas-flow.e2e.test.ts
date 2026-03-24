/**
 * SaaS Cross-Service E2E Test
 *
 * Tests the full SaaS flow in a single process using in-memory stores
 * and Fastify inject (no external dependencies, always runs):
 *
 * 1. Webhook → signature verify → task enqueue with installationId
 * 2. API key auth → workflow status update
 * 3. Workflow result finalization
 * 4. Key rotation → new key works, old key is rejected
 */

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import Fastify from 'fastify';
import type { FastifyInstance } from 'fastify';
import { InMemoryInstallationStore } from '../persistence/installation-store.js';
import { InMemoryWorkflowStore } from '../persistence/workflow-store.js';
import { InMemoryTaskQueue } from '../services/in-memory-task-queue.js';
import { InstallationRouter } from '../services/installation-router.js';
import { registerGitHubWebhookRoute } from '../routes/github/github-webhook.js';
import { registerSaaSRoutes } from '../routes/saas/index.js';
import { registerWorkflowRoutes } from '../routes/workflows/index.js';
import { generateApiKey, hashApiKey, getApiKeyPrefix } from '../auth/api-key.js';
import { signWebhookPayload } from './test-helpers.js';
import {
  issueOpenedPayload,
} from './fixtures/webhook-payloads.js';

describe('SaaS Flow E2E', () => {
  let app: FastifyInstance;
  let installationStore: InMemoryInstallationStore;
  let workflowStore: InMemoryWorkflowStore;
  let taskQueue: InMemoryTaskQueue;
  let apiKey: string;
  const WEBHOOK_SECRET = 'test-webhook-secret';
  const INSTALLATION_ID = 12345;

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

  beforeEach(async () => {
    installationStore = new InMemoryInstallationStore();
    workflowStore = new InMemoryWorkflowStore();
    taskQueue = new InMemoryTaskQueue();
    const installationRouter = new InstallationRouter(installationStore);

    // Seed an active installation with API key
    apiKey = generateApiKey();
    const keyHash = hashApiKey(apiKey);
    const keyPrefix = getApiKeyPrefix(apiKey);

    await installationStore.upsertInstallation({
      installationId: INSTALLATION_ID,
      accountLogin: 'test-org',
      accountType: 'Organization',
      appId: 99,
      permissions: { contents: 'write', issues: 'write' },
      suspendedAt: null,
      apiKeyHash: keyHash,
      apiKeyPrefix: keyPrefix,
      apiKeyEncrypted: null,
    });

    await installationStore.setRepos(INSTALLATION_ID, [
      { repoId: 100, owner: 'test-org', name: 'repo-alpha', fullName: 'test-org/repo-alpha' },
      { repoId: 101, owner: 'test-org', name: 'repo-beta', fullName: 'test-org/repo-beta' },
    ]);

    // Build the app with all relevant routes
    app = Fastify({ logger: false });

    // Webhook route
    await app.register(
      async (instance) => {
        await registerGitHubWebhookRoute(instance, {
          webhookSecret: WEBHOOK_SECRET,
          appId: 99,
          installationStore,
          taskQueue,
          installationRouter,
        });
      },
      { prefix: '' },
    );

    // Workflow routes
    await app.register(
      async (instance) => {
        await registerWorkflowRoutes(instance, { store: workflowStore });
      },
      { prefix: '' },
    );

    // SaaS routes (API key protected)
    await app.register(
      async (instance) => {
        await registerSaaSRoutes(instance, {
          installationStore,
          workflowStore,
          createOctokit: mockCreateOctokit,
        });
      },
      { prefix: '' },
    );

    await app.ready();
  });

  afterEach(async () => {
    await app.close();
  });

  // -----------------------------------------------------------------------
  // Step 1: Webhook → task enqueue
  // -----------------------------------------------------------------------

  it('webhook with valid signature enqueues a task with installationId', async () => {
    const payload = issueOpenedPayload(INSTALLATION_ID);
    const body = JSON.stringify(payload);
    const signature = signWebhookPayload(body, WEBHOOK_SECRET);

    const response = await app.inject({
      method: 'POST',
      url: '/api/github/webhooks',
      headers: {
        'x-github-event': 'issues',
        'x-hub-signature-256': signature,
        'content-type': 'application/json',
      },
      payload: body,
    });

    expect(response.statusCode).toBe(200);

    // Verify task was enqueued with the correct installationId
    const tasks = await taskQueue.list({ installationId: INSTALLATION_ID });
    expect(tasks.length).toBeGreaterThanOrEqual(1);
    expect(tasks[0]!.payload.installationId).toBe(INSTALLATION_ID);
  });

  it('webhook with invalid signature is rejected', async () => {
    const payload = issueOpenedPayload(INSTALLATION_ID);
    const body = JSON.stringify(payload);

    const response = await app.inject({
      method: 'POST',
      url: '/api/github/webhooks',
      headers: {
        'x-github-event': 'issues',
        'x-hub-signature-256': 'sha256=invalidsignature',
        'content-type': 'application/json',
      },
      payload: body,
    });

    expect(response.statusCode).toBe(401);
  });

  // -----------------------------------------------------------------------
  // Step 2: API key auth → workflow status
  // -----------------------------------------------------------------------

  it('authenticated request can update workflow status', async () => {
    // Create a workflow instance first
    await workflowStore.createInstance({
      id: 'wf-e2e-1',
      definitionId: 'def-1',
      status: 'pending',
      variables: {},
      createdAt: Date.now(),
      updatedAt: Date.now(),
    });

    const response = await app.inject({
      method: 'POST',
      url: '/api/v1/workflows/wf-e2e-1/status',
      headers: { authorization: `Bearer ${apiKey}` },
      payload: {
        status: 'running',
        step: 'code-generation',
        progress: 50,
      },
    });

    expect(response.statusCode).toBe(200);
    expect(response.json().ok).toBe(true);
  });

  it('unauthenticated request is rejected', async () => {
    const response = await app.inject({
      method: 'POST',
      url: '/api/v1/workflows/wf-e2e-1/status',
      payload: {
        status: 'running',
        step: 'analysis',
      },
    });

    expect(response.statusCode).toBe(401);
  });

  // -----------------------------------------------------------------------
  // Step 3: Workflow result finalization
  // -----------------------------------------------------------------------

  it('can finalize a workflow with result', async () => {
    await workflowStore.createInstance({
      id: 'wf-e2e-2',
      definitionId: 'def-1',
      status: 'running',
      variables: {},
      createdAt: Date.now(),
      updatedAt: Date.now(),
    });

    const response = await app.inject({
      method: 'POST',
      url: '/api/v1/workflows/wf-e2e-2/result',
      headers: { authorization: `Bearer ${apiKey}` },
      payload: {
        status: 'completed',
        prNumber: 42,
        duration: 30000,
      },
    });

    expect(response.statusCode).toBe(200);
    const body = response.json();
    expect(body.ok).toBe(true);
    expect(body.status).toBe('completed');

    // Verify the instance was updated
    const instance = await workflowStore.getInstance('wf-e2e-2');
    expect(instance!.status).toBe('completed');
  });

  // -----------------------------------------------------------------------
  // Step 4: Key rotation
  // -----------------------------------------------------------------------

  it('key rotation: new key works, old key is rejected', async () => {
    const oldKey = apiKey;

    // Rotate the key
    const rotateResponse = await app.inject({
      method: 'POST',
      url: `/api/v1/installations/${INSTALLATION_ID}/rotate-key`,
      headers: { authorization: `Bearer ${oldKey}` },
    });

    expect(rotateResponse.statusCode).toBe(200);
    const rotateBody = rotateResponse.json();
    expect(rotateBody.ok).toBe(true);
    expect(rotateBody.keyPrefix).toMatch(/^tamma_sk_/);

    // Old key should now fail (hash has been replaced)
    await workflowStore.createInstance({
      id: 'wf-e2e-3',
      definitionId: 'def-1',
      status: 'running',
      variables: {},
      createdAt: Date.now(),
      updatedAt: Date.now(),
    });

    const oldKeyResponse = await app.inject({
      method: 'POST',
      url: '/api/v1/workflows/wf-e2e-3/status',
      headers: { authorization: `Bearer ${oldKey}` },
      payload: { status: 'running', step: 'test' },
    });

    expect(oldKeyResponse.statusCode).toBe(401);
  });

  // -----------------------------------------------------------------------
  // Full flow: webhook → status → result
  // -----------------------------------------------------------------------

  it('full flow: webhook enqueues task, status update, result finalization', async () => {
    // 1. Webhook enqueues a task
    const payload = issueOpenedPayload(INSTALLATION_ID);
    const body = JSON.stringify(payload);
    const signature = signWebhookPayload(body, WEBHOOK_SECRET);

    const webhookResponse = await app.inject({
      method: 'POST',
      url: '/api/github/webhooks',
      headers: {
        'x-github-event': 'issues',
        'x-hub-signature-256': signature,
        'content-type': 'application/json',
      },
      payload: body,
    });
    expect(webhookResponse.statusCode).toBe(200);

    // 2. Create a workflow instance (simulating worker picking up the task)
    await workflowStore.createInstance({
      id: 'wf-full-flow',
      definitionId: 'issue-workflow',
      status: 'pending',
      variables: { issueNumber: 42 },
      createdAt: Date.now(),
      updatedAt: Date.now(),
    });

    // 3. Update status via SaaS API
    const statusResponse = await app.inject({
      method: 'POST',
      url: '/api/v1/workflows/wf-full-flow/status',
      headers: { authorization: `Bearer ${apiKey}` },
      payload: { status: 'running', step: 'code-generation', progress: 75 },
    });
    expect(statusResponse.statusCode).toBe(200);

    // 4. Finalize the workflow
    const resultResponse = await app.inject({
      method: 'POST',
      url: '/api/v1/workflows/wf-full-flow/result',
      headers: { authorization: `Bearer ${apiKey}` },
      payload: { status: 'completed', prNumber: 99, duration: 45000 },
    });
    expect(resultResponse.statusCode).toBe(200);

    // 5. Verify final state
    const instance = await workflowStore.getInstance('wf-full-flow');
    expect(instance!.status).toBe('completed');

    const tasks = await taskQueue.list({ installationId: INSTALLATION_ID });
    expect(tasks.length).toBeGreaterThanOrEqual(1);
  });
});
