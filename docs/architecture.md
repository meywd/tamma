# Tamma - Technical Architecture

**Version:** 1.0.0
**Date:** 2025-10-28
**Project Type:** Software (Level 3)
**Field Type:** Greenfield

---

## Executive Summary

Tamma  is an autonomous development orchestration platform that manages complete development workflows from issue assignment to production deployment. The architecture is built on four core pillars:

1. **Dynamic Extensibility** - Plugin-based architecture allows users to customize workflows without code changes
2. **Event-Driven Traceability** - DCB (Dynamic Consistency Boundary) event sourcing provides 100% audit trail
3. **Multi-Provider Abstraction** - Supports multiple AI providers (Claude Code, OpenCode, GLM, local LLMs) and Git platforms (GitHub, GitLab, Gitea, Forgejo)
4. **AI Agent Consistency** - Strict implementation patterns prevent conflicts when multiple AI agents work on the same codebase

### Key Capabilities

- **Autonomous Loop**: 14-step development workflow with 3-retry quality gates
- **Real-time Observability**: <500ms dashboard refresh via Server-Sent Events
- **Time-Travel Debugging**: Event sourcing enables replay of any workflow state
- **Plugin Ecosystem**: npm-distributed plugins with capability-based sandboxing
- **Conditional Automation**: Trigger workflows based on issue labels, file patterns, PR size, etc.

### Non-Functional Achievements

- **Performance**: <2hr autonomous loop completion, <500ms dashboard refresh
- **Reliability**: 70%+ autonomous completion rate, 99.5% uptime
- **Security**: 100% traceability, encryption at rest/transit, mandatory approval for breaking changes

---

## Technology Stack

### Foundation

| Category | Technology | Version | Rationale |
|----------|-----------|---------|-----------|
| **Language** | TypeScript | 5.7+ | Type safety, IDE support, AI agent consistency |
| **Runtime** | Node.js | 22 LTS | Production stability, crypto performance (10x faster than Bun for security scanning) |
| **CLI Framework** | Ink (React for CLIs) | 5.1+ | Component-based UI, aligns with Claude Code stack |
| **Package Manager** | pnpm | 9.15+ | Fastest, 70-80% disk savings, monorepo-optimized |

### Backend

| Category | Technology | Version | Rationale |
|----------|-----------|---------|-----------|
| **API Framework** | Fastify | 5.2+ | Fastest Node.js framework, native TypeScript, schema validation |
| **Database** | PostgreSQL | 17+ | ACID compliance, JSONB for DCB tags, LISTEN/NOTIFY for real-time |
| **ORM** | Drizzle | 0.38+ | Type-safe, zero-runtime overhead, PostgreSQL-native |
| **Event Store** | Emmett | 0.23+ | PostgreSQL-based event sourcing, DCB pattern support |
| **Job Queue** | BullMQ | 5.31+ | Redis-based, TypeScript support, job priorities |
| **Real-time** | Server-Sent Events | Native | Simpler than WebSocket, unidirectional, HTTP/2 multiplexing |

### Testing & Build

| Category | Technology | Version | Rationale |
|----------|-----------|---------|-----------|
| **Testing** | Vitest | 3.2+ | 10-20x faster than Jest, native TypeScript, ESM support |
| **Build Tool** | esbuild + tsc | Latest | esbuild for bundling (100x faster), tsc for type checking |
| **Schema Validation** | Zod | 3.24+ | Zero dependencies, TypeScript-first, composable schemas |

### Frontend (Dashboard)

| Category | Technology | Version | Rationale |
|----------|-----------|---------|-----------|
| **UI Framework** | React | 19+ | Component reusability, large ecosystem |
| **CSS Framework** | Tailwind CSS | 4+ | Utility-first, Rust engine (no PostCSS), tree-shaking |
| **State Management** | React Context | Native | Sufficient for CLI/dashboard state, no external deps |

### Infrastructure

| Category | Technology | Version | Rationale |
|----------|-----------|---------|-----------|
| **Logging** | Pino | 9.6+ | 5x faster than Winston, structured JSON, zero-copy |
| **Git Operations** | simple-git | 3.27+ | Promise-based, comprehensive Git operations |
| **Date/Time** | dayjs | 1.11+ | Smallest bundle (6kb), moment-compatible, immutable |

---

## Project Structure

```
claude-code-manager/
├── packages/
│   ├── cli/                           # Ink-based CLI interface
│   │   ├── src/
│   │   │   ├── index.ts              # CLI entry point
│   │   │   ├── commands/             # CLI commands
│   │   │   ├── components/           # Ink React components
│   │   │   └── state/                # React Context state
│   │   └── package.json
│   │
│   ├── orchestrator/                  # 14-step autonomous loop
│   │   ├── src/
│   │   │   ├── index.ts
│   │   │   ├── loop-engine.ts        # Main orchestration logic
│   │   │   ├── workflow-config.ts    # Dynamic workflow definitions
│   │   │   ├── trigger-evaluator.ts  # Conditional trigger system
│   │   │   └── state-machine.ts      # FSM for loop steps
│   │   └── package.json
│   │
│   ├── workers/                       # Background job workers
│   │   ├── src/
│   │   │   ├── index.ts
│   │   │   ├── code-worker.ts        # Code generation worker
│   │   │   ├── review-worker.ts      # Code review worker
│   │   │   └── scan-worker.ts        # Security scan worker
│   │   └── package.json
│   │
│   ├── gates/                         # Quality gates (3-retry)
│   │   ├── src/
│   │   │   ├── index.ts
│   │   │   ├── gate-base.ts          # Base gate implementation
│   │   │   ├── build-gate.ts         # Build validation
│   │   │   ├── test-gate.ts          # Test validation
│   │   │   └── security-gate.ts      # Security scan (FR-33)
│   │   └── package.json
│   │
│   ├── intelligence/                  # Research & ambiguity detection
│   │   ├── src/
│   │   │   ├── index.ts
│   │   │   ├── ambiguity-detector.ts
│   │   │   └── research-agent.ts
│   │   └── package.json
│   │
│   ├── events/                        # Event sourcing (DCB pattern)
│   │   ├── src/
│   │   │   ├── index.ts
│   │   │   ├── event-store.ts        # DCB event store
│   │   │   ├── projections.ts        # Read models
│   │   │   └── schemas.ts            # Event schemas
│   │   └── package.json
│   │
│   ├── providers/                     # AI provider abstraction
│   │   ├── src/
│   │   │   ├── index.ts
│   │   │   ├── base-provider.ts
│   │   │   ├── claude-code.ts
│   │   │   ├── opencode.ts
│   │   │   ├── glm.ts
│   │   │   └── local-llm.ts
│   │   └── package.json
│   │
│   ├── platforms/                     # Git platform abstraction
│   │   ├── src/
│   │   │   ├── index.ts
│   │   │   ├── base-platform.ts
│   │   │   ├── github.ts
│   │   │   ├── gitlab.ts
│   │   │   ├── gitea.ts
│   │   │   └── forgejo.ts
│   │   └── package.json
│   │
│   ├── api/                           # Fastify API + SSE
│   │   ├── src/
│   │   │   ├── index.ts
│   │   │   ├── routes/
│   │   │   │   ├── issues.ts
│   │   │   │   ├── events.ts
│   │   │   │   └── plugins.ts
│   │   │   └── sse/                  # Server-Sent Events
│   │   └── package.json
│   │
│   ├── dashboard/                     # React observability dashboard
│   │   ├── src/
│   │   │   ├── index.tsx
│   │   │   ├── components/
│   │   │   ├── hooks/
│   │   │   └── utils/
│   │   └── package.json
│   │
│   ├── observability/                 # Logging & metrics
│   │   ├── src/
│   │   │   ├── index.ts
│   │   │   ├── logger.ts             # Pino configuration
│   │   │   └── metrics.ts
│   │   └── package.json
│   │
│   └── shared/                        # Shared utilities
│       ├── src/
│       │   ├── index.ts
│       │   ├── contracts/            # Interface definitions
│       │   ├── types/                # Shared types
│       │   ├── utils/                # Utility functions
│       │   └── errors/               # Error classes
│       └── package.json
│
├── database/
│   ├── migrations/                    # Drizzle migrations
│   └── schema/                        # Drizzle schemas
│
├── plugins/                           # Official plugins
│   ├── debug-workflow/
│   ├── security-scan/
│   └── performance-profiler/
│
├── docs/
│   ├── architecture.md               # This document
│   ├── PRD.md
│   ├── epics.md
│   └── plugin-sdk.md
│
├── pnpm-workspace.yaml
├── tsconfig.base.json
└── package.json
```

