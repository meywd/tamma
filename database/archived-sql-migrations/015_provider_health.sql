-- Migration 015: Provider Health
-- Story 9-3: Health Tracker Service + API
--
-- Persists circuit breaker state so that failures recorded by one caller
-- (TS engine or Elsa workflow) trip the circuit for all callers.

BEGIN;

CREATE TABLE IF NOT EXISTS provider_health (
  key TEXT PRIMARY KEY,
  circuit_open BOOLEAN NOT NULL DEFAULT false,
  circuit_open_until TIMESTAMPTZ,
  failure_count INTEGER NOT NULL DEFAULT 0,
  last_failure_at TIMESTAMPTZ,
  last_success_at TIMESTAMPTZ,
  half_open_in_progress BOOLEAN NOT NULL DEFAULT false,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_provider_health_open
  ON provider_health (circuit_open)
  WHERE circuit_open = true;

COMMIT;
