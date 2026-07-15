# API Reference

_Last updated: 2026-07-15._

Reference for Tamma's REST surface: the C# ASP.NET minimal APIs in `Tamma.Api` (plus the ELSA engine's resume seams). This page is a **companion** to the machine-generated OpenAPI — it documents the route map, the auth/RBAC policies, the SSE streams, the webhook signature schemes, the DCB event catalog, and how the two operating modes differ. For request/response schemas of an individual endpoint, use Swagger (below).

Related: [Usage & Configuration](Usage-and-Configuration) · [Installation & Setup](Installation) · [Architecture](Architecture) · [Security](Security).

## Reaching the OpenAPI / Swagger UI

The API wires Swashbuckle (`AddEndpointsApiExplorer` + `AddSwaggerGen`, doc `v1`). The UI and JSON are mounted **only in the Development environment**:

```
GET /swagger                       # Swagger UI  (Development only)
GET /swagger/v1/swagger.json       # raw OpenAPI document
```

In Production the Swagger middleware is not mounted, so this markdown page is the reference for the deployed API. Most endpoints are currently mapped without `WithTags` / `.Produces` / `WithOpenApi` metadata, so the generated doc is a bare route/verb listing — the tables below add the grouping, auth, and semantics the raw doc lacks.

## Conventions

- **Base URL:** `https://api.tamma.dev` in production; `http://localhost:3100` in local dev.
- **Versioning is mixed.** Newer surfaces are under `/api/v1/…` (auth, orgs/tenants, secrets, billing, the engine-mediation `llm`/`git`/`ci`/`jira`/`agent-dispatch`/`notifications` routes, admin alerts/audit). Many established surfaces are unversioned `/api/…` (the large admin tenant surface, pricing, agents-v2, prompts, conventions, config, providers, engine, workflows, github, webhooks, dashboard, kb). Health checks are `/health*` (no prefix). ELSA resume seams are `/elsa/api/adl/*`.
- **Auth schemes:** JWT `Bearer` (user sessions) and `ApiKey` (service principals). `AuthenticatedAny` also accepts the oauth2-proxy cookie (nginx `auth_request`).

## Route groups → default policy

Each minimal-API group applies a default authorization policy; individual endpoints can override it. Prefix and group policy:

| Prefix | Group policy | Area |
|--------|--------------|------|
| `/api/v1/auth` | (none / anon per-route) | Auth |
| `/api/admin` | `AdminAccess` | Platform admin |
| `/api/v1/admin` | `AdminAccess` | Alerts / audit / billing admin |
| `/api/pricing` | `MemberAccess` | Pricing / plans |
| `/api/v1/orgs` | `MemberAccess` | Orgs / tenants |
| `/api/v1/agents` | `SettingsView` | Agent config + BYOK |
| `/api/agents` | `MemberAccess` | Agent registry (v2) |
| `/api/prompts` | `SettingsView` | Prompt store |
| `/api/conventions` | `AuthenticatedAny` | Convention store |
| `/api/admin/conventions` | `PlatformOwnerAccess` | Convention system defaults |
| `/api/config` | `SettingsView` | Settings |
| `/api/providers` | `SettingsView` | Provider health / diagnostics |
| `/api/engine` | `WorkflowsView` | Engine / history / DCB |
| `/api/workflows` | `WorkflowsView` | Workflow CRUD |
| `/api/adl` | `WorkflowsManage` | ADL approval resumes |
| `/api/github` | (none) | GitHub webhooks + OAuth |
| `/api/dashboard` | `DashboardView` | Dashboard |
| `/api/kb` | `SettingsView` | Knowledge base |

## Authorization policies (RBAC)

Named policies from `Program.cs` `AddAuthorization`. The default policy is "authenticated user" over the `Bearer` + `ApiKey` schemes.