---

## Epic to Architecture Mapping

### Epic 1: Foundation & Core Infrastructure

| Story | Component | Technology |
|-------|-----------|-----------|
| Initialize repository | Root | pnpm workspaces |
| Set up PostgreSQL | @Tamma/events | PostgreSQL 17, Drizzle ORM |
| Install dependencies | All packages | pnpm |
| Configure TypeScript | Root | tsconfig.base.json (strict mode) |
| Set up Git workflows | @Tamma/platforms | simple-git |
| Configure CI/CD | Root | GitHub Actions (future) |
| Set up error handling | @Tamma/shared | TammaError base class |
| Configure logging | @Tamma/observability | Pino |
| Set up testing framework | All packages | Vitest |

### Epic 2: Autonomous Development Loop - Core

| Story | Component | Technology |
|-------|-----------|-----------|
| Orchestrator service | @Tamma/orchestrator | TypeScript, Node.js |
| AI provider abstraction | @Tamma/providers | Interface-based design |
| Git platform abstraction | @Tamma/platforms | Interface-based design |
| Integrate Claude Code | @Tamma/providers | Claude Code API |
| Integrate OpenCode | @Tamma/providers | OpenCode API |
| State machine | @Tamma/orchestrator | FSM pattern |
| Context management | @Tamma/orchestrator | Event-driven context |
| Dynamic workflows | @Tamma/orchestrator | WorkflowConfig + plugins |
| Plugin system | @Tamma/orchestrator | PluginManager + sandboxing |
| Conditional triggers | @Tamma/orchestrator | TriggerEvaluator |
| Worker processes | @Tamma/workers | BullMQ job queue |

### Epic 3: Quality Gates & Intelligence Layer

| Story | Component | Technology |
|-------|-----------|-----------|
| Base gate implementation | @Tamma/gates | Chain of Responsibility pattern |
| Build validation gate | @Tamma/gates | Build tool integration |
| Test validation gate | @Tamma/gates | Vitest integration |
| Code quality gate | @Tamma/gates | ESLint, Prettier |
| 3-retry mechanism | @Tamma/gates | Retry Decorator pattern |
| Ambiguity detection | @Tamma/intelligence | NLP analysis |
| Research agent | @Tamma/intelligence | AI-powered research |
| Gate escalation | @Tamma/gates | Manual approval system |
| Approval UI | @Tamma/cli | Ink components |

### Epic 4: Event Sourcing & Audit Trail

| Story | Component | Technology |
|-------|-----------|-----------|
| Event store setup | @Tamma/events | PostgreSQL + Emmett |
| DCB pattern implementation | @Tamma/events | Single stream with JSONB tags |
| Event schemas | @Tamma/events | Zod schemas |
| Event indexing | @Tamma/events | GIN indexes on tags |
| Projections | @Tamma/events | Read models from events |
| Time-travel debugging | @Tamma/events | Event replay |
| Event querying | @Tamma/events | Tag-based queries |
| Audit trail UI | @Tamma/dashboard | React components |

### Epic 5: Observability & Production Readiness

| Story | Component | Technology |
|-------|-----------|-----------|
| Pino logging setup | @Tamma/observability | Pino |
| Structured logging | @Tamma/observability | JSON logs |
| Real-time metrics | @Tamma/api | SSE streaming |
| Dashboard UI | @Tamma/dashboard | React + Tailwind CSS v4 |
| Loop monitoring | @Tamma/dashboard | Real-time charts |
| Error tracking | @Tamma/observability | Error aggregation |
| Performance monitoring | @Tamma/observability | Duration tracking |
| Health checks | @Tamma/api | /health endpoint |
| Graceful shutdown | All services | SIGTERM/SIGINT handlers |
| Production deployment | Infrastructure | Docker (future) |

---

## Novel Architectural Patterns

### 1. DCB (Dynamic Consistency Boundary) Event Sourcing

Traditional event sourcing uses separate streams per aggregate (one stream per Issue, one stream per PR). Tamma adopts the DCB pattern: **single event stream with JSONB tags** for multi-perspective reads.

#### Benefits

- **Simpler Cross-Aggregate Queries**: Query events across multiple issues/PRs without complex joins
- **Flexible Tagging**: Add arbitrary tags without schema migrations
- **Better Audit Trail**: Single source of truth for all events (FR-20-24)
- **Plugin Extensibility**: Plugins can add custom tags without core changes

#### Implementation

```typescript
// Event Structure
interface DomainEvent {
  id: string;                    // UUID v7 (time-sortable)
  type: string;                  // "CODE.GENERATED.SUCCESS"
  timestamp: string;             // ISO 8601 millisecond precision

  // DCB: Tags for multi-perspective reads
  tags: {
    // Entity IDs (all that this event affects)
    issueId?: string;
    prId?: string;
    buildId?: string;
    userId?: string;

    // Contextual tags
    mode?: 'dev' | 'business';
    provider?: string;
    loopIteration?: string;
    workflowName?: string;

    // Plugin tags
    pluginName?: string;
    triggerName?: string;

    // User can add ANYTHING here
    [customKey: string]: string | undefined;
  };

  metadata: {
    workflowVersion: string;
    eventSource: 'system' | 'plugin';
  };

  data: Record<string, unknown>;
}
```

