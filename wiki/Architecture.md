# Tamma System Architecture

_Last updated: 2026-04-22._

This page is the definitive map of the running system. Every claim is grounded in code paths you can open. The page is heavy on **"where it lives"** — pair it with the [Epic pages](Epics.md) for "why it exists" and the sprint stories for "how far we got."

Tamma is a **dual-stack** platform:

- **C# / .NET 8** (`apps/tamma-elsa/`) hosts the workflow engine, the REST API, persistent durable state, and every auth / tenancy / secret plane added since Epic 18.
- **TypeScript / Node 22 LTS** (`packages/`, `apps/tamma-engine`, `apps/wiki-site`, `apps/marketing-site`) hosts AI-provider abstractions, CLI agents, the Ink CLI, the React dashboard, a Fastify intelligence sidecar, the wiki/marketing sites, and the engine worker loop.

The two stacks talk over HTTP via a narrow seam (`packages/orchestrator/src/elsa-client.ts` + `apps/tamma-elsa/src/Tamma.Api/Services/KnowledgeBase/IntelligenceHttpClient.cs`); nothing shares in-process state.

---

## 1. Three deployment modes

Tamma ships the same codebase under three operational topologies. `IAgentExecutor` (`apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/IAgentExecutor.cs`) and `AgentExecutorFactory` (`apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/AgentExecutorFactory.cs`) pick the execution surface at runtime from: explicit override → `TAMMA_AGENT_MODE` env → `Agent:ExecutorMode` config → auto-detection (GitHub-Actions when `GitHub:AppId` + `GitHub:PrivateKey` are set, Local otherwise).

```
 Mode                 Processes                                DBs                         Executor
 ───────────────────  ───────────────────────────────────────  ──────────────────────────  ────────────────────────
 CLI (standalone)     tamma start (Ink CLI)                    none                        LocalExecutor
                      tamma-elsa/ElsaServer (embedded / local)  (or ephemeral tamma_dev pg)

 Self-hosted SaaS     tamma-api (.NET 8)                       tamma (central Postgres)    GitHubActionsExecutor
 (single-tenant)      elsa-server (.NET 8)                     tamma_control (CP, opt)     or LocalExecutor
                      tamma-engine (TS Node 22)                per-tenant (Epic 28 only)
                      tamma-dashboard (nginx + React)
                      intelligence-server (TS Fastify)
                      nginx-proxy / oauth2-proxy
                      postgres + rabbitmq + chromadb
                      opensearch (optional, profile=observability)

 Multi-tenant SaaS    tamma-api                                tamma_control (CP)          GitHubActionsExecutor
 (db-per-tenant)      elsa-server                              tenant_<guid> × N (TP)      (agent code runs on
                      intelligence-server                      (Cranl / Hetzner / CF / BYO  tenant's own Actions
                      nginx-proxy                               per Epic 30 backend)        runners — never on the
                      + every service above                                                  control plane)
```

| Facet                | CLI                             | Self-hosted SaaS                             | Multi-tenant SaaS                                    |
| -------------------- | ------------------------------- | -------------------------------------------- | ---------------------------------------------------- |
| Entry point          | `packages/cli/src/index.tsx`    | `apps/tamma-elsa/src/Tamma.Api/Program.cs`   | Same as self-hosted + `Cranl:ApiKey` set             |
| DB layout            | optional dev Postgres           | Central Postgres (single schema)             | `tamma_control` CP + per-tenant DBs (Epic 28)        |
| Tenant isolation     | none (single user)              | Epic 17 RLS scaffold (dormant / superseded)  | Epic 28 DB-per-tenant (authoritative)                |
| Agent execution      | `LocalExecutor` subprocess      | `LocalExecutor` or `GitHubActionsExecutor`   | Only `GitHubActionsExecutor` (user code stays on their runner) |
| Provisioning         | n/a                             | Seed schema at startup                       | `ITenantInfrastructureProvider` (Epic 30)            |
| GitHub App required  | No                              | Optional (LocalExecutor fallback)            | Yes (mandatory for dispatch)                         |

CLI startup path: `packages/cli/src/index.tsx` → `packages/cli/src/commands/start.tsx` / `server.ts` / `api.ts`. The LocalExecutor shell-out protocol is implemented on both sides: C# writes `exec-request-<sessionId>.json`, invokes `packages/cli/src/commands/execute-agent.ts`, and collects `exec-result-<sessionId>.json`. See `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/LocalExecutor.cs` for the full protocol comment.

---

## 2. Component map

Every running process. Cited paths are the concrete entry points.

### 2.1 C# plane (`apps/tamma-elsa/`)

| Service              | Code home                                                        | Purpose                                                                 | Key config keys                                                |
| -------------------- | ---------------------------------------------------------------- | ----------------------------------------------------------------------- | -------------------------------------------------------------- |
| `tamma-api`          | `apps/tamma-elsa/src/Tamma.Api/Program.cs`                       | REST API: auth, tenants, engine callbacks, admin, KB proxy, SaaS        | `ConnectionStrings:TammaDb`, `:TammaAppDb`, `:ControlPlane`; `Jwt:Secret`; `GitHub:AppId`, `:PrivateKey`, `:WebhookSecret`; `Dashboard:Url`; `TAMMA_SECRET_STORE_KEK_PRIMARY` |
| `elsa-server`        | `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs`                | Elsa 3.5.3 workflow engine + 35 code-first workflows + agent seeder    | `ConnectionStrings:DefaultConnection`; `Elsa:Identity:SigningKey`; `Elsa:Identity:AdminUser:Password`; `ELSA_SEED_FORCE` |
| `elsa-studio`        | `apps/tamma-elsa/src/Tamma.Studio/` (+ docker nginx wrapper)      | Blazor WASM workflow designer                                           | `ELSASERVER__URL`; `ELSA_ADMIN_PASSWORD`                       |
| `Tamma.Activities`   | `apps/tamma-elsa/src/Tamma.Activities/`                          | Activities used by workflows (ADL, LlmCall, Tools, AgentDispatch, etc.) | (library — no config of its own)                               |
| `Tamma.Data`         | `apps/tamma-elsa/src/Tamma.Data/`                                | EF Core DbContexts + migrations + repositories                          | (library)                                                      |
| `Tamma.Core`         | `apps/tamma-elsa/src/Tamma.Core/`                                | Shared entities / enums / interfaces (MentorshipSession, etc.)          | (library)                                                      |

### 2.2 TypeScript plane

| Service                        | Code home                                                    | Purpose                                                                  | Key config                                    |
| ------------------------------ | ------------------------------------------------------------ | ------------------------------------------------------------------------ | --------------------------------------------- |
| `tamma-engine`                 | `apps/tamma-engine/src/index.ts`                             | Autonomous issue-processing loop (boots into a worker that polls work)   | `DATABASE_URL`, `TAMMA_API_URL`, `GITHUB_APP_*` |
| `intelligence-server`          | `packages/intelligence-server/src/server.ts`                 | Fastify sidecar exposing `/kb/*` so the C# API can delegate KB endpoints | `INTELLIGENCE_PORT`, `CHROMADB_URL`           |
| `tamma-dashboard`              | `packages/dashboard/src/index.tsx` → Vite SPA                | React admin UI (nginx-served static bundle)                              | `VITE_API_URL` (build-time)                   |
| `tamma-cli`                    | `packages/cli/src/index.tsx` (Ink 5 React CLI)               | `tamma start`, `tamma server`, `tamma api`, `tamma execute-agent`, etc.  | `~/.tamma/config.json` layered config         |
| `wiki-site`                    | `apps/wiki-site/src/worker.ts` + `App.tsx`                   | Docs site (Vite SPA on Cloudflare Workers at wiki.tamma.dev)             | `wrangler.jsonc`                              |
| `marketing-site`               | `apps/marketing-site/src/index.ts`                           | Landing site (Cloudflare Worker + KV for signups at tamma.dev)           | `wrangler.toml`                               |
| Providers (`@tamma/providers`) | `packages/providers/src/`                                    | `IAgentProvider` / `IAIProvider` impls (Claude Code, OpenCode, OpenRouter, Zen MCP) | n/a (invoked via config)                      |
| Platforms (`@tamma/platforms`) | `packages/platforms/src/github/`                             | `IGitPlatform` impl — Octokit-based PR / issue / branch ops              | GitHub token / App                            |
| Orchestrator                   | `packages/orchestrator/src/engine.ts` + `elsa-client.ts`      | In-process engine brain + HTTP bridge to ElsaServer                      | `ELSA_SERVER_URL`, `ELSA_API_KEY`             |

