# Finding 012: `refresh_tokens` missing partial index on `expires_at WHERE revoked_at IS NULL`

**Scope**: admin-db
**Severity**: P2
**Status**: Data-model regression
**Estimated port effort**: 30min

## 1. What's in TS

Archived at `database/archived-sql-migrations/018_user_auth_fields.sql`.

- File: `packages/api/database/migrations/018_user_auth_fields.sql:23-36`
- Contract/behavior: refresh-token table with user_id FK (cascade), unique token hash, and two indexes — one on `user_id`, and a **partial index** on `expires_at` filtered by `revoked_at IS NULL`. That partial index is used by the hot-path reaper query `SELECT id FROM refresh_tokens WHERE revoked_at IS NULL AND expires_at < NOW()` and by session-validation: "is this token still live?".
- Key code (verbatim quote, annotated):

```sql
-- 018_user_auth_fields.sql
CREATE TABLE IF NOT EXISTS refresh_tokens (
  id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id     UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  token_hash  TEXT NOT NULL UNIQUE,
  expires_at  TIMESTAMPTZ NOT NULL,
  revoked_at  TIMESTAMPTZ,
  created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_refresh_tokens_user_id ON refresh_tokens(user_id);
CREATE INDEX IF NOT EXISTS idx_refresh_tokens_expires_at
  ON refresh_tokens(expires_at) WHERE revoked_at IS NULL;   -- ← partial, active-only
```

- Dependencies: `users(id)` FK.
- Tests that exercised this: login/refresh flow perf.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs:359-373, 606-615, 717-723`
- Contract/behavior: same table shape, same two non-partial indexes on `TokenHash` and `UserId`. **No partial index** on `ExpiresAt WHERE RevokedAt IS NULL`.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs (current)
migrationBuilder.CreateTable(
    name: "refresh_tokens",
    columns: table => new
    {
        Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
        UserId = table.Column<Guid>(type: "uuid", nullable: false),
        TokenHash = table.Column<string>(type: "text", nullable: false),
        ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
        RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
        CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
    },
    constraints: table => { table.PrimaryKey("PK_refresh_tokens", x => x.Id); });

migrationBuilder.CreateIndex(name: "IX_refresh_tokens_TokenHash", table: "refresh_tokens", column: "TokenHash", unique: true);
migrationBuilder.CreateIndex(name: "IX_refresh_tokens_UserId", table: "refresh_tokens", column: "UserId");
// Missing: partial index on (ExpiresAt) WHERE RevokedAt IS NULL
migrationBuilder.AddForeignKey(
    name: "FK_refresh_tokens_users_UserId", ... onDelete: ReferentialAction.Cascade);
```

- Dependencies: `users(Id)` FK.
- Tests: none assert query plans.

## 3. The gap

- TS did: provide a partial index covering active (non-revoked) tokens only, keeping it small relative to the historic archive.
- C# does: lack it; expiration reaper and active-token lookup must use `IX_refresh_tokens_UserId` and filter `RevokedAt IS NULL` in memory, or do a seq-scan.
- For a caller at scale (100k revoked + 1k active), TS's reaper scans 1k rows, C#'s scans 101k.
- In production: minor until the revoked-tokens archive grows. Then login-path latency slowly increases without a visible cause.

Error paths: none — purely a performance regression.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-2-user-login-session-management.md`.
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Data-model regression
- **What's needed to finish**:
  1. Add partial index `IX_refresh_tokens_active_expires` on `ExpiresAt` with filter `"RevokedAt" IS NULL` via `migrationBuilder.Sql(@"CREATE INDEX ...");` (EF's `CreateIndex` supports `filter:` parameter).
- **Is it "just a stub" or is scope missing?** Partial port.
- **Blockers**: none.

## Remediation

- Files to modify: none.
- Files to create: `20260418000012_RefreshTokensActiveIndex.cs`.
- Tests to add: `EXPLAIN (FORMAT JSON) SELECT id FROM refresh_tokens WHERE revoked_at IS NULL AND expires_at < NOW()` — expect Index Scan on the partial index.
- Estimated effort: 30min.

## References

- TS source: `database/archived-sql-migrations/018_user_auth_fields.sql`
- C# source: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs`
- Story: `docs/stories/epic-18/18-2-user-login-session-management.md`
- Related findings: `013-schema-password-reset-tokens-partial-index.md`
