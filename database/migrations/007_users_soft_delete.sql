-- Add soft-delete and activity tracking columns to the users table.

ALTER TABLE users ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMPTZ;
ALTER TABLE users ADD COLUMN IF NOT EXISTS last_active_at TIMESTAMPTZ;

-- Partial index for fast lookups of non-deleted users.
CREATE INDEX idx_users_deleted_at ON users (deleted_at) WHERE deleted_at IS NULL;
