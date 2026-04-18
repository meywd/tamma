# Finding 013: `password_reset_tokens` missing partial index on `expires_at WHERE consumed_at IS NULL`

**Scope**: admin-db
**Severity**: P2
**Status**: Data-model regression
**Estimated port effort**: 30min

## 1. What's in TS

Archived at `database/archived-sql-migrations/018_user_auth_fields.sql`.

- File: `packages/api/database/migrations/018_user_auth_fields.sql:38-49`
- Contract/behavior: password-reset tokens with `consumed_at` (instead of `revoked_at`), user_id FK (cascade), and a partial index on `expires_at` filtered to active (non-consumed) tokens. Used by the password-reset reaper and by validation ("is this reset link still valid?").
- Key code (verbatim quote, annotated):

```sql
-- 018_user_auth_fields.sql
CREATE TABLE IF NOT EXISTS password_reset_tokens (
  id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id     UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  token_hash  TEXT NOT NULL UNIQUE,
  expires_at  TIMESTAMPTZ NOT NULL,
  consumed_at TIMESTAMPTZ,
  created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_password_reset_tokens_user_id ON password_reset_tokens(user_id);
CREATE INDEX IF NOT EXISTS idx_password_reset_tokens_expires_at
  ON password_reset_tokens(expires_at) WHERE consumed_at IS NULL;   -- ← partial
```

- Dependencies: `users(id)` FK.
- Tests that exercised this: password-reset flow.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs:343-357, 579-587, 709-715`
- Contract/behavior: same shape, same two non-partial indexes; **no partial index** on `ExpiresAt WHERE ConsumedAt IS NULL`.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs (current)
migrationBuilder.CreateTable(
    name: "password_reset_tokens",
    columns: table => new
    {
        Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
        UserId = table.Column<Guid>(type: "uuid", nullable: false),
        TokenHash = table.Column<string>(type: "text", nullable: false),
        ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
        ConsumedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
        CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
    },
    constraints: table => { table.PrimaryKey("PK_password_reset_tokens", x => x.Id); });

migrationBuilder.CreateIndex(name: "IX_password_reset_tokens_TokenHash", ..., unique: true);
migrationBuilder.CreateIndex(name: "IX_password_reset_tokens_UserId", ...);
// Missing partial index on ExpiresAt WHERE ConsumedAt IS NULL

migrationBuilder.AddForeignKey(name: "FK_password_reset_tokens_users_UserId", ... onDelete: ReferentialAction.Cascade);
```

- Dependencies: `users(Id)` FK.
- Tests: none.

## 3. The gap

- TS did: partial index for active tokens; reaper and validation use it.
- C# does: no partial index; queries scan all historical reset tokens.
- For a caller at scale, same math as finding 012.
- In production: reaper and validation get slower over time. Password-reset failure rate will appear to rise as timeouts kick in on the hot path.

Error paths: none — performance regression.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/story-18-6` (password reset).
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Data-model regression
- **What's needed to finish**: add partial index `IX_password_reset_tokens_active_expires` with filter `"ConsumedAt" IS NULL`.
- **Is it "just a stub" or is scope missing?** Partial port.
- **Blockers**: none.

## Remediation

- Files to modify: none.
- Files to create: `20260418000013_PasswordResetActiveIndex.cs`.
- Tests to add: `EXPLAIN` for the reaper query.
- Estimated effort: 30min.

## References

- TS source: `database/archived-sql-migrations/018_user_auth_fields.sql`
- C# source: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs`
- Story: `docs/stories/epic-18/story-18-6`
- Related findings: `012-schema-refresh-tokens-partial-index.md`
