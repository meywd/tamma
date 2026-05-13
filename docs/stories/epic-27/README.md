# Epic 27: Prompt Store — Multi-Tenant Prompt Management

## Overview

**Goal**: Replace the file-based, single-tenant `PromptStore` with a PostgreSQL-backed, multi-tenant prompt management system that supports system defaults, tenant-level overrides, and full audit trails.

**Value Delivered**:
- Multi-tenant prompt isolation: each tenant (organization) sees its own prompts without cross-tenant leakage
- Two-tier resolution: tenant overrides take precedence over system defaults, with transparent fallback
- Platform admin control over the 80 system default role+action templates, 8 role system prompts, and 20 convention templates
- Tenant admin self-service for customizing prompts without touching system defaults
- Full audit trail via DCB events for every prompt change (who, when, what)
- Elsa workflows resolve prompts per-tenant, enabling different organizations to use different prompt strategies
- Admin UI and Tenant UI for managing prompts without API calls

**Why Now**: The current `PromptStore` is an in-memory Map backed by a single JSON file. It has no concept of tenants, no database persistence, and no multi-tenant isolation. As Tamma moves to SaaS with multiple GitHub App installations (Epic 17), prompts must be scoped to tenants so that one customer's customizations do not affect another.

**Supersedes Epic 9 Story 9-6**: This epic absorbs the original Epic 9 Story 9-6 (Agent Prompt Registry). The original story defined an in-process `AgentPromptRegistry` class with a 6-level resolution chain and `{{variable}}` template interpolation. Epic 27 subsumes that functionality with Postgres-backed storage, multi-tenant isolation, and a provider dimension on prompt resolution. The existing `AgentPromptRegistry` class at `packages/providers/src/agent-prompt-registry.ts` will be updated to delegate to the Prompt Store API (Story 27-2/27-3) instead of resolving from in-memory config. Epic 9 Story 9-8 (Unified Agent Resolver) depends on Epic 27 for prompt resolution.

## Architecture

### Data Model

Three PostgreSQL tables store all prompt data:

```
prompts
├── id              UUID PK (gen_random_uuid())
├── tenant_id      UUID NULL (NULL = system default, non-NULL = tenant override)
├── role            TEXT NOT NULL
├── action          TEXT NOT NULL
├── template        TEXT NOT NULL
├── system_prompt   TEXT NOT NULL DEFAULT ''
├── variables       JSONB NOT NULL DEFAULT '[]'
├── enable_tools    BOOLEAN NOT NULL DEFAULT false
├── max_tokens      INTEGER NOT NULL DEFAULT 4096
├── version         INTEGER NOT NULL DEFAULT 1
├── created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
├── updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
├── created_by      UUID NULL (FK to users)
├── updated_by      UUID NULL (FK to users)
└── UNIQUE (tenant_id, role, action)   -- one prompt per role+action per tenant

system_prompts
├── id              UUID PK (gen_random_uuid())
├── tenant_id      UUID NULL (NULL = system default)
├── role            TEXT NOT NULL
├── prompt          TEXT NOT NULL
├── version         INTEGER NOT NULL DEFAULT 1
├── created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
├── updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
├── created_by      UUID NULL
├── updated_by      UUID NULL
└── UNIQUE (tenant_id, role)            -- one system prompt per role per tenant

action_prompts
├── id              UUID PK (gen_random_uuid())
├── tenant_id      UUID NULL (NULL = system default)
├── action          TEXT NOT NULL
├── template        TEXT NOT NULL
├── variables       JSONB NOT NULL DEFAULT '[]'
├── enable_tools    BOOLEAN NOT NULL DEFAULT false
├── max_tokens      INTEGER NOT NULL DEFAULT 4096
├── version         INTEGER NOT NULL DEFAULT 1
├── created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
├── updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
├── created_by      UUID NULL
├── updated_by      UUID NULL
└── UNIQUE (tenant_id, action)          -- one action template per action per tenant
```

### Resolution Logic

When resolving a prompt for `(tenantId, role, action)`:

1. Look for a tenant-specific row in `prompts` WHERE `tenant_id = :tenantId AND role = :role AND action = :action`
2. If not found, fall back to the system default WHERE `tenant_id IS NULL AND role = :role AND action = :action`
3. The system prompt is resolved similarly from `system_prompts`
4. Action defaults are resolved from `action_prompts`

```
Tenant Override (tenant_id = 'acme-uuid')
        │
        │ found? → use it
        │
        ▼ not found?
System Default (tenant_id IS NULL)
        │
        │ found? → use it
        │
        ▼ not found?
Return 404
```

### Table Roles

