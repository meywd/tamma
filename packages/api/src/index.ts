/**
 * @tamma/api
 * Fastify REST API + SSE for the Tamma platform
 */

import Fastify, { type FastifyInstance } from 'fastify';
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
import type { IUserStore, User, UserInstallation, UpsertUserInput } from './persistence/user-store.js';
import { PgUserStore } from './persistence/pg-user-store.js';
import { generateApiKey, hashApiKey, getApiKeyPrefix } from './auth/api-key.js';
import { registerApiKeyAuthPlugin } from './auth/api-key-auth.js';
import type { InstallationContext, ApiKeyAuthConfig } from './auth/api-key-auth.js';
import { registerGitHubOAuthRoutes } from './routes/auth/github-oauth.js';
import type { GitHubOAuthOptions } from './routes/auth/github-oauth.js';
import { registerAuthMeRoute } from './routes/auth/me-route.js';
import type { AuthMeRouteOptions, AuthMeUser } from './routes/auth/me-route.js';
import { registerRoleCheckRoute } from './routes/auth/role-check.js';
import { registerUserManagementRoutes } from './routes/users/index.js';
import type { UserManagementRouteOptions } from './routes/users/index.js';
import { InMemoryUserApiKeyStore, PgUserApiKeyStore } from './persistence/user-api-key-store.js';
import type { IUserApiKeyStore, UserApiKey, CreateApiKeyInput } from './persistence/user-api-key-store.js';
import { InMemoryInviteStore, PgInviteStore } from './persistence/invite-store.js';
import type { IInviteStore, UserInvite, CreateInviteInput } from './persistence/invite-store.js';
import { requireRole, requireSelfOrRole } from './middleware/require-role.js';
import type { AuthenticatedUser } from './middleware/require-role.js';
import { GitHubSecretsProvisioner } from './services/github-secrets-provisioner.js';
import { GitHubRepoConfigReader } from './services/settings/repo-config-reader.js';
import type { RepoConfigReader } from './services/settings/repo-config-reader.js';
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
import { registerAdminRoutes } from './routes/admin/index.js';
import type { AdminRouteOptions } from './routes/admin/index.js';
import { registerEngineContextRoutes } from './routes/engine/engine-context-routes.js';
import type { EngineContextRouteOptions } from './routes/engine/engine-context-routes.js';
import { registerEngineGitHubRoutes } from './routes/engine/engine-github-routes.js';
import type { EngineGitHubRouteOptions } from './routes/engine/engine-github-routes.js';
import { registerEngineTaskRoutes } from './routes/engine/engine-task-routes.js';
import type { EngineTaskRouteOptions } from './routes/engine/engine-task-routes.js';
import { registerPromptRoutes } from './routes/prompts/prompt-routes.js';
import { PromptStore } from './services/prompt-store.js';
import { registerConventionTemplateRoutes } from './routes/convention-templates.js';
import type { PromptStoreOptions, UpsertPromptInput, RenderInput, PromptSummary, RenderedPrompt } from './services/prompt-store.js';
import type { PromptTemplate, PromptRole, PromptAction } from './services/default-prompts.js';
import { InMemoryTenantStore, PgTenantStore } from './persistence/tenant-store.js';
import type { ITenantStore, Tenant, CreateTenantInput, UpdateTenantInput } from './persistence/tenant-store.js';
import { InMemoryTenantMembershipStore, PgTenantMembershipStore, generateToken as generateInviteToken, hashToken as hashInviteToken } from './persistence/tenant-membership-store.js';
import type { ITenantMembershipStore, TenantMembership, TenantInvite, CreateTenantInviteInput } from './persistence/tenant-membership-store.js';
import { registerOrgRoutes } from './routes/orgs/index.js';
import type { OrgRoutesOptions } from './routes/orgs/index.js';
import { ConsoleEmailService, buildTenantInviteEmail } from './services/email.js';
import type { IEmailService, EmailMessage } from './services/email.js';
import { createEnsurePersonalTenant } from './middleware/ensure-personal-tenant.js';
import type { EnsurePersonalTenantOptions } from './middleware/ensure-personal-tenant.js';

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
  GitHubRepoConfigReader,
  InstallationRouter,
  InMemoryTaskQueue,
  registerGitHubOAuthRoutes,
  registerAuthMeRoute,
  registerRoleCheckRoute,
  InMemoryUserApiKeyStore,
  PgUserApiKeyStore,
  InMemoryInviteStore,
  PgInviteStore,
  requireRole,
  requireSelfOrRole,
  registerAdminRoutes,
  registerUserManagementRoutes,
  registerEngineContextRoutes,
  registerEngineGitHubRoutes,
  registerEngineTaskRoutes,
  registerPromptRoutes,
  registerConventionTemplateRoutes,
  PromptStore,
  // Story 18-3: Tenant/Org management
  InMemoryTenantStore,
  PgTenantStore,
  InMemoryTenantMembershipStore,
  PgTenantMembershipStore,
  generateInviteToken,
  hashInviteToken,
  registerOrgRoutes,
  ConsoleEmailService,
  buildTenantInviteEmail,
  createEnsurePersonalTenant,
};

