# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Status

**Active Development**: 30 Elsa workflows implemented, TypeScript API + CLI operational, deployed on Hetzner VPS with Docker. Wiki site live at wiki.tamma.dev.

## Project Overview

**Tamma** is an AI-powered autonomous development orchestration platform that manages complete development workflows from GitHub/GitLab issue assignment to production deployment. The system operates as a "self-maintaining" platform capable of autonomously developing features for itself.

**Core Goal**: Achieve 70%+ autonomous issue completion rate with full audit trail and time-travel debugging capabilities.

**Key Architecture Patterns**:
- **DCB (Dynamic Consistency Boundary) Event Sourcing**: Single event stream with JSONB tags for 100% audit trail
- **Multi-Provider AI Abstraction**: Supports 8+ AI providers (Anthropic Claude, OpenAI, GitHub Copilot, Google Gemini, OpenCode, z.ai, Zen MCP, OpenRouter, local LLMs)
- **Multi-Platform Git Support**: 7 Git platforms (GitHub, GitLab, Gitea, Forgejo, Bitbucket, Azure DevOps, plain Git)
- **Hybrid Architecture**: Operates as standalone CLI, orchestrator service, or distributed worker pool

## Technology Stack

- **Language**: TypeScript 5.7+ (strict mode)
- **Runtime**: Node.js 22 LTS
- **Database**: PostgreSQL 17 (event store, task queue)
- **API Framework**: Fastify 5.x
- **Package Manager**: pnpm 9+ (monorepo with workspaces)
- **Testing**: Vitest 3.x (10-20x faster than Jest)
- **Build**: esbuild + tsc (esbuild for bundling, tsc for type checking)
- **CLI**: Ink 5.x (React for CLIs)
- **Logging**: Pino (5x faster than Winston)
- **Date/Time**: dayjs (6kb, moment-compatible)

## Repository Structure

```
tamma/
├── .dev/                      # Development knowledge base (READ THIS FIRST!)
│   ├── README.md             # Knowledge base guide
│   ├── spikes/               # Research and prototyping
│   ├── bugs/                 # Bug reports and resolutions
│   ├── findings/             # Pitfalls, best practices, lessons learned
│   ├── decisions/            # Architecture Decision Records (ADRs)
│   └── templates/            # Document templates
├── docs/                      # All planning and specification documents
│   ├── architecture.md        # Complete technical architecture (1900+ lines)
│   ├── PRD.md                # Product requirements document
│   ├── epics.md              # Epic breakdown with 58 stories
│   ├── tech-spec-epic-*.md   # Detailed technical specs per epic
│   ├── stories/              # Individual story implementation plans
│   │   ├── 1-0-ai-provider-strategy-research.md
│   │   ├── 1-1-ai-provider-interface-definition.md
│   │   └── ... (13 stories for Epic 1)
│   └── ...
├── wiki/                      # GitHub wiki pages
├── packages/                  # Monorepo packages (initialized)
│   ├── cli/                  # Ink-based CLI interface
│   ├── orchestrator/         # 14-step autonomous loop engine
│   ├── workers/              # Background job workers
│   ├── gates/                # Quality gates (build, test, security)
│   ├── intelligence/         # Research & ambiguity detection
│   ├── events/               # DCB event sourcing
│   ├── providers/            # AI provider abstraction
│   ├── platforms/            # Git platform abstraction
│   ├── api/                  # Fastify REST API + SSE
│   ├── dashboard/            # React observability dashboard
│   ├── observability/        # Logging & metrics (Pino)
│   └── shared/               # Shared utilities and types
├── database/                  # Future database migrations
└── pnpm-workspace.yaml       # Workspace configuration (to be created)
```

## ⚠️ IMPORTANT: Read Before Coding

**MANDATORY**: Before writing ANY code, read these documents in order:

1. **`BEFORE_YOU_CODE.md`** - Mandatory process guide (MUST READ FIRST!)
2. **`.dev/README.md`** - Development knowledge base guide
3. **`CLAUDE.md`** - This file (project guidelines)
4. **`docs/architecture.md`** - Technical architecture
5. **Story file** - Your specific task in `docs/stories/`

**Check these folders BEFORE coding:**
- `.dev/spikes/` - Existing research
- `.dev/bugs/` - Known bugs
- `.dev/findings/` - Pitfalls and best practices
- `.dev/decisions/` - Architecture decisions

