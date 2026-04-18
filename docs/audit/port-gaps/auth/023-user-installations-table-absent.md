# Finding 023: `user_installations` table absent entirely from C# schema

**Scope**: auth
**Severity**: P1 (installation bootstrap + delete-cascade broken)
**Estimated port effort**: 4h (schema + repo + seeding on OAuth callback)

## 1. What's in TS

Pre-delete snapshots at archived SQL and TS interfaces.

- Migration: `database/archived-sql-migrations/002_users.sql:14-20` created the table.

```sql
-- database/archived-sql-migrations/002_users.sql:14-26
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

- TS model: `packages/api/src/persistence/user-store.ts:32-37`:

```typescript
// packages/api/src/persistence/user-store.ts:32-37 (9e9a57c~1)
export interface UserInstallation {
  userId: string;
  installationId: number;
  role: 'owner' | 'admin' | 'member';
  createdAt: string;
}
```

- Callers:
  - OAuth callback (`github-oauth.ts:165-173`) — on first login, links the user to all active installations (bootstrap):
    ```typescript
    const installations = await userStore.getUserInstallations(user.id);
    if (installations.length === 0) {
      const allInstallations = await installationStore.listActiveInstallations();
      for (const inst of allInstallations) {
        await userStore.linkUserToInstallation(user.id, inst.installationId, 'member');
      }
    }
    ```
  - User-detail endpoint (`user-routes.ts:67-68`) — returns user's installations.
  - Delete-user cascade (`user-routes.ts:136`) — unlinks all on soft-delete.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- No `UserInstallation` entity file exists under `apps/tamma-elsa/src/Tamma.Data/Entities/`.
- `User.cs` has no `UserInstallations` navigation collection (lines 24-29 show only `Memberships`, `RefreshTokens`, `PasswordResetTokens`, `ApiKeys` + `Tenant`).
- `TammaDbContext.cs` has no `DbSet<UserInstallation>`.
- InitialSchema migration (`20260416172234_InitialSchema.cs`) does not create a `user_installations` table. Verified by grepping all migrations for `UserInstallation` or `user_installations` — no matches.
- `IUserRepository` has no `LinkToInstallationAsync`, `GetInstallationsAsync`, or `UnlinkAllInstallationsAsync`.
- Tests: None.

## 3. The gap

The table simply doesn't exist. Therefore:

- **OAuth callback bootstrap is impossible** (Finding 008 is a stub; even when implemented, the "auto-link to all active installations" path has nowhere to write).
- **User-detail endpoint cannot return installations** — the TS `GET /api/admin/users/:id` returned `{ user, installations, apiKeys }`; C# `GetUser` (line 76-81) returns only `AdminUserResponse` with no installations slot.
- **Delete-cascade (Finding 019) has nowhere to delete from.**
- **RBAC for installation-scoped operations is architecturally missing.** A user's membership in a specific GitHub App installation (per-repo-group role) has no representation. A member-level user in the platform could have admin-level in a specific installation — TS modeled this; C# cannot.

Alternative modeling in C#: the Epic 17 `tenant_memberships` table could subsume this if one tenant per installation. But:
- Epic 17 decided tenants are at the org level (`Tenant.Type = 'personal' | 'organization'`), not the installation level (`github_installations` is still its own table in C# — see `GitHubInstallation.cs`).
- So `tenant_memberships` ≠ `user_installations`. They represent different scopes.

Production consequences:
- A user signs up via GitHub OAuth (when Finding 008 lands). They arrive with no installation access. Even though the tenant has GitHub App installed on repos, the user is disconnected — the API cannot resolve "which installations can this user see?" for dashboard listing, engine command authorization, etc.
- Installations-admin UI (if any) cannot list a user's install-level role.

Error paths: N/A — the data model simply can't express the relationship.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md`.
- Story AC 5 (line 17): *"Installation settings endpoint `GET/PUT /api/v1/orgs/:tenantId/installation` returns and updates installation-level settings"*.
- Story subtask 3.2 (line 43): *"When `installation.created` webhook arrives with `state` parameter, extract `tenantId` and link"* — this is tenant↔installation link, which DOES exist in C# via `GitHubInstallation.TenantId`.
- Story 18-4 does NOT discuss user↔installation directly. The TS `user_installations` table was created earlier (migration 002 — pre-Epic 17) and partially supplanted by the tenant model introduced in Epic 17 (migration 008+).
- Arguably, user↔installation linking is legacy pre-Epic-17 modeling that Epic 17 made redundant for tenant-owned operations. But TS still used it for installation-scoped RBAC in OAuth callback.
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior (schema)
  - [ ] Describes a third behavior
  - [x] No story — `user_installations` is pre-Epic-17 modeling; Epic 17 didn't explicitly retire it but didn't re-spec it either