| Table | Purpose | Row Count (Initial) |
|-------|---------|-------------------|
| `prompts` | Role+action templates with full template body | 80 system defaults (8 roles x 10 actions) |
| `system_prompts` | Role identity preambles (system prompt per role) | 8 system defaults |
| `action_prompts` | Action-level default templates (no role specificity) | 10 system defaults |

### Provider Dimension

The `prompts` table supports a **provider dimension** inherited from the original Epic 9 Story 9-6 resolution chain. When resolving a prompt for `(tenantId, role, action)`, the resolution can also consider the target provider (e.g., Anthropic prompts may differ from OpenAI prompts). This is implemented by allowing an optional `provider` column on the `prompts` table (NULL = provider-agnostic), with the resolution logic:

1. Account override for (role, action, provider)
2. Account override for (role, action) -- provider-agnostic
3. System default for (role, action, provider)
4. System default for (role, action) -- provider-agnostic

This enables per-provider prompt tuning while maintaining backward compatibility with provider-agnostic prompts.

### Relationship to Existing Code

| Current File | Status After Epic |
|-------------|------------------|
| `packages/api/src/services/prompt-store.ts` | Replaced by Postgres-backed implementation |
| `packages/api/src/services/default-prompts.ts` | Becomes seed data for the migration; file retained as reference |
| `packages/api/src/services/convention-templates.ts` | Becomes seed data for migration 018; retained as source of truth for `ResetSystemDefault` |
| `apps/tamma-elsa/src/Tamma.Api/Services/Conventions/ConventionTemplates.cs` | Same — seed data source + reset defaults |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ReadRepoConventionsActivity.cs` | Demoted to fallback; replaced by `ResolveConventionsActivity` (Story 27-13) |
| `packages/api/src/routes/prompts/prompt-routes.ts` | Replaced by tenant-scoped routes |
| `apps/tamma-elsa/.../ResolvePromptFromRegistryActivity.cs` | Updated to pass tenantId |
| `apps/tamma-elsa/.../LlmCallWorkflow.cs` | Updated to propagate tenantId |

## Stories

| Story | Title | Priority | Dependencies | Status |
|-------|-------|----------|-------------|--------|
| 27-1 | Prompt Store Database Schema + Migration | P0 (Critical) | Epic 17 (tenants table) | Planned |
| 27-2 | Prompt Store Service (TypeScript) | P0 (Critical) | Story 27-1 | Planned |
| 27-3 | Prompt Store API Endpoints | P0 (Critical) | Story 27-2 | Planned |
| 27-4 | Prompt Store Admin UI | P1 (High) | Story 27-3 | Planned |
| 27-5 | Prompt Store Tenant UI | P1 (High) | Story 27-3 | Planned |
| 27-6 | Elsa Workflow Integration | P0 (Critical) | Story 27-2 | Planned |
| 27-7 | Prompt Store Event Sourcing | P1 (High) | Story 27-2 | Planned |
| 27-8 | Convention Store Database Schema + Migration | P1 (High) | Epic 17, Story 27-1 | Planned |
| 27-9 | Convention Store Service (C#) | P1 (High) | Story 27-8 | Planned |
| 27-10 | Convention Store API Endpoints | P1 (High) | Story 27-9 | Planned |
| 27-11 | Convention Store Admin UI | P2 (Medium) | Story 27-10 | Planned |
| 27-12 | Convention Store Tenant UI | P2 (Medium) | Story 27-10, Story 27-11 | Planned |
| 27-13 | Convention Store Elsa Integration | P1 (High) | Story 27-9, Story 27-6 | Planned |
| 27-14 | Convention Store Event Sourcing | P2 (Medium) | Story 27-9 | Planned |

## Dependency Graph

```
Epic 17 (tenants table exists)
  │
  ▼
Story 27-1 (database schema + migration)
  │
  ▼
Story 27-2 (Postgres-backed PromptStore service)
  │
  ├──────────────────┬──────────────────┐
  ▼                  ▼                  ▼
Story 27-3         Story 27-6        Story 27-7
(API endpoints)    (Elsa integration) (event sourcing)
  │
  ├────────┐
  ▼        ▼
Story 27-4  Story 27-5
(admin UI)  (tenant UI)
```

### Convention Store (Stories 27-8 through 27-14)

```
Story 27-1 (prompt schema patterns)    Epic 17 (tenants table)
  │                                      │
  └──────────────┬───────────────────────┘
                 │
                 ▼
Story 27-8 (convention DB schema + migration)
                 │
                 ▼
