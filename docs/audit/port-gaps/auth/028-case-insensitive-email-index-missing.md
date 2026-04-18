# Finding 028: Case-insensitive email index missing

**Scope**: auth
**Severity**: P2 (duplicate accounts possible on case-different emails)
**Status**: Data-model regression
**Estimated port effort**: 0.5h

## 1. What's in TS

Pre-delete snapshot at archived SQL.

- Migration `018_user_auth_fields.sql:19-21`:

```sql
-- database/archived-sql-migrations/018_user_auth_fields.sql:19-21
-- Case-insensitive unique index on email (for email-based login)
CREATE UNIQUE INDEX IF NOT EXISTS idx_users_email_lower
  ON users (LOWER(email)) WHERE email IS NOT NULL;
```

- Effect: `INSERT INTO users (email, ...) VALUES ('Alice@Example.com', ...)` and a later `INSERT ... VALUES ('alice@example.com', ...)` — Postgres rejects the second as a duplicate, because `LOWER('Alice@Example.com') = LOWER('alice@example.com')`.
- The partial index (`WHERE email IS NOT NULL`) allows multiple null-email rows (GitHub users) without constraint violation.
- TS code also normalizes email to lowercase before write in every endpoint (`email.toLowerCase().trim()`) — so the index is a safety net for any caller that forgets.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- Migration `InitialSchema.cs:669-672`:

```csharp
// apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs:669-672
migrationBuilder.CreateIndex(
    name: "IX_users_Email",
    table: "users",
    column: "Email");
```

- Plain index on `Email` column. No `UNIQUE`. No `LOWER()` function. No partial-index filter.
- Searching the migrations for `idx_users_email_lower` or `LOWER(email)` — zero matches.
- Registration code normalizes with `req.Email.ToLowerInvariant()` (line 50 of AuthEndpoints.cs) — so in practice, emails are always lowercased before insert. But the DB doesn't enforce it.

## 3. The gap

Two regressions: not unique, not case-insensitive.

1. **Not unique**: Two rows can share the same email. The existing C# registration flow does a `GetByEmailAsync` precheck (line 50-52 of AuthEndpoints.cs), but this is racy — two concurrent `Register` requests both pass the precheck and both insert. Without a DB-level unique constraint, both succeed. Duplicate users.
2. **Not case-insensitive**: If anyone writes `Alice@Example.com` directly via the admin API, SQL, or the OAuth callback (which might pass through GitHub's preserved casing), the index treats it as a different key from `alice@example.com`. The `GetByEmailAsync` query: `u.Email == email` is case-sensitive at the DB level in Postgres. So:
   - User registers with `alice@example.com` — row 1 created.
   - OAuth callback passes `Alice@Example.com` (preserved casing from GitHub) → `GetByEmailAsync` returns null → new row 2 inserted.
   - Now Alice has two distinct accounts.

For a caller:
- `POST /register { email: 'alice@example.com', ... }` succeeds.
- Concurrent `POST /register { email: 'alice@example.com', ... }` (same input, same second) — both pass the precheck, both insert. Two rows share `email = 'alice@example.com'`.
- Subsequent `POST /login { email: 'alice@example.com' }` — `FirstOrDefaultAsync` returns whichever row the Postgres sort stage produces first. Arbitrary. Other login attempts may pick the other row.

Additionally, there's no uniqueness check on the current simple `IX_users_Email` index (it's not declared `unique: true`), so even exact-match duplicates go through.

Error paths:
- TS: Postgres rejects duplicate with `unique_violation` (23505) → TS maps to 409.
- C#: no error. Duplicate silently created.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-1-user-registration-email-verification.md`
- Story AC 3 (line 15): *"Email uniqueness enforced at the database level; duplicate email returns 409 Conflict"*.
- Subtask 1.3 (line 31): *"Add unique index on `email` column (case-insensitive using `LOWER(email)`)"*.
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

Story is unambiguous — both unique AND case-insensitive. C# did neither.

## 5. Status

- **Classification**: Data-model regression (index missing critical properties).
- **What's needed to finish**:
  1. Create an EF migration that:
     - Drops `IX_users_Email`.
     - Creates `IX_users_Email_Lower` as `CREATE UNIQUE INDEX "IX_users_Email_Lower" ON "users" (LOWER("Email")) WHERE "Email" IS NOT NULL;` via raw SQL (EF Core doesn't natively support expression-based indexes but supports them via `migrationBuilder.Sql(...)`).
  2. Add a defensive check in the registration endpoint: catch `DbUpdateException` for unique-constraint violation → 409.
  3. Normalize email in `Register`, `Login`, `PasswordResetRequest`, `ResendVerification`, OAuth callback — already mostly done with `.ToLowerInvariant()`, but audit to ensure consistent.
- **Is it "just a stub" or is scope missing?** Scope missing — index was specified in Story 18-1 subtask 1.3 but not ported.
- **Blockers**: None.

## Remediation

- Files to modify: TammaDbContext.cs model builder for User, exception-handling in AuthEndpoints.Register.
- Files to create: EF migration `apps/tamma-elsa/src/Tamma.Data/Migrations/<ts>_UniqueEmailLowerIndex.cs`.
- Tests to add:
  - `Register_DuplicateEmail_DifferentCase_Returns409`.
  - `Migration_CreatesUniqueLowerIndex_OnEmail`.
  - `UserRepository_TwoConcurrentInserts_OneFails` (DB-level uniqueness test).
- Estimated effort: 0.5h
  - Migration SQL: 15m
  - Exception handling + test: 15m

## References

- TS source: N/A (schema-only)
- Archived SQL: `database/archived-sql-migrations/018_user_auth_fields.sql:19-21`
- C# source: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs:669-672`
- Story: `docs/stories/epic-18/18-1-user-registration-email-verification.md` (AC 3, subtask 1.3)
- Related findings: `026-users-email-not-null-regression.md` (same column; both need to coordinate with nullable-email)
