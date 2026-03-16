/**
 * GitHub App webhook handler.
 *
 * Receives POST /api/github/webhooks with GitHub event payloads.
 * Verifies HMAC-SHA256 signature before processing.
 *
 * Handles:
 * - installation (created, deleted, suspend, unsuspend)
 * - installation_repositories (added, removed)
 * - issues (opened, edited, labeled, assigned)
 * - pull_request (opened, synchronize, closed)
 * - push events
 *
 * Multi-tenant support:
 * - Extracts installation.id from every webhook payload
 * - Tags all task payloads with installationId
 * - In SaaS mode, rejects webhooks missing installation.id (400)
 */

import { createHmac, timingSafeEqual } from 'node:crypto';
import type { FastifyInstance } from 'fastify';
import rateLimit from '@fastify/rate-limit';
import type { IGitHubInstallationStore } from '../../persistence/installation-store.js';
import type { ITaskQueue } from '../../services/task-queue.js';
import type { InstallationRouter } from '../../services/installation-router.js';

export interface GitHubWebhookOptions {
  webhookSecret: string;
  appId: number;
  installationStore: IGitHubInstallationStore;
  /** Task queue for enqueueing webhook-triggered work. */
  taskQueue?: ITaskQueue;
  /** Installation router for resolving/caching installations. */
  installationRouter?: InstallationRouter;
  /** When true, reject webhooks without installation.id (SaaS mode). */
  requireInstallationId?: boolean;
}

/**
 * Verify the GitHub webhook signature using HMAC-SHA256 with timing-safe comparison.
 */
function verifySignature(payload: string, signature: string, secret: string): boolean {
  const expected = 'sha256=' + createHmac('sha256', secret).update(payload).digest('hex');
  if (expected.length !== signature.length) return false;
  return timingSafeEqual(Buffer.from(expected), Buffer.from(signature));
}

/**
 * Extract installation.id from a GitHub webhook payload.
 * Returns null if the installation object or id is missing.
 */
function extractInstallationId(payload: Record<string, unknown>): number | null {
  const installation = payload['installation'] as Record<string, unknown> | undefined;
  if (!installation || installation['id'] === undefined || installation['id'] === null) {
    return null;
  }
  const id = Number(installation['id']);
  return Number.isNaN(id) ? null : id;
}

export async function registerGitHubWebhookRoute(
  app: FastifyInstance,
  options: GitHubWebhookOptions,
): Promise<void> {
  // Register rate-limit plugin so route-level config takes effect
  await app.register(rateLimit, { max: 300, timeWindow: '1 minute' });

  // Fastify needs raw body for signature verification.
  // We register a content-type parser to capture raw body.
  app.addContentTypeParser(
    'application/json',
    { parseAs: 'string' },
    (_req, body, done) => {
      done(null, body);
    },
  );

  app.post('/api/github/webhooks', {
    config: {
      rateLimit: { max: 300, timeWindow: '1 minute' },
    },
  }, async (request, reply) => {
    const signatureHeader = request.headers['x-hub-signature-256'];
    const event = request.headers['x-github-event'] as string | undefined;
    const rawBody = request.body as string;

    if (!signatureHeader || typeof signatureHeader !== 'string') {
      return reply.status(401).send({ error: 'Missing signature' });
    }

    if (!verifySignature(rawBody, signatureHeader, options.webhookSecret)) {
      return reply.status(401).send({ error: 'Invalid signature' });
    }

    let payload: Record<string, unknown>;
    try {
      payload = JSON.parse(rawBody) as Record<string, unknown>;
    } catch {
      return reply.status(400).send({ error: 'Invalid JSON' });
    }

    // Extract installationId from every webhook payload
    const installationId = extractInstallationId(payload);

    // In SaaS mode, reject webhooks without installation.id
    if (options.requireInstallationId && installationId === null) {
      app.log.warn({ msg: 'Webhook rejected: missing installation.id in SaaS mode', event });
      return reply.status(400).send({ error: 'Missing installation.id' });
    }

    app.log.info({ msg: 'GitHub webhook received', event, installationId });

    try {
      if (event === 'installation') {
        await handleInstallationEvent(payload, options, installationId);
      } else if (event === 'installation_repositories') {
        await handleInstallationRepositoriesEvent(payload, options, installationId);
      } else if (event === 'issues' || event === 'pull_request' || event === 'push') {
        // Enqueue a task for actionable webhook events
        await enqueueWebhookTask(event, payload, options, installationId);
      }
    } catch (err) {
      app.log.error({ msg: 'Failed to process webhook', event, installationId, error: err });
      return reply.status(500).send({ error: 'Internal error processing webhook' });
    }

    return reply.status(200).send({ ok: true });
  });
}

