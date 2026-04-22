# Layer 2: Parallel Infrastructure

**Duration**: wall-clock ~46 hours (critical path Team A), ~128 total hours
**Teams**: 5 parallel teams (A, B, C, D, E)
**Goal**: Build the infrastructure pieces that depend on Layer 1 — tenant isolation, prompt store foundation, agent config API foundation, admin UI — plus knock out quick-win bug fixes.

**Prerequisite**: Layer 1 is merged to `main` and CI is green. All teams pull from `main` when creating their worktrees.

## Team Overview

| Team | Focus | Stories | Worktree | Hours |
|------|-------|---------|----------|-------|
| **A** | Tenant scoping | 17-2, 17-3, 17-4, 17-5 | `layer-2-team-a-tenant-scoping` | 46 |
| **B** | Prompt store foundation | 27-1, 27-2 | `layer-2-team-b-prompt-store` | 24 |
| **C** | Agent config API foundation | 9-1 | `layer-2-team-c-agent-config` | 16 |
| **D** | Admin UI | 16-3, 16-4 | `layer-2-team-d-admin-ui` | 36 |
| **E** | Quick wins | 12-5c, 12-5e | `layer-2-team-e-quickwins` | 6 |

## Parallelism Notes

- **Team A vs. Team B/C**: A touches `packages/api/src/rls/`, `persistence/event-store.ts`, `workflow-store.ts`, `middleware/tenant-context.ts`. B touches `packages/api/src/services/prompt-store.ts`, `routes/prompts/*.ts`. C touches `packages/api/src/routes/agents/config.ts`. **No file overlap; safe parallel.**
- **Team B vs. Team C**: Both touch `database/migrations/` (011 vs 012) but different files. Migration numbers pre-assigned — see migration-ordering.md. **Safe.**
- **Team D vs. A/B/C**: D touches `packages/dashboard/src/` and `packages/dashboard-admin/` (front-end). **Safe.**
- **Team E vs. others**: E touches `apps/tamma-elsa/.../MentorshipWorkflow.cs` and `SingleIssueCycleWorkflow.cs`. Isolated C# files. **Safe.**

**Integration checkpoint**: All five teams merge their PRs independently. Layer 3 starts only when all five are green on `main`.

---

## Team A: Tenant Scoping

**Agent**: 1 (or 2 with 17-2 + 17-3 paralleled once 17-2 is merged)
**Goal**: Enable Row-Level Security, scope event store and workflow store by tenant, add tenant context middleware.

### Story 17-2: Row-Level Security for Tenant Isolation

| Attribute | Value |
|-----------|-------|
| **Description** | Enable RLS on tenant-scoped tables (`github_installations`, `users`, `user_api_keys`, `user_invites`), create `tamma_app` role, `tenant_isolation_policy`, and `prevent_tenant_id_change()` trigger. |
| **Depends on** | 17-1 (Layer 1) |
| **Blocks** | 17-3, 17-4, 17-5 |
| **Estimated hours** | 12 |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-2-team-a-17-2-rls` |
| **Branch** | `feat/story-17-2-row-level-security` |
| **Migration** | **009** (`009_rls_tenant_isolation.sql`) |
| **Deploy** | NO (migration only; DB role creation on next deploy) |
| **Story file** | `docs/stories/epic-17/17-2-row-level-security-tenant-isolation.md` |

**Key files**:
- `database/migrations/009_rls_tenant_isolation.sql` — new
- `packages/api/src/persistence/db-pool.ts` — connect as `tamma_app` role, not `postgres`
- `docs/stories/rbac-unified-model.md` — cross-reference from RLS policies

**Test strategy**:
- Migration idempotency (`DROP POLICY IF EXISTS` + `CREATE POLICY`)
- Unit: mock `SET app.current_tenant_id` and assert query results are filtered
- Integration: two tenants, query one tenant's rows → only see that tenant's data
- Security test: without `SET app.current_tenant_id`, queries return zero rows

**Success criteria**:
- Migration 009 clean and idempotent
- `tamma_app` role used by runtime connections (superuser reserved for migrations)
- All existing integration tests still pass
- New RLS test suite added

### Story 17-3: Tenant-Scoped Event Store

| Attribute | Value |
|-----------|-------|
| **Description** | Add `tenant_id` column to event store tables; update `EngineEvent` type and `IEventStore` methods to carry tenant context. |
| **Depends on** | 17-1, 17-2 |
| **Blocks** | Layer 3 event-sourced stories (9-2, 9-11, 27-7) |
| **Estimated hours** | 10 |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-2-team-a-17-3-event-store` |
| **Branch** | `feat/story-17-3-tenant-event-store` |
| **Migration** | **010** (`010_tenant_scoped_event_store.sql`) — combined with 17-4 |
| **Deploy** | NO |
| **Story file** | `docs/stories/epic-17/17-3-tenant-scoped-event-store.md` |

