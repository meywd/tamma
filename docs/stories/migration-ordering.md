# Cross-Epic Migration Ordering

This document defines the shared database migration sequence across all epics. Every story that creates a migration MUST reference its assigned migration number from this file. Migrations are applied in numeric order, so the numbering here determines the execution order.

## Existing Migrations (001-007)

These migrations already exist in `database/migrations/`:

| Number | File | Epic | Description |
|--------|------|------|-------------|
| 001 | `001_github_installations.sql` | Pre-epic | GitHub App installations table |
| 002 | `002_users.sql` | Pre-epic | Users table |
| 003 | `003_api_keys.sql` | Pre-epic | API keys table |
| 004 | `004_user_settings.sql` | Pre-epic | User settings table |
| 005 | `005_user_api_keys.sql` | Pre-epic | User API keys table |
| 006 | `006_user_invites.sql` | Pre-epic | User invites table |
| 007 | `007_users_soft_delete.sql` | Pre-epic | Soft delete for users |

## Planned Migrations (008+)

### Foundation: Multi-Tenancy (Epic 17)

| Number | File | Story | Description | Dependencies |
|--------|------|-------|-------------|-------------|
| 008 | `008_tenants.sql` | 17-1 | Create `tenants` table, insert default tenant, add `tenant_id` to `github_installations`, `users` (nullable), `user_api_keys`, `user_invites` | None (builds on 001-007) |
| 009 | `009_rls_tenant_isolation.sql` | 17-2 | Enable RLS on tenant-scoped tables, create `tamma_app` role, create `tenant_isolation_policy` on each table, create `prevent_tenant_id_change()` trigger | 008 |
| 010 | `010_tenant_scoped_event_store.sql` | 17-3, 17-4 | Add `tenant_id` to event store and workflow instance tables, apply RLS | 008, 009 |

### Prompt Store (Epic 27)

| Number | File | Story | Description | Dependencies |
|--------|------|-------|-------------|-------------|
| 011 | `011_prompt_store.sql` | 27-1 | Create `prompts`, `system_prompts`, `action_prompts` tables with partial unique indexes. Seed 80+8+10 system default rows. FK to `tenants(id)` on `tenant_id`. **No RLS** (exempt -- see Story 17-2). | 008 (tenants table for FK) |

### Agent Management (Epic 9)

| Number | File | Story | Description | Dependencies |
|--------|------|-------|-------------|-------------|
| 012 | `012_agent_configs.sql` | 9-1 | Create `agent_configs` table for per-tenant agent configuration | 008 |
| 013 | `013_provider_diagnostics.sql` | 9-2 | Create `provider_diagnostics` table for cost/token/latency tracking | 008 |
| 014 | `014_provider_health.sql` | 9-3 | Create `provider_health` table for circuit breaker state | 008 |
| 015 | `015_sanitization_rules.sql` | 9-7 | Create `sanitization_rules` table for per-tenant content sanitization | 008 |

### User Authentication & Membership (Epic 18)

| Number | File | Story | Description | Dependencies |
|--------|------|-------|-------------|-------------|
| 016 | `016_tenant_memberships.sql` | 18-3 | Create `tenant_memberships` (M:N user-to-tenant) and `tenant_invites` tables | 008 (tenants table), 002 (users table) |
| 017 | `017_user_auth_fields.sql` | 18-1 | Add `password_hash`, `email_verified`, `email_verification_token_hash`, `email_verification_expires_at`, `auth_method` columns to `users`. Add unique index on `LOWER(email)`. | 002 |

### Convention Store (Epic 27, Stories 27-8+)

| Number | File | Story | Description | Dependencies |
|--------|------|-------|-------------|-------------|
| 018 | `018_convention_store.sql` | 27-8 | Create `conventions` table + normalized `convention_keywords` table with B-tree index on `keyword`, partial unique indexes for system defaults / tenant overrides, seed 20 system defaults + ~80 keyword rows from `ConventionTemplates.cs`. FK to `tenants(id)` on `tenant_id`. **No RLS** (exempt — same as prompts). | 008 (tenants table for FK) |

## Migration Dependency Graph

```
001-007 (existing)
  |
  v
008 (tenants) ----+----+----+----+----+----+
  |               |    |    |    |    |    |
  v               v    v    v    v    v    v
009 (RLS)       011  012  013  014  015  016  018
  |             (prompts)(configs)(diag)(health)(sanit)(memberships)(conventions)
  v
010 (event+wf scoping)
                                           017 (user auth fields)
```

## Rules

1. **Never reuse a migration number.** If a planned migration is cancelled, leave a gap.
2. **Never reorder migrations.** Once a number is assigned, it is permanent.
3. **Each story references its migration number.** The story file should state "This story uses migration NNN."
4. **Idempotency required.** All migrations must use `IF NOT EXISTS`, `ON CONFLICT DO NOTHING`, etc.
5. **No data-only migrations between DDL migrations.** Seed data goes in the same migration as the table creation.
6. **FK constraints may be deferred.** If a migration references a table from another epic that is not yet deployed, the FK can be added later via an ALTER TABLE in a subsequent migration.

## Adding New Migrations

When a new story requires a database migration:

1. Check this file for the next available number.
2. Assign the number and add a row to the table above.
3. Update the story file to reference the migration number.
4. If the migration depends on tables from other epics, list the dependency in the "Dependencies" column.

---

**Last Updated**: 2026-04-09
**Maintained By**: Platform Engineering
