# Finding 027: `tenant_invites` Table Absent — Conflated with `user_invites`

**Scope**: orgs
**Severity**: P1 (data-model regression; semantic conflation of two different concepts)
**Status**: Data-model regression
**Estimated port effort**: 2h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:database/archived-sql-migrations/017_tenant_memberships.sql`.

- File: `database/archived-sql-migrations/017_tenant_memberships.sql:18-32`; story specification: `docs/stories/epic-18/18-3-organization-tenant-creation.md:115-131, 162-175`.
- Contract/behavior: Story 18-3 explicitly introduces a **new** `tenant_invites` table to replace the legacy `user_invites` table:
  - `user_invites` was the **platform-admin** invite table (admin invites a user to the platform, global auth).
  - `tenant_invites` is the **tenant-admin** invite table (tenant admin invites a user to a specific tenant).
  The story's migration plan (L162-L175 "Invite Table Migration: `user_invites` -> `tenant_invites`") describes: create new `tenant_invites` table, migrate pending rows from `user_invites` → `tenant_invites` pointing at the default tenant, update the invite store to delegate to `ITenantMembershipStore`, drop `user_invites` in a subsequent migration.
- Key code (verbatim quote, annotated):

```sql
-- database/archived-sql-migrations/017_tenant_memberships.sql (archived) L19-L32
-- Tenant invites
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

CREATE INDEX IF NOT EXISTS idx_tenant_invites_tenant_id ON tenant_invites(tenant_id);
CREATE INDEX IF NOT EXISTS idx_tenant_invites_email ON tenant_invites(email);
```

```markdown
# docs/stories/epic-18/18-3-organization-tenant-creation.md L162-L175

### Invite Table Migration: `user_invites` -> `tenant_invites`

The `tenant_invites` table defined in this story **replaces** the existing `user_invites` table …
The legacy `user_invites` table was platform-scoped (admin invites a user to the platform).
The new `tenant_invites` table is tenant-scoped (tenant admin invites a user to a specific tenant).

**Migration steps:**
1. Create the new `tenant_invites` table …
2. Migrate pending (non-expired, non-accepted) invites from `user_invites` to `tenant_invites` …
3. Update `packages/api/src/persistence/invite-store.ts` to delegate to the new `ITenantMembershipStore` invite methods.
4. After confirming all invite flows use `tenant_invites`, drop the `user_invites` table in a subsequent migration.
5. Update `packages/api/src/routes/users/invite-routes.ts` to redirect or proxy to the new tenant invite endpoints.
```

- Dependencies: `PgTenantMembershipStore.createInvite/getInviteByTokenHash/etc.` queried `tenant_invites`; legacy `PgInviteStore` (for platform invites) still queried `user_invites`.
- Tests: TS tests asserted against `tenant_invites` for org flows.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/Entities/UserInvite.cs:3-16`, `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs:165-182`, `apps/tamma-elsa/src/Tamma.Data/Repositories/InviteRepository.cs:1-43`, `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs:88-143`.
- Contract/behavior: there is one table, `user_invites`, and one repository, `InviteRepository`. It serves both the platform-admin invite flow AND the tenant-invite flow described in Story 18-3. The entity has a mandatory `TenantId` column (every invite is tenant-scoped), but there's no discrimination between "invite to the platform" and "invite to a specific tenant" semantics. The legacy platform-invite flow (admin invites user to Tamma itself) is not ported.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Data/Entities/UserInvite.cs (current) L1-L16
public class UserInvite
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }       // ← always required
    public string? Email { get; set; }
    public string Role { get; set; } = "member";
    public string InviteTokenHash { get; set; } = null!;
    public Guid InvitedBy { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public Tenant Tenant { get; set; } = null!;
}
```

```csharp
// apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs (current) L165-L182
modelBuilder.Entity<UserInvite>(entity =>
{
    entity.ToTable("user_invites");   // ← the legacy name, with tenant_invites-style fields
    entity.HasKey(e => e.Id);
    entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
    entity.Property(e => e.InviteTokenHash).IsRequired();
    entity.Property(e => e.Role).IsRequired().HasMaxLength(20).HasDefaultValue("member");
    entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

    entity.HasIndex(e => e.InviteTokenHash).IsUnique();
    entity.HasIndex(e => e.TenantId);

    entity.HasOne(e => e.Tenant)
        .WithMany(t => t.Invites)
        .HasForeignKey(e => e.TenantId)
        .OnDelete(DeleteBehavior.Cascade);
});
```

- Dependencies: `InviteRepository` is the single consumer.
- Tests: none.

## 3. The gap

Concrete behavioral difference.

- TS did: two tables, two distinct repositories, two distinct endpoints for two distinct use cases. The Epic 17 archived migrations `008_tenants.sql:47-51` left `user_invites.tenant_id` defaulted to the default tenant for platform-admin invites; the new Epic 18 `017_tenant_memberships.sql:19-32` created the separate `tenant_invites` table for tenant-scoped invites. The story's migration plan describes a forward migration and an eventual DROP of `user_invites`.
- C# does: one table named `user_invites` but with a tenant-scoped schema shape (NOT NULL `TenantId`, FK to `tenants`, ON DELETE CASCADE). The platform-admin invite flow from Story 18-2 / pre-18-3 is neither ported nor represented; every invite is assumed tenant-scoped.
- For the dashboard: the Epic 18 tenant-invite flow works (findings 014, 017 aside), but the legacy platform-admin invite flow (if it had any users in production) is dropped.
- For the schema shape itself: the C# `user_invites` is effectively the TS `tenant_invites` with the old name. An operator running a cross-SQL audit (e.g., "show me tables that end in `_invites`") sees only `user_invites` and may reasonably conclude the Epic 17 default-tenant backfill is still in effect — which it isn't, because the column is NOT NULL with a FK but no default pointing at a sentinel.
- In production: a naming drift that causes confusion and a missing feature (platform-admin invites) that may or may not have been in active use. Stories 18-1 (`user-registration-email-verification.md`) and 18-2 (`user-login-session-management.md`) would need to be cross-referenced to determine if platform-admin invite is still required.

Error paths:
- n/a — this is schema-level drift rather than a runtime error.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-3-organization-tenant-creation.md`.
- Story's acceptance criteria for this behavior:
  - Task 4 Subtask 4.1: "Create `TenantInvite` model: `{ id, tenantId, email, role, token, invitedBy, expiresAt, acceptedAt, createdAt }`" — explicit name.
  - Implementation Notes L162-L175 describe a full table rename/split migration.
  - AC 14: events `TENANT.MEMBER_INVITED.SUCCESS` (tenant-scoped) vs whatever the legacy platform-invite used.