#### PostgreSQL Schema

```sql
CREATE TABLE events (
  id UUID PRIMARY KEY,
  type VARCHAR(255) NOT NULL,
  timestamp TIMESTAMPTZ NOT NULL,
  tags JSONB NOT NULL DEFAULT '{}',
  metadata JSONB NOT NULL,
  data JSONB NOT NULL
);

-- GIN index for fast tag queries
CREATE INDEX idx_events_tags_gin ON events USING GIN(tags);

-- Specific indexes for common queries
CREATE INDEX idx_events_issue_id ON events((tags->>'issueId'));
CREATE INDEX idx_events_pr_id ON events((tags->>'prId'));
CREATE INDEX idx_events_user_id ON events((tags->>'userId'));
CREATE INDEX idx_events_plugin_name ON events((tags->>'pluginName'));
```

#### Query Examples

```typescript
// Get all events for an issue
const events = await db
  .select()
  .from(eventsTable)
  .where(sql`tags->>'issueId' = ${issueId}`)
  .orderBy(eventsTable.timestamp);

// Get all plugin events for a user
const pluginEvents = await db
  .select()
  .from(eventsTable)
  .where(sql`
    tags->>'userId' = ${userId} AND
    metadata->>'eventSource' = 'plugin'
  `);

// Time-travel: Replay events up to timestamp
const pastEvents = await db
  .select()
  .from(eventsTable)
  .where(
    and(
      sql`tags->>'issueId' = ${issueId}`,
      lte(eventsTable.timestamp, replayTimestamp)
    )
  )
  .orderBy(eventsTable.timestamp);
```

### 2. Plugin Architecture with Capability-Based Sandboxing

Tamma's plugin system allows users to extend workflows without code changes, distributed via npm packages.

#### Plugin Types

1. **StepPlugin**: Custom workflow steps (e.g., debug snapshot)
2. **GatePlugin**: Custom quality gates (e.g., security scan)
3. **WorkflowPlugin**: Complete sub-workflows (e.g., debug investigation)
4. **ProviderPlugin**: AI providers (e.g., local LLM)
5. **HookPlugin**: Lifecycle hooks (e.g., pre-commit)
6. **DashboardPlugin**: Dashboard widgets (e.g., custom charts)
7. **BundlePlugin**: Bundle of multiple plugins

#### Plugin Manifest

```typescript
interface PluginManifest {
  name: string;                    // e.g., "@Tamma/debug-workflow"
  version: string;                 // semver
  type: PluginType;

  provides: {
    steps?: StepPlugin[];
    gates?: GatePlugin[];
    workflows?: WorkflowPlugin[];
    providers?: ProviderPlugin[];
    hooks?: HookPlugin[];
    dashboardWidgets?: DashboardPlugin[];
  };

  requires: {
    TammaVersion: string;            // Compatible Tamma versions
    capabilities: Capability[];    // Required permissions
    dependencies?: string[];       // Other plugins
  };

  customTags?: string[];          // DCB tags this plugin uses
}

type Capability =
  | 'network'         // HTTP/HTTPS requests
  | 'filesystem'      // Read/write files
  | 'exec'            // Execute shell commands
  | 'events:read'     // Read events from store
  | 'events:write'    // Write events to store
  | 'git'             // Git operations
  | 'ai'              // AI provider access
  | 'secrets';        // Access secrets/credentials
```

#### Plugin SDK

```typescript
// Plugin SDK provides typed context
interface StepContext {
  issueId: string;
  userId: string;
  workflowName: string;
  mode: 'dev' | 'business';

  // Sandboxed API
  fs: SandboxedFS;           // If 'filesystem' capability
  http: SandboxedHTTP;       // If 'network' capability
  exec: SandboxedExec;       // If 'exec' capability
  events: EventStore;        // If 'events:write' capability
  git: GitPlatform;          // If 'git' capability
  ai: AIProvider;            // If 'ai' capability

  // Data from previous steps
  previousStepResults: Map<string, unknown>;

  // Plugin configuration
  config: Record<string, unknown>;
}

interface StepPlugin {
  id: string;
  name: string;
  version: string;

  execute(context: StepContext): Promise<StepResult>;
  validate?(context: StepContext): Promise<ValidationResult>;
  rollback?(context: StepContext): Promise<void>;
}
```

#### Plugin Manager

```typescript
class PluginManager {
  async installPlugin(name: string): Promise<void> {
    // 1. Download from npm registry
    const manifest = await this.downloadPlugin(name);

    // 2. Validate manifest
    const validation = await this.validateManifest(manifest);
    if (!validation.valid) {
      throw new TammaError('INVALID_PLUGIN', validation.errors);
    }

    // 3. Check capability permissions (user approval)
    await this.requestCapabilities(manifest.requires.capabilities);

    // 4. Install dependencies
    await this.installDependencies(manifest.requires.dependencies);

    // 5. Register plugin
    await this.registry.register(manifest);

    // 6. Emit installation event
    await this.events.append({
      type: 'PLUGIN.INSTALLED',
      tags: { pluginName: name },
      data: { capabilities: manifest.requires.capabilities }
    });
  }

  async executePlugin(
    pluginName: string,
    context: StepContext
  ): Promise<StepResult> {
    // 1. Get plugin
    const plugin = await this.registry.get(pluginName);

    // 2. Create sandboxed context with only granted capabilities
    const sandboxedContext = this.createSandbox(plugin, context);

    // 3. Execute with timeout
    const result = await this.executeWithTimeout(
      () => plugin.execute(sandboxedContext),
      30000 // 30s timeout
    );

    // 4. Emit execution event
    await this.events.append({
      type: 'PLUGIN.EXECUTED',
      tags: {
        pluginName,
        issueId: context.issueId,
        success: result.success.toString()
      },
      data: result
    });

    return result;
  }
}
```

#### Plugin Distribution

```
npm registry structure:

@Tamma/debug-workflow          # Official plugin
@Tamma/security-scan           # Official plugin
@community/custom-gate       # Community plugin
@enterprise/compliance-check # Enterprise plugin
```

### 3. Conditional Trigger System

Plugins activate based on issue metadata, file patterns, PR characteristics, and custom conditions.

#### Trigger Configuration

