# Epic Technical Specification: Deployment, Packaging & Operations

Date: 2025-10-29
Author: meywd
Epic ID: 1.5
Status: Draft

---

## Overview

Epic 1.5 establishes the deployment infrastructure and packaging strategy that enables Tamma to operate across multiple environments and installation methods. This epic addresses the critical gap between core functionality (Epic 1) and production readiness by implementing flexible deployment modes (CLI, Service, Web, Container, Kubernetes), webhook-triggered automation, unified configuration management, and comprehensive packaging for distribution via npm, standalone binaries, and OS-specific installers.

This epic delivers the operational foundation required for Tamma's self-maintenance MVP goal: without multiple deployment modes and webhook integration, Tamma cannot autonomously respond to issue assignments or PR events; without npm packaging and binary releases, users cannot easily install and run Tamma; without unified configuration, managing Tamma across environments becomes prohibitively complex. By implementing 5 deployment modes, webhook event processing, system configuration management, and 3 packaging strategies, Epic 1.5 enables Tamma to operate as a CLI tool (developer workstations), background service (CI/CD runners), web server (hosted environments), Docker container (reproducible deployments), or Kubernetes cluster (enterprise scale).

**MVP Criticality:** Stories 1.5-1 through 1.5-9 are MVP CRITICAL - Tamma cannot achieve self-maintenance capability without deployment infrastructure (service mode + webhooks for automatic triggering) and packaging (npm + binaries for distribution). Story 1.5-10 (Kubernetes) is MVP OPTIONAL but valuable for enterprise adoption.

## Objectives and Scope

**In Scope:**
- Story 1.5-1: Core Engine Separation - extract autonomous loop logic into @tamma/core package with launch wrappers
- Story 1.5-2: CLI Mode Enhancement - improve interactive CLI with better prompts, progress tracking, error handling
- Story 1.5-3: Service Mode Implementation - background daemon with issue/PR event listening and automatic triggering
- Story 1.5-4: Web Server & API - REST API server with webhook endpoints, task queue, authentication
- Story 1.5-5: Docker Packaging - multi-stage Dockerfile, docker-compose for local testing, image publishing to registries
- Story 1.5-6: Webhook Integration - GitHub/GitLab webhook processing with signature verification and event filtering
- Story 1.5-7: System Configuration Management - unified config schema across all deployment modes with validation
- Story 1.5-8: NPM Package Publishing - publish @tamma/cli, @tamma/core, @tamma/server to npm registry with CI/CD automation
- Story 1.5-9: Binary Releases & Installers - standalone executables (pkg/nexe), Homebrew tap, Chocolatey package, APT/Snap packages
- Story 1.5-10: Kubernetes Deployment - Helm charts, deployment manifests, StatefulSet for orchestrator, DaemonSet for workers (OPTIONAL)

**Out of Scope:**
- Initial marketing website (Story 1-12 in Epic 1)
- Observability dashboards and monitoring infrastructure (Epic 5)
- Advanced deployment strategies (blue-green, canary, A/B testing) - deferred to post-MVP
- Multi-region deployment and geographic distribution - deferred to post-MVP
- Auto-scaling and capacity planning - deferred to post-MVP

## System Architecture Alignment

Epic 1.5 implements the deployment and packaging architecture defined in the PRD functional requirements FR-37 through FR-40. The core engine separation (Story 1.5-1) establishes the modular architecture where `@tamma/core` contains the autonomous development loop logic, while launch wrappers (`@tamma/cli`, `@tamma/server`) provide environment-specific initialization. This aligns with the hybrid orchestrator/worker architecture from Epic 1, ensuring consistent behavior across all deployment modes.

The webhook integration (Story 1.5-6) implements the event-driven architecture pattern from architecture.md section 3.3, enabling Tamma to respond to GitHub/GitLab events (issue assignment, PR creation) automatically. The unified configuration management (Story 1.5-7) extends the `packages/config` package from Epic 1 to support environment-specific overrides (dev, staging, production) while maintaining type safety through JSON Schema validation.

All implementations follow the TypeScript 5.7+ strict mode, Node.js 22 LTS runtime, and pnpm workspace monorepo structure. The Docker packaging (Story 1.5-5) uses multi-stage builds to minimize image size, while the Kubernetes deployment (Story 1.5-10) follows cloud-native best practices with Helm charts for configuration management and StatefulSets for stateful orchestrator components.

## Detailed Design

### Services and Modules

**1. Core Engine Separation (`@tamma/core` package - Story 1.5-1)**

Extract autonomous development loop logic from CLI-specific code into a reusable core package:

```typescript
// @tamma/core/src/engine.ts
export class TammaEngine {
  private aiProvider: IAIProvider;
  private gitPlatform: IGitPlatform;
  private config: TammaConfig;

  constructor(config: TammaConfig) {
    this.config = config;
    this.aiProvider = createProvider(config.aiProvider);
    this.gitPlatform = createPlatform(config.gitPlatform);
  }

  async initialize(): Promise<void> {
    await this.aiProvider.initialize(this.config.aiProvider);
    await this.gitPlatform.initialize(this.config.gitPlatform);
  }

  // Core autonomous loop methods
  async selectIssue(filters: IssueFilters): Promise<Issue | null>;
  async analyzeIssue(issue: Issue): Promise<IssueAnalysis>;
  async generatePlan(analysis: IssueAnalysis): Promise<DevelopmentPlan>;
  async createBranch(plan: DevelopmentPlan): Promise<Branch>;
  async implementCode(plan: DevelopmentPlan): Promise<CodeChanges>;
  async runTests(changes: CodeChanges): Promise<TestResults>;
  async createPullRequest(changes: CodeChanges): Promise<PullRequest>;
  async monitorPR(pr: PullRequest): Promise<PRStatus>;
  async mergePR(pr: PullRequest): Promise<MergeResult>;

  async dispose(): Promise<void> {
    await this.aiProvider.dispose();
  }
}

// Launch wrapper interface for different modes
export interface LaunchContext {
  mode: 'cli' | 'service' | 'web' | 'worker';
  config: TammaConfig;
  logger: ILogger;
}

export async function createEngine(context: LaunchContext): Promise<TammaEngine> {
  const config = await loadConfig(context.config);
  return new TammaEngine(config);
}
```

**Package Structure:**
```
packages/
├── core/                    # @tamma/core - Core engine logic
│   ├── src/
│   │   ├── engine.ts        # TammaEngine class
│   │   ├── workflow/        # Workflow step implementations
│   │   ├── state/           # State management
│   │   └── index.ts         # Public API exports
│   └── package.json
├── cli/                     # @tamma/cli - CLI launch wrapper
│   ├── src/
│   │   ├── commands/        # CLI commands (run, config, init)
│   │   ├── prompts/         # Interactive prompts
│   │   ├── index.ts         # CLI entry point
│   │   └── bin.ts           # Shebang executable
│   └── package.json
├── server/                  # @tamma/server - Web/Service launch wrapper
│   ├── src/
│   │   ├── api/             # REST API routes
│   │   ├── webhooks/        # Webhook handlers
│   │   ├── queue/           # Task queue implementation
│   │   ├── service.ts       # Service mode daemon
│   │   ├── web.ts           # Web server
│   │   └── index.ts         # Server entry points
│   └── package.json
└── config/                  # @tamma/config - Shared configuration
    ├── src/
    │   ├── schema.ts        # JSON Schema definitions
    │   ├── loader.ts        # Config loading/merging
    │   └── validator.ts     # Config validation
    └── package.json
```

**2. CLI Mode Enhancement (`@tamma/cli` - Story 1.5-2)**

Improve CLI user experience with better prompts, progress tracking, and error handling:

