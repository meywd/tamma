-- Migration 016: Sanitization Rules
-- Story 9-7: Sanitization Service + API
--
-- Per-account sanitization rule configuration. Account admins can customize
-- sanitization behavior (extra injection patterns, blocked commands, etc.).
-- Falls back to system defaults when no account-specific rules exist.

BEGIN;

CREATE TABLE IF NOT EXISTS sanitization_rules (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  account_id UUID NULL REFERENCES tenants(id) ON DELETE CASCADE,
  enabled BOOLEAN NOT NULL DEFAULT true,
  extra_injection_patterns TEXT[] DEFAULT '{}',
  blocked_command_patterns TEXT[] DEFAULT '{}',
  max_fetch_size_bytes INTEGER DEFAULT 10485760,
  validate_urls BOOLEAN DEFAULT true,
  gate_actions BOOLEAN DEFAULT true,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  UNIQUE (account_id)
);

COMMIT;