```yaml
# User config: ~/.Tamma/config.yaml
workflows:
  main:
    triggers:
      - name: 'Debug workflow for bugs'
        plugin: '@Tamma/debug-workflow'
        workflow: 'debug-investigation'

        when:
          issueLabels:
            includes: ['bug', 'critical-bug']
          condition: |
            issue.priority === 'high' ||
            issue.severity === 'critical'

        position:
          after: 'CODE_GENERATION'
          before: 'PR_CREATION'

        required: true         # Block loop if trigger fails
        runOnce: true          # Only run once per loop

      - name: 'Security scan for auth changes'
        plugin: '@Tamma/security-scan'
        step: 'deep-security-scan'

        when:
          filePatterns:
            includes: ['auth/**', 'security/**', '**/*.auth.ts']
          breakingChange: true

        position:
          after: 'BUILD_VALIDATION'

        required: true

      - name: 'Performance profiling for optimization'
        plugin: '@Tamma/performance-profiler'
        workflow: 'profile-and-optimize'

        when:
          issueLabels:
            includes: ['performance', 'optimization']
          prSize:
            linesChanged:
              gt: 200

        position:
          after: 'TEST_VALIDATION'

        required: false

      - name: 'Notify on large PRs'
        plugin: '@Tamma/slack-notify'
        step: 'send-notification'

        when:
          prSize:
            filesChanged:
              gt: 10
            linesChanged:
              gt: 500

        position:
          after: 'PR_CREATION'

        required: false
```

#### Trigger Condition Schema

```typescript
interface TriggerCondition {
  // Issue metadata
  issueLabels?: {
    includes?: string[];         // Has any of these labels
    excludes?: string[];         // Doesn't have any of these
    match?: string;              // Regex pattern
  };

  issueType?: 'bug' | 'feature' | 'task' | 'hotfix';
  issuePriority?: {
    in?: string[];               // Priority in list
    gte?: string;                // Priority >= threshold
  };

  // Code metadata
  filePatterns?: {
    includes?: string[];         // Glob patterns
    excludes?: string[];
  };

  prSize?: {
    linesChanged?: {
      gt?: number;
      lt?: number;
    };
    filesChanged?: {
      gt?: number;
    };
  };

  breakingChange?: boolean;      // Has breaking changes
  testsPassed?: boolean;         // Tests pass/fail
  testCoverage?: {
    lt?: number;                 // Coverage < threshold
    gte?: number;                // Coverage >= threshold
  };

  // Complex expressions (sandboxed JavaScript)
  condition?: string;

  // Logical operators
  and?: TriggerCondition[];
  or?: TriggerCondition[];
  not?: TriggerCondition;
}
```

#### Trigger Evaluator

```typescript
class TriggerEvaluator {
  async evaluateCondition(
    condition: TriggerCondition,
    context: LoopContext
  ): Promise<boolean> {
    // Issue labels
    if (condition.issueLabels?.includes) {
      const labels = await this.getIssueLabels(context.issueId);
      const hasLabel = condition.issueLabels.includes.some(label =>
        labels.includes(label)
      );
      if (!hasLabel) return false;
    }

    if (condition.issueLabels?.excludes) {
      const labels = await this.getIssueLabels(context.issueId);
      const hasExcluded = condition.issueLabels.excludes.some(label =>
        labels.includes(label)
      );
      if (hasExcluded) return false;
    }

    // File patterns
    if (condition.filePatterns?.includes) {
      const changedFiles = await this.getChangedFiles(context);
      const matches = changedFiles.some(file =>
        this.matchesGlob(file, condition.filePatterns.includes)
      );
      if (!matches) return false;
    }

    // PR size
    if (condition.prSize?.linesChanged?.gt) {
      const pr = await this.getPR(context.prId);
      if (pr.linesChanged <= condition.prSize.linesChanged.gt) {
        return false;
      }
    }

    // Breaking changes
    if (condition.breakingChange !== undefined) {
      const hasBreaking = await this.detectBreakingChanges(context);
      if (hasBreaking !== condition.breakingChange) {
        return false;
      }
    }

    // Complex condition (sandboxed eval)
    if (condition.condition) {
      const result = await this.evalInSandbox(
        condition.condition,
        context
      );
      if (!result) return false;
    }

    // Logical operators
    if (condition.and) {
      for (const subCondition of condition.and) {
        if (!await this.evaluateCondition(subCondition, context)) {
          return false;
        }
      }
    }

    if (condition.or) {
      let matched = false;
      for (const subCondition of condition.or) {
        if (await this.evaluateCondition(subCondition, context)) {
          matched = true;
          break;
        }
      }
      if (!matched) return false;
    }

    if (condition.not) {
      const result = await this.evaluateCondition(condition.not, context);
      if (result) return false;
    }

    return true;
  }

  async evaluateTriggers(
    step: WorkflowStep,
    context: LoopContext
  ): Promise<TriggerExecution[]> {
    const triggers = this.config.workflows[context.workflowName].triggers;
    const executions: TriggerExecution[] = [];

    for (const trigger of triggers) {
      // Check position
      const shouldRun = this.shouldRunAtPosition(trigger, step);
      if (!shouldRun) continue;

      // Check runOnce
      if (trigger.runOnce && await this.hasRunBefore(trigger, context)) {
        continue;
      }

      // Evaluate condition
      const matched = await this.evaluateCondition(trigger.when, context);

      if (matched) {
        // Emit trigger activation event
        await this.events.append({
          type: 'TRIGGER.ACTIVATED',
          tags: {
            issueId: context.issueId,
            triggerName: trigger.name,
            pluginName: trigger.plugin,
            workflowName: context.workflowName
          },
          data: {
            condition: trigger.when,
            position: trigger.position
          }
        });

        executions.push({
          trigger,
          plugin: trigger.plugin,
          workflow: trigger.workflow || undefined,
          step: trigger.step || undefined,
          required: trigger.required
        });
      }
    }

    return executions;
  }
}
```

### 4. Dynamic User Workflows

Users customize workflows via configuration while maintaining AI agent consistency.

#### Base 14-Step Workflow (Static)

```typescript
const BASE_WORKFLOW: WorkflowStep[] = [
  { id: 'ISSUE_ASSIGNMENT', name: 'Assign issue' },
  { id: 'CONTEXT_GATHERING', name: 'Gather context' },
  { id: 'RESEARCH', name: 'Research approach' },
  { id: 'AMBIGUITY_CHECK', name: 'Check for ambiguities' },
  { id: 'CODE_GENERATION', name: 'Generate code' },
  { id: 'BUILD_VALIDATION', name: 'Validate build' },
  { id: 'TEST_VALIDATION', name: 'Validate tests' },
  { id: 'SECURITY_SCAN', name: 'Security scan' },
  { id: 'CODE_REVIEW', name: 'Code review' },
  { id: 'PR_CREATION', name: 'Create PR' },
  { id: 'CI_CHECK', name: 'CI checks' },
  { id: 'APPROVAL_GATE', name: 'Approval gate' },
  { id: 'MERGE', name: 'Merge PR' },
  { id: 'DEPLOYMENT', name: 'Deploy' }
];
```

#### User Customizations