### 2.3 Workflows (`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/`)

Thirty-five code-first workflows (all are `WorkflowBase` subclasses registered via `elsa.AddWorkflowsFrom<LlmCallWorkflow>()` in `ElsaServer/Program.cs:112`):

| Workflow                                   | Entry role                                                           |
| ------------------------------------------ | -------------------------------------------------------------------- |
| `AdlOrchestratorWorkflow`                  | Main autonomous-development loop                                     |
| `SingleIssueCycleWorkflow`                 | End-to-end lifecycle of a single issue (refactored to call `ExecuteAgentActivity`) |
| `LlmCallWorkflow`                          | See §7.5                                                             |
| `BranchCreationWorkflow` / `PullRequestWorkflow` / `MergeWorkflow` / `MergeApprovalWorkflow` | Git platform ADL steps                              |
| `CodeReviewWorkflow` / `ReviewFixWorkflow` | Review cycle                                                         |
| `TddWorkflow` / `TddWithDebugRetryWorkflow`| TDD cycle                                                            |
| `TestingWorkflow` / `CiWithDebugRetryWorkflow` | Test + CI retry loop                                             |
| `ContextGatheringWorkflow`                 | Multi-source context assembly                                        |
| `PlanGenerationWorkflow` / `PlanReviewWorkflow` | Planning + human review                                         |
| `AssessmentWorkflow` / `BlockerDiagnosisWorkflow` / `DebuggingWorkflow` | Diagnostic workflows                          |
| `DeploymentPipelineWorkflow`               | Deploy pipeline                                                      |
| `IssueTriageWorkflow` / `TriageContextGatheringWorkflow` / `TriagePanelReviewWorkflow` / `TriagePODecisionWorkflow` / `TriageItemCycleWorkflow` | Triage cascade |
| `MentorshipWorkflow`                       | 28-state mentorship state machine                                    |
| `TaskCreationWorkflow` / `TaskReviewWorkflow` / `TestCaseCreationWorkflow` | Upstream work-item cycle                             |
| `UpdateIssueStatusWorkflow`                | Status mirror back to the Git platform                               |
| `CreateTenantWorkflow` / `DeleteTenantWorkflow` | Tenant lifecycle (composed from `Tamma.Activities/TenantLifecycle/`) |

Activity categories (`apps/tamma-elsa/src/Tamma.Activities/`):

| Category        | Dir                 | Activity count / notable members                                                              |
| --------------- | ------------------- | --------------------------------------------------------------------------------------------- |
| ADL             | `ADL/`              | 23 activities — SelectWorkItem, CreateBranch, CreatePR, MergePR, WaitForApproval, …            |
| Agent Dispatch  | `AgentDispatch/`    | 4 activities (Dispatch / Monitor / Collect / ExecuteAgent) + executors + WebhookSignalRegistry |
| AI              | `AI/`               | ClaudeAnalysis, ContextGathering, SuggestionGenerator                                          |
| Assessment      | `Assessment/`       | Developer skill assessment                                                                     |
| Blocker         | `Blocker/`          | ClassifyBlocker, DetectProgress, EscalateToSenior                                              |
| Context         | `Context/`          | FetchFileContents, FetchRecentCommits, AssembleContext, ApplyBudget                            |
| Debug           | `Debug/`            | 12 activities — Collect*, SelectHypothesis, AIDiagnosis, WriteRegressionTest, …                |
| Integration     | `Integration/`      | GitHub, Slack, Jira, Email                                                                     |
| LlmCall         | `LlmCall/`          | 9 activities — see §7.5                                                                        |
| Mentorship      | `Mentorship/`       | AssessJuniorCapability, ProvideGuidance, QualityGateCheck, …                                   |
| Review          | `Review/`           | (via ADL) AnalyzeReview, ApplyReviewFixes                                                      |
| Security        | `Security/`         | `ContentSanitizer`, `ErrorRedactor`, `ActionGate`, `ToolCallValidator`, `ProviderAllowlist`    |
| TDD / Testing   | `TDD/`, `Testing/`  | TDD cycle management, test execution                                                           |
| Tool Execution  | `ToolExecution/`    | `ParallelToolExecutor`, `IFileSystemTool`, `IToolLoopEventSink`                                |
| Tenant Lifecycle| `TenantLifecycle/`  | 15 activities — BuildConnectionString, CreateDatabase, Encrypt, Migrate, Seed, WarmPool, …      |
| Code Index      | `CodeIndex/`        | Codebase indexing hooks                                                                        |
| Core            | `Core/`             | Shared primitives                                                                              |

### 2.4 REST API surface (`apps/tamma-elsa/src/Tamma.Api/Program.cs:728-1080`)

~170 routes grouped:

| Group                | Prefix                           | Policy                   | File                                   |
| -------------------- | -------------------------------- | ------------------------ | -------------------------------------- |
| Health               | `/api/health`, `/health*`        | none                     | `Endpoints/HealthEndpoints.cs`         |
| Auth                 | `/api/v1/auth/*`, `/api/auth/*`  | mostly none + member     | `Endpoints/AuthEndpoints.cs` (982 lines) |
| Admin                | `/api/admin/*`                   | AdminAccess / OwnerAccess | `Endpoints/AdminEndpoints.cs`          |
| Admin analytics      | `/api/admin/analytics/*`         | OwnerAccess              | `Services/Analytics/`                  |
| Admin tenant provisioning | `/api/admin/tenants/*`      | OwnerAccess              | `Endpoints/AdminEndpoints.cs` + `Services/Provisioning/` |
| Admin KEK rotation   | `/api/admin/kek/rotate/*`        | OwnerAccess              | `Endpoints/KekRotationEndpoints.cs`    |
| Orgs / Tenants       | `/api/v1/orgs/*`                 | MemberAccess + membership filter | `Endpoints/OrgEndpoints.cs` (844 lines) |
| Onboarding wizard    | `/api/v1/onboarding/*`           | MemberAccess             | `Endpoints/OnboardingEndpoints.cs`     |
| Agents config        | `/api/v1/agents/*`               | SettingsView / Manage    | `Endpoints/AgentEndpoints.cs`          |
| Prompts              | `/api/prompts/*`                 | SettingsView / Manage    | `Endpoints/PromptEndpoints.cs`         |
| Convention templates | `/api/convention-templates/*`    | none                     | `Endpoints/ConventionEndpoints.cs`     |
| Config               | `/api/config/*`                  | SettingsView / Manage    | `Endpoints/SettingsEndpoints.cs`       |
| Providers            | `/api/providers/*`               | SettingsView / Manage    | `Endpoints/ProviderEndpoints.cs`       |
| Engine               | `/api/engine/*`                  | WorkflowsView / Manage   | `Endpoints/EngineEndpoints.cs`         |
| Workflows            | `/api/workflows/*`               | WorkflowsView / Manage / Delete | `Endpoints/WorkflowEndpoints.cs` |
| GitHub App           | `/api/github/webhooks`, `/api/github/callback` | rate-limited only | `Endpoints/GitHubEndpoints.cs` |
| SaaS (API key)       | `/api/v1/llm/chat`, `/api/v1/workflows/*/*` | API key auth | `Endpoints/SaaSEndpoints.cs`           |
| Dashboard            | `/api/dashboard/*`               | DashboardView            | `Endpoints/DashboardEndpoints.cs`      |
| Knowledge base       | `/api/kb/*` (30 routes)          | SettingsView / Manage    | `Endpoints/KbEndpoints.cs` → `IntelligenceHttpClient` |

### 2.5 Infrastructure

