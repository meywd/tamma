-- 013_agent_configs.sql
-- Story 9-1: Agent Config Schema + API
--
-- Per-account agent configuration stored as JSONB.
-- System defaults use account_id IS NULL (unique constraint allows one NULL row).
-- Account-level overrides reference the tenants table.

-- 1. Create agent_configs table
CREATE TABLE IF NOT EXISTS agent_configs (
  id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  account_id    UUID NULL REFERENCES tenants(id) ON DELETE CASCADE,
  config        JSONB NOT NULL,
  version       INTEGER NOT NULL DEFAULT 1,
  created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  created_by    UUID NULL,
  updated_by    UUID NULL
);

-- Unique constraint: one row per account_id (including NULL for system defaults)
CREATE UNIQUE INDEX IF NOT EXISTS idx_agent_configs_account_id
  ON agent_configs (account_id)
  WHERE account_id IS NOT NULL;

-- Unique partial index for the system default row (account_id IS NULL)
CREATE UNIQUE INDEX IF NOT EXISTS idx_agent_configs_system_default
  ON agent_configs ((1))
  WHERE account_id IS NULL;

-- 2. Seed system defaults
INSERT INTO agent_configs (account_id, config, version)
VALUES (
  NULL,
  '{
    "agents": {
      "defaults": {
        "providerChain": [{"provider": "claude-code"}],
        "maxBudgetUsd": 5.0
      }
    },
    "security": {
      "sanitizeContent": true,
      "validateUrls": true,
      "gateActions": false,
      "maxFetchSizeBytes": 10485760,
      "blockedCommandPatterns": ["rm\\s+-rf\\s+/", "DROP\\s+TABLE", "DELETE\\s+FROM"]
    }
  }'::jsonb,
  1
)
ON CONFLICT DO NOTHING;
