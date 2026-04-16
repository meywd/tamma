-- Migration 017: Tenant Memberships + Invites
-- Story 18-3: Organization/Tenant Creation
--
-- Creates tenant_memberships (M:N between users and tenants) and
-- tenant_invites tables. Organizations ARE tenants from Epic 17.

-- Tenant memberships (M:N relationship between users and tenants)
CREATE TABLE IF NOT EXISTS tenant_memberships (
  tenant_id   UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  user_id     UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  role        TEXT NOT NULL DEFAULT 'member' CHECK (role IN ('owner', 'admin', 'member')),
  joined_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  PRIMARY KEY (tenant_id, user_id)
);

CREATE INDEX IF NOT EXISTS idx_tenant_memberships_user_id ON tenant_memberships(user_id);

-- Tenant invites
CREATE TABLE IF NOT EXISTS tenant_invites (
  id                UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id         UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  email             TEXT NOT NULL,
  role              TEXT NOT NULL DEFAULT 'member' CHECK (role IN ('owner', 'admin', 'member')),
  invite_token_hash TEXT NOT NULL UNIQUE,
  invited_by        UUID NOT NULL REFERENCES users(id),
  expires_at        TIMESTAMPTZ NOT NULL,
  accepted_at       TIMESTAMPTZ,
  created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_tenant_invites_tenant_id ON tenant_invites(tenant_id);
CREATE INDEX IF NOT EXISTS idx_tenant_invites_email ON tenant_invites(email);