```yaml
# ~/.Tamma/config.yaml
workflows:
  main:
    base: 'standard-14-step'

    # Override step behavior
    overrides:
      CODE_GENERATION:
        provider: 'claude-code'
        model: 'opus-4'
        temperature: 0.7

      APPROVAL_GATE:
        mode: 'business'  # Require business stakeholder approval

    # Insert custom steps
    customSteps:
      - id: 'PERFORMANCE_BENCHMARK'
        name: 'Run performance benchmarks'
        position:
          after: 'TEST_VALIDATION'
        plugin: '@Tamma/performance-profiler'
        step: 'benchmark'

    # Conditional branching
    branches:
      hotfix:
        condition:
          issueLabels:
            includes: ['hotfix']
        skipSteps: ['RESEARCH', 'AMBIGUITY_CHECK']
        overrides:
          APPROVAL_GATE:
            timeout: 3600  # 1hr instead of 24hr
```

#### Workflow Engine

```typescript
class WorkflowEngine {
  async executeWorkflow(
    workflowName: string,
    context: LoopContext
  ): Promise<WorkflowResult> {
    const config = this.config.workflows[workflowName];

    // 1. Start with base workflow
    const steps = [...BASE_WORKFLOW];

    // 2. Apply custom steps
    if (config.customSteps) {
      for (const customStep of config.customSteps) {
        const index = this.findStepIndex(steps, customStep.position);
        steps.splice(index, 0, customStep);
      }
    }

    // 3. Apply branch overrides if condition matches
    if (config.branches) {
      for (const [branchName, branch] of Object.entries(config.branches)) {
        if (await this.evaluateCondition(branch.condition, context)) {
          // Skip steps
          if (branch.skipSteps) {
            steps = steps.filter(s => !branch.skipSteps.includes(s.id));
          }

          // Apply overrides
          if (branch.overrides) {
            Object.assign(config.overrides, branch.overrides);
          }

          break; // Only one branch per workflow
        }
      }
    }

    // 4. Execute steps
    for (const step of steps) {
      // Emit step start event
      await this.events.append({
        type: 'WORKFLOW.STEP_STARTED',
        tags: {
          issueId: context.issueId,
          workflowName,
          stepId: step.id
        },
        data: { stepName: step.name }
      });

      // Check for triggers before step
      const beforeTriggers = await this.triggerEvaluator.evaluateTriggers(
        { ...step, position: 'before' },
        context
      );

      for (const trigger of beforeTriggers) {
        await this.executeTrigger(trigger, context);
      }

      // Execute step
      const result = await this.executeStep(step, config.overrides, context);

      // Check for triggers after step
      const afterTriggers = await this.triggerEvaluator.evaluateTriggers(
        { ...step, position: 'after' },
        context
      );

      for (const trigger of afterTriggers) {
        await this.executeTrigger(trigger, context);
      }

      // Emit step completion event
      await this.events.append({
        type: 'WORKFLOW.STEP_COMPLETED',
        tags: {
          issueId: context.issueId,
          workflowName,
          stepId: step.id,
          success: result.success.toString()
        },
        data: result
      });

      // Handle failure
      if (!result.success) {
        return { success: false, failedStep: step.id, error: result.error };
      }
    }

    return { success: true };
  }
}
```

---

## Implementation Patterns (AI Agent Consistency)

### 1. Naming Conventions

#### Files & Directories

- **Files**: kebab-case: `event-store.ts`, `plugin-manager.ts`
- **Test files**: `*.test.ts` (colocated)
- **Type definitions**: `*.types.ts`
- **Interfaces**: `I` prefix: `IPluginManifest`, `IEventStore`
- **Classes**: PascalCase: `PluginManager`, `EventStore`
- **Constants**: SCREAMING_SNAKE_CASE: `MAX_RETRY_ATTEMPTS`, `DEFAULT_TIMEOUT_MS`

#### Functions

- **General**: camelCase: `evaluateCondition()`, `appendEvent()`
- **Boolean**: `is/has/should` prefix: `isRetryable()`, `hasCapability()`
- **Private**: `_` prefix: `_validateSchema()`, `_executeInSandbox()`

#### API Endpoints

```
GET    /api/v1/issues/:issueId
POST   /api/v1/issues
PATCH  /api/v1/issues/:issueId
DELETE /api/v1/issues/:issueId
GET    /api/v1/issues/:issueId/events
POST   /api/v1/plugins/:pluginName/install
SSE    /api/v1/events/stream
```

#### Database Tables

- **Tables**: snake_case: `events`, `plugin_installs`, `workflow_runs`
- **Junction**: `entity1_entity2`: `issue_labels`, `plugin_capabilities`
- **Columns**: snake_case: `created_at`, `issue_id`, `plugin_name`

#### Event Types

```typescript
// Pattern: AGGREGATE.ACTION.STATUS
"ISSUE.ASSIGNED.SUCCESS"
"CODE.GENERATED.SUCCESS"
"CODE.GENERATED.FAILED"
"PLUGIN.DEBUG_SNAPSHOT.SUCCESS"
"TRIGGER.ACTIVATED"
"WORKFLOW.STEP_COMPLETED"
"GATE.REVIEW_REQUESTED"
```

### 2. Structure Patterns

#### Package Structure

```
packages/[package-name]/
├── src/
│   ├── index.ts              # Public API exports
│   ├── [feature]/
│   │   ├── [feature].ts
│   │   ├── [feature].test.ts
│   │   └── [feature].types.ts
│   └── lib/                   # Internal utilities
├── tsconfig.json
└── package.json
```

#### Test Organization

```typescript
describe('PluginManager', () => {
  describe('installPlugin', () => {
    it('should install plugin with valid manifest', async () => {
      // Arrange
      const manifest = createMockManifest();

      // Act
      const result = await pluginManager.installPlugin(manifest);

      // Assert
      expect(result.success).toBe(true);
    });
  });
});
```

### 3. Format Patterns

#### API Responses

```typescript
// Success
{
  "success": true,
  "data": { "issueId": "uuid-v7", "status": "CODE_GENERATION" },
  "timestamp": "2025-10-28T12:34:56.789Z",
  "requestId": "uuid-v7"
}

// Error
{
  "success": false,
  "error": {
    "code": "GIT_PUSH_FAILED",
    "message": "Failed to push to remote branch",
    "details": { "branch": "feature/123" },
    "retryable": true,
    "severity": "high"
  },
  "timestamp": "2025-10-28T12:34:56.789Z",
  "requestId": "uuid-v7"
}
```

#### Date/Time

```typescript
// ALWAYS ISO 8601 with millisecond precision
"2025-10-28T12:34:56.789Z"

// Use dayjs for all operations
import dayjs from 'dayjs';
import utc from 'dayjs/plugin/utc';
dayjs.extend(utc);

const now = dayjs.utc().toISOString();
```

#### Error Handling

```typescript
class TammaError extends Error {
  constructor(
    public code: string,
    message: string,
    public context: Record<string, unknown> = {},
    public retryable: boolean = false,
    public severity: 'low' | 'medium' | 'high' | 'critical' = 'medium'
  ) {
    super(message);
    this.name = 'TammaError';
  }
}
```

