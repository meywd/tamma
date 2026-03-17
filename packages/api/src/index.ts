/**
 * @tamma/api
 * Fastify REST API + SSE for the Tamma platform
 */

import Fastify from 'fastify';
import cors from '@fastify/cors';
import helmet from '@fastify/helmet';
import { registerKnowledgeBaseRoutes, createKBServices } from './routes/knowledge-base/index.js';
import type { KBServices } from './routes/knowledge-base/index.js';
import { registerSettingsRoutes, createSettingsServices } from './routes/settings/index.js';
import type { SettingsServices } from './routes/settings/index.js';
import { registerEngineRoutes } from './routes/engine/index.js';
import type { EngineRouteOptions } from './routes/engine/index.js';
import { registerAuthPlugin } from './auth/index.js';
import type { AuthConfig } from './auth/index.js';
import { EngineRegistry } from './engine-registry.js';
import type { EngineInfo } from './engine-registry.js';
import { registerWorkflowRoutes } from './routes/workflows/index.js';
import type { WorkflowRouteOptions } from './routes/workflows/index.js';
import { registerDashboardRoutes } from './routes/dashboard/index.js';
import type { DashboardRouteOptions } from './routes/dashboard/index.js';
import { InMemoryWorkflowStore } from './persistence/workflow-store.js';
import type {
  IWorkflowStore,
  WorkflowDefinition,
  WorkflowInstance,
} from './persistence/workflow-store.js';
import { registerGitHubCallbackRoute } from './routes/github/github-callback.js';
import type { GitHubCallbackOptions } from './routes/github/github-callback.js';
import { registerGitHubWebhookRoute } from './routes/github/github-webhook.js';
import type { GitHubWebhookOptions } from './routes/github/github-webhook.js';
import { registerSaaSRoutes } from './routes/saas/index.js';
import type { SaaSRouteOptions } from './routes/saas/index.js';
import { InMemoryInstallationStore } from './persistence/installation-store.js';
import type { IGitHubInstallationStore, GitHubInstallation, GitHubInstallationRepo } from './persistence/installation-store.js';
import { PgInstallationStore } from './persistence/pg-installation-store.js';
import { InMemoryUserStore } from './persistence/user-store.js';
import type { IUserStore, User, UserInstallation } from './persistence/user-store.js';
import { PgUserStore } from './persistence/pg-user-store.js';
import { generateApiKey, hashApiKey, getApiKeyPrefix } from './auth/api-key.js';
import { registerApiKeyAuthPlugin } from './auth/api-key-auth.js';
import type { InstallationContext, ApiKeyAuthConfig } from './auth/api-key-auth.js';
import { GitHubSecretsProvisioner } from './services/github-secrets-provisioner.js';
import type { ProvisionResult } from './services/github-secrets-provisioner.js';
import { InstallationRouter } from './services/installation-router.js';
import type { InstallationResolveResult, InstallationRouterOptions } from './services/installation-router.js';
import { InMemoryTaskQueue } from './services/in-memory-task-queue.js';
import type { InMemoryTaskQueueOptions } from './services/in-memory-task-queue.js';
import type {
  ITask,
  ITaskQueue,
  EnqueueTaskInput,
  DequeueOptions,
  ListTasksOptions,
} from './services/task-queue.js';

export {
  registerKnowledgeBaseRoutes,
  createKBServices,
  registerEngineRoutes,
  registerAuthPlugin,
  EngineRegistry,
  registerWorkflowRoutes,
  registerDashboardRoutes,
  InMemoryWorkflowStore,
  registerSettingsRoutes,
  createSettingsServices,
  registerGitHubCallbackRoute,
  registerGitHubWebhookRoute,
  registerSaaSRoutes,
  InMemoryInstallationStore,
  PgInstallationStore,
  InMemoryUserStore,
  PgUserStore,
  generateApiKey,
  hashApiKey,
  getApiKeyPrefix,
  registerApiKeyAuthPlugin,
  GitHubSecretsProvisioner,
  InstallationRouter,
  InMemoryTaskQueue,
};

export { startApiServer } from './serve.js';
export type { ApiServerOptions } from './serve.js';

