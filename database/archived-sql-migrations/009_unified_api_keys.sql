-- 009_unified_api_keys.sql
-- Story 16-7: Service-to-Service Authentication
--
-- Creates a unified api_keys table that supports three scopes:
--   - user: per-user keys (migrated from user_api_keys)
--   - installation: per-GitHub-App-installation keys (migrated from github_installations)
--   - service: service-to-service keys (new, for Elsa, tamma-api-dotnet, etc.)
--
-- Existing tables (user_api_keys, github_installations) are left in place
-- for one release cycle as a rollback safety net.

-- 1. Create unified api_keys table
CREATE TABLE IF NOT EXISTS api_keys (
  id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  scope         TEXT NOT NULL CHECK (scope IN ('user', 'installation', 'service')),
  owner_id      TEXT NOT NULL,               -- user_id UUID | installation_id bigint | service_name text
  key_hash      TEXT NOT NULL UNIQUE,
  key_prefix    TEXT NOT NULL,
  label         TEXT NOT NULL DEFAULT 'default',
  permissions   JSONB NOT NULL DEFAULT '[]'::jsonb,  -- ['prompts:read','diagnostics:write',...] (service scope only)
  tenant_id     UUID REFERENCES tenants(id), -- NULL for service keys; set for user/installation
  created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  last_used_at  TIMESTAMPTZ,
  revoked_at    TIMESTAMPTZ,                 -- may be in the future during rotation grace period
  rotated_from  UUID REFERENCES api_keys(id) -- track rotation chain
);

CREATE INDEX IF NOT EXISTS idx_api_keys_key_hash ON api_keys (key_hash);
CREATE INDEX IF NOT EXISTS idx_api_keys_scope_owner ON api_keys (scope, owner_id);
CREATE INDEX IF NOT EXISTS idx_api_keys_active ON api_keys (scope) WHERE revoked_at IS NULL;

-- 2. Copy existing user API keys into the unified table
INSERT INTO api_keys (id, scope, owner_id, key_hash, key_prefix, label, tenant_id, created_at, last_used_at, revoked_at)
SELECT
  id,
  'user',
  user_id::text,
  key_hash,
  key_prefix,
  label,
  tenant_id,
  created_at,
  last_used_at,
  revoked_at
FROM user_api_keys
ON CONFLICT (key_hash) DO NOTHING;

-- 3. Copy existing installation API keys into the unified table
-- Only installations that have an api_key_hash set
INSERT INTO api_keys (scope, owner_id, key_hash, key_prefix, label, tenant_id, created_at)
SELECT
  'installation',
  gi.installation_id::text,
  gi.api_key_hash,
  COALESCE(gi.api_key_prefix, LEFT(gi.api_key_hash, 12)),
  'GitHub App installation',
  gi.tenant_id,
  gi.created_at
FROM github_installations gi
WHERE gi.api_key_hash IS NOT NULL
ON CONFLICT (key_hash) DO NOTHING;