#### Logging (Pino)

```typescript
{
  "level": 30,
  "time": 1698483296789,
  "service": "orchestrator",
  "issueId": "uuid-v7",
  "eventType": "CODE.GENERATED.SUCCESS",
  "msg": "Code generated successfully"
}
```

### 4. Communication Patterns

#### Event Publishing

```typescript
await eventStore.append({
  type: 'CODE.GENERATED.SUCCESS',
  tags: {
    issueId: context.issueId,
    userId: context.userId,
    provider: 'claude-code',
    mode: context.mode
  },
  metadata: {
    workflowVersion: '1.0.0',
    eventSource: 'system'
  },
  data: {
    filesChanged: ['src/foo.ts'],
    duration: 1234
  }
});
```

#### State Updates

```typescript
// NEVER mutate
// ❌ BAD
context.step = 'CODE_GENERATION';

// ✅ GOOD
const updatedContext = {
  ...context,
  step: 'CODE_GENERATION',
  updatedAt: dayjs.utc().toISOString()
};
```

#### Inter-Package Communication

```typescript
// Define contracts in @Tamma/shared
export interface IEventStore {
  append(event: DomainEvent): Promise<void>;
  query(query: EventQuery): Promise<DomainEvent[]>;
}

// Use dependency injection
class LoopOrchestrator {
  constructor(private eventStore: IEventStore) {}
}
```

### 5. Lifecycle Patterns

#### Loading States

```typescript
type LoadingState<T> =
  | { status: 'idle' }
  | { status: 'loading' }
  | { status: 'success'; data: T }
  | { status: 'error'; error: TammaError };
```

#### Retry with Backoff

```typescript
async function retryWithBackoff<T>(
  fn: () => Promise<T>,
  options: {
    maxAttempts: number;
    baseDelay: number;
    maxDelay: number;
  }
): Promise<T> {
  let attempt = 0;

  while (true) {
    try {
      return await fn();
    } catch (error) {
      attempt++;
      if (attempt >= options.maxAttempts) throw error;

      const delay = Math.min(
        options.baseDelay * Math.pow(2, attempt),
        options.maxDelay
      );

      await sleep(delay);
    }
  }
}
```

#### Graceful Shutdown

```typescript
class WorkerService {
  async shutdown() {
    logger.info('Graceful shutdown initiated');
    await this.queue.pause();
    await this.waitForInFlightJobs(30000);
    await Promise.all([
      this.db.close(),
      this.redis.quit()
    ]);
    logger.info('Graceful shutdown complete');
  }
}

process.on('SIGTERM', () => service.shutdown());
```

### 6. Consistency Rules

#### TypeScript

```json
{
  "compilerOptions": {
    "strict": true,
    "noImplicitAny": true,
    "noImplicitReturns": true,
    "noFallthroughCasesInSwitch": true
  }
}
```

#### Import Order

```typescript
// 1. Node.js built-ins
import { readFile } from 'fs/promises';

// 2. External dependencies
import dayjs from 'dayjs';

// 3. Internal packages
import type { IEventStore } from '@Tamma/shared/contracts';

// 4. Relative imports
import { PluginManager } from '../plugin-manager';
```

#### Async/Await

```typescript
// ALWAYS use async/await, NEVER .then()/.catch()

// ✅ GOOD
async function getUser(id: string): Promise<User> {
  try {
    const rows = await db.query('SELECT * FROM users WHERE id = ?', [id]);
    return rows[0];
  } catch (error) {
    logger.error('Failed to get user', { error, userId: id });
    throw error;
  }
}
```

---

## Data Architecture

### Database Schema

```sql
-- Events table (DCB pattern)
CREATE TABLE events (
  id UUID PRIMARY KEY,
  type VARCHAR(255) NOT NULL,
  timestamp TIMESTAMPTZ NOT NULL,
  tags JSONB NOT NULL DEFAULT '{}',
  metadata JSONB NOT NULL,
  data JSONB NOT NULL
);

CREATE INDEX idx_events_tags_gin ON events USING GIN(tags);
CREATE INDEX idx_events_issue_id ON events((tags->>'issueId'));
CREATE INDEX idx_events_timestamp ON events(timestamp DESC);

-- Plugin installs
CREATE TABLE plugin_installs (
  id UUID PRIMARY KEY,
  plugin_name VARCHAR(255) NOT NULL UNIQUE,
  version VARCHAR(50) NOT NULL,
  capabilities TEXT[] NOT NULL,
  installed_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  active BOOLEAN NOT NULL DEFAULT true
);

-- Workflow runs
CREATE TABLE workflow_runs (
  id UUID PRIMARY KEY,
  issue_id UUID NOT NULL,
  workflow_name VARCHAR(255) NOT NULL,
  started_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  completed_at TIMESTAMPTZ,
  status VARCHAR(50) NOT NULL, -- running, success, failed
  current_step VARCHAR(255),
  error_message TEXT
);

CREATE INDEX idx_workflow_runs_issue ON workflow_runs(issue_id);
CREATE INDEX idx_workflow_runs_status ON workflow_runs(status);

-- User configurations
CREATE TABLE user_configs (
  user_id UUID PRIMARY KEY,
  workflow_config JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

### Projections (Read Models)

```typescript
// Issue aggregate
interface IssueProjection {
  issueId: string;
  currentStep: string;
  status: 'pending' | 'running' | 'completed' | 'failed';
  assignedTo: string;
  createdAt: string;
  updatedAt: string;

  // Denormalized data
  totalEvents: number;
  codeGenerationAttempts: number;
  gateFailures: number;
  pluginsExecuted: string[];
}

// Build projection from events
async function buildIssueProjection(issueId: string): Promise<IssueProjection> {
  const events = await eventStore.query({
    tags: { issueId },
    orderBy: 'timestamp'
  });

  const projection: IssueProjection = {
    issueId,
    currentStep: 'ISSUE_ASSIGNMENT',
    status: 'pending',
    assignedTo: '',
    createdAt: '',
    updatedAt: '',
    totalEvents: 0,
    codeGenerationAttempts: 0,
    gateFailures: 0,
    pluginsExecuted: []
  };

  for (const event of events) {
    projection.totalEvents++;
    projection.updatedAt = event.timestamp;

    switch (event.type) {
      case 'ISSUE.ASSIGNED.SUCCESS':
        projection.assignedTo = event.data.userId;
        projection.createdAt = event.timestamp;
        break;

      case 'WORKFLOW.STEP_STARTED':
        projection.currentStep = event.data.stepId;
        projection.status = 'running';
        break;

      case 'CODE.GENERATED.SUCCESS':
        projection.codeGenerationAttempts++;
        break;

      case 'GATE.FAILED':
        projection.gateFailures++;
        break;

      case 'PLUGIN.EXECUTED':
        if (!projection.pluginsExecuted.includes(event.tags.pluginName)) {
          projection.pluginsExecuted.push(event.tags.pluginName);
        }
        break;

      case 'WORKFLOW.COMPLETED':
        projection.status = 'completed';
        break;

      case 'WORKFLOW.FAILED':
        projection.status = 'failed';
        break;
    }
  }

  return projection;
}
```

---

## API Contracts

### REST API

```typescript
// Issues
GET    /api/v1/issues/:issueId
POST   /api/v1/issues
PATCH  /api/v1/issues/:issueId
DELETE /api/v1/issues/:issueId

