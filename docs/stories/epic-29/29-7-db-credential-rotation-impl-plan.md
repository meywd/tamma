# Story 29-7 Implementation Plan — Postgres Role-Password Rotation

**Status**: Planned (2026-04-20)
**Story brief**: [`29-7-db-credential-rotation.md`](./29-7-db-credential-rotation.md)
**Epic 29 phase**: Handlers — after 29-6.
**Branch**: `feat/story-29-7-postgres-rotation`

---

## 1. Objective

Ship `PostgresRoleRotationHandler` that plugs into 29-6's workflow and
rotates a Postgres role password via `ALTER ROLE ... WITH PASSWORD`,
pushes to the secret store, probes with a fresh connection, drains
the old pool after the grace window. Replaces the manual `ALTER
ROLE tamma_app` runbook step with a one-click rotation from the UI.

## 2. Dependencies

Hard blockers:

- **Story 29-6** — rotation workflow contract.
- **Story 19-6** (co-requisite) — without app-role wiring the drain is a no-op.

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Secrets/Handlers/PostgresRoleRotationHandler.cs` | Handler impl. |
| `.../Services/Secrets/Handlers/PostgresPasswordGenerator.cs` | 64-char safe-charset generator. |
| `.../Services/Secrets/Handlers/SqlLiteralEscaper.cs` | Single-quote escape helper. |
| `.../Services/Secrets/PostgresConnectionPoolDrainer.cs` | `ClearPool` wrapper per brief AC5. |
| `.../Services/Secrets/Handlers/RoleWhitelist.cs` | Static allow-list per scope. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.IntegrationTests/Secrets/PostgresRoleRotationTests.cs` | Testcontainers integration. |
| `/home/meywd/tamma/docs/runbooks/postgres-role-rotation.md` | Ops runbook. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.ElsaServer.Global/Program.cs` | `AddKeyedSingleton<IRotationHandler, PostgresRoleRotationHandler>("postgres")`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Activities/Secrets/Activities/ActivateNewVersionActivity.cs` | Post-hook invokes pool drainer for postgres secrets. |

## 5. Sequence of changes

### Step 1 — Password generator + SQL escaper (2h)

- `PostgresPasswordGenerator.Generate(length=64)` from safe charset
  (regex `[A-Za-z0-9!@#$%^&*()_+\-=\[\]{}|;:,.<>?]+`).
- `SqlLiteralEscaper.Escape(literal)` doubles single quotes + validates regex.
- Unit tests: charset validation, escape round-trip, invalid char rejection.
- **Commit**: `feat(secrets): password generator + SQL escaper`.

### Step 2 — Role whitelist (1h)

- Platform-scope whitelist: `tamma_app`, `tamma_engine`, `tamma_admin` (never itself).
- Tenant-scope pattern: `^tamma_tenant_[0-9a-f]{32}$`.
- Whitelist check in `PushAsync`; violation throws with clear error.
- **Commit**: `feat(secrets): role-name whitelist`.

### Step 3 — PushAsync + RollbackAsync (3h)

- `PushAsync`:
  - Acquire admin connection (`ConnectionStrings:TammaAdmin` or tenant's admin pool).
  - Interpolate escaped password into `ALTER ROLE "<role>" WITH PASSWORD '<new>'`.
  - Parameterise the role name via `pg_escape_identifier` (still safe).
- `RollbackAsync`:
  - Fetch previous active version's plaintext.
  - ALTER back to previous.
  - If no prior active: `ALTER ROLE "<role>" WITH PASSWORD NULL` +
    emit `SECRET.ROTATE.ROLLBACK.ROLE_DISABLED`.
- **Commit**: `feat(secrets): postgres role push + rollback`.

### Step 4 — ProbeAsync (2h)

- Build a fresh `NpgsqlDataSource` with the new password.
- `SELECT 1`; close immediately.
- Return `Healthy` on success; `Unhealthy(ExceptionType, SqlState, first 120 chars)` on failure.
- **Commit**: `feat(secrets): postgres probe with fresh pool`.

### Step 5 — Pool drainer (2h)

- `PostgresConnectionPoolDrainer.Drain(oldConnectionString)`:
  - `NpgsqlConnection.ClearPool(cs)` forces idle close.
  - Emits `POOL.DRAINED`.
- Invoked by `ActivateNewVersionActivity` post-hook after grace window.
- **Commit**: `feat(secrets): postgres pool drainer`.

### Step 6 — Dry-run mode (1h)

- `PushAsync` accepts `RotationContext.DryRun=true`:
  - Generates password, validates, returns would-execute SQL with
    password redacted.
  - Does not touch DB.
- Used by 29-4 "preview rotation".
- **Commit**: `feat(secrets): dry-run mode for postgres rotation`.

### Step 7 — Integration tests (3h)

- Rotate `tamma_app` → new-pool connects, old-pool fails after drain.
- Probe failure → rollback → old password still works.
- Dry-run returns preview.
- **Commit**: `test(secrets): postgres rotation E2E`.

### Step 8 — Runbook (1h)

- `postgres-role-rotation.md`: UI flow, troubleshooting, pool-drain check.
- **Commit**: `docs(runbooks): postgres role rotation`.

## 6. Test strategy

### Unit

- Password generator charset, escape helper edge cases, whitelist.

### Integration (Testcontainers)

- Full rotation happy path.
- Probe failure → compensation restores old password.
- Pool drain verified via failed old-pool connection attempt.
- Dry-run returns redacted SQL without executing.

### Security

- Parameter injection via tampered metadata → whitelist blocks.
- Log inspection: no password bytes appear in any log line.

## 7. Rollback plan

- **Handler disable**: remove keyed registration.
- **Compensation safety**: brief AC4 — rollback path restores prior
  value or disables role.
- **Non-reversible**: password rotation is destructive in the sense
  that the old password is lost; rotation-compensation recovers by
  re-ALTERing to the prior plaintext fetched from secret store.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Generator + escaper | 2 |
| 2. Whitelist | 1 |
| 3. Push + rollback | 3 |
| 4. Probe | 2 |
| 5. Pool drainer | 2 |
| 6. Dry-run | 1 |
| 7. Integration tests | 3 |
| 8. Runbook | 1 |
| **Total** | **15** (brief 14). |

## 9. Open questions

- **SQL-literal double-defense**: charset whitelist + quote-escape.
  Pick one? Plan: both — belt and braces.
- **Admin connection string for tenant-scoped rotation**: resolved
  via tenant DB provisioner's admin credentials, or via the global
  `tamma_provisioner` role from 28-12? Plan: `tamma_provisioner`
  — it has `CREATEROLE`.
- **Unicode password support**: documented as unsupported per
  Postgres-version inconsistency.
- **Probe latency**: 3× retry with 5/15/45s = 65s worst case. OK for
  the 15-min grace window.
- **Preview-only permission**: 29-4 UI allows preview without
  executing — gated on `platform_admin` role.
