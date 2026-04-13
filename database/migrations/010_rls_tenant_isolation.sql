-- 010_rls_tenant_isolation.sql
-- Story 17-2: Row-Level Security (RLS) for Tenant Isolation
--
-- Creates a non-superuser application role (tamma_app), enables RLS on all
-- tenant-scoped tables, and adds policies + triggers to enforce isolation.
--
-- Tables covered: tenants, github_installations, users, user_api_keys, user_invites
-- Tables exempt: prompts, system_prompts, action_prompts (need cross-tenant reads for defaults)

-- =========================================================================
-- 1. Create the application role (non-superuser, subject to RLS)
-- =========================================================================
DO $$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'tamma_app') THEN
    CREATE ROLE tamma_app LOGIN PASSWORD 'changeme';
  END IF;
END $$;

-- Grant necessary permissions
DO $$
BEGIN
  EXECUTE format('GRANT CONNECT ON DATABASE %I TO tamma_app', current_database());
END $$;
GRANT USAGE ON SCHEMA public TO tamma_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO tamma_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO tamma_app;

-- Ensure future tables also get permissions
ALTER DEFAULT PRIVILEGES IN SCHEMA public
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO tamma_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public
  GRANT USAGE, SELECT ON SEQUENCES TO tamma_app;

-- =========================================================================
-- 2. Enable RLS and create policies
-- =========================================================================

-- tenants table: self-referencing policy (can only see own tenant record)
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

-- =========================================================================
-- 3. Prevent tenant_id mutation via trigger
-- =========================================================================
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
