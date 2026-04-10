# Layer 4: Integration & UI

**Duration**: wall-clock ~140 hours (Team D critical path), ~343 total hours
**Teams**: 4 parallel teams (A, B, C, D)
**Goal**: Complete Epic 9 end-to-end (chain, resolver, engine integration, CLI wiring, Elsa integration, integration test), build the prompt store UIs, finish Epic 18 with onboarding + user dashboard, and deliver Epic 12 prompt-engineering + context-tool features.

**Prerequisite**: Layer 3 merged to `main`. All Epic 9 foundations + Prompt Store API + Epic 18 backend live.

## Team Overview

| Team | Focus | Stories | Worktree | Hours |
|------|-------|---------|----------|-------|
| **A** | Epic 9 completion | 9-5, 9-9, 9-10, 9-11, 9-12 | `layer-4-team-a-epic-9-completion` | 89 |
| **B** | Prompt Store UIs | 27-4, 27-5 | `layer-4-team-b-prompt-ui` | 32 |
| **C** | Epic 18 completion | 18-4, 18-5 | `layer-4-team-c-epic-18-ui` | 64 |
| **D** | Epic 12 (prompt engineering + context tools) | 12-5a, 12-5b, 12-5d, 12-7a, 12-7b, 12-7c, 12-7d, 12-7e | `layer-4-team-d-epic-12` | 140 |

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

**Agent**: 1 (or 2 to pipeline 9-5 + 9-9 after 9-8)
**Order**: 9-5 → 9-9 → 9-10 (CLI) || 9-11 (Elsa) → 9-12 (integration test)

### Story 9-5: Provider Chain API

