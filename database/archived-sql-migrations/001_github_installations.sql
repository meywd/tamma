-- GitHub App installations and their associated repositories
-- Created as part of the GitHub App integration feature

CREATE TABLE IF NOT EXISTS github_installations (
  installation_id   BIGINT PRIMARY KEY,
  account_login     TEXT NOT NULL,
  account_type      TEXT NOT NULL CHECK (account_type IN ('User', 'Organization')),
  app_id            BIGINT NOT NULL,
  permissions       JSONB NOT NULL DEFAULT '{}',
  suspended_at      TIMESTAMPTZ,
  created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at        TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS github_installation_repos (
  id                BIGSERIAL PRIMARY KEY,
  installation_id   BIGINT NOT NULL REFERENCES github_installations(installation_id) ON DELETE CASCADE,
  repo_id           BIGINT NOT NULL,
  owner             TEXT NOT NULL,
  name              TEXT NOT NULL,
  full_name         TEXT NOT NULL,
  is_active         BOOLEAN NOT NULL DEFAULT TRUE,
  created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  UNIQUE (installation_id, repo_id)
);

CREATE INDEX IF NOT EXISTS idx_installation_repos_full_name
  ON github_installation_repos (full_name);

CREATE INDEX IF NOT EXISTS idx_installation_repos_installation_id
  ON github_installation_repos (installation_id);

CREATE INDEX IF NOT EXISTS idx_installations_account_login
  ON github_installations (account_login);