```typescript
// @tamma/cli/src/commands/run.ts
export async function runCommand(options: RunOptions) {
  const spinner = ora('Initializing Tamma...').start();

  try {
    const config = await loadConfig(options.config);
    const engine = await createEngine({ mode: 'cli', config, logger });

    spinner.succeed('Tamma initialized');

    // Interactive issue selection if not provided
    let issue: Issue;
    if (options.issue) {
      issue = await engine.selectIssue({ number: options.issue });
    } else {
      const issues = await engine.selectIssue({ limit: 10 });
      issue = await promptIssueSelection(issues);
    }

    console.log(chalk.blue(`\n📋 Working on: ${issue.title} (#${issue.number})\n`));

    // Progress tracking with spinners
    const analysisSpinner = ora('Analyzing issue...').start();
    const analysis = await engine.analyzeIssue(issue);
    analysisSpinner.succeed(`Analysis complete: ${analysis.complexity} complexity`);

    const planSpinner = ora('Generating development plan...').start();
    const plan = await engine.generatePlan(analysis);
    planSpinner.succeed('Development plan ready');

    // Show plan for approval
    console.log('\n📝 Proposed Plan:\n');
    console.log(formatPlan(plan));

    const approved = await confirm({ message: 'Approve this plan?' });
    if (!approved) {
      console.log(chalk.yellow('Plan rejected. Exiting.'));
      return;
    }

    // Execute plan with live progress updates
    await engine.createBranch(plan);
    await engine.implementCode(plan);
    await engine.runTests();
    await engine.createPullRequest();

    console.log(chalk.green(`\n✅ Pull request created successfully!\n`));

  } catch (error) {
    spinner.fail('Error occurred');

    // Enhanced error handling with suggestions
    if (error instanceof ProviderError) {
      console.error(chalk.red(`\n❌ AI Provider Error: ${error.message}`));
      console.error(chalk.yellow(`\n💡 Suggestion: Check your API key in ${config.path}`));
    } else if (error instanceof PlatformError) {
      console.error(chalk.red(`\n❌ Git Platform Error: ${error.message}`));
      console.error(chalk.yellow(`\n💡 Suggestion: Verify repository access and credentials`));
    } else {
      console.error(chalk.red(`\n❌ Unexpected Error: ${error.message}`));
      console.error(chalk.yellow(`\n💡 Run with --debug for detailed logs`));
    }

    process.exit(1);
  }
}

// Interactive prompts using @inquirer/prompts
async function promptIssueSelection(issues: Issue[]): Promise<Issue> {
  const choices = issues.map(issue => ({
    name: `#${issue.number}: ${issue.title} (${issue.labels.join(', ')})`,
    value: issue.number,
    description: truncate(issue.body, 100),
  }));

  const answer = await select({
    message: 'Select an issue to work on:',
    choices,
  });

  return issues.find(i => i.number === answer)!;
}
```

**3. Service Mode Implementation (`@tamma/server` - Story 1.5-3)**

Background daemon that listens for webhook events and automatically triggers autonomous loops:

```typescript
// @tamma/server/src/service.ts
export class TammaService {
  private engine: TammaEngine;
  private queue: TaskQueue;
  private pollInterval: NodeJS.Timeout | null = null;

  constructor(private config: ServiceConfig) {
    this.engine = new TammaEngine(config.tamma);
    this.queue = new TaskQueue(config.queue);
  }

  async start(): Promise<void> {
    await this.engine.initialize();
    await this.queue.connect();

    logger.info('Tamma service started', { mode: 'service' });

    // Poll queue for tasks
    this.pollInterval = setInterval(async () => {
      await this.processPendingTasks();
    }, this.config.pollInterval);

    // Register signal handlers for graceful shutdown
    process.on('SIGTERM', () => this.shutdown());
    process.on('SIGINT', () => this.shutdown());
  }

  async processPendingTasks(): Promise<void> {
    const task = await this.queue.dequeue();
    if (!task) return;

    logger.info('Processing task', { taskId: task.id, type: task.type });

    try {
      switch (task.type) {
        case 'issue_assigned':
          await this.handleIssueAssigned(task.payload);
          break;
        case 'pr_review_requested':
          await this.handlePRReviewRequested(task.payload);
          break;
        case 'issue_comment':
          await this.handleIssueComment(task.payload);
          break;
        default:
          logger.warn('Unknown task type', { type: task.type });
      }

      await this.queue.complete(task.id);
      logger.info('Task completed', { taskId: task.id });

    } catch (error) {
      logger.error('Task failed', { taskId: task.id, error });
      await this.queue.fail(task.id, error.message);

      // Retry logic with exponential backoff
      if (task.retries < 3) {
        await this.queue.retry(task.id, task.retries + 1);
      } else {
        await this.escalateTask(task, error);
      }
    }
  }

  private async handleIssueAssigned(payload: IssueAssignedPayload): Promise<void> {
    const issue = await this.engine.selectIssue({ number: payload.issue.number });
    const analysis = await this.engine.analyzeIssue(issue);
    const plan = await this.engine.generatePlan(analysis);

    // In service mode, auto-approve if complexity is low
    if (analysis.complexity === 'low' && this.config.autoApprove.lowComplexity) {
      await this.engine.createBranch(plan);
      await this.engine.implementCode(plan);
      await this.engine.runTests();
      await this.engine.createPullRequest();
    } else {
      // Escalate for human approval
      await this.escalateForApproval(issue, plan);
    }
  }

  async shutdown(): Promise<void> {
    logger.info('Shutting down Tamma service...');

    if (this.pollInterval) {
      clearInterval(this.pollInterval);
    }

    await this.queue.disconnect();
    await this.engine.dispose();

    logger.info('Tamma service stopped');
    process.exit(0);
  }
}

// Service mode entry point
export async function startService(config: ServiceConfig): Promise<void> {
  const service = new TammaService(config);
  await service.start();
}
```

**4. Web Server & API (`@tamma/server` - Story 1.5-4)**

REST API server with webhook endpoints, task queue, and authentication:

```typescript
// @tamma/server/src/web.ts
import Fastify from 'fastify';
import { fastifyAuth } from '@fastify/auth';
import { fastifyJwt } from '@fastify/jwt';

export async function createWebServer(config: WebServerConfig): Promise<FastifyInstance> {
  const fastify = Fastify({ logger: true });

  // JWT authentication
  await fastify.register(fastifyJwt, {
    secret: config.jwtSecret,
  });

  await fastify.register(fastifyAuth);

  // Health check
  fastify.get('/health', async () => {
    return { status: 'ok', timestamp: Date.now() };
  });

  // Webhook endpoints
  fastify.post('/webhooks/github', async (request, reply) => {
    const signature = request.headers['x-hub-signature-256'];
    if (!verifyGitHubSignature(request.body, signature, config.githubWebhookSecret)) {
      return reply.code(401).send({ error: 'Invalid signature' });
    }

    const event = request.headers['x-github-event'];
    await handleGitHubWebhook(event, request.body);

    return { status: 'accepted' };
  });

  fastify.post('/webhooks/gitlab', async (request, reply) => {
    const token = request.headers['x-gitlab-token'];
    if (token !== config.gitlabWebhookToken) {
      return reply.code(401).send({ error: 'Invalid token' });
    }

    const event = request.headers['x-gitlab-event'];
    await handleGitLabWebhook(event, request.body);

    return { status: 'accepted' };
  });

  // Task API (authenticated)
  fastify.post('/api/v1/tasks', {
    preHandler: fastify.auth([fastify.verifyJWT]),
  }, async (request, reply) => {
    const task = await taskQueue.enqueue(request.body);
    return { taskId: task.id, status: 'queued' };
  });

  fastify.get('/api/v1/tasks/:id', {
    preHandler: fastify.auth([fastify.verifyJWT]),
  }, async (request, reply) => {
    const task = await taskQueue.get(request.params.id);
    if (!task) {
      return reply.code(404).send({ error: 'Task not found' });
    }
    return task;
  });

  fastify.get('/api/v1/tasks', {
    preHandler: fastify.auth([fastify.verifyJWT]),
  }, async (request, reply) => {
    const tasks = await taskQueue.list(request.query);
    return tasks;
  });

  // Metrics endpoint (Prometheus)
  fastify.get('/metrics', async () => {
    return metricsRegistry.metrics();
  });

  return fastify;
}

