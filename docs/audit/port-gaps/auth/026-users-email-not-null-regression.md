# Finding 026: `users.email` declared NOT NULL; GitHub users without public email cannot persist

**Scope**: auth
**Severity**: P2 (onboarding failure for no-public-email GitHub users)
**Status**: Data-model regression
**Estimated port effort**: 1h

## 1. What's in TS

Pre-delete snapshots at archived SQL.

- Migration `002_users.sql:4-12` (initial table):

```sql
-- database/archived-sql-migrations/002_users.sql:4-12
CREATE TABLE IF NOT EXISTS users (
  id                UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  github_id         BIGINT UNIQUE NOT NULL,
  github_login      TEXT NOT NULL,
  email             TEXT,                -- nullable
  role              TEXT NOT NULL DEFAULT 'member' ...
  created_at        TIMESTAMPTZ ...
  updated_at        TIMESTAMPTZ ...
);
```

- Migration `018_user_auth_fields.sql:16-17` later made `github_id` nullable (for email-only users):

```sql
ALTER TABLE users ALTER COLUMN github_id DROP NOT NULL;
```

- So by Epic 18's world: both `email` and `github_id` are nullable. At least one must be present for the user to be useful, but the DB allows either.
- TS type: `email: string | null` (`user-store.ts:10`).
- TS upsert handling (`user-store.ts:130-157`) treats `email: null` as a valid state for GitHub users who haven't exposed a public email.

Rationale: GitHub's OAuth `read:user user:email` scopes return an email only if the user has made one public or if the token scope includes `user:email` AND the user has any verified email on their account. Some users have no verified emails, or all emails are set to private. The GitHub API returns `email: null` for those users.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- `User.cs:6`: `public string Email { get; set; } = null!;` — non-null reference type; null-forgiving operator. If you set it to null, EF will throw on save.
- `InitialSchema.cs:441`: `Email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),` — NOT NULL at the column level.
- Tests: no test exercises the null-email path.

## 3. The gap

Scenario: A GitHub user with no verified email signs up via OAuth callback (when Finding 008 lands).

- TS: `upsertUser({ githubId: 12345, githubLogin: 'octocat', email: null, role: 'member' })` → row inserted with `email = NULL`.
- C#: The callback handler would need to set `user.Email = null` → EF's change-tracker or Npgsql will throw `InvalidOperationException: 'Required properties {Email} are missing'` OR the insert-SQL fails with `null value in column "email" violates not-null constraint`.
- Workaround attempts the code could make:
  - Synthesize an email like `nobody@noreply.github.com` — but this is indistinguishable from a real user who has that address; breaks uniqueness checks.
  - Synthesize `{githubId}@users.noreply.github.com` — GitHub's documented no-reply format. This would work but is not documented as the C# strategy.
  - Refuse the signup → "Please make your email public before signing up" — user-facing friction, not what TS did.

Production impact: users without public GitHub emails cannot sign up. Estimate: 5-15% of GitHub users keep email private.

Error paths:
- TS: user row created with `email IS NULL`. No error.
- C#: `DbUpdateException: null value in column "email" of relation "users" violates not-null constraint`. 500 error on the callback. User sees a blank browser after OAuth round-trip.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-2-user-login-session-management.md` (AC 6, line 18): *"GitHub OAuth creates a new user if none exists (with `emailVerified: true`, `authMethod: 'github'`), or links to existing email-matched user"*.
- Story doesn't explicitly address the no-email case.
- Archived migration `018_user_auth_fields.sql` (line 19-21):
  ```sql
  CREATE UNIQUE INDEX IF NOT EXISTS idx_users_email_lower
    ON users (LOWER(email)) WHERE email IS NOT NULL;
  ```
  The `WHERE email IS NOT NULL` clause explicitly acknowledges that email CAN be null and the uniqueness constraint should skip those rows.
- Story alignment:
  - [x] Matches TS behavior (nullable email)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [x] No story — TS migration made it nullable; story doesn't spec this

## 5. Status

- **Classification**: Data-model regression. The nullable-email capability from TS was dropped.
- **What's needed to finish**:
  1. Change `User.Email` to nullable: `public string? Email { get; set; }`.
  2. Update the EF mapping: `entity.Property(e => e.Email).HasMaxLength(255);` (drop `IsRequired`).
  3. Create an EF migration that alters the column to nullable.
  4. Update callers to handle null email:
     - `Register`: email is always required for email+password registration → no change needed.
     - `Login`: email lookup tolerates null (users with null email can't log in via email+password, which is intentional).
     - `PasswordResetRequest`: skip if `user.Email is null` or (stronger) if `user.AuthMethod == "github"` (pairs with Finding 015).
     - `GetMe`: return `email: null` in response if null.
  5. Email verification flow (`ResendVerification`, `VerifyEmail`): only applicable to users with non-null emails.
- **Is it "just a stub" or is scope missing?** Scope missing (the migration-018 nullability was not ported).
- **Blockers**: Finding 008 (OAuth callback) will hit this first — if 008 lands, all no-email GitHub users hit a 500 until this is fixed.

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Data/Entities/User.cs`, `TammaDbContext.cs`, most callers in `AuthEndpoints.cs` (null-handling in `Login`, `PasswordResetRequest`, `GetMe`).
- Files to create: EF migration `apps/tamma-elsa/src/Tamma.Data/Migrations/<ts>_RelaxUserEmailNullable.cs`.
- Tests to add:
  - `UserRepository_CreateAsync_WithNullEmail_Succeeds` — critical regression test.
  - `GitHubCallback_NoPublicEmail_CreatesUserWithNullEmail` (Finding 008 integration).
  - `Login_NullEmailUser_CannotAuthenticateWithEmail` (expected 401 — keeps the invariant that email login requires email).
- Estimated effort: 1h
  - Entity + migration: 30m
  - Caller null-handling: 15m
  - Tests: 15m

## References

- TS source: `packages/api/src/persistence/user-store.ts:10` (commit `9e9a57c~1`)
- Archived SQL: `database/archived-sql-migrations/002_users.sql:8`, `018_user_auth_fields.sql:16-21`
- C# source: `apps/tamma-elsa/src/Tamma.Data/Entities/User.cs:6`, `Migrations/20260416172234_InitialSchema.cs:441`
- Story: `docs/stories/epic-18/18-2-user-login-session-management.md` (AC 6, line 18)
- Related findings: `008-oauth-callback-stub.md` (blocked by this), `015-password-reset-sends-to-github-only-users.md`

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Invalid
- **Commit**: `e56b04d`
- **Notes**: Per admin-db decision: Email is intentionally NOT NULL with a placeholder pattern for OAuth-only users. GitHubCallback synthesizes `{id}+{login}@users.noreply.github.com` when GitHub returns no public email.