export type {
  KBServices,
  EngineRouteOptions,
  AuthConfig,
  EngineInfo,
  WorkflowRouteOptions,
  DashboardRouteOptions,
  IWorkflowStore,
  WorkflowDefinition,
  WorkflowInstance,
  SettingsServices,
  GitHubCallbackOptions,
  GitHubWebhookOptions,
  SaaSRouteOptions,
  IGitHubInstallationStore,
  GitHubInstallation,
  GitHubInstallationRepo,
  IUserStore,
  User,
  UserInstallation,
  InstallationContext,
  ApiKeyAuthConfig,
  ProvisionResult,
  InstallationResolveResult,
  InstallationRouterOptions,
  InMemoryTaskQueueOptions,
  ITask,
  ITaskQueue,
  EnqueueTaskInput,
  DequeueOptions,
  ListTasksOptions,
};

/** Options for creating the Fastify app with optional engine support. */
export interface CreateAppOptions {
  /** Knowledge-base services (optional; defaults are created if omitted). */
  kbServices?: KBServices;
  /** Engine to expose via REST/SSE routes (optional). */
  engine?: EngineRouteOptions;
  /** Auth configuration (optional; defaults to dev mode). */
  auth?: AuthConfig;
  /** Workflow store (optional; uses in-memory store if omitted). */
  workflowStore?: IWorkflowStore;
  /** Engine registry for multi-engine support (optional). */
  engineRegistry?: EngineRegistry;
  /** Settings services for config, health, and diagnostics (optional). */
  settingsServices?: SettingsServices;
  /** GitHub App callback options (optional; enables /api/github/callback). */
  githubCallback?: GitHubCallbackOptions;
  /** GitHub App webhook options (optional; enables /api/github/webhooks). */
  githubWebhook?: GitHubWebhookOptions;
  /** SaaS API route options (optional; enables /api/v1/* routes). */
  saas?: SaaSRouteOptions;
  /** Enable Fastify logger (boolean or pino options object). */
  logger?: boolean | object;
}

/**
 * Create and configure the Fastify API server.
 */
export async function createApp(options?: CreateAppOptions) {
  const app = Fastify({ logger: options?.logger ?? false });

  // Global error handler — return structured errors without leaking stack traces
  app.setErrorHandler((error, _request, reply) => {
    const statusCode = error.statusCode ?? 500;
    if (statusCode >= 500) {
      app.log.error(error);
    }
    return reply.status(statusCode).send({
      error: statusCode >= 500 ? 'Internal Server Error' : error.message,
    });
  });

  await app.register(cors, { origin: true });
  await app.register(helmet);

  // Health check
  app.get('/api/health', async () => ({ status: 'ok', timestamp: new Date().toISOString() }));

  // Auth plugin (if configured)
  if (options?.auth !== undefined) {
    await app.register(registerAuthPlugin, options.auth);
  }

  // Knowledge Base Management routes
  await registerKnowledgeBaseRoutes(app, options?.kbServices);

  // Settings routes (config, health, diagnostics)
  await registerSettingsRoutes(app, options?.settingsServices);

  // Engine routes (if an engine is provided)
  if (options?.engine !== undefined) {
    await app.register(
      async (instance) => {
        await registerEngineRoutes(instance, options.engine!);
      },
      { prefix: '' },
    );
  }

  // Workflow routes
  if (options?.workflowStore !== undefined) {
    await app.register(
      async (instance) => {
        await registerWorkflowRoutes(instance, { store: options.workflowStore! });
      },
      { prefix: '' },
    );
  }

  // GitHub App routes
  if (options?.githubCallback !== undefined) {
    await registerGitHubCallbackRoute(app, options.githubCallback);
  }
  if (options?.githubWebhook !== undefined) {
    await app.register(
      async (instance) => {
        await registerGitHubWebhookRoute(instance, options.githubWebhook!);
      },
      { prefix: '' },
    );
  }

  // SaaS API routes (protected by API key auth)
  if (options?.saas !== undefined) {
    await app.register(
      async (instance) => {
        await registerSaaSRoutes(instance, options.saas!);
      },
      { prefix: '' },
    );
  }

  // Dashboard routes (requires both engine registry and workflow store)
  if (options?.engineRegistry !== undefined && options?.workflowStore !== undefined) {
    await app.register(
      async (instance) => {
        await registerDashboardRoutes(instance, {
          engineRegistry: options.engineRegistry!,
          workflowStore: options.workflowStore!,
        });
      },
      { prefix: '' },
    );
  }

  return app;
}

/**
 * Start the API server (used when running standalone).
 */
export async function startServer(port = 3001, host = '0.0.0.0', options?: CreateAppOptions) {
  const app = await createApp(options);
  await app.listen({ port, host });
  return app;
}