// Web server entry point
export async function startWebServer(config: WebServerConfig): Promise<void> {
  const server = await createWebServer(config);
  await server.listen({ port: config.port, host: config.host });
  logger.info(`Tamma web server listening on ${config.host}:${config.port}`);
}
```

**5. Docker Packaging (Story 1.5-5)**

Multi-stage Dockerfile for optimized image size and layer caching:

```dockerfile
# Build stage
FROM node:22-alpine AS builder

WORKDIR /app

# Install pnpm
RUN corepack enable && corepack prepare pnpm@latest --activate

# Copy package files
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml ./
COPY packages/core/package.json ./packages/core/
COPY packages/cli/package.json ./packages/cli/
COPY packages/server/package.json ./packages/server/
COPY packages/config/package.json ./packages/config/
COPY packages/providers/package.json ./packages/providers/
COPY packages/platforms/package.json ./packages/platforms/

# Install dependencies
RUN pnpm install --frozen-lockfile

# Copy source code
COPY packages/ ./packages/
COPY tsconfig.json ./

# Build all packages
RUN pnpm run build

# Production stage
FROM node:22-alpine

WORKDIR /app

RUN corepack enable && corepack prepare pnpm@latest --activate

# Copy built packages and dependencies
COPY --from=builder /app/package.json /app/pnpm-lock.yaml ./
COPY --from=builder /app/node_modules ./node_modules
COPY --from=builder /app/packages/core/dist ./packages/core/dist
COPY --from=builder /app/packages/core/package.json ./packages/core/
COPY --from=builder /app/packages/server/dist ./packages/server/dist
COPY --from=builder /app/packages/server/package.json ./packages/server/

# Create non-root user
RUN addgroup -g 1001 -S tamma && adduser -u 1001 -S tamma -G tamma
USER tamma

# Environment variables
ENV NODE_ENV=production
ENV TAMMA_MODE=service

# Expose API port
EXPOSE 3000

# Health check
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
  CMD node -e "require('http').get('http://localhost:3000/health', (r) => r.statusCode === 200 ? process.exit(0) : process.exit(1))"

# Start Tamma service
CMD ["node", "packages/server/dist/service.js"]
```

**docker-compose.yml for local development:**

```yaml
version: '3.8'

services:
  tamma-orchestrator:
    build:
      context: .
      dockerfile: Dockerfile
    environment:
      - TAMMA_MODE=service
      - TAMMA_AI_PROVIDER=anthropic-claude
      - TAMMA_GIT_PLATFORM=github
      - ANTHROPIC_API_KEY=${ANTHROPIC_API_KEY}
      - GITHUB_TOKEN=${GITHUB_TOKEN}
      - POSTGRES_URL=postgresql://tamma:tamma@postgres:5432/tamma
    ports:
      - "3000:3000"
    depends_on:
      - postgres
      - redis
    volumes:
      - ./config:/app/config:ro
    restart: unless-stopped

  postgres:
    image: postgres:16-alpine
    environment:
      - POSTGRES_USER=tamma
      - POSTGRES_PASSWORD=tamma
      - POSTGRES_DB=tamma
    volumes:
      - postgres-data:/var/lib/postgresql/data
    ports:
      - "5432:5432"

  redis:
    image: redis:7-alpine
    ports:
      - "6379:6379"
    volumes:
      - redis-data:/data

volumes:
  postgres-data:
  redis-data:
```

**6. Webhook Integration (Story 1.5-6)**

GitHub and GitLab webhook processing with signature verification:

```typescript
// @tamma/server/src/webhooks/github.ts
import crypto from 'crypto';

export function verifyGitHubSignature(
  payload: any,
  signature: string,
  secret: string
): boolean {
  const expectedSignature = 'sha256=' + crypto
    .createHmac('sha256', secret)
    .update(JSON.stringify(payload))
    .digest('hex');

  return crypto.timingSafeEqual(
    Buffer.from(signature),
    Buffer.from(expectedSignature)
  );
}

export async function handleGitHubWebhook(
  event: string,
  payload: any
): Promise<void> {
  logger.info('GitHub webhook received', { event, action: payload.action });

  switch (event) {
    case 'issues':
      await handleIssuesEvent(payload);
      break;
    case 'issue_comment':
      await handleIssueCommentEvent(payload);
      break;
    case 'pull_request':
      await handlePullRequestEvent(payload);
      break;
    case 'pull_request_review':
      await handlePRReviewEvent(payload);
      break;
    default:
      logger.debug('Unhandled GitHub event', { event });
  }
}

async function handleIssuesEvent(payload: any): Promise<void> {
  const { action, issue, assignee } = payload;

  // Only trigger on assignment to Tamma bot user
  if (action === 'assigned' && assignee.login === config.botUsername) {
    await taskQueue.enqueue({
      type: 'issue_assigned',
      priority: determineIssuePriority(issue),
      payload: {
        repository: payload.repository.full_name,
        issue: {
          number: issue.number,
          title: issue.title,
          body: issue.body,
          labels: issue.labels.map(l => l.name),
        },
      },
    });

    logger.info('Issue assigned to Tamma', {
      repo: payload.repository.full_name,
      issue: issue.number
    });
  }
}

async function handleIssueCommentEvent(payload: any): Promise<void> {
  const { action, issue, comment } = payload;

  // Check if comment mentions Tamma bot
  if (action === 'created' && comment.body.includes(`@${config.botUsername}`)) {
    await taskQueue.enqueue({
      type: 'issue_comment',
      payload: {
        repository: payload.repository.full_name,
        issue: issue.number,
        comment: {
          id: comment.id,
          body: comment.body,
          author: comment.user.login,
        },
      },
    });
  }
}

// @tamma/server/src/webhooks/gitlab.ts
export async function handleGitLabWebhook(
  event: string,
  payload: any
): Promise<void> {
  logger.info('GitLab webhook received', { event });

  switch (event) {
    case 'Issue Hook':
      await handleGitLabIssueHook(payload);
      break;
    case 'Note Hook':
      await handleGitLabNoteHook(payload);
      break;
    case 'Merge Request Hook':
      await handleGitLabMRHook(payload);
      break;
    default:
      logger.debug('Unhandled GitLab event', { event });
  }
}

async function handleGitLabIssueHook(payload: any): Promise<void> {
  const { object_attributes, assignees } = payload;

  // Check if Tamma bot is in assignees
  const tammaAssigned = assignees?.some(a => a.username === config.botUsername);

  if (tammaAssigned && object_attributes.action === 'update') {
    await taskQueue.enqueue({
      type: 'issue_assigned',
      priority: determineIssuePriority(object_attributes),
      payload: {
        repository: payload.project.path_with_namespace,
        issue: {
          number: object_attributes.iid,
          title: object_attributes.title,
          body: object_attributes.description,
          labels: object_attributes.labels.map(l => l.title),
        },
      },
    });
  }
}
```

**7. System Configuration Management (Story 1.5-7)**

Unified configuration schema with environment-specific overrides:

```typescript
// @tamma/config/src/schema.ts
import { z } from 'zod';

