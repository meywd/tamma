/**
 * Standalone API server for Docker/SaaS deployment.
 *
 * Exports startApiServer() for use by both:
 * - Docker entrypoint (this file's main() at bottom)
 * - CLI `tamma api` command
 *
 * Reads configuration from environment variables and optional overrides.
 */

import { readFileSync } from 'node:fs';
import pg from 'pg';
import { Octokit } from '@octokit/rest';
import { createAppAuth } from '@octokit/auth-app';
import {
  createApp,
  InMemoryWorkflowStore,
  InMemoryInstallationStore,
  PgInstallationStore,
  InMemoryUserStore,
  PgUserStore,
  InMemoryUserApiKeyStore,
  PgUserApiKeyStore,
  InMemoryInviteStore,
  PgInviteStore,
  InstallationRouter,
  InMemoryTaskQueue,
} from './index.js';

export interface ApiServerOptions {
  port?: number;
  host?: string;
  /** Override for GITHUB_APP_PRIVATE_KEY_PATH env var. */
  privateKeyPath?: string;
  /** Override for LOG_LEVEL env var. */
  logLevel?: string;
}

/**
 * Start the Tamma API server in SaaS mode.
 * All deps (pg, Octokit) live in @tamma/api — callers don't need them.
 */
export async function startApiServer(options: ApiServerOptions = {}): Promise<void> {
  const port = options.port ?? parseInt(process.env['PORT'] ?? '3100', 10);
  const host = options.host ?? process.env['HOST'] ?? '0.0.0.0';

  // Database (optional — falls back to in-memory stores)
  const databaseUrl = process.env['DATABASE_URL'];
  let pool: pg.Pool | undefined;
  if (databaseUrl) {
    pool = new pg.Pool({ connectionString: databaseUrl });
    console.log('Using PostgreSQL persistence');
  } else {
    console.log('No DATABASE_URL — using in-memory stores');
  }

  // Stores
  const installationStore = pool
    ? new PgInstallationStore(pool)
    : new InMemoryInstallationStore();
  const userStore = pool
    ? new PgUserStore(pool)
    : new InMemoryUserStore();
  const workflowStore = new InMemoryWorkflowStore();
  const taskQueue = new InMemoryTaskQueue();
  const installationRouter = new InstallationRouter(installationStore);
  const apiKeyStore = pool
    ? new PgUserApiKeyStore(pool)
    : new InMemoryUserApiKeyStore();
  const inviteStore = pool
    ? new PgInviteStore(pool)
    : new InMemoryInviteStore();

  // GitHub App config
  const appIdStr = process.env['GITHUB_APP_ID'];
  const webhookSecret = process.env['GITHUB_WEBHOOK_SECRET'];
  const keyPath = options.privateKeyPath ?? process.env['GITHUB_APP_PRIVATE_KEY_PATH'];

  let privateKey: string | undefined;
  if (keyPath) {
    try {
      privateKey = readFileSync(keyPath, 'utf-8');
    } catch (err) {
      console.warn(`Warning: Could not read private key from ${keyPath}:`, (err as Error).message);
    }
  } else if (process.env['GITHUB_APP_PRIVATE_KEY']) {
    privateKey = process.env['GITHUB_APP_PRIVATE_KEY'];
  }

  const appId = appIdStr ? parseInt(appIdStr, 10) : undefined;

  // Build app options
  const logLevel = options.logLevel ?? process.env['LOG_LEVEL'] ?? 'info';
  const dashUrl = process.env['DASHBOARD_URL'] ?? 'http://localhost:3001';
  const appOptions: Parameters<typeof createApp>[0] = {
    workflowStore,
    logger: { level: logLevel },
    userManagement: {
      userStore,
      apiKeyStore,
      inviteStore,
      dashboardUrl: dashUrl,
    },
    admin: {
      pgPool: pool,
    },
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

    // GitHub OAuth login (requires GITHUB_OAUTH_CLIENT_ID + SECRET)
    const oauthClientId = process.env['GITHUB_OAUTH_CLIENT_ID'];
    const oauthClientSecret = process.env['GITHUB_OAUTH_CLIENT_SECRET'];
    const jwtSecret = process.env['JWT_SECRET'] ?? 'tamma-dev-jwt-secret';
    const apiBaseUrl = process.env['API_BASE_URL'] ?? `http://localhost:${port}`;

    if (oauthClientId && oauthClientSecret) {
      appOptions.githubOAuth = {
        clientId: oauthClientId,
        clientSecret: oauthClientSecret,
        jwtSecret,
        userStore,
        installationStore,
        dashboardUrl: dashUrl,
        apiBaseUrl,
      };
      console.log('GitHub OAuth login enabled');
    } else {
      console.warn('GitHub OAuth not configured — login disabled');
      if (!oauthClientId) console.warn('  Missing: GITHUB_OAUTH_CLIENT_ID');
      if (!oauthClientSecret) console.warn('  Missing: GITHUB_OAUTH_CLIENT_SECRET');
    }
  } else {
    console.warn('GitHub App not fully configured — webhook/SaaS routes disabled');
    if (!appId) console.warn('  Missing: GITHUB_APP_ID');
    if (!privateKey) console.warn('  Missing: GITHUB_APP_PRIVATE_KEY or GITHUB_APP_PRIVATE_KEY_PATH');
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

// Docker entrypoint — only runs when executed directly
const isDirectRun = process.argv[1]?.endsWith('serve.js') || process.argv[1]?.endsWith('serve.ts');
if (isDirectRun) {
  startApiServer().catch((err) => {
    console.error('Failed to start API server:', err);
    process.exit(1);
  });
}
