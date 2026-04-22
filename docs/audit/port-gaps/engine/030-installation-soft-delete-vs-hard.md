# Finding 030: Installation soft-delete semantics — uses `SuspendedAt` as the delete marker

**Scope**: engine (GitHub App)
**Severity**: P3 (drift / contract — "deleted" and "suspended" collide)
**Status**: Data-model regression
**Estimated port effort**: 2h

## 1. What's in TS

- File: `packages/api/src/persistence/pg-installation-store.ts:39-41` (9e9a57c~1)

```typescript
// pg-installation-store.ts:39-41 (9e9a57c~1)
async removeInstallation(installationId: number): Promise<void> {
  await this.pool.query('DELETE FROM github_installations WHERE installation_id = $1', [installationId]);
}
```

`removeInstallation` was a **hard delete**. The `github_installation_repos` rows cascaded (archived migration 001: `ON DELETE CASCADE`). No row was retained post-uninstall. Reinstallation by the same account created a fresh row.

Suspension was a separate field (`suspended_at TIMESTAMPTZ` nullable). The two semantics — "suspended" (paused, can unsuspend) and "deleted" (gone) — were kept distinct.

## 2. What's in C#

- File: `apps/tamma-elsa/src/Tamma.Data/Repositories/InstallationRepository.cs:111-122`

```csharp
// InstallationRepository.cs:111-122 (current)
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

The C# port changed semantics in two ways:

1. Hard delete → soft delete ("keeps the row for audit" per code comment).
2. **Reused** the `SuspendedAt` column as the soft-delete marker rather than adding a `DeletedAt` column.

And the sibling method `SetSuspendedAsync` also writes to `SuspendedAt`:

```csharp
// InstallationRepository.cs:124-134
public async Task SetSuspendedAsync(long installationId, bool suspended)
{
    // ...
    installation.SuspendedAt = suspended ? DateTime.UtcNow : null;
    // ...
}
```

Which means: after `SoftDeleteAsync` runs, `SuspendedAt` is non-null. If then a `suspend_repositories` webhook arrives, `SetSuspendedAsync(true)` is called → no-op (already set). If later an `unsuspend` webhook arrives → `SetSuspendedAsync(false)` clears `SuspendedAt` → the "deleted" state is now gone. The installation reappears.

## 3. The gap

- TS did: hard delete on `installation.deleted` webhook. Clean slate.
- C# does: soft-delete via `SuspendedAt`. "Suspended" and "deleted" now occupy the same column and can no longer be distinguished.

Observable consequences:

1. After a `installation.deleted` webhook, a subsequent `installation.unsuspend` webhook resurrects the "deleted" record.
2. Queries like "show me all suspended installations" return both currently-suspended and historically-deleted ones.
3. Listing active installations (`ListActiveAsync`) excludes both correctly, but there's no way to query just the deleted ones.
4. The `api_keys` table, the `tenants` table, and any cascading relations weren't audited for the hard-delete vs soft-delete pivot. Some FK behaviour might have been designed for cascade deletes that now never happen.

Note the audit summary phrasing: "Installation soft-delete vs hard-delete on `github_installations.deleted`". The TS version **had a `deleted` column implicitly (via hard deletion)**; the C# port conflates it with suspended.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md`.
- Archived SQL: `database/archived-sql-migrations/001_github_installations.sql` only has `suspended_at`; there is no `deleted` column in the original schema. So the TS hard-delete was the design; adding a `deleted_at` or equivalent is a new decision.
- Story alignment:
  - [x] Matches TS behavior (hard delete — C# is a data-model regression)
  - [ ] Matches C# behavior (deliberate pivot but causes state collision)
  - [x] Describes a third behavior (needs a `DeletedAt` column if soft-delete is desired)
  - [ ] No story

## 5. Status

- **Classification**: Data-model regression — semantics collision.
- **What's needed to finish**: Two options.
  - **Option A (match TS)**: hard delete on `installation.deleted`. Remove the "keeps the row for audit" comment — audit is preserved by the `INSTALLATION.DELETED.SUCCESS` event emitted at `InstallationRouterService.cs:268-278`. That event carries the installation id and can survive the row deletion.
  - **Option B (keep soft-delete, fix collision)**: add `DeletedAt` column distinct from `SuspendedAt`. Update `SoftDeleteAsync` to set `DeletedAt`. Update `ListActiveAsync` and every other filter to honour both.
- **Is it "just a stub" or is scope missing?** Pivot decision needed. Quick implementation either way.
- **Blockers**: none.

## Remediation

### Option A (hard delete, 1.5h)

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/InstallationRepository.cs:111-122` — switch to `db.GitHubInstallations.Remove(...)`.
- Tests to add:
  - `SoftDelete_RemovesRow_Cascade` (rename method too).
  - `DeletedThenUnsuspended_DoesNotResurrect`.
- No migration needed.

### Option B (add DeletedAt, 2h)

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Data/Entities/GitHubInstallation.cs` — add `DateTime? DeletedAt`.
  - `InstallationRepository.cs` — update `SoftDelete`, `SetSuspendedAsync`, `ListActiveAsync`.
  - New EF migration.
- Tests:
  - `SoftDelete_SetsDeletedAt_NotSuspendedAt`
  - `Suspend_SetsSuspendedAt_NotDeletedAt`
  - `Unsuspend_DoesNotClearDeletedAt`
  - `ListActive_ExcludesBothDeletedAndSuspended`

- Estimated effort: 2h either way.

## References

- TS source: `packages/api/src/persistence/pg-installation-store.ts:39-41` (hard delete)
- C# source: `apps/tamma-elsa/src/Tamma.Data/Repositories/InstallationRepository.cs:111-134`
- Archived SQL: `database/archived-sql-migrations/001_github_installations.sql`
- Story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md`
- Related findings: `020-saas-key-rotation-id-type.md`, `029-installation-router-no-cache.md`

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed (Option A — match TS hard delete)
- **Commit**: a3d2e7e
- **Notes**: `installation.deleted` webhook now calls
  `IInstallationRepository.DeleteAsync(installationId)` (hard delete). The
  `INSTALLATION.DELETED.SUCCESS` event preserves audit. The
  reuse-`SuspendedAt`-as-soft-delete-marker collision is gone — an
  `unsuspend` webhook can no longer resurrect a deleted record because
  the row no longer exists. Cache also invalidated on delete so the
  next webhook lookup correctly returns null.