| Service        | Image                                       | Purpose                                    | Where configured                                         |
| -------------- | ------------------------------------------- | ------------------------------------------ | -------------------------------------------------------- |
| postgres       | `postgres:16-alpine`                         | Primary data store, Elsa runtime, events   | `docker/docker-compose.yml` + `docker/init-db.sql`       |
| rabbitmq       | `rabbitmq:3.13-management-alpine`           | Message broker (Elsa optional bus)         | `docker/docker-compose.yml`                              |
| chromadb       | `chromadb/chroma:0.6.3`                     | Vector store for `intelligence-server`     | `docker/docker-compose.yml`                              |
| opensearch     | `opensearchproject/opensearch:2.19.0`       | Structured-log aggregation (Serilog sink)  | `docker/docker-compose.yml` profile `observability`      |
| opensearch-dashboards | `opensearchproject/opensearch-dashboards:2.19.0` | Log visualization                          | same profile                                             |
| nginx-proxy    | `nginx:1.27-alpine`                         | Reverse proxy + origin SSL termination     | `docker/nginx-proxy.conf.template`                       |
| oauth2-proxy   | `quay.io/oauth2-proxy/oauth2-proxy:v7.7.1`  | GitHub OAuth for nginx `auth_request`      | `docker/oauth2-proxy.cfg`                                |

Memory budget (from `docker/docker-compose.yml` + production overrides in `docker-compose.prod.yml`): ~7.1 GB without OpenSearch, ~11.8 GB with. Hetzner CPX42 (16 GB) comfortably runs the full stack.

---

## 3. Data storage layout

There are **two** EF Core DbContexts: `ControlPlaneDbContext` and `TenantDbContext` (Epic 28 split). The pre-Epic-28 `TammaDbContext` / `TammaAppDbContext` pair was **deleted in Wave A.5** (see §13).

### 3.1 Control-plane DB (Epic 28, authoritative)

Code: `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs`.
Connection string: `ConnectionStrings:ControlPlane`. Falls back to the admin connection for local dev (logged at startup).
Migrations: `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/` — a single collapsed `InitialControlPlane` baseline (unified-tenancy Phase 0, 2026-06-09); history table `__ControlPlaneMigrationsHistory` (runtime == design-time).

**15 core tables** (all CP-resident, no tenant query filters — the CP is not tenant-scoped):

| Table                          | Entity                       | Purpose                                                   |
| ------------------------------ | ---------------------------- | --------------------------------------------------------- |
| `users`                        | `User`                       | Platform identities (email, github_id, settings jsonb)    |
| `refresh_tokens`               | `RefreshToken`               | Session refresh token hashes                              |
| `password_reset_tokens`        | `PasswordResetToken`         | Forgot-password reset hashes                              |
| `tenants`                      | `Tenant` (+shadow props)     | Tenant directory. Epic-28 shadow cols: `PlanId`, `Status` (CHECK `ck_tenants_status`), `EncryptedConnectionString`, `KekVersion` (`smallint NOT NULL DEFAULT 1`), `FailureReason`, `DeleteRequestedAt`; unified-tenancy Phase-0 cols: `SchemaName`, `DatabaseId` (FK → `tenant_databases`) |
| `tenant_memberships`           | `TenantMembership`           | User ↔ tenant with per-tenant role                        |
| `user_invites`                 | `UserInvite`                 | Pending email-invite tokens                               |
| `api_keys`                     | `ApiKey`                     | API keys. Transitional CHECK `ck_api_keys_scope` allows `platform/user/installation/service/tenant`; tightens to `platform/user` when tenant-scoped keys physically move out (Phase 2+) |
| `github_installations`         | `GitHubInstallation`         | GitHub App install records                                |
| `github_installation_repos`    | `GitHubInstallationRepo`     | Per-installation repo activation                          |
| `github_webhook_deliveries`    | `GitHubWebhookDelivery`      | Replay-protection ledger for webhook deliveries           |
| `plans`                        | `Plan`                       | Billing plans (free / team / enterprise) + quotas jsonb + `PlacementPolicy` (`shared`/`dedicated`; free/team=shared, enterprise=dedicated) |
| `platform_events`              | `PlatformEvent`              | Cross-tenant audit log (admin analytics)                  |
| `platform_queued_tasks`        | `PlatformQueuedTask`         | CP-scoped background jobs (provisioning, etc.)            |
| `platform_email_outbox`        | `PlatformEmailOutboxMessage` | CP-scoped outbound mail (invites, verifications)          |
| `tenant_databases`             | `TenantDatabase`             | Unified-tenancy Phase 0: operator DB pool — one row per Postgres DB hosting tenant schemas (placement class, tier eligibility, capacity, AES-GCM-encrypted admin connection string) |

```
control-plane schema (text ER)
──────────────────────────────
users ──< tenant_memberships >── tenants ──< user_invites
  │                                 │
  │                                 ├── github_installations ──< github_installation_repos
  │                                 │
  ├── refresh_tokens                ├── (shadow) PlanId ─→ plans
  ├── password_reset_tokens         └── (shadow) DatabaseId ─→ tenant_databases
  └── api_keys  (CHECK Scope ∈ {'platform','user','installation','service','tenant'} — transitional)

github_webhook_deliveries  (orphan — delivery-id uniqueness only)
platform_events            (orphan — audit)
platform_queued_tasks      (orphan — job queue)
platform_email_outbox      (orphan — SMTP outbox)
```

### 3.2 Per-tenant DB (Epic 28, authoritative)

Code: `apps/tamma-elsa/src/Tamma.Data/TenantDbContext.cs`.
Connection string: resolved per-request by `ITenantConnectionResolver` (`apps/tamma-elsa/src/Tamma.Data/Abstractions/ITenantConnectionResolver.cs`). Each tenant lives in its own Postgres DB, so there is no `TenantId` column on any row — **the discriminator is the connection string**.
Migrations: `apps/tamma-elsa/src/Tamma.Data/Migrations/Tenant/` — a single collapsed `InitialTenant` baseline (unified-tenancy Phase 1, 2026-06-09) that applies on bare Postgres (no extensions; `gen_random_uuid()` is a pg_catalog builtin since PG13). Tenant migrations are schema-aware: when the connection string carries a `Search Path`, `EfTenantDbMigrator` applies the baseline into that `t_<hex>` schema with an in-schema `__TenantMigrationsHistory`; with no `Search Path` it applies into `public`, exactly as before.

**16 tables**:

| Table                    | Entity               | Purpose                                                                 |
| ------------------------ | -------------------- | ----------------------------------------------------------------------- |
| `agent_configs`          | `AgentConfig`        | Per-tenant role → provider-chain mapping                                |
| `prompt_overrides`       | `PromptOverride`     | 3-layer prompt store overrides (user × scope × role × action)           |
| `provider_health`        | `ProviderHealth`     | Three-state circuit-breaker per provider+model                          |
| `provider_diagnostics`   | `ProviderDiagnostic` | Per-call cost + latency + success samples                               |
| `sanitization_rules`     | `SanitizationRule`   | Per-tenant content-sanitizer rule overrides                             |
| `workflow_definitions`   | `WorkflowDefinition` | Elsa workflow JSON (synced from ElsaServer)                             |
| `workflow_instances`     | `WorkflowInstance`   | Live + completed workflow runs (variables + result jsonb)               |
| `domain_events`          | `DomainEvent`        | DCB event-sourcing stream (tagged jsonb)                                |
| `queued_tasks`           | `QueuedTask`         | Per-tenant background job queue (DbTaskQueue consumer)                  |
| `email_outbox`           | `EmailOutboxMessage` | Per-tenant outbound mail (SMTP retry ladder)                            |
| `budget_configs`         | `BudgetConfig`       | Per-account LLM budget + alert threshold                                |
| `api_keys`               | `ApiKey`             | **Scope='tenant' only** (enforced by `ck_api_keys_tenant_scope` check)  |
| `mentorship_sessions`    | `MentorshipSession`  | 28-state mentorship state machine                                       |
| `mentorship_events`      | `MentorshipEvent`    | State-transition log                                                    |
| `junior_developers`      | `JuniorDeveloper`    | Mentee profiles + skill level                                           |
| `stories`                | `Story`              | Work-item metadata consumed by workflows                                |