**Key files**:
- `database/migrations/010_tenant_scoped_event_store.sql` — adds `tenant_id` to event store tables + workflow tables (17-4 shares this file)
- `packages/shared/src/events/engine-event.ts` — add `tenantId` to base event shape
- `packages/api/src/persistence/event-store.ts` — append, query methods scoped by tenant

**Test strategy**:
- Migration replay on shared test DB
- Unit: appending an event without tenant → error
- Integration: two tenants writing events, each can only read own events

### Story 17-4: Tenant-Scoped Workflow Instances

| Attribute | Value |
|-----------|-------|
| **Description** | Add `tenant_id` column to workflow instance tables; update `IWorkflowStore` methods; C#-side Elsa activity updates to set `WorkflowVariable.TenantId`. |
| **Depends on** | 17-1, 17-2 |
| **Blocks** | Layer 3 Elsa stories (9-11, 27-6) |
| **Estimated hours** | 10 |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-2-team-a-17-4-workflow-store` |
| **Branch** | `feat/story-17-4-tenant-workflow-store` |
| **Migration** | **010** (shared with 17-3) |
| **Deploy** | NO |
| **Story file** | `docs/stories/epic-17/17-4-tenant-scoped-workflow-instances.md` |

**Coordination**: 17-3 and 17-4 share migration 010. Team A must land them in a single PR **or** carefully sequence: write migration 010 once, include both tenant_id column additions. Recommended: one PR with both stories.

### Story 17-5: API Tenant Context Middleware

| Attribute | Value |
|-----------|-------|
| **Description** | Fastify preHandler that reads `tenantId` from the JWT, calls `SET app.current_tenant_id = :tenantId` on the request's DB connection, falls back to `DEFAULT_TENANT_ID` in CLI mode per `cli-fallback-behavior.md`. |
| **Depends on** | 17-1, 17-2, 16-7 (for service JWT tenant claim) |
| **Blocks** | Every Layer 3 API route |
| **Estimated hours** | 14 |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-2-team-a-17-5-tenant-middleware` |
| **Branch** | `feat/story-17-5-tenant-context-middleware` |
| **Migration** | — |
| **Deploy** | NO |
| **Story file** | `docs/stories/epic-17/17-5-api-tenant-context-middleware.md` |

**Key files**:
- `packages/api/src/middleware/tenant-context.ts` — new Fastify preHandler
- `packages/api/src/index.ts` — register middleware globally
- `packages/api/src/persistence/db-pool.ts` — per-request connection with `SET app.current_tenant_id`

**Test strategy**:
- Unit: middleware extracts `tenantId` from `UnifiedJwtPayload`
- Unit: CLI mode (no JWT) → uses `DEFAULT_TENANT_ID`
- Integration: authenticated request → downstream queries see only that tenant's rows

**Success criteria**:
- Every DB query in a request runs under the correct tenant context
- CLI mode works without a tenant claim
- RLS enforcement verified end-to-end

---

## Team B: Prompt Store Foundation

### Story 27-1: Prompt Store Database Schema + Migration

| Attribute | Value |
|-----------|-------|
| **Description** | Create `prompts`, `system_prompts`, `action_prompts` tables with partial unique indexes. Seed 80+8+10 system default rows from `default-prompts.ts`. FK to `tenants(id)`. **No RLS** (exempt per 17-2 exemption list). |
| **Depends on** | 17-1 |
| **Blocks** | 27-2, 27-3, 27-6, 27-7 |
| **Estimated hours** | 10 |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-2-team-b-27-1-schema` |
| **Branch** | `feat/story-27-1-prompt-store-schema` |
| **Migration** | **011** (`011_prompt_store.sql`) |
| **Deploy** | NO |
| **Story file** | `docs/stories/epic-27/27-1-prompt-store-database-schema.md` |

**Key files**:
- `database/migrations/011_prompt_store.sql` — new
- `scripts/generate-prompt-seed.ts` — helper script to emit seed SQL from `default-prompts.ts`

**Test strategy**:
- Migration replay on shared test DB
- Unit: query system defaults (tenant_id IS NULL), expect 80 + 8 + 10 rows
- Integration: insert a tenant override, ensure partial unique index permits both system default and tenant override for same (role, action)

### Story 27-2: Prompt Store Service (TypeScript)

| Attribute | Value |
|-----------|-------|
| **Description** | Implement `PgPromptStore` and `InMemoryPromptStore` implementing `IPromptStore`. Two-tier resolution (tenant override → system default). Provider-dimension support per Epic 27 spec. |
| **Depends on** | 27-1 |
| **Blocks** | 27-3, 27-6, 27-7, 9-8 |
| **Estimated hours** | 14 |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-2-team-b-27-2-service` |
| **Branch** | `feat/story-27-2-prompt-store-service` |
| **Migration** | — |
| **Deploy** | NO |
| **Story file** | `docs/stories/epic-27/27-2-prompt-store-service.md` |

