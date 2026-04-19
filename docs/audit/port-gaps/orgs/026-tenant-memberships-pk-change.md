# Finding 026: `tenant_memberships` PK Changed From Composite to Surrogate `Id`

**Scope**: orgs
**Severity**: P3 (drift / minor bloat)
**Status**: Data-model regression
**Estimated port effort**: 1h

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Invalid (idiomatic preference)
- **Commit**: n/a
- **Notes**: Surrogate `Id` PK is idiomatic EF Core and not load-bearing for any application code (no method calls `db.TenantMemberships.Find(id)`). Uniqueness on `(TenantId, UserId)` is enforced by the explicit unique index in `TammaDbContext.cs:159`. Reverting to a composite PK requires a non-trivial drop-and-recreate migration with no observable behavioral benefit; the audit's "minor write amplification" cost is negligible at the membership-table cardinality. Decision: keep surrogate, accept the schema-shape drift from TS. If a future cross-system tool relies on the composite PK shape we can revisit.

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:database/archived-sql-migrations/017_tenant_memberships.sql`.

- File: `database/archived-sql-migrations/017_tenant_memberships.sql:8-16`.
- Contract/behavior: `tenant_memberships` used a composite primary key `PRIMARY KEY (tenant_id, user_id)` — no surrogate id column. This made membership identity strictly `(tenant, user)` with no second copy via a separate UUID. It also gave the PK index the exact column order the lookup queries use (`WHERE tenant_id = $1 AND user_id = $2`), providing optimal index coverage without a secondary index.
- Key code (verbatim quote, annotated):

```sql
-- database/archived-sql-migrations/017_tenant_memberships.sql (archived) L8-L16
CREATE TABLE IF NOT EXISTS tenant_memberships (
  tenant_id   UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  user_id     UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  role        TEXT NOT NULL DEFAULT 'member' CHECK (role IN ('owner', 'admin', 'member')),
  joined_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  PRIMARY KEY (tenant_id, user_id)
);

CREATE INDEX IF NOT EXISTS idx_tenant_memberships_user_id ON tenant_memberships(user_id);
```

Observe: the PK on `(tenant_id, user_id)` plus a secondary index on `user_id` for the "my tenants" query. No `id` column at all.

- Dependencies: `pg-tenant-membership-store.ts` queries referenced `(tenant_id, user_id)` composite everywhere.
- Tests: the TS store explicitly rejected duplicate memberships via the composite PK constraint.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/Entities/TenantMembership.cs:3-13`, `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs:144-163`.
- Contract/behavior: adds a surrogate `Guid Id` PK and makes `(TenantId, UserId)` a separate unique index. Three columns now identify the same row (Id, TenantId+UserId), with overhead on every write: the PK index on `Id`, a unique index on `(TenantId, UserId)`, and a third index on `UserId`.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Data/Entities/TenantMembership.cs (current) L1-L13
public class TenantMembership
{
    public Guid Id { get; set; }        // ← NEW surrogate PK
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string Role { get; set; } = "member";
    public DateTime JoinedAt { get; set; }

    public Tenant Tenant { get; set; } = null!;
    public User User { get; set; } = null!;
}
```

```csharp
// apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs (current) L144-L163
modelBuilder.Entity<TenantMembership>(entity =>
{
    entity.ToTable("tenant_memberships");
    entity.HasKey(e => e.Id);   // ← PK on surrogate, not composite
    entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
    entity.Property(e => e.Role).IsRequired().HasMaxLength(20).HasDefaultValue("member");
    entity.Property(e => e.JoinedAt).HasDefaultValueSql("now()");

    entity.HasIndex(e => new { e.TenantId, e.UserId }).IsUnique();   // ← secondary unique

    // … relationships omitted
});
```

- Dependencies: `TenantMembershipRepository` uses `FirstOrDefaultAsync(m => m.TenantId == t && m.UserId == u)` — functionally identical.
- Tests: none.

## 3. The gap

Concrete behavioral difference.

- TS did: composite PK. 1 index (the PK) serving `(tenant_id, user_id)` lookups, plus 1 secondary on `user_id`.
- C# does: surrogate PK + a unique secondary on `(TenantId, UserId)`. Two functionally-equivalent indexes overlap. Per-row storage is 16 bytes larger (UUID v4 Id). Inserts/deletes update 3 indexes instead of 2.
- For an API endpoint that looks up `(tenantId, userId)` → member: both return in O(log n), but the C# plan still requires a unique-index lookup followed by a heap fetch. The surrogate PK is never addressed by application code — it's dead weight. It shows up in `listMembers` results (`MemberResponse` doesn't expose it, but the EF entity carries it).
- In production: a small, measurable write-amplification penalty proportional to member count. Also creates a drift between the C# schema and the archived TS schema, making side-by-side comparisons or cross-system tools harder.

One subtle behavioral side-effect: if somewhere the code uses `db.TenantMemberships.Find(id)` with a surrogate ID (e.g., for an invite-acceptance or admin-audit feature), the C# path works but TS would have had no such affordance. Review all uses to confirm none actually rely on `Id`.

Error paths:
- n/a — purely a schema-shape difference.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-3-organization-tenant-creation.md`.
- Story's acceptance criteria for this behavior:
  - Task 1 Subtask 1.5: "Create database migration for `tenant_memberships` table (Migration 016)" — the migration text in Implementation Notes L106-L112 uses `PRIMARY KEY (tenant_id, user_id)`.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Data-model regression (minor).
- **What's needed to finish**:
  1. Decide: keep surrogate `Id` (idiomatic EF) or revert to composite PK for story parity.
  2. If reverting: remove `Id` property, change `entity.HasKey(e => e.Id)` to `entity.HasKey(e => new { e.TenantId, e.UserId })`, drop the now-redundant unique index on `(TenantId, UserId)`. Write an EF migration to drop `Id` column and re-key.
  3. If keeping: at minimum, delete the redundant unique index (the surrogate PK already guarantees uniqueness of `Id`; use `(TenantId, UserId)` as the PK's covering index alternative, OR keep both and accept the write amplification).
- **Is it "just a stub" or is scope missing?** Scope defined in story migration text; port preferred an idiomatic surrogate PK. Either shape is defensible architecturally, but they should align.
- **Blockers**: a migration that drops a PK and re-keys is non-trivial; easier to ship if no one depends on `Id`.

## Remediation (if reverting to composite)

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Data/Entities/TenantMembership.cs` (remove `Id`).
  - `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs` (change HasKey, drop unique index).
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Data/Migrations/XXXXXXXXXXXX_TenantMembershipsCompositePk.cs`.
  - `apps/tamma-elsa/tests/Tamma.Data.Tests/Tenancy/TenantMembershipsPkShapeTests.cs`.
- Tests to add:
  - `AddDuplicateMembership_Throws_OnCompositePk`
  - `SchemaSnapshot_PkIs_TenantIdUserIdComposite`
- Estimated effort: 1h broken down as:
  - Migration (non-trivial PK change): 0.5h
  - Model + tests: 0.5h

## References

- TS source: n/a (schema-side). Archived SQL: `database/archived-sql-migrations/017_tenant_memberships.sql:8-16`.
- C# source: `apps/tamma-elsa/src/Tamma.Data/Entities/TenantMembership.cs:3-13`, `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs:144-163`
- Story: `docs/stories/epic-18/18-3-organization-tenant-creation.md` (Task 1 Subtask 1.5; Implementation Notes L106-L112)
- Related findings: `025-tenant-memberships-check-constraint-lost.md`, `027-tenant-invites-table-absent.md`
- Archived SQL migration: `database/archived-sql-migrations/017_tenant_memberships.sql`