| Attribute | Value |
|-----------|-------|
| **Description** | `POST /api/v1/providers/chain/resolve` — returns ordered list of providers to try based on health state and config. |
| **Depends on** | 9-2, 9-3, 9-4, 9-8 |
| **Blocks** | 9-9, 9-11, 9-12 |
| **Estimated hours** | 14 |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-4-team-a-9-5-chain` |
| **Branch** | `feat/story-9-5-provider-chain-api` |
| **Deploy** | NO |
| **Story file** | `docs/stories/epic-9/story-9-5/9-5-provider-chain.md` |

### Story 9-9: Engine Integration

| Attribute | Value |
|-----------|-------|
| **Description** | TypeScript engine (`packages/orchestrator/`) calls `/api/v1/agents/*/resolve` instead of in-process factory. |
| **Depends on** | 9-8, 9-5 |
| **Blocks** | 9-10 |
| **Estimated hours** | 14 |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-4-team-a-9-9-engine` |
| **Branch** | `feat/story-9-9-engine-integration` |
| **Deploy** | NO |
| **Story file** | `docs/stories/epic-9/story-9-9/9-9-engine-integration.md` |

### Story 9-10: CLI Wiring

| Attribute | Value |
|-----------|-------|
| **Description** | CLI mode uses in-memory/file fallbacks per `cli-fallback-behavior.md`. Auto-detects Postgres availability. |
| **Depends on** | 9-1, 9-9, 9-11 |
| **Blocks** | 9-12 |
| **Estimated hours** | 12 |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-4-team-a-9-10-cli` |
| **Branch** | `feat/story-9-10-cli-wiring` |
| **Deploy** | NO |
| **Story file** | `docs/stories/epic-9/story-9-10/9-10-cli-wiring.md` |

**Key files**:
- `packages/cli/src/config.ts` — fallback detection logic
- `packages/cli/src/bootstrap.ts` — service container wiring per `cli-fallback-behavior.md`

### Story 9-11: Diagnostics Queue + Elsa Integration

| Attribute | Value |
|-----------|-------|
| **Description** | Replace 5 C# activities (`CheckCircuitBreakerActivity`, `RecordDiagnosticsActivity`, `ResolveAgentConfigActivity`, `CheckBudgetActivity`, `CallLlmActivity`) with HTTP calls to the Fastify API. Revised to 32h from 20h for 5-activity scope. |
| **Depends on** | 9-2, 9-3, 9-5, 9-8, 16-7 |
| **Blocks** | 9-12 |
| **Estimated hours** | 32 |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-4-team-a-9-11-elsa` |
| **Branch** | `feat/story-9-11-diagnostics-queue-elsa` |
| **Deploy** | YES (Elsa container redeploy) |
| **Story file** | `docs/stories/epic-9/story-9-11/9-11-diagnostics-queue-mcp-interceptors.md` |

**Key files**:
- `apps/tamma-elsa/.../Activities/CheckCircuitBreakerActivity.cs` (simplified)
- `apps/tamma-elsa/.../Activities/RecordDiagnosticsActivity.cs` (simplified)
- `apps/tamma-elsa/.../Activities/ResolveAgentConfigActivity.cs` (simplified)
- `apps/tamma-elsa/.../Activities/CheckBudgetActivity.cs` (simplified)
- `apps/tamma-elsa/.../Activities/CallLlmActivity.cs` (HTTP delegated)
- `apps/tamma-elsa/.../HttpClients/TammaApiClient.cs` — shared client with service JWT auth

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
| **Description** | Platform admin UI in `app.tamma.dev/admin/prompts` to manage the 80+8+10 system default prompts. Diff view, version history, import/export. |
| **Depends on** | 27-3, 16-3 |
| **Blocks** | — |
| **Estimated hours** | 16 |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-4-team-b-27-4-admin-ui` |
| **Branch** | `feat/story-27-4-prompt-store-admin-ui` |
| **Deploy** | NO |
| **Story file** | `docs/stories/epic-27/27-4-prompt-store-admin-ui.md` |

### Story 27-5: Prompt Store Tenant UI

| Attribute | Value |
|-----------|-------|
| **Description** | Tenant admin UI in `dash.tamma.dev/prompts` (user-facing) to override system defaults for their tenant. Read-only view of system defaults; editable overrides. |
| **Depends on** | 27-3, 18-5 (preferred shell) |
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
| **Description** | Onboarding flow after org creation: redirect user to GitHub to install the Tamma GitHub App, select repos, callback links installation to tenant. |
| **Depends on** | 18-3 |
| **Blocks** | 18-5 |
| **Estimated hours** | M (~24) |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-4-team-c-18-4-github-app` |
| **Branch** | `feat/story-18-4-github-app-onboarding` |
| **Deploy** | YES (GitHub App webhook URL, callback URL configuration) |
| **Story file** | `docs/stories/epic-18/18-4-github-app-installation-onboarding.md` |

**Key files**:
- `packages/api/src/routes/onboarding/github-app.ts`
- `packages/api/src/persistence/installation-store.ts` — link to tenant

### Story 18-5: User-Facing Dashboard Shell

| Attribute | Value |
|-----------|-------|
| **Description** | New React app at `dash.tamma.dev` — user-facing dashboard. Separate from admin dashboard at `app.tamma.dev`. Navigation, tenant switcher, profile, workflow run list, API key management. |
| **Depends on** | 18-2, 18-3 |
| **Blocks** | 27-5 |
| **Estimated hours** | L (~40) |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-4-team-c-18-5-user-dashboard` |
| **Branch** | `feat/story-18-5-user-dashboard` |
| **Deploy** | YES (new dashboard subdomain + nginx config + oauth2-proxy binding for dash.tamma.dev) |
| **Story file** | `docs/stories/epic-18/18-5-user-facing-dashboard-shell.md` |

**Key files**:
- `packages/dashboard-user/` — new package
- `nginx-proxy/conf.d/dash.tamma.dev.conf`
- `docker-compose.yml` — new dashboard service + oauth2-proxy binding

---

## Team D: Epic 12 (Prompt Engineering + Context Tools)

**Agent**: 1 (preferably 2 — split 12-5 track from 12-7 track)
**Hours**: 140 total

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
| **Description** | Wire the tool loop into Elsa's `CallLlmInlineActivity`. When `EnableToolLoop=true`, the activity runs the loop calling the context tools. |
| **Depends on** | 12-7a, 12-7b, 12-7c, 12-7d |
| **Blocks** | — |
| **Estimated hours** | 20 |
| **Git worktree** | `/home/meywd/tamma-worktrees/layer-4-team-d-12-7e-elsa-loop` |
| **Branch** | `feat/story-12-7e-elsa-tool-loop` |
| **Deploy** | YES (Elsa container redeploy) |
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
