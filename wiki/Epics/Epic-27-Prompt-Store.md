# Epic 27: Prompt Store — Multi-Tenant Prompt Management

**Status:** 5 of 7 stories shipped (27-1 schema, 27-2 service, 27-3 API, 27-4 admin UI, 27-6 Elsa integration, 27-7 event sourcing). 27-5 tenant UI still drafted.
**Stories:** 7 (27-1 through 27-7)
**Layer:** Layer 4 (integration / UI)
**Packages:** `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/`, `apps/tamma-elsa/src/Tamma.Data/Entities/PromptOverride.cs`, `apps/tamma-elsa/src/Tamma.Data/Repositories/PromptRepository.cs`, `apps/tamma-elsa/src/Tamma.Api/Endpoints/PromptEndpoints.cs`, `packages/dashboard/src/pages/admin/prompts/`, `packages/dashboard/src/components/prompts/`

## Overview

Epic 27 is the prompt management plane. Every Elsa workflow that calls an LLM resolves its prompt through the prompt store; system defaults ship in code (`SystemPrompts`), tenant overrides live in Postgres, and the 4-layer resolution order (tenant role+action → system role+action → tenant action-default → system action-default) gives each tenant a safe place to tune its prompts without touching the defaults everyone else depends on.

The 6-week Wave-A sprint delivered the schema, service, API, Elsa integration, event sourcing, and — as of 2026-04-22 — the full admin UI (Story 27-4). The one remaining story is the per-tenant UI (27-5) where tenant-admins edit their overrides in a non-admin dashboard shell. Convention templates (20 language/framework starters) remain static in code by design.

This epic also **supersedes** Epic 9 Story 9-6 (Agent Prompt Registry). The in-process `AgentPromptRegistry` at `packages/providers/src/agent-prompt-registry.ts` delegates to the Prompt Store API; Epic 9 Story 9-8 (Unified Agent Resolver) depends on Epic 27.

## Architecture

### Four-layer resolution

```
 Request: (userId/tenantId, role, action)

   Layer 1:  User/Tenant override for (role, action)            ← prompt_overrides
      │     scope = 'role-action'
      │     found? → use it
      ▼ not found
   Layer 2:  System default for (role, action)                  ← SystemPrompts.RoleActionTemplates
      │     shipped in code (80 = 8 roles × 10 actions)
      │     found? → use it
      ▼ not found
   Layer 3:  User/Tenant override for action-default            ← prompt_overrides
      │     scope = 'action-default'
      │     found? → use it
      ▼ not found
   Layer 4:  System action default                              ← SystemPrompts.ActionDefaults
            shipped in code (10 actions — safety net)
```

System prompt (role identity) uses a **2-layer** variant:

```
   Layer 1:  User/Tenant override for role-system              ← prompt_overrides, scope='role-system'
   Layer 2:  System role identity                              ← SystemPrompts.RoleSystemPrompts (8 roles)
```

### Storage (C# / EF Core, Postgres)

A single `prompt_overrides` table carries all three scopes:

| Column | Type | Notes |
|--------|------|-------|
| `id` | `uuid` PK | `gen_random_uuid()` |
| `user_id` | `uuid` NULL | `NULL` = tenant-wide override; non-NULL = user-scoped override |
| `tenant_id` | `uuid` NULL | Tenant FK; `NULL` = sentinel self-hosted mode |
| `scope` | `text` | `'role-system'`, `'action-default'`, or `'role-action'` |
| `role` | `text` NULL | `NULL` for `action-default` |
| `action` | `text` NULL | `NULL` for `role-system` |
| `template` | `text` | User-prompt body |
| `system_prompt` | `text` NULL | System-prompt preamble |
| `variables` | `text[]` | Declared template variables |
| `enable_tools` | `bool` | Enables LLM tool use |
| `max_tokens` | `int` | CHECK (`> 0`), default 4096 |
| `version` | `int` | Optimistic concurrency token; increments per edit |
| `created_by`, `updated_by` | `uuid` NULL | Audit — user id of creator / last updater |
| `created_at`, `updated_at` | `timestamptz` | Auto-maintained |
| UNIQUE `(user_id, scope, role, action)` | | One override per (user, scope, role, action) |

