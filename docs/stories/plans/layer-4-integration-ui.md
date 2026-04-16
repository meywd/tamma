# Layer 4: Integration & UI

**Status**: Revised 2026-04-16 for post–Epic 19 reality.
**Duration**: wall-clock ~156 hours (Team D critical path), ~411 total hours (343 original + 60 hardening + 8 Team A rebalance + 16 bridge)
**Teams**: 4 parallel teams (A, B, C, D)
**Goal**: Complete Epic 9 end-to-end (chain, resolver, engine integration, CLI wiring, Elsa integration, integration test), build the prompt store UIs, finish Epic 18 with onboarding + user dashboard, and deliver Epic 12 prompt-engineering + context-tool features.

**Prerequisites** (both required):

1. PR #328 (`feat/auth-foundation`) merged to `main`.
2. The ~60h hardening punch list in
   [`layer-2-3-status-post-epic-19.md`](./layer-2-3-status-post-epic-19.md)
   completed. Epic 19 replaced the TS `packages/api/` with a thinner C# API,
   so every Layer 3 "done" story has a shallower implementation than the
   original Layer 4 plan assumed. Tasks 1–3 of that punch list (system
   prompts, agent resolver, provider chain) are **hard** blockers for
   Team A; tasks 6–7 (prompt render, sanitization rules) are hard blockers
   for Team B and Team D.

## Post–Epic 19 architecture delta

The pre-Epic-19 plan targeted a TypeScript `packages/api/` (Fastify) as the
single backend. Epic 19 Phase 3 deleted that package and replaced it with a
C# Minimal API at `apps/tamma-elsa/src/Tamma.Api/`. All "API" work in Layer
4 now means C#. The TypeScript side still hosts:

- `packages/orchestrator/` — engine (14-step autonomous loop)
- `packages/intelligence/` — vector DB, context tools, conventions
- `packages/providers/` — 8 AI provider implementations
- `packages/platforms/` — 7 Git platform implementations
- `packages/cli/` — Ink-based CLI
- `packages/dashboard/` — admin dashboard (React)
- `packages/dashboard-user/` — user dashboard (to be created by 18-5)

Cross-language integration points:

- TS engine → C# API: HTTP (service JWT). Replaces in-process factory calls.
- Elsa C# activities → C# API: HTTP on the same host (was "→ Fastify API").
- Elsa C# activities → TS context tools: via a new `/api/v1/context/tools/*`
  proxy on the C# API, which itself calls into a lightweight TS sidecar
  that wraps `packages/intelligence`. (See Team D section for the delta.)

## Team Overview

| Team | Focus | Stories | Worktree | Hours |
|------|-------|---------|----------|-------|
| **A** | Epic 9 completion | 9-5, 9-9, 9-10, 9-11, 9-12 | `layer-4-team-a-epic-9-completion` | 97 (was 89; +8 net after Epic 19 rebalance) |
| **B** | Prompt Store UIs | 27-4, 27-5 | `layer-4-team-b-prompt-ui` | 34 (was 32) |
| **C** | Epic 18 completion | 18-4, 18-5 | `layer-4-team-c-epic-18-ui` | 64 (unchanged) |
| **D** | Epic 12 + cross-language bridge | 12-5a, 12-5b, 12-5d, 12-7a, 12-7b, 12-7c, 12-7d, 12-7e, bridge | `layer-4-team-d-epic-12` | 156 (was 140; +16 bridge) |
| **Hardening** | pre-Layer-4 C# backend depth restoration | punch list in `layer-2-3-status-post-epic-19.md` | — | 60 |

## Parallelism Notes

- **Team A vs. D**: Some overlap in `packages/api/` routes and `packages/orchestrator/`. Coordinate via file ownership: A owns `routes/providers/`, `routes/diagnostics/`, `routes/agents/`; D owns `routes/context/` (new), `tools/` packages. **Safe with discipline.**
- **Team B vs. others**: B only touches `packages/dashboard/src/prompts/` (admin UI) and `packages/dashboard-user/src/prompts/` (tenant UI). **Isolated.**
- **Team C vs. others**: C builds on 16-3 admin shell (16-3) and creates `packages/dashboard-user/` if it doesn't already exist. **Isolated.**
- **Team D internal dependencies**:
  - 12-7a → 12-7c (budget manager needs vector tools)
  - 12-7b → 12-7c (budget manager needs convention/history tools)
  - 12-7c → 12-7e (Elsa integration needs budget manager)
  - 12-7d → 12-7e (tool access config needs Elsa integration)
  - 12-5a, 12-5b, 12-5d run in parallel with 12-7 tracks

