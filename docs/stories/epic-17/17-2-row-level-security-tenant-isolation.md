# Story 17.2: Row-Level Security (RLS) for Tenant Isolation

Status: ready-for-dev

## Story

As a **security engineer**,
I want PostgreSQL Row-Level Security policies on every tenant-scoped table,
so that even if application code has a bug (missing WHERE clause, wrong join, SQL injection), one tenant can never read or modify another tenant's data.

## Acceptance Criteria

1. RLS is enabled (`ALTER TABLE ... ENABLE ROW LEVEL SECURITY`) on: `tenants`, `github_installations`, `users`, `user_api_keys`, `user_invites`
2. Each table has a policy named `tenant_isolation_policy` that restricts SELECT, INSERT, UPDATE, DELETE to rows where `tenant_id = current_setting('app.current_tenant_id')::uuid`
3. The `tenants` table has a self-referencing policy: `id = current_setting('app.current_tenant_id')::uuid`
4. A PostgreSQL role `tamma_app` exists (or is reused) that is subject to RLS (is NOT a superuser, does NOT have `BYPASSRLS`)
5. The existing superuser/owner role used for migrations is NOT subject to RLS (retains `BYPASSRLS` for migration and maintenance operations)
6. When `app.current_tenant_id` is not set, all queries on RLS-protected tables return zero rows (fail-closed behavior)
7. When `app.current_tenant_id` is set to a valid tenant UUID, queries return only that tenant's rows
8. INSERT operations enforce that the `tenant_id` on the new row matches `current_setting('app.current_tenant_id')::uuid`
9. UPDATE operations cannot change the `tenant_id` column (policy on UPDATE restricts both old and new row)
10. All RLS policies use `PERMISSIVE` mode (default) so multiple policies combine with OR (only one policy per table is needed)
11. Performance impact is measurable: query plans show "Filter: (tenant_id = ...)" but no sequential scans on tenant_id (covered by existing B-tree indexes from Story 17.1)
12. A migration test confirms cross-tenant reads are blocked: set tenant A, insert row, set tenant B, SELECT returns zero rows

## Technical Context

### Why RLS?

Application-level WHERE clauses are the primary tenant filter. RLS is the **defense-in-depth** layer:

| Failure Mode | Without RLS | With RLS |
|-------------|-------------|----------|
| Forgot WHERE tenant_id = ... | Data leak | Zero rows returned |
| SQL injection bypasses WHERE | Full table access | Filtered by session variable |
| New developer writes query without tenant filter | Data leak | Zero rows returned |
| ORM generates wrong query | Data leak | Zero rows returned |

### PostgreSQL Session Variables

PostgreSQL allows arbitrary session variables via `SET` and `current_setting()`:

```sql
-- Set at the start of each request/transaction
SET app.current_tenant_id = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee';

-- RLS policy reads it
current_setting('app.current_tenant_id')::uuid
```

The variable is connection-scoped. For connection pools (e.g., `pg.Pool`), it must be set on every connection checkout or at the start of every transaction.

### Fail-Closed Behavior

When `app.current_tenant_id` is not set, `current_setting('app.current_tenant_id')` throws an error by default. To make this fail-closed (return zero rows instead of error), use:

```sql
current_setting('app.current_tenant_id', true)::uuid
```

The second argument (`true`) means "return NULL if not set". Combined with `tenant_id = NULL`, this returns zero rows (NULL != anything in SQL).

However, `NULL::uuid` is valid but comparing `uuid = NULL` always returns false in SQL. This is the desired fail-closed behavior.

### Database Roles

| Role | Purpose | RLS Behavior |
|------|---------|-------------|
| `tamma_owner` (or current superuser) | Runs migrations, maintenance | BYPASSRLS — sees all rows |
| `tamma_app` | Used by the Node.js application | Subject to RLS — sees only current tenant's rows |

If the application currently connects as a superuser, a new `tamma_app` role must be created and granted the necessary table permissions.

### Files to Create

| File | Purpose |
|------|---------|
| `database/migrations/009_rls_tenant_isolation.sql` | Enable RLS, create policies, create app role |
| `packages/api/src/persistence/__tests__/rls-tenant-isolation.integration.test.ts` | Integration test proving cross-tenant isolation |