```
per-tenant schema (text ER)
───────────────────────────
workflow_definitions ──< workflow_instances
     (no TenantId; one DB = one tenant)

domain_events ───┐
queued_tasks ────┤   all flat, indexed by (Type, CreatedAt)
email_outbox ────┘

agent_configs
prompt_overrides
provider_health ───< provider_diagnostics
sanitization_rules
budget_configs
api_keys  (CHECK Scope='tenant')

stories ──< mentorship_sessions ──< mentorship_events
                 │                      (session_id FK)
                 └─→ junior_developers
```

### 3.3 Legacy contexts (deleted in Wave A.5)

Two DbContexts predated Epic 28 and were removed by Wave A.5; only doc comments reference them now:

- **`TammaDbContext`** — the original single-DB context holding the union of everything CP + tenant, with a legacy `TenantId` column on every row and a permissive query filter. Superseded by `ControlPlaneDbContext` + `TenantDbContext`.
- **`TammaAppDbContext`** — subclass that connected as `tamma_app` (non-superuser) so the **Epic 17 RLS** policies bit. Superseded by the per-tenant connection model.

The legacy `ConnectionStrings:TammaDb` / `:TammaAppDb` configuration keys survive in `Program.cs`'s Phase-3 lookup chain (they now feed the new contexts). The RLS approach from Epic 17 is **superseded** by the db-per-tenant model (Epic 28) — see §6.

The `Phase-2 RLS` migration (`20260419021119`) also installed a `prevent_tenant_id_change()` trigger and six BEFORE-UPDATE triggers that block any mutation of `TenantId` on established rows. Those triggers remain useful during the transition (they enforce the "personal tenant bootstrap is a one-way NULL → uuid" invariant). The standalone migration file no longer exists — unified-tenancy Phase 0 collapsed the CP chain into the `InitialControlPlane` baseline and carried these objects into it verbatim; they are dropped in unified-tenancy Phase 5.

---

## 4. Runtime architecture — request flow

End-to-end path for a dashboard API call. Services are columns; time flows downward.

```
browser (app.tamma.dev)
   │  cookie: tamma_session=<jwt>
   │  Origin: app.tamma.dev
   ▼
nginx-proxy                                 [docker/nginx-proxy.conf.template]
   │  TLS termination via Cloudflare origin cert
   │  auth_request → oauth2-proxy (for cross-subdomain)
   │  proxy_pass → tamma-api:3100
   ▼
tamma-api (Tamma.Api/Program.cs)
   ├─ JwtBearer middleware                  [reads Authorization OR tamma_session cookie]
   ├─ Rate limiter (per-IP fixed window)    [ConfigRead, ConfigWrite, ProviderIngest, ProviderExecute, GitHubWebhook, OAuthStart]
   ├─ TenantContextMiddleware               [Middleware/TenantContextMiddleware.cs]
   │     · resolves tenant from: AuthPrincipal → JWT active_tenant_id → Installation → users.tenant_id
   │     · warms per-tenant connection pool via ITenantConnectionResolver
   │     · adds Activity baggage tag tamma.tenant_id
   │     · 401 on stale/deleted/unprovisioned tenant (fail-fast)
   ├─ EnsurePersonalTenantMiddleware        [boots a personal tenant for first-time users]
   ├─ Endpoint routing                      [Program.cs maps ~170 routes]
   │
   ▼ endpoint handlers branch on storage target
   │
   ├─► ControlPlaneDbContext                [users, tenants, api_keys, …]
   │      └─ NpgsqlDataSource → ConnectionStrings:ControlPlane
   │
   ├─► TenantDbContext (via ITenantDbContextFactory)
   │      └─ NpgsqlDataSource from ITenantConnectionResolver (per-tenant pool cache)
   │           └─ reads tenants.EncryptedConnectionString → IConnectionStringDecryptor (AesGcm)
   │               └─ IKekProvider.GetKek(kekId) → TAMMA_SECRET_STORE_KEK_PRIMARY / _SECONDARY
   │
   ├─► ElsaClient                            [packages/orchestrator/src/elsa-client.ts — used by TS engine only]
   │      └─ http(s)://elsa-server:5000/api/workflow-definitions/…
   │
   ├─► IntelligenceHttpClient                [Services/KnowledgeBase/IntelligenceHttpClient.cs]
   │      └─ http://intelligence-server:4100/kb/…  (30 endpoints; 10 s timeout; empty-payload fallback on 5xx)
   │
   └─► Octokit GitHub App                    [Services/GitHub/OctokitGitHubAppClient.cs + OctokitGitHubEngineCallbackService.cs]
          └─ api.github.com (installation token per request; rate-limit headers tracked)

elsa-server (ElsaServer/Program.cs)
   ├─ Elsa 3.5.3 Workflow Engine (EF Core + PostgreSQL)
   ├─ AgentSeeder + WorkflowSeeder (IHostedService; run once at startup)
   └─ Hosts ~35 code-first workflows from Tamma.ElsaServer/Workflows/
         └─ Each workflow composes activities from Tamma.Activities/
              ├─ LlmCall/     (resolve → check-budget → check-breaker → call → record-diagnostics)
              ├─ ADL/         (select-issue → plan → branch → PR → merge)
              ├─ AgentDispatch/ (dispatch → monitor → collect via IAgentExecutor)
              ├─ ToolExecution/ (file-read, file-write, search, shell-exec, run-tests, git-ops)
              └─ Security/, Integration/, Debug/, Context/, Mentorship/, TDD/, Testing/
```

Load-bearing facts:

- **`tamma-api` and `elsa-server` are separate processes** sharing Postgres. The API does not host the Elsa runtime. This lets the API scale horizontally for request load while Elsa keeps its bookmarks and scheduling.
- **Only Option B for agent dispatch** is wired. `GitHubActionsExecutor` calls `IAgentDispatchService` / `IAgentMonitorService` / `IAgentResultCollectorService` directly — it does not programmatically invoke Elsa activities. The Elsa activities and the executor share those services, so there's one implementation of each phase.
- **Intelligence server is stateless.** The C# API proxies 30 `/api/kb/*` endpoints 1:1 to the TS sidecar. A sidecar failure returns an empty payload and logs the incident — the dashboard renders a degraded view instead of erroring.

---

## 5. Authentication flow

JWT is the primary credential. The `tamma_session` cookie is a fallback read by `JwtBearerEvents.OnMessageReceived` so cross-subdomain dashboard requests work without an `Authorization` header. API keys use a separate scheme handler.

### 5.1 JWT / password flow

```
POST /api/v1/auth/register            [AuthEndpoints.Register]
   │  Argon2id hash (Konscious.Security.Cryptography.Argon2)  [Auth/PasswordService.cs]
   ▼
users row (CP)
   │  + email-verify token → platform_email_outbox (CP)
   │
   └─ email link → POST /api/v1/auth/verify-email → EmailVerified = true

POST /api/v1/auth/login               [AuthEndpoints.Login]
   │  LoginLockoutService check (fail-open after 5 failures, 15-min window)  [Auth/LoginLockoutService.cs]
   │  PasswordService.Verify(password, hash)
   │  LoadTenantClaimsAsync(membershipRepo, userId)            [projects memberships → JwtTenantClaim[]]
   ▼
JwtService.GenerateAccessToken                                 [Auth/JwtService.cs:57]
   │  claims: { sub, tenantId, role, platformRole, email, name, authMethod,
   │            active_tenant_id, tenants: [{tenantId, role}…] }
   │  alg: HS256   signing key: Jwt:Secret   15-min expiry
   │
   ├─► 200 { accessToken, refreshToken, user, tenants }
   └─► Set-Cookie: tamma_session=<jwt>  (HttpOnly, Secure, SameSite=Lax, Domain=.tamma.dev)

POST /api/v1/auth/refresh             [AuthEndpoints.Refresh]
   │  hash(refresh) = SHA256   →   RefreshTokenRepository.GetByHashAsync
   │  rotate: old row deleted, new pair minted
   │
   └─► 200 { accessToken, refreshToken }

POST /api/v1/auth/switch-org          [AuthEndpoints.SwitchOrg — Story 28-9]
   │  validate tenant is in the user's membership list
   │  PersistActiveTenantAsync (users.Settings.activeTenantId — trigger blocks uuid→uuid on column)
   │  mint new JWT with new active_tenant_id
   │  rotate refresh token
   │
   └─► 200 { accessToken, refreshToken, activeTenantId }
```