Story 27-9 (convention store service — C#)
                 │
  ┌──────────────┼──────────────────────┐
  ▼              ▼                      ▼
Story 27-10    Story 27-13            Story 27-14
(API endpoints)(Elsa integration)     (event sourcing)
  │
  ├────────┐
  ▼        ▼
Story 27-11  Story 27-12
(admin UI)   (tenant UI)
```

## Design Constraints

1. **Tenant scoping**: `tenant_id` maps to `tenants.id` from Epic 17. The sentinel `DEFAULT_TENANT_ID` (`00000000-...`) is used for system defaults (NULL tenant_id) and self-hosted/CLI mode.
2. **NULL tenant_id = system default**: System defaults have `tenant_id IS NULL`, not the sentinel UUID. This differentiates "system-shipped" from "default tenant's overrides."
3. **Convention templates → Convention Store (Stories 27-8 to 27-14)**: The 20 static convention templates are migrated to a PostgreSQL-backed Convention Store with keyword-based matching and tenant override support. The store follows the same two-tier pattern as the prompt store (system defaults + tenant overrides), keyed by slug with keywords stored in a normalized `convention_keywords` table (B-tree indexed) for matching against LLM call context. The `{{conventions}}` variable is now populated by the convention store resolver instead of `.tamma/config.json`.
4. **Backward compatibility**: Existing prompt API routes (`/api/prompts/:role/:action`) must continue working for the self-hosted/CLI mode (resolved against system defaults).
5. **No RLS on prompt tables**: Prompt resolution crosses tenant boundaries by design (reading system defaults when tenant override is absent). Application-level filtering is used instead of RLS. See Story 17-2 for the RLS exemption list.
6. **Seed data from code**: The migration seeds all 80 role+action templates, 8 system prompts, and 10 action defaults from the existing `default-prompts.ts` code. The seed SQL is generated from the TypeScript constants to avoid duplication.

## Estimated Total Effort

| Story | Estimate |
|-------|----------|
| 27-1 Prompt Store Database Schema + Migration | 10 hours |
| 27-2 Prompt Store Service (TypeScript) | 14 hours |
| 27-3 Prompt Store API Endpoints | 12 hours |
| 27-4 Prompt Store Admin UI | 16 hours |
| 27-5 Prompt Store Tenant UI | 16 hours |
| 27-6 Elsa Workflow Integration | 10 hours |
| 27-7 Prompt Store Event Sourcing | 8 hours |
| 27-8 Convention Store Database Schema + Migration | 10.5 hours |
| 27-9 Convention Store Service (C#) | 15.5 hours |
| 27-10 Convention Store API Endpoints | 12 hours |
| 27-11 Convention Store Admin UI | 21 hours |
| 27-12 Convention Store Tenant UI | 16 hours |
| 27-13 Convention Store Elsa Integration | 14 hours |
| 27-14 Convention Store Event Sourcing | 6.5 hours |
| **Total** | **181.5 hours** |

## Host Constraints

- **Database**: PostgreSQL 17 (existing instance on Hetzner VPS)
- **No additional infrastructure**: All data stored in existing PostgreSQL; no new services required
- **Migration strategy**: Online migration with DEFAULT values to avoid table locks; seed data inserted via INSERT...ON CONFLICT DO NOTHING for idempotency

## Cross-Cutting Requirements

### Rate Limiting

All new API endpoints introduced by Epic 27 stories **must include rate limiting**. This is not a separate story -- it is a requirement on every story that adds an API route. Recommended defaults:

- Read endpoints (`GET`): 100 requests/minute per tenant
- Write endpoints (`POST`, `PUT`, `DELETE`): 30 requests/minute per tenant
- Prompt resolution (called by Elsa): 300 requests/minute per tenant

### Convention Store (Stories 27-8 to 27-14)

The 20 static convention templates are now migrated to a PostgreSQL-backed Convention Store via Stories 27-8 through 27-14. Each convention's keywords are stored in a normalized `convention_keywords` table with a B-tree index on `keyword` for fast resolution hot-path queries (`WHERE keyword IN (...)`). The store follows the same two-tier pattern as prompts: system defaults (`tenant_id IS NULL`) seeded from `ConventionTemplates.cs`, with tenant-level overrides by key.

Convention resolution happens at LLM-call time in `ResolveConventionsActivity` (Story 27-13): keywords on each convention are matched against the action, tools, repo languages, and searchable text of the call. Matching conventions are concatenated by priority and substituted into the `{{conventions}}` template variable. Story 12-7b's `search_conventions` tool reads from the same store.

Rate limiting for convention endpoints follows the same defaults as prompt endpoints.

---

**Last Updated**: 2026-05-04
**Epic Owner**: Platform Engineering