**Key files**:
- `packages/api/src/services/prompt-store/pg-prompt-store.ts`
- `packages/api/src/services/prompt-store/in-memory-prompt-store.ts`
- `packages/api/src/services/prompt-store/types.ts` — `IPromptStore`, `ResolvedPrompt`
- `packages/api/src/services/prompt-store/resolution.ts` — 4-step resolution logic

**Test strategy**:
- Unit: two-tier resolution (tenant found, tenant not found → system default, neither → 404)
- Unit: provider-dimension resolution (role+action+provider > role+action)
- Integration: PgPromptStore round-trip against shared test DB
- CLI mode: `InMemoryPromptStore` seeded from `default-prompts.ts`

---

## Team C: Agent Config API Foundation

### Story 9-1: Agent Config Schema + API

| Attribute | Value |
|-----------|-------|
| **Description** | Define `AgentConfig` schema. Add `GET/PUT/POST /api/v1/agents/config` endpoints. `PgAgentConfigStore` persists per-tenant; `FileAgentConfigStore` reads from `tamma.config.json` in CLI mode. |
| **Depends on** | 17-1, 17-5, 16-5, 16-7 |
| **Blocks** | 9-2, 9-3, 9-4, 9-5, 9-7, 9-8, 9-10 |
| **Estimated hours** | 16 |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-2-team-c-9-1-agent-config` |
| **Branch** | `feat/story-9-1-agent-config-api` |
| **Migration** | **012** (`012_agent_configs.sql`) |
| **Deploy** | NO |
| **Story file** | `docs/stories/epic-9/story-9-1/9-1-configuration-schema.md` |

**Key files**:
- `database/migrations/012_agent_configs.sql`
- `packages/api/src/routes/agents/config.ts`
- `packages/api/src/persistence/agent-config-store.ts` — `PgAgentConfigStore` + `FileAgentConfigStore`
- `packages/shared/src/schemas/provider.schema.ts` — Zod schema for `AgentConfig`

**Test strategy**:
- Unit: schema validation (valid config passes, invalid fails)
- Unit: rate limiting (30 req/min write, 100 req/min read)
- Integration: PUT then GET round-trip; per-tenant isolation via 17-5 middleware
- CLI mode: `FileAgentConfigStore` loads from `tamma.config.json`

**Success criteria**:
- Endpoints documented in OpenAPI
- Both Pg and File backends covered by unit tests
- Rate limit verified
- Coverage ≥ 80% line

---

## Team D: Admin UI

### Story 16-3: Admin Dashboard

| Attribute | Value |
|-----------|-------|
| **Description** | React admin dashboard at `app.tamma.dev/admin` with system health overview, user management UI, and quick links to ELSA Studio and OpenSearch Dashboards. |
| **Depends on** | 16-1, 16-2 |
| **Blocks** | — |
| **Estimated hours** | 24 |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-2-team-d-16-3-admin-dashboard` |
| **Branch** | `feat/story-16-3-admin-dashboard` |
| **Deploy** | YES (new dashboard bundle; ensure nginx serves `/admin`) |
| **Story file** | `docs/stories/epic-16/16-3-admin-dashboard.md` |

**Key files**:
- `packages/dashboard/src/admin/` — new pages
- `packages/dashboard/src/admin/users/*.tsx` — user list, edit, invite
- `packages/dashboard/src/admin/health/*.tsx` — system health panel
- `packages/dashboard/src/routes.tsx` — add admin routes

**Test strategy**:
- Unit (Vitest + React Testing Library): page renders, forms validate
- Playwright E2E: login → navigate to admin → create user invite
- Accessibility: axe-core scan passes