export const TammaConfigSchema = z.object({
  // Core configuration
  mode: z.enum(['cli', 'service', 'web', 'worker']),
  logLevel: z.enum(['error', 'warn', 'info', 'debug', 'trace']),

  // AI Provider configuration
  aiProvider: z.object({
    default: z.string(),
    providers: z.array(z.object({
      name: z.string(),
      enabled: z.boolean(),
      apiKey: z.string().optional(),
      baseUrl: z.string().url().optional(),
      model: z.string().optional(),
      options: z.record(z.any()).optional(),
    })),
    perWorkflowProviders: z.record(z.string()).optional(),
  }),

  // Git Platform configuration
  gitPlatform: z.object({
    default: z.string(),
    platforms: z.array(z.object({
      name: z.string(),
      enabled: z.boolean(),
      token: z.string().optional(),
      baseUrl: z.string().url().optional(),
      options: z.record(z.any()).optional(),
    })),
  }),

  // Service mode configuration
  service: z.object({
    pollInterval: z.number().min(1000).max(60000),
    maxConcurrentTasks: z.number().min(1).max(20),
    autoApprove: z.object({
      lowComplexity: z.boolean(),
      mediumComplexity: z.boolean(),
    }),
  }).optional(),

  // Web server configuration
  web: z.object({
    host: z.string(),
    port: z.number().min(1).max(65535),
    jwtSecret: z.string().min(32),
    corsOrigins: z.array(z.string()),
  }).optional(),

  // Webhook configuration
  webhooks: z.object({
    github: z.object({
      enabled: z.boolean(),
      secret: z.string(),
      botUsername: z.string(),
    }).optional(),
    gitlab: z.object({
      enabled: z.boolean(),
      token: z.string(),
      botUsername: z.string(),
    }).optional(),
  }).optional(),

  // Database configuration
  database: z.object({
    url: z.string().url(),
    ssl: z.boolean().optional(),
    poolSize: z.number().min(1).max(100).optional(),
  }).optional(),

  // Queue configuration
  queue: z.object({
    type: z.enum(['memory', 'redis', 'sqs']),
    redis: z.object({
      url: z.string().url(),
    }).optional(),
    sqs: z.object({
      queueUrl: z.string(),
      region: z.string(),
    }).optional(),
  }).optional(),
});

export type TammaConfig = z.infer<typeof TammaConfigSchema>;

// @tamma/config/src/loader.ts
export class ConfigLoader {
  async load(configPath?: string): Promise<TammaConfig> {
    // Load in order of precedence (lowest to highest):
    // 1. Default config
    // 2. Config file (~/.tamma/config.yaml)
    // 3. Environment-specific config (~/.tamma/config.production.yaml)
    // 4. Environment variables (TAMMA_*)
    // 5. Command-line arguments

    const defaultConfig = this.getDefaultConfig();
    const fileConfig = await this.loadConfigFile(configPath);
    const envConfig = this.loadEnvironmentConfig();
    const cliConfig = this.parseCommandLineArgs();

    const merged = this.mergeConfigs([
      defaultConfig,
      fileConfig,
      envConfig,
      cliConfig,
    ]);

    // Validate against schema
    const validated = TammaConfigSchema.parse(merged);

    return validated;
  }

  private async loadConfigFile(configPath?: string): Promise<Partial<TammaConfig>> {
    const path = configPath || this.getDefaultConfigPath();

    if (!fs.existsSync(path)) {
      return {};
    }

    const content = await fs.promises.readFile(path, 'utf-8');

    if (path.endsWith('.json')) {
      return JSON.parse(content);
    } else if (path.endsWith('.yaml') || path.endsWith('.yml')) {
      return yaml.parse(content);
    } else if (path.endsWith('.toml')) {
      return toml.parse(content);
    }

    throw new Error(`Unsupported config file format: ${path}`);
  }

  private loadEnvironmentConfig(): Partial<TammaConfig> {
    // Map environment variables to config structure
    return {
      mode: process.env.TAMMA_MODE as any,
      logLevel: process.env.TAMMA_LOG_LEVEL as any,
      aiProvider: {
        default: process.env.TAMMA_AI_PROVIDER,
      },
      gitPlatform: {
        default: process.env.TAMMA_GIT_PLATFORM,
      },
      database: {
        url: process.env.DATABASE_URL,
      },
    };
  }
}
```

**Example configuration file (`~/.tamma/config.yaml`):**

```yaml
mode: service
logLevel: info

aiProvider:
  default: anthropic-claude
  providers:
    - name: anthropic-claude
      enabled: true
      apiKey: ${ANTHROPIC_API_KEY}
      model: claude-3-5-sonnet-20241022
    - name: openai
      enabled: true
      apiKey: ${OPENAI_API_KEY}
      model: gpt-4o
  perWorkflowProviders:
    issue-analysis: anthropic-claude
    code-generation: anthropic-claude
    test-generation: openai
    code-review: anthropic-claude

gitPlatform:
  default: github
  platforms:
    - name: github
      enabled: true
      token: ${GITHUB_TOKEN}
    - name: gitlab
      enabled: false
      token: ${GITLAB_TOKEN}

service:
  pollInterval: 5000
  maxConcurrentTasks: 3
  autoApprove:
    lowComplexity: true
    mediumComplexity: false

webhooks:
  github:
    enabled: true
    secret: ${GITHUB_WEBHOOK_SECRET}
    botUsername: tamma-bot
  gitlab:
    enabled: false
    token: ${GITLAB_WEBHOOK_TOKEN}
    botUsername: tamma-bot

database:
  url: ${DATABASE_URL}
  ssl: true
  poolSize: 10

queue:
  type: redis
  redis:
    url: ${REDIS_URL}
```

### Data Models and Contracts

**Deployment Mode Enum:**
```typescript
export enum DeploymentMode {
  CLI = 'cli',           // Interactive terminal mode
  SERVICE = 'service',   // Background daemon mode
  WEB = 'web',          // REST API server mode
  WORKER = 'worker',    // CI/CD worker mode
}
```

**Task Queue Contract:**
```typescript
export interface Task {
  id: string;
  type: TaskType;
  priority: TaskPriority;
  payload: any;
  status: TaskStatus;
  retries: number;
  createdAt: Date;
  startedAt?: Date;
  completedAt?: Date;
  error?: string;
}

export enum TaskType {
  ISSUE_ASSIGNED = 'issue_assigned',
  ISSUE_COMMENT = 'issue_comment',
  PR_REVIEW_REQUESTED = 'pr_review_requested',
  PR_COMMENT = 'pr_comment',
}

export enum TaskPriority {
  LOW = 0,
  MEDIUM = 1,
  HIGH = 2,
  CRITICAL = 3,
}

export enum TaskStatus {
  QUEUED = 'queued',
  IN_PROGRESS = 'in_progress',
  COMPLETED = 'completed',
  FAILED = 'failed',
  CANCELLED = 'cancelled',
}
```

**Webhook Event Contracts:**
```typescript
export interface GitHubIssueEvent {
  action: 'assigned' | 'unassigned' | 'opened' | 'closed' | 'reopened';
  issue: {
    number: number;
    title: string;
    body: string;
    labels: Array<{ name: string }>;
    assignees: Array<{ login: string }>;
  };
  repository: {
    full_name: string;
  };
  assignee?: {
    login: string;
  };
}

export interface GitLabIssueHook {
  object_kind: 'issue';
  object_attributes: {
    id: number;
    iid: number;
    title: string;
    description: string;
    action: 'open' | 'update' | 'close' | 'reopen';
    labels: Array<{ title: string }>;
  };
  assignees: Array<{ username: string }>;
  project: {
    path_with_namespace: string;
  };
}
```

### APIs and Interfaces

**1. Core Engine Public API**

```typescript
export interface ITammaEngine {
  // Lifecycle
  initialize(): Promise<void>;
  dispose(): Promise<void>;

  // Workflow methods
  selectIssue(filters: IssueFilters): Promise<Issue | null>;
  analyzeIssue(issue: Issue): Promise<IssueAnalysis>;
  generatePlan(analysis: IssueAnalysis): Promise<DevelopmentPlan>;
  executePlan(plan: DevelopmentPlan): Promise<ExecutionResult>;

  // State management
  getState(): EngineState;
  pauseExecution(): Promise<void>;
  resumeExecution(): Promise<void>;
}
```

**2. REST API Endpoints**

```
POST   /webhooks/github           # GitHub webhook receiver
POST   /webhooks/gitlab           # GitLab webhook receiver

POST   /api/v1/tasks              # Submit task
GET    /api/v1/tasks              # List tasks
GET    /api/v1/tasks/:id          # Get task details
DELETE /api/v1/tasks/:id          # Cancel task
GET    /api/v1/tasks/:id/logs     # Stream task logs (SSE)

GET    /api/v1/config             # Get current configuration
PUT    /api/v1/config             # Update configuration
POST   /api/v1/config/validate    # Validate configuration

