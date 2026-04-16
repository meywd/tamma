-- Users and their association to GitHub App installations
-- Created as part of the GitHub App integration feature

CREATE TABLE IF NOT EXISTS users (
  id                UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  github_id         BIGINT UNIQUE NOT NULL,
  github_login      TEXT NOT NULL,
  email             TEXT,
  role              TEXT NOT NULL DEFAULT 'member' CHECK (role IN ('owner', 'admin', 'member')),
  created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at        TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS user_installations (
  user_id           UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  installation_id   BIGINT NOT NULL REFERENCES github_installations(installation_id) ON DELETE CASCADE,
  role              TEXT NOT NULL DEFAULT 'member' CHECK (role IN ('owner', 'admin', 'member')),
  created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  PRIMARY KEY (user_id, installation_id)
);

CREATE INDEX IF NOT EXISTS idx_users_github_id
  ON users (github_id);

CREATE INDEX IF NOT EXISTS idx_user_installations_installation_id
  ON user_installations (installation_id);