Decision needed from architecture: does Tamma's model intentionally drop user-level install scoping in favor of tenant-level scoping (where all tenant members see all tenant-linked installations)?

## 5. Status

- **Classification**: Data-model regression (table absent) — with potential for semantic rewrite if the model was intentionally collapsed.
- **What's needed to finish** (if restoring the TS model):
  1. Create `UserInstallation` entity: `{ UserId (FK), InstallationId (FK), Role, CreatedAt }`, composite PK `(UserId, InstallationId)`.
  2. Create an EF migration `UserInstallations` adding the table + index on `InstallationId`.
  3. Add `DbSet<UserInstallation>` to `TammaDbContext`.
  4. Add `ICollection<UserInstallation>` navigation on both `User` and `GitHubInstallation`.
  5. Extend `IUserRepository` with `LinkToInstallationAsync(Guid userId, long installationId, string role)`, `GetInstallationsAsync(Guid userId)`, `UnlinkAllInstallationsAsync(Guid userId)`.
  6. Wire into OAuth callback (Finding 008) and delete cascade (Finding 019).
- **Alternative (if Epic 17 intentionally collapses this)**: document the decision in an ADR under `.dev/decisions/`, update Story 18-4 to explicitly say "user access is via tenant membership only; installation-level roles are not modeled". Then close this finding as wontfix.
- **Blockers**: Decision needed before implementation.

## Remediation

- Files to modify: `TammaDbContext.cs`, `User.cs`, `GitHubInstallation.cs`, `IUserRepository.cs`, `UserRepository.cs`, eventually `AuthEndpoints.GitHubCallback` and `DeleteUser`.
- Files to create: `apps/tamma-elsa/src/Tamma.Data/Entities/UserInstallation.cs`; EF migration `AddUserInstallations.cs`.
- Tests to add:
  - `UserRepository_LinkToInstallation_AddsRow`.
  - `UserRepository_GetInstallations_ReturnsLinked`.
  - `UserRepository_UnlinkAllInstallations_RemovesAll`.
  - `GitHubCallback_NewUserWithNoInstallations_AutoLinksToAllActive` (Finding 008 integration).
- Estimated effort: 4h
  - Entity + migration + DbContext + nav props: 1.5h
  - Repo methods: 1h
  - Tests: 1h
  - OAuth callback + DeleteUser integration: 0.5h (part of 008 and 019)

## References

- TS source: `packages/api/src/persistence/user-store.ts:32-37` (model), `packages/api/src/routes/auth/github-oauth.ts:165-173` (bootstrap), `packages/api/src/routes/users/user-routes.ts:67-68, 136` (callers) (commit `9e9a57c~1`)
- Archived SQL: `database/archived-sql-migrations/002_users.sql:14-26`
- C# source: None — table missing
- Story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md` (does not explicitly cover user_installations)
- Related findings: `008-oauth-callback-stub.md`, `019-admin-delete-user-no-cascade.md`, `022-user-repository-missing-methods.md`
