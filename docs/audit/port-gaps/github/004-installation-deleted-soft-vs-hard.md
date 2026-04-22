# Finding 004: Installation lifecycle — soft-delete vs hard-delete semantics drift

**Scope**: github
**Severity**: P3 (drift/contract)
**Status**: Behavioral drift (ported but semantics diverged)
**Estimated port effort**: 2h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/github/github-webhook.ts`.

- File: `packages/api/src/routes/github/github-webhook.ts:163-168`
- Contract/behavior: On `installation.deleted` the TS handler called `removeInstallation(id)` on the store — this is a **hard delete** (the row is removed from `github_installations`, cascading to `github_installation_repos` by `ON DELETE CASCADE`, see `database/archived-sql-migrations/001_github_installations.sql:17`).

```typescript
// packages/api/src/routes/github/github-webhook.ts:163-168 (9e9a57c~1)
} else if (action === 'deleted') {
  await options.installationStore.removeInstallation(id);
  // Invalidate the cache when an installation is deleted
  if (options.installationRouter) {
    options.installationRouter.invalidate(id);
  }
}
```

The store method name (`removeInstallation`) and the archived migration's cascade behavior together make the intent unambiguous: the installation is gone. Audit trail for "who had this installation" only survives via emitted domain events, not via the table.

- Dependencies: `IGitHubInstallationStore.removeInstallation(installationId)`; CASCADE on `github_installation_repos.installation_id`; `InstallationRouter.invalidate` (see Finding 005).
- Tests that exercised this: webhook handler tests asserted that after `installation.deleted` the installation row was no longer retrievable.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs:268-279`; `apps/tamma-elsa/src/Tamma.Data/Repositories/InstallationRepository.cs:111-122`
- Contract/behavior: On `installation.deleted` the handler calls `_installations.SoftDeleteAsync(installationId)`, which **reuses `SuspendedAt`** as a soft-delete marker. The row stays; the `SuspendedAt` timestamp is populated. Linked `GitHubInstallationRepo` rows are untouched.

```csharp
// apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs:268-279 (current)
case "deleted":
{
    await _installations.SoftDeleteAsync(installationId.Value);
    await EmitEventAsync(
        "INSTALLATION.DELETED.SUCCESS",
        null,
        new Dictionary<string, object?>
        {
            ["installationId"] = installationId
        });
    return new WebhookResult("installation", action, Skipped: false);
}
```

```csharp
// apps/tamma-elsa/src/Tamma.Data/Repositories/InstallationRepository.cs:111-122 (current)
public async Task SoftDeleteAsync(long installationId)
{
    var installation = await db.GitHubInstallations
        .FirstOrDefaultAsync(i => i.InstallationId == installationId);
    if (installation is not null)
    {
        // Use SuspendedAt as the soft-delete marker — keeps the row for audit.
        installation.SuspendedAt = DateTime.UtcNow;
        installation.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }
}
```

A true hard-delete method exists on the repository (`DeleteAsync`, line 51-60) but is not called by the webhook path.

- Dependencies: `IInstallationRepository.SoftDeleteAsync`, `TammaDbContext.GitHubInstallations`.
- Tests: `InstallationRouterServiceTests` covers the deleted branch; asserts the handler returns `Skipped=false`. It does not explicitly assert that the row remains or that `SuspendedAt` is set.

## 3. The gap

Concrete behavioral difference — what a caller or user experiences differently.

- TS did: DELETE the `github_installations` row; CASCADE deleted the `github_installation_repos` rows.
- C# does: UPDATE the `github_installations` row setting `SuspendedAt = now()`; leave `github_installation_repos` rows untouched and keyed to the (still-present) installation.
- For a caller doing `GET /api/v1/orgs/:tenantId/installations` (hypothetical list endpoint) after a `deleted` event, TS would return zero rows; C# returns the row with a non-null `SuspendedAt`. Consumers that filter on `SuspendedAt IS NULL` will see the same "hidden" behavior as TS, but consumers that do a raw count will see a divergence. `ListActiveAsync` at `InstallationRepository.cs:45-49` filters `SuspendedAt == null`, so it will exclude these rows — matching TS's observable list behavior.
- **Semantic collision with `suspend`/`unsuspend`**: a `deleted`-then-new-install sequence becomes ambiguous. A subsequent `installation.created` for the **same account** produces a different `installation_id` (GitHub issues fresh IDs), so there's no literal row collision. But if a user uninstalls and re-installs quickly, you now have a tombstone row with `SuspendedAt` set alongside a new live row. The "suspended" semantic is overloaded.
- Linked repos leak: `github_installation_repos` rows for the deleted installation remain. Nothing queries them (the `ListActiveAsync` filter drops the parent), but they accumulate forever.
- In production with existing data / deployed clients, this means: the table grows monotonically. Audit queries for "installations that existed on date X" get richer information — arguably a positive. `ApiKey` rows previously pinned to a deleted installation survive the "delete" — see Finding 006 and Finding 018 for how that interacts with key rotation.