| Policy | Requires |
|--------|----------|
| `AdminAccess` | permission `admin:access` |
| `PlatformOwnerAccess` | platform permission `platform_admin` (JWT `platformRole` from `users.platform_role`) — gates all platform-scoped admin |
| `OwnerAccess` | permission `users:manage` (tenant owner; largely superseded) |
| `MemberAccess` | any authenticated principal |
| `SettingsView` / `SettingsManage` | `settings:view` / `settings:manage` |
| `PromptManage` | `prompts:manage` (tenant_owner or tenant_admin) |
| `ConventionManage` | `conventions:manage` (tenant_owner or tenant_admin) |
| `PlatformsManage` | `platforms:manage` (admin + owner) |
| `AgentManage` | `agents:manage` (admin + owner) |
| `WorkflowsView` / `WorkflowsManage` / `WorkflowsDelete` | `workflows:view` / `:manage` (admin+owner) / `:delete` (owner) |
| `DashboardView` | `dashboard:view` |
| `EngineServiceOnly` | authenticated **service principal** (typed `ServiceAuthPrincipal` from a service-scope ApiKey) — user JWTs are rejected 403 |
| `SelfOrApiKeysManage` / `SelfOrUsersView` | the permission **or** the caller acting on their own record |
| `AuthenticatedAny` | any authenticated principal (cookie or bearer); nginx `auth_request` gate |

Extra layers: tenant-path routes (`/api/v1/orgs/{tenantId}/…`) add a `RequireTenantMembershipFilter` (proves membership of the route tenant; write/role gating is done in-handler on `HttpContext.Items["TenantRole"]`). Named rate-limit policies also apply per route: `ConfigRead`, `ConfigWrite`, `ProviderIngest`, `ProviderExecute`, `SecretReveal`, `OAuthStart`, `GitHubWebhook`. In Development with no JWT secret configured, a permissive anonymous default policy is installed.

### Single-user vs SaaS endpoint differences

SaaS-only routes are conditionally mapped when `TammaMode == SaaS`; in single-user they are unmapped (→ 404, backed by `NullBillingProvider`):

- `POST /api/v1/billing/stripe/webhook`
- `GET /api/v1/admin/billing/webhook-events`, `POST …/{id}/replay`
- the `/api/v1/orgs/{tenantId}/billing/subscription/*` group

Behavioural difference: the mutation policies `PromptManage` / `ConventionManage` / `AgentManage` / `WorkflowsManage` map to `tenant_owner`/`tenant_admin` and **403 member-role callers in SaaS**; in single-user the sole user is auto-owner of their personal tenant, so the same routes pass.

## Endpoint catalog

Representative routes per area (verb + path + auth). This is grounded against `Program.cs` and the `Endpoints/**` handlers; use Swagger for the exhaustive, machine-generated list and schemas.

### Health

| Verb | Path | Auth |
|------|------|------|
| GET | `/api/health` | anon (container/deploy probe) |
| GET | `/health`, `/health/live`, `/health/ready` | anon |
| GET | `/api/admin/health` | AdminAccess |

### Auth (`/api/v1/auth`, `/api/auth`)

| Verb | Path | Auth |
|------|------|------|
| POST | `/api/v1/auth/register` | anon |
| POST | `/api/v1/auth/verify-email` | anon |
| POST | `/api/v1/auth/resend-verification` | anon |
| POST | `/api/v1/auth/login` | anon |
| POST | `/api/v1/auth/refresh` | anon |
| POST | `/api/v1/auth/password-reset/request` | anon |
| POST | `/api/v1/auth/password-reset/confirm` | anon |
| POST | `/api/v1/auth/switch-org` | MemberAccess |
| POST | `/api/auth/logout` | MemberAccess |
| GET | `/api/auth/me` | AuthenticatedAny |
| GET | `/api/auth/role-check` | AuthenticatedAny (nginx `auth_request`) |
| POST | `/api/auth/impersonate/end` | AuthenticatedAny |

Browser GitHub sign-in is handled by `oauth2-proxy` (`/oauth2/start`), not by `Tamma.Api`.

### Platform admin (`/api/admin`, group `AdminAccess`)

A large surface, mostly `PlatformOwnerAccess`. Highlights:

