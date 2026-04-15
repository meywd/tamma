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
import { createPinoLogger } from '@tamma/observability';
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
  InMemoryApiKeyStore,
  PgApiKeyStore,
  InMemoryAgentConfigStore,
  PgAgentConfigStore,
  InstallationRouter,
  InMemoryTaskQueue,
  GitHubRepoConfigReader,
} from './index.js';
import {
  InMemoryRefreshTokenStore,
  PgRefreshTokenStore,
} from './persistence/refresh-token-store.js';
import {
  InMemoryPasswordResetStore,
  PgPasswordResetStore,
} from './persistence/password-reset-store.js';
import {
  InMemoryTenantMembershipStore,
  PgTenantMembershipStore,
} from './persistence/tenant-membership-store.js';
import { LoginLockoutService } from './auth/login-lockout.js';
import { ConsoleEmailService } from './services/email.js';
import { InMemoryHealthStore } from './services/health-store.js';
import { PgHealthStore } from './services/pg-health-store.js';
import { InMemorySanitizationStore } from './services/sanitization-store.js';
import { PgSanitizationStore } from './services/pg-sanitization-store.js';
import { InMemoryPromptStore } from './services/in-memory-prompt-store.js';
import { PgPromptStore } from './services/pg-prompt-store.js';
import { AgentResolverService } from './services/agent-resolver.js';

export interface ApiServerOptions {
  port?: number;
  host?: string;
  /** Override for GITHUB_APP_PRIVATE_KEY_PATH env var. */
  privateKeyPath?: string;
  /** Override for LOG_LEVEL env var. */
  logLevel?: string;
}

/**
 * Resolve the JWT secret used by v1 auth and GitHub OAuth routes.
 *
 * Fails fast in production when JWT_SECRET is unset to avoid silently
 * booting with the predictable dev fallback (every token would be forgeable).
 * In non-production environments the dev fallback is preserved so local
 * development and tests keep working without extra setup.
 */
export function resolveJwtSecret(env: NodeJS.ProcessEnv = process.env): string {
  const secret = env['JWT_SECRET'];
  if (secret && secret.length > 0) {
    return secret;
  }
  if (env['NODE_ENV'] === 'production') {
    throw new Error(
      'JWT_SECRET environment variable is required in production. Refusing to start with the insecure dev fallback.',
    );
  }
  return 'tamma-dev-jwt-secret';
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

  // Unified API key store (Story 16-7 — service keys + user keys under one table)
  const unifiedApiKeyStore = pool
    ? new PgApiKeyStore(pool)
    : new InMemoryApiKeyStore();

  // Auth v1 stores (Stories 18-1/18-2/18-6 — email+password auth)
  const refreshTokenStore = pool
    ? new PgRefreshTokenStore(pool)
    : new InMemoryRefreshTokenStore();
  const passwordResetStore = pool
    ? new PgPasswordResetStore(pool)
    : new InMemoryPasswordResetStore();
  const tenantMembershipStore = pool
    ? new PgTenantMembershipStore(pool)
    : new InMemoryTenantMembershipStore();
  const lockoutService = new LoginLockoutService();
  const emailService = new ConsoleEmailService();

  // Agent resolver dependencies (Story 9-8 — composes provider chain with health)
  const healthStore = pool ? new PgHealthStore(pool) : new InMemoryHealthStore();
  const sanitizationStore = pool
    ? new PgSanitizationStore(pool)
    : new InMemorySanitizationStore();
  const agentConfigStore = pool
    ? new PgAgentConfigStore(pool)
    : new InMemoryAgentConfigStore();
  const promptStore = pool ? new PgPromptStore(pool) : new InMemoryPromptStore();
  const agentResolverService = new AgentResolverService({
    configStore: agentConfigStore,
    healthStore,
    promptStore,
    sanitizationStore,
  });

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

  // Create pino logger with OpenSearch transport (when OPENSEARCH_ENABLED=true).
  // Passing the raw pino instance to Fastify ensures that both application logs
  // AND request/response logs are shipped to OpenSearch.
  const pinoLogger = createPinoLogger('tamma-api', logLevel);

  // JWT secret used by both v1 auth login and GitHub OAuth routes.
  // Throws in production if JWT_SECRET is unset — see resolveJwtSecret.
  const jwtSecret = resolveJwtSecret();

  const appOptions: Parameters<typeof createApp>[0] = {
    workflowStore,
    loggerInstance: pinoLogger,
    userManagement: {
      userStore,
      apiKeyStore,
      inviteStore,
      dashboardUrl: dashUrl,
    },
    admin: {
      pgPool: pool,
      unifiedApiKeyStore,
    },
    agentConfigStore,
    agentResolverService,
    promptStore,
    authV1: {
      register: {
        userStore,
        emailService,
      },
      login: {
        userStore,
        refreshTokenStore,
        membershipStore: tenantMembershipStore,
        lockoutService,
        jwtSecret,
      },
      passwordReset: {
        userStore,
        passwordResetStore,
        refreshTokenStore,
        emailService,
      },
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

  // --- Engine Context API routes (Elsa activity callbacks) ---
  // Always enable context storage routes; wire RepoConfigReader when Octokit is available.
  appOptions.engineContext = true;

  // Engine GitHub routes: use a PAT-authenticated Octokit if GITHUB_TOKEN is set,
  // or fall back to an app-authenticated Octokit for a default installation.
  const githubToken = process.env['GITHUB_TOKEN'];
  if (githubToken) {
    const engineOctokit = new Octokit({ auth: githubToken });
    appOptions.engineGitHub = { octokit: engineOctokit };
    appOptions.engineContext = {
      repoConfigReader: new GitHubRepoConfigReader(
        (params) => engineOctokit.repos.getContent(params) as ReturnType<typeof engineOctokit.repos.getContent>,
      ),
    };
    console.log('Engine GitHub routes enabled (PAT)');
  } else if (appId && privateKey) {
    // Use app-level Octokit without installation-specific auth.
    // The engine GitHub routes that need installation scope will fail gracefully.
    const defaultInstallationId = process.env['GITHUB_DEFAULT_INSTALLATION_ID'];
    if (defaultInstallationId) {
      const engineOctokit = new Octokit({
        authStrategy: createAppAuth,
        auth: {
          appId,
          privateKey,
          installationId: parseInt(defaultInstallationId, 10),
        },
      });
      appOptions.engineGitHub = { octokit: engineOctokit };
      appOptions.engineContext = {
        repoConfigReader: new GitHubRepoConfigReader(
          (params) => engineOctokit.repos.getContent(params) as ReturnType<typeof engineOctokit.repos.getContent>,
        ),
      };
      console.log(`Engine GitHub routes enabled (App installation ${defaultInstallationId})`);
    } else {
      console.warn('Engine GitHub routes disabled — no GITHUB_TOKEN or GITHUB_DEFAULT_INSTALLATION_ID');
    }
  } else {
    console.warn('Engine GitHub routes disabled — no GitHub credentials available');
  }

  // Engine task routes: agent resolver is not available in the standalone API server.
  // The execute-task endpoint will return 503 until an agent resolver is injected.
  appOptions.engineTask = {};
  console.log('Engine task routes enabled (agent resolver not yet configured)');

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
