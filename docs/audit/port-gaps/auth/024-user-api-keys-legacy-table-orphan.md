# Finding 024: `user_api_keys` legacy table absent — no consolidation path

**Scope**: auth
**Severity**: P2 (data-model regression; depends on prod-data state)
**Status**: Data-model regression
**Estimated port effort**: 2h

## 1. What's in TS

Pre-delete snapshots at archived SQL migrations.

- Migration `005_user_api_keys.sql` introduced a dedicated per-user keys table:

```sql
-- database/archived-sql-migrations/005_user_api_keys.sql
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
```

- Migration `009_unified_api_keys.sql` introduced the unified `api_keys` table and **copied data from `user_api_keys` into it**:

```sql
-- database/archived-sql-migrations/009_unified_api_keys.sql:32-46
INSERT INTO api_keys (id, scope, owner_id, key_hash, key_prefix, label, tenant_id, created_at, last_used_at, revoked_at)
SELECT
  id, 'user', user_id::text, key_hash, key_prefix, label,
  tenant_id, created_at, last_used_at, revoked_at
FROM user_api_keys
ON CONFLICT (key_hash) DO NOTHING;
```

- The comment at migration top lines 9-10: *"Existing tables (user_api_keys, github_installations) are left in place for one release cycle as a rollback safety net."*
- So in a TS production deployment, both tables exist:
  - `user_api_keys` — legacy rows (rollback safety net).
  - `api_keys` — unified table with `scope='user'` rows copied over.

- TS stores: `IUserApiKeyStore` (pre-unification) and `IApiKeyStore` (unified). Routes depending on user-scope auth went through `IApiKeyStore` by migration 009 era.
- Callers: routes/users/api-key-routes.ts used `IUserApiKeyStore` still (dual-write?) — source shows it took `apiKeyStore: IUserApiKeyStore` as option (line 12-13 of api-key-routes.ts).

## 2. What's in C#

Current state on `feat/auth-foundation`.

- EF schema in `InitialSchema` migration: has `api_keys` (unified) but **no `user_api_keys` table**. Grep of migrations shows no reference to `user_api_keys`.
- No `UserApiKey` entity exists in `Tamma.Data.Entities`.
- Deployment bootstrap in `Program.cs:555-566` DROPs a list of known tables including `user_api_keys`... wait, let me re-check.

```csharp
// apps/tamma-elsa/src/Tamma.Api/Program.cs:555-567
dbContext.Database.ExecuteSqlRaw(@"
    DROP TABLE IF EXISTS
        api_keys, agent_configs, domain_events,
        github_installation_repos, github_installations,
        junior_developers, mentorship_events, mentorship_sessions,
        password_reset_tokens, prompt_overrides,
        provider_diagnostics, provider_health, refresh_tokens,
        sanitization_rules, stories, tenant_memberships, tenants,
        user_invites, users, workflow_definitions, workflow_instances,
        knex_migrations, knex_migrations_lock,
        ""__TammaMigrationsHistory""
    CASCADE;");
```

- The drop list does NOT include `user_api_keys`. So if a production DB has a `user_api_keys` table from the TS era, the deploy wipe script leaves it **orphaned**. The table still exists, still has rows, but nothing in the C# schema references it.

- Additionally, there is no "copy legacy rows into api_keys" analog to migration 009. So even if `user_api_keys` is kept, its rows are never consolidated into the new `api_keys`.

## 3. The gap

Three compounded states depending on what production has:

1. **Fresh deploy (greenfield DB)**: `user_api_keys` never existed. No rows to migrate. Everything works via `api_keys`. Low risk.
2. **Cutover from TS with ≥1-release rollback safety net**: `user_api_keys` exists alongside `api_keys`. Drop-script on deploy misses `user_api_keys`. The table persists as an orphan — it's never queried, never written, never dropped. Data just sits there. No security issue (unused), but a schema-drift embarrassment and a compliance concern ("what data exists in prod?").
3. **Cutover from TS without rollback safety net but with data that was never consolidated**: if migration 009 was never applied cleanly, `user_api_keys` has rows that were never copied to `api_keys`. On cutover, those user-scope keys are invisible to the C# auth handler, which only queries `api_keys`. Users lose their keys.

Scenario 3 is the worst case. The TS wiki/runbook likely assumed 009 was always applied before a cutover, but nothing in the C# side verifies this or protects against it.

**Paired with Finding 003** (API key hash algorithm): even if `user_api_keys` rows WERE copied to `api_keys`, they'd be unverifiable because they were hashed with scrypt, and C# uses SHA-256. So the migration of legacy data fails at the auth layer anyway (see Finding 003).

Error paths:
- Orphaned table: no error — silently ignored.
- Un-migrated rows: auth handler returns 401 "Invalid API key" for keys that existed before.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-16/16-7-service-to-service-auth.md` (introduced `api_keys` unification).
- Story does not explicitly describe the legacy-cleanup strategy — that was operational lore.
- CLAUDE.md section: *"No migration anxiety: App is not in production with users. All data stores can be replaced without migration."* — this line explicitly licenses losing legacy data. So for the stated project posture, this finding is acceptable.
- But for any future production environment that mutates through the TS→C# transition:
  - [x] Matches TS behavior (partial: TS had the table)
  - [ ] Matches C# behavior
  - [x] Describes a third behavior (greenfield assumption in CLAUDE.md)
  - [ ] No story — operational migration play not covered

## 5. Status

- **Classification**: Data-model regression (silent). Per CLAUDE.md greenfield policy, this is arguably intentional.
- **What's needed to finish**:
  1. (If honoring CLAUDE.md greenfield policy) Add `user_api_keys` to the Program.cs:555 drop list for defensive cleanup on boot.
  2. (If honoring legacy data) Add a one-time EF migration that:
     - Copies `user_api_keys` rows into `api_keys` with `scope='user'`, rehashing if the direction from Finding 003 is to move to SHA-256.
     - Drops `user_api_keys` post-copy.
  3. Document the chosen direction in an ADR under `.dev/decisions/`.
- **Is it "just a stub" or is scope missing?** Scope deliberately skipped per CLAUDE.md; depending on ops decision.
- **Blockers**: Finding 003 (rehash strategy if copying legacy rows).

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Program.cs:555-567` (add `user_api_keys` to drop list if greenfield); or create migration.
- Files to create (if consolidating): `apps/tamma-elsa/src/Tamma.Data/Migrations/<ts>_ConsolidateLegacyUserApiKeys.cs`.
- Tests to add:
  - If consolidating: `Migration_CopiesUserApiKeyRows_WithScopeUser`.
  - If dropping: smoke test `Boot_WipesLegacyUserApiKeysTable`.
- Estimated effort: 2h
  - If drop-only: 15m.
  - If full migration + test: 2h.

## References

- TS source: N/A (schema-only)
- Archived SQL: `database/archived-sql-migrations/005_user_api_keys.sql`, `009_unified_api_keys.sql`
- C# source: `apps/tamma-elsa/src/Tamma.Api/Program.cs:555-567`, `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs`
- Story: `docs/stories/epic-16/16-7-service-to-service-auth.md` (referenced)
- Related findings: `003-api-key-hash-algorithm.md`
- CLAUDE.md section: "No migration anxiety" (end of the file)