### 5.2 GitHub OAuth flow

```
GET /api/auth/github                                        [AuthEndpoints.GitHubAuth]
   │  OAuthStateCodec.Encode(returnTo, tenantId)            [Auth/OAuthStateCodec.cs]
   │  302 → github.com/login/oauth/authorize?state=<signed>
   │
   └─ user consents
      │
GET /api/auth/github/callback?code&state                    [AuthEndpoints.GitHubCallback]
   │  RedirectUrlSanitizer.IsSameOrigin(returnTo)            [Auth/RedirectUrlSanitizer.cs]
   │  GitHubOAuthService.ExchangeCodeForTokenAsync           [Services/OAuth/GitHubOAuthService.cs]
   │  GitHubOAuthService.GetUserProfileAsync
   │  UserRepository.UpsertByGitHubIdAsync
   │  LoadTenantClaimsAsync + JwtService.GenerateAccessToken
   │  Set-Cookie: tamma_session                                [Auth/SessionCookieWriter.cs]
   │
   └─► 302 → Dashboard:Url + returnTo
```

### 5.3 API-key flow

```
Authorization: ApiKey <key>
   │
   ▼
ApiKeyAuthHandler                                           [Auth/ApiKeyAuthHandler.cs]
   │  parse prefix (first 12 chars)                         [Auth/ApiKeyPrefixParser.cs]
   │  ApiKeyRepository.GetByPrefixAsync (CP or tenant DB by Scope)
   │  ApiKeyHasher.Verify(raw, row.KeyHash)                 [Auth/ApiKeyHasher.cs]
   │  revoked? → 401
   │
   └─► ClaimsPrincipal with permissions[] from ApiKey.Permissions
```

Platform scopes (`api_keys.Scope`): the target bifurcation is `'platform'` / `'user'` in CP and `'tenant'` keys in the tenant DB (`ck_api_keys_tenant_scope` enforces the tenant side). Transitionally (unified-tenancy Phase 0), live code still writes `service`/`installation`/`tenant` scopes to CP, so the CP CHECK `ck_api_keys_scope` allows all five; it tightens to `('platform','user')` when tenant-scoped keys physically move out (Phase 2+).

### 5.4 Authorization policies (`Program.cs:562-642`)

| Policy                    | Requirement                                                  | Used by                            |
| ------------------------- | ------------------------------------------------------------ | ---------------------------------- |
| `AdminAccess`             | `admin:access` permission                                    | `/api/admin/**`                    |
| `OwnerAccess`             | `users:manage` permission                                    | User role mutations, KEK rotation, tenant provisioning |
| `SettingsView` / `Manage` | `settings:view` / `settings:manage`                          | `/api/config/**`, `/api/agents/**` |
| `WorkflowsView` / `Manage` / `Delete` | `workflows:view` / `:manage` / `:delete`            | `/api/workflows/**`                |
| `SelfOrApiKeysManage`     | `apikeys:manage` OR path-user == token-user                  | `/api/admin/users/{id}/keys`       |
| `SelfOrUsersView`         | `users:view` OR self                                         | `/api/admin/users/{id}`            |
| `AuthenticatedAny`        | any authenticated scheme                                     | `/api/auth/role-check` (nginx auth_request) |

Path-tenant routes (`/api/v1/orgs/{tenantId}/**`) additionally run `RequireTenantMembershipFilter` (`Authorization/RequireTenantMembershipFilter.cs`) to verify membership and stash role in `HttpContext.Items["TenantRole"]`.

---

## 6. Tenant isolation (Epic 28 — authoritative direction)

The current architecture is **one Postgres database per tenant, plus a shared control plane**. This supersedes the Epic 17 RLS scaffold that is still present in code for backward compatibility.

```
resolution pipeline (per request)
─────────────────────────────────
JWT / AuthPrincipal / user row
        │
        ▼
TenantContextMiddleware (Middleware/TenantContextMiddleware.cs)
        │  SetTenantId on ITenantContext
        │  await ITenantConnectionResolver.GetDataSourceAsync(tenantId)
        │       ├─ LRU pool cache (Story 28-4)
        │       ├─ CP round-trip: SELECT EncryptedConnectionString, KekVersion
        │       │                 FROM tenants WHERE Id = @tenantId
        │       └─ IConnectionStringDecryptor.Decrypt → NpgsqlDataSource
        ▼
Endpoint handler
        │  await using var ctx = await factory.CreateAsync(tenantId, ct)
        ▼
TenantDbContext bound to the tenant-specific NpgsqlDataSource
        │  no TenantId column on rows
        │  no HasQueryFilter
        │  isolation comes from the CONNECTION, not the row
```

Key files:

- **Abstractions**: `apps/tamma-elsa/src/Tamma.Data/Abstractions/ITenantConnectionResolver.cs`, `ITenantDbContextFactory.cs`, `IConnectionStringDecryptor.cs`.
- **CP shadow columns** (Doc 01 §8.1): declared on the `Tenant` entity in `TammaModelConfiguration.ConfigureControlPlaneEntities` — `PlanId`, `Status` (state machine `pending_verification` → `provisioning` → `active` → `suspended`/`failed`/`delete_requested` → `deleting` → `deleted`, CHECK-enforced by `ck_tenants_status` since unified-tenancy Phase 0), `EncryptedConnectionString` (bytea), `KekVersion` (`smallint NOT NULL DEFAULT 1`), `FailureReason`, `DeleteRequestedAt`, plus Phase-0 `SchemaName` / `DatabaseId` (FK → `tenant_databases`; NULL until Phase 3 mints them).
- **Interceptors**: `Tamma.Data/Interceptors/TenantContextInterceptor.cs` runs `SET LOCAL app.current_tenant_id = '...'` on every connection open (used on the legacy `TammaAppDbContext` path to make the Phase-2 RLS policies enforce).

### 6.1 Epic 17 RLS — superseded

The Epic 17 Phase-2 scaffold originally shipped with `Phase2RlsAndTriggers` (migration `20260419021119`). It created the `tamma_app` Postgres role, eight RLS policies against `current_setting('app.current_tenant_id')`, and the six BEFORE-UPDATE triggers. On the central-DB path those policies enforce; on the per-tenant-DB path they are unnecessary (the database IS the boundary). Unified-tenancy Phase 0 (2026-06-09) collapsed the CP migration chain into a single `InitialControlPlane` baseline and ported the surviving RLS scaffold into it: the role plus 7 RLS policies and 4 tenant-id triggers — the remaining policies/triggers died with the tables that moved out of the CP schema. Phase 0 stays behavior-neutral for the tables that remain; removal is planned for unified-tenancy Phase 5.

### 6.2 KEK rotation (Story 28-12)

`KekRotationCoordinator` (`apps/tamma-elsa/src/Tamma.Api/Services/Secrets/KekRotationCoordinator.cs`) drives platform-wide KEK rotation:

1. Mint a fresh 32-byte KEK, stage it as the `KekProvider` secondary so concurrent decrypt traffic can fall back to the old primary.
2. List every `tenants` row with `EncryptedConnectionString IS NOT NULL AND KekVersion < target`.
3. Per row: decrypt with old primary, re-encrypt under new primary, bump `KekVersion`, evict the tenant's warm pool.
4. When every row is rewrapped, promote secondary → primary, clear secondary.

Endpoints: `POST /api/admin/kek/rotate/start`, `GET /api/admin/kek/rotate/status` (owner-only). `apps/tamma-elsa/src/Tamma.Api/Endpoints/KekRotationEndpoints.cs`.

See [Epic 28 — DB-per-Tenant](Epics/Epic-28-DB-Per-Tenant.md) for the full 14-story plan and [Multi-Tenant Provisioning](Multi-Tenant-Provisioning.md) for the onboarding UX.

---

## 7. Agent dispatch (Epic 19 — complete)

`IAgentExecutor` (`apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/IAgentExecutor.cs`) is the abstraction over "actually run the agent." Two implementations ship:

| Executor                  | File                                                                                   | When used                                         |
| ------------------------- | -------------------------------------------------------------------------------------- | ------------------------------------------------- |
| `LocalExecutor`           | `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/LocalExecutor.cs`                  | CLI mode or self-hosted with agent on same host   |
| `GitHubActionsExecutor`   | `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/GitHubActionsExecutor.cs`          | SaaS — agent runs on tenant's GitHub Actions      |