GET    /health                    # Health check
GET    /metrics                   # Prometheus metrics
GET    /ready                     # Readiness probe
GET    /live                      # Liveness probe
```

**3. Task Queue Interface**

```typescript
export interface ITaskQueue {
  enqueue(task: TaskPayload): Promise<Task>;
  dequeue(): Promise<Task | null>;
  get(taskId: string): Promise<Task | null>;
  list(filters: TaskFilters): Promise<Task[]>;
  complete(taskId: string): Promise<void>;
  fail(taskId: string, error: string): Promise<void>;
  retry(taskId: string, retryCount: number): Promise<void>;
  cancel(taskId: string): Promise<void>;
}

// Implementations
export class MemoryTaskQueue implements ITaskQueue { }
export class RedisTaskQueue implements ITaskQueue { }
export class SQSTaskQueue implements ITaskQueue { }
```

### Workflows and Sequencing

**1. Service Mode Startup Sequence**

```
1. Load configuration from file, environment, CLI args
2. Validate configuration against schema
3. Initialize logger with configured log level
4. Connect to database (if configured)
5. Connect to task queue (Redis/SQS/memory)
6. Initialize TammaEngine
   a. Initialize AI provider(s)
   b. Initialize Git platform(s)
7. Start task polling loop
8. Register signal handlers (SIGTERM, SIGINT)
9. Service running - process tasks from queue
```

**2. Webhook Event Processing Flow**

```
GitHub Webhook Received → Verify Signature → Parse Event Type
  ↓
Filter Event (only process relevant events)
  ↓
Issue Assigned Event:
  1. Extract issue details from webhook payload
  2. Determine task priority based on issue labels/complexity
  3. Enqueue task to task queue with payload
  4. Return 200 OK to GitHub (acknowledge webhook)
  ↓
Task Queue Processing (async):
  1. Dequeue task from queue
  2. Create TammaEngine instance
  3. Execute autonomous development loop:
     - Select issue (already known from webhook)
     - Analyze issue
     - Generate plan
     - Auto-approve if complexity is low (service mode setting)
     - Otherwise, escalate for human approval
     - Create branch, implement code, run tests
     - Create pull request
     - Monitor PR for CI/CD status
  4. Mark task as completed or failed
  5. If failed with retries < 3, retry with exponential backoff
  6. If failed with retries >= 3, escalate to human
```

**3. Docker Container Startup Sequence**

```
1. Container starts with CMD ["node", "packages/server/dist/service.js"]
2. Environment variables loaded (TAMMA_MODE, API keys, DB URL)
3. Config loaded from /app/config/config.yaml (if mounted as volume)
4. Service mode starts (see Service Mode Startup Sequence above)
5. Health check endpoint responds on /health
6. Container ready to receive webhook events
```

## Non-Functional Requirements

### Performance

**CLI Mode:**
- Command startup time: <2 seconds (cold start)
- Interactive prompt response time: <100ms
- Configuration loading time: <500ms

**Service Mode:**
- Task queue polling interval: Configurable (default 5 seconds)
- Task processing throughput: 3-5 concurrent tasks (configurable)
- Webhook response time: <200ms (acknowledge and enqueue)
- Queue latency: <50ms for enqueue/dequeue operations

**Web Server:**
- API request latency: <100ms for simple operations (health, config get)
- API request latency: <500ms for complex operations (task creation)
- Webhook processing latency: <200ms (signature verification + enqueue)
- WebSocket connection handling: 100+ concurrent connections
- Server startup time: <5 seconds

**Docker:**
- Image size: <500MB (optimized with multi-stage build)
- Container startup time: <10 seconds (including health check)
- Memory usage: <512MB idle, <2GB under load
- CPU usage: <10% idle, <100% per core under load

### Security

**Authentication:**
- JWT tokens for API authentication (RS256 algorithm)
- Token expiration: 1 hour (access tokens), 7 days (refresh tokens)
- API key rotation support for providers and platforms

**Webhook Security:**
- GitHub webhook signature verification (HMAC-SHA256)
- GitLab webhook token verification
- Reject webhooks with invalid signatures/tokens
- Rate limiting: 100 requests/minute per IP address

**Configuration Security:**
- Secrets stored in environment variables (never committed to git)
- Support for secret management services (AWS Secrets Manager, Vault)
- Configuration file permissions: 600 (owner read/write only)
- API keys never logged or exposed in error messages

**Docker Security:**
- Run as non-root user (uid 1001)
- Minimal base image (alpine)
- No unnecessary packages in production image
- Image scanning for vulnerabilities (Trivy, Snyk)

### Reliability/Availability

**Error Handling:**
- All exceptions caught and logged with context
- Graceful degradation for optional dependencies (Redis unavailable → use memory queue)
- Retry logic with exponential backoff for transient failures
- Maximum 3 retries before escalation to human

**Process Management:**
- Graceful shutdown on SIGTERM/SIGINT (finish in-progress tasks)
- Health checks for container orchestration
- Automatic restart on crash (managed by Docker/Kubernetes)

**Data Durability:**
- Task queue persistence (Redis AOF, SQS message durability)
- Configuration file backups before updates
- Event logs persisted to durable storage (Epic 4)

**Availability Targets:**
- Service mode uptime: 99.5% (3.6 hours downtime per month)
- Web server uptime: 99.5%
- Webhook processing success rate: 99%

### Observability

**Logging:**
- Structured logging with JSON format
- Log levels: error, warn, info, debug, trace
- Contextual logging (include taskId, issueNumber, repoName in all logs)
- Log aggregation via stdout (captured by Docker/Kubernetes)

**Metrics:**
- Task queue depth (gauge)
- Task processing rate (counter)
- Task processing duration (histogram)
- Webhook events received (counter)
- API request latency (histogram)
- Active connections (gauge)

**Health Checks:**
- Liveness probe: /live (checks if process is running)
- Readiness probe: /ready (checks if dependencies are healthy)
- Health check: /health (detailed health status with dependencies)

## Dependencies and Integrations

### External Dependencies

**Runtime Dependencies:**
```json
{
  "dependencies": {
    "@anthropic-ai/sdk": "^0.45.0",
    "@fastify/auth": "^5.0.0",
    "@fastify/cors": "^10.0.0",
    "@fastify/jwt": "^8.0.0",
    "fastify": "^5.0.0",
    "pino": "^9.0.0",
    "pino-pretty": "^11.0.0",
    "zod": "^3.23.0",
    "yaml": "^2.4.0",
    "toml": "^3.0.0",
    "ioredis": "^5.4.0",
    "@inquirer/prompts": "^5.0.0",
    "ora": "^8.0.0",
    "chalk": "^5.3.0"
  },
  "devDependencies": {
    "@types/node": "^22.0.0",
    "typescript": "^5.7.0",
    "tsx": "^4.11.0",
    "vitest": "^2.0.0"
  }
}
```

**Build/Package Dependencies:**
- `pkg@^5.8.0` - Package Node.js app into executable (Story 1.5-9)
- `nexe@^4.0.0` - Alternative executable packager (fallback option)
- `esbuild@^0.23.0` - Fast bundler for reducing package size

### Internal Dependencies

**Epic 1.5 depends on:**
- Epic 1 Story 1-1: IAIProvider interface (core engine requires AI provider)
- Epic 1 Story 1-2: Anthropic Claude provider implementation (default provider)
- Epic 1 Story 1-3: Provider configuration management (config loading)
- Epic 1 Story 1-4: IGitPlatform interface (core engine requires Git platform)
- Epic 1 Story 1-5: GitHub platform implementation (default platform)
- Epic 1 Story 1-9: Basic CLI scaffolding (foundation for CLI enhancement)

**Epic 1.5 provides to:**
- Epic 2: Deployment modes for autonomous loop execution (CLI, service, web)
- Epic 4: Event capture infrastructure (logging, task queue events)
- Epic 5: Observability infrastructure (metrics, health checks, logging)

### External Service Integrations

**Required Integrations:**
- **Anthropic Claude API**: AI provider for code generation
- **GitHub API**: Git platform for repository operations
- **GitHub Webhooks**: Event-driven triggering for issue assignments
- **npm Registry**: Package publishing for @tamma/* packages

**Optional Integrations:**
- **GitLab API**: Alternative Git platform
- **GitLab Webhooks**: Event-driven triggering for GitLab
- **Redis**: Persistent task queue (alternative to in-memory)
- **PostgreSQL**: Event store backend (Epic 4)
- **AWS SQS**: Cloud-based task queue (alternative to Redis)
- **AWS Secrets Manager**: Secret management service
- **Prometheus**: Metrics scraping for observability
- **Docker Hub/GHCR**: Container image registry

### Integration Points

**1. CLI → Core Engine Integration**
```typescript
// @tamma/cli/src/commands/run.ts
import { createEngine, TammaEngine } from '@tamma/core';
import { loadConfig } from '@tamma/config';

