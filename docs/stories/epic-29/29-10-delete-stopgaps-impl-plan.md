# Story 29-10 Implementation Plan — Delete Stopgaps

**Status**: Planned (2026-04-20)
**Story brief**: [`29-10-delete-stopgaps.md`](./29-10-delete-stopgaps.md)
**Epic 29 phase**: Cleanup — one release after 29-9.
**Branch**: `feat/story-29-10-delete-stopgaps`

---

## 1. Objective

Delete `TenantSecretProtector`, drop the `cranl_database_url_encrypted`
column, remove the env-var fallback in `RuntimeSecretResolver`, and
add a CI forbidden-symbols guard so these stopgaps cannot resurrect.
Ships one release cycle after 29-9 to confirm the cabinet path works
end-to-end in production.

## 2. Dependencies

Hard blockers:

- **Story 29-9** — migrate-secrets has run in production.
- **Story 19-6** — co-requirement for `tamma_app` liveness.
- One release cycle of observation post-29-9.

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Migrations/20260501000000_DropCranlDatabaseUrlEncrypted.cs` | Asserts cabinet parity, then drops column. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Migrations/20260501010000_RotateTammaAppPassword.cs` | Safety-net assertion: `tamma_app` has been rotated off `changeme`. |
| `/home/meywd/tamma/ci/forbidden-symbols.txt` | Ripgrep patterns that fail CI. |
| `/home/meywd/tamma/.github/workflows/forbidden-symbols.yml` | CI workflow. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/TenantSecretProtector.cs` | **Delete**. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/ITenantConnectionResolver.cs` + impl | Remove `cranl_database_url_encrypted` fallback. Read from `ISecretStore.GetAsync("tenant:db/cranl-connection", tenantId)`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/ProvisioningServiceCollectionExtensions.cs` | Drop protector registration. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Secrets/RuntimeSecretResolver.cs` | Remove env-var fallback; fail-fast if cabinet missing. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Entities/Tenant.cs` | Remove `CranlDatabaseUrlEncrypted` property. |
| `/home/meywd/tamma/docs/runbooks/enable-app-role-rls.md` | Remove `ALTER ROLE tamma_app` manual step; point at UI rotation. |
| `/home/meywd/tamma/CHANGELOG.md` | Add release note paragraph. |

## 5. Sequence of changes

### Step 1 — Assert cabinet parity migration (3h)

- `20260501000000_DropCranlDatabaseUrlEncrypted.cs`:
  - `SELECT COUNT(*) FROM tenants WHERE cranl_database_url_encrypted IS NOT NULL` → `A`.
  - `SELECT COUNT(*) FROM tenant_secrets WHERE name='db/cranl-connection'` → `B`.
  - If `A != B`, migration THROWS with "run 29-9 migrate-secrets first".
  - Else drop column.
- Integration test: seed `A=B`, migration succeeds; seed `A=3, B=2`, migration throws.
- **Commit**: `migration(secrets): drop cranl_database_url_encrypted`.

### Step 2 — Rotate safety-net migration (2h)

- `20260501010000_RotateTammaAppPassword.cs`:
  - Asserts at least one `Active` version exists for
    `platform:db/tamma_app` in secret store AND its plaintext
    decoded is not `changeme`.
  - Decoding via `ISecretStore.GetVersionAsync` called from a
    data-seed migration helper.
  - Throws on violation.
- **Commit**: `migration(secrets): assert tamma_app rotated`.

### Step 3 — Delete TenantSecretProtector (2h)

- Delete the file.
- Remove `using` + DI registration in `ProvisioningServiceCollectionExtensions`.
- `ITenantConnectionResolver` reads from cabinet via store.
- **Commit**: `chore(secrets): delete TenantSecretProtector`.

### Step 4 — Remove env fallback (2h)

- `RuntimeSecretResolver.GetAsync` no longer calls env fallback.
- Missing cabinet entry throws
  `MissingSecretException("Run migrate-secrets from Story 29-9")`.
- Unit tests: missing throws, present returns.
- **Commit**: `chore(secrets): remove env-var fallback`.

### Step 5 — Remove column from entity (1h)

- Delete `Tenant.CranlDatabaseUrlEncrypted` property.
- Verify all call sites were migrated in step 3.
- **Commit**: `chore(db): remove CranlDatabaseUrlEncrypted from entity`.

### Step 6 — Forbidden-symbols CI guard (2h)

- `ci/forbidden-symbols.txt`:
  ```
  TenantSecretProtector
  Cranl:EncryptionKey
  cranl_database_url_encrypted
  sk_live_
  AKIA[0-9A-Z]{16}
  -----BEGIN RSA PRIVATE KEY-----
  -----BEGIN PRIVATE KEY-----
  ```
- `.github/workflows/forbidden-symbols.yml` runs `rg --no-ignore -f
  ci/forbidden-symbols.txt apps/ packages/` excluding migrations and
  test fixtures; fails on any match.
- **Commit**: `ci(security): forbidden-symbols guard`.

### Step 7 — Docs + release notes (1h)

- Update `enable-app-role-rls.md`.
- CHANGELOG entry.
- **Commit**: `docs(release): 29-10 cleanup notes`.

## 6. Test strategy

### Unit

- `RuntimeSecretResolver` fail-fast on missing cabinet entry.

### Integration

- Boot with cabinet absent → startup throws.
- Boot with cabinet populated → startup succeeds.
- Migration assert path: parity met vs. missing.

### CI

- Forbidden-symbols workflow fails on a planted literal.

## 7. Rollback plan

- **Revert**: each commit independent. Reverting step 4 restores
  env fallback path.
- **Migration rollback**: `20260501000000` has a `Down` method that
  re-adds the column (but the data is gone; downgrade path is
  operator-only).
- **Non-reversible**: once the column is dropped, its data is lost.
  Rolling back requires 29-9 to restore from cabinet.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Cabinet parity migration | 3 |
| 2. Rotate safety-net migration | 2 |
| 3. Delete protector | 2 |
| 4. Remove env fallback | 2 |
| 5. Entity cleanup | 1 |
| 6. CI guard | 2 |
| 7. Docs + release notes | 1 |
| **Total** | **13** (brief 8; plan adds testing + safety-net
migration + CI guard). |

## 9. Open questions

- **Release-cycle wait**: how long is "one release cycle"? Plan:
  2 weeks post-29-9 deploy with no cabinet-related incidents.
- **Forbidden-symbols exclusion list**: migrations must reference
  old names (their `Up` methods created them). Exclude via
  `Migrations/` directory glob.
- **Decoding KEK in a migration**: EF migrations don't have DI.
  Workaround: migration invokes a `MigrationHelperService` that
  wraps `ISecretStore`. Pattern from 28-5 `MigrateTenantDb`.
- **Revert-migration safety**: `Down` method restores column but
  not data. Operator must know this; runbook updated.
- **Startup with empty cabinet**: fail-fast is intentional. Local
  dev gets a `TAMMA_SKIP_CABINET=true` bypass for convenience.