**Failure to follow this process may result in wasted work!**

## Development Commands

```bash
# Package management
pnpm install                   # Install all dependencies
pnpm build                     # Build all packages
pnpm test                      # Run all tests with Vitest
pnpm lint                      # Run ESLint
pnpm format                    # Run Prettier

# Development
pnpm dev                       # Run in development mode
pnpm dev --filter @tamma/cli  # Run specific package

# Testing
pnpm test:unit                 # Unit tests only
pnpm test:integration          # Integration tests (requires credentials)
pnpm test:coverage             # Generate coverage report

# Database (orchestrator mode)
pnpm migrate:latest            # Run database migrations
pnpm migrate:rollback          # Rollback last migration
```

## Architecture Principles

### 1. Event Sourcing (DCB Pattern)

All system actions are captured as immutable events in a single PostgreSQL stream:

```typescript
interface DomainEvent {
  id: string;                    // UUID v7 (time-sortable)
  type: string;                  // "CODE.GENERATED.SUCCESS"
  timestamp: string;             // ISO 8601 millisecond precision
  tags: {                        // JSONB for flexible queries
    issueId?: string;
    prId?: string;
    userId?: string;
    mode?: 'dev' | 'business';
    provider?: string;
    [key: string]: string | undefined;
  };
  metadata: {
    workflowVersion: string;
    eventSource: 'system' | 'plugin';
  };
  data: Record<string, unknown>;
}
```

**Key Benefits**:
- Complete audit trail (compliance: SOC2, ISO27001, GDPR)
- Time-travel debugging (replay any workflow state)
- Black-box testing (reproduce issues with exact context)

### 2. Naming Conventions

**Files & Directories**:
- Files: kebab-case (`event-store.ts`, `plugin-manager.ts`)
- Test files: `*.test.ts` (colocated with source)
- Type definitions: `*.types.ts`

**Code**:
- Interfaces: `I` prefix (`IPluginManifest`, `IEventStore`)
- Classes: PascalCase (`PluginManager`, `EventStore`)
- Functions: camelCase (`evaluateCondition()`, `appendEvent()`)
- Boolean functions: `is/has/should` prefix (`isRetryable()`, `hasCapability()`)
- Private functions: `_` prefix (`_validateSchema()`)
- Constants: SCREAMING_SNAKE_CASE (`MAX_RETRY_ATTEMPTS`, `DEFAULT_TIMEOUT_MS`)

**API Endpoints**:
```
GET    /api/v1/issues/:issueId
POST   /api/v1/issues
PATCH  /api/v1/issues/:issueId
DELETE /api/v1/issues/:issueId
GET    /api/v1/issues/:issueId/events
POST   /api/v1/plugins/:pluginName/install
SSE    /api/v1/events/stream
```

**Event Types** (Pattern: `AGGREGATE.ACTION.STATUS`):
```
ISSUE.ASSIGNED.SUCCESS
CODE.GENERATED.SUCCESS
CODE.GENERATED.FAILED
PLUGIN.DEBUG_SNAPSHOT.SUCCESS
TRIGGER.ACTIVATED
WORKFLOW.STEP_COMPLETED
GATE.REVIEW_REQUESTED
```

### 3. TypeScript Strict Mode

All code must compile with strict TypeScript settings:

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

**Import Order**:
```typescript
// 1. Node.js built-ins
import { readFile } from 'fs/promises';

// 2. External dependencies
import dayjs from 'dayjs';

// 3. Internal packages
import type { IEventStore } from '@tamma/shared/contracts';

// 4. Relative imports
import { PluginManager } from '../plugin-manager';
```

**Async/Await** (ALWAYS use, NEVER .then()/.catch()):
```typescript
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

// ❌ BAD
function getUser(id: string): Promise<User> {
  return db.query('SELECT * FROM users WHERE id = ?', [id])
    .then(rows => rows[0])
    .catch(error => {
      logger.error('Failed to get user', { error, userId: id });
      throw error;
    });
}
```

### 4. Error Handling

Use custom error class with structured context:

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

**Retry Pattern** (All async operations):
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

### 5. Logging (Pino)

All logs use structured JSON format:

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

**Log Levels**:
- DEBUG: Verbose details for development
- INFO: Key milestones (workflow steps, API calls)
- WARN: Recoverable issues (retry attempts, degraded mode)
- ERROR: Failures requiring attention

