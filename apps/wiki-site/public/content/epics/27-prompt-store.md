---
title: "Epic 27: Prompt Store — Multi-Tenant Prompt Management"
sidebar:
  order: 27
---

**Status:** Planned (7 stories scoped, ~86h)
**Stories:** 7 (27-1 through 27-7)
**Layer:** Layer 4 (integration/UI)
**Depends on:** Epic 17 (`tenants` table), Epic 28 Phase A (DbContext factory)

> **Overview**: This epic has no root-level wiki page; the prompt store architecture summary in `CLAUDE.md` (Prompt Store Architecture section) is the platform-wide reference. See [Architecture](/architecture/) and [Workflow → LLM Call](Workflow-LLM-Call) for how Elsa workflows consume prompts.

## Purpose

Replace the current file-based, single-tenant `PromptStore` with a PostgreSQL-backed, multi-tenant prompt management system that supports system defaults, tenant-level overrides, and full audit trails.

The current `PromptStore` is an in-memory `Map<string, Prompt>` backed by a single JSON file (`packages/api/src/services/prompt-store.ts`). It has no concept of tenants, no database persistence, and no multi-tenant isolation. As Tamma moves to SaaS with multiple GitHub App installations (Epic 17/28), prompts must be scoped to tenants so one customer's customizations cannot affect another.

Epic 27 **supersedes** the original Epic 9 Story 9-6 (Agent Prompt Registry). The in-process `AgentPromptRegistry` at `packages/providers/src/agent-prompt-registry.ts` is updated to delegate to the Prompt Store API (Story 27-2/27-3) instead of resolving from in-memory config. Epic 9 Story 9-8 (Unified Agent Resolver) depends on Epic 27.

## Current state

- 80 system default role+action templates ship in `packages/api/src/services/default-prompts.ts`
- 8 system role identity prompts (architect, implementer, reviewer, tester, mentor, debugger, scrum-master, triage)
- 10 action default templates (the safety net)
- 20 convention templates in `convention-templates.ts` (static, not part of this epic)
- All planned; no story has shipped code yet

## Stories

| Story | Title | Priority | Effort | Status |
|-------|-------|----------|--------|--------|
| 27-1 | Prompt Store Database Schema + Migration | P0 | 10h | Planned |
| 27-2 | Prompt Store Service (TypeScript) | P0 | 14h | Planned |
| 27-3 | Prompt Store API Endpoints | P0 | 12h | Planned |
| 27-4 | Prompt Store Admin UI | P1 | 16h | Planned |
| 27-5 | Prompt Store Tenant UI | P1 | 16h | Planned |
| 27-6 | Elsa Workflow Integration | P0 | 10h | Planned |
| 27-7 | Prompt Store Event Sourcing | P1 | 8h | Planned |

**Total**: 86h.

## Architecture / key decisions

1. **Three tables**: `prompts` (role+action), `system_prompts` (role identity), `action_prompts` (action defaults). Each carries a nullable `tenant_id` — `NULL` means system default; non-NULL means tenant override.
2. **Two-tier resolution**: For `(tenantId, role, action)`: tenant override → system default → 404. Same shape for system prompts and action defaults.
3. **Provider dimension**: optional `provider` column on `prompts` allows per-provider tuning (Anthropic vs OpenAI prompts) while staying backward-compatible with provider-agnostic rows.
4. **No RLS on prompt tables**: prompt resolution crosses tenant boundaries by design (reading system defaults when a tenant override is absent). Application-level filtering is used; see Story 17-2 for the RLS exemption list.
5. **Convention templates remain static**: the 20 templates in `convention-templates.ts` ship in code, injected via the `{{conventions}}` variable. A future story may move them to Postgres.
6. **Seed data from code**: the migration seeds all 80 role+action templates, 8 system prompts, and 10 action defaults from `default-prompts.ts` via INSERT...ON CONFLICT DO NOTHING for idempotency.

## Resolution order

For a request `(tenantId, role, action)`:

1. User's role+action override → if exists, use it
2. System default role+action → if exists, use it
3. User's action default override → if exists, use it
4. System default action template → safety net (always present)

For system prompt `(tenantId, role)`:

1. User's role system prompt override → if exists, use it
2. System default role system prompt

## Dependencies

**Upstream**:
- [Epic 17](Epic-17-Multi-Tenancy.md) — `tenants` table for the `tenant_id` FK
- [Epic 28](Epic-28-DB-Per-Tenant.md) — DbContext factory once db-per-tenant lands; pre-28 it lives in the shared central Postgres

**Downstream**:
- Epic 9 Story 9-8 (Unified Agent Resolver) — depends on 27-2/27-3 for prompt resolution
- Epic 12 Story 12-7b (Convention & History Tools) — depends on 27 for prompt fetching and event-store search

## Open questions

1. **Convention-template per-tenant migration timing**: leave in code for v1; the user design intent permits per-tenant convention overrides eventually. Re-open when a tenant requests it.
2. **Provider dimension**: ship the column nullable but defer the resolver enhancement (steps 1 and 3 of the four-step provider resolution) until at least one tenant requests provider-specific tuning. Conservative default keeps complexity low.
3. **Prompt versioning vs event sourcing (27-7)**: `prompts` table carries a `version` integer but versions are not retained in the table itself — old versions live in the event log. Trade-off documented in Story 27-7.

## API surface

```
GET    /api/prompts                           — list resolved prompts for current tenant
GET    /api/prompts/:role/:action             — get resolved prompt
PUT    /api/prompts/:role/:action             — create/update tenant override
DELETE /api/prompts/:role/:action             — delete tenant override (falls back to system default)
POST   /api/prompts/:role/:action/reset       — alias for DELETE
GET    /api/prompts/defaults                  — list system defaults (read-only)
GET    /api/prompts/defaults/:action          — get action default template
GET    /api/prompts/defaults/:role/:action    — get system default role+action template
GET    /api/convention-templates              — list all (static)
GET    /api/convention-templates/:key         — get full template with conventions string
```

All write endpoints require tenant-admin role; all read endpoints are tenant-scoped.

## Story files

[Epic 27 stories on GitHub](/stories/epic-27/)

---

_Last updated: 2026-04-21_
