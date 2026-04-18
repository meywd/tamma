# Finding 019: `user_invites` diff — invite_token raw→hash, role CHECK lost, invited_by FK missing

**Scope**: admin-db
**Severity**: P1
**Status**: Data-model regression
**Estimated port effort**: 1.5h

## 1. What's in TS

Archived at `database/archived-sql-migrations/006_user_invites.sql` + `008_tenants.sql`.

- File: `packages/api/database/migrations/006_user_invites.sql`
- Contract/behavior: per-user invites with plaintext invite token (looked up verbatim). Role constrained by CHECK. `invited_by` enforced as FK to `users(id)`.
- Key code (verbatim quote, annotated):

```sql
-- 006_user_invites.sql
CREATE TABLE IF NOT EXISTS user_invites (
  id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  email         TEXT,
  role          TEXT NOT NULL DEFAULT 'member' CHECK (role IN ('owner', 'admin', 'member')),  -- ← CHECK
  invite_token  TEXT NOT NULL UNIQUE,                                                         -- ← raw token
  invited_by    UUID NOT NULL REFERENCES users(id),                                            -- ← NOT NULL + FK
  expires_at    TIMESTAMPTZ NOT NULL,
  accepted_at   TIMESTAMPTZ,
  created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_user_invites_token ON user_invites (invite_token);
```

Story 17/18 later hashed the token in-app; `tenant_invites` (migration 017) declared it explicitly as `invite_token_hash`. This table (`user_invites`) retained the raw-token shape.

- Dependencies: `users(id)` FK, `tenants(id)` FK (via migration 008's `ALTER TABLE ... ADD COLUMN tenant_id`).
- Tests that exercised this: invite acceptance flow.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs:411-434, 657-666`
- Contract/behavior: renamed `invite_token` → `InviteTokenHash`. Lost CHECK on role. `InvitedBy` is `Guid` NOT NULL but has **no foreign key** to `users`. `TenantId` becomes NOT NULL with an explicit FK to `tenants`.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs (current)
migrationBuilder.CreateTable(
    name: "user_invites",
    columns: table => new
    {
        Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
        TenantId = table.Column<Guid>(type: "uuid", nullable: false),
        Email = table.Column<string>(type: "text", nullable: true),
        Role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "member"),  // ← no CHECK
        InviteTokenHash = table.Column<string>(type: "text", nullable: false),          // ← hashed
        InvitedBy = table.Column<Guid>(type: "uuid", nullable: false),                   // ← no FK to users
        ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
        AcceptedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
        CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
    },
    constraints: table =>
    {
        table.PrimaryKey("PK_user_invites", x => x.Id);
        table.ForeignKey(
            name: "FK_user_invites_tenants_TenantId",
            column: x => x.TenantId, principalTable: "tenants", principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);
    });

migrationBuilder.CreateIndex(
    name: "IX_user_invites_InviteTokenHash", table: "user_invites",
    column: "InviteTokenHash", unique: true);
migrationBuilder.CreateIndex(
    name: "IX_user_invites_TenantId", table: "user_invites", column: "TenantId");
```

- Dependencies: FK to `tenants`. `AdminEndpoints.InviteUser` hashes the token before storing (good — security improvement).
- Tests: none.

## 3. The gap

| Aspect | TS | C# | Impact |
|---|---|---|---|
| Token storage | `invite_token TEXT UNIQUE` (raw) | `InviteTokenHash TEXT UNIQUE` | **C# is an improvement** (security) — not a regression |
| `role` CHECK | `IN ('owner','admin','member')` | none | Arbitrary roles can be stored |
| `invited_by` FK | `REFERENCES users(id)` NOT NULL | `InvitedBy uuid` NOT NULL, **no FK** | Orphaned `InvitedBy` values allowed; `InviteUser` even uses `Guid.Empty` when the JWT has no `NameIdentifier` (`AdminEndpoints.cs:126`), creating a row with fake foreign id |
| Per-tenant FK | added via migration 008 as NOT NULL DEFAULT sentinel | NOT NULL, sentinel gone (finding 023) | No default target; every insert requires explicit `TenantId` |

- For a caller calling `POST /api/admin/users/invite` via a JWT without a user id claim, C# inserts a row with `InvitedBy = Guid.Empty` (no referential integrity to catch this); TS would either reject (FK violation) or block upstream.
- For a caller attempting to set `role = "superadmin"` via a customized invite flow, TS raises CHECK; C# accepts, and the downstream accept-invite handler then assigns `superadmin` — which the RBAC middleware silently treats as undefined.

Error paths:
- TS: CHECK violation, FK violation.
- C#: silent.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-3-organization-tenant-creation.md` — this actually wanted a separate `tenant_invites` table (finding 028), distinct from `user_invites`. C# collapsed both into `user_invites`, further muddling scope.
- Story alignment:
  - [ ] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [x] No story — spec gap; must be backfilled

## 5. Status

- **Classification**: Data-model regression
- **What's needed to finish**:
  1. Add CHECK constraint on `Role`.
  2. Add FK on `InvitedBy` → `users(Id)` with `ON DELETE SET NULL` (invite survives inviter deletion) or `RESTRICT`.
  3. Harden `InviteUser` handler to reject requests without a valid user id (don't silently insert `Guid.Empty`).
  4. Consider splitting to `tenant_invites` per story 18-3 (finding 028).
- **Is it "just a stub" or is scope missing?** Partial port + conflated with `tenant_invites` spec.
- **Blockers**: coordination with finding 028.

## Remediation

- Files to modify: `AdminEndpoints.cs:126` (stop falling back to `Guid.Empty`), `Tamma.Data/Entities/UserInvite.cs`.
- Files to create: `20260418000009_UserInvitesHardening.cs`.
- Tests to add: invalid role → CHECK violation; invite with deleted inviter; missing-claim request → 400 not `Guid.Empty` insert.
- Estimated effort: 1.5h.

## References

- TS source: `database/archived-sql-migrations/006_user_invites.sql`
- C# source: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs`, `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs`
- Story: `docs/stories/epic-18/18-3-organization-tenant-creation.md`
- Related findings: `028-schema-tenant-invites-table-absent.md`, `020-schema-rls-policies-missing.md`
