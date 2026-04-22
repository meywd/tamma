# Story 29-10: Delete Stopgaps — `TenantSecretProtector`, `cranl_database_url_encrypted`, Env-Var Fallbacks

Status: todo (planning brief, 2026-04-20)

## Story

As a **platform engineer**,
I want the stopgap helpers and columns from Epic 28 Phase-3 deleted after the cabinet migration (Story 29-9) has been live for one release cycle,
so that there is exactly one secret-storage surface in the codebase — the Epic 29 cabinet — and the `TenantSecretProtector`, the `tenants.cranl_database_url_encrypted` column, and the env-var fallback path all go away.

## Acceptance Criteria

1. Delete `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/TenantSecretProtector.cs`.
2. Delete any `using`s and DI registrations that referenced it. Grep-level CI check added to `ci/forbidden-symbols.txt` that fails the build if `TenantSecretProtector`, `Cranl:EncryptionKey`, or `cranl_database_url_encrypted` appear in any non-deleted-migration C# file.
3. Migration `20260501000000_DropCranlDatabaseUrlEncrypted.cs`:
   - Asserts the cabinet has a matching `tenant:db/cranl-connection` row for every tenant whose `cranl_database_url_encrypted` is non-null (by counting rows in `tenant_secrets` with the expected name).
   - If mismatch, the migration **throws** with a message pointing at Story 29-9's runbook — refuses to drop the column silently.
   - Drops the column via `ALTER TABLE tenants DROP COLUMN cranl_database_url_encrypted`.
4. Rename / drop `ProvisioningServiceCollectionExtensions` references to the protector. `ITenantConnectionResolver` stops reading `cranl_database_url_encrypted` and instead calls `ISecretStore.GetAsync("tenant:db/cranl-connection", tenantId)` followed by a reveal against the in-process handler path (not the reveal-token UX — this is a machine consumer).
5. Delete the env-var fallback in `RuntimeSecretResolver` (introduced in Story 29-9 AC 5) that reads `ConnectionStrings:TammaAppDb`, `TAMMA_SHARED_SECRET`, `Cranl:ApiKey`, `GitHub:WebhookSecret`, and `Cranl:EncryptionKey`. Startup now **requires** the cabinet to be populated; missing rows fail-fast with a clear error ("Run migrate-secrets command from Story 29-9").
6. Update `docs/runbooks/enable-app-role-rls.md` (from Story 19-6) to remove references to `ALTER ROLE tamma_app WITH PASSWORD` — that flow is now owned by Story 29-7's rotation handler, and the runbook points at the admin UI's rotation button.
7. Update `apps/tamma-elsa/src/Tamma.Data/Migrations/20260419021119_Phase2RlsAndTriggers.cs` historical migration *not* to change its existing content (migrations are immutable once shipped) but ship a follow-up migration `20260501010000_RotateTammaAppPassword.cs` that asserts the cabinet has rotated the `tamma_app` password off the `changeme` literal. This is the final safety net for any deployment that skipped 29-9's auto-rotate.
8. Integration test: boot the process with the cabinet absent → assert startup throws with the expected error. Boot with cabinet populated → assert startup succeeds and all previously-env-var secrets resolve correctly.
9. Audit the PR against a checklist of "anywhere a secret might still leak through a code path" (ripgrep patterns in `ci/forbidden-symbols.txt` — literal `sk_`, `cranl_sk_`, `tamma_sk_`, `AKIA`, `BEGIN PRIVATE KEY`, `BEGIN RSA` — fail the build if present in non-test, non-README, non-example files).
10. Release notes draft: single-paragraph operator-facing note explaining the removal + pointer to Story 29-9's runbook as the migration path. Fits in the existing `CHANGELOG.md` format.

## Technical Context

### Why a separate deletion story

Epic 28's Phase-3 helpers (`TenantSecretProtector`,
`cranl_database_url_encrypted`) are not *broken* — they're adequate
for the current single-tenant test deploy. Deleting them *before* the
cabinet migration has been observed to work in production would leave
a window where the cabinet is the only source of truth and any bug in
29-9 is a production outage.

By waiting one release after 29-9, the operator can verify the
cabinet is working for all existing secrets before the stopgap code
is removed.

### CI forbidden-symbols file

```
TenantSecretProtector
Cranl:EncryptionKey
cranl_database_url_encrypted
```

The CI step is a grep-level guard that fails the build if any of
these strings appear in `apps/` or `packages/` outside the migration
files that legitimately reference them. Prevents accidental
resurrection.

### Cross-check with Story 19-6

Story 19-6 (wire app-role context) is a prerequisite for this story —
once `tamma_app` is actually live on the per-request plane, the
rotation in 29-7 has teeth. Until 19-6 ships, 29-9 works but the pool
drain is a no-op. This story's migration (`20260501010000`) asserts
`tamma_app` has been rotated at least once — a quick smoke check that
the whole stack is coherent.

## Estimated hours

8 — deletions + CI guard + drop-column migration + final safety-net
migration + release notes.

## Files to touch

- `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/TenantSecretProtector.cs` (delete)
- `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/ITenantConnectionResolver.cs` (remove `_encrypted` fallback)
- `apps/tamma-elsa/src/Tamma.Data/Migrations/20260501000000_DropCranlDatabaseUrlEncrypted.cs` (new)
- `apps/tamma-elsa/src/Tamma.Data/Migrations/20260501010000_RotateTammaAppPassword.cs` (new)
- `ci/forbidden-symbols.txt` (new)
- `docs/runbooks/enable-app-role-rls.md` (update)
- `CHANGELOG.md` (update)

## References

- Story 29-9 (prerequisite)
- Story 19-6 (co-requisite)
- Epic 28 phase-3 commits: `e53c5a1`, `9e20e05`, `159f12a`