const config = await loadConfig();
const engine = await createEngine({ mode: 'cli', config, logger });
await engine.initialize();
await engine.selectIssue();
```

**2. Service Mode → Task Queue Integration**
```typescript
// @tamma/server/src/service.ts
import { RedisTaskQueue } from '@tamma/server/queue';
import { createEngine } from '@tamma/core';

const queue = new RedisTaskQueue(config.queue.redis);
await queue.connect();

const task = await queue.dequeue();
const engine = await createEngine({ mode: 'service', config, logger });
await engine.executePlan(task.payload);
```

**3. Web Server → Webhook Integration**
```typescript
// @tamma/server/src/web.ts
import { handleGitHubWebhook } from '@tamma/server/webhooks';

fastify.post('/webhooks/github', async (request, reply) => {
  verifySignature(request);
  await handleGitHubWebhook(request.headers['x-github-event'], request.body);
  return { status: 'accepted' };
});
```

### Cross-Epic Dependencies

**Epic 1.5 enables:**
- **Epic 2 (Autonomous Loop)**: Provides execution environments for autonomous workflows
- **Epic 3 (Quality Gates)**: Service mode enables continuous quality checking
- **Epic 4 (Event Sourcing)**: Task queue provides event stream for capture
- **Epic 5 (Observability)**: Metrics and logging infrastructure for monitoring

**Epic 1.5 requires:**
- **Epic 1 (Foundation)**: AI/Git abstractions, CLI scaffolding
- **Epic 5 (Observability - Partial)**: Structured logging implementation (Story 5.1)

### Development Environment Dependencies

**Required Tools:**
- Node.js 22 LTS
- pnpm 9+
- Docker 24+
- Docker Compose 2+

**Optional Tools:**
- kubectl (for Kubernetes deployment testing)
- Helm 3+ (for Helm chart development)
- Minikube/Kind (for local Kubernetes testing)

### Configuration Dependencies

**Environment Variables Required:**
```bash
# AI Provider
ANTHROPIC_API_KEY=sk-...

# Git Platform
GITHUB_TOKEN=ghp_...

# Webhooks
GITHUB_WEBHOOK_SECRET=...

# Database (optional)
DATABASE_URL=postgresql://...

# Queue (optional)
REDIS_URL=redis://...

