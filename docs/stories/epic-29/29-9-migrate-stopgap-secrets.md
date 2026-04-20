# Story 29-9: Migrate Stopgap Secrets Into the Cabinet

Status: todo (planning brief, 2026-04-20)

## Story

As a **platform operator**,
I want every secret that today lives in an env var, a config file, an unrelated DB column, or a literal `changeme` string to be imported into the Epic 29 cabinet with a recorded consumer map, rotation schedule, and owner,
so that the stopgaps from Epic 28 Phase-2/Phase-3 (`changeme` password, `TAMMA_SHARED_SECRET`, `cranl_database_url_encrypted`, `Cranl:ApiKey`, `Cranl:EncryptionKey`, GitHub App webhook HMAC) stop being single points of untracked failure and start appearing in the admin UI with real rotation schedules.

## Acceptance Criteria

1. A one-shot migration command `dotnet run --project Tamma.Api -- migrate-secrets` imports the following secrets into the cabinet:
   - `platform:db/tamma_app` — `Purpose=DbCredential`, consumer `{ system: postgres, identifier: role=tamma_app;db=tamma_control }`, rotation = every 90 days, owner = deploy-op admin. Value sourced from `ConnectionStrings:TammaAppDb` (env) or `changeme` (migration literal) — migration immediately triggers a rotation via 29-7 to replace the literal.
   - `platform:hmac/shared-engine` — from `TAMMA_SHARED_SECRET`. `Purpose=HmacSharedSecret`, consumer `{ system: tamma-engine, identifier: request-signing }`. Rotation every 30 days.
   - `platform:cranl/api-key` — from `Cranl:ApiKey`. `Purpose=ApiKey`, consumer `{ system: cranl, identifier: org-scoped }`. Rotation = none (manually rotated via the Cranl dashboard; this cabinet row tracks the value so a future per-tenant split has a home to migrate from).
   - For each existing tenant row with a non-null `cranl_database_url_encrypted`: `tenant:db/cranl-connection` (tenant-scoped), `Purpose=Connection`, consumer `{ system: cranl, identifier: app=<appId>;env=DATABASE_URL }`. Rotation = none initially; tenant admin can opt in.
   - `platform:github/app-webhook-hmac` — from `GitHub:WebhookSecret`. `Purpose=HmacSharedSecret`, consumer `{ system: github_webhook, identifier: app-level }`.
2. Migration is **idempotent**: running it twice is a no-op on the second run (existing rows are identified by `(scope, tenantId, name)` and skipped).
3. Migration emits `SECRET.IMPORTED` audit events with `{ source: "env" | "config" | "db_column" | "migration_literal", previousLocation: "ConnectionStrings:TammaAppDb" | ... }` for every row imported.
4. After import, for every secret that was a `changeme` literal or other known-bad initial value (today: only `tamma_app` password), the migration **immediately** triggers `RotateSecretWorkflow` via Story 29-7's handler. The `changeme` value is never read by the runtime — the rotation workflow mints a new password before any repository tries to connect as `tamma_app`.
5. Post-migration, reading `ConnectionStrings:TammaAppDb`, `TAMMA_SHARED_SECRET`, or `Cranl:ApiKey` from the process is done by a new `IRuntimeSecretResolver` which fetches from `ISecretStore` at startup and caches. Env-var fallback is kept during the grace window (one release cycle) with a startup warning `"Using env-var fallback for <name>; deprecated — see Story 29-10"`.
6. Rollback plan: if the migration fails midway, the migrate-secrets command rolls back by deleting imported rows whose `SECRET.IMPORTED` event has a matching `failed` sibling. Idempotent retry recovers. The runtime `IRuntimeSecretResolver` still has env-var fallback, so a failed migration does not bring the process down.
7. Integration test with Testcontainers: seed a Postgres with the pre-migration schema (including `cranl_database_url_encrypted` columns + sample tenants + `changeme` role password); run the command; assert all expected rows exist in `platform_secrets` / `tenant_secrets`; assert `changeme` has been rotated; assert the app can connect with the new password.
8. Documentation: `docs/runbooks/migrate-secrets.md` with step-by-step — backup DB → deploy new code behind env-var fallback → run command → verify cabinet rows → rotate again after 48h to confirm the cabinet path is live → remove env-var fallback in the next release (Story 29-10).
9. Each imported secret's initial `LastRotatedAt` is set to **now** (not the import time of the env variable, which may be months ago) because the import is the first time the value entered the cabinet's audit boundary — subsequent rotation overdue calculations are meaningful from this point forward.
10. For tenant-scoped Cranl connection URLs, migration also writes a `SECRET.IMPORTED.TENANT_URL` event into the tenant's `domain_events` so the tenant admin sees the import in their audit feed.

## Technical Context

### Order of operations

The runtime and the cabinet coexist for one release:

1. Deploy Story 29-1..29-7 code with `IRuntimeSecretResolver` defaulting to env vars.
2. Run the migrate-secrets command.
3. Cabinet now has rows; `changeme` has been rotated; runtime has
   refreshed cached secrets.
4. One release later, Story 29-10 deletes the env-var fallback and
   the old stopgap helpers.

### Why rotate `changeme` during import

A `changeme` literal is a known-public value. The moment the cabinet
imports it and the runtime starts reading from the cabinet, any read
returns that literal — still bad. Rotating during import + caching the
new value means the process never exposes `changeme` outside the
migration's own short-lived flow.

### `IRuntimeSecretResolver` sketch

```csharp
interface IRuntimeSecretResolver {
  Task<string> GetAsync(string secretName, CancellationToken ct);
  Task RefreshAsync(string secretName, CancellationToken ct);
  event EventHandler<SecretRotatedEvent> OnRotated;
}
```

The resolver subscribes to `SECRET.ROTATE.ACTIVATED` events and
refreshes its cache when any of its registered names rotate. Connection
pool drain (29-7) is wired to this event so the pool resets.

## Estimated hours

20 — migrate command + runtime resolver + env-var fallback + rollback
+ runbook + integration tests + tenant-events bridge.

## Files to touch

- `apps/tamma-elsa/src/Tamma.Api/Commands/MigrateSecretsCommand.cs` (new)
- `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/RuntimeSecretResolver.cs` (new)
- `docs/runbooks/migrate-secrets.md` (new)
- Relevant startup wiring in `Program.cs`

## References

- Story 29-7 DB rotation
- Story 29-8 Cranl rotation
- Review findings 4, 15, 16
- Epic 28 decision memory: `project_epic28_kek_decision.md`