### Story 16-4: Unified Navigation Header

| Attribute | Value |
|-----------|-------|
| **Description** | Shared nav header shown across app.tamma.dev, elsa.tamma.dev, logs.tamma.dev. Shows logged-in user, service links, and current service indicator. |
| **Depends on** | 16-1 |
| **Blocks** | — |
| **Estimated hours** | 12 |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-2-team-d-16-4-unified-nav` |
| **Branch** | `feat/story-16-4-unified-nav` |
| **Deploy** | YES (ELSA Studio and OpenSearch Dashboards need header injection; possibly via oauth2-proxy response headers or reverse proxy HTML rewriting) |
| **Story file** | `docs/stories/epic-16/16-4-unified-navigation.md` |

**Key files**:
- `packages/shared-ui/src/nav-header.tsx` — new package or added to `@tamma/shared`
- `nginx-proxy/conf.d/*.conf` — header injection for non-React services (may use `sub_filter` to inject HTML)
- `packages/dashboard/src/layouts/*.tsx` — include the shared header

**Test strategy**:
- Unit: header renders, current-service highlighting works
- Integration: manual verification on all three subdomains
- E2E: Playwright across services, ensure clicking switches correctly

**Note**: Team D's two stories can run serially (one agent, 16-3 → 16-4) or in parallel (two agents). No file conflicts.

---

## Team E: Quick Wins

Two small bug fixes with zero external dependencies. Assign to a junior agent or a spare cycle. Should merge within a day.

### Story 12-5c: Mentorship Skill-Level Adaptation Fix

| Attribute | Value |
|-----------|-------|
| **Description** | Fix hardcoded `skillLevel = 3` in `MentorshipWorkflow.cs`. Thread assessment activity outputs into the skill-level variable so mentorship adapts. |
| **Depends on** | None |
| **Blocks** | — |
| **Estimated hours** | 4 |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-2-team-e-12-5c-skill-level` |
| **Branch** | `fix/story-12-5c-skill-level` |
| **Deploy** | NO |
| **Story file** | `docs/stories/epic-12/story-12-5/12-5-prompt-engineering-framework.md` (sub-story 12-5c) |

**Key files**:
- `apps/tamma-elsa/.../MentorshipWorkflow.cs`
- Workflow JSON exports (`packages/workflows/*.json` if any)

**Test strategy**:
- Unit test on the workflow variable assignment
- Manual verification by replaying a mentorship workflow with different assessment outcomes

### Story 12-5e: CI Retry Counter Bug Fix

| Attribute | Value |
|-----------|-------|
| **Description** | Reset `ciRetryCount` on re-entry to CI phase from review-fix or merge re-test in `SingleIssueCycleWorkflow.cs` lines 349-351. |
| **Depends on** | None |
| **Blocks** | — |
| **Estimated hours** | 2 |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-2-team-e-12-5e-ci-retry` |
| **Branch** | `fix/story-12-5e-ci-retry-counter` |
| **Deploy** | NO |
| **Story file** | `docs/stories/epic-12/story-12-5/12-5-prompt-engineering-framework.md` (sub-story 12-5e) |

**Key files**:
- `apps/tamma-elsa/.../SingleIssueCycleWorkflow.cs`

**Test strategy**:
- Workflow unit test: re-enter CI from review-fix → counter resets to 0
- Re-enter from merge re-test → counter resets to 0

---

## Integration Checkpoint

Before Layer 3:

1. All five teams' PRs merged to `main`
2. CI green on `main`
3. Migrations 009, 010, 011, 012 applied cleanly on staging DB
4. Smoke test: end-to-end tenant isolation:
   - Create two tenants
   - Put agent config for each
   - Verify tenant A cannot see tenant B's config
5. Coordinator announces Layer 3 start

## Rollback Considerations

- 17-2 RLS: if blocking legitimate queries, temporarily connect as superuser (emergency only)
- 17-5 middleware: ensure CLI fallback path is tested before merging
- 27-1 seed data: if seed fails, migration is a no-op
- 16-3 admin UI: ship behind a feature flag if needed

## Handoff to Layer 3

Layer 3 assumes:

- Tenant context middleware (17-5) is active on all API routes
- Prompt store service (27-2) available as `IPromptStore` from `@tamma/api`
- Agent config API (9-1) available as reference template for other Epic 9 stories
- Admin dashboard shell (16-3) exists for adding pages
- Migration numbers through **012** applied

---

**Next**: [`layer-3-parallel-services.md`](./layer-3-parallel-services.md)
