# Finding 027: `users.github_id` narrowed from `bigint` to `integer` (2^31 overflow)

**Scope**: auth
**Severity**: P2 (overflow for very high-numbered GitHub users)
**Status**: Data-model regression
**Estimated port effort**: 1h

## 1. What's in TS

Pre-delete snapshots at archived SQL.

- Migration `002_users.sql:6`:

```sql
github_id         BIGINT UNIQUE NOT NULL,
```

- PostgreSQL `BIGINT` holds signed 64-bit integers, range ±9.2×10^18 — way more than enough for GitHub's numeric user IDs, which monotonically increase as new accounts are created.
- TS type: `githubId: number | null` (`user-store.ts:9`). JavaScript numbers can accurately represent integers up to `Number.MAX_SAFE_INTEGER` (2^53 - 1 ≈ 9×10^15). For foreseeable GitHub-user-ID values this is effectively unbounded.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- `User.cs:15`: `public int? GitHubId { get; set; }` — `int` is 32-bit signed, range ±2.1×10^9.
- EF mapping `InitialSchema.cs:450`: `GitHubId = table.Column<int>(type: "integer", nullable: true),`.
- Repository methods use `int`:
  - `IUserRepository.cs:9`: `Task<User?> GetByGitHubIdAsync(int githubId);`
  - `UserRepository.cs:23`: `Task<User?> GetByGitHubIdAsync(int githubId) => await db.Users.FirstOrDefaultAsync(u => u.GitHubId == githubId);`

## 3. The gap

GitHub assigns numeric user IDs sequentially. As of early 2026, GitHub's active user ID range is around `1.5 × 10^8` (150 million). Growth rate: ~10-20% per year. At current pace, GitHub will cross `2^31 = 2,147,483,648` (~2.15 billion) in roughly 2040-2060 depending on retention.

Two scenarios where this bites sooner than "2040":
- **Machine accounts / bots**: GitHub allocates IDs for GitHub Apps, bots, and services in a separate range — some in the high billions. Specifically, GitHub App installation IDs are separate, but App *user* IDs can exceed normal ranges.
- **Dependabot, github-actions[bot]**: these have high IDs already. `github-actions[bot]` has id 41898282. Still under 2^31, but the trend is upward.
- **Any future GitHub ID algorithm change** (e.g. snowflake IDs) could produce post-2^31 values tomorrow.

When a user with `github_id > 2^31 - 1` signs in:
- TS: the BIGINT column holds it; the TS `number` type holds it (below 2^53); no error.
- C#: `GitHubId` is `int`. Assigning a value > `int.MaxValue` throws `OverflowException` in the deserializer OR truncates silently depending on path.
  - `System.Text.Json` with default settings: throws `JsonException` on a value larger than int.MaxValue.
  - Manual cast: `(int)githubUserId` throws `OverflowException` in a `checked` context; truncates to garbage in an `unchecked` context.
- `GetByGitHubIdAsync(int githubId)` cannot accept a >2^31 value even as a query parameter.

Production impact today: probably zero (most active GitHub users are below 2^31). Future impact: inevitable unless fixed. And fixing later = altering a foreign-key type across `user_installations` (if it's restored per Finding 023) and all referencing code.

Error paths:
- TS: no error.
- C#: depending on path, `OverflowException` or `JsonException` or silent truncation leading to mis-matched user records.

## 4. Gap from stories

- Referenced story: none — schema-level detail never called out in a story.
- Archived SQL `002_users.sql` explicitly used `BIGINT`.
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [x] No story — schema-level detail

Compare to GitHub's own API docs, which document user ID as a 64-bit integer.

## 5. Status

- **Classification**: Data-model regression. Type narrowed without justification.
- **What's needed to finish**:
  1. Change `User.GitHubId` from `int?` to `long?`.
  2. Update the EF mapping: `GitHubId = table.Column<long>(type: "bigint", nullable: true)`.
  3. Create an EF migration that alters the column from `integer` to `bigint` (Postgres `ALTER COLUMN github_id TYPE bigint USING github_id::bigint` — trivially compatible since every `int` fits in `bigint`).
  4. Update `IUserRepository.GetByGitHubIdAsync(int)` → `(long)`.
  5. Update any caller code that passes/receives this value.
  6. If Finding 023 lands with `user_installations`, make `installation_id` a `long` too (GitHub App installation IDs follow the same monotonic scheme).
- **Is it "just a stub" or is scope missing?** Scope was understood incorrectly (used `int` when `long` was required). Drift.
- **Blockers**: None.

## Remediation

- Files to modify: `User.cs`, `IUserRepository.cs`, `UserRepository.cs`, any caller code.
- Files to create: EF migration `apps/tamma-elsa/src/Tamma.Data/Migrations/<ts>_WidenGitHubIdToBigint.cs`.
- Tests to add:
  - `UserRepository_GetByGitHubIdAsync_WithValueAboveIntMax_Returns` — uses `long.MaxValue / 2` as fixture.
  - `User_GitHubId_AcceptsBigIntValues` — write + read.
- Estimated effort: 1h
  - Field widening + migration: 30m
  - Caller + test updates: 30m

## References

- TS source: `packages/api/src/persistence/user-store.ts:9` (commit `9e9a57c~1`)
- Archived SQL: `database/archived-sql-migrations/002_users.sql:6`
- C# source: `apps/tamma-elsa/src/Tamma.Data/Entities/User.cs:15`, `Migrations/20260416172234_InitialSchema.cs:450`, `Repositories/IUserRepository.cs:9`, `UserRepository.cs:23`
- Story: No governing story — schema-level detail
- Related findings: `023-user-installations-table-absent.md` (installation_id should also be bigint)
