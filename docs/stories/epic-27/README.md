# Epic 27: Prompt Store — Multi-Tenant Prompt Management

## Overview

**Goal**: Replace the file-based, single-tenant `PromptStore` with a PostgreSQL-backed, multi-tenant prompt management system that supports system defaults, account-level overrides, and full audit trails.

**Value Delivered**:
- Multi-tenant prompt isolation: each account (organization) sees its own prompts without cross-tenant leakage
- Two-tier resolution: account overrides take precedence over system defaults, with transparent fallback
- Platform admin control over the 80 system default role+action templates, 8 role system prompts, and 20 convention templates
- Account admin self-service for customizing prompts without touching system defaults
- Full audit trail via DCB events for every prompt change (who, when, what)
- Elsa workflows resolve prompts per-account, enabling different organizations to use different prompt strategies
- Admin UI and Account UI for managing prompts without API calls

**Why Now**: The current `PromptStore` is an in-memory Map backed by a single JSON file. It has no concept of accounts, no database persistence, and no multi-tenant isolation. As Tamma moves to SaaS with multiple GitHub App installations (Epic 17), prompts must be scoped to accounts so that one customer's customizations do not affect another.

## Architecture

### Data Model

Three PostgreSQL tables store all prompt data:

```
prompts
├── id              UUID PK (gen_random_uuid())
├── account_id      UUID NULL (NULL = system default, non-NULL = account override)
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
└── UNIQUE (account_id, role, action)   -- one prompt per role+action per account

system_prompts
├── id              UUID PK (gen_random_uuid())
├── account_id      UUID NULL (NULL = system default)
├── role            TEXT NOT NULL
├── prompt          TEXT NOT NULL
├── version         INTEGER NOT NULL DEFAULT 1
├── created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
├── updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
├── created_by      UUID NULL
├── updated_by      UUID NULL
└── UNIQUE (account_id, role)            -- one system prompt per role per account

action_prompts
├── id              UUID PK (gen_random_uuid())
├── account_id      UUID NULL (NULL = system default)
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
└── UNIQUE (account_id, action)          -- one action template per action per account
```

### Resolution Logic

When resolving a prompt for `(accountId, role, action)`:

1. Look for an account-specific row in `prompts` WHERE `account_id = :accountId AND role = :role AND action = :action`
2. If not found, fall back to the system default WHERE `account_id IS NULL AND role = :role AND action = :action`
3. The system prompt is resolved similarly from `system_prompts`
4. Action defaults are resolved from `action_prompts`

```
Account Override (account_id = 'acme-uuid')
        │
        │ found? → use it
        │
        ▼ not found?
System Default (account_id IS NULL)
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

### Relationship to Existing Code

| Current File | Status After Epic |
|-------------|------------------|
| `packages/api/src/services/prompt-store.ts` | Replaced by Postgres-backed implementation |
| `packages/api/src/services/default-prompts.ts` | Becomes seed data for the migration; file retained as reference |
| `packages/api/src/services/convention-templates.ts` | Unchanged (convention templates are static, not per-account) |
| `packages/api/src/routes/prompts/prompt-routes.ts` | Replaced by account-scoped routes |
| `apps/tamma-elsa/.../ResolvePromptFromRegistryActivity.cs` | Updated to pass accountId |
| `apps/tamma-elsa/.../LlmCallWorkflow.cs` | Updated to propagate accountId |

## Stories

| Story | Title | Priority | Dependencies | Status |
|-------|-------|----------|-------------|--------|
| 27-1 | Prompt Store Database Schema + Migration | P0 (Critical) | Epic 17 (tenants table) | Planned |
| 27-2 | Prompt Store Service (TypeScript) | P0 (Critical) | Story 27-1 | Planned |
| 27-3 | Prompt Store API Endpoints | P0 (Critical) | Story 27-2 | Planned |
| 27-4 | Prompt Store Admin UI | P1 (High) | Story 27-3 | Planned |
| 27-5 | Prompt Store Account UI | P1 (High) | Story 27-3 | Planned |
| 27-6 | Elsa Workflow Integration | P0 (Critical) | Story 27-2 | Planned |
| 27-7 | Prompt Store Event Sourcing | P1 (High) | Story 27-2 | Planned |

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
(admin UI)  (account UI)
```

## Design Constraints

1. **Account = Tenant**: `account_id` maps to `tenants.id` from Epic 17. The sentinel `DEFAULT_TENANT_ID` (`00000000-...`) is used for system defaults (NULL account_id) and self-hosted/CLI mode.
2. **NULL account_id = system default**: System defaults have `account_id IS NULL`, not the sentinel UUID. This differentiates "system-shipped" from "default tenant's overrides."
3. **Convention templates remain static**: The 20 convention templates in `convention-templates.ts` are injected via the `{{conventions}}` variable and are not part of the prompt store tables. They could be moved to Postgres in a future epic.
4. **Backward compatibility**: Existing prompt API routes (`/api/prompts/:role/:action`) must continue working for the self-hosted/CLI mode (resolved against system defaults).
5. **No RLS on prompt tables**: Prompt resolution crosses account boundaries by design (reading system defaults when account override is absent). Application-level filtering is used instead of RLS.
6. **Seed data from code**: The migration seeds all 80 role+action templates, 8 system prompts, and 10 action defaults from the existing `default-prompts.ts` code. The seed SQL is generated from the TypeScript constants to avoid duplication.

## Estimated Total Effort

| Story | Estimate |
|-------|----------|
| 27-1 Prompt Store Database Schema + Migration | 10 hours |
| 27-2 Prompt Store Service (TypeScript) | 14 hours |
| 27-3 Prompt Store API Endpoints | 12 hours |
| 27-4 Prompt Store Admin UI | 16 hours |
| 27-5 Prompt Store Account UI | 16 hours |
| 27-6 Elsa Workflow Integration | 10 hours |
| 27-7 Prompt Store Event Sourcing | 8 hours |
| **Total** | **86 hours** |

## Host Constraints

- **Database**: PostgreSQL 17 (existing instance on Hetzner VPS)
- **No additional infrastructure**: All data stored in existing PostgreSQL; no new services required
- **Migration strategy**: Online migration with DEFAULT values to avoid table locks; seed data inserted via INSERT...ON CONFLICT DO NOTHING for idempotency

---

**Last Updated**: 2026-04-08
**Epic Owner**: Platform Engineering