---

## Team A: Epic 9 Completion

**Agent**: 1 (or 2 to pipeline 9-5 + 9-9 after 9-8 is hardened)
**Order**: hardening punch list tasks 2–5 → 9-5 → 9-9 → 9-10 (CLI) || 9-11 (Elsa) → 9-12 (integration test)
**Language target**: all API work in C# (`apps/tamma-elsa/src/Tamma.Api/`). TS work only in `packages/orchestrator/`, `packages/cli/`, `packages/providers/`.

### Story 9-5: Provider Chain API

| Attribute | Value |
|-----------|-------|
| **Description** | `POST /api/v1/providers/chain/resolve` in C# — returns ordered list of providers to try based on health state and config. Builds on hardening punch list task 3 (which provides the minimum viable endpoint) + task 4 (real circuit breaker). This story adds config-aware ordering, fallback chain, per-tenant overrides. |
| **Depends on** | hardening tasks 3, 4; 9-2 (hardened), 9-3 (hardened) |
| **Blocks** | 9-9, 9-11, 9-12 |
| **Estimated hours** | 14 (unchanged — builds on hardening foundation) |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-4-team-a-9-5-chain` |
| **Branch** | `feat/story-9-5-provider-chain-api` |
| **Key files** | `apps/tamma-elsa/src/Tamma.Api/Endpoints/ProviderEndpoints.cs` (add `ResolveChain`), `.../Services/ProviderChainResolver.cs` (new) |
| **Deploy** | NO (C# API hot-reloads on CI deploy) |

### Story 9-9: Engine Integration

| Attribute | Value |
|-----------|-------|
| **Description** | TypeScript engine (`packages/orchestrator/`) calls C# API `/api/v1/agents/{role}/resolve` and `/api/v1/providers/chain/resolve` instead of the in-process factory. Requires a TS `TammaApiClient` with service-JWT auth. |
| **Depends on** | 9-5, 9-8 (= hardening task 2) |
| **Blocks** | 9-10 |
| **Estimated hours** | 18 (+4h vs original — service-JWT token acquisition + retry/backoff client needed) |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-4-team-a-9-9-engine` |
| **Branch** | `feat/story-9-9-engine-integration` |
| **Key files** | `packages/orchestrator/src/api-client.ts` (new), `packages/orchestrator/src/engine.ts` (swap resolver call), `packages/shared/src/auth/service-jwt.ts` (token acquisition) |
| **Deploy** | NO |

### Story 9-10: CLI Wiring