// Events
GET    /api/v1/events?issueId={uuid}&from={iso8601}&to={iso8601}
GET    /api/v1/events/:eventId

// Plugins
GET    /api/v1/plugins
GET    /api/v1/plugins/:pluginName
POST   /api/v1/plugins/:pluginName/install
POST   /api/v1/plugins/:pluginName/uninstall
PATCH  /api/v1/plugins/:pluginName/activate
PATCH  /api/v1/plugins/:pluginName/deactivate

// Workflows
GET    /api/v1/workflows
GET    /api/v1/workflows/:workflowName
GET    /api/v1/workflows/runs?issueId={uuid}
GET    /api/v1/workflows/runs/:runId

// Health
GET    /api/v1/health
```

### Server-Sent Events (SSE)

```typescript
// Real-time event stream
GET /api/v1/events/stream?issueId={uuid}

// Response format
event: CODE.GENERATED.SUCCESS
data: {"issueId":"uuid","filesChanged":["src/foo.ts"]}

event: WORKFLOW.STEP_COMPLETED
data: {"issueId":"uuid","step":"CODE_GENERATION"}
```

### WebSocket (Future)

```typescript
// Bidirectional communication for interactive approvals
WS /api/v1/ws/approvals

// Client → Server
{
  "type": "APPROVAL_RESPONSE",
  "gateId": "uuid",
  "approved": true,
  "feedback": "LGTM"
}

// Server → Client
{
  "type": "APPROVAL_REQUEST",
  "gateId": "uuid",
  "gateName": "Code Review",
  "context": { ... }
}
```

---

## Security Architecture

### Authentication

```typescript
// JWT-based authentication
interface JWTPayload {
  userId: string;
  email: string;
  roles: string[];
  exp: number; // Unix timestamp
}

// API Key-based authentication (for automation)
interface APIKey {
  keyId: string;
  userId: string;
  scopes: string[]; // ['read:issues', 'write:plugins']
  expiresAt?: string;
}
```

### Authorization

```typescript
// Role-based access control
enum Role {
  ADMIN = 'admin',           // Full access
  DEVELOPER = 'developer',   // Manage workflows, install plugins
  BUSINESS = 'business',     // View dashboards, approve gates
  READONLY = 'readonly'      // View only
}

// Scope-based permissions for API keys
const SCOPES = [
  'read:issues',
  'write:issues',
  'read:events',
  'read:plugins',
  'write:plugins',
  'read:workflows',
  'write:workflows'
];
```

### Plugin Sandboxing

```typescript
// Capability-based security
class PluginSandbox {
  private allowedCapabilities: Capability[];

  constructor(capabilities: Capability[]) {
    this.allowedCapabilities = capabilities;
  }

  // Only allow filesystem access if capability granted
  get fs(): SandboxedFS {
    if (!this.allowedCapabilities.includes('filesystem')) {
      throw new TammaError(
        'CAPABILITY_DENIED',
        'Plugin does not have filesystem capability'
      );
    }

    return new SandboxedFS(this.workDir); // Restricted to workDir
  }

