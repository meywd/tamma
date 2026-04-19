# Finding 011: `user_api_keys` legacy table absent — no copy-migration replayed

**Scope**: admin-db
**Severity**: P1
**Status**: Data-model regression
**Estimated port effort**: 2h

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed (Option 1 — explicit DROP, no copy-migration)
- **Notes**: Per CLAUDE.md "No migration anxiety", the cold-install approach is canonical. `Program.cs` wipe-list now includes `user_api_keys`, `user_installations`, `tenant_invites`, `email_outbox`, and `queued_tasks` so stray legacy tables don't persist as dead weight. No copy-in migration is added — Option 2 in the finding is explicitly out of scope.

## 1. What's in TS

Archived at `database/archived-sql-migrations/005_user_api_keys.sql` and `009_unified_api_keys.sql`.

- File: `packages/api/database/migrations/005_user_api_keys.sql`, `009_unified_api_keys.sql`
- Contract/behavior: migration 005 created the original per-user API-key table. Migration 009 created the unified `api_keys` table and **copied** rows from `user_api_keys` into it, leaving the legacy table in place as a rollback safety net. The TS runtime read from `api_keys` going forward; `user_api_keys` was kept for one release cycle.
- Key code (verbatim quote, annotated):

```sql
-- 005_user_api_keys.sql
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

-- 009_unified_api_keys.sql
-- 2. Copy existing user API keys into the unified table
INSERT INTO api_keys (id, scope, owner_id, key_hash, key_prefix, label, tenant_id, created_at, last_used_at, revoked_at)
SELECT
  id,
  'user',
  user_id::text,
  key_hash, key_prefix, label,
  tenant_id,
  created_at, last_used_at, revoked_at
FROM user_api_keys
ON CONFLICT (key_hash) DO NOTHING;
```

- Dependencies: migration 009 assumes the 005 table already exists with data to copy.
- Tests that exercised this: API-key auth flow.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/Migrations/*.cs` — no `user_api_keys` table anywhere.
- Contract/behavior: C# starts fresh with the unified `api_keys` table. If a DB were migrated from TS (which stored user keys in `user_api_keys`), those rows would **not be replayed** because the C# `InitialSchema` migration is the first EF migration — it doesn't know `user_api_keys` ever existed.
- Key code: n/a (absence).
- Dependencies: the `Program.cs:556-567` wipe logic explicitly drops `user_api_keys` if it exists, so cross-over is hostile, not accommodative:

```csharp
// apps/tamma-elsa/src/Tamma.Api/Program.cs (current)
dbContext.Database.ExecuteSqlRaw(@"
    DROP TABLE IF EXISTS
        api_keys, agent_configs, domain_events, ...
        password_reset_tokens, prompt_overrides,
        ...
        user_invites, users, workflow_definitions, workflow_instances,
        knex_migrations, knex_migrations_lock,
        ""__TammaMigrationsHistory""
    CASCADE;");
// Note: user_api_keys is NOT in the drop list — it would persist as dead weight.
```

- Tests: none.

## 3. The gap

- TS did: migrate `user_api_keys` rows into `api_keys` so no user lost their existing API keys.
- C# does: expect a cold install (CLAUDE.md "No migration anxiety: App is not in production with users"). If anyone did run TS migrations then C# migrations against the same database, `user_api_keys` rows would be orphaned.
- For a caller with a TS-era API key `uk_abcdef…`, TS still validates it (post-009) because the row was copied into `api_keys`; C# does not validate (row was never copied).
- In production: per the CLAUDE.md directive, this is non-blocking. It becomes blocking if we ever turn on data preservation or restore from a TS-era backup. There's also a latent hazard: `user_api_keys` is absent from the DROP list in `Program.cs:556-567`, so a stray `user_api_keys` table could persist indefinitely with no owner.

Error paths:
- TS: rows preserved across migrations.
- C#: rows orphaned.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-16/16-7-service-to-service-auth.md` and migration 009 comments.
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

CLAUDE.md "No migration anxiety: App is not in production with users" explicitly authorizes the cold-install approach.

## 5. Status

- **Classification**: Data-model regression — but explicitly authorized by CLAUDE.md
- **What's needed to finish**: either:
  1. Add `user_api_keys` to `Program.cs:556-567`'s DROP list to make the hostility explicit; or
  2. Add a one-shot migration that copies rows from `user_api_keys` into `api_keys` if the legacy table exists (harmless if it doesn't).
- **Is it "just a stub" or is scope missing?** Scope was intentional per CLAUDE.md. The inconsistency is that the DROP list doesn't enumerate `user_api_keys` — an oversight.
- **Blockers**: none.

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Program.cs:556-567` (add `user_api_keys` to DROP list).
- Files to create: none.
- Tests to add: startup on a DB containing a stale `user_api_keys` table — expect clean wipe.
- Estimated effort: 2h, of which 1.5h is the optional copy-migration.

## References

- TS source: `database/archived-sql-migrations/005_user_api_keys.sql`, `009_unified_api_keys.sql`
- C# source: `apps/tamma-elsa/src/Tamma.Data/Migrations/`, `apps/tamma-elsa/src/Tamma.Api/Program.cs`
- Story: none
- Related findings: `016-schema-api-keys-diff.md`
- CLAUDE.md section: "No migration anxiety"
