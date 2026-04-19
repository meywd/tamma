# Finding 010: `user_installations` table entirely deleted

**Scope**: admin-db
**Severity**: P1
**Status**: Data-model regression
**Estimated port effort**: 3h

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Invalid (intentional simplification preserved)
- **Notes**: The C# port routes user-to-installation linkage through `tenant_memberships` + `github_installations.TenantId`. Per finding's own "What's needed to finish": this is a product-decision-pending question. Per CLAUDE.md "No migration anxiety: App is not in production with users", reintroducing a separate `user_installations` table absent a confirmed multi-install-per-user requirement is premature. Story 18-4 should be updated separately; the schema change is deferred until product confirms the per-installation role distinct from per-tenant role.

## 1. What's in TS

Archived at `database/archived-sql-migrations/002_users.sql`.

- File: `packages/api/database/migrations/002_users.sql:14-27`
- Contract/behavior: bridge table between a user and the GitHub App installations they have access to, with a per-installation role. A single user can belong to multiple installations (e.g. `acme-corp` and their personal account), with different roles in each. This is analogous to what `tenant_memberships` does for tenants, but specifically for GitHub-App installs.
- Key code (verbatim quote, annotated):

```sql
-- 002_users.sql
CREATE TABLE IF NOT EXISTS user_installations (
  user_id           UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  installation_id   BIGINT NOT NULL REFERENCES github_installations(installation_id) ON DELETE CASCADE,
  role              TEXT NOT NULL DEFAULT 'member' CHECK (role IN ('owner', 'admin', 'member')),
  created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  PRIMARY KEY (user_id, installation_id)
);

CREATE INDEX IF NOT EXISTS idx_user_installations_installation_id
  ON user_installations (installation_id);
```

- Dependencies: `users(id)`, `github_installations(installation_id)` — both with `ON DELETE CASCADE`.
- Tests that exercised this: install-selection flow, multi-install user testing.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/Migrations/*.cs` — no `user_installations` table anywhere. Not even referenced in `Program.cs:556-564`'s DROP list (which suggests it was never ported).
- Contract/behavior: non-existent. The closest C# analog is `tenant_memberships` (finding 027), but memberships are per-tenant, not per-installation.
- Key code: n/a.
- Dependencies: none.
- Tests: none.

## 3. The gap

- TS did: track which users had what role in which installations. A single GitHub user could be `owner` of install A and `member` of install B.
- C# does: nothing. User-to-installation relationship has no canonical home.
- For a caller running "list my installations", TS queries `user_installations WHERE user_id = ?`; C# has no equivalent query and returns... whatever `tenant_memberships` says? But installations and tenants aren't 1:1 (one tenant can own multiple installs).
- In production with existing data / deployed clients, this means:
  - Multi-installation-per-user flows cannot be implemented without reintroducing the table.
  - Per-install role (owner vs member) is not tracked — the UX can't show "you're an admin of this install but just a member of that one".
  - The `github_installations.TenantId` nullable FK (finding 017) is the only user-install linkage, and it's indirect (user → tenant → installation).

Error paths: none at DB level — queries either succeed (wrong data) or fail (feature missing).

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md` — the AC enumerate multi-install scenarios that `user_installations` existed to support.
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

The port essentially conflated user-installation relationships with user-tenant memberships. If the product decision is "one tenant owns one or more installations, and users have roles per-tenant only", the simplification is valid but under-documented.

## 5. Status

- **Classification**: Data-model regression (or intentional simplification — see note below)
- **What's needed to finish**:
  1. Product decision: do we need per-install roles distinct from per-tenant roles?
  2. If yes: restore the table as an EF migration, map to a `UserInstallation` entity.
  3. If no: write an ADR documenting the decision and delete all story language that implies multi-install-per-user roles.
- **Is it "just a stub" or is scope missing?** Scope was never ported and the story language that required it was not updated.
- **Blockers**: product decision; coordination with story 18-4.

## Remediation

- Files to modify: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md` (update to reflect C# reality) **or** add migration.
- Files to create: depending on decision, `UserInstallation.cs` entity + migration.
- Tests to add: if restored, basic CRUD; multi-install listing.
- Estimated effort: 3h (schema + entity + tests), or 30min (ADR).

## References

- TS source: `database/archived-sql-migrations/002_users.sql`
- C# source: `apps/tamma-elsa/src/Tamma.Data/Migrations/` (absence)
- Story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md`
- Related findings: `017-schema-github-installations-diff.md`, `027-schema-tenant-memberships-diff.md`
