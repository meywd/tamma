# Story 17.1: Tenant Model + Database Schema

Status: ready-for-dev

## Story

As a **platform engineer**,
I want a `tenants` table and a `tenant_id` foreign key on every tenant-scoped table,
so that every row in the database has an unambiguous owner and the system can enforce hard isolation between organizations.

## Acceptance Criteria

1. A `tenants` table exists with columns: `id` (UUID PK, default `gen_random_uuid()`), `name` (TEXT NOT NULL), `slug` (TEXT UNIQUE NOT NULL), `external_id` (TEXT UNIQUE — maps to `installation_id::text`), `plan` (TEXT NOT NULL DEFAULT `'free'`), `settings` (JSONB NOT NULL DEFAULT `'{}'`), `created_at` (TIMESTAMPTZ), `updated_at` (TIMESTAMPTZ), `deleted_at` (TIMESTAMPTZ nullable for soft delete)
2. A sentinel "default" tenant row is inserted with `id = '00000000-0000-0000-0000-000000000000'`, `name = 'Default'`, `slug = 'default'`, `external_id = NULL` for CLI/self-hosted mode
3. `github_installations` gains a `tenant_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000' REFERENCES tenants(id)` column
4. `users` gains a `tenant_id UUID NULL DEFAULT NULL REFERENCES tenants(id)` column. This column is nullable: it represents the user's "active tenant" shortcut, NOT the ownership relationship. The canonical user-to-tenant relationship is M:N via the `tenant_memberships` table (Epic 18 Story 18-3). A NULL value means the user has not yet selected an active tenant.
5. `user_api_keys` gains a `tenant_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000' REFERENCES tenants(id)` column
6. `user_invites` gains a `tenant_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000' REFERENCES tenants(id)` column
7. Every new `tenant_id` column has a B-tree index
8. Existing rows in all tables are backfilled with the default tenant ID during migration
9. A `Tenant` TypeScript interface exists in `packages/shared/src/types/tenant.ts` matching the DB schema
10. An `ITenantStore` interface exists in `packages/api/src/persistence/tenant-store.ts` with `createTenant`, `getTenant`, `getTenantByExternalId`, `getTenantBySlug`, `updateTenant`, `deleteTenant` (soft delete), `listTenants` methods
11. `InMemoryTenantStore` and `PgTenantStore` implementations exist and pass tests
12. CLI/self-hosted mode continues to work without any tenant configuration (uses default tenant implicitly)
13. Migration is idempotent (running it twice produces no errors)

## Technical Context

### Current Schema (Pre-Migration)

The database has 7 migrations (001-007). The tables that need `tenant_id`:

| Table | PK | Notes |
|-------|-----|-------|
| `github_installations` | `installation_id BIGINT` | Will also link to `tenants` via `tenant_id` |
| `github_installation_repos` | `id BIGSERIAL` | Scoped through `github_installations` FK cascade — no direct `tenant_id` needed |
| `users` | `id UUID` | Direct `tenant_id` |
| `user_installations` | `(user_id, installation_id)` | Scoped through both FKs — no direct `tenant_id` needed |
| `user_api_keys` | `id UUID` | Direct `tenant_id` |
| `user_invites` | `id UUID` | Direct `tenant_id` |

Tables that do NOT need `tenant_id` because they are scoped transitively through foreign keys:
- `github_installation_repos` (always accessed via `installation_id`)
- `user_installations` (join table — both sides carry `tenant_id`)

### Default Tenant Strategy

The sentinel UUID `00000000-0000-0000-0000-000000000000` is used for:
- CLI/self-hosted mode (no SaaS, single organization)
- Existing data migration (all current rows belong to the default tenant)
- Any operation where tenant context is not explicitly set

This avoids nullable `tenant_id` columns and simplifies RLS policies.

### Files to Create

| File | Purpose |
|------|---------|
| `database/migrations/008_tenants.sql` | Create `tenants` table, insert default tenant, add `tenant_id` to existing tables |
| `packages/shared/src/types/tenant.ts` | `Tenant` interface, `DEFAULT_TENANT_ID` constant |
| `packages/api/src/persistence/tenant-store.ts` | `ITenantStore` interface + `InMemoryTenantStore` |
| `packages/api/src/persistence/pg-tenant-store.ts` | `PgTenantStore` PostgreSQL implementation |
| `packages/api/src/persistence/__tests__/tenant-store.test.ts` | Unit tests for InMemoryTenantStore |

### Files to Modify

