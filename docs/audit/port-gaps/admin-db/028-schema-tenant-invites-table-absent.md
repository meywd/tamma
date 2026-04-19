# Finding 028: `tenant_invites` table entirely absent; Epic 18 invite flow conflated with `user_invites`

**Scope**: admin-db
**Severity**: P2
**Status**: Data-model regression
**Estimated port effort**: 2h

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Invalid (intentional collapse — single-table approach matches C# port model)
- **Notes**: Both `AdminEndpoints.InviteUser` and `OrgEndpoints.CreateInvite` use the same `user_invites` table with explicit `TenantId` (NOT NULL after migration). The finding's own assessment is "intentional simplification" pending a product decision. Splitting back to two tables would break the unified `IInviteRepository` contract. Story 18-3 should be updated to reflect the canonical approach. Adding role CHECK and InvitedBy FK on `user_invites` (finding 019) closes the safety gap that motivated `tenant_invites` originally.

## 1. What's in TS

Archived at `database/archived-sql-migrations/017_tenant_memberships.sql`.

- File: `packages/api/database/migrations/017_tenant_memberships.sql:19-32`
- Contract/behavior: separate invites table specifically for *tenant* invitations (Story 18-3), with hashed token, role CHECK, inviter FK, and indexes for tenant + email lookups. Distinct from `user_invites` (Story 18-1), which predates the tenants/memberships model.
- Key code (verbatim quote, annotated):

```sql
-- 017_tenant_memberships.sql
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

- Dependencies: `tenants`, `users`.
- Tests that exercised this: invite-email-accept flow per story 18-3.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/Migrations/*.cs` — no `tenant_invites` table anywhere.
- Contract/behavior: `AdminEndpoints.InviteUser` writes to the older `user_invites` table and synthesizes a token there. `OrgEndpoints.CreateInvite` (mapped in `Program.cs:363`) also routes to `user_invites`. Both flows — user-level invites (18-1) and tenant/org invites (18-3) — collapse into a single table.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs (current)
public static async Task<IResult> InviteUser(
    InviteUserRequest req,
    IInviteRepository inviteRepo,
    ITenantContext tenantContext,
    System.Security.Claims.ClaimsPrincipal principal)
{
    ...
    var invite = await inviteRepo.CreateAsync(new UserInvite   // ← user_invites, not tenant_invites
    {
        TenantId = tenantContext.TenantId.Value,
        Email = req.Email,
        Role = req.Role,
        InviteTokenHash = tokenHash,
        InvitedBy = userId is not null ? Guid.Parse(userId) : Guid.Empty,
        ExpiresAt = DateTime.UtcNow.AddDays(7)
    });
    ...
}
```

- Dependencies: `IInviteRepository` (only writes `user_invites`).
- Tests: none assert spec-vs-reality.

## 3. The gap

- TS did: maintain two invite tables — one per story. `user_invites` for user-level onboarding (no tenant concept yet), `tenant_invites` for tenant/org invitations with stronger semantics (required email, tenant FK, indexes for listing all invites for a tenant).
- C# does: collapse both flows into `user_invites` (which itself has different column conventions — see finding 019).
- For a caller querying "all pending invites for tenant `acme`", TS queries `tenant_invites WHERE tenant_id = ? AND accepted_at IS NULL`; C# queries `user_invites` with an implicit tenant join. Works in practice because `user_invites.TenantId` is now NOT NULL, but violates the Story 18-3 spec.
- In production: the `email` column is nullable in `user_invites` (C#'s port) but NOT NULL in `tenant_invites`. Tenant invites via the C# path allow NULL email, which breaks the invite-by-email contract in the UX.

Error paths: none — the spec-vs-reality gap is silent.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-3-organization-tenant-creation.md` Task 4.1-4.8 specifies the `TenantInvite` model.
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior (C# diverged from story to reuse `user_invites`)
  - [ ] Describes a third behavior
  - [ ] No story

Story 18-3 AC#6 explicitly says `POST /api/v1/orgs/:tenantId/invites` with a tenant-scoped invite model.

## 5. Status

- **Classification**: Data-model regression (per Story 18-3) or intentional simplification (if we decide one table suffices).
- **What's needed to finish**:
  1. Product/architecture decision: two invite tables or one?
  2. If two: add `tenant_invites` as a migration, move `OrgEndpoints.CreateInvite` to write there, keep `AdminEndpoints.InviteUser` on `user_invites`.
  3. If one: update Story 18-3 to document the decision, tighten `user_invites` (NOT NULL email, role CHECK — finding 019).
- **Is it "just a stub" or is scope missing?** Scope was pruned during port without updating the spec.
- **Blockers**: decision + finding 019 alignment.

## Remediation

- Files to modify: `Tamma.Api/Endpoints/OrgEndpoints.cs`, `IInviteRepository` (add tenant-scoped variant).
- Files to create: `20260418000011_TenantInvitesTable.cs` (if two-table path chosen).
- Tests to add: per-tenant invite list; email required on create.
- Estimated effort: 2h.

## References

- TS source: `database/archived-sql-migrations/017_tenant_memberships.sql`
- C# source: `apps/tamma-elsa/src/Tamma.Data/Migrations/`, `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs`
- Story: `docs/stories/epic-18/18-3-organization-tenant-creation.md`
- Related findings: `019-schema-user-invites-diff.md`, `027-schema-tenant-memberships-diff.md`