**Sensitive Data**: ALWAYS redact API keys, tokens, passwords from logs.

### 6. State Management

**NEVER mutate state** - Always create new objects:

```typescript
// ❌ BAD
context.step = 'CODE_GENERATION';

// ✅ GOOD
const updatedContext = {
  ...context,
  step: 'CODE_GENERATION',
  updatedAt: dayjs.utc().toISOString()
};
```

### 7. Date/Time Handling

**ALWAYS use ISO 8601 with millisecond precision**:

```typescript
import dayjs from 'dayjs';
import utc from 'dayjs/plugin/utc';
dayjs.extend(utc);

const now = dayjs.utc().toISOString(); // "2025-10-28T12:34:56.789Z"
```

## Implementation Guidelines

### Epic 1: Foundation & Core Infrastructure (Current Focus)

**Goal**: Establish multi-provider AI abstraction, multi-platform Git integration, and hybrid orchestrator/worker architecture.

**Stories (12 total)**:
1. **Story 1-0**: AI Provider Strategy Research - Document comparing 8+ AI providers with cost analysis and recommendations
2. **Story 1-1**: AI Provider Interface Definition - Define `IAIProvider` interface with streaming, context management, error handling
3. **Story 1-2**: Anthropic Claude Provider Implementation - Reference implementation using `@anthropic-ai/sdk`
4. **Story 1-3**: Provider Configuration Management - Multi-provider config with environment variable overrides
5. **Story 1-4**: Git Platform Interface Definition - Define `IGitPlatform` interface for PR, issue, branch operations
6. **Story 1-5**: GitHub Platform Implementation - GitHub API integration using Octokit
7. **Story 1-6**: GitLab Platform Implementation - GitLab API integration
8. **Story 1-7**: Git Platform Configuration Management - Multi-platform config with credential handling
9. **Story 1-8**: Hybrid Orchestrator/Worker Architecture Design - Architecture document with sequence diagrams
10. **Story 1-9**: Basic CLI Scaffolding - CLI with mode selection (orchestrator/worker/standalone)
11. **Story 1-10**: Additional AI Provider Implementations - OpenAI, GitHub Copilot, Gemini, OpenCode, z.ai, Zen MCP, OpenRouter, local LLMs
12. **Story 1-11**: Additional Git Platform Implementations - Gitea, Forgejo, Bitbucket, Azure DevOps, plain Git

**Starting Point**: Begin with Story 1-0 (research) or Story 1-1 (interface definition). All story documentation is in `docs/stories/`.

### Key Design Patterns

**1. Interface-Based Design** (Dependency Inversion):
```typescript
// Define interface first
interface IAIProvider {
  initialize(config: ProviderConfig): Promise<void>;
  sendMessage(request: MessageRequest): Promise<AsyncIterable<MessageChunk>>;
  getCapabilities(): ProviderCapabilities;
  dispose(): Promise<void>;
}

// Implementations depend on interface
class AnthropicClaudeProvider implements IAIProvider { /* ... */ }
class OpenAIProvider implements IAIProvider { /* ... */ }
```

**2. Plugin Architecture** (Dynamic Provider Registration):
```typescript
class ProviderRegistry {
  register(plugin: ProviderPlugin): void;
  getProvider(name: string): IAIProvider;
  listProviders(): string[];
}
```

**3. Circuit Breaker Pattern** (API Resilience):
```typescript
// Provider API failures: 5 failures in 60s → open for 300s
// Platform API failures: 5 failures in 60s → open for 300s
```

### Testing Strategy

**Unit Tests** (Jest → Vitest 3.x):
- Coverage targets: 80% line, 75% branch, 85% function
- Critical paths (error handling, retry logic): 100%
- Mock external APIs using MSW (Mock Service Worker)
- Mock database using in-memory SQLite

**Integration Tests**:
- Real API calls to test providers and platforms
- Requires test credentials: `ANTHROPIC_API_KEY_TEST`, `GITHUB_TOKEN_TEST`, `GITLAB_TOKEN_TEST`
- Test repositories: `tamma-test-github`, `tamma-test-gitlab`

**Performance Tests** (Artillery.io):
- Provider API: 100 concurrent requests, p95 < 500ms
- Platform API: 500 concurrent requests, p95 < 1000ms
- Orchestrator: 5 tasks/second sustained throughput
- CLI startup: p95 < 1000ms cold start

### Security Requirements