export { startApiServer } from './serve.js';
export type { ApiServerOptions } from './serve.js';
export type { GitHubOAuthOptions } from './routes/auth/github-oauth.js';

// RBAC
export { hasPermission, getRolePermissions, isValidRole, PERMISSIONS } from './auth/permissions.js';
export type { Role, Permission } from './auth/permissions.js';
export { requirePermission } from './auth/require-permission.js';

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
  UpsertUserInput,
  InstallationContext,
  ApiKeyAuthConfig,
  ProvisionResult,
  InstallationResolveResult,
  InstallationRouterOptions,
  RepoConfigReader,
  InMemoryTaskQueueOptions,
  ITask,
  ITaskQueue,
  EnqueueTaskInput,
  DequeueOptions,
  ListTasksOptions,
  IUserApiKeyStore,
  UserApiKey,
  CreateApiKeyInput,
  IInviteStore,
  UserInvite,
  CreateInviteInput,
  AuthenticatedUser,
  AuthMeRouteOptions,
  AuthMeUser,
  AdminRouteOptions,
  UserManagementRouteOptions,
  EngineGitHubRouteOptions,
  EngineTaskRouteOptions,
  PromptStoreOptions,
  UpsertPromptInput,
  RenderInput,
  PromptSummary,
  RenderedPrompt,
  PromptTemplate,
  PromptRole,
  PromptAction,
  EngineContextRouteOptions,
  // Story 18-3: Tenant/Org types
  ITenantStore,
  Tenant,
  CreateTenantInput,
  UpdateTenantInput,
  ITenantMembershipStore,
  TenantMembership,
  TenantInvite,
  CreateTenantInviteInput,
  OrgRoutesOptions,
  IEmailService,
  EmailMessage,
  EnsurePersonalTenantOptions,
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
  /** GitHub OAuth login options (optional; enables /api/auth/github). */
  githubOAuth?: GitHubOAuthOptions;
  /** User management route options (optional; enables /api/admin/users/* routes). */
  userManagement?: UserManagementRouteOptions;
  /** Admin route options (optional; enables /api/admin/health). */
  admin?: AdminRouteOptions;
  /** Engine GitHub route options (optional; enables /api/engine/issues, etc.). */
  engineGitHub?: EngineGitHubRouteOptions;
  /** Engine task route options (optional; enables /api/engine/execute-task, etc.). */
  engineTask?: EngineTaskRouteOptions;
  /** Enable engine context routes (store-context, query-context, repo-config). Always registered when true. */
  engineContext?: boolean | EngineContextRouteOptions;
  /** Prompt store for the prompt registry API (optional; creates default in-memory store if omitted). */
  promptStore?: PromptStore;
  /** Organization routes options (optional; enables /api/v1/orgs/* routes). */
  orgRoutes?: OrgRoutesOptions;
  /** Enable Fastify logger (boolean, pino options object, or pino Logger instance). */
  logger?: boolean | object;
  /** Pre-built pino Logger instance (takes precedence over logger option). */
  loggerInstance?: object;
}

/**
 * Create and configure the Fastify API server.
 */
export async function createApp(options?: CreateAppOptions) {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const logOpts: Record<string, unknown> = options?.loggerInstance
    ? { loggerInstance: options.loggerInstance }
    : { logger: options?.logger ?? false };
  const app = Fastify(logOpts as any) as unknown as FastifyInstance;

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

  await app.register(cors, {
    origin: [
      'https://app.tamma.dev',
      'https://elsa.tamma.dev',
      'https://logs.tamma.dev',
      /^https?:\/\/localhost(:\d+)?$/,
    ],
    credentials: true,
  });
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

  // GitHub OAuth login routes
  if (options?.githubOAuth !== undefined) {
    await registerGitHubOAuthRoutes(app, options.githubOAuth);
    // Role check endpoint for nginx service gating (depends on JWT/cookie from OAuth)
    await registerRoleCheckRoute(app);
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

  // Organization (tenant) routes
  if (options?.orgRoutes !== undefined) {
    await registerOrgRoutes(app, options.orgRoutes);
  }

  // User management routes (admin panel)
  if (options?.userManagement !== undefined) {
    await registerUserManagementRoutes(app, options.userManagement);
  }

  // Admin routes (system health)
  if (options?.admin !== undefined) {
    await registerAdminRoutes(app, options.admin);
  }

  // Engine context routes (Elsa activity callbacks — always register when enabled)
  if (options?.engineContext) {
    const contextOpts = typeof options.engineContext === 'object' ? options.engineContext : undefined;
    await registerEngineContextRoutes(app, contextOpts);
  }

  // Engine GitHub routes (Elsa activity callbacks for GitHub operations)
  if (options?.engineGitHub !== undefined) {
    await registerEngineGitHubRoutes(app, options.engineGitHub);
  }

  // Engine task routes (Elsa activity callbacks for LLM execution and cycle results)
  if (options?.engineTask !== undefined) {
    await registerEngineTaskRoutes(app, options.engineTask);
  }

  // Prompt Registry routes (always registered — uses default store if none provided)
  {
    const promptStore = options?.promptStore ?? new PromptStore();
    await registerPromptRoutes(app, promptStore);
  }

  // Convention template routes (always registered — read-only reference data)
  await registerConventionTemplateRoutes(app);

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