async function handleInstallationEvent(
  payload: Record<string, unknown>,
  options: GitHubWebhookOptions,
  installationId: number | null,
): Promise<void> {
  const action = payload['action'] as string;
  const installation = payload['installation'] as Record<string, unknown>;
  const id = installationId ?? Number(installation['id']);
  const account = installation['account'] as Record<string, unknown>;

  if (action === 'created') {
    await options.installationStore.upsertInstallation({
      installationId: id,
      accountLogin: String(account['login']),
      accountType: String(account['type']) as 'User' | 'Organization',
      appId: options.appId,
      permissions: (installation['permissions'] ?? {}) as Record<string, string>,
      suspendedAt: null,
      apiKeyHash: null,
      apiKeyPrefix: null,
      apiKeyEncrypted: null,
    });

    // Store repos from the installation event
    const repositories = (payload['repositories'] ?? []) as Array<Record<string, unknown>>;
    const repos = repositories.map((repo) => ({
      repoId: Number(repo['id']),
      owner: String((repo['full_name'] as string).split('/')[0]),
      name: String(repo['name']),
      fullName: String(repo['full_name']),
    }));
    await options.installationStore.setRepos(id, repos);
  } else if (action === 'deleted') {
    await options.installationStore.removeInstallation(id);
    // Invalidate the cache when an installation is deleted
    if (options.installationRouter) {
      options.installationRouter.invalidate(id);
    }
  } else if (action === 'suspend') {
    await options.installationStore.suspendInstallation(id);
    // Invalidate cache so the suspended state is picked up
    if (options.installationRouter) {
      options.installationRouter.invalidate(id);
    }
  } else if (action === 'unsuspend') {
    await options.installationStore.unsuspendInstallation(id);
    if (options.installationRouter) {
      options.installationRouter.invalidate(id);
    }
  }
}

async function handleInstallationRepositoriesEvent(
  payload: Record<string, unknown>,
  options: GitHubWebhookOptions,
  installationId: number | null,
): Promise<void> {
  const installation = payload['installation'] as Record<string, unknown>;
  const id = installationId ?? Number(installation['id']);

  const added = (payload['repositories_added'] ?? []) as Array<Record<string, unknown>>;
  const removed = (payload['repositories_removed'] ?? []) as Array<Record<string, unknown>>;

  if (added.length > 0) {
    const repos = added.map((repo) => ({
      repoId: Number(repo['id']),
      owner: String((repo['full_name'] as string).split('/')[0]),
      name: String(repo['name']),
      fullName: String(repo['full_name']),
    }));
    await options.installationStore.addRepos(id, repos);
  }

  if (removed.length > 0) {
    const repoIds = removed.map((repo) => Number(repo['id']));
    await options.installationStore.removeRepos(id, repoIds);
  }
}

/**
 * Enqueue a task for actionable webhook events (issues, pull_request, push).
 * Tags every task with installationId for multi-tenant routing.
 */
async function enqueueWebhookTask(
  event: string,
  payload: Record<string, unknown>,
  options: GitHubWebhookOptions,
  installationId: number | null,
): Promise<void> {
  if (!options.taskQueue) {
    return; // No task queue configured — skip enqueue
  }

  const action = (payload['action'] as string | undefined) ?? 'unknown';

  const taskInput: {
    type: string;
    installationId?: number | null;
    payload: Record<string, unknown>;
  } = {
    type: `github.${event}.${action}`,
    payload: {
      event,
      action,
    },
  };

  // Conditionally set installationId to avoid exactOptionalPropertyTypes violation
  if (installationId !== null) {
    taskInput.installationId = installationId;
    taskInput.payload['installationId'] = installationId;
  }

  // Include optional payload fields
  if (payload['delivery'] !== undefined) {
    taskInput.payload['delivery'] = payload['delivery'];
  }
  if (payload['repository'] !== undefined) {
    taskInput.payload['repository'] = payload['repository'];
  }
  if (payload['sender'] !== undefined) {
    taskInput.payload['sender'] = payload['sender'];
  }

  await options.taskQueue.enqueue(taskInput);
}