# Web Server (if running in web mode)
JWT_SECRET=...
```

## Acceptance Criteria (Authoritative)

### Story 1.5-1: Core Engine Separation

1. `@tamma/core` package exports `TammaEngine` class with complete autonomous loop implementation
2. Launch wrapper packages (`@tamma/cli`, `@tamma/server`) use core package without code duplication
3. All deployment modes (CLI, service, web, worker) use same core engine logic
4. Configuration passed to core engine through standardized `LaunchContext` interface
5. Core engine has no dependencies on CLI-specific or server-specific packages
6. Unit tests validate core engine isolation and behavior consistency
7. Integration tests validate all deployment modes execute same workflow with same results

### Story 1.5-2: CLI Mode Enhancement

1. CLI startup displays branded banner with version number
2. Interactive issue selection with fuzzy search and preview
3. Progress indicators (spinners) for long-running operations (analysis, code generation)
4. Color-coded output (errors in red, success in green, warnings in yellow)
5. Enhanced error messages with actionable suggestions for resolution
6. `--debug` flag enables verbose logging to file
7. Configuration wizard (`tamma init`) creates config file interactively
8. Unit tests validate CLI command parsing and output formatting
9. Integration tests validate end-to-end CLI workflows

### Story 1.5-3: Service Mode Implementation

1. Service starts as background daemon and listens for task queue events
2. Graceful shutdown on SIGTERM/SIGINT (finishes in-progress tasks, max 30 seconds)
3. Task polling interval configurable (default 5 seconds)
4. Maximum concurrent tasks configurable (default 3)
5. Auto-approval for low-complexity issues (configurable)
6. Retry logic with exponential backoff (max 3 retries)
7. Health check endpoint responds within 100ms
8. Service mode runs without user interaction (no prompts)
9. Unit tests validate task processing logic
10. Integration tests validate service lifecycle (start, process tasks, shutdown)

### Story 1.5-4: Web Server & API

1. REST API server listens on configurable host:port (default 0.0.0.0:3000)
2. JWT authentication for all API endpoints (except webhooks, health)
3. Webhook endpoints for GitHub and GitLab with signature verification
4. Task submission API with validation and queuing
5. Task status API for monitoring task progress
6. Configuration API for runtime config updates (authenticated)
7. Health check, metrics, readiness, liveness endpoints
8. API documentation (OpenAPI/Swagger)
9. Unit tests validate API request/response handling
10. Integration tests validate end-to-end API workflows with authentication

### Story 1.5-5: Docker Packaging

1. Multi-stage Dockerfile builds optimized image (<500MB)
2. Non-root user (uid 1001) for security
3. Health check configured in Dockerfile
4. docker-compose.yml for local development (orchestrator + postgres + redis)
5. Image published to Docker Hub and GitHub Container Registry
6. Environment variable configuration (no config file required)
7. Container starts in <10 seconds and responds to health checks
8. Integration tests validate Docker container startup and basic functionality
9. Security scan passes (no critical vulnerabilities)

### Story 1.5-6: Webhook Integration

1. GitHub webhook signature verification (HMAC-SHA256)
2. GitLab webhook token verification
3. Webhook events enqueued to task queue within 200ms
4. Support for issue assignment, issue comment, PR review events
5. Configurable bot username for filtering relevant events
6. Webhook failure logging with error details
7. Rate limiting (100 requests/minute per IP)
8. Unit tests validate signature verification and event parsing
9. Integration tests validate end-to-end webhook flow (GitHub/GitLab → queue → processing)

### Story 1.5-7: System Configuration Management

1. Configuration loaded from file, environment variables, and CLI args (in that order)
2. Supported formats: JSON, YAML, TOML
3. Environment-specific overrides (config.production.yaml, config.development.yaml)
4. JSON Schema validation with helpful error messages
5. Configuration hot-reload support (service mode restarts on config change)
6. Secrets management integration (environment variables, AWS Secrets Manager)
7. Configuration API for runtime updates
8. Unit tests validate config loading, merging, validation
9. Integration tests validate config loading in all deployment modes

### Story 1.5-8: NPM Package Publishing

1. Packages published to npm registry: @tamma/cli, @tamma/core, @tamma/server, @tamma/config
2. Semantic versioning (semver) enforced
3. Automated release pipeline (GitHub Actions) on git tag push
4. Package README files with installation and usage instructions
5. Changelog generated from commit history (conventional commits)
6. npm audit passes (no critical vulnerabilities)
7. Package size optimized (<5MB per package)
8. Integration tests validate package installation and usage

### Story 1.5-9: Binary Releases & Installers

1. Standalone executables for Windows (x64), macOS (x64, arm64), Linux (x64, arm64)
2. Executables include Node.js runtime (no Node.js installation required)
3. Homebrew tap for macOS installation (`brew install tamma`)
4. Chocolatey package for Windows installation (`choco install tamma`)
5. APT package for Debian/Ubuntu (`apt install tamma`)
6. Snap package for universal Linux installation (`snap install tamma`)
7. Executables digitally signed (macOS, Windows)
8. Binary releases published to GitHub Releases on tag push
9. Installation instructions in documentation
10. Integration tests validate binary execution on each platform

### Story 1.5-10: Kubernetes Deployment (OPTIONAL)

1. Helm chart for Tamma deployment (orchestrator + workers)
2. StatefulSet for orchestrator with persistent volume for state
3. Deployment for workers with horizontal pod autoscaling
4. Service for exposing orchestrator API
5. Ingress for external access with TLS
6. ConfigMap for configuration management
7. Secret for sensitive credentials
8. Liveness and readiness probes configured
9. Resource limits and requests defined
10. Integration tests validate Helm chart installation on Minikube/Kind

## Traceability Mapping

### PRD Requirements → Epic 1.5 Stories

**FR-35 (Initial Marketing Website):**
- Addressed in Epic 1 Story 1-12 (not Epic 1.5)

**FR-36 (Comprehensive Documentation Website):**
- Addressed in Epic 5 Stories 5.9a-5.9d (not Epic 1.5)

**FR-37 (Multiple Deployment Modes):**
- Story 1.5-1: Core Engine Separation (enables all modes)
- Story 1.5-2: CLI Mode Enhancement (CLI mode)
- Story 1.5-3: Service Mode Implementation (Service mode)
- Story 1.5-4: Web Server & API (Web mode)
- Story 1.5-5: Docker Packaging (Container mode)
- Story 1.5-10: Kubernetes Deployment (Cluster mode - OPTIONAL)

**FR-38 (NPM Package Publishing):**
- Story 1.5-8: NPM Package Publishing

**FR-39 (Standalone Binary Releases):**
- Story 1.5-9: Binary Releases & Installers

**FR-40 (Webhook Integration):**
- Story 1.5-6: Webhook Integration
- Story 1.5-3: Service Mode Implementation (processes webhook events)

### Architecture Alignment → Epic 1.5 Stories

**Hybrid Orchestrator/Worker Architecture (Epic 1 Story 1-8):**
- Story 1.5-1: Core Engine Separation (enables hybrid architecture)
- Story 1.5-3: Service Mode (orchestrator mode)
- Story 1.5-4: Web Server (orchestrator API)

**Configuration Management (Epic 1 Story 1-3, 1-7):**
- Story 1.5-7: System Configuration Management (extends to all modes)

**Event-Driven Architecture:**
- Story 1.5-6: Webhook Integration (event-driven triggers)
- Story 1.5-3: Service Mode (event processing)

**Monorepo Package Structure:**
- Story 1.5-1: Core Engine Separation (establishes @tamma/* packages)
- Story 1.5-8: NPM Package Publishing (publishes packages)

### NFR Requirements → Epic 1.5 Stories

**NFR-1 (Performance & Scalability):**
- Story 1.5-3: Service Mode (concurrent task processing)
- Story 1.5-4: Web Server (API request latency)
- Story 1.5-5: Docker Packaging (container startup time)

**NFR-2 (Reliability & Availability):**
- Story 1.5-3: Service Mode (graceful shutdown, retry logic)
- Story 1.5-4: Web Server (health checks, readiness probes)
- Story 1.5-5: Docker Packaging (health check configuration)

**NFR-3 (Security & Compliance):**
- Story 1.5-4: Web Server (JWT authentication)
- Story 1.5-6: Webhook Integration (signature verification)
- Story 1.5-7: Configuration Management (secrets management)

**NFR-4 (Observability):**
- Story 1.5-3: Service Mode (structured logging)
- Story 1.5-4: Web Server (metrics endpoint)
- Story 1.5-5: Docker Packaging (log aggregation via stdout)

## Risks, Assumptions, Open Questions

### Risks

**Risk 1: Package Size Bloat (Story 1.5-9)**
- **Description**: Standalone executables may exceed 100MB due to bundled Node.js runtime
- **Impact**: HIGH - Large downloads discourage adoption
- **Mitigation**: Use esbuild for aggressive tree-shaking, exclude dev dependencies, compress executables
- **Contingency**: Provide "slim" binaries that require Node.js installation separately

**Risk 2: Webhook Event Delivery Reliability (Story 1.5-6)**
- **Description**: GitHub/GitLab webhooks may fail to deliver due to network issues or rate limits
- **Impact**: MEDIUM - Tamma misses issue assignments and cannot self-maintain
- **Mitigation**: Implement webhook retry mechanism, fallback to polling, monitor webhook delivery failures
- **Contingency**: Document manual issue assignment as fallback (CLI mode)

**Risk 3: Configuration Complexity (Story 1.5-7)**
- **Description**: Unified config schema across 5 deployment modes may become too complex
- **Impact**: MEDIUM - Users struggle to configure Tamma correctly
- **Mitigation**: Provide mode-specific config templates, configuration wizard CLI, validation with helpful errors
- **Contingency**: Split configuration into mode-specific files if complexity becomes unmanageable

**Risk 4: Docker Image Security Vulnerabilities (Story 1.5-5)**
- **Description**: Base image or dependencies may contain security vulnerabilities
- **Impact**: HIGH - Tamma cannot be deployed in security-conscious environments
- **Mitigation**: Use minimal base image (alpine), scan images with Trivy/Snyk, automated vulnerability updates
- **Contingency**: Provide alternative base images (distroless, wolfi) if alpine has vulnerabilities

**Risk 5: Kubernetes Complexity (Story 1.5-10)**
- **Description**: Helm chart may be too complex for users unfamiliar with Kubernetes
- **Impact**: LOW (story is OPTIONAL) - Limits enterprise adoption
- **Mitigation**: Provide comprehensive Helm chart documentation, example values.yaml, Terraform modules
- **Contingency**: Defer Kubernetes support to post-MVP if complexity is prohibitive

### Assumptions

**Assumption 1: npm Registry Availability**
- **Statement**: npm registry will be available for package publishing and installation
- **Validation**: Monitor npm status page, implement retry logic in CI/CD pipeline
- **If Invalid**: Use alternative registries (GitHub Packages) or self-hosted registry

**Assumption 2: GitHub/GitLab Webhook Stability**
- **Statement**: Webhook APIs are stable and will not change significantly
- **Validation**: Monitor GitHub/GitLab API changelog, implement API version locking
- **If Invalid**: Implement webhook version negotiation, support multiple API versions

**Assumption 3: Docker/Kubernetes Maturity**
- **Statement**: Docker and Kubernetes are mature platforms with stable APIs
- **Validation**: Use stable API versions (Docker API v1.43, Kubernetes API v1.28)
- **If Invalid**: Provide fallback deployment methods (systemd services, PM2)

**Assumption 4: Node.js 22 LTS Support**
- **Statement**: Node.js 22 LTS will be supported for the duration of MVP development
- **Validation**: Node.js 22 LTS end-of-life is April 2027 (safe for 2+ years)
- **If Invalid**: Support Node.js 20 LTS as fallback (EOL April 2026)

**Assumption 5: User Environment Compatibility**
- **Statement**: Target platforms (Windows, macOS, Linux) support required dependencies
- **Validation**: Test on Windows 10+, macOS 12+, Ubuntu 20.04+
- **If Invalid**: Document minimum platform requirements, provide alternative installation methods

### Open Questions

**Question 1: Webhook Delivery Guarantees**
- **Question**: What SLA do GitHub/GitLab provide for webhook delivery?
- **Answer Needed By**: Story 1.5-6 implementation start
- **Options**: (1) Rely on webhooks only, (2) Implement polling fallback, (3) Hybrid approach
- **Decision Owner**: Technical Lead
- **Current Status**: Research in progress - GitHub does not guarantee webhook delivery

**Question 2: Binary Signing for macOS/Windows**
- **Question**: Do we need code signing certificates for macOS/Windows binaries?
- **Answer Needed By**: Story 1.5-9 implementation start
- **Options**: (1) Sign binaries (requires Apple Developer, Windows EV cert), (2) Unsigned (users see warnings)
- **Decision Owner**: Product Manager
- **Current Status**: Defer to post-MVP - unsigned binaries acceptable for alpha release

**Question 3: Kubernetes Cluster Requirements**
- **Question**: What are minimum Kubernetes cluster requirements (version, resources)?
- **Answer Needed By**: Story 1.5-10 implementation start (OPTIONAL story)
- **Options**: (1) Kubernetes 1.28+, (2) Support older versions (1.24+)
- **Decision Owner**: DevOps Engineer
- **Current Status**: To be determined if story is prioritized

**Question 4: Multi-Tenancy Support**
- **Question**: Should web server mode support multiple tenants (organizations)?
- **Answer Needed By**: Story 1.5-4 design phase
- **Options**: (1) Single-tenant only (MVP), (2) Multi-tenant with tenant isolation
- **Decision Owner**: Product Manager
- **Current Status**: Decision: Single-tenant for MVP, multi-tenant in post-MVP

**Question 5: Configuration Hot-Reload Safety**
- **Question**: Is hot-reload of configuration safe while tasks are in progress?
- **Answer Needed By**: Story 1.5-7 implementation start
- **Options**: (1) Allow hot-reload (may cause inconsistencies), (2) Require service restart, (3) Queue config changes
- **Decision Owner**: Technical Lead
- **Current Status**: To be determined during implementation

## Test Strategy Summary

### Unit Testing Strategy

**Core Engine Tests (Story 1.5-1):**
```typescript
describe('TammaEngine', () => {
  it('should initialize with valid configuration');
  it('should execute complete workflow from issue to PR');
  it('should handle provider initialization failures gracefully');
  it('should dispose resources on shutdown');
  it('should maintain consistent state across operations');
});