| File | Change |
|------|--------|
| `packages/shared/src/types/index.ts` | Re-export tenant types |
| `packages/api/src/persistence/installation-store.ts` | Add `tenantId` to `GitHubInstallation` interface |
| `packages/api/src/persistence/pg-installation-store.ts` | Include `tenant_id` in queries and mappers |
| `packages/api/src/persistence/user-store.ts` | Add `tenantId` to `User` interface, update `UpsertUserInput` |
| `packages/api/src/persistence/pg-user-store.ts` | Include `tenant_id` in queries and mappers |
| `packages/api/src/persistence/user-api-key-store.ts` | Add `tenantId` to `UserApiKey`, `CreateApiKeyInput` |
| `packages/api/src/persistence/invite-store.ts` | Add `tenantId` to `UserInvite`, `CreateInviteInput` |

## Implementation Plan

### Step 1: Create the Migration

```sql
-- database/migrations/008_tenants.sql

-- 1. Create tenants table
CREATE TABLE IF NOT EXISTS tenants (
  id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name          TEXT NOT NULL,
  slug          TEXT UNIQUE NOT NULL,
  external_id   TEXT UNIQUE,
  plan          TEXT NOT NULL DEFAULT 'free' CHECK (plan IN ('free', 'pro', 'enterprise')),
  settings      JSONB NOT NULL DEFAULT '{}',
  created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  deleted_at    TIMESTAMPTZ
);

-- Partial index for fast lookups of non-deleted tenants
CREATE INDEX IF NOT EXISTS idx_tenants_deleted_at ON tenants (deleted_at) WHERE deleted_at IS NULL;
CREATE INDEX IF NOT EXISTS idx_tenants_external_id ON tenants (external_id) WHERE external_id IS NOT NULL;

-- 2. Insert the sentinel "default" tenant for CLI/self-hosted mode
INSERT INTO tenants (id, name, slug, external_id, plan)
VALUES ('00000000-0000-0000-0000-000000000000', 'Default', 'default', NULL, 'free')
ON CONFLICT (id) DO NOTHING;

-- 3. Add tenant_id to github_installations
ALTER TABLE github_installations
  ADD COLUMN IF NOT EXISTS tenant_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'
  REFERENCES tenants(id);

CREATE INDEX IF NOT EXISTS idx_installations_tenant_id ON github_installations (tenant_id);

-- 4. Add tenant_id to users (nullable — "active tenant" shortcut)
-- NOTE: This column is nullable. The canonical user-to-tenant relationship is M:N
-- via the tenant_memberships table (Epic 18 Story 18-3). tenant_id here is the
-- user's currently active tenant, set on login and org-switch. NULL means the
-- user has not yet selected an active tenant.
ALTER TABLE users
  ADD COLUMN IF NOT EXISTS tenant_id UUID DEFAULT NULL
  REFERENCES tenants(id);

CREATE INDEX IF NOT EXISTS idx_users_tenant_id ON users (tenant_id);

-- 5. Add tenant_id to user_api_keys
ALTER TABLE user_api_keys
  ADD COLUMN IF NOT EXISTS tenant_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'
  REFERENCES tenants(id);

CREATE INDEX IF NOT EXISTS idx_user_api_keys_tenant_id ON user_api_keys (tenant_id);

-- 6. Add tenant_id to user_invites
ALTER TABLE user_invites
  ADD COLUMN IF NOT EXISTS tenant_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'
  REFERENCES tenants(id);

CREATE INDEX IF NOT EXISTS idx_user_invites_tenant_id ON user_invites (tenant_id);
```

### Step 2: Tenant TypeScript Types

```typescript
// packages/shared/src/types/tenant.ts

/**
 * Sentinel UUID for the default tenant used in CLI/self-hosted mode.
 * All existing data is backfilled to this tenant during migration.
 */
export const DEFAULT_TENANT_ID = '00000000-0000-0000-0000-000000000000';

/** Supported billing plans. */
export type TenantPlan = 'free' | 'pro' | 'enterprise';

/** Represents a tenant (organization/user) in the Tamma SaaS platform. */
export interface Tenant {
  id: string;
  name: string;
  slug: string;
  externalId: string | null;
  plan: TenantPlan;
  settings: Record<string, unknown>;
  createdAt: string;
  updatedAt: string;
  deletedAt: string | null;
}
```

### Step 3: Tenant Store Interface

