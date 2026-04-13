-- 011_tenant_scoped_stores.sql
-- Stories 17-3 + 17-4: Tenant-Scoped Event Store & Workflow Instances
--
-- Creates engine_events and workflow_instances tables with tenant_id,
-- RLS policies, and indexes for tenant-scoped queries.

-- =========================================================================
-- 1. Engine Events table (DCB event store)
-- =========================================================================
CREATE TABLE IF NOT EXISTS engine_events (
  id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  type          TEXT NOT NULL,
  timestamp     BIGINT NOT NULL DEFAULT (EXTRACT(EPOCH FROM NOW()) * 1000)::BIGINT,
  tenant_id     UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'
                REFERENCES tenants(id),
  issue_number  INTEGER,
  data          JSONB NOT NULL DEFAULT '{}',
  created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_engine_events_tenant_id
  ON engine_events (tenant_id);

CREATE INDEX IF NOT EXISTS idx_engine_events_tenant_issue
  ON engine_events (tenant_id, issue_number)
  WHERE issue_number IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_engine_events_tenant_type
  ON engine_events (tenant_id, type);

-- RLS
ALTER TABLE engine_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE engine_events FORCE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation_policy ON engine_events
  USING (tenant_id = current_setting('app.current_tenant_id', true)::uuid)
  WITH CHECK (tenant_id = current_setting('app.current_tenant_id', true)::uuid);

-- Prevent tenant_id mutation
CREATE TRIGGER trg_prevent_tenant_change_engine_events
  BEFORE UPDATE ON engine_events
  FOR EACH ROW EXECUTE FUNCTION prevent_tenant_id_change();

-- =========================================================================
-- 2. Workflow Instances table
-- =========================================================================
CREATE TABLE IF NOT EXISTS workflow_instances (
  id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  definition_id   TEXT NOT NULL,
  tenant_id       UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'
                  REFERENCES tenants(id),
  status          TEXT NOT NULL DEFAULT 'pending',
  current_activity TEXT,
  variables       JSONB NOT NULL DEFAULT '{}',
  created_at      BIGINT NOT NULL DEFAULT (EXTRACT(EPOCH FROM NOW()) * 1000)::BIGINT,
  updated_at      BIGINT NOT NULL DEFAULT (EXTRACT(EPOCH FROM NOW()) * 1000)::BIGINT
);

CREATE INDEX IF NOT EXISTS idx_workflow_instances_tenant_id
  ON workflow_instances (tenant_id);

CREATE INDEX IF NOT EXISTS idx_workflow_instances_tenant_definition
  ON workflow_instances (tenant_id, definition_id);

CREATE INDEX IF NOT EXISTS idx_workflow_instances_tenant_status
  ON workflow_instances (tenant_id, status);

-- RLS
ALTER TABLE workflow_instances ENABLE ROW LEVEL SECURITY;
ALTER TABLE workflow_instances FORCE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation_policy ON workflow_instances
  USING (tenant_id = current_setting('app.current_tenant_id', true)::uuid)
  WITH CHECK (tenant_id = current_setting('app.current_tenant_id', true)::uuid);

-- Prevent tenant_id mutation
CREATE TRIGGER trg_prevent_tenant_change_workflow_instances
  BEFORE UPDATE ON workflow_instances
  FOR EACH ROW EXECUTE FUNCTION prevent_tenant_id_change();

-- =========================================================================
-- 3. Grant permissions to tamma_app role (if it exists)
-- =========================================================================
DO $$
BEGIN
  IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'tamma_app') THEN
    GRANT SELECT, INSERT, UPDATE, DELETE ON engine_events TO tamma_app;
    GRANT SELECT, INSERT, UPDATE, DELETE ON workflow_instances TO tamma_app;
  END IF;
END $$;
