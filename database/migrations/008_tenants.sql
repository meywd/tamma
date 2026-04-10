-- 008_tenants.sql
-- Story 17-1: Tenant Model + Database Schema
--
-- Introduces a tenants table and adds tenant_id foreign keys to
-- github_installations, users, user_api_keys, and user_invites.
-- A sentinel "default" tenant is inserted for CLI/self-hosted mode.

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

CREATE INDEX IF NOT EXISTS idx_tenants_deleted_at ON tenants (deleted_at) WHERE deleted_at IS NULL;
CREATE INDEX IF NOT EXISTS idx_tenants_external_id ON tenants (external_id) WHERE external_id IS NOT NULL;

-- 2. Insert default tenant sentinel
INSERT INTO tenants (id, name, slug, external_id, plan)
VALUES ('00000000-0000-0000-0000-000000000000', 'Default', 'default', NULL, 'free')
ON CONFLICT (id) DO NOTHING;

-- 3. Add tenant_id to github_installations
ALTER TABLE github_installations
  ADD COLUMN IF NOT EXISTS tenant_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'
  REFERENCES tenants(id);
CREATE INDEX IF NOT EXISTS idx_installations_tenant_id ON github_installations (tenant_id);

-- 4. Add tenant_id to users (nullable — "active tenant" shortcut, not the canonical relationship)
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