**Credential Management**:
- API keys encrypted at rest (AES-256)
- OS-specific secure storage: Windows Credential Manager, macOS Keychain, Linux Secret Service API
- Config files: chmod 600 (owner read/write only)
- NO credentials in logs, error messages, or debug output

**Network Security**:
- All API calls over HTTPS/TLS 1.3+
- Certificate validation enabled
- Webhook signature verification for platform events

**Input Validation**:
- Sanitize all user inputs against injection attacks
- Validate provider messages against schema
- Validate platform API parameters
- File path validation to prevent directory traversal

## Common Development Tasks

### Adding a New AI Provider

1. Create provider class implementing `IAIProvider` in `packages/providers/src/`
2. Add provider config schema to `packages/config/src/schemas/provider.schema.ts`
3. Register provider in `ProviderRegistry`
4. Add unit tests covering happy path, error cases, retry logic
5. Add integration test with real API (requires test credentials)
6. Update documentation with setup instructions

### Adding a New Git Platform

1. Create platform class implementing `IGitPlatform` in `packages/platforms/src/`
2. Add platform config schema to `packages/config/src/schemas/platform.schema.ts`
3. Implement platform-specific API client
4. Map platform models to normalized interfaces (PR, Issue, Branch)
5. Add unit tests covering API operations, pagination, rate limiting
6. Add integration test with real API
7. Update documentation with setup instructions

### Emitting Events for Audit Trail

All operations must emit events for DCB event sourcing:

```typescript
await eventStore.append({
  type: 'CODE.GENERATED.SUCCESS',
  tags: {
    issueId: context.issueId,
    userId: context.userId,
    provider: 'anthropic-claude',
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

## Documentation References

- **Architecture**: `docs/architecture.md` - Complete technical architecture (1900+ lines)
- **PRD**: `docs/PRD.md` - Product requirements and acceptance criteria
- **Epics**: `docs/epics.md` - Epic breakdown with 50+ stories
- **Tech Specs**: `docs/tech-spec-epic-*.md` - Detailed specs per epic
- **Stories**: `docs/stories/` - Individual story implementation plans

## Key Architectural Decisions

1. **Node.js 22 LTS** over Bun: Production stability, crypto performance (10x faster for security scanning)
2. **PostgreSQL + Emmett** over EventStoreDB: Unified storage, JSONB flexibility, lower operational complexity
3. **Fastify** over Express/Hono: Fastest Node.js framework, native TypeScript, schema validation
4. **Server-Sent Events** over WebSocket: Simpler, unidirectional, HTTP/2 multiplexing, lower overhead
5. **Vitest** over Jest: 10-20x faster, native TypeScript, ESM support
6. **Pino** over Winston: 5x faster, structured JSON, zero-copy logging
7. **pnpm** over npm/yarn: Fastest, 70-80% disk savings, monorepo-optimized
8. **DCB Pattern** over aggregate-per-stream: Simpler cross-aggregate queries, flexible tagging, better audit trail

## Self-Maintenance Goal

Tamma is designed to autonomously develop features for itself. This means:
- The system must maintain 100% test coverage on critical paths
- All changes must pass automated quality gates (build, test, security scan)
- Breaking changes require mandatory human approval
- Complete audit trail enables time-travel debugging of self-made changes

**Validation Milestone**: Tamma successfully completes an Epic 2 story (autonomous development workflow) for Epic 3 (quality gates), demonstrating self-maintenance capability.

## Support and Resources

- **GitHub Repository**: https://github.com/meywd/tamma
- **Issues**: https://github.com/meywd/tamma/issues
- **Discussions**: https://github.com/meywd/tamma/discussions
- **Wiki**: https://github.com/meywd/tamma/wiki

## Operating Modes

Tamma deploys in one of two modes, chosen at process startup. Mode determines who is the **principal** (the entity that owns settings, prompts, providers, secrets) and what RBAC applies.

| Mode | Process entry | Principal | RBAC | Typical tenancy |
|---|---|---|---|---|
| **single-user** | `tamma start` (self-hosted engine), `tamma server` (self-hosted HTTP) | The user | None — sole user owns everything | One user per Tamma instance |
| **saas** | `tamma api` (SaaS / GitHub App) | The tenant (org) | `tenant_owner` / `tenant_admin` / `member` | Many tenants per Tamma instance |

**Mode detection**: a deployment is either single-user OR SaaS — not both. The mode is settled by the entry-point binary plus env config (presence of `Tamma:TenantSharedSecret`, `ConnectionStrings:ControlPlane`, etc. signals SaaS). All request handlers can assume a stable mode for the lifetime of the process.

**Universal rule for any tenant-aware feature**: design two scoping models, not one. Every feature that customizes Tamma's behavior (prompts, providers, sanitization rules, agent configs, budgets, ...) must answer "in single-user mode, who owns this?" AND "in SaaS mode, who owns this?" separately. The wrong default is to ship the single-user model and assume it works for SaaS.

## Prompt Store Architecture

### Data Model

```
System Defaults (immutable, shipped with Tamma):
  ├── SYSTEM_PROMPTS[role]           — 8 role identity prompts
  ├── ACTION_DEFAULTS[action]        — 10 action base templates (safety net)
  └── ROLE_ACTION_DEFAULTS[role][action] — 80 role+action templates (good defaults)

