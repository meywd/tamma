# Admin/Health + DB Schema — Port Gap Findings Index

Scope: Admin endpoints (`/api/admin/*`), health endpoints (`/health`, `/api/health`), and a comprehensive diff of every archived SQL migration (`database/archived-sql-migrations/001_…` through `018_…`) versus the current EF Core migrations (`apps/tamma-elsa/src/Tamma.Data/Migrations/`).

Baseline: TypeScript API at `9e9a57c~1` vs current `feat/auth-foundation`.

## Summary

- **33 findings** across two surfaces: 9 admin/health + 24 schema.
- **5 P0 (cutover-blocking)** — all tenant-isolation safety nets lost.
- **11 P1 (feature broken)** — service-key semantics, billing accuracy, FK/uniqueness drift, missing tables.
- **13 P2 (correctness/observability)** — missing CHECKs, partial indexes, response-field gaps.
- **4 P3 (drift/contract)** — documentation, never-existed features, status codes.

## Findings

### Admin / Health (001-009)

| # | Title | Severity | Classification |
|---|---|---|---|
| [001](001-admin-health-aggregator-stub.md) | Admin health aggregator regressed to trivial stub | P2 | Not-yet-implemented |
| [002](002-health-liveness-readiness-split.md) | `/health` endpoint lacks live/ready split | P3 | Incomplete |
| [003](003-service-keys-permission-drift.md) | Service-key POST permission drift — SettingsManage vs owner-only | P1 | Behavioral drift |
| [004](004-service-keys-owner-id-hardcoded.md) | POST service-keys hardcodes OwnerId = "system" | P1 | Behavioral drift |
| [005](005-service-keys-tenant-auto-bound.md) | POST service-keys auto-binds TenantId instead of null | P1 | Behavioral drift |
| [006](006-service-keys-list-response-fields-missing.md) | GET service-keys response missing rotatedFrom, lastUsedAt, revokedAt | P2 | Incomplete |
| [007](007-service-keys-rotate-no-grace-warning.md) | Rotate service-keys missing 24h grace warning + rotatedFrom | P2 | Incomplete |
| [008](008-service-keys-delete-status-code.md) | DELETE service-keys returns 200 not 204, no 404 | P3 | Behavioral drift |
| [009](009-admin-operations-never-existed.md) | Impersonate/forced-logout/lockdown/banners — never existed in TS | P3 | Not a regression |

### Schema — tenant-isolation safety net (020-023) — **P0 cluster**

| # | Title | Severity | Classification |
|---|---|---|---|
| [020](020-schema-rls-policies-missing.md) | RLS tenant isolation policies entirely absent | P0 | Data-model regression |
| [021](021-schema-tamma-app-role-missing.md) | `tamma_app` non-superuser role not created | P0 | Data-model regression |
| [022](022-schema-prevent-tenant-id-change-trigger.md) | `prevent_tenant_id_change()` trigger missing | P0 | Data-model regression |
| [023](023-schema-default-tenant-sentinel.md) | Default tenant sentinel `00000000-…` not seeded | P1 | Data-model regression |

### Schema — per-table diffs (010-019, 024-033)

