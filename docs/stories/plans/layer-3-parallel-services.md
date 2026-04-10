# Layer 3: Parallel Services

**Duration**: wall-clock ~168 hours (Epic 18 is critical path), ~260 total hours
**Teams**: 4 parallel teams (A, B, C, D)
**Goal**: Build the core services that Layer 4 UIs and integrations will consume — Epic 9 services, Prompt Store API + Elsa integration, Epic 18 user-auth backend, and Epic 12-7 foundation shim.

**Prerequisite**: Layer 2 merged to `main`. Migration numbers through 012 applied. Tenant middleware (17-5) live. Prompt store service (27-2) available.

## Team Overview

| Team | Focus | Stories | Worktree | Hours |
|------|-------|---------|----------|-------|
| **A** | Epic 9 services | 9-2, 9-3, 9-4, 9-7 | `layer-3-team-a-epic-9-services` | 62 |
| **B** | Prompt store API + Elsa + events | 27-3, 27-6, 27-7 | `layer-3-team-b-prompt-api` | 30 |
| **C** | Epic 18 backend | 18-1, 18-2, 18-3, 18-6 | `layer-3-team-c-epic-18` | 168 (critical path) |
| **D** | Epic 9 bridging | 9-8 | `layer-3-team-d-9-8-resolver` | 18 (starts after 27-3 merged) |

## Parallelism Notes

- **Team A files**: `packages/api/src/routes/diagnostics/*.ts`, `routes/health/*.ts`, `routes/providers/*.ts`, `routes/sanitize/*.ts`, plus `providers/` package updates for each. **Isolated from B and C.**
- **Team B files**: `packages/api/src/routes/prompts/*.ts`, `packages/api/src/services/prompt-store/`, `apps/tamma-elsa/.../prompts/*.cs`. **Isolated from A and C.**
- **Team C files**: `packages/api/src/routes/auth/register.ts`, `login.ts`, `organizations.ts`, `reset-password.ts`, `persistence/user-store.ts` (extended). **Isolated from A and B.**
- **Team D**: 9-8 needs both Epic 9 services (Team A) and Prompt Store API (Team B). Delay start until Team A's 9-2/9-3/9-4 and Team B's 27-3 all merged.

**Key coordination**: Team C will modify `users` table via migration 017 (auth fields). Team C + Team D do not conflict because D waits for A/B.

---

## Team A: Epic 9 Services

**Agent**: 1 (or 2 to run 9-2/9-3/9-4 in parallel inside the team after 9-1 patterns are clear)

### Story 9-2: Diagnostics Service + API