### Files to Modify

| File | Change |
|------|--------|
| `packages/api/src/persistence/pg-tenant-store.ts` | SET session variable before queries (or rely on middleware) |
| `packages/api/src/persistence/__tests__/pg-test-helper.ts` | Add helper to set `app.current_tenant_id` in test connections |

## Implementation Plan

### Step 1: Create the Application Role

```sql
-- Create a non-superuser role for the application
DO $$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'tamma_app') THEN
    CREATE ROLE tamma_app LOGIN PASSWORD 'changeme';
  END IF;
END $$;

-- Grant necessary permissions
GRANT CONNECT ON DATABASE tamma TO tamma_app;
GRANT USAGE ON SCHEMA public TO tamma_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO tamma_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO tamma_app;

-- Ensure future tables also get permissions
ALTER DEFAULT PRIVILEGES IN SCHEMA public
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO tamma_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public
  GRANT USAGE, SELECT ON SEQUENCES TO tamma_app;
```

### Step 2: Enable RLS and Create Policies

```sql
-- tenants table: can only see own tenant record
ALTER TABLE tenants ENABLE ROW LEVEL SECURITY;
ALTER TABLE tenants FORCE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation_policy ON tenants
  USING (id = current_setting('app.current_tenant_id', true)::uuid)
  WITH CHECK (id = current_setting('app.current_tenant_id', true)::uuid);

-- github_installations
ALTER TABLE github_installations ENABLE ROW LEVEL SECURITY;
ALTER TABLE github_installations FORCE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation_policy ON github_installations
  USING (tenant_id = current_setting('app.current_tenant_id', true)::uuid)
  WITH CHECK (tenant_id = current_setting('app.current_tenant_id', true)::uuid);

-- users
ALTER TABLE users ENABLE ROW LEVEL SECURITY;
ALTER TABLE users FORCE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation_policy ON users
  USING (tenant_id = current_setting('app.current_tenant_id', true)::uuid)
  WITH CHECK (tenant_id = current_setting('app.current_tenant_id', true)::uuid);

-- user_api_keys
ALTER TABLE user_api_keys ENABLE ROW LEVEL SECURITY;
ALTER TABLE user_api_keys FORCE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation_policy ON user_api_keys
  USING (tenant_id = current_setting('app.current_tenant_id', true)::uuid)
  WITH CHECK (tenant_id = current_setting('app.current_tenant_id', true)::uuid);

-- user_invites
ALTER TABLE user_invites ENABLE ROW LEVEL SECURITY;
ALTER TABLE user_invites FORCE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation_policy ON user_invites
  USING (tenant_id = current_setting('app.current_tenant_id', true)::uuid)
  WITH CHECK (tenant_id = current_setting('app.current_tenant_id', true)::uuid);
```

### Step 3: Prevent tenant_id Mutation

The WITH CHECK clause on UPDATE already prevents inserting a row with the wrong tenant_id. To additionally prevent changing `tenant_id` on an existing row:

```sql
-- Trigger to prevent tenant_id changes on UPDATE
CREATE OR REPLACE FUNCTION prevent_tenant_id_change()
RETURNS TRIGGER AS $$
BEGIN
  IF OLD.tenant_id IS DISTINCT FROM NEW.tenant_id THEN
    RAISE EXCEPTION 'Cannot change tenant_id on existing row';
  END IF;
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_prevent_tenant_change_installations
  BEFORE UPDATE ON github_installations
  FOR EACH ROW EXECUTE FUNCTION prevent_tenant_id_change();

CREATE TRIGGER trg_prevent_tenant_change_users
  BEFORE UPDATE ON users
  FOR EACH ROW EXECUTE FUNCTION prevent_tenant_id_change();

CREATE TRIGGER trg_prevent_tenant_change_api_keys
  BEFORE UPDATE ON user_api_keys
  FOR EACH ROW EXECUTE FUNCTION prevent_tenant_id_change();

CREATE TRIGGER trg_prevent_tenant_change_invites
  BEFORE UPDATE ON user_invites
  FOR EACH ROW EXECUTE FUNCTION prevent_tenant_id_change();
```