Overrides (stored in Postgres `prompt_overrides`):
  ├── single-user mode: keyed by user_id (the sole user owns their overrides)
  └── SaaS mode:        keyed by tenant_id (tenant_admin owns the team's overrides;
                                            member users don't have edit permission)
```

### Resolution Order — single-user mode

For a given `(userId, role, action)`:
1. User's role+action override → if exists, use it
2. System default role+action → if exists, use it
3. User's action default override → if exists, use it
4. System default action template → safety net

For system prompt `(userId, role)`:
1. User's role system prompt override → if exists, use it
2. System default role system prompt

### Resolution Order — SaaS mode

For a given `(tenantId, role, action)`:
1. Tenant's role+action override → if exists, use it
2. System default role+action → if exists, use it
3. Tenant's action default override → if exists, use it
4. System default action template → safety net

For system prompt `(tenantId, role)`:
1. Tenant's role system prompt override → if exists, use it
2. System default role system prompt

**No per-user override layer in SaaS.** Member users see the tenant_admin's resolved prompt without edit access. Per-user personalization on top of tenant prompts is intentionally NOT a feature — keeps audit/compliance simple and avoids "one user's customization broke an agent run" support cases.

### RBAC

| Action | single-user | SaaS |
|---|---|---|
| GET resolved prompt | any user | any tenant member |
| PUT/DELETE override | any user | `tenant_owner` or `tenant_admin` only |
| GET system defaults | any user | any tenant member |

In SaaS, the upsert/delete endpoints reject member-role users with 403.

### Storage

```sql
CREATE TABLE prompt_overrides (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id UUID,                 -- set in single-user mode; NULL in SaaS mode
  tenant_id UUID,               -- set in SaaS mode; NULL in single-user mode
  scope TEXT NOT NULL,          -- 'role-system' | 'action-default' | 'role-action'
  role TEXT,                    -- NULL for action-default scope
  action TEXT,                  -- NULL for role-system scope
  template TEXT NOT NULL,
  system_prompt TEXT,
  variables TEXT[],
  enable_tools BOOLEAN DEFAULT false,
  max_tokens INTEGER DEFAULT 4096,
  created_at TIMESTAMPTZ DEFAULT now(),
  updated_at TIMESTAMPTZ DEFAULT now(),
  -- Exactly one of user_id / tenant_id is non-null (CHECK constraint)
  CONSTRAINT principal_xor CHECK (
    (user_id IS NOT NULL AND tenant_id IS NULL)
    OR (user_id IS NULL AND tenant_id IS NOT NULL)
  ),
  UNIQUE NULLS NOT DISTINCT (user_id, tenant_id, scope, role, action)
);
```

System defaults remain in code (e.g. `SystemPrompts.cs`). Overrides in Postgres. The Prompt Store reads the appropriate column based on mode and applies the per-mode resolution order above.

### API

| Endpoint | single-user | SaaS |
|---|---|---|
| `GET /api/prompts` | list all (resolved for current user) | list all (resolved for current tenant) |
| `GET /api/prompts/:role/:action` | get resolved | get resolved |
| `PUT /api/prompts/:role/:action` | create/update user override | create/update tenant override (owner/admin only) |
| `DELETE /api/prompts/:role/:action` | delete user override → fall back to system | delete tenant override → fall back to system (owner/admin only) |
| `POST /api/prompts/:role/:action/reset` | alias for DELETE | alias for DELETE |
| `GET /api/prompts/defaults` | list system defaults | list system defaults |
| `GET /api/prompts/defaults/:action` | get action default | get action default |
| `GET /api/prompts/defaults/:role/:action` | get role+action default | get role+action default |

The endpoint shape is identical between modes — the auth middleware decides which override key (`user_id` or `tenant_id`) to use based on mode + caller identity. Member users in SaaS mode hit a 403 on PUT/DELETE.

### Convention Templates

20 language/framework convention templates available at:
```
GET /api/convention-templates          — list all (key, name, description)
GET /api/convention-templates/:key     — get full template with conventions string
```

User selects a starter, customizes it, saves to `.tamma/config.json` in their repo as `conventions` field. LlmCallWorkflow injects it into every prompt via `{{conventions}}`.

## Multi-tenant provisioning (Cranl)

The C# port supports two infra modes per tenant:

- **Shared infrastructure (default)**: tenant rides on the central Postgres on Hetzner via Phase-3 RLS. No external resources. This is the dev / self-hosted default and what every tenant gets when Cranl is not configured.
- **Per-tenant Cranl resources**: each tenant gets one Cranl Project + one Postgres Database + one Application (the Elsa engine, deployed from the Tamma GitHub repo). Central Tamma stays the control plane (auth, orgs, tenant registry, routing); Cranl is the data + compute plane.

**Enable Cranl provisioning** by setting:
```
Cranl:ApiKey                — cranl_sk_<32 chars> (org-scoped)
Cranl:OrganizationId         — Cranl org id that owns Tamma's resources
Cranl:RepositoryId           — Cranl's id for the Tamma engine repo (registered via their GitHub App)
Cranl:DefaultRegion          — e.g. germany-1 (default)
Cranl:DefaultBuildType       — dockerfile (default) or nixpacks
Cranl:AppBuildPath           — /apps/tamma-elsa (default)
Cranl:EncryptionKey          — base64-encoded 32 random bytes (production: REQUIRED)
Tamma:ControlPlaneUrl        — https://api.tamma.dev (used as TAMMA_CONTROL_PLANE_URL on each engine)
Tamma:TenantSharedSecret     — HMAC secret pushed as TAMMA_SHARED_SECRET to each engine
```

When `Cranl:ApiKey` is unset the Null seam wins (`NullTenantProvisioner`) and tenants stay on the shared central DB. The admin endpoints still work — they just mark tenants Ready immediately.

**Admin endpoints** (platform-owner only — `OwnerAccess` policy):
```
POST  /api/admin/tenants/{tenantId}/provision     body: { region, customName? }
GET   /api/admin/tenants/{tenantId}/provisioning
POST  /api/admin/tenants/{tenantId}/deprovision
```

`POST /provision` returns `202 Accepted` immediately; the long-running Cranl polling (db ready ≈ 1-3 min, app deploy ≈ 3-8 min) runs on the existing `TaskQueueProcessor` thread. Subsequent `GET /provisioning` calls report state transitions: `pending → database_provisioning → database_ready → app_provisioning → app_deploying → ready`.

**Routing** (current state): per-request DB connection switching by tenant is wired in production via `ConnectionStrings:ControlPlane` + `LruPooledTenantConnectionResolver`. In dev/test environments without a CP connection string, the resolver falls back to `StubTenantConnectionResolver` which keeps every tenant on the central DB. The `cranl_database_url_encrypted` column is populated correctly during provisioning, so flipping routing on for a tenant only requires the env config — no code change.

**Encryption**: tenant `DATABASE_URL` is AES-GCM-encrypted at rest via `TenantSecretProtector`. Key source: `Cranl:EncryptionKey` (base64, 32 bytes) — required in production. Without it, a key is derived from `Cranl:ApiKey` via HKDF and a warning logged. TODO: migrate to OpenBao via `IKeyProtector` once Story 28-13 lands.

## Notes for Claude Code

- **Story-driven development**: Each story in `docs/stories/` has comprehensive technical context and acceptance criteria.
- **Test-first approach**: Write tests before implementation (TDD workflow).
- **Event emission**: Every operation must emit events for audit trail.
- **Strict TypeScript**: All code must compile with strict mode enabled.
- **Security-first**: Never commit secrets, always encrypt credentials, validate all inputs.
- **Documentation**: Update docs when adding new providers, platforms, or features.
- **No migration anxiety**: App is not in production with users. All data stores can be replaced without migration.
