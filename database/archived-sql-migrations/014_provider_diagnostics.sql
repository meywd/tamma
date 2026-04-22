-- Migration 014: Provider Diagnostics
-- Story 9-2: Diagnostics Service + API
--
-- Stores per-call diagnostics records for provider usage tracking,
-- cost reporting, and budget monitoring. Both the TS engine (via in-process
-- calls) and Elsa workflows (via HTTP API) write to this table.

BEGIN;

CREATE TABLE IF NOT EXISTS provider_diagnostics (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  account_id UUID NULL REFERENCES tenants(id),
  event_type TEXT NOT NULL,
  provider_name TEXT NOT NULL,
  model TEXT,
  agent_type TEXT,
  project_id TEXT,
  engine_id TEXT,
  task_id TEXT,
  task_type TEXT,
  input_tokens INTEGER DEFAULT 0,
  output_tokens INTEGER DEFAULT 0,
  latency_ms INTEGER DEFAULT 0,
  cost_usd NUMERIC(12, 6) DEFAULT 0,
  success BOOLEAN NOT NULL DEFAULT false,
  error_code TEXT,
  error_message TEXT,
  correlation_id UUID,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_diagnostics_account_created ON provider_diagnostics (account_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_diagnostics_provider ON provider_diagnostics (provider_name, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_diagnostics_model ON provider_diagnostics (model, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_diagnostics_event_type ON provider_diagnostics (event_type, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_diagnostics_engine ON provider_diagnostics (engine_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_diagnostics_correlation ON provider_diagnostics (correlation_id) WHERE correlation_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_diagnostics_budget ON provider_diagnostics (account_id, created_at) WHERE success = true;

COMMIT;