### Step 4: FORCE vs ENABLE

- `ENABLE ROW LEVEL SECURITY` makes RLS apply to non-owner roles.
- `FORCE ROW LEVEL SECURITY` makes RLS apply even to the table owner.

Using `FORCE` is safer because if the application accidentally connects as the table owner, RLS still applies. The migration role (superuser) bypasses RLS via the `BYPASSRLS` attribute.

### Step 5: Connection Pool Integration

The Node.js `pg.Pool` must set the session variable on each checkout:

```typescript
// Called by middleware (Story 17.5) before any query
async function setTenantContext(client: pg.PoolClient, tenantId: string): Promise<void> {
  await client.query('SET app.current_tenant_id = $1', [tenantId]);
}
```

For pooled connections, this must happen after `pool.connect()` and before any business query. The middleware in Story 17.5 handles this.

## Implementation Notes

1. `FORCE ROW LEVEL SECURITY` is used on all tables so that even if the application connects as the table owner, RLS applies. Only a SUPERUSER or BYPASSRLS role can bypass.
2. The `current_setting('app.current_tenant_id', true)` form (with `true` as second arg) returns NULL when not set, causing all comparisons to fail (fail-closed).
3. The `github_installation_repos` and `user_installations` tables do NOT get direct RLS policies. They are join tables accessed through FK relationships. If direct access is needed later, RLS can be added.
4. Performance: The B-tree indexes on `tenant_id` (created in Story 17.1) ensure RLS filter predicates use index scans, not sequential scans. On small tables (< 100K rows), the overhead is negligible.
5. The migration creates the `tamma_app` role with a placeholder password. The actual password must be configured via environment variable in production.
6. If the database currently uses a single superuser for everything, the application's connection string must be updated to use `tamma_app` after this migration. This is a deployment concern documented in the migration file.

## Testing Strategy

### Unit Tests

1. Verify the migration SQL is syntactically valid (parse test)
2. Verify `FORCE ROW LEVEL SECURITY` is used (not just `ENABLE`)
3. Verify all five tables have policies defined

### Integration Tests

Create `packages/api/src/persistence/__tests__/rls-tenant-isolation.integration.test.ts`:

4. **Cross-tenant read isolation**: Connect as `tamma_app`, SET tenant A, insert a user row, SET tenant B, SELECT from users => zero rows
5. **Same-tenant read**: SET tenant A, insert row, SELECT => returns the row
6. **Cross-tenant write rejection**: SET tenant B, INSERT with `tenant_id = tenant_A_id` => rejected by WITH CHECK
7. **Fail-closed when unset**: RESET `app.current_tenant_id`, SELECT from users => zero rows (not an error)
8. **tenant_id mutation blocked**: SET tenant A, UPDATE row SET tenant_id = tenant_B_id => trigger raises exception
9. **Superuser bypass**: Connect as migration role (superuser), SELECT without setting tenant => returns all rows
10. **Performance**: EXPLAIN ANALYZE on a query with RLS shows index scan on `tenant_id`, not seq scan

### Manual Verification

11. Run `\d+ users` in psql and confirm "Policies" section shows `tenant_isolation_policy`
12. Run `SELECT * FROM pg_policies WHERE tablename = 'users'` to verify policy definition

## Dependencies

- **Story 17.1** (Tenant Model + Database Schema) — `tenants` table and `tenant_id` columns must exist
- Internal: `database/migrations/008_tenants.sql` must have been applied

## Estimated Effort

| Task | Hours |
|------|-------|
| Application role creation SQL | 1 |
| RLS policies for 5 tables | 2 |
| tenant_id mutation triggers | 1 |
| pg-test-helper updates | 1 |
| Integration tests (cross-tenant isolation) | 3 |
| Performance validation (EXPLAIN ANALYZE) | 1 |
| Documentation (connection string change, deployment notes) | 1 |
| Migration testing on staging | 2 |
| **Total** | **12 hours** |

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-28 | 1.0 | Initial story creation | Architecture Team |