| Attribute | Value |
|-----------|-------|
| **Description** | `/api/v1/diagnostics` GET/POST and `/diagnostics/report`, `/diagnostics/budget/:accountId`. Stores cost, tokens, latency per tenant. CLI mode: in-memory diagnostics with log drain. |
| **Depends on** | 9-1, 17-5 |
| **Blocks** | 9-5, 9-11, 9-12 |
| **Estimated hours** | 20 |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-3-team-a-9-2-diagnostics` |
| **Branch** | `feat/story-9-2-diagnostics-api` |
| **Migration** | **013** (`013_provider_diagnostics.sql`) |
| **Deploy** | NO |
| **Story file** | `docs/stories/epic-9/story-9-2/9-2-provider-diagnostics.md` |

**Key files**:
- `database/migrations/013_provider_diagnostics.sql`
- `packages/api/src/routes/diagnostics/*.ts`
- `packages/api/src/persistence/diagnostics-store.ts` — `PgDiagnosticsStore`, `InMemoryDiagnosticsStore`
- `packages/providers/src/diagnostics-queue.ts` (existing) — wire to store

**Test strategy**:
- Unit: budget calc, cost aggregation, time-window queries
- Integration: append diagnostics, assert query returns correct breakdown
- Rate limit: 300 req/min for POST

### Story 9-3: Health Tracker Service + API

| Attribute | Value |
|-----------|-------|
| **Description** | Postgres-backed circuit breaker state shared across services. GET/POST `/api/v1/health/providers[:key]`, reset endpoint. |
| **Depends on** | 9-1, 17-5 |
| **Blocks** | 9-5, 9-11, 9-12 |
| **Estimated hours** | 16 |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-3-team-a-9-3-health` |
| **Branch** | `feat/story-9-3-health-tracker-api` |
| **Migration** | **014** (`014_provider_health.sql`) |
| **Deploy** | NO |
| **Story file** | `docs/stories/epic-9/story-9-3/9-3-provider-health-tracker.md` |

**Key files**:
- `database/migrations/014_provider_health.sql`
- `packages/api/src/routes/health/providers.ts`
- `packages/providers/src/pg-provider-health-tracker.ts` — new Postgres impl
- `packages/providers/src/provider-health.ts` — existing in-memory; becomes CLI fallback

**Test strategy**:
- Unit: sliding-window failure counting, state transitions (closed → open → half-open → closed)
- Integration: concurrent writers update shared state correctly

### Story 9-4: Provider Factory API

| Attribute | Value |
|-----------|-------|
| **Description** | `POST /api/v1/providers/create` — resolves config + health + diagnostics and returns a provider instance reference. |
| **Depends on** | 9-1, 9-2, 9-3 |
| **Blocks** | 9-5, 9-8 |
| **Estimated hours** | 12 |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-3-team-a-9-4-factory` |
| **Branch** | `feat/story-9-4-provider-factory-api` |
| **Migration** | — |
| **Deploy** | NO |
| **Story file** | `docs/stories/epic-9/story-9-4/9-4-agent-provider-factory.md` |

**Key files**:
- `packages/api/src/routes/providers/create.ts`
- `packages/providers/src/agent-provider-factory.ts` — extend with API wiring

### Story 9-7: Sanitization Service + API

| Attribute | Value |
|-----------|-------|
| **Description** | Per-tenant sanitization rules (prompt-injection, PII, credentials). `POST /sanitize`, `GET/PUT /sanitize/rules`. CLI mode: default rules from `packages/shared/src/security/`. |
| **Depends on** | 9-1, 17-5 |
| **Blocks** | 9-8 |
| **Estimated hours** | 14 |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-3-team-a-9-7-sanitization` |
| **Branch** | `feat/story-9-7-sanitization-api` |
| **Migration** | **015** (`015_sanitization_rules.sql`) |
| **Deploy** | NO |
| **Story file** | `docs/stories/epic-9/story-9-7/9-7-content-sanitization.md` |

**Key files**:
- `database/migrations/015_sanitization_rules.sql`
- `packages/shared/src/security/sanitization.ts` — existing default rules
- `packages/api/src/routes/sanitize/*.ts`
- `packages/api/src/persistence/sanitization-rule-store.ts`

**Test strategy**:
- Unit: sanitization catches known-bad patterns (prompt injection, secrets)
- Integration: per-tenant rule override

---

## Team B: Prompt Store API + Elsa + Events

**Agent**: 1

### Story 27-3: Prompt Store API Endpoints

| Attribute | Value |
|-----------|-------|
| **Description** | Tenant-scoped REST API: `GET/PUT/POST /api/v1/prompts/:role/:action`, plus admin endpoints for system defaults (platform_admin only). |
| **Depends on** | 27-2, 17-5, 16-5 |
| **Blocks** | 27-4, 27-5, 27-6, 9-8 |
| **Estimated hours** | 12 |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-3-team-b-27-3-api` |
| **Branch** | `feat/story-27-3-prompt-store-api` |
| **Migration** | — |
| **Deploy** | NO |
| **Story file** | `docs/stories/epic-27/27-3-prompt-store-api-endpoints.md` |

**Key files**:
- `packages/api/src/routes/prompts/tenant-prompts.ts` — replaces or augments existing routes
- `packages/api/src/routes/prompts/system-prompts.ts` — platform_admin only
- `packages/api/src/schemas/prompt.schema.ts`

**Test strategy**:
- RBAC: tenant admin can edit own prompts but not system defaults
- Platform admin: can edit system defaults
- Integration: two-tier resolution returns correct prompt

### Story 27-6: Elsa Workflow Integration

| Attribute | Value |
|-----------|-------|
| **Description** | Update `ResolvePromptFromRegistryActivity.cs` and `LlmCallWorkflow.cs` to call the Fastify API for prompt resolution, passing `tenantId`. |
| **Depends on** | 27-3, 16-7 (service-to-service auth), 17-4 (tenant-scoped workflow) |
| **Blocks** | — |
| **Estimated hours** | 10 |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-3-team-b-27-6-elsa` |
| **Branch** | `feat/story-27-6-prompt-store-elsa-integration` |
| **Migration** | — |
| **Deploy** | YES (Elsa container needs redeploy) |
| **Story file** | `docs/stories/epic-27/27-6-elsa-workflow-integration.md` |

**Key files**:
- `apps/tamma-elsa/.../ResolvePromptFromRegistryActivity.cs`
- `apps/tamma-elsa/.../LlmCallWorkflow.cs`
- `apps/tamma-elsa/.../HttpClients/PromptStoreClient.cs` — new

### Story 27-7: Prompt Store Event Sourcing

| Attribute | Value |
|-----------|-------|
| **Description** | Emit DCB events (`PROMPT.CREATED`, `PROMPT.UPDATED`, `PROMPT.DELETED`) via the tenant-scoped event store for every prompt mutation. |
| **Depends on** | 27-2, 17-3 |
| **Blocks** | — |
| **Estimated hours** | 8 |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-3-team-b-27-7-events` |
| **Branch** | `feat/story-27-7-prompt-store-event-sourcing` |
| **Migration** | — |
| **Deploy** | NO |
| **Story file** | `docs/stories/epic-27/27-7-prompt-store-event-sourcing.md` |

---

## Team C: Epic 18 Backend (Critical Path)

**Agent**: 1 (or 2 if you can split 18-1/18-2 from 18-3/18-6)
**Total hours**: 168 (critical path for Layer 3)

### Story 18-1: User Registration + Email Verification

| Attribute | Value |
|-----------|-------|
| **Description** | Self-service registration endpoint. Argon2 password hashing. Email verification via nodemailer. Adds `password_hash`, `email_verified`, `email_verification_token_hash`, `email_verification_expires_at`, `auth_method` columns to `users`. |
| **Depends on** | 17-5, 16-7 |
| **Blocks** | 18-2, 18-6 |
| **Estimated hours** | L (~40) |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-3-team-c-18-1-registration` |
| **Branch** | `feat/story-18-1-registration-email` |
| **Migration** | **017** (`017_user_auth_fields.sql`) |
| **Deploy** | YES (SMTP config env vars required) |
| **Story file** | `docs/stories/epic-18/18-1-user-registration-email-verification.md` |

**Key files**:
- `database/migrations/017_user_auth_fields.sql`
- `packages/api/src/routes/auth/register.ts`
- `packages/api/src/routes/auth/verify-email.ts`
- `packages/api/src/services/mailer.ts` — nodemailer wrapper
- `packages/api/src/persistence/user-store.ts` — extend with password fields
- `.env.example` — SMTP_HOST, SMTP_USER, SMTP_PASS, SMTP_FROM, FRONTEND_URL

**Test strategy**:
- Unit: Argon2 hash/verify
- Integration: POST /register → user row created with `email_verified=false`
- Integration: verify token → `email_verified=true`
- Expiry: expired token rejected

### Story 18-2: User Login + Session Management

| Attribute | Value |
|-----------|-------|
| **Description** | `POST /api/v1/auth/login` with email+password. Issues `UnifiedJwtPayload` JWT with `tenantId`, `role`, `platformRole`. Refresh token rotation. |
| **Depends on** | 18-1 |
| **Blocks** | 18-3, 18-5, 18-6 |
| **Estimated hours** | L (~40) |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-3-team-c-18-2-login` |
| **Branch** | `feat/story-18-2-login-sessions` |
| **Migration** | — |
| **Deploy** | NO |
| **Story file** | `docs/stories/epic-18/18-2-user-login-session-management.md` |

**Key files**:
- `packages/api/src/routes/auth/login.ts`
- `packages/api/src/routes/auth/refresh.ts`
- `packages/api/src/services/refresh-token-store.ts`
- `packages/api/src/schemas/jwt.schema.ts` — confirm `UnifiedJwtPayload` shape from 16-5

### Story 18-3: Organization/Tenant Creation

| Attribute | Value |
|-----------|-------|
| **Description** | `POST /api/v1/orgs` to create a new tenant (organization). `GET /api/v1/orgs` to list user's memberships. `tenant_memberships` M:N table. |
| **Depends on** | 18-2, 17-1 |
| **Blocks** | 18-4, 18-5 |
| **Estimated hours** | XL (~64) |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-3-team-c-18-3-org-tenant` |
| **Branch** | `feat/story-18-3-organization-tenant` |
| **Migration** | **016** (`016_tenant_memberships.sql`) |
| **Deploy** | NO |
| **Story file** | `docs/stories/epic-18/18-3-organization-tenant-creation.md` |

**Key files**:
- `database/migrations/016_tenant_memberships.sql`
- `packages/api/src/routes/orgs/*.ts`
- `packages/api/src/persistence/tenant-membership-store.ts`
- `packages/api/src/rbac/middleware.ts` — extend to check tenant_memberships

**Note on migration ordering**: 016 depends on `tenants` (008) and `users` (002). Teams A's migrations (013, 014, 015) and 016 can land in any order as long as each depends only on earlier numbers. **Team B's 27-* has no new migrations**, so 016 is the last one in Layer 3.

### Story 18-6: Password Reset Flow

| Attribute | Value |
|-----------|-------|
| **Description** | `POST /api/v1/auth/reset-password-request` + `POST /api/v1/auth/reset-password` with token. Uses same nodemailer service from 18-1. |
| **Depends on** | 18-1, 18-2 |
| **Blocks** | — |
| **Estimated hours** | M (~24) |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-3-team-c-18-6-password-reset` |
| **Branch** | `feat/story-18-6-password-reset` |
| **Migration** | — (reuses `users` table; may add `password_reset_token_hash` column via 017 or a follow-up 018) |
| **Deploy** | NO |
| **Story file** | `docs/stories/epic-18/story-18-6/18-6-password-reset.md` |

**Note**: If 18-6 needs a new column, add it to migration 017 (same PR as 18-1) or request a new number from the Migration Steward.

---

## Team D: Epic 9 Bridging (Deferred Start)

### Story 9-8: Unified Agent Resolver API

| Attribute | Value |
|-----------|-------|
| **Description** | `GET /api/v1/agents/:role/resolve` and `POST /api/v1/agents/resolve-for-phase`. Resolves agent config + provider chain + prompt for a given role. Single resolution logic for TS engine and Elsa. |
| **Depends on** | 9-1, 9-3, 9-4, 9-7, 27-3 (all must be merged) |
| **Blocks** | 9-9, 9-11, 9-12 |
| **Estimated hours** | 18 |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-3-team-d-9-8-resolver` |
| **Branch** | `feat/story-9-8-agent-resolver-api` |
| **Migration** | — |
| **Deploy** | NO |
| **Story file** | `docs/stories/epic-9/story-9-8/9-8-role-based-agent-resolver.md` |

**Starts when**: Team A finishes 9-2, 9-3, 9-4, 9-7 AND Team B finishes 27-3. Until then, Team D agent contributes as reviewer or helps Team A.

**Key files**:
- `packages/api/src/routes/agents/resolve.ts`
- `packages/providers/src/role-based-agent-resolver.ts` — extend with API wiring
- `packages/api/src/services/agent-resolver.ts` — composes AgentConfigStore + HealthTracker + PromptStore + Sanitization

---

## Integration Checkpoint

At the end of Layer 3:

1. All stories merged to `main`
2. Migrations **013, 014, 015, 016, 017** applied on staging DB (in order)
3. Smoke test:
   - Register a new user on `app.tamma.dev` → verify email → login
   - User creates an org → becomes owner
   - User PUT agent config for their new org
   - Call `GET /api/v1/agents/coder/resolve` → returns config + prompt + provider chain
4. Deploy Coordinator does a staging deploy of API + Elsa + dashboard

## Rollback Considerations

- Migration 016 (tenant_memberships) is additive; safe to leave if code reverted
- 18-x stories all behind a feature flag (`ENABLE_SELF_SERVICE_REGISTRATION`)
- 27-6 Elsa integration: if API call fails, fall back to file-based prompt resolution (transition flag)

## Handoff to Layer 4

Layer 4 assumes:

- Epic 9 services 9-2, 9-3, 9-4, 9-7 + resolver 9-8 live
- Prompt Store API 27-3 live; Elsa uses it (27-6); events emitted (27-7)
- Registration + login + orgs + password reset working end-to-end
- Migration numbers through **017** applied
- Service-to-service auth carrying `tenantId` across all inter-service calls

---

**Next**: [`layer-4-integration-ui.md`](./layer-4-integration-ui.md)