Both are picked by `AgentExecutorFactory` from the precedence rules in §1.

### 7.1 LocalExecutor sequence

```
Elsa activity ExecuteAgentActivity
   │  AgentExecutorFactory.Create() → LocalExecutor (auto: no GitHub App)
   ▼
LocalExecutor.ExecuteAsync
   │  serialize request → .tamma/exec-request-<sessionId>.json
   │  spawn: node packages/cli/dist/index.js execute-agent
   │           --request .tamma/exec-request-<sessionId>.json
   │           --output  .tamma/exec-result-<sessionId>.json
   │  (via IProcessRunner — wrapped so tests can substitute a fake)
   │
   ▼   (subprocess)
packages/cli/src/commands/execute-agent.ts
   │  read request
   │  resolve provider via RoleBasedAgentResolver → AgentProviderFactory
   │  provider.executeTask(request.task)  ── @tamma/providers ──
   │  write AgentResultArtifact → output path
   │  exit 0 on success / non-zero on fail
   │
   ▼
LocalExecutor reads result file → AgentExecutionResult
   │  cleanup temp files
   └─► returns to Elsa activity
```

### 7.2 GitHubActionsExecutor sequence

```
Elsa activity ExecuteAgentActivity
   │  AgentExecutorFactory.Create() → GitHubActionsExecutor (GitHub App configured)
   ▼
GitHubActionsExecutor.ExecuteAsync
   │
   ├─► IAgentDispatchService.DispatchAsync
   │     · Octokit.CreateWorkflowDispatch(owner, repo, workflow.yml, ref, inputs)
   │     · inputs embed tamma_session_id, task, plan, agent_provider, timeout
   │     · tenant-scoped install token via InstallationRepoResolver
   │
   ├─► IAgentMonitorService.MonitorAsync
   │     mode=Auto → try WebhookSignalRegistry first (workflow_run.completed)
   │              → fall back to polling listWorkflowRunsByWorkflow
   │     · install:{id}:<session> key prefix (review finding 5)
   │     · max 60 polls / timeout
   │
   └─► IAgentResultCollectorService.CollectAsync
         · download artifacts/result-<sessionId>.json
         · 4 MB cap per artifact via LimitedStream  (review finding 6)
         · parse → AgentExecutionResult
```

Webhook resume path: `POST /api/github/webhooks` (`GitHubEndpoints.Webhooks`) deserializes `workflow_run.completed`, extracts the run-id + installation-id, calls `WebhookSignalRegistry.Signal(key)` which wakes the monitor's `TaskCompletionSource`. The registry is a singleton; multi-pod fanout awaits a Postgres LISTEN/NOTIFY bridge.

See [Agent Dispatch](Agent-Dispatch.md) for the complete story and [Epic 19](Epics/Epic-19-Agent-Dispatch.md) for acceptance criteria.

### 7.3 LLM call pipeline (Epic 12 — complete)

The `LlmCallWorkflow` is the canonical entry point for every LLM interaction. It's a Sequence-based retry loop that composes **nine activities** from `apps/tamma-elsa/src/Tamma.Activities/LlmCall/`:

```
LlmCallWorkflow (apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs)
   │
   ├─► CheckLlmConcurrencyActivity        — gate on process-wide semaphore
   │   └─ ConcurrencyWaitDelayActivity    — back off + retry when saturated
   │
   ├─► ResolveAgentConfigActivity         — maps phase → role → provider chain
   │   └─ reads AgentConfigRepository     — per-tenant override > default
   │
   ├─► ResolveLlmPromptActivity           — PromptStore 3-layer resolution
   │   └─ ResolvePromptFromRegistryActivity  — user override → system default
   │
   ├─► ResolveToolsActivity               — selects tools from the registry
   │   └─ ToolExecutorRegistry            — file-read, file-write, search-code,
   │                                        shell-execute, run-tests, git-ops
   │
   ├─► CheckBudgetActivity                — budget_configs row + spend window
   │   └─ fail-closed on over-budget
   │
   ├─► CheckCircuitBreakerActivity        — provider_health three-state breaker
   │   └─ fail-closed when OPEN
   │
   ├─► CallLlmInlineActivity              — the actual HTTP call
   │   └─ optional agentic tool loop (EnableToolLoop=true):
   │      · parse tool_calls from response
   │      · ToolCallValidator (Security/) → ActionGate → argument-schema check
   │      · ParallelToolExecutor dispatches to IToolExecutor impls
   │      · tool results fed back as a new message
   │      · loop until text-only response or maxSteps
   │      · ContextCompactor when near token limit (TokenEstimator)
   │
   └─► RecordDiagnosticsActivity          — provider_diagnostics row + budget delta
       · cost calculated via ProviderPricingService
       · success? → ProviderHealth success counter
       · failure? → breaker opens after N consecutive failures
```

Key files:

- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs` — the synchronous HTTP activity. Sanitization via `IContentSanitizer` + `IErrorRedactor` before any logging.
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/ToolExecutorRegistry.cs` — singleton registry of `IToolExecutor` impls.
- `apps/tamma-elsa/src/Tamma.Activities/Security/ActionGate.cs` + `ToolCallValidator.cs` — substring-blocklist + argument-schema validation. No regex, no ReDoS.
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/ContextCompactor.cs` — emergency context shrinkage when approaching provider token limits.
- `apps/tamma-elsa/src/Tamma.Api/Services/Providers/HttpProviderClient.cs` — shared Named HttpClient wrapper (OpenAI, Anthropic, Copilot, Gemini, OpenRouter, z.ai, local).

HTTP provider clients are registered in `Tamma.Api/Program.cs:90-168` as Named HttpClients, one per provider with its own base URL + auth header. CLI-agent providers (Claude Code, OpenCode) and the Zen MCP provider require subprocess / MCP transports and live on the TS side in `packages/providers/src/`.

---

## 8. Cross-language bridge (C# ↔ TypeScript)

Two HTTP bridges, narrow and unidirectional:

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                        C# plane                                             │
│                                                                             │
│   Tamma.Api (tamma-api:3100)                                                │
│     ├── IntelligenceHttpClient ──────────────────────┐                     │
│     │   [Services/KnowledgeBase/IntelligenceHttpClient.cs]                 │
│     │                                                 │                     │
│   Tamma.ElsaServer (elsa-server:5000)                │                     │
│     (Elsa HTTP API consumed by ElsaClient)           │                     │
│                                                       │                     │
└───────────────────────────────────────────────────────┼─────────────────────┘
                                                        │
                                          HTTP (JSON, camelCase)
                                                        │
┌───────────────────────────────────────────────────────┼─────────────────────┐
│                        TypeScript plane               │                     │
│                                                       ▼                     │
│   intelligence-server (Fastify on :4100)                                    │
│     [packages/intelligence-server/src/server.ts]                            │
│     30 routes under /kb/* matching KbEndpoints 1:1                          │
│       /kb/index/*        → @tamma/intelligence IndexManagementService       │
│       /kb/vector-db/*    → VectorDbManagementService                        │
│       /kb/rag/*          → RagManagementService                             │
│       /kb/mcp/*          → McpManagementService (@tamma/mcp-client)         │
│       /kb/context/*      → ContextTestingService                            │
│       /kb/analytics/*    → AnalyticsService                                 │
│                                                                             │
│   tamma-engine (TS worker)                                                  │
│     [apps/tamma-engine/src/index.ts]                                        │
│     └── ElsaClient ─────────────────┐                                       │
│         [packages/orchestrator/src/elsa-client.ts]                          │
│                                     │                                       │
└─────────────────────────────────────┼───────────────────────────────────────┘
                                      │
                                      ▼  HTTP back to elsa-server:5000
                                          (api-key scheme via Authorization: ApiKey <elsa-admin-key>)
```

The bridge is **stateless in both directions**. If the intelligence sidecar is down, the C# API returns an empty payload for `/kb/*` calls and logs the incident — the dashboard renders a degraded KB view rather than erroring. Timeout: 10 s (`IntelligenceServer:TimeoutSeconds`).