**No RLS on prompt tables** — resolution deliberately crosses tenant boundaries when falling back to system defaults. Application-level filtering enforces isolation; see Epic 17 Story 17-2 for the RLS exemption list.

### System defaults (code-shipped, immutable)

`apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/SystemPrompts.cs`:

- **`RoleSystemPrompts`** — dictionary of 8 role-identity preambles (architect, implementer, reviewer, tester, mentor, debugger, scrum-master, triage).
- **`RoleActionTemplates`** — 80 role+action pairs (8 roles × 10 actions).
- **`ActionDefaults`** — 10 action-level safety-net templates used when no role+action template exists.

Convention templates (20 language/framework starters in `convention-templates.ts`) stay static and are injected via the `{{conventions}}` variable from per-repo `.tamma/config.json`.

## Components

### Backend

| Component | Location | Responsibility |
|-----------|----------|----------------|
| `PromptStoreService` | `Tamma.Api/Services/PromptStore/PromptStoreService.cs` | Resolve, render, upsert, delete, list. Implements the 4-layer and 2-layer resolution. |
| `PromptEventsService` | `Tamma.Api/Services/PromptStore/PromptEventsService.cs` | Appends DCB events: `PROMPT.UPSERT.SUCCESS`, `PROMPT.RESET.SUCCESS`, `PROMPT.RESOLVE.CALLED`, `PROMPT.RENDER.CALLED`. |
| `SystemPrompts` | same folder | Static registry of role identities (8), role+action templates (80), action defaults (10). |
| `PromptOverride` entity | `Tamma.Data/Entities/PromptOverride.cs` | EF Core entity. |
| `PromptRepository` | `Tamma.Data/Repositories/PromptRepository.cs` | Routes through `TammaAppDbContext` (tenant-aware app role) per Epic 19 Story 19-6. |
| `PromptEndpoints` | `Tamma.Api/Endpoints/PromptEndpoints.cs` | Minimal-API handlers wired in `Program.cs`. |
| `PromptDtos` | `Tamma.Api/Dtos/Prompts/PromptDtos.cs` | `UpsertPromptRequest`, `PromptResponse`, `RenderedPromptResponse`, `SystemDefaultsResponse`. |
| `PromptStoreServiceCollectionExtensions` | `Tamma.Api/Extensions/PromptStoreServiceCollectionExtensions.cs` | DI wiring. |
| `ResolvePromptFromRegistryActivity` | `Tamma.Activities/Prompts/` | Elsa activity; consumed by `LlmCallWorkflow`. |

### API surface

```
GET    /api/prompts                         — list resolved prompts for current user/tenant
GET    /api/prompts/:role/:action           — get resolved prompt (+ source layer)
PUT    /api/prompts/:role/:action           — upsert override (auth: self or admin)
DELETE /api/prompts/:role/:action           — reset to system default
POST   /api/prompts/:role/:action/reset     — alias for DELETE
POST   /api/prompts/:role/:action/render    — interpolate template with variables
GET    /api/prompts/defaults                — bulk system defaults (role+action, system, action-default)
GET    /api/prompts/defaults/:action        — single action default
GET    /api/prompts/defaults/:role/:action  — single role+action default
GET    /api/prompts/system/:role            — system prompt for role
PUT    /api/prompts/system/:role            — upsert system-prompt override
DELETE /api/prompts/system/:role            — reset system-prompt override
GET    /api/prompts/action/:action          — resolved action default
PUT    /api/prompts/action/:action          — upsert action-default override
DELETE /api/prompts/action/:action          — reset action-default override
GET    /api/convention-templates            — list all 20 convention starters (static)
GET    /api/convention-templates/:key       — single template with full conventions string
```

All write endpoints require admin authority for tenant-wide scope; read endpoints are tenant-scoped. All endpoints are rate-limited (read 100/min/tenant, write 30/min/tenant, `render` 300/min/tenant).

