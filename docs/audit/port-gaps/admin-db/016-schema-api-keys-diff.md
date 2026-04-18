# Finding 016: `api_keys` table diff — permissions jsonb→text[], rotated_from FK unenforced, lost scope CHECK, lost partial active index, new UserId FK

**Scope**: admin-db
**Severity**: P1
**Status**: Data-model regression
**Estimated port effort**: 3h

## 1. What's in TS

Archived at `database/archived-sql-migrations/009_unified_api_keys.sql`.

- File: `packages/api/database/migrations/009_unified_api_keys.sql`
- Contract/behavior: unified `api_keys` table consolidating per-user keys (`user_api_keys`), per-installation keys (`github_installations.api_key_hash`), and service-to-service keys (new). Structured `permissions` as JSONB so app can use `@>` containment queries, enforced scope enum via CHECK, and provided an active-only partial index.
- Key code (verbatim quote, annotated):

```sql
-- 009_unified_api_keys.sql
CREATE TABLE IF NOT EXISTS api_keys (
  id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  scope         TEXT NOT NULL CHECK (scope IN ('user', 'installation', 'service')),
  owner_id      TEXT NOT NULL,               -- user_id UUID | installation_id bigint | service_name text
  key_hash      TEXT NOT NULL UNIQUE,
  key_prefix    TEXT NOT NULL,
  label         TEXT NOT NULL DEFAULT 'default',
  permissions   JSONB NOT NULL DEFAULT '[]'::jsonb,  -- ['prompts:read','diagnostics:write',...] (service scope only)
  tenant_id     UUID REFERENCES tenants(id), -- NULL for service keys; set for user/installation
  created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  last_used_at  TIMESTAMPTZ,
  revoked_at    TIMESTAMPTZ,                 -- may be in the future during rotation grace period
  rotated_from  UUID REFERENCES api_keys(id) -- track rotation chain
);

CREATE INDEX IF NOT EXISTS idx_api_keys_key_hash ON api_keys (key_hash);
CREATE INDEX IF NOT EXISTS idx_api_keys_scope_owner ON api_keys (scope, owner_id);
CREATE INDEX IF NOT EXISTS idx_api_keys_active ON api_keys (scope) WHERE revoked_at IS NULL;

-- Plus copy-in INSERTs from user_api_keys and github_installations
```

- Dependencies: `tenants` (FK), `gen_random_uuid()`, `user_api_keys` and `github_installations.api_key_hash` as source data.
- Tests that exercised this: `api-key.test.ts`, `create-app-admin-auth.test.ts`.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs:320-342, 475-495, 702-707`
- Contract/behavior: the table exists but several constraints and index shapes differ.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs (current)
migrationBuilder.CreateTable(
    name: "api_keys",
    columns: table => new
    {
        Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
        Scope = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),  // ← no CHECK
        OwnerId = table.Column<string>(type: "text", nullable: false),
        KeyHash = table.Column<string>(type: "text", nullable: false),
        KeyPrefix = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
        Label = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
        Permissions = table.Column<string[]>(type: "text[]", nullable: false),  // ← was jsonb
        TenantId = table.Column<Guid>(type: "uuid", nullable: true),
        CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
        LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
        RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
        RotatedFromId = table.Column<Guid>(type: "uuid", nullable: true),  // ← no FK
        UserId = table.Column<Guid>(type: "uuid", nullable: true)           // ← new field, FK to users
    },
    constraints: table => { table.PrimaryKey("PK_api_keys", x => x.Id); });

migrationBuilder.CreateIndex(name: "IX_api_keys_KeyHash", table: "api_keys", column: "KeyHash", unique: true);
migrationBuilder.CreateIndex(name: "IX_api_keys_Scope_OwnerId", table: "api_keys", columns: new[] { "Scope", "OwnerId" });
migrationBuilder.CreateIndex(name: "IX_api_keys_TenantId", table: "api_keys", column: "TenantId");
migrationBuilder.CreateIndex(name: "IX_api_keys_UserId", table: "api_keys", column: "UserId");
// No partial index "WHERE RevokedAt IS NULL"

migrationBuilder.AddForeignKey(
    name: "FK_api_keys_users_UserId", table: "api_keys",
    column: "UserId", principalTable: "users", principalColumn: "Id");
```

- Dependencies: `ApiKey` entity, `ApiKeyRepository`, FK on `UserId` (new).
- Tests: none in `Tamma.Api.Tests`.

## 3. The gap

| Aspect | TS | C# | Impact |
|---|---|---|---|
| `permissions` | `jsonb` (`'[]'::jsonb`) | `text[]` | JSONB operators (`@>`, `?`, path queries) break; array comparison only |
| `scope` CHECK | `IN ('user','installation','service')` | none | Invalid scopes insertable; `idx_api_keys_scope_owner` gets garbage buckets |
| `rotated_from` | `UUID REFERENCES api_keys(id)` (self-FK) | `RotatedFromId Guid?` — no FK | Rotation chains can point at non-existent rows |
| `idx_api_keys_active WHERE revoked_at IS NULL` | partial index | absent | Hot-path auth lookup (`WHERE revoked_at IS NULL`) falls back to full scan as the table grows |
| `UserId` | not present (owner_id is text) | new FK to users | Net positive but duplicates info in `owner_id` for user scope |
| `TenantId` FK | `REFERENCES tenants(id)`, nullable | FK existence unclear from migration (index only, no explicit `AddForeignKey` block) | Orphaned rows possible |

- For a caller inserting `(scope='banana', owner_id='x', …)`, TS raises CHECK violation; C# silently accepts.
- For auth lookup `WHERE scope='service' AND revoked_at IS NULL`, TS uses the partial index for O(log n) scan of active-only rows; C# scans all rows (including revoked). At 100k revoked keys + 100 active, this is a 1000x slowdown on every authenticated API call.
- For a caller running `SELECT permissions FROM api_keys WHERE permissions @> '["diagnostics:write"]'`, TS returns matching rows; C# errors — `text[]` doesn't support `@>` with JSON literals (only `&&` array-overlap).

Error paths:
- TS: CHECK violation on invalid scope; FK violation on dangling `rotated_from`.
- C#: silent acceptance; no FK safety on rotation chain.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-16/16-7-service-to-service-auth.md`.
- Story alignment:
  - [x] Matches TS behavior (C# permission-storage is a regression)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Data-model regression (multiple aspects in one table)
- **What's needed to finish**:
  1. Add CHECK constraint on `Scope`.
  2. Change `Permissions` column type from `text[]` to `jsonb` (requires data migration and repository changes).
  3. Add self-FK on `RotatedFromId`.
  4. Add partial index on `Scope` WHERE `RevokedAt IS NULL`.
  5. Explicitly add FK on `TenantId` if not already.
- **Is it "just a stub" or is scope missing?** Partial port; structural details dropped.
- **Blockers**: `Permissions` type change is breaking for any consumer doing array operations; plan a rollout.

## Remediation

- Files to modify: `Tamma.Data/Entities/ApiKey.cs` (change `Permissions` to `JsonDocument` or `List<string>` serialized as jsonb), repository queries.
- Files to create: `20260418000006_ApiKeysHardening.cs`.
- Tests to add: CHECK violation on bogus scope; partial-index usage via `EXPLAIN`; FK violation on rotation chain.
- Estimated effort: 3h.

## References

- TS source: `database/archived-sql-migrations/009_unified_api_keys.sql`
- C# source: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs`
- Story: `docs/stories/epic-16/16-7-service-to-service-auth.md`
- Related findings: `004`, `005`, `017-schema-github-installations-diff.md`, `021-schema-user-api-keys-table-absent.md`
