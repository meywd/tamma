# Finding 017: `github_installations` diff — PK bigint→uuid, app_id bigint→integer, account_type CHECK lost, nullable TenantId, api_key_* columns missing, indexes missing

**Scope**: admin-db
**Severity**: P1
**Status**: Data-model regression
**Estimated port effort**: 4h

## 1. What's in TS

Archived at `database/archived-sql-migrations/001_github_installations.sql`, `003_api_keys.sql`, `008_tenants.sql`.

- File: `packages/api/database/migrations/001_github_installations.sql`, plus column additions in 003 and 008
- Contract/behavior: the `installation_id` from GitHub (a stable bigint ID assigned by GitHub) is the **primary key**. This is natural-key design: lookups by GitHub webhook payload hit the PK directly. Account type is constrained by CHECK. The `api_key_*` columns support pre-provisioning API keys for the installation (SaaS key provisioning at install-time).
- Key code (verbatim quote, annotated):

```sql
-- 001_github_installations.sql
CREATE TABLE IF NOT EXISTS github_installations (
  installation_id   BIGINT PRIMARY KEY,                    -- ← natural PK from GitHub
  account_login     TEXT NOT NULL,
  account_type      TEXT NOT NULL CHECK (account_type IN ('User', 'Organization')),  -- ← CHECK
  app_id            BIGINT NOT NULL,                       -- ← bigint
  permissions       JSONB NOT NULL DEFAULT '{}',
  suspended_at      TIMESTAMPTZ,
  created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at        TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_installations_account_login ON github_installations (account_login);

-- 003_api_keys.sql
ALTER TABLE github_installations
  ADD COLUMN api_key_hash TEXT,
  ADD COLUMN api_key_prefix TEXT,
  ADD COLUMN api_key_encrypted TEXT;
CREATE INDEX idx_installations_api_key_hash ON github_installations (api_key_hash);

-- 008_tenants.sql
ALTER TABLE github_installations
  ADD COLUMN IF NOT EXISTS tenant_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'
  REFERENCES tenants(id);
CREATE INDEX IF NOT EXISTS idx_installations_tenant_id ON github_installations (tenant_id);
```

- Dependencies: `tenants` FK (migration 008).
- Tests that exercised this: GitHub webhook handler tests.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs:51-70, 512-516`
- Contract/behavior: uses surrogate `Id` uuid as PK, with GitHub's `installation_id` as a unique secondary key. `AppId` is narrowed to `integer`. No CHECK on `AccountType`. API-key provisioning columns are absent (handled via the unified `api_keys` table — see finding 016).
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs (current)
migrationBuilder.CreateTable(
    name: "github_installations",
    columns: table => new
    {
        Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),  // ← surrogate PK
        InstallationId = table.Column<long>(type: "bigint", nullable: false),                           // ← demoted to unique key
        AccountLogin = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
        AccountType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),  // ← no CHECK
        AppId = table.Column<int>(type: "integer", nullable: false),                                     // ← narrowed to int
        AppSlug = table.Column<string>(type: "text", nullable: true),                                    // ← new
        Permissions = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
        SuspendedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
        TenantId = table.Column<Guid>(type: "uuid", nullable: true),                                     // ← nullable, no FK visible
        CreatedAt = ..., UpdatedAt = ...
        // NO api_key_hash, api_key_prefix, api_key_encrypted
    },
    constraints: table => { table.PrimaryKey("PK_github_installations", x => x.Id); });

migrationBuilder.CreateIndex(
    name: "IX_github_installations_InstallationId", table: "github_installations",
    column: "InstallationId", unique: true);
// NO idx_installations_account_login, idx_installations_tenant_id
```

- Dependencies: FK from `github_installation_repos.InstallationEntityId` to the surrogate `Id`.
- Tests: none on constraints.

## 3. The gap

| Aspect | TS | C# | Impact |
|---|---|---|---|
| PK | `installation_id BIGINT` (natural) | `Id uuid` (surrogate) | Webhook payload lookups now two-hop: find `Id` from `InstallationId`, then use `Id`. Joins to `github_installation_repos` use surrogate |
| `app_id` type | `BIGINT` | `integer` | GitHub App IDs can in principle exceed 2^31. Low risk today (Apps IDs are ~6-7 digit), but no headroom |
| `account_type` CHECK | `IN ('User','Organization')` | none | Invalid types accepted |
| `TenantId` | NOT NULL, DEFAULT sentinel, FK | nullable, no FK visible in migration | Orphaned installations possible; sentinel assumption broken |
| `api_key_hash`, `api_key_prefix`, `api_key_encrypted` | present (migration 003) | **all absent** | The copy-in INSERT from migration 009 (`WHERE gi.api_key_hash IS NOT NULL`) now has no source data. If C# ever wants to migrate existing TS installs, the API key is lost |
| `idx_installations_account_login` | index on `account_login` | **absent** | Account-login lookup (common from webhook → UI) falls to seq scan |
| `idx_installations_tenant_id` | index | **absent** | Tenant-scoped listings do seq scans |
| `idx_installations_api_key_hash` | index | **absent** | Installation API-key auth lookups degrade |

- For a callbacker sending a GitHub webhook `{ installation: { id: 12345678, account: { login: "acme", type: "User" } } }`, TS finds the row in one PK lookup; C# does a unique-index lookup on `InstallationId`, finds `Id`, then joins. Slight extra overhead, significant semantic change.
- For a caller migrating existing TS rows: the `api_key_hash`/`api_key_encrypted` pair stored the raw+encrypted key so installations pre-provisioned before migration 009 still worked. Without these columns, those rows' API keys are orphaned.

Error paths:
- TS: CHECK violation on invalid `account_type`; FK violation on tenant_id.
- C#: silent insertion on either.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md`.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Data-model regression (six sub-regressions in one table)
- **What's needed to finish**:
  1. Add CHECK constraint on `AccountType`.
  2. Widen `AppId` to `bigint`.
  3. Add FK on `TenantId` → `tenants(Id)` (with explicit ON DELETE behavior).
  4. Add missing indexes (`AccountLogin`, `TenantId`).
  5. Decide: retain `api_key_*` columns for migration, or accept that pre-migration 009 keys are gone.
- **Is it "just a stub" or is scope missing?** Schema was redesigned intentionally (surrogate PK, AppSlug addition), but hardening (CHECK, FK, indexes) was dropped alongside.
- **Blockers**: natural-vs-surrogate PK decision is already made; don't revisit. Focus on CHECK + indexes.

## Remediation

- Files to modify: none existing.
- Files to create: `20260418000007_GitHubInstallationsHardening.cs`.
- Tests to add: invalid `AccountType` → CHECK violation; bulk-insert 100k rows + `EXPLAIN` on account_login filter uses index.
- Estimated effort: 4h.

## References

- TS source: `database/archived-sql-migrations/001_github_installations.sql`, `003_api_keys.sql`, `008_tenants.sql`
- C# source: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs`
- Story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md`
- Related findings: `016-schema-api-keys-diff.md`, `018-schema-github-installation-repos-diff.md`, `020-schema-rls-policies-missing.md`
