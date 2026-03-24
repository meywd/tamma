/**
 * Shared test helpers for API route tests.
 *
 * Provides factory functions for creating Fastify test apps, seeding
 * installations with API keys, signing webhook payloads, and making
 * authenticated requests.
 */

import { createHmac } from 'node:crypto';
import Fastify from 'fastify';
import type { FastifyInstance, InjectOptions } from 'fastify';
import { InMemoryInstallationStore } from '../persistence/installation-store.js';
import { InMemoryWorkflowStore } from '../persistence/workflow-store.js';
import { InMemoryUserStore } from '../persistence/user-store.js';
import { InMemoryTaskQueue } from '../services/in-memory-task-queue.js';
import { generateApiKey, hashApiKey, getApiKeyPrefix } from '../auth/api-key.js';
import { createTestInstallation, createTestRepos } from './fixtures/installations.js';

/** Bundled stores for test setup. */
export interface TestStores {
  installationStore: InMemoryInstallationStore;
  workflowStore: InMemoryWorkflowStore;
  userStore: InMemoryUserStore;
  taskQueue: InMemoryTaskQueue;
}

/** Result of seeding a test installation with an API key. */
export interface SeededInstallation {
  installationId: number;
  apiKey: string;
  apiKeyHash: string;
  apiKeyPrefix: string;
}

/** Create all in-memory stores for testing. */
export function createTestStores(): TestStores {
  return {
    installationStore: new InMemoryInstallationStore(),
    workflowStore: new InMemoryWorkflowStore(),
    userStore: new InMemoryUserStore(),
    taskQueue: new InMemoryTaskQueue(),
  };
}

/** Create a bare Fastify instance for testing (no routes registered). */
export function createTestApp(options?: { logger?: boolean }): FastifyInstance {
  return Fastify({ logger: options?.logger ?? false });
}

/**
 * Seed a test installation with an API key in the given store.
 * Returns the plain API key for use in test requests.
 */
export async function seedInstallation(
  store: InMemoryInstallationStore,
  overrides: Partial<Parameters<typeof createTestInstallation>[0]> = {},
): Promise<SeededInstallation> {
  const apiKey = generateApiKey();
  const apiKeyHash = hashApiKey(apiKey);
  const apiKeyPrefix = getApiKeyPrefix(apiKey);

  const installationId = overrides.installationId ?? 12345;

  await store.upsertInstallation(
    createTestInstallation({
      ...overrides,
      installationId,
      apiKeyHash,
      apiKeyPrefix,
    }),
  );

  // Seed default repos
  await store.setRepos(installationId, createTestRepos(installationId));

  return { installationId, apiKey, apiKeyHash, apiKeyPrefix };
}

/**
 * Compute HMAC-SHA256 signature for a webhook payload.
 * Returns the `sha256=<hex>` string expected in the X-Hub-Signature-256 header.
 */
export function signWebhookPayload(body: string | Buffer, secret: string): string {
  const signature = createHmac('sha256', secret).update(body).digest('hex');
  return `sha256=${signature}`;
}

/**
 * Helper to inject an authenticated request into a Fastify app.
 * Adds the `Authorization: Bearer <apiKey>` header automatically.
 */
export async function injectWithAuth(
  app: FastifyInstance,
  method: InjectOptions['method'],
  url: string,
  apiKey: string,
  payload?: unknown,
) {
  const options: InjectOptions = {
    url,
    headers: { authorization: `Bearer ${apiKey}` },
  };
  if (method !== undefined) {
    options.method = method;
  }
  if (payload !== undefined) {
    options.payload = payload as NonNullable<InjectOptions['payload']>;
  }
  return app.inject(options);
}

/**
 * Helper to inject a webhook request with proper signature.
 */
export async function injectWebhook(
  app: FastifyInstance,
  event: string,
  payload: unknown,
  secret: string,
) {
  const body = JSON.stringify(payload);
  const signature = signWebhookPayload(body, secret);

  return app.inject({
    method: 'POST',
    url: '/api/github/webhooks',
    headers: {
      'x-github-event': event,
      'x-hub-signature-256': signature,
      'content-type': 'application/json',
    },
    payload: body,
  });
}

/** Create a mock Octokit factory for SaaS route tests. */
export function createMockOctokitFactory() {
  const mockOctokit = {
    rest: {
      actions: {
        getRepoPublicKey: vi.fn().mockResolvedValue({
          data: { key_id: 'key-id', key: 'ug/7DBrOP4EIKNbrqwfx5OtBw1eSrkQOrAQVtOTh8kc=' },
        }),
        createOrUpdateRepoSecret: vi.fn().mockResolvedValue(undefined),
      },
    },
  };

  const factory = vi.fn().mockResolvedValue(mockOctokit);
  return { factory, mockOctokit };
}