- Story alignment:
  - [ ] Matches TS behavior
  - [ ] Matches C# behavior
  - [x] Describes a third behavior (story says "create `tenant_invites`, migrate from `user_invites`, drop `user_invites`"; C# kept the old name and did not split)
  - [ ] No story

## 5. Status

- **Classification**: Data-model regression (name drift) + scope loss (platform-admin invite flow).
- **What's needed to finish**:
  1. Decide: is a platform-admin invite flow still required? If yes, create a separate `user_invites` table with the legacy schema (no tenant FK) and rename the current table to `tenant_invites`. If no, just rename the current `user_invites` to `tenant_invites` for alignment with the story.
  2. EF migration: `ALTER TABLE user_invites RENAME TO tenant_invites` (simple case) OR a create-new + copy-data + drop-old for the separate-platform-invites case.
  3. Rename the `UserInvite` entity class to `TenantInvite`.
  4. Rename `IInviteRepository` → `ITenantInviteRepository` (or keep the interface name but rename the entity).
  5. Reconfigure the `HasCheckConstraint` (finding 025) once renamed.
- **Is it "just a stub" or is scope missing?** Scope was fully documented; port skipped the rename and the split. If the platform-admin invite feature was intentionally dropped, a story amendment should record that decision.
- **Blockers**: requires a decision on whether platform-admin invites are still supported.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Data/Entities/UserInvite.cs` → rename to `TenantInvite.cs`.
  - `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs` (rename DbSet, ToTable).
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/IInviteRepository.cs` / `InviteRepository.cs` (rename or adapt).
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs` (type rename).
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Data/Migrations/XXXXXXXXXXXX_RenameUserInvitesToTenantInvites.cs`.
  - `apps/tamma-elsa/tests/Tamma.Data.Tests/Tenancy/TenantInvitesSchemaTests.cs`.
- Tests to add:
  - `SchemaSnapshot_TableName_IsTenantInvites`
  - `TenantInvite_HasFkTo_TenantsOnDeleteCascade`
  - `CheckConstraint_Role_InOwnerAdminMember` (combined with finding 025)
- Estimated effort: 2h broken down as:
  - Rename migration + entity: 0.5h
  - Downstream ref rewrites: 0.5h
  - Platform-invite decision + (optional) separate table: 0.5h
  - Tests: 0.5h

## References

- TS source: n/a (schema-side). Archived SQL: `database/archived-sql-migrations/017_tenant_memberships.sql:19-32`.
- C# source: `apps/tamma-elsa/src/Tamma.Data/Entities/UserInvite.cs`, `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs:165-182`, `apps/tamma-elsa/src/Tamma.Data/Repositories/InviteRepository.cs`
- Story: `docs/stories/epic-18/18-3-organization-tenant-creation.md` (Task 4 Subtask 4.1; Implementation Notes L162-L175)
- Related findings: `014-create-invite-weak-token-no-email.md`, `017-accept-invite-no-active-tenant.md`, `025-tenant-memberships-check-constraint-lost.md`
- Archived SQL migration: `database/archived-sql-migrations/017_tenant_memberships.sql`