| Attribute | Value |
|-----------|-------|
| **Description** | CLI mode uses in-memory/file fallbacks per `cli-fallback-behavior.md`. Auto-detects C# API availability (HTTP probe); falls back to in-process resolver when offline. |
| **Depends on** | 9-1 (hardened), 9-9 |
| **Blocks** | 9-12 |
| **Estimated hours** | 14 (+2h — API detection probe + two-code-path tests) |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-4-team-a-9-10-cli` |
| **Branch** | `feat/story-9-10-cli-wiring` |
| **Key files** | `packages/cli/src/config.ts` — API detection + fallback; `packages/cli/src/bootstrap.ts` — wire HTTP client vs. in-process shim |
| **Deploy** | NO |

### Story 9-11: Diagnostics Queue + Elsa Integration

| Attribute | Value |
|-----------|-------|
| **Description** | Five Elsa C# activities (`CheckCircuitBreakerActivity`, `RecordDiagnosticsActivity`, `ResolveAgentConfigActivity`, `CheckBudgetActivity`, `CallLlmActivity`) must delegate to the C# API. Since both live in the same solution (`Tamma.sln`) and the same container, the delegation is an in-process HTTP call via `HttpClient` pointing at `http://localhost:5000` — simpler than the pre-Epic-19 plan assumed. |
| **Depends on** | 9-2 (hardened), 9-3 (hardened), 9-5, 9-8 (hardened), 16-7 |
| **Blocks** | 9-12 |
| **Estimated hours** | 24 (−8h — no cross-container auth handshake) |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-4-team-a-9-11-elsa` |
| **Branch** | `feat/story-9-11-diagnostics-queue-elsa` |
| **Key files** | `apps/tamma-elsa/src/Tamma.Activities/Diagnostics/*.cs` (rewrite), `apps/tamma-elsa/src/Tamma.Activities/Shared/TammaApiClient.cs` (new — wraps `HttpClient` + service-JWT) |
| **Deploy** | YES (single `tamma-api` container rebuild includes both activities and endpoints) |

### Story 9-12: Cross-Epic Integration Test

| Attribute | Value |
|-----------|-------|
| **Description** | End-to-end test that spans Epic 9 + 17 + 27 + 18: create tenant, register user, PUT agent config, resolve role, execute Elsa workflow, verify diagnostics persisted. |
| **Depends on** | 9-10, 9-11, 27-6, 18-3 |
| **Blocks** | Layer 5 |
| **Estimated hours** | 17 |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-4-team-a-9-12-integration-test` |
| **Branch** | `feat/story-9-12-cross-epic-integration` |
| **Deploy** | NO |
| **Story file** | `docs/stories/epic-9/story-9-12/9-12-cross-epic-integration-test.md` |

---

## Team B: Prompt Store UIs

**Agent**: 1 (or 2 to parallelize 27-4 and 27-5)

### Story 27-4: Prompt Store Admin UI

| Attribute | Value |
|-----------|-------|
| **Description** | Platform admin UI in `app.tamma.dev/admin/prompts` to manage the 80+8+10 system default prompts. Diff view, version history, import/export. Targets the C# prompt store endpoints (`/api/prompts/*`) — unchanged URL surface, but response shapes were regenerated in C# so verify DTO alignment. |
| **Depends on** | hardening task 1 (system defaults in C#), hardening task 6 (prompt render), 16-3 |
| **Blocks** | — |
| **Estimated hours** | 18 (+2h — DTO-shape alignment sweep across admin + user UIs) |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-4-team-b-27-4-admin-ui` |
| **Branch** | `feat/story-27-4-prompt-store-admin-ui` |
| **Deploy** | NO |
| **Story file** | `docs/stories/epic-27/27-4-prompt-store-admin-ui.md` |

### Story 27-5: Prompt Store Tenant UI

| Attribute | Value |
|-----------|-------|
| **Description** | Tenant admin UI in `dash.tamma.dev/prompts` (user-facing) to override system defaults for their tenant. Read-only view of system defaults; editable overrides. Shares the DTO-alignment work done by 27-4. |
| **Depends on** | 27-4 (DTO alignment), 18-5 (shell) |
| **Blocks** | — |
| **Estimated hours** | 16 |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-4-team-b-27-5-tenant-ui` |
| **Branch** | `feat/story-27-5-prompt-store-tenant-ui` |
| **Deploy** | NO |
| **Story file** | `docs/stories/epic-27/27-5-prompt-store-account-ui.md` |

**Coordination**: 27-5 depends on the user-facing dashboard shell (18-5, Team C). Start 27-5 only after 18-5 has merged its `packages/dashboard-user/` skeleton.

---

## Team C: Epic 18 Completion

**Agent**: 1

### Story 18-4: GitHub App Installation Onboarding

| Attribute | Value |
|-----------|-------|
| **Description** | Onboarding flow after org creation: redirect user to GitHub to install the Tamma GitHub App, select repos, callback links installation to tenant. Endpoints live in C#; React UI calls them from the user dashboard. The C# stub `GitHubEndpoints.Callback` + webhook already exist — this story fills in the real flow (exchange installation_id → InstallationRepo row → tenant link). |
| **Depends on** | 18-3 (C# `OrgEndpoints` — already in) |
| **Blocks** | 18-5 |
| **Estimated hours** | 24 (unchanged) |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-4-team-c-18-4-github-app` |
| **Branch** | `feat/story-18-4-github-app-onboarding` |
| **Key files** | `apps/tamma-elsa/src/Tamma.Api/Endpoints/GitHubEndpoints.cs` (flesh out Callback), `apps/tamma-elsa/src/Tamma.Data/Repositories/InstallationRepository.cs` (link-to-tenant method), `packages/dashboard-user/src/pages/onboarding/github.tsx` (UI) |
| **Deploy** | YES (GitHub App webhook URL, callback URL configuration in GitHub + `.env` on VPS) |
| **Story file** | `docs/stories/epic-18/18-4-github-app-installation-onboarding.md` |

### Story 18-5: User-Facing Dashboard Shell

| Attribute | Value |
|-----------|-------|
| **Description** | New React app at `dash.tamma.dev` — user-facing dashboard. Separate from admin dashboard at `app.tamma.dev`. Navigation, tenant switcher, profile, workflow run list, API key management. Backed entirely by the C# API. |
| **Depends on** | 18-2 (C# `AuthEndpoints` — already in), 18-3 (already in), hardening task 8 (email) for the profile flow |
| **Blocks** | 27-5 |
| **Estimated hours** | 40 (unchanged) |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-4-team-c-18-5-user-dashboard` |
| **Branch** | `feat/story-18-5-user-dashboard` |
| **Key files** | `packages/dashboard-user/` (new package), `nginx-proxy/conf.d/dash.tamma.dev.conf`, `docker/docker-compose.yml` (new dashboard-user service + oauth2-proxy upstream), `apps/tamma-elsa/src/Tamma.Api/Endpoints/Dashboard*.cs` (add user-scoped variants if admin-only ones don't fit) |
| **Deploy** | YES (new dashboard subdomain + nginx config + oauth2-proxy binding for `dash.tamma.dev`) |
| **Story file** | `docs/stories/epic-18/18-5-user-facing-dashboard-shell.md` |

---

## Team D: Epic 12 (Prompt Engineering + Context Tools)

**Agent**: 1 (preferably 2 — split 12-5 track from 12-7 track)
**Hours**: 156 total (+16 for C#/TS bridge)

### Cross-language bridge (pre-12-7e)

Context tools live in `packages/intelligence/` (TS). Elsa activities are C#.
Two viable bridges:

**Option A — HTTP proxy on C# API (recommended)**: add a `/api/v1/context/tools/*`
group on the C# API. Handlers forward to a lightweight TS sidecar
(`packages/intelligence-server/`) over localhost HTTP. The sidecar wraps
`packages/intelligence`. One extra hop on every tool call, but keeps the
vector DB code in TS where it lives today.

**Option B — port tools to C# (8h per tool × 5 tools = 40h)**. Rejected:
too much rewrite for features that already work in TS.

Option A is blessed. 12-7e (Elsa tool loop integration) consumes the proxy
endpoints. Budget: 16h for the proxy + sidecar skeleton, counted once and
amortized across 12-7a through 12-7e.

### Story 12-5a: Context Priority-Based Truncation

| Attribute | Value |
|-----------|-------|
| **Description** | Prompt context system that truncates lower-priority sections when token budget is exceeded. |
| **Depends on** | 27-2, 9-1 |
| **Blocks** | — |
| **Estimated hours** | 16 |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-4-team-d-12-5a-truncation` |
| **Branch** | `feat/story-12-5a-context-truncation` |
| **Deploy** | NO |
| **Story file** | `docs/stories/epic-12/story-12-5/12-5-prompt-engineering-framework.md` (sub-story 12-5a) |

### Story 12-5b: Few-Shot Example Injection

| Attribute | Value |
|-----------|-------|
| **Description** | Inject few-shot examples from vector DB into the prompt context based on task similarity. |
| **Depends on** | 27-2, ChromaDB (Epic 6) |
| **Blocks** | — |
| **Estimated hours** | 20 |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-4-team-d-12-5b-fewshot` |
| **Branch** | `feat/story-12-5b-few-shot` |
| **Deploy** | NO |
| **Story file** | `docs/stories/epic-12/story-12-5/12-5-prompt-engineering-framework.md` (sub-story 12-5b) |

### Story 12-5d: A/B Testing Hooks

| Attribute | Value |
|-----------|-------|
| **Description** | Emit events when a prompt variant is served so the diagnostics pipeline can track A/B outcomes. |
| **Depends on** | 27-1, 9-2 |
| **Blocks** | — |
| **Estimated hours** | 12 |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-4-team-d-12-5d-ab-testing` |
| **Branch** | `feat/story-12-5d-ab-testing` |
| **Deploy** | NO |
| **Story file** | `docs/stories/epic-12/story-12-5/12-5-prompt-engineering-framework.md` (sub-story 12-5d) |

### Story 12-7a: Vector DB Search Tools

| Attribute | Value |
|-----------|-------|
| **Description** | Implement `search_code_semantic`, `search_findings`, `search_stories` tools that call ChromaDB via the Intelligence package. |
| **Depends on** | Epic 6 (6-2, 6-3) |
| **Blocks** | 12-7c, 12-7e |
| **Estimated hours** | 24 |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-4-team-d-12-7a-vector-tools` |
| **Branch** | `feat/story-12-7a-vector-search-tools` |
| **Deploy** | NO |
| **Story file** | `docs/stories/epic-12/story-12-7/12-7a-vector-db-search-tools.md` |

### Story 12-7b: Convention & History Tools

| Attribute | Value |
|-----------|-------|
| **Description** | `search_conventions` and `search_history` tools. Conventions from `convention-templates.ts`; history from the event store. |
| **Depends on** | 27-2, event store (17-3) |
| **Blocks** | 12-7c, 12-7e |
| **Estimated hours** | 16 |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-4-team-d-12-7b-convention-history` |
| **Branch** | `feat/story-12-7b-convention-history` |
| **Deploy** | NO |
| **Story file** | `docs/stories/epic-12/story-12-7/12-7b-convention-and-history-tools.md` |

### Story 12-7c: Context Budget Manager

| Attribute | Value |
|-----------|-------|
| **Description** | Tracks cumulative token usage across tool results and enforces per-provider limits. |
| **Depends on** | 12-7a, 12-7b, 9-1 |
| **Blocks** | 12-7e |
| **Estimated hours** | 20 |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-4-team-d-12-7c-budget` |
| **Branch** | `feat/story-12-7c-context-budget` |
| **Deploy** | NO |
| **Story file** | `docs/stories/epic-12/story-12-7/12-7c-context-budget-manager.md` |

### Story 12-7d: Tool Access Configuration Per Role

| Attribute | Value |
|-----------|-------|
| **Description** | Agent config specifies which tools each role can invoke. Enforced by the tool registry. |
| **Depends on** | 12-7a, 12-7b, 27-1 |
| **Blocks** | 12-7e |
| **Estimated hours** | 12 |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-4-team-d-12-7d-access-config` |
| **Branch** | `feat/story-12-7d-tool-access-config` |
| **Deploy** | NO |
| **Story file** | `docs/stories/epic-12/story-12-7/12-7d-tool-access-config-per-role.md` |

### Story 12-7e: Elsa Tool Loop Integration

| Attribute | Value |
|-----------|-------|
| **Description** | Wire the tool loop into Elsa's `CallLlmInlineActivity`. When `EnableToolLoop=true`, the activity calls the C# API `/api/v1/context/tools/*` proxy endpoints (see "Cross-language bridge" above), which forwards to the TS sidecar wrapping `packages/intelligence`. |
| **Depends on** | 12-7a, 12-7b, 12-7c, 12-7d, **cross-language bridge** |
| **Blocks** | — |
| **Estimated hours** | 28 (+8h — bridge integration + fallback behavior when sidecar is offline) |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-4-team-d-12-7e-elsa-loop` |
| **Branch** | `feat/story-12-7e-elsa-tool-loop` |
| **Key files** | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/AgenticToolLoop*.cs` (already exists — add bridge-aware tool invocation), `apps/tamma-elsa/src/Tamma.Api/Endpoints/ContextEndpoints.cs` (new proxy), `packages/intelligence-server/` (new TS sidecar package) |
| **Deploy** | YES (C# container rebuild + new sidecar service in `docker-compose.yml`) |
| **Story file** | `docs/stories/epic-12/story-12-7/12-7e-elsa-tool-loop-integration.md` |

---

## Integration Checkpoint

At the end of Layer 4:

1. All stories merged to `main`
2. CI green
3. End-to-end smoke test:
   - User registers → creates org → installs GitHub App → sees repos in dashboard
   - User edits a prompt override in `dash.tamma.dev/prompts`
   - User kicks off a workflow run; Elsa calls the tool loop; diagnostics recorded; result visible in user dashboard
4. Deploy Coordinator does a full staging deploy (API + Elsa + both dashboards + oauth2-proxy config for `dash.tamma.dev`)
5. Feature flags gating self-service registration can be enabled on staging

## Rollback Considerations

- 12-7e tool loop behind `EnableToolLoop` flag (default OFF) — safe to ship
- 9-9 engine integration: can fall back to old in-process resolver with a feature flag
- 9-11 Elsa activities: dual-write period possible (call API + old code path, compare)
- 18-5 user dashboard: served from separate subdomain, independent rollback

## Handoff to Layer 5

Layer 5 assumes:

- Every story from Epics 9, 12, 16, 17, 18, 27 is merged
- All migrations (008–017) applied on staging
- No outstanding `deploy requirement` items
- Green CI on `main`

---

**Next**: [`layer-5-validation.md`](./layer-5-validation.md)