The TS engine talks to the Elsa workflow engine via `ElsaClient` for workflow lifecycle (start / suspend / resume / cancel / signal) with 3× exponential-backoff retries on transient failures.

---

## 9. Secret management (Epic 29 — shipping)

Platform secrets live in a Postgres-backed, envelope-encrypted cabinet.

### 9.1 Abstractions

- `ISecretStore` (`Services/Secrets/ISecretStore.cs`) — typed read/write/rotate/retire surface. Plaintext never crosses the public signature; revealed only via out-of-band rotation handler callbacks and the reveal-once UX.
- `ISecretStoreBackend` (`Services/Secrets/ISecretStoreBackend.cs`) — pluggable backend. Two impls ship:
  - `InMemorySecretStoreBackend` — test fixture.
  - `PostgresSecretStoreBackend` (`Services/Secrets/Postgres/PostgresSecretStoreBackend.cs`) — production. Stores each version as an AES-256-GCM envelope.

### 9.2 Envelope format (`Services/Secrets/Postgres/SecretEnvelope.cs`)

```
offset  bytes  field
──────  ─────  ─────────────────────────────────────────────
0       1      format_version   (currently 1)
1       1      kek_id           (which KEK slot wrapped the DEK)
2       12     wrap_nonce       (AES-GCM nonce for the DEK wrap)
14      32     wrapped_dek      (AES-256-GCM ciphertext of DEK)
46      16     wrap_tag         (AES-GCM tag for the DEK wrap)
62      12     value_nonce      (AES-GCM nonce for the value)
74      N      value_ct         (AES-256-GCM ciphertext of plaintext)
74+N    16     value_tag        (AES-GCM tag for the value)
```

Fresh DEK per row bounds blast radius; KEK rotation rewraps DEKs only (O(rows) AES-GCM ops, not O(bytes)).

### 9.3 KEK provider

`EnvKekProvider` (`Services/Secrets/Postgres/EnvKekProvider.cs`) reads two env vars:

- `TAMMA_SECRET_STORE_KEK_PRIMARY` — required. Format: `kekId:base64(32-byte-key)`. Used for writes.
- `TAMMA_SECRET_STORE_KEK_SECONDARY` — optional. Same format. Decrypt-only during rotation overlap.

Validation at startup: exact 32-byte AES-256 decode, slot id ∈ [0, 255], slots unique. Fails fast if misconfigured. OpenBao-backed provider is deferred to Story 28-13 until a trigger fires (see `MEMORY.md`).

### 9.4 Rotation

`KekRotationCoordinator` (`Services/Secrets/KekRotationCoordinator.cs`) — singleton coordinator. See §6.2. Admin endpoints at `/api/admin/kek/rotate/{start,status}` (owner-only).

See [Secret Management](Secret-Management.md) for the full story and [Epic 29](Epics/Epic-29-Secret-Management.md) for the roadmap.

---

## 10. External integrations

| Integration        | Entry point                                                                                    | Purpose                                                         |
| ------------------ | ---------------------------------------------------------------------------------------------- | --------------------------------------------------------------- |
| GitHub App         | `Services/GitHub/OctokitGitHubAppClient.cs`, `OctokitGitHubEngineCallbackService.cs`, `OctokitGitHubActionsClient.cs` | PR / issue / workflow / artifact + installation tokens (via Octokit 14) |
| GitHub OAuth       | `Services/OAuth/GitHubOAuthService.cs`                                                         | User sign-in                                                    |
| GitHub Webhooks    | `Endpoints/GitHubEndpoints.Webhooks`                                                           | HMAC-SHA256 verification + replay-protection in `github_webhook_deliveries` |
| Cranl API          | `Services/Provisioning/Cranl/CranlApiClient.cs` + `CranlTenantProvisioner.cs`                   | Per-tenant Postgres + Elsa app provisioning (current shipping backend) |
| Slack              | `Services/SlackIntegrationService.cs`                                                          | Mentorship notifications                                        |
| Jira               | `Services/JiraIntegrationService.cs`                                                           | Issue mirror (legacy integration)                               |
| Email (Resend)     | `Services/Email/ResendEmailService.cs`                                                         | Transactional email (invites, verifications)                    |
| Email (SMTP/MailKit)| `Services/Email/SmtpEmailService.cs` + `MailKitSmtpTransport.cs`                              | Fallback transactional email                                    |
| Cloudflare Workers | `apps/wiki-site/wrangler.jsonc`, `apps/marketing-site/wrangler.toml`                            | Hosts wiki.tamma.dev + tamma.dev                                |
| Hetzner Cloud      | _not yet implemented_ — Epic 30-4                                                              | Planned dedicated-VPS-per-tenant backend                        |

Epic 30 introduces `ITenantInfrastructureProvider` v2 with four backends (Cranl active today, Hetzner / Cloudflare / BYO planned). Each declares a capability matrix (`DatabaseOnly` / `DedicatedCompute` / `Managed`); the onboarding UI filters valid (backend, topology) combos.

---

## 11. Observability

| Surface          | Tech                                                   | Where wired                                                               |
| ---------------- | ------------------------------------------------------ | ------------------------------------------------------------------------- |
| Structured logs  | Serilog (+ Console + File + OpenSearch sinks)          | `Tamma.Api/Program.cs:29-59`, `Tamma.ElsaServer/Program.cs:19-48`         |
| Log aggregation  | OpenSearch 2.19.0 + OpenSearch Dashboards 2.19.0       | `docker/docker-compose.yml` profile=observability + `docker/opensearch/` setup |
| OpenTelemetry    | `Activity` baggage tag `tamma.tenant_id`               | `Middleware/TenantContextMiddleware.cs`                                   |
| Health probes    | ASP.NET `/health`, `/health/live`, `/health/ready`     | `Tamma.Api/Program.cs:718-726`                                            |
| Rate-limit state | In-process (default) or Redis (when `ConnectionStrings:Redis` set) | `Program.cs:257-269`, `Services/RateLimit/`                               |
| Pino logs (TS)   | `@tamma/observability` + Pino                          | All TS packages import from `packages/observability/src/`                 |
| Request logging  | `UseSerilogRequestLogging()`                           | `Program.cs:699`                                                          |

Index prefixes per service: `tamma-api`, `tamma-api-dotnet`, `tamma-elsa`, `tamma-ts`. Buffer file: `./logs/opensearch-buffer` (50 MB cap). Self-log emits to `Console.Error` on sink failure.

Engine lifecycle SSE (`Services/Engine/Lifecycle/`) fans workflow / task-queue / engine-registry events to dashboard `EventSource` clients at `/api/engine/events/state` and `/api/engine/events/logs`.

---

## 12. Technology stack

### 12.1 C# plane (`.NET 8`)

| Component                 | Version  | Source                                                |
| ------------------------- | -------- | ----------------------------------------------------- |
| .NET                      | 8.0      | `Tamma.Api.csproj`, `Tamma.ElsaServer.csproj`         |
| Elsa Workflows            | 3.5.3    | `Tamma.ElsaServer.csproj` (8 Elsa packages)           |
| Entity Framework Core     | 8.0.0    | `Tamma.Data.csproj`                                   |
| Npgsql.EntityFrameworkCore| 8.0.0    | `Tamma.Data.csproj`                                   |
| JwtBearer                 | 8.0.15   | `Tamma.Api.csproj`                                    |
| System.IdentityModel.Tokens.Jwt | 8.3.0 | `Tamma.Api.csproj`                                 |
| Octokit                   | 14.0.0   | `Tamma.Api.csproj`                                    |
| Sodium.Core               | 1.4.0    | `Tamma.Api.csproj` (libsodium sealed-box)             |
| Konscious Argon2          | 1.3.1    | `Tamma.Api.csproj` (password hashing)                 |
| BouncyCastle.Cryptography | 2.6.2    | `Tamma.Api.csproj`                                    |
| MailKit                   | 4.16.0   | `Tamma.Api.csproj`                                    |
| StackExchange.Redis       | 2.12.14  | `Tamma.Api.csproj`                                    |
| Serilog.AspNetCore        | 8.0.0    | `Tamma.Api.csproj`, `Tamma.ElsaServer.csproj`         |
| Serilog.Sinks.OpenSearch  | 1.3.0    | same                                                  |
| Swashbuckle.AspNetCore    | 6.5.0    | `Tamma.Api.csproj`                                    |