```typescript
// packages/api/src/persistence/tenant-store.ts

export interface CreateTenantInput {
  name: string;
  slug: string;
  externalId?: string | null;
  plan?: TenantPlan;
  settings?: Record<string, unknown>;
}

export interface ITenantStore {
  createTenant(input: CreateTenantInput): Promise<Tenant>;
  getTenant(id: string): Promise<Tenant | null>;
  getTenantByExternalId(externalId: string): Promise<Tenant | null>;
  getTenantBySlug(slug: string): Promise<Tenant | null>;
  updateTenant(id: string, update: Partial<Pick<Tenant, 'name' | 'slug' | 'plan' | 'settings'>>): Promise<Tenant>;
  deleteTenant(id: string): Promise<void>;  // soft delete
  listTenants(): Promise<Tenant[]>;
}
```

### Step 4: Update Existing Interfaces

Add `tenantId: string` to `GitHubInstallation`, `User`, `UserApiKey`, `UserInvite` interfaces. All input types gain an optional `tenantId` that defaults to `DEFAULT_TENANT_ID`.

### Step 5: Tenant Provisioning on GitHub App Install

When a GitHub App installation webhook fires (`installation.created`), the webhook handler should:
1. Create a new tenant via `ITenantStore.createTenant()` with `externalId = String(installationId)`
2. Pass the new `tenant_id` to `upsertInstallation()`
3. Link the installing user to that tenant

This wiring happens in Story 17.5 (middleware) but the store interfaces must be ready here.

## Implementation Notes

1. The migration uses `ADD COLUMN IF NOT EXISTS` and `ON CONFLICT DO NOTHING` for idempotency.
2. `DEFAULT '00000000-0000-0000-0000-000000000000'` on `tenant_id` columns for `github_installations`, `user_api_keys`, and `user_invites` means the ALTER TABLE fills all existing rows automatically. **Exception: `users.tenant_id` is nullable with `DEFAULT NULL`** because the canonical user-to-tenant relationship is the M:N `tenant_memberships` table (Epic 18 Story 18-3). `users.tenant_id` is the "active tenant" shortcut.
3. The `slug` column enables vanity URLs (e.g., `app.tamma.dev/orgs/acme-corp`) in future.
4. `settings` JSONB on the tenant stores tenant-level configuration (rate limits, feature flags, etc.).
5. `external_id` is nullable because the default tenant has no external identity.
6. The `plan` column enables future billing logic (not implemented in this epic).
7. All store implementations (both InMemory and Pg) must be updated to carry `tenantId` through reads and writes.

## Testing Strategy

### Unit Tests

Create `packages/api/src/persistence/__tests__/tenant-store.test.ts`:

1. `createTenant` creates and returns a tenant with generated UUID
2. `getTenant` returns null for nonexistent ID
3. `getTenantByExternalId` returns the correct tenant
4. `getTenantBySlug` returns the correct tenant
5. `updateTenant` updates only specified fields, bumps `updatedAt`
6. `deleteTenant` sets `deletedAt` (soft delete)
7. `listTenants` excludes soft-deleted tenants
8. Duplicate `slug` throws an error
9. Duplicate `externalId` throws an error

Update existing store tests to pass `tenantId`:

10. `InMemoryInstallationStore` stores and returns `tenantId`
11. `InMemoryUserStore` stores and returns `tenantId`
12. `InMemoryUserApiKeyStore` stores and returns `tenantId`
13. `InMemoryInviteStore` stores and returns `tenantId`

### Integration Tests

14. Run migration 008 against a test PostgreSQL database — verify tables, columns, indexes, and default tenant row exist
15. `PgTenantStore` CRUD operations work end-to-end
16. `PgInstallationStore` correctly stores and queries `tenant_id`
17. `PgUserStore` correctly stores and queries `tenant_id`

### Backward Compatibility

18. All existing tests continue to pass without specifying `tenantId` (defaults to `DEFAULT_TENANT_ID`)
19. CLI mode starts and runs a workflow cycle without tenant configuration

## Migration Number

This story uses **migration 008** (`008_tenants.sql`). See `/docs/stories/migration-ordering.md` for the cross-epic migration sequence.

## Dependencies

- None (this is the foundation story for the epic)
- Internal: `packages/shared/src/types/`, `packages/api/src/persistence/`

## Estimated Effort

| Task | Hours |
|------|-------|
| Migration SQL (tenants table + ALTER TABLE statements) | 2 |
| Tenant TypeScript types | 1 |
| ITenantStore interface + InMemoryTenantStore | 2 |
| PgTenantStore implementation | 2 |
| Update existing interfaces (GitHubInstallation, User, UserApiKey, UserInvite) | 2 |
| Update existing InMemory stores | 2 |
| Update existing Pg stores | 2 |
| Unit tests | 2 |
| Integration tests | 1 |
| **Total** | **16 hours** |

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-28 | 1.0 | Initial story creation | Architecture Team |
