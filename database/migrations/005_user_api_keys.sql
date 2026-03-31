-- Per-user API keys for authenticating with the Tamma API.
-- Keys are stored as HMAC-SHA256 hashes; the raw key is shown only once at creation time.

CREATE TABLE IF NOT EXISTS user_api_keys (
  id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id       UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  key_hash      TEXT NOT NULL UNIQUE,
  key_prefix    TEXT NOT NULL,
  label         TEXT NOT NULL DEFAULT 'default',
  last_used_at  TIMESTAMPTZ,
  created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  revoked_at    TIMESTAMPTZ
);

CREATE INDEX idx_user_api_keys_user_id ON user_api_keys (user_id);
CREATE INDEX idx_user_api_keys_key_hash ON user_api_keys (key_hash);
