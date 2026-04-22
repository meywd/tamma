# Finding 024: `users` table diff — NOT NULL email, smaller github_id, lost settings/CHECKs/case-insensitive index

**Scope**: admin-db
**Severity**: P1
**Status**: Data-model regression
**Estimated port effort**: 3h

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed (partial — Email kept NOT NULL)
- **Notes**: `Phase1` migration (a) widens `GitHubId` from int to bigint (matches GitHub's bigint), (b) adds `Settings jsonb DEFAULT '{}'` (per-user provider config from TS migration 004), (c) adds `ck_users_role` and `ck_users_auth_method` CHECK constraints, (d) adds `ix_users_email_lower` partial unique on `LOWER(Email) WHERE DeletedAt IS NULL`. `IUserRepository.GetByGitHubIdAsync` widened to `long`. **Kept**: `Email NOT NULL` — making it nullable would cascade through JWT claims, AdminUserResponse, EnsurePersonalTenantMiddleware, and several auth flows that all assume non-null email. OAuth-only users with no public email synthesize a placeholder via the registration flow today; that pattern is preserved.

## 1. What's in TS

Archived across `database/archived-sql-migrations/002_users.sql`, `004_user_settings.sql`, `007_users_soft_delete.sql`, `008_tenants.sql`, `018_user_auth_fields.sql`.

- File: `packages/api/database/migrations/002_users.sql` and 4 follow-up migrations
- Contract/behavior: the TS `users` table accumulated across six migrations into this final shape:
  - `id UUID PK`
  - `github_id BIGINT UNIQUE` — nullable after migration 018 for email-only users, indexed
  - `github_login TEXT NOT NULL`
  - `email TEXT` — nullable (OAuth users without public emails)
  - `role TEXT NOT NULL DEFAULT 'member' CHECK (role IN ('owner','admin','member'))`
  - `settings JSONB NOT NULL DEFAULT '{}'` — per-user provider config (migration 004)
  - `deleted_at TIMESTAMPTZ`, `last_active_at TIMESTAMPTZ` (migration 007) with partial index `idx_users_deleted_at WHERE deleted_at IS NULL`
  - `tenant_id UUID` nullable (migration 008)
  - `password_hash TEXT`, `email_verified BOOL NOT NULL DEFAULT false`, `auth_method TEXT NOT NULL DEFAULT 'github' CHECK (auth_method IN ('email','github','both'))` (migration 018)
  - case-insensitive unique index `idx_users_email_lower ON users (LOWER(email)) WHERE email IS NOT NULL`

- Key code (verbatim quote, annotated):

```sql
-- 002_users.sql
CREATE TABLE IF NOT EXISTS users (
  id                UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  github_id         BIGINT UNIQUE NOT NULL,
  github_login      TEXT NOT NULL,
  email             TEXT,  -- ← NULLABLE
  role              TEXT NOT NULL DEFAULT 'member' CHECK (role IN ('owner', 'admin', 'member')),
  ...
);
-- 004_user_settings.sql
ALTER TABLE users ADD COLUMN settings JSONB DEFAULT '{}' NOT NULL;
-- 007_users_soft_delete.sql
ALTER TABLE users ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMPTZ;
ALTER TABLE users ADD COLUMN IF NOT EXISTS last_active_at TIMESTAMPTZ;
CREATE INDEX idx_users_deleted_at ON users (deleted_at) WHERE deleted_at IS NULL;
-- 018_user_auth_fields.sql
ALTER TABLE users ADD COLUMN ... auth_method TEXT NOT NULL DEFAULT 'github'
    CHECK (auth_method IN ('email', 'github', 'both'));
ALTER TABLE users ALTER COLUMN github_id DROP NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS idx_users_email_lower
  ON users (LOWER(email)) WHERE email IS NOT NULL;
```

- Dependencies: `tenants` FK (migration 008).
- Tests that exercised this: OAuth login flow, email registration flow, soft-delete tests.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs:437-467`
- Contract/behavior: narrower table; `Email` is NOT NULL, `GitHubId` is `integer` not `bigint`, no `Settings` column, no CHECK constraints, no case-insensitive email index.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs (current)
migrationBuilder.CreateTable(
    name: "users",
    columns: table => new
    {
        Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
        Email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),  // ← NOT NULL
        PasswordHash = table.Column<string>(type: "text", nullable: true),
        DisplayName = table.Column<string>(type: "text", nullable: true),
        AvatarUrl = table.Column<string>(type: "text", nullable: true),
        Role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "member"),  // ← no CHECK
        TenantId = table.Column<Guid>(type: "uuid", nullable: true),
        EmailVerified = table.Column<bool>(type: "boolean", nullable: false),
        IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
        AuthMethod = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "email"),  // ← no CHECK
        GitHubId = table.Column<int>(type: "integer", nullable: true),  // ← integer, not bigint
        GitHubLogin = table.Column<string>(type: "text", nullable: true),
        EmailVerificationTokenHash = table.Column<string>(type: "text", nullable: true),
        EmailVerificationExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
        LastActiveAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
        CreatedAt = ..., UpdatedAt = ..., DeletedAt = ...
        // NO Settings column
    },
    ...);

migrationBuilder.CreateIndex(
    name: "IX_users_Email", table: "users", column: "Email", unique: true,
    filter: "\"DeletedAt\" IS NULL");  // ← case-sensitive, not LOWER(email)
migrationBuilder.CreateIndex(
    name: "IX_users_GitHubId", table: "users", column: "GitHubId", unique: true,
    filter: "\"GitHubId\" IS NOT NULL AND \"DeletedAt\" IS NULL");
```

- Dependencies: `FK_users_tenants_TenantId` with no `onDelete` action (defaults to `Restrict`).
- Tests: none that assert the constraints match TS.

## 3. The gap

Concrete column/index/constraint differences:

| Aspect | TS | C# | Impact |
|---|---|---|---|
| `email` | NULLABLE | NOT NULL | OAuth users without public emails cannot be persisted |
| `github_id` | `bigint` NULLABLE | `integer` NULLABLE | GitHub user IDs >2^31 overflow silently (existing accounts already exceed this) |
| `settings jsonb` | present, default `'{}'` | **missing** | Per-user provider config has no storage |
| `role` CHECK | `IN ('owner','admin','member')` | none | Any string can be inserted as a role |
| `auth_method` CHECK | `IN ('email','github','both')` | none | Any string can be inserted |
| Case-insensitive email index | `idx_users_email_lower ON (LOWER(email))` | `IX_users_Email` on `Email` | Login queries must `LOWER()` on every request, or collide on mixed-case duplicates |

- For a caller registering via OAuth where GitHub returns `{ github_id: 3_000_000_000, email: null }`, TS accepts; C# rejects (NOT NULL violation on email) and silently truncates `github_id` to int overflow error.
- In production: GitHub user IDs exceeded 200 million years ago — 2^31 (~2.1B) ceiling is already uncomfortably close and will overflow within a few years. Accounts linked by older `bigint` values cannot be rehydrated.

Error paths:
- TS: nullable email, bigint github_id — no errors.
- C#: `23502 not_null_violation` on missing email, `22003 numeric_value_out_of_range` for large GitHub IDs.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-1-user-registration-email-verification.md`, `docs/stories/epic-18/18-2-user-login-session-management.md`.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Data-model regression (four distinct regressions in one table)
- **What's needed to finish**:
  1. Add migration: alter `Email` to nullable; alter `GitHubId` to `bigint`; add `Settings jsonb NOT NULL DEFAULT '{}'`; add CHECK constraints on `Role` and `AuthMethod`; create `idx_users_email_lower` as a unique partial index on `LOWER(Email)`.
  2. Drop the case-sensitive `IX_users_Email` (or keep it non-unique).
  3. Update `User` entity to expose `Settings` as `JsonDocument`.
- **Is it "just a stub" or is scope missing?** Partial port — auth fields present, hardening fields dropped.
- **Blockers**: none, but `bigint` migration may require rewriting existing rows.

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Data/Entities/User.cs`, `Tamma.Data/Configurations/UserConfiguration.cs` (if using Fluent API).
- Files to create: `20260418000004_UserTableAlignWithTs.cs`.
- Tests to add: register with null email; register with `github_id = 3_000_000_000`; insert role `"pirate"` → expect CHECK violation; two emails differing only by case → unique violation.
- Estimated effort: 3h.

## References

- TS source: `database/archived-sql-migrations/002_users.sql`, `004_user_settings.sql`, `007_users_soft_delete.sql`, `018_user_auth_fields.sql`
- C# source: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs`
- Story: `docs/stories/epic-18/18-1-user-registration-email-verification.md`
- Related findings: `020`, `025-schema-tenants-table-diff.md`
