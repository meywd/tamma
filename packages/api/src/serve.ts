/**
 * Standalone API server entrypoint for Docker/SaaS deployment.
 *
 * Reads configuration from environment variables (not CLI config files).
 * Starts the Fastify server with GitHub App webhook, SaaS routes, and
 * all management endpoints.
 *
 * Usage:
 *   node packages/api/dist/serve.js
 *
 * Required env vars:
 *   DATABASE_URL             — PostgreSQL connection string
 *   GITHUB_APP_ID            — GitHub App ID
 *   GITHUB_APP_PRIVATE_KEY_PATH — Path to PEM file
 *   GITHUB_WEBHOOK_SECRET    — Webhook signing secret
 */

import { readFileSync } from 'node:fs';
import pg from 'pg';
import {
  createApp,
  InMemoryWorkflowStore,
  InMemoryInstallationStore,
  PgInstallationStore,
  InstallationRouter,
  InMemoryTaskQueue,
} from './index.js';

async function main(): Promise<void> {
  const port = parseInt(process.env['PORT'] ?? '3100', 10);
  const host = process.env['HOST'] ?? '0.0.0.0';

  // Database (optional — falls back to in-memory stores)
  const databaseUrl = process.env['DATABASE_URL'];
  let pool: pg.Pool | undefined;
  if (databaseUrl) {
    pool = new pg.Pool({ connectionString: databaseUrl });
  }

  // Stores
  const installationStore = pool
    ? new PgInstallationStore(pool)
    : new InMemoryInstallationStore();
  const workflowStore = new InMemoryWorkflowStore();
  const taskQueue = new InMemoryTaskQueue();
  const installationRouter = new InstallationRouter(installationStore);

  // GitHub App config (optional — webhook/callback won't register without these)
  const appIdStr = process.env['GITHUB_APP_ID'];
  const privateKeyPath = process.env['GITHUB_APP_PRIVATE_KEY_PATH'];
  const webhookSecret = process.env['GITHUB_WEBHOOK_SECRET'];

  let privateKey: string | undefined;
  if (privateKeyPath) {
    try {
      privateKey = readFileSync(privateKeyPath, 'utf-8');
    } catch (err) {
      console.warn(`Warning: Could not read private key from ${privateKeyPath}:`, (err as Error).message);
    }
  }

  const appId = appIdStr ? parseInt(appIdStr, 10) : undefined;

  // Build app options
  const appOptions: Parameters<typeof createApp>[0] = {
    workflowStore,
    logger: { level: process.env['LOG_LEVEL'] ?? 'info' },
  };

  // Register GitHub App routes if configured
  if (appId && privateKey && webhookSecret) {
    appOptions.githubWebhook = {
      webhookSecret,
      appId,
      installationStore,
      taskQueue,
      installationRouter,
    };

    appOptions.githubCallback = {
      appId,
      privateKey,
      installationStore,
      successRedirectUrl: process.env['GITHUB_CALLBACK_SUCCESS_URL'] ?? '/api/health',
    };

    // SaaS routes need Octokit factory
    const { Octokit } = await import('@octokit/rest');
    const { createAppAuth } = await import('@octokit/auth-app');

    appOptions.saas = {
      installationStore,
      workflowStore,
      createOctokit: async (installationId: number) => {
        return new Octokit({
          authStrategy: createAppAuth,
          auth: { appId, privateKey, installationId },
        });
      },
    };

    console.log(`GitHub App configured (appId=${appId})`);
  } else {
    console.warn('GitHub App not configured — webhook/SaaS routes disabled');
    if (!appId) console.warn('  Missing: GITHUB_APP_ID');
    if (!privateKey) console.warn('  Missing: GITHUB_APP_PRIVATE_KEY_PATH');
    if (!webhookSecret) console.warn('  Missing: GITHUB_WEBHOOK_SECRET');
  }

  const app = await createApp(appOptions);

  // Graceful shutdown
  const shutdown = async (): Promise<void> => {
    console.log('Shutting down...');
    await app.close();
    if (pool) await pool.end();
    process.exit(0);
  };
  process.on('SIGINT', () => void shutdown());
  process.on('SIGTERM', () => void shutdown());

  await app.listen({ port, host });
  console.log(`Tamma API listening on ${host}:${port}`);
}

main().catch((err) => {
  console.error('Failed to start API server:', err);
  process.exit(1);
});