### Frontend (`packages/dashboard/src/pages/admin/prompts/` — Story 27-4)

| Component | File | Responsibility |
|-----------|------|----------------|
| `PromptsAdminPage` | `PromptsAdminPage.tsx` | Tabbed shell. Mirrors `AdminLayout` + `OrganizationLayout` pattern. RBAC-gated via `AdminGuard`. |
| Templates tab | `PromptTable.tsx` | 80-cell role × action matrix; click cell → edit dialog. |
| `PromptEditDialog` | `PromptEditDialog.tsx` | Template editor with syntax-highlighted template, system-prompt preamble, variable chips, max-tokens input. |
| System Prompts tab | `SystemPromptEditor.tsx` | 8 role identities — override or reset per role. |
| Action Defaults tab | `ActionDefaultsList.tsx` | 10 safety-net templates (read-only v1). |
| Conventions tab | `ConventionPreview.tsx` | 20 convention-template browser (read-only). |
| Supporting components | `TemplateEditor.tsx`, `VariableChips.tsx`, `extract-variables.ts`, `prompt-constants.ts` | Parse template, extract `{{vars}}`, enumerate known actions/roles. |
| `useSystemPrompts` hook | `hooks/admin/useSystemPrompts.ts` | Loads resolved prompts + system defaults, exposes `upsertOverride` / `resetOverride` mutations. |

### Frontend (tenant UI, 27-5 — drafted)

A separate page in the tenant user-dashboard shell (`packages/dashboard/src/pages/user/prompts/`) will expose the same editing surface without admin gating — but only for the tenant's own overrides, never system defaults.

## Class diagram

```
                                ┌──────────────────────────────────┐
                                │      PromptStoreService           │
                                │ ResolveRoleActionAsync()          │
                                │ ResolveRoleSystemAsync()          │
                                │ ResolveActionDefaultAsync()       │
                                │ RenderAsync()                     │
                                │ UpsertOverrideAsync()             │
                                │ ResetOverrideAsync()              │
                                │ ListUserOverridesAsync()          │
                                └─────┬────────────────────────┬────┘
                                      │ uses                    │ uses
                            ┌─────────▼─────────┐    ┌──────────▼───────────┐
                            │  PromptRepository  │    │  SystemPrompts       │
                            │  (TammaAppDbContext)│    │  (static, code-only) │
                            │  FindOverride(...)  │    │  RoleSystemPrompts[8]│
                            │  InsertOverride(...) │    │  RoleActionTemplates[80]│
                            │  UpsertOverride(...) │    │  ActionDefaults[10]  │
                            │  DeleteOverride(...) │    └──────────────────────┘
                            └─────────┬───────────┘
                                      │ persists
                            ┌─────────▼──────────┐
                            │ PromptOverride     │
                            │ (prompt_overrides) │
                            │ scope + role + act │
                            │ version (OCC)      │
                            └────────────────────┘

       ┌───────────────────────────┐
       │  PromptEventsService       │
       │  OnUpsert / OnReset /      │
       │  OnResolve / OnRender      │
       │  → DCB event stream        │
       └───────────────────────────┘

 Consumers
 ┌──────────────────────────┐         ┌──────────────────────────┐
 │ ResolvePromptFromRegistry│         │ PromptEndpoints          │
 │ Activity (Elsa)          │────▶    │ HTTP + auth + rate-limit │
 │ called by LlmCallWorkflow│         └──────────────────────────┘
 └──────────────────────────┘

 Admin UI
 PromptsAdminPage
    ├─ PromptTable        (80-cell matrix)
    ├─ SystemPromptEditor (8 identities)
    ├─ ActionDefaultsList (10 safety-net)
    └─ ConventionPreview  (20 starters)
```

## Sequence diagram — LLM call resolves and renders

