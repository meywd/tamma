# Story 29-7: Postgres Role-Password Rotation Workflow

Status: todo (planning brief, 2026-04-20)

## Story

As a **platform or tenant administrator**,
I want a `PostgresRoleRotationHandler` that plugs into the generic rotation workflow from Story 29-6 and rotates a Postgres role's password atomically — `ALTER ROLE ... WITH PASSWORD` in the DB, push to the secret store, probe with a fresh connection, drain the old pool,
so that `tamma_app` (per-tenant DB app role) and any other Postgres role managed by Tamma can be rotated through the same audited workflow as every other secret.

## Acceptance Criteria

1. `PostgresRoleRotationHandler : IRotationHandler` registered with `System = "postgres"`. Resolved by the `ResolveHandlerActivity` in 29-6 when a secret's first `ConsumerRef` is `{ system: "postgres", identifier: "role=<name>;db=<dbname>" }`.
2. `PushAsync`:
   - Opens an admin-role connection to the target database (resolved via the `ITenantConnectionResolver` for tenant-scoped secrets, `ConnectionStrings:TammaAdmin` for platform-scoped).
   - Runs `ALTER ROLE "<role>" WITH PASSWORD '<new>'` using parameterised SQL (Npgsql does **not** support parameters in `ALTER ROLE`, so the handler escapes the password into a safe SQL literal after validating it matches `[A-Za-z0-9!@#$%^&*()_+\-=\[\]{}|;:,.<>?]+` — no single quotes, no backslashes).
   - Updates the secret-store version to `Pending` with the new plaintext already persisted (the row exists from 29-6's `MintPendingVersionActivity`).
3. `ProbeAsync`:
   - Opens a **fresh** connection pool using the new password; runs `SELECT 1`; closes.
   - Returns `ProbeResult.Healthy` on success; `ProbeResult.Unhealthy(reason)` on any exception (captures `ExceptionType`, `SqlState`, first 120 chars of message — never the password).
4. `RollbackAsync`:
   - Opens admin-role connection.
   - Runs `ALTER ROLE "<role>" WITH PASSWORD '<previous>'` using the previous `Active` version's plaintext (fetched from the secret store by version number).
   - If there is no previous active version (brand-new role), drops the password by running `ALTER ROLE "<role>" WITH PASSWORD NULL` + emits `SECRET.ROTATE.ROLLBACK.ROLE_DISABLED`.
5. `PostgresConnectionPoolDrainer` is invoked by `ActivateNewVersionActivity` (via a handler post-hook) so existing pooled connections using the old password are evicted after the grace window expires. Drains by calling `NpgsqlConnection.ClearPool(cs)` on the old connection-string pool. Emits `POOL.DRAINED`.
6. Integration test with Testcontainers Postgres 17:
   - Rotate `tamma_app` password.
   - Assert new-pool connection succeeds with new password.
   - Assert old-pool connection fails after drain.
   - Inject probe failure (kill the DB momentarily mid-probe) → compensation rolls back to the old password; verify old-pool connection still works.
7. Password generator for DB credentials produces 64 chars from the validated set in AC 2; no special chars that need escaping. Unicode not permitted — some Postgres versions treat Unicode passwords inconsistently across encoding settings.
8. Handler refuses to rotate a role it does not "own": maintains a whitelist of role names (`tamma_app`, `tamma_engine`, ...) so a malformed or tampered secret metadata cannot drive the handler into `ALTER ROLE postgres WITH PASSWORD ...`. Whitelist is per-tenant for tenant-scoped secrets.
9. Handler supports a dry-run mode (for UI preview): mints the password, validates SQL-literal safety, returns the would-execute statement with password redacted, does not touch the DB. Used by 29-4's "preview rotation" button.
10. Depends on Story 19-6 — if `TammaAppDbContext` is not yet wired onto the app role, the rotation still works (the admin role can ALTER any role) but the grace-window pool drain is a no-op because nothing pools the app-role connection yet. 29-7's rollout runbook calls this out.

## Technical Context

### Grace window + pool drain

Without draining, a pool holds onto connections authenticated against
the old password. After the grace window and the old password is
revoked (29-6's `ScheduleRetireOldActivity`), the server closes the
old connection but the pool may re-dial and fail. `ClearPool` forces
the pool to close idle connections; in-flight requests finish on
their current connection. This is the standard Npgsql pattern for
credential rotation.

### SQL literal safety

Postgres `ALTER ROLE` does not accept parameter placeholders — the
password has to be in the SQL text. To avoid injection we:

1. Generate the password from a fixed safe character set (no single
   quote, no backslash, no semicolon).
2. Validate the generated password against the same regex before
   interpolation.
3. Use a SQL literal helper that single-quote-escapes as a belt-and-
   braces measure even though step 2 rules it out.

### Why not use Vault's dynamic DB credentials

Vault / OpenBao can mint short-lived DB users on-demand. That's a
future optimisation (Story 28-13 + its Epic 30 BYO-cloud adoption).
Today's Postgres role is long-lived and named (`tamma_app`); rotating
its password is the pragmatic step.

## Estimated hours

14 — handler + pool drainer + integration tests + password validator +
whitelist + runbook.

## Files to touch

- `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/Handlers/PostgresRoleRotationHandler.cs` (new)
- `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/Handlers/PostgresPasswordGenerator.cs` (new)
- `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/PostgresConnectionPoolDrainer.cs` (new)

## References

- Story 29-6 workflow primitive
- Story 19-6 per-tenant routing (co-requirement)
- Npgsql `ClearPool` docs (Npgsql 8.x)
- Research notes §3
