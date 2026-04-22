# Finding 025: `tenant_memberships.role` CHECK Constraint Lost

**Scope**: orgs
**Severity**: P2 (correctness; invariant moved from DB to app and then removed)
**Status**: Data-model regression
**Estimated port effort**: 0.5h

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Already-fixed
- **Commit**: e8dd76b (admin-db Phase-1)
- **Notes**: `SchemaHardeningPhase1` migration (2026-04-19) installs `ck_tenant_memberships_role CHECK (Role IN ('owner','admin','member'))` and the matching `ck_user_invites_role` and `ck_users_role` constraints. App-layer whitelist (finding 012) now layers above this DB-level constraint so invalid roles fail with 400, not the raw 23514 from the DB. Verified via Phase-1 migration source (lines 269-279).

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:database/archived-sql-migrations/017_tenant_memberships.sql`.

- File: `database/archived-sql-migrations/017_tenant_memberships.sql:8-14` (`tenant_memberships`) and `:19-29` (`tenant_invites`).
- Contract/behavior: both tables had `role TEXT NOT NULL DEFAULT 'member' CHECK (role IN ('owner', 'admin', 'member'))`. This is a database-level whitelist: any `INSERT` or `UPDATE` that tries to set `role = 'root'` or `role = ''` fails with SQLSTATE 23514 (`check_violation`). The constraint is enforced regardless of whether the app-layer validation was correct — a defense-in-depth for role integrity.
- Key code (verbatim quote, annotated):

```sql
-- database/archived-sql-migrations/017_tenant_memberships.sql (archived) L8-L14
CREATE TABLE IF NOT EXISTS tenant_memberships (
  tenant_id   UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  user_id     UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  role        TEXT NOT NULL DEFAULT 'member' CHECK (role IN ('owner', 'admin', 'member')),
  joined_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  PRIMARY KEY (tenant_id, user_id)
);
```

```sql
-- database/archived-sql-migrations/017_tenant_memberships.sql (archived) L19-L29
CREATE TABLE IF NOT EXISTS tenant_invites (
  id                UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id         UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  email             TEXT NOT NULL,
  role              TEXT NOT NULL DEFAULT 'member' CHECK (role IN ('owner', 'admin', 'member')),
  invite_token_hash TEXT NOT NULL UNIQUE,
  invited_by        UUID NOT NULL REFERENCES users(id),
  expires_at        TIMESTAMPTZ NOT NULL,
  accepted_at       TIMESTAMPTZ,
  created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

- Dependencies: none (PostgreSQL native constraints).
- Tests: the TS tests at the app layer (whitelist in route handlers, finding 012) made collateral assertions that the DB enforced this — any unknown role was rejected before reaching INSERT.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs:144-182`.
- Contract/behavior: EF configures `Role` as a required `VARCHAR(20)` with default `'member'` but **no CHECK constraint**. EF Core in 8.x supports `entity.ToTable(t => t.HasCheckConstraint(...))` but it's not used here.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs (current) L144-L163 — TenantMembership config
modelBuilder.Entity<TenantMembership>(entity =>
{
    entity.ToTable("tenant_memberships");
    entity.HasKey(e => e.Id);
    entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
    entity.Property(e => e.Role).IsRequired().HasMaxLength(20).HasDefaultValue("member");  // ← no .HasCheckConstraint
    entity.Property(e => e.JoinedAt).HasDefaultValueSql("now()");
    // …
});
```

```csharp
// apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs (current) L165-L182 — UserInvite config
modelBuilder.Entity<UserInvite>(entity =>
{
    entity.ToTable("user_invites");
    entity.HasKey(e => e.Id);
    entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
    entity.Property(e => e.InviteTokenHash).IsRequired();
    entity.Property(e => e.Role).IsRequired().HasMaxLength(20).HasDefaultValue("member");  // ← no CHECK
    // …
});
```

- Dependencies: none.
- Tests: none.

## 3. The gap

Concrete behavioral difference.

- TS did: Postgres rejected any role outside `{owner, admin, member}` with `check_violation`. Combined with route-level whitelist in `/orgs` handlers (finding 012), no "rogue" role could ever land.
- C# does: the app-level whitelist is gone (finding 012, 014), AND the DB-level CHECK is gone. So `INSERT INTO tenant_memberships (..., role) VALUES (..., 'root')` succeeds. A membership with `role = 'root'` breaks:
  - Role-hierarchy comparisons that key on the literal string.
  - JWT-building logic that maps role → JWT claim.
  - Dashboard UIs that have a discriminated union on role.
- For a request body `{"role":"root"}`: TS 400 at app layer or 500 (`check_violation`) at DB layer. C# silently persists.
- In production, this is a correctness gap that's currently mitigated only by the fact that no code path actively passes a non-whitelisted role; the moment a new endpoint or a bug does, invalid data goes into `tenant_memberships`/`user_invites` with no signal.

Error paths:
- TS error path: `400 { "error": "role must be one of: owner, admin, member" }` (app layer) or SQLSTATE 23514 (DB layer) if app layer missed.
- C# error path: none.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-3-organization-tenant-creation.md`.
- Story's acceptance criteria for this behavior:
  - Task 1 Subtask 1.5: "Create database migration for `tenant_memberships` table (Migration 016)" — migration text in Implementation Notes L106-L112 explicitly includes `CHECK (role IN ('owner', 'admin', 'member'))`.
  - AC 5: "Membership model links users to tenants with roles: `owner`, `admin`, `member`".
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Data-model regression.
- **What's needed to finish**:
  1. Add `entity.ToTable("tenant_memberships", t => t.HasCheckConstraint("ck_tenant_memberships_role", "role IN ('owner', 'admin', 'member')"));` on the `TenantMembership` entity config.
  2. Same on `UserInvite` config.
  3. EF migration auto-generates `ALTER TABLE ... ADD CONSTRAINT ...`.
  4. Add repository-level guard: `TenantMembershipRepository.AddAsync`/`UpdateRoleAsync` throws `ArgumentException` on invalid role strings so the error surfaces as 400 instead of 500 at the DB layer.
- **Is it "just a stub" or is scope missing?** Scope defined in the story; EF migration generator simply didn't emit it.
- **Blockers**: finding 012 (adds app-layer whitelist) should ship alongside so the happy-path errors return 400 not 500.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs` (add `HasCheckConstraint` to both entities).
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/TenantMembershipRepository.cs` (validate role input).
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/InviteRepository.cs` (validate role input).
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Data/Migrations/XXXXXXXXXXXX_AddRoleCheckConstraints.cs`.
  - `apps/tamma-elsa/tests/Tamma.Data.Tests/Tenancy/RoleCheckConstraintTests.cs`.
- Tests to add:
  - `AddMembership_Throws_WhenRoleInvalid_AppLayer`
  - `DirectSql_UpdateRole_RaisesCheckViolation_WhenRoleInvalid` (raw ADO.NET)
  - `CreateInvite_Throws_WhenRoleInvalid`
- Estimated effort: 0.5h broken down as:
  - Constraint config + migration: 0.2h
  - Repo guards: 0.1h
  - Tests: 0.2h

## References

- TS source: n/a (schema-side)
- C# source: `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs:144-163, 165-182`
- Story: `docs/stories/epic-18/18-3-organization-tenant-creation.md` (Task 1 Subtask 1.5; AC 5; Implementation Notes L106-L112)
- Related findings: `012-update-member-role-privilege-escalation.md`, `014-create-invite-weak-token-no-email.md`, `026-tenant-memberships-pk-change.md`, `027-tenant-invites-table-absent.md`
- Archived SQL migration: `database/archived-sql-migrations/017_tenant_memberships.sql`