| # | Title | Severity | Classification |
|---|---|---|---|
| [010](010-schema-user-installations-table-deleted.md) | `user_installations` table entirely deleted | P1 | Data-model regression |
| [011](011-schema-user-api-keys-table-absent.md) | `user_api_keys` legacy table absent, no copy-migration | P1 | Data-model regression |
| [012](012-schema-refresh-tokens-partial-index.md) | `refresh_tokens` missing partial `expires_at WHERE revoked_at IS NULL` index | P2 | Data-model regression |
| [013](013-schema-password-reset-tokens-partial-index.md) | `password_reset_tokens` missing partial active index | P2 | Data-model regression |
| [014](014-schema-sanitization-rules-diff.md) | `sanitization_rules` flattened to Rules jsonb; UNIQUE + cascade lost | P1 | Data-model regression |
| [015](015-schema-service-key-prefix-convention-drift.md) | Service-key prefix drift — CLAUDE.md `tk_pl_` vs `tamma_sk_` | P3 | Behavioral drift (docs) |
| [016](016-schema-api-keys-diff.md) | `api_keys` — permissions jsonb→text[], rotated_from FK unenforced, lost partial active index | P1 | Data-model regression |
| [017](017-schema-github-installations-diff.md) | `github_installations` — PK bigint→uuid, app_id bigint→integer, CHECK + FK + indexes gone, api_key_* cols dropped | P1 | Data-model regression |
| [018](018-schema-github-installation-repos-diff.md) | `github_installation_repos` — BIGSERIAL→uuid, owner/name split lost | P2 | Data-model regression |
| [019](019-schema-user-invites-diff.md) | `user_invites` — role CHECK lost, invited_by FK missing | P1 | Data-model regression |
| [024](024-schema-users-table-diff.md) | `users` — email NOT NULL, github_id int, settings gone, CHECKs + LOWER(email) index lost | P1 | Data-model regression |
| [025](025-schema-tenants-table-diff.md) | `tenants` — plan CHECK lost, no sentinel seed | P2 | Data-model regression |
| [026](026-schema-engine-events-domain-events-rename.md) | `engine_events → domain_events` — timestamp BIGINT lost, partial issue_number index gone, RLS gone | P1 | Data-model regression |
| [027](027-schema-tenant-memberships-diff.md) | `tenant_memberships` — composite PK→surrogate, role CHECK lost | P2 | Data-model regression |
| [028](028-schema-tenant-invites-table-absent.md) | `tenant_invites` table absent — Epic 18 invite flow conflated with `user_invites` | P2 | Data-model regression |
| [029](029-schema-workflow-instances-diff.md) | `workflow_instances` — definition_id text→uuid breaking, created_at BIGINT→ts, RLS gone | P1 | Data-model regression |
| [030](030-schema-prompts-collapse.md) | `prompts`/`system_prompts`/`action_prompts` → `prompt_overrides` collapse (CLAUDE.md compliant) | P3 | Semantic rewrite |
| [031](031-schema-agent-configs-diff.md) | `agent_configs` — non-partial unique on nullable TenantId allows multiple NULL rows | P2 | Data-model regression |
| [032](032-schema-provider-diagnostics-diff.md) | `provider_diagnostics` — 8 columns dropped, TokensUsed conflates input/output, 6 indexes missing (billing accuracy) | P1 | Data-model regression |
| [033](033-schema-provider-health-diff.md) | `provider_health` — PK key→Id uuid, circuit_open bool→Status string, no CHECK, partial index missing | P2 | Data-model regression |

## Severity cluster

- **P0 (5)**: 020, 021, 022 form the tenant-isolation triumvirate; 023 is the sentinel dependency. 024 elevates to P0 via `github_id int` overflow risk but is classified P1 here pending auth-team review.
- **P1 (11)**: 003, 004, 005, 010, 011, 014, 016, 017, 019, 023, 024, 026, 029, 032.
- **P2 (13)**: 001, 006, 007, 012, 013, 018, 025, 027, 028, 031, 033.
- **P3 (4)**: 002, 008, 009, 015, 030.

## Effort (from audit summary `/tmp/tamma-audit/36-admin-db.md`)

- Like-for-like health aggregator: 6-10h (finding 001)
- 24h grace on rotation: 2h (finding 007)
- RLS + prevent_tenant_id_change trigger restoration: ~15-20h (findings 020, 021, 022)
- Missing indexes + CHECK constraints: ~8h (findings 012, 013, 025, 027, 031, 033)
- Schema column restorations (users.settings, correlation_id, etc.): ~10h (findings 024, 032)

**Total estimated port effort**: ~70-85h (dominated by RLS restoration and `provider_diagnostics` column restoration).

## Files I could not find

All 18 archived SQL migrations were readable from the local working copy at `database/archived-sql-migrations/NNN_*.sql`. Quoting `git show 9e9a57c~1:packages/api/database/migrations/*.sql` failed (`fatal: path ... does not exist`), so the working-copy files were used directly. The 0-byte blob concern in the prompt was not present — all 18 files have content.

## Cross-scope links

- `docs/audit/port-gaps/auth/` — user auth / session flows overlap with findings 024, 012, 013
- `docs/audit/port-gaps/orgs/` — tenant/org flows overlap with findings 023, 025, 027, 028
- `docs/audit/port-gaps/providers/` — overlaps with findings 031, 032, 033
- `docs/audit/port-gaps/prompts/` — overlaps with finding 030
- `docs/audit/port-gaps/engine/` — overlaps with findings 026, 029
- `docs/audit/port-gaps/github/` — overlaps with findings 017, 018
- `docs/audit/port-gaps/kb/` — no direct schema overlap