describe('LaunchContext', () => {
  it('should create engine for each deployment mode');
  it('should pass configuration to engine correctly');
  it('should initialize logger with correct level');
});
```

**CLI Tests (Story 1.5-2):**
```typescript
describe('CLI Commands', () => {
  it('should parse run command with options');
  it('should display interactive issue selection');
  it('should show progress spinners during execution');
  it('should format errors with helpful suggestions');
  it('should create config file with init command');
});
```

**Service Mode Tests (Story 1.5-3):**
```typescript
describe('TammaService', () => {
  it('should start service and connect to queue');
  it('should process tasks from queue');
  it('should retry failed tasks with exponential backoff');
  it('should shutdown gracefully on SIGTERM');
  it('should auto-approve low-complexity issues');
});
```

**Web Server Tests (Story 1.5-4):**
```typescript
describe('Web Server API', () => {
  it('should authenticate with valid JWT');
  it('should reject requests with invalid JWT');
  it('should enqueue task via API');
  it('should return task status via API');
  it('should respond to health check within 100ms');
});
```

**Webhook Tests (Story 1.5-6):**
```typescript
describe('GitHub Webhook Handler', () => {
  it('should verify webhook signature correctly');
  it('should reject webhooks with invalid signature');
  it('should enqueue task for issue assignment');
  it('should ignore events from non-bot assignees');
});

describe('GitLab Webhook Handler', () => {
  it('should verify webhook token correctly');
  it('should enqueue task for issue assignment');
});
```

**Configuration Tests (Story 1.5-7):**
```typescript
describe('ConfigLoader', () => {
  it('should load configuration from YAML file');
  it('should merge environment variables correctly');
  it('should validate configuration against schema');
  it('should reject invalid configuration with helpful errors');
  it('should support environment-specific overrides');
});
```

### Integration Testing Strategy

**Deployment Mode Integration Tests:**
```typescript
describe('CLI Mode Integration', () => {
  it('should execute complete workflow from command line');
  it('should handle user input for issue selection');
  it('should display progress and complete successfully');
});

describe('Service Mode Integration', () => {
  it('should start service, process task, and shutdown');
  it('should process webhook event end-to-end');
  it('should retry failed tasks automatically');
});

describe('Web Server Integration', () => {
  it('should accept webhook, enqueue task, process, and complete');
  it('should authenticate API requests and return task status');
  it('should export metrics for Prometheus scraping');
});
```

**Docker Integration Tests:**
```typescript
describe('Docker Container', () => {
  it('should start container with docker-compose');
  it('should respond to health check endpoint');
  it('should process webhook event end-to-end');
  it('should shutdown gracefully on SIGTERM');
});
```

**Package Integration Tests:**
```typescript
describe('NPM Package Installation', () => {
  it('should install @tamma/cli from npm');
  it('should run tamma CLI command after installation');
  it('should load configuration correctly');
});

describe('Binary Installation', () => {
  it('should execute standalone binary without Node.js');
  it('should run complete workflow from binary');
});
```

### End-to-End (E2E) Testing Strategy

**E2E Scenario 1: Webhook-Triggered Autonomous PR**
```
1. Start Tamma in service mode
2. Configure GitHub webhook to point to Tamma
3. Assign issue to Tamma bot on GitHub
4. Verify webhook received and processed
5. Verify task created and executed
6. Verify PR created on GitHub
7. Verify service handles PR merge
```

**E2E Scenario 2: CLI Interactive Workflow**
```
1. Run tamma CLI in interactive mode
2. Select issue from list
3. Approve development plan
4. Verify code generated and tests pass
5. Verify PR created successfully
```

**E2E Scenario 3: Docker Compose Local Testing**
```
1. Start docker-compose (orchestrator + postgres + redis)
2. Send webhook to orchestrator
3. Verify task processed and PR created
4. Verify metrics exported
5. Verify graceful shutdown
```

### Performance Testing Strategy

**Load Testing:**
```
- Simulate 100 concurrent webhook events
- Measure task queue depth and processing latency
- Target: <500ms webhook response time, <2 hour task completion time
```

**Stress Testing:**
```
- Simulate 1000 webhook events in 1 minute
- Measure system stability and error rate
- Target: <5% error rate, no crashes
```

**Soak Testing:**
```
- Run service mode for 24 hours under moderate load (10 tasks/hour)
- Monitor memory leaks, CPU usage, queue depth
- Target: Stable memory usage (<2GB), <50% CPU average
```

### Security Testing Strategy

**Webhook Security Tests:**
```
- Test webhook signature verification with invalid signatures
- Test webhook rate limiting with 200 requests/minute
- Test webhook replay attacks (same event twice)
```

**API Security Tests:**
```
- Test JWT authentication with invalid tokens
- Test JWT token expiration and refresh
- Test API authorization (ensure non-admin cannot update config)
```

**Docker Security Tests:**
```
- Scan Docker image with Trivy for vulnerabilities
- Verify container runs as non-root user
- Test container escape attempts (ensure proper isolation)
```

### Continuous Integration Strategy

**CI Pipeline (GitHub Actions):**
```yaml
name: CI
on: [push, pull_request]

jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: pnpm/action-setup@v2
      - uses: actions/setup-node@v4
        with: { node-version: 22 }
      - run: pnpm install --frozen-lockfile
      - run: pnpm test
      - run: pnpm run build

  docker:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: docker/build-push-action@v5
        with:
          context: .
          push: false
          tags: tamma:test
      - run: docker run tamma:test npm test

  security:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: aquasecurity/trivy-action@master
        with:
          scan-type: 'fs'
          scan-ref: '.'
      - run: pnpm audit
```

### Test Execution Guidance

**Test Execution Order:**
1. Unit tests (fast, isolated)
2. Integration tests (moderate speed, requires dependencies)
3. E2E tests (slow, requires full environment)
4. Performance tests (slow, requires production-like environment)
5. Security tests (moderate speed, automated scans)

**Test Environment Setup:**
```bash
# Local development
pnpm install
pnpm test

# Docker testing
docker-compose up -d
pnpm test:integration
docker-compose down

# E2E testing
./scripts/setup-e2e-env.sh
pnpm test:e2e
./scripts/teardown-e2e-env.sh
```

**Minimum Test Coverage Targets:**
- Unit tests: 80% code coverage
- Integration tests: 60% code coverage
- E2E tests: Critical user journeys covered
- Security tests: Zero critical vulnerabilities

---

**Change Log:**

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2025-10-29 | 1.0.0 | Initial Epic 1.5 technical specification | meywd |
