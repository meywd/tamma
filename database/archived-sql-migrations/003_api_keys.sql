-- API key columns for GitHub App installations
-- Supports SaaS API key provisioning and GitHub Secrets setup

ALTER TABLE github_installations
  ADD COLUMN api_key_hash TEXT,
  ADD COLUMN api_key_prefix TEXT,
  ADD COLUMN api_key_encrypted TEXT;

CREATE INDEX idx_installations_api_key_hash ON github_installations (api_key_hash);