Error paths:
- TS error path: row not found → silent no-op inside `removeInstallation`.
- C# error path: row not found → silent no-op at `InstallationRepository.cs:114-118` (the null-check).

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md`
- Story's acceptance criteria for this behavior: Task 3 Subtask 3.4 says: "Handle `installation.deleted` by marking installation as **removed in org context**." The verb "marking … as removed" is ambiguous — it reads closer to soft-delete than hard-delete. So the story arguably authorizes the C# semantics.
- Story alignment:
  - [ ] Matches TS behavior (C# is a regression vs both story and TS)
  - [x] Matches C# behavior (story was updated during port; TS was ahead of spec)
  - [ ] Describes a third behavior
  - [ ] No story — spec gap

However, the story does NOT discuss:
- The overload of `SuspendedAt` as both "suspended" and "deleted" marker.
- The fate of child `github_installation_repos` rows.
- Whether `ApiKey` rows should be revoked on delete.

These are real spec gaps and must be resolved before declaring this finding closed.

## 5. Status

- **Classification**: Behavioral drift — explicitly intentional (the repository comment says "Use SuspendedAt as the soft-delete marker — keeps the row for audit"), but introduces ambiguity.
- **What's needed to finish**:
  1. Add a dedicated `DeletedAt` column (nullable timestamptz) and stop overloading `SuspendedAt`. Migrate existing rows: if you can't distinguish old "suspended" from "deleted" rows after the fact, accept that and start fresh from here.
  2. On `installation.deleted`: set `DeletedAt`, and cascade to `github_installation_repos` (either hard-delete children or set an `IsActive = false` on each — the entity already supports this via `GitHubInstallationRepo.IsActive`).
  3. On `installation.deleted`: revoke any active `ApiKey` rows for this installation (coordinate with Finding 006).
  4. Update `ListActiveAsync` to filter `DeletedAt == null AND SuspendedAt == null`.
  5. Keep `DeleteAsync` (hard-delete) for admin-only / GDPR erasure flows.
- **Is it "just a stub" or is scope missing?** Scope shift. The port deliberately moved from hard-delete to soft-delete; that was a defensible design choice but left loose ends. Fix the loose ends rather than revert.
- **Blockers**:
  - Schema migration (add `DeletedAt` column) — low-risk.
  - Coordination with Finding 006 (API key revocation on delete) — medium coupling.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Data/Entities/GitHubInstallation.cs` — add `DateTime? DeletedAt`.
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/InstallationRepository.cs` — `SoftDeleteAsync` writes `DeletedAt` (not `SuspendedAt`); cascade to repos; `ListActiveAsync` filters both.
  - `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs:268-279` — on deleted, invoke key-revocation path (Finding 006).
- Files to create: one EF Core migration adding `DeletedAt` column.
- Tests to add:
  - `InstallationRouterServiceTests.HandleWebhook_InstallationDeleted_SetsDeletedAt_NotSuspendedAt`
  - `InstallationRouterServiceTests.HandleWebhook_InstallationDeleted_CascadesRepoDeactivation`
  - `InstallationRepositoryTests.ListActive_ExcludesDeleted`
- Estimated effort: 2h broken down as:
  - Schema + entity + repo update: 1h
  - Tests + migration: 1h

## References

- TS source: `packages/api/src/routes/github/github-webhook.ts:163-168` (commit `9e9a57c~1`)
- C# source:
  - `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs:268-279`
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/InstallationRepository.cs:111-122,51-60`
  - `apps/tamma-elsa/src/Tamma.Data/Entities/GitHubInstallation.cs`
- Story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md` (Task 3.4)
- Related findings: `005-no-cache-invalidation-hook.md`, `006-installation-created-no-provisioning.md`, `021-installation-id-bigint-pk-vs-guid.md`
- Archived SQL migration: `database/archived-sql-migrations/001_github_installations.sql`

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Already-fixed
- **Commit**: `a3d2e7e` (engine scope, finding 030)
- **Notes**: Engine-scope remediation already switched `installation.deleted` to hard-delete via `_installations.DeleteAsync(installationId)` (matches TS contract). The `INSTALLATION.DELETED.SUCCESS` event preserves the audit trail. The ambiguous SuspendedAt overload concern is resolved by the hard-delete path; suspend/unsuspend remain on `SuspendedAt`. No additional schema work required.