```
LlmCallWorkflow   ResolvePromptActivity   PromptStoreService   PromptRepo   SystemPrompts   PromptEvents
       │                   │                     │                 │             │               │
       │ Resolve(role,act) │                     │                 │             │               │
       │──────────────────▶│                     │                 │             │               │
       │                   │ Resolve(role,act)   │                 │             │               │
       │                   │────────────────────▶│                 │             │               │
       │                   │                     │ Find(tenant,    │             │               │
       │                   │                     │   role,act)     │             │               │
       │                   │                     │────────────────▶│             │               │
       │                   │                     │ (not found)     │             │               │
       │                   │                     │◀────────────────│             │               │
       │                   │                     │ Get(role,act)   │             │               │
       │                   │                     │──────────────────────────────▶│               │
       │                   │                     │ system default                 │               │
       │                   │                     │◀──────────────────────────────│               │
       │                   │                     │ append PROMPT.RESOLVE.CALLED  │               │
       │                   │                     │───────────────────────────────────────────────▶
       │                   │ ResolvedPrompt      │                 │             │               │
       │                   │◀────────────────────│                 │             │               │
       │                   │ Render(template,vars)                 │             │               │
       │                   │ (interpolate {{...}})                 │             │               │
       │                   │ append PROMPT.RENDER.CALLED           │             │               │
       │                   │───────────────────────────────────────────────────────────────────▶ │
       │ {systemPrompt,    │                     │                 │             │               │
       │  userPrompt,      │                     │                 │             │               │
       │  unresolved[]}    │                     │                 │             │               │
       │◀──────────────────│                     │                 │             │               │
       │ ask LLM           │                     │                 │             │               │
```

## Use cases

1. **Workflow calls LLM** — `LlmCallWorkflow` resolves `(tenantId, role, action)`, gets either a tenant override or system default, interpolates `{{issueTitle}}`, `{{conventions}}`, etc., emits `PROMPT.RESOLVE.CALLED` + `PROMPT.RENDER.CALLED` events, sends to provider.
2. **Platform admin edits a system default** — on `/admin/prompts`, selects architect × plan-generation, edits the template, saves. The mutation upserts a `PromptOverride` for the admin's user id with scope `role-action`; emits `PROMPT.UPSERT.SUCCESS`.
3. **Tenant admin customises triage prompt (27-5)** — on `/user/prompts`, selects triage × classify-issue, edits template; the upsert is scoped to the tenant; other tenants unaffected.
4. **Reset to default** — admin clicks "Reset"; `DELETE /api/prompts/:role/:action` removes the override row; next resolve falls back through Layer 2.
5. **Render-only preview** — UI calls `POST /render` with sample variables to show the admin what the interpolated prompt looks like before saving.
6. **Bulk default read** — agent bootstrap reads `GET /api/prompts/defaults` once at startup to cache the 80+8+10 snapshot.
7. **Optimistic concurrency protection** — two admins edit the same cell; the second save fails with a 409 (stale `version`); UI surfaces "this prompt was edited by someone else — reload and retry".
8. **Audit trail** — every upsert/reset emits an event; `/admin/events` can search by `PROMPT.*` to answer "who last changed this template and when?"
9. **Convention starters** — tenant picks a Python + Django convention template from `/api/convention-templates`, saves it as `conventions` in `.tamma/config.json`; `LlmCallWorkflow` injects it into every prompt via `{{conventions}}`.

## Stories