  // Only allow network access if capability granted
  get http(): SandboxedHTTP {
    if (!this.allowedCapabilities.includes('network')) {
      throw new TammaError(
        'CAPABILITY_DENIED',
        'Plugin does not have network capability'
      );
    }

    return new SandboxedHTTP(); // No SSRF, rate-limited
  }
}
```

### Encryption

```typescript
// Secrets at rest (PostgreSQL pg_crypto)
CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE TABLE secrets (
  id UUID PRIMARY KEY,
  user_id UUID NOT NULL,
  key VARCHAR(255) NOT NULL,
  value BYTEA NOT NULL, -- Encrypted with pgp_sym_encrypt
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Encrypt
INSERT INTO secrets (id, user_id, key, value)
VALUES (
  gen_random_uuid(),
  'user-uuid',
  'github_token',
  pgp_sym_encrypt('token-value', 'encryption-key')
);

-- Decrypt
SELECT pgp_sym_decrypt(value, 'encryption-key') AS decrypted_value
FROM secrets WHERE key = 'github_token';
```

### Breaking Change Protection (FR-34)

```typescript
async function detectBreakingChanges(
  context: LoopContext
): Promise<boolean> {
  const changedFiles = await gitPlatform.getChangedFiles(context.prId);

  // Check for breaking API changes
  const apiChanges = changedFiles.filter(f =>
    f.path.includes('/api/') && f.status === 'modified'
  );

  for (const file of apiChanges) {
    const diff = await gitPlatform.getDiff(file.path);

    // Detect removed exports, changed signatures, etc.
    if (this.hasBreakingChange(diff)) {
      return true;
    }
  }

  // Check for database migration changes
  const migrationChanges = changedFiles.filter(f =>
    f.path.includes('/migrations/')
  );

  if (migrationChanges.length > 0) {
    return true; // All migrations are potentially breaking
  }

  return false;
}

// NEVER auto-approve breaking changes
async function approvalGate(context: LoopContext): Promise<void> {
  const hasBreaking = await detectBreakingChanges(context);

  if (hasBreaking) {
    // MANDATORY manual approval
    await requestManualApproval(context, {
      reason: 'Breaking change detected',
      timeout: null, // No timeout
      escalate: true
    });
  }
}
```

---

## Deployment Architecture

### Development

```yaml
# docker-compose.dev.yml
services:
  postgres:
    image: postgres:17-alpine
    environment:
      POSTGRES_DB: Tamma_dev
      POSTGRES_USER: Tamma
      POSTGRES_PASSWORD: dev_password
    ports:
      - "5432:5432"

  redis:
    image: redis:7-alpine
    ports:
      - "6379:6379"

  Tamma-api:
    build: .
    command: pnpm dev --filter @Tamma/api
    environment:
      DATABASE_URL: postgresql://Tamma:dev_password@postgres:5432/Tamma_dev
      REDIS_URL: redis://redis:6379
    ports:
      - "3000:3000"
    volumes:
      - ./packages:/app/packages

  Tamma-worker:
    build: .
    command: pnpm dev --filter @Tamma/workers
    environment:
      DATABASE_URL: postgresql://Tamma:dev_password@postgres:5432/Tamma_dev
      REDIS_URL: redis://redis:6379
```

### Production (Future)

```yaml
# Kubernetes deployment
apiVersion: apps/v1
kind: Deployment
metadata:
  name: Tamma-api
spec:
  replicas: 3
  selector:
    matchLabels:
      app: Tamma-api
  template:
    metadata:
      labels:
        app: Tamma-api
    spec:
      containers:
      - name: Tamma-api
        image: Tamma-api:1.0.0
        env:
        - name: DATABASE_URL
          valueFrom:
            secretKeyRef:
              name: Tamma-secrets
              key: database-url
        - name: REDIS_URL
          valueFrom:
            secretKeyRef:
              name: Tamma-secrets
              key: redis-url
        ports:
        - containerPort: 3000
        livenessProbe:
          httpGet:
            path: /api/v1/health
            port: 3000
          initialDelaySeconds: 30
          periodSeconds: 10
        resources:
          requests:
            memory: "512Mi"
            cpu: "500m"
          limits:
            memory: "1Gi"
            cpu: "1000m"
```

### Observability Stack

```yaml
# Monitoring services
services:
  prometheus:
    image: prom/prometheus:latest
    volumes:
      - ./prometheus.yml:/etc/prometheus/prometheus.yml
    ports:
      - "9090:9090"

  grafana:
    image: grafana/grafana:latest
    ports:
      - "3001:3000"
    volumes:
      - ./grafana-dashboards:/etc/grafana/provisioning/dashboards
```

---

## Performance Optimizations

### Database

```typescript
// Use connection pooling
import { Pool } from 'pg';

const pool = new Pool({
  connectionString: process.env.DATABASE_URL,
  max: 20,           // Max connections
  idleTimeoutMillis: 30000,
  connectionTimeoutMillis: 2000
});

// Use prepared statements
const result = await pool.query(
  'SELECT * FROM events WHERE tags->>$1 = $2',
  ['issueId', issueId]
);
```

### Caching

```typescript
// Redis caching for projections
class ProjectionCache {
  async getIssueProjection(issueId: string): Promise<IssueProjection | null> {
    const cached = await redis.get(`projection:issue:${issueId}`);
    if (cached) {
      return JSON.parse(cached);
    }

    const projection = await buildIssueProjection(issueId);
    await redis.setex(
      `projection:issue:${issueId}`,
      300, // 5min TTL
      JSON.stringify(projection)
    );

    return projection;
  }
}
```

### SSE Performance

```typescript
// Use Redis pub/sub for distributed SSE
import { createClient } from 'redis';

const pub = createClient({ url: process.env.REDIS_URL });
const sub = createClient({ url: process.env.REDIS_URL });

// Publisher (from event store)
await pub.publish('events', JSON.stringify(event));

// Subscriber (SSE handler)
await sub.subscribe('events', (message) => {
  const event = JSON.parse(message);

  // Send to all connected SSE clients
  for (const client of sseClients) {
    if (client.filter(event)) {
      client.send(event);
    }
  }
});
```

---

## Migration Path (Future Enhancements)

### Phase 1: Foundation (Current)

- ✅ Monorepo structure
- ✅ Event sourcing with DCB
- ✅ Plugin architecture
- ✅ Conditional triggers
- ✅ Dynamic user workflows

### Phase 2: Scale (Q2 2026)

- Horizontal scaling with Kubernetes
- Distributed tracing (OpenTelemetry)
- Multi-region deployment
- Advanced caching strategies

### Phase 3: Enterprise (Q3 2026)

- SAML/SSO authentication
- Compliance certifications (SOC 2, ISO 27001)
- On-premise deployment options
- Enterprise plugin marketplace

### Phase 4: AI Evolution (Q4 2026)

- Multi-agent collaboration (beyond single loop)
- Self-improving AI (learn from past loops)
- Predictive issue assignment
- Automated workflow optimization

---

## Appendix A: Decision Log

| ID | Decision | Alternatives | Rationale |
|----|----------|--------------|-----------|
| D1 | Node.js runtime | Bun | Production stability, crypto performance (10x faster for security scanning) |
| D2 | PostgreSQL + Emmett | EventStoreDB | Unified storage, JSONB flexibility, lower operational complexity |
| D3 | Fastify API | Express, Hono | Fastest Node.js framework, native TypeScript, schema validation |
| D4 | Server-Sent Events | WebSocket | Simpler, unidirectional, HTTP/2 multiplexing, lower overhead |
| D5 | Vitest testing | Jest | 10-20x faster, native TypeScript, ESM support |
| D6 | simple-git | nodegit | Promise-based, simpler API, better error handling |
| D7 | Drizzle ORM | Prisma, TypeORM | Type-safe, zero-runtime overhead, PostgreSQL-native |
| D8 | React Context | Redux, Zustand | Sufficient for CLI/dashboard, no external deps, simpler |
| D9 | React dashboard | Svelte, Vue | Component reusability, largest ecosystem, team familiarity |
| D10 | Tailwind CSS v4 | CSS-in-JS, Styled | Utility-first, Rust engine (no PostCSS), tree-shaking |
| D11 | Pino logging | Winston, Bunyan | 5x faster, structured JSON, zero-copy |
| D12 | pnpm | npm, yarn | Fastest, 70-80% disk savings, monorepo-optimized |
| D13 | esbuild + tsc | Webpack, Rollup | 100x faster bundling, tsc for type checking |
| D14 | Zod validation | Yup, Joi | Zero dependencies, TypeScript-first, composable |
| D15 | BullMQ queue | Agenda, Bee-Queue | Redis-based, TypeScript support, job priorities |
| D16 | dayjs dates | moment, date-fns | Smallest bundle (6kb), moment-compatible, immutable |
| D17 | DCB pattern | Aggregate-per-stream | Simpler cross-aggregate queries, flexible tagging, better audit trail |
| D18 | Plugin system | Freeform custom steps | Structured contracts, marketplace distribution, security sandboxing |
| D19 | Conditional triggers | Static workflows | Issue metadata matching, file patterns, declarative configuration |

---

## Appendix B: Glossary

| Term | Definition |
|------|------------|
| **DCB** | Dynamic Consistency Boundary - Event sourcing pattern with single stream and tags |
| **SSE** | Server-Sent Events - Unidirectional real-time communication protocol |
| **CQRS** | Command Query Responsibility Segregation - Separate read/write models |
| **GIN Index** | Generalized Inverted Index - PostgreSQL index for JSONB queries |
| **Projection** | Read model built from events |
| **Capability** | Permission granted to plugins (network, filesystem, exec, etc.) |
| **Trigger** | Conditional workflow activation based on metadata |
| **Gate** | Quality validation step with 3-retry mechanism |
| **Loop** | 14-step autonomous development workflow |
| **Sandbox** | Isolated execution environment for plugins |

---

**END OF ARCHITECTURE DOCUMENT**

_Generated: 2025-10-28_
_Version: 1.0.0_
_Project: Tamma_
_Level: 3 (Greenfield)_
