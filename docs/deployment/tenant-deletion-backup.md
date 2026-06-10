# Tenant Pre-Drop Backup — Operator Guide

**Story**: 28-5 (create/delete tenant workflows, AC4)
**Audience**: platform operators / oncall engineers
**Last updated**: 2026-06-05

When a tenant is deleted, `DeleteTenantWorkflow` runs
`DROP DATABASE ... WITH (FORCE)` — an O(1), irreversible teardown. The
optional pre-drop backup captures a `pg_dump` snapshot of the tenant
database *before* the drop, so a tenant deleted by mistake (or one whose
SLA promises soft-delete recovery) can be restored with `pg_restore`.

The feature is **OFF by default**. The shared-infrastructure topology
relies on cluster-level Postgres backups; per-tenant dumps are an opt-in
layer for deployments that need them.

---

## Configuration (`Backup` section)

```json
"Backup": {
  "DeletionBackup": false,
  "Directory": "/var/backups/tamma",
  "PgDumpPath": "pg_dump",
  "TimeoutSeconds": 1800
}
```

| Key | Default | Meaning |
|---|---|---|
| `Backup:DeletionBackup` | `false` | Master switch. When `false` the backup step is a pure no-op. |
| `Backup:Directory` | `/var/backups/tamma` | Destination directory for dump files. Created if missing. **Must be a durable, mounted volume in production** — otherwise the dump lands on the container's ephemeral filesystem. |
| `Backup:PgDumpPath` | `pg_dump` | Path to the `pg_dump` binary (PATH-resolved by default). |
| `Backup:TimeoutSeconds` | `1800` | Hard timeout for the dump (30 min). |

Env-var form (compose / k8s): `Backup__DeletionBackup`,
`Backup__Directory`, `Backup__PgDumpPath`, `Backup__TimeoutSeconds`.

The section is bound on **both** the API host and the elsa-server host,
but the delete workflow runs on **elsa-server**, so that is the host that
must have `pg_dump` and the mounted volume.

---

## Prerequisites to enable

1. **`pg_dump` in the image.** The `elsa-server` runtime image
   (`mcr.microsoft.com/dotnet/aspnet:8.0`) ships only `curl`. Add
   `postgresql-client` to `src/Tamma.ElsaServer/Dockerfile` (a commented
   example is in that file) so `pg_dump` resolves on PATH.
2. **A durable backup volume.** Uncomment the `tenant_backups` volume +
   mount in `docker-compose.prod.yml` (or `./data/backups` in
   `docker-compose.yml` for dev).
3. **Flip the switch.** Uncomment the `Backup__*` env block in the compose
   file and set `BACKUP_DELETION_BACKUP=true` in `.env`.

---

## Behaviour & guarantees

- Runs **between** pool eviction and `DROP DATABASE` in
  `DeleteTenantWorkflow` — the snapshot always precedes the destructive
  step. If the dump fails (non-zero exit / timeout), the workflow
  **aborts before the drop**, so a tenant is never destroyed without its
  backup.
- **Custom format** (`pg_dump -Fc`) — compressed, restore with
  `pg_restore`. Files land at `<Directory>/<tenant_db>_<UTC-timestamp>.dump`.
- **Secret hygiene**: the admin password is passed via the `PGPASSWORD`
  environment variable, never on the command line (argv is world-readable
  via `/proc/<pid>/cmdline`). `pg_dump` stderr is logged locally but is
  **not** persisted into the `TENANT.LIFECYCLE.BACKUP_DATABASE` audit
  event (it can echo connection details).
- **Idempotent**: if the database is already gone (a prior run dropped it)
  the step logs and exits cleanly.

## Audit trail

The step emits `TENANT.LIFECYCLE.BACKUP_DATABASE` (`STEP_STARTED` /
`STEP_COMPLETED` / `STEP_FAILED`) to `platform_events` with `step` +
`attempt` tags, like every other tenant-lifecycle step.

## Restore (sketch)

```bash
createdb -h <host> -U <admin> tamma_tenant_<hex>_restore
pg_restore -h <host> -U <admin> -d tamma_tenant_<hex>_restore \
  /var/backups/tamma/tamma_tenant_<hex>_<timestamp>.dump
```

Then re-point the tenant's `cranl_database_url_encrypted` / routing entry
at the restored database (manual control-plane operation — there is no
automated un-delete yet).