| # | Title | Priority | Effort | Status |
|---|-------|----------|--------|--------|
| 27-1 | [Database Schema + Migration](https://github.com/meywd/tamma/blob/main/docs/stories/epic-27/27-1-prompt-store-database-schema.md) | P0 | 10h | **Done** |
| 27-2 | [Prompt Store Service](https://github.com/meywd/tamma/blob/main/docs/stories/epic-27/27-2-prompt-store-service.md) | P0 | 14h | **Done** (ported to C#) |
| 27-3 | [Prompt Store API Endpoints](https://github.com/meywd/tamma/blob/main/docs/stories/epic-27/27-3-prompt-store-api-endpoints.md) | P0 | 12h | **Done** |
| 27-4 | [Prompt Store Admin UI](https://github.com/meywd/tamma/blob/main/docs/stories/epic-27/27-4-prompt-store-admin-ui.md) | P1 | 16h | **Done (2026-04-22)** |
| 27-5 | [Prompt Store Tenant/Account UI](https://github.com/meywd/tamma/blob/main/docs/stories/epic-27/27-5-prompt-store-account-ui.md) | P1 | 16h | Drafted |
| 27-6 | [Elsa Workflow Integration](https://github.com/meywd/tamma/blob/main/docs/stories/epic-27/27-6-elsa-workflow-integration.md) | P0 | 10h | **Done** |
| 27-7 | [Prompt Store Event Sourcing](https://github.com/meywd/tamma/blob/main/docs/stories/epic-27/27-7-prompt-store-event-sourcing.md) | P1 | 8h | **Done** |

**Total**: 86h scoped; ~72h shipped; ~16h remaining for 27-5.

## Dependencies

**Upstream**:
- [Epic 17 — Multi-Tenancy](Epic-17-Multi-Tenancy.md) — `tenants` table for `tenant_id` FK.
- [Epic 28 — DB-per-Tenant](Epic-28-DB-Per-Tenant.md) — DbContext factory when db-per-tenant lands; pre-28 prompt_overrides lives in shared Postgres.
- [Epic 19 Story 19-6](Epic-19-Agent-Dispatch.md) — `TammaAppDbContext` app-role context (wired for the Prompt repository via commit `aeb9bbe4`).

**Downstream**:
- Epic 9 Story 9-8 (Unified Agent Resolver) — consumes 27-2 / 27-3 for prompt resolution.
- Epic 12 Story 12-7b (Convention & History Tools) — depends on 27 for prompt fetching + event-store search.
- Every Elsa workflow calling `LlmCallWorkflow` — indirect dependency via `ResolvePromptFromRegistryActivity`.

## Current state

- **Shipped**: `PromptStoreService`, `PromptEventsService`, `PromptOverride` entity, `PromptRepository` (routed via `TammaAppDbContext`), `PromptEndpoints` full API surface, `ResolvePromptFromRegistryActivity` (Elsa integration), prompt events (`PROMPT.UPSERT.SUCCESS`, `PROMPT.RESET.SUCCESS`, `PROMPT.RESOLVE.CALLED`, `PROMPT.RENDER.CALLED`), admin UI with 4 tabs (templates / system prompts / action defaults / conventions), all backed by `useSystemPrompts` hook.
- **Drafted**: 27-5 tenant UI — same editing surface in the user-dashboard shell, tenant-scoped, no admin gating.
- **Recent cleanups** (2026-04 audit): JSON property naming aligned (`docs/audit/port-gaps/prompts/013`), 4-layer resolution confirmed (`012`), unique constraint added (`011`), `created_by`/`updated_by` audit columns restored (`010`), `variables` moved from JSONB back to `text[]` (`009`), action-default layer ported from TS original (`008`), dead emit methods removed (`007`), defaults endpoints added (`006`).
- **Open questions**:
  - Convention-template per-tenant migration: left in code for v1; re-open when a tenant requests per-tenant overrides.
  - Provider dimension: `provider` column is not added; defer until at least one tenant needs provider-specific prompt tuning.
  - Prompt versioning vs event sourcing (27-7): `version` on the row increments per edit, but historical bodies live in the event log — not in a separate versions table.

## See also

- [Epic 9 — Agent Management](Epic-9-Agent-Management.md) — Stories 9-6, 9-8 superseded by this epic.
- [Epic 17 — Multi-Tenancy](Epic-17-Multi-Tenancy.md) — tenant model.
- [Epic 28 — DB-per-Tenant](Epic-28-DB-Per-Tenant.md) — future DbContext factory for per-tenant DBs.
- [Epic 19 — Agent Dispatch](Epic-19-Agent-Dispatch.md) — Story 19-6 wires the app-role DbContext used by PromptRepository.
- [Workflow — LLM Call](Workflow-LLM-Call) — the consumer that resolves + renders prompts.
- [Roadmap](Roadmap.md) — placement in the overall plan.

## Story files

[Epic 27 stories on GitHub](https://github.com/meywd/tamma/tree/main/docs/stories/epic-27)

---

_Last updated: 2026-04-22_