### 12.2 TypeScript plane (Node 22 LTS)

| Component                 | Version   | Source                                                |
| ------------------------- | --------- | ----------------------------------------------------- |
| Node.js                   | ≥22       | `package.json` engines                                |
| TypeScript                | ~5.7.2    | Root `package.json`                                   |
| pnpm                      | 9.15.0    | `packageManager`                                      |
| Vitest                    | 3.x       | Root `vitest.config.ts`                               |
| Fastify                   | 5.x       | `packages/intelligence-server/package.json`           |
| React                     | 18.3.1    | `packages/cli`, `packages/dashboard`                  |
| Ink                       | 5.0.1     | `packages/cli/package.json`                           |
| Vite                      | 6.0.7     | `packages/dashboard`                                  |
| Tailwind CSS              | 4.2.1     | `packages/dashboard`                                  |
| Zustand                   | 5.0.11    | `packages/dashboard` (state)                          |
| react-router              | 7.x       | `apps/wiki-site`, `packages/dashboard`                |
| react-markdown            | (latest)  | `apps/wiki-site/src/components/MarkdownPage.tsx`      |
| remark-gfm, rehype-raw    | latest    | `apps/wiki-site/src/components/MarkdownPage.tsx`      |

### 12.3 Infrastructure

| Component                 | Version           | Source                              |
| ------------------------- | ----------------- | ----------------------------------- |
| PostgreSQL                | 16-alpine         | `docker/docker-compose.yml`         |
| RabbitMQ                  | 3.13-management   | same                                |
| ChromaDB                  | 0.6.3             | same                                |
| OpenSearch                | 2.19.0            | same (profile=observability)        |
| nginx                     | 1.27-alpine       | same                                |
| oauth2-proxy              | v7.7.1            | same                                |

Note: `CLAUDE.md` documents "PostgreSQL 17" as the target. The shipped compose file pins `postgres:16-alpine` as of 2026-04-22 (drift called out in §13).

---

## 13. Current vs future state

### 13.1 Wave A.5 cleanup (contexts deleted — residuals flagged)

Wave A.5 **landed**: `TammaDbContext`, `TammaAppDbContext`, and the mentorship single-DB path are gone — every endpoint now runs on the `ControlPlaneDbContext` + `TenantDbContext` split (`MentorshipSessionRepository` uses `ITenantDbContextFactory`). Remaining residuals **in the codebase but scheduled for deletion**:

1. **Phase-2 RLS artefacts** — the `tamma_app` role, 7 RLS policies, 4 tenant-id triggers (originally 8/6 in migration `20260419021119`; since unified-tenancy Phase 0 the survivors live in the collapsed `InitialControlPlane` baseline — the rest died with tables that left the CP schema). The triggers have belt-and-suspenders value during the transition but become redundant under db-per-tenant; removal is unified-tenancy Phase 5.
2. **Cranl provisioning columns** (`cranl_project_id`, `cranl_database_id`, etc. on `tenants` — originally migration `20260419204924`, now part of the collapsed `InitialControlPlane` baseline). Epic 30's `ITenantInfrastructureProvider` v2 supersedes the inline Cranl columns; they go when the legacy provisioning path is removed.

### 13.2 Coming in Epic 30

Cloudflare / Hetzner / BYO backends for `ITenantInfrastructureProvider`. The interface shape (Epic 30 preview):

```csharp
public interface ITenantInfrastructureProvider
{
    string ProviderKey { get; }                              // "cranl" | "hetzner" | "cloudflare" | "byo"
    ProvisioningTopologyCapabilities Capabilities { get; }
    Task<ProvisionResult> ProvisionAsync(ProvisionRequest req, CancellationToken ct);
    Task<HealthStatus> ProbeAsync(Guid tenantId, CancellationToken ct);
    Task DeprovisionAsync(Guid tenantId, CancellationToken ct);
}

public enum ProvisioningTopology { DatabaseOnly, DedicatedCompute, Managed }
```

See [Epic 30 — Pluggable Provisioning](Epics/Epic-30-Pluggable-Provisioning.md).

---

## 14. Deployment topology

### 14.1 Production VPS (current)

```
 Hetzner CPX42 (204.168.131.39)  — 16 GB RAM, amd64, Docker Compose
 ─────────────────────────────────────────────────────────────────
 nginx-proxy       :443/:80    ← TLS terminates Cloudflare origin cert
 oauth2-proxy      :4180       ← auth_request for cross-subdomain
 tamma-api         :3100       ← .NET 8 REST API
 tamma-dashboard   :8080       ← React SPA behind nginx
 elsa-server       :5000       ← Elsa workflow engine
 elsa-studio       :8081       ← Blazor WASM workflow designer
 intelligence-server :4100     ← TS Fastify sidecar
 tamma-engine      (no port)   ← TS autonomous worker (pulls from DB)
 postgres          :5432       ← internal
 rabbitmq          :5672/:15672← internal
 chromadb          :8000       ← internal
 opensearch        :9200       ← internal (profile=observability)
 opensearch-dashboards :5601   ← internal (profile=observability)
```

DNS (Cloudflare, Full SSL with origin cert):

- `app.tamma.dev` → dashboard (via nginx-proxy)
- `api.tamma.dev` → `tamma-api`
- `elsa.tamma.dev` → `elsa-server` + `elsa-studio`
- `wiki.tamma.dev` → Cloudflare Worker serving `apps/wiki-site` static bundle
- `tamma.dev` → Cloudflare Worker serving `apps/marketing-site`

Compose layering: `docker/docker-compose.yml` (base) + `docker-compose.override.yml` (dev auto-load) or `-f docker-compose.prod.yml` (production tuning). Observability is opt-in: `docker compose --profile observability up -d`.

### 14.2 CI/CD (`.github/workflows/`)

| Workflow                       | Purpose                                                     |
| ------------------------------ | ----------------------------------------------------------- |
| `ci.yml`                       | TS build + lint + test on PRs                               |
| `codeql.yml`                   | CodeQL security scan                                        |
| `deploy.yml`                   | VPS deploy via SSH                                          |
| `docker-publish.yml`           | Build + push images to GHCR                                 |
| `docker-smoke-test.yml`        | Full-stack smoke test on PR                                 |
| `e2e-deploy.yml`               | E2E suite against deployed stack                            |
| `release.yml`                  | GitHub release cutting                                      |
| `tamma-worker.yml`             | GitHub Actions worker template (agent runs)                 |
| `ai-provider-benchmark.yml`    | Nightly provider benchmark                                  |
| `claude.yml`                   | Claude-assisted CI                                          |
| `create-story.yml`             | Story template generator                                    |
| `wiki-deploy.yml`              | Deploy `apps/wiki-site` to Cloudflare                       |

Edge services deploy via `wrangler publish` to Cloudflare Workers; the main stack deploys via SSH to the VPS (see [Deployment](Deployment.md) runbook).

---

## Role/Action Taxonomy & Resolution

Prompts and conventions resolve by exact `(role, action)` lookup against one
shared code-defined taxonomy (no keyword matching). See
[Role/Action Taxonomy](Role-Action-Taxonomy.md).

---

## For more detail

- [Deployment runbook](Deployment.md) — VPS bring-up, Cranl activation, Phase-3 RLS runbook
- [Multi-Tenant Provisioning](Multi-Tenant-Provisioning.md) — Epic 30 backend selection
- [Agent Dispatch](Agent-Dispatch.md) — Epic 19 implementation detail
- [Secret Management](Secret-Management.md) — Epic 29 cabinet + rotation
- [Port Audit](Port-Audit.md) — C# port-gap findings from the 2026-04 audit
- [Epic 28 — DB-per-Tenant](Epics/Epic-28-DB-Per-Tenant.md)
- [Epic 29 — Secret Management](Epics/Epic-29-Secret-Management.md)
- [Epic 30 — Pluggable Provisioning](Epics/Epic-30-Pluggable-Provisioning.md)
- [All Epics](Epics.md)
- [Docs source](https://github.com/meywd/tamma/blob/main/docs/architecture.md)
