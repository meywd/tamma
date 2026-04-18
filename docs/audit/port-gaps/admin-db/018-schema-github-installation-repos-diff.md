# Finding 018: `github_installation_repos` diff — id BIGSERIAL→uuid, owner/name decomposition lost

**Scope**: admin-db
**Severity**: P2
**Status**: Data-model regression
**Estimated port effort**: 2h

## 1. What's in TS

Archived at `database/archived-sql-migrations/001_github_installations.sql`.

- File: `packages/api/database/migrations/001_github_installations.sql:15-36`
- Contract/behavior: bridge table between installations and their selected repos. Keeps the repo's natural `(owner, name)` decomposition as separate columns *and* a combined `full_name` for convenience. `BIGSERIAL` auto-increment for compact PKs. Indexes on both `full_name` (for dashboard lookups) and `installation_id` (for per-install listings).
- Key code (verbatim quote, annotated):

```sql
-- 001_github_installations.sql
CREATE TABLE IF NOT EXISTS github_installation_repos (
  id                BIGSERIAL PRIMARY KEY,
  installation_id   BIGINT NOT NULL REFERENCES github_installations(installation_id) ON DELETE CASCADE,
  repo_id           BIGINT NOT NULL,
  owner             TEXT NOT NULL,         -- ← "acme-corp"
  name              TEXT NOT NULL,         -- ← "my-repo"
  full_name         TEXT NOT NULL,         -- ← "acme-corp/my-repo"
  is_active         BOOLEAN NOT NULL DEFAULT TRUE,
  created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  UNIQUE (installation_id, repo_id)
);

CREATE INDEX IF NOT EXISTS idx_installation_repos_full_name
  ON github_installation_repos (full_name);
CREATE INDEX IF NOT EXISTS idx_installation_repos_installation_id
  ON github_installation_repos (installation_id);
```

- Dependencies: `github_installations.installation_id` FK (natural key).
- Tests that exercised this: GitHub App install-selection flow tests.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs:214-233, 506-510`
- Contract/behavior: narrowed to `RepoFullName` only — `owner` and `name` are lost. PK is `uuid` instead of `BIGSERIAL`. FK moves to the surrogate `InstallationEntityId` (finding 017).
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs (current)
migrationBuilder.CreateTable(
    name: "github_installation_repos",
    columns: table => new
    {
        Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),  // ← was BIGSERIAL
        InstallationEntityId = table.Column<Guid>(type: "uuid", nullable: false),
        RepoId = table.Column<long>(type: "bigint", nullable: false),
        RepoFullName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),  // ← only full_name
        IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
        // NO owner column, NO name column
        // NO created_at, NO updated_at
    },
    constraints: table =>
    {
        table.PrimaryKey("PK_github_installation_repos", x => x.Id);
        table.ForeignKey(
            name: "FK_github_installation_repos_github_installations_Installation~",
            column: x => x.InstallationEntityId,
            principalTable: "github_installations",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);
    });

migrationBuilder.CreateIndex(
    name: "IX_github_installation_repos_InstallationEntityId_RepoId",
    table: "github_installation_repos",
    columns: new[] { "InstallationEntityId", "RepoId" },
    unique: true);
// NO index on RepoFullName
```

- Dependencies: FK to surrogate `github_installations.Id`.
- Tests: none.

## 3. The gap

| Aspect | TS | C# | Impact |
|---|---|---|---|
| `owner` column | present | **absent** | To get "owner-name of this repo", must `split('/')` on `RepoFullName` in app code on every read |
| `name` column | present | **absent** | Same |
| `created_at`, `updated_at` | present | **absent** | Cannot audit when a repo was added to the installation |
| PK type | `BIGSERIAL` | `uuid` | Much larger index; negligible at dashboard scale, real at tens of millions of rows |
| Index on `full_name` | present | **absent** | Lookups by `RepoFullName` do seq scan |

- For a caller listing "all repos owned by `acme-corp`", TS does `WHERE owner = 'acme-corp'` with an index (needs to be added) or `WHERE full_name LIKE 'acme-corp/%'` using `idx_installation_repos_full_name`; C# must do `WHERE RepoFullName LIKE 'acme-corp/%'` on unindexed full text.
- For a caller updating a repo's name (GitHub rename event) where `owner` stays the same and `name` changes, TS updates two columns; C# must rewrite `RepoFullName` entirely and there's no audit timestamp.

Error paths: none — the regressions are silent.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md`.
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Data-model regression
- **What's needed to finish**:
  1. Add `Owner` and `Name` columns, backfilled from `RepoFullName` split on `/`.
  2. Add `CreatedAt`/`UpdatedAt`.
  3. Add index on `RepoFullName`.
- **Is it "just a stub" or is scope missing?** Partial port — columns were consolidated without preserving audit/index structure.
- **Blockers**: backfill migration for existing rows.

## Remediation

- Files to modify: `Tamma.Data/Entities/GitHubInstallationRepo.cs`.
- Files to create: `20260418000008_RestoreRepoOwnerName.cs` with backfill.
- Tests to add: rename flow; per-owner listing performance.
- Estimated effort: 2h.

## References

- TS source: `database/archived-sql-migrations/001_github_installations.sql`
- C# source: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs`
- Story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md`
- Related findings: `017-schema-github-installations-diff.md`
