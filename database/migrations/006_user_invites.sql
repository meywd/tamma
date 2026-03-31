-- User invitations for onboarding new users with a pre-assigned role.
-- The invite_token is stored as-is (hashed by app layer if desired) and looked up on OAuth callback.

CREATE TABLE IF NOT EXISTS user_invites (
  id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  email         TEXT,
  role          TEXT NOT NULL DEFAULT 'member' CHECK (role IN ('owner', 'admin', 'member')),
  invite_token  TEXT NOT NULL UNIQUE,
  invited_by    UUID NOT NULL REFERENCES users(id),
  expires_at    TIMESTAMPTZ NOT NULL,
  accepted_at   TIMESTAMPTZ,
  created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_user_invites_token ON user_invites (invite_token);
