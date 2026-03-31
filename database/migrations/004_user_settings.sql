-- Add settings JSONB column to users table for per-user provider configuration.
-- In SaaS mode, this is equivalent to ~/.tamma/providers.json in CLI mode.
ALTER TABLE users ADD COLUMN settings JSONB DEFAULT '{}' NOT NULL;

-- Comment for documentation
COMMENT ON COLUMN users.settings IS 'User-level provider config (IProvidersConfig): provider credentials, models, budgets. Equivalent to ~/.tamma/providers.json in CLI mode.';