- **Users / invites / keys:** `GET /users`, `GET /users/{id}` (SelfOrUsersView), `PUT /users/{id}/role`, `DELETE /users/{id}`, `POST /users/invite`, `GET /users/invites`, `POST|GET|DELETE /users/{id}/keys` (SelfOrApiKeysManage).
- **Service keys:** `POST|GET /service-keys`, `POST /service-keys/{id}/rotate`, `DELETE /service-keys/{id}` (SettingsManage).
- **Tenants / provisioning:** `POST /tenants/{id}/provision`, `GET /tenants/{id}/provisioning`, `POST /tenants/{id}/deprovision`, `GET /tenants`, `GET /tenants/{id}/detail`, `…/entitlements`, `POST /tenants/{id}/actions/{retry|delete|force-delete|cancel-delete}`, `POST /tenants/{id}/cleanup`, `PATCH|PUT /tenants/{id}/plan`, `POST /tenants/{id}/move`.
- **DB pool:** `GET /pools/stats`, `GET /pools/tenants`, `POST /pools/{id}/evict`, `GET|POST|PATCH|DELETE /tenant-databases[/{id}]`.
- **Pricing / plans / providers:** `GET|POST /providers`, `PATCH /providers/{key}`, `GET|PUT /providers/{key}/prices`, `GET|PUT /pricing/margins`, `GET|POST|PUT /pricing/plans[...]`, `GET /pricing/overview`, `GET /plans[...]`.
- **KEK rotation:** `POST /kek/rotate/start`, `GET /kek/rotate/status`, `POST /kek/rotate/retry`.
- **Secrets:** `POST /secrets`, `POST /secrets/{id}/rotate`, `GET /secrets[/{id}[/versions]]`, `POST /secrets/{id}/retire-version/{n}`.
- **Analytics / impersonation:** `GET /analytics/{summary|tenants|events}`, `POST /tenants/{id}/impersonate`, `GET /impersonations/active`, `GET /diagnostics/platform-queues`.
- **Infrastructure monitor (Story 23-8):** `GET /monitoring/infrastructure` (PlatformOwnerAccess) — one read-only live snapshot of the API process + host composed with the admin health probes: `runtime` (framework/OS/arch, CPU %, uptime), `process` (threads, GC collections), `memory` (working set / managed heap vs. the cgroup or GC limit, `memoryLimitSource: "cgroup"|"gc"`), `disks[]`, `dependencies[]` (Postgres, RabbitMQ, ELSA, ChromaDB, OpenSearch — `status: healthy|unhealthy|unknown` + allowlist-sanitized `detail`, never a raw exception/host/secret), `collectedAt`. System-level, not tenant-scoped; non-platform-admins get 403.
- **SSE:** `GET /tenants/{tenantId}/events/stream` (see [SSE](#sse-streams)).

### Admin alerts / audit / billing (`/api/v1/admin`, mostly `PlatformOwnerAccess`)

`GET /alerts[...]`, `POST /alerts/{id}/{acknowledge|resolve}`, `POST /alerts/_test`, `GET|POST|PATCH|DELETE /alert-channels[...]`, `GET|POST|PATCH|DELETE /alert-rules[...]`, `POST /alert-rules/{id}/_test`, `GET /audit`, `GET /audit/verify`, `POST /audit/checkpoint`, and (SaaS-only) `GET /billing/webhook-events`, `POST /billing/webhook-events/{id}/replay`.

### Pricing & billing

| Verb | Path | Auth |
|------|------|------|
| GET | `/api/pricing/{estimate\|entitlements\|plans\|plans/{slug}}` | MemberAccess |
| POST | `/api/pricing/subscribe` | SettingsManage |
| GET/POST | `/api/v1/orgs/{tenantId}/billing/subscription[/…]` | MemberAccess + membership (SaaS-only) |
| POST | `/api/v1/billing/stripe/webhook` | anon + Stripe signature (SaaS-only) |

### Orgs / tenants (`/api/v1/orgs`, group `MemberAccess`)

Org lifecycle, members, invites, audit, secrets, API keys, dashboard, analytics, alerts, and agent trails — all tenant-scoped with `RequireTenantMembershipFilter`. Examples: `POST /`, `GET|PUT /{tenantId}[/settings]`, `POST /{tenantId}/reprovision`, `GET|PUT|DELETE /{tenantId}/members[...]`, `POST|GET|DELETE /{tenantId}/invites[...]`, `POST /{tenantId}/transfer-ownership`, `DELETE /{tenantId}`, `…/secrets[...]` (incl. `/rotate`, `/rotate-workflow`, `/retire-version/{n}`), `…/api-keys[...]`, `…/dashboard/{summary|runs|stats}`, `…/analytics/{usage|usage/breakdown|cost}`, `…/alerts[...]`, `…/alert-channels[...]`, `…/agents/{agentId}/{runs|trail}`. Standalone: `GET /api/v1/tenants`, `GET /api/v1/tenants/{id}/status`, secret reveal `GET /api/v1/secrets/reveal/{token}` (bearer token, rate `SecretReveal`), onboarding `GET /api/v1/onboarding/{status|install-github}`, `GET|POST /api/onboarding/{platforms|install|installations}`.

Onboarding write slices (Story 18-4, tenant strictly from `ITenantContext` — null/empty tenant fails closed `404 no_active_tenant`, no route/body tenant → no IDOR):

| Verb | Path | Auth | Semantics |
|------|------|------|-----------|
| PATCH | `/api/v1/onboarding/repos/{installationId}/{repoId}` | PlatformsManage | Flip a connected repo's `IsActive` flag (body `{ "active": bool }`). Foreign/unknown installation → `404 installation_not_found`; unknown repo → `404 repo_not_found`; missing body → 400. Idempotent (a no-op flip emits nothing); a real flip emits `REPO.ACTIVATED.SUCCESS` / `REPO.DEACTIVATED.SUCCESS`. Returns `{installationId, repoId, repoFullName, active, changed}`. |
| POST | `/api/v1/onboarding/complete` | MemberAccess | Record the onboarding-complete milestone by emitting `ONBOARDING.COMPLETED.SUCCESS` (the append-only event IS the record — no persisted flag). Idempotent: a prior completion returns `{completed:true, alreadyCompleted:true, completedAt}` without a duplicate append; first call returns installation + active-repo counts. |

### Agents & providers

- **Agent config** (`/api/v1/agents`, SettingsView): `GET|PUT /config`, `POST /config/validate`, `GET /{role}/resolve`, `POST /resolve-for-phase`, `GET|POST /`, `GET /{id}[/versions[/{n}]]`, `POST /{id}/versions`, `POST /{id}/archive`. **BYOK:** `GET /providers`, `POST /providers/{provider}/credential`, `…/credential/rotate`, `DELETE …/credential`.
- **Agent registry v2** (`/api/agents`, MemberAccess): `GET /`, `GET /resolve`, `GET /role-selections`, `GET /{id}[/versions[/{n}]]`, `POST /`, `POST /{id}/{versions|archive|rollback}`, `PUT /role-selections/{role}`, `GET|PUT|DELETE /enablement` (`AgentManage` on writes).
- **Integration BYOK** (`/api/v1/integrations`): `POST|DELETE /jira/credential`, `POST|DELETE /email/credential` (PlatformsManage).
- **Provider health/diagnostics** (`/api/providers`, SettingsView): `GET /health[/providers[/{key}]]`, `POST /health/providers/{key}/{failure|success|reset}`, `POST /chain/resolve`, `GET /diagnostics[/query|/report|/deep|/budget/{acct}]`, `POST /diagnostics[/batch]` (rate `ProviderIngest`), `POST /providers/create`, `POST /providers/{handle}/execute` (rate `ProviderExecute`), `DELETE /providers/{handle}`, `GET /providers/sessions`.
  - `GET /diagnostics/deep` (Story 23-6) — per-provider latency percentiles (p50/p95/p99), error-class breakdown (by `ErrorCode`), token/cost analytics and per-model usage over `?from&to&providerKey` (defaults: last 24h). Cost figures are the tenant's own recorded spend — never a platform margin (the cross-tenant view is the separate `PlatformOwnerAccess` admin analytics surface).
  - **Diagnostics read hardening (Story 23-6 review):** all four tenant-scoped reads (`/diagnostics`, `/diagnostics/query`, `/diagnostics/report`, `/diagnostics/deep`) fail closed on a null ambient tenant with `404 {"error":"no_active_tenant"}` before any repository call — a stale JWT can no longer trigger a cross-tenant fan-out. `from`/`to` are bound as raw ISO-8601 strings and parsed `AssumeUniversal | AdjustToUniversal` (offset-less ⇒ pinned UTC, explicit offset ⇒ converted), unparseable → `400 invalid_from` / `invalid_to`. On `/report` and `/deep`, a `[from,to)` window wider than **90 days** → `400 {"error":"window_too_large","maxDays":90}`.

### Prompts, conventions, config

See [Usage & Configuration](Usage-and-Configuration#prompt-override-store) for the prompt store, and [convention templates / store](Usage-and-Configuration#convention-templates). Settings: `GET|PUT /api/config/{agents|security|providers}`, `POST /api/config/sanitize`, `GET|PUT /api/config/sanitize/rules`, `GET|PUT /api/config/prompts[/{role}]`.

### Engine / history / DCB (`/api/engine`, group `WorkflowsView`)

`POST /command` (WorkflowsManage), `GET /{state|stats|plan|history|issues|security-alerts|cycle-results|agent-available}`, `GET /events/state` + `GET /events/logs` (SSE), `POST /store-context`, `GET /context/{issueNumber}`, `POST /query-context`, `GET /repo-config`, `POST /{issue-comment|issue-labels|create-issue|trigger-ci|execute-task|cycle-result}`, `DELETE /issue-labels/{repo}/{issueNumber}/{label}`. DCB append: `POST /events` and `POST /platform-events` (`EngineServiceOnly`).

**DCB event query (Story 4-7)** — `GET /api/engine/events/query` (WorkflowsView, tenant-scoped). Cursor-paginated read over `domain_events`, newest-first. Query params: `type` (+ `prefix=true` for prefix match), `correlationId`, `actor`, `from`/`to` (ISO-8601; `from > to` → 400), `cursor` (last `sequenceNumber` of the prior page; `< 1` → 400), `limit` (default 50, clamp 1–200), `includeTotal`. Response: `{events[{id,type,tags,data,createdAt,issueNumber,sequenceNumber}], total, limit, nextCursor, hasMore}`. No resolved tenant → an empty page (never cross-tenant rows). Backs the dashboard's Event Store Explorer and Agent Monitor pages.

**Black-box replay (Story 4-8)** — `GET /api/engine/runs/{correlationId}/replay?upTo={seq|timestamp}&from={seq}` (WorkflowsView, tenant-scoped). Point-in-time state reconstruction: a pure, deterministic left-fold over the run's ordered DCB event slice — nothing is re-executed or written (time-travel for debugging, not re-run). `upTo` = as-of point, either a positive `sequenceNumber` or an ISO-8601 timestamp (parsed `AssumeUniversal | AdjustToUniversal` — offset-less ⇒ UTC, explicit offset ⇒ converted); omitted = whole run. `from` = optional positive `sequenceNumber`; adds a `delta` diff of the two prefix folds. Fail-loud 400s: non-positive/unparseable `upTo` or `from`, and `from` resolving **after** `upTo` (`400` instead of a silent empty diff). Null/empty tenant or a run this tenant doesn't own → `404 run not found` (no IDOR). `upTo` before the run began → `200` with `eventsReplayed = 0`. The event fetch is bounded to **10,000** events; an over-cap run folds the capped oldest-first slice and sets `truncated: true`. The result categorizes events into AI decisions, code changes, approval points, and errors, and derives the step reached + terminal status.

### Workflows & ADL

`POST|GET /api/workflows/definitions`, `POST /instances`, `PUT /instances/{id}`, `GET /instances`, `POST /instances/{id}/cancel`, `DELETE /instances/{id}` (WorkflowsDelete), `GET /instances/{id}/events`. ADL resumes: `POST /api/adl/{merge-approval|deploy-approval|blocker}/resume` (WorkflowsManage). ELSA engine internal seams: `POST /elsa/api/adl/{merge-approval|deploy-approval|blocker}/resume` (ELSA admin API-key auth).

### Engine-mediation callbacks (Epic 38 — `EngineServiceOnly`)

The engine holds no external credentials; all outbound integration calls are mediated by `Tamma.Api`. These require a service principal:

`POST /api/v1/llm/call` · `GET /api/v1/llm/runs/{correlationId}/stream` (SSE, AuthenticatedAny) · `POST|GET|PUT|PATCH|DELETE /api/v1/git/{owner}/{repo}/…` (branches, pull-requests, issues, commits, file-changes, releases) · `POST|GET /api/v1/ci/{owner}/{repo}/{test-runs|build-status}` · `GET|PATCH /api/v1/jira/tickets/{id}` · `POST|GET /api/v1/agent-dispatch/{owner}/{repo}/runs[…]` · `POST /api/v1/notifications/{slack|email}` · `POST /api/v1/workflows/{id}/{status|result}` · `POST /api/v1/installations/{id}/rotate-key`.

### User dashboard: repos & workflow runs (Stories 21-4 / 23-5)

Tenant-facing read surface behind the SPA's `/repos` and `/runs` pages. All four are `MemberAccess`; the tenant is resolved strictly from the ambient `ITenantContext` (never a route/body value → no IDOR), and a null/empty tenant fails closed with `404 {"error":"no_active_tenant"}` before any repository call. Per-run cost is the tenant's **own** recorded spend summed from the run's `costUsd` event fields — no platform margin/markup is ever read.

| Verb | Path | Returns |
|------|------|---------|
| GET | `/api/v1/repos` | The tenant's connected platform installations, newest-first: `{tenantId, repos[{id,name,platform,baseUrl,externalId,status,isPrimary,connectedAt,updatedAt}], count}` |
| GET | `/api/v1/runs?limit&page` | Workflow runs, newest-first, offset-paginated (`limit` clamp 1–100, default 25): `{tenantId, total, page, pageSize, runs[{id,definitionId,status,currentActivity,createdAt,startedAt,completedAt,durationMs}]}` |
| GET | `/api/v1/runs/summary?from&to` | Workflow Monitor aggregate: per-status + per-definition instance counts over an optional `[from,to)` window (`from`/`to` ISO-8601 parsed as UTC; unparseable values are ignored). Counts only — no cost/economics. |
| GET | `/api/v1/runs/{runId}` | One run's detail: instance metadata + the full DCB event timeline (correlationId = run id, oldest-first) + derived `logs`, `filesChanged`, `prUrl`, `totalCostUsd`. Foreign/unknown run → `404 run_not_found`. The timeline fetch is bounded to **10,000** events; over the cap the response carries the capped slice with `truncated: true`. |

The Story-23 monitoring pages (System Health, Event Store Explorer, Configuration Audit, Workflow Monitor, Agent Monitor, Provider Diagnostics, Infrastructure Monitor) are dashboard compositions over the endpoints on this page — only `/api/v1/runs/summary`, `/api/providers/diagnostics/deep`, and `/api/admin/monitoring/infrastructure` were added for them; the rest reuse `/api/engine/events/query`, `/api/providers/health`, `/api/v1/llm/runs/{id}/stream`, `/api/health`, config/prompts/conventions reads, and the org audit log.

### Dashboard, KB, Mentorship

- **Dashboard** (`/api/dashboard`, DashboardView): `GET /{summary|engines|workflows}`.
- **Knowledge base** (`/api/kb`, SettingsView; writes SettingsManage): ~30 routes across `/index`, `/vector-db`, `/rag`, `/mcp`, `/context`, `/analytics` (e.g. `GET /index/status`, `POST /vector-db/search`, `POST /rag/query`, `GET /mcp/servers`, `POST /mcp/tools/invoke`). The C# API proxies these 1-to-1 to the `intelligence-server` sidecar's `/kb/*` routes. Since Epic 6 (2026-07-05/06) the sidecar composition root wires a **real vector store** (ChromaDB via `CHROMADB_URL`, or pgvector) with embeddings served by **local Ollama** (`EMBEDDING_PROVIDER=ollama`, `nomic-embed-text`, 768-dim — no OpenAI key/cost), and bootstraps the RAG collection at startup, so a configured deployment reports `configured` on `GET /index/status` / `GET /vector-db/status`. With no vector-store env set, the pre-existing `not_configured` stub behaviour still applies.
- **Mentorship** (MVC controller `/api/Mentorship`, `[Authorize]`): `POST /start`, `GET /sessions[/{id}]`, `POST /sessions/{id}/{pause|resume|cancel}`, `GET /sessions/{id}/events`, `GET /analytics/dashboard`.

## SSE streams

Server-Sent Events (`Content-Type: text/event-stream`). Shared writer: `Services/Engine/Lifecycle/SseWriter.cs`.

| Path | Streams | Heartbeat | Tenant scoping | Auth |
|------|---------|-----------|----------------|------|
| `GET /api/admin/tenants/{tenantId}/events/stream` | Platform lifecycle events for one tenant (tags scrubbed to an allow-list) | `:` comment every **30s**; DB poll every **2s**; supports `?fallback=poll` + `Last-Event-ID` cursor | route `{tenantId}` (400 if empty) | PlatformOwnerAccess |
| `GET /api/v1/llm/runs/{correlationId}/stream` | Live read-only view of a managed LLM run | 30s heartbeat; duration-bounded | SaaS: only your tenant's runs (foreign id → 404); single-user: any local run | AuthenticatedAny |
| `GET /api/engine/events/state` | Initial engine `state` snapshot | opens with `:open` | ambient tenant (`ITenantContext`) | WorkflowsView |
| `GET /api/engine/events/logs` | `log` events + live event fan-out (event name = event type) | `:heartbeat` during quiet periods | ambient tenant | WorkflowsView |

The admin stream sets `Cache-Control: no-cache, no-store` and `X-Accel-Buffering: no` (nginx must not buffer SSE).

## Webhooks

All three receivers verify the signature **before** parsing the body and carry `RequireRateLimiting("GitHubWebhook")` (300/min). A missing/bad signature → 401 (Stripe → 400/503).

| Path | Signature | Secret source |
|------|-----------|---------------|
| `POST /api/github/webhooks` | HMAC-SHA256, header `X-Hub-Signature-256` (prefix `sha256=`), timing-safe compare | `GitHub:WebhookSecret` |
| `POST /api/webhooks/{platform}` | Per-platform verifier (see below); tenant secret via per-installation `IWebhookSecretResolver`, else `Webhooks:Secrets:{platform}`, else legacy `GitHub:WebhookSecret` | per-installation cabinet / `Webhooks:Secrets:{platform}` |
| `POST /api/v1/billing/stripe/webhook` | Stripe `EventUtility.ConstructEvent` over raw body + `Stripe-Signature`; unresolvable signing secret → **503 fail-closed** | Epic-29 secret cabinet (`IStripeSigningSecretSource`), not `IConfiguration` |

Per-platform verifiers (`Services/Webhooks/WebhookServiceCollectionExtensions.cs`):

| Platform | Scheme | Header |
|----------|--------|--------|
| GitHub | HMAC-SHA256 | `X-Hub-Signature-256` |
| Gitea | HMAC-SHA256 | `X-Gitea-Signature` |
| Forgejo | HMAC-SHA256 | `X-Forgejo-Signature` (fallback `X-Gitea-Signature`) |
| GitLab | static token | `X-Gitlab-Token` (not HMAC) |

## DCB event catalog

Tamma's audit trail is a single DCB event stream. Events are appended through `POST /api/engine/events` / `POST /api/engine/platform-events` (`EngineServiceOnly`), keyed by type in the pattern `AGGREGATE.ACTION.STATUS`. Type constants live beside the service that raises them (e.g. `Services/Git/GitEventTypes.cs`).

> The `CODE.GENERATED.*` / `ISSUE.ASSIGNED.*` examples in older docs are the **legacy TypeScript** naming and are not constants in the C# port. Use the C# names below.

Representative families (see the `*EventTypes.cs` / `*Events.cs` constant classes for the full lists):

- **Git** — `GIT.BRANCH_CREATED.{SUCCESS,FAILED}`, `GIT.PR_OPENED.*`, `GIT.PR_MERGED.SUCCESS` / `GIT.PR_MERGE.FAILED`, `GIT.ISSUE_UPDATED.*`, `GIT.COMMITS_READ.*`, `GIT.FILE_CHANGES_READ.*`, `GIT.BRANCH_DELETED.*`, `GIT.RELEASE_CREATED.*`.
- **CI** — `CI.TESTS_TRIGGERED.*`, `CI.BUILD_STATUS_READ.*`.
- **Jira** — `JIRA.TICKET_READ.*`, `JIRA.TICKET_UPDATED.*`.
- **Agent dispatch** — `AGENT_DISPATCH.RUN_TRIGGERED.*`, `AGENT_DISPATCH.RUN_POLLED.*`, `AGENT_DISPATCH.RESULTS_COLLECTED.*`.
- **Agent runs / trail** — `AGENT.RUN.{STARTED,SUCCESS,FAILED}`, `AGENT.TASK.{SUCCESS,FAILED,PARTIAL}`, `AGENT.TOOL_CALL.*`, `AGENT.SELECTED_FOR_ROLE.SUCCESS`, `AGENT.ENABLED.SUCCESS` / `AGENT.DISABLED.SUCCESS`.
- **LLM / billing** — `LLM.CALL.{SUCCESS,FAILED}`, `BILLING.SUBSCRIPTION.{CREATED,UPDATED,CANCELED}`, `BILLING.INVOICE.*`, `BILLING.PAYMENT.*`, `BILLING.MODE.MISMATCH`, `PROVIDER_KEY.CHANGED.SUCCESS`.
- **Pricing / entitlements** — `PLAN.VERSION.CREATED`, `TENANT.PLAN.{CHANGED,CANCELLED}`, `PRICING.MARGIN.UPDATED`, `ENTITLEMENT.RESOLVED.{SUCCESS,FAILED}`, `PROVIDER.{REGISTERED,PRICE.VERSIONED,STATUS_CHANGED}`.
- **Audit / notifications** — `AUDIT.CHAIN.{VERIFIED,TAMPER_DETECTED,CHECKPOINTED}`, `AUDIT.QUERIED`, `EMAIL.SENT.{SUCCESS,FAILED}`, `NOTIFICATION.SLACK.SENT.SUCCESS` / `…SEND.FAILED`.
- **Tenant lifecycle / secrets** — `TENANT.CREATED.SUCCESS`, `TENANT.PROVISIONED.SUCCESS`, `TENANT.DELETE.*`, `PLATFORM.INSTALLATION.{CONNECTED,DISCONNECTED}.SUCCESS`, `SECRET.ROTATION.*` (`REQUESTED`/`STAGED`/`SWITCHED`/`COMPLETED`/`RETIRED`/…).
- **Onboarding / repos** — `ONBOARDING.COMPLETED.SUCCESS` (the append-only record of the onboarding milestone; tags `tenantId`/`userId`, data = installation + active-repo counts), `REPO.ACTIVATED.SUCCESS` / `REPO.DEACTIVATED.SUCCESS` (a connected repo's `IsActive` flip).
- **Workflow / ADL activities** — e.g. `CYCLE.{STARTED,COMPLETED,FAILED}`, `MERGE_APPROVAL.DECISION.*`, `MERGE.{SUCCESS,FAILED}`, `PR.CREATED.*`, `REVIEW_FIX.*`, `TDD_DEBUG.*`, `DEPLOY.{STAGE,PIPELINE,ROLLBACK}.*`, `GATE.{PASSED,FAILED,ESCALATED}.*`, `TRIAGE.*`, `BLOCKER.*`, `DEBUG.*`, `CODE_REVIEW.*`, `ANALYTICS.ROLLUP.*`.

## Related

- [Usage & Configuration](Usage-and-Configuration) — config, prompt store, providers, BYOK.
- [Installation & Setup](Installation) — running the API, health checks, deploy.
- [Security](Security) — auth, rate limiting, content sanitization, webhook tenant-scoping.
- [Agent Dispatch](Agent-Dispatch) · [Multi-Tenant Provisioning](Multi-Tenant-Provisioning).
