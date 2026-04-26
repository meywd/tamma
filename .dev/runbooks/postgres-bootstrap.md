# Postgres Role Bootstrap Runbook

**Story**: 28-12 (R2 fix H1)
**Audience**: platform operators
**Severity**: high — these roles own the entire control-plane DB
**Estimated downtime**: zero (one-shot init on a fresh cluster)
**Last reviewed**: 2026-04-26

---

## What this bootstraps

`scripts/db/postgres-roles.sql` creates Tamma's three-tier privilege
separation on a fresh Postgres cluster. The roles are:

| Role | Privileges | Used by |
|---|---|---|
| `tamma_admin` | LOGIN SUPERUSER | Operator-only, emergency cluster work + the docker-entrypoint init pipeline |
| `tamma_provisioner` | LOGIN CREATEDB CREATEROLE NOSUPERUSER | `CreateTenantWorkflow` + `ITenantAdminConnection` (provisioning new per-tenant DBs) |
| `tamma_app` | LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE | `Tamma.Api` runtime (control-plane reads + writes) |

Per-tenant roles (`tamma_t_<hex>`) are minted at provision time with
privileges scoped to their own database — they are NOT created here.

---

## How passwords flow

R2-H1 hardening: passwords never appear on a command line, in
`pg_stat_activity`, in `/proc/<pid>/cmdline`, or in the server log.

The bootstrap script uses three layers of defence:

1. **`PGPASSWORD` env var** — the superuser password reaches `libpq`
   via the `PGPASSWORD` environment variable, which `libpq` scrubs from
   any visible state. The superuser password is NEVER passed as
   `--password=…` or `-W`.
2. **`psql -v` variables** — the three role passwords are threaded into
   `psql` as `-v admin_password=…` (etc). Inside the SQL, `psql`
   substitutes `:'admin_password'` as a properly-escaped SQL literal.
   Server-side `pg_stat_activity` sees the SUBSTITUTED text (the
   literal `'<password>'`), but only for the brief duration of the
   `CREATE ROLE` execution itself, and not in any per-statement
   `SELECT set_config(...)` preamble (the previous shape that round-1
   security flagged).
3. **`SET LOCAL log_statement = 'none'`** — every CREATE ROLE in
   `postgres-roles.sql` runs inside a `BEGIN; SET LOCAL log_statement
   = 'none'; SET LOCAL log_min_duration_statement = -1; CREATE ROLE …;
   COMMIT;` block. Even if the cluster runs `log_statement=ddl` or
   `log_statement=all`, the `CREATE ROLE … PASSWORD …` line is not
   logged. `SET LOCAL` resets at end of transaction.

A previous shape used `DO $$ … EXECUTE format(... %L, current_setting(...)) … $$`
with `psql --command="SELECT set_config('tamma.admin_password', …)"`
preambles. That shape leaked the plaintext via `pg_stat_activity` for
the session that ran the `set_config` call. The current shape uses
`\if` + `\gset` + plain CREATE ROLE statements at the top level, where
psql variable substitution works (psql does NOT substitute inside
dollar-quoted strings).

In addition, the script uses `WITH ENCRYPTED PASSWORD` so the storage
form in `pg_authid.rolpassword` is `scram-sha-256` (cluster default
since PG14). The plaintext is hashed at write time before reaching
`pg_authid`.

---

## Required environment variables

The docker-entrypoint hook (`docker-entrypoint-bootstrap.sh`) reads:

```
POSTGRES_DB                        — name of the CP database
POSTGRES_USER                      — cluster superuser (default: postgres)
POSTGRES_PASSWORD                  — cluster superuser password (consumed via PGPASSWORD)
TAMMA_ADMIN_PASSWORD               — password for tamma_admin
TAMMA_PROVISIONER_PASSWORD         — password for tamma_provisioner
TAMMA_APP_PASSWORD                 — password for tamma_app
```

The bootstrap script `unset`s `PGPASSWORD` + the three `TAMMA_*_PASSWORD`
variables before exiting so any downstream init hook in the same
docker-entrypoint pipeline does not see them.

---

## Direct operator invocation

When NOT running under the postgres image's init pipeline (e.g.
applying the bootstrap to an existing cluster):

```bash
# Stage the env vars in your shell — these MUST come from your secrets
# manager, never typed inline (shell history retention + screen
# scrollback + colleague-shoulder-surf).
export POSTGRES_DB=tamma_control
export POSTGRES_USER=postgres
export POSTGRES_PASSWORD="$(my-secrets-cli get pg-superuser)"
export TAMMA_ADMIN_PASSWORD="$(my-secrets-cli get tamma-admin)"
export TAMMA_PROVISIONER_PASSWORD="$(my-secrets-cli get tamma-provisioner)"
export TAMMA_APP_PASSWORD="$(my-secrets-cli get tamma-app)"

# Run the bootstrap.
bash scripts/db/docker-entrypoint-bootstrap.sh

# Scrub the env. The script unsets the TAMMA_*_PASSWORD vars before
# exiting, but POSTGRES_PASSWORD persists — clear it explicitly.
unset POSTGRES_PASSWORD
```

Direct `psql` invocation (e.g. for ad-hoc role rotation) follows the
same shape:

```bash
PGPASSWORD="$(my-secrets-cli get pg-superuser)" psql \
    --dbname=tamma_control \
    --username=postgres \
    --set=ON_ERROR_STOP=on \
    --set="cp_database=tamma_control" \
    --set="admin_password=$(my-secrets-cli get tamma-admin)" \
    --set="provisioner_password=$(my-secrets-cli get tamma-provisioner)" \
    --set="app_password=$(my-secrets-cli get tamma-app)" \
    --file=scripts/db/postgres-roles.sql
```

`PGPASSWORD` should be cleared from the operator shell as soon as the
`psql` call returns; consider using a one-shot wrapper like
`pgpassfile`-based auth if your environment supports it.

---

## Verification

After bootstrap, confirm the three roles exist and have the expected
attributes:

```sql
SELECT rolname,
       rolsuper       AS is_superuser,
       rolcreatedb    AS can_create_db,
       rolcreaterole  AS can_create_role,
       rolcanlogin    AS can_login
FROM pg_roles
WHERE rolname IN ('tamma_admin', 'tamma_provisioner', 'tamma_app')
ORDER BY rolname;
```

Expected (one row per role):

| rolname | is_superuser | can_create_db | can_create_role | can_login |
|---|---|---|---|---|
| tamma_admin | t | t | t | t |
| tamma_app | f | f | f | t |
| tamma_provisioner | f | t | t | t |

Confirm the password storage is hashed (not plaintext):

```sql
SELECT rolname,
       substring(rolpassword for 14) AS prefix,
       octet_length(rolpassword)     AS hash_len
FROM pg_authid
WHERE rolname IN ('tamma_admin', 'tamma_provisioner', 'tamma_app')
ORDER BY rolname;
```

The `prefix` should start with `SCRAM-SHA-256$` for every role.

Confirm `pg_stat_statements` does NOT contain the literal passwords (run
this WHILE the bootstrap is happening from another session — the
plaintext should not be there):

```sql
SELECT query
FROM pg_stat_statements
WHERE query ILIKE '%CREATE ROLE%'
   OR query ILIKE '%PASSWORD%';
```

Every `query` row should show the parameterized form
(`CREATE ROLE … ENCRYPTED PASSWORD <repeated>`) — `pg_stat_statements`
canonicalises literal values to `$1` (etc) automatically, so the actual
plaintext should never appear.

---

## Failure recovery

### "Role already exists" on re-run

Expected. The `IF NOT EXISTS (SELECT 1 FROM pg_roles …)` guard makes the
script idempotent. To force a password rotation, run a separate
`ALTER ROLE … WITH ENCRYPTED PASSWORD …` statement (NOT a re-run of
`postgres-roles.sql`).

### "permission denied to create role"

The connecting user (`POSTGRES_USER`) is not a SUPERUSER. The
docker-entrypoint pipeline runs as `POSTGRES_USER=postgres` against
the cluster's initial superuser — that path always works. Outside the
pipeline, the operator must connect as a SUPERUSER.

### "password authentication failed for user"

Either `PGPASSWORD` was not exported, or the wrong superuser is being
used. Check `echo "$PGPASSWORD" | wc -c` (should print the length of
the password + 1 for the newline). Never `echo "$PGPASSWORD"` directly.

---

## Compensating controls

- **Logging**: every DO block silences DDL logging via `SET LOCAL
  log_statement = 'none'`. Even `log_statement=ddl|all` at the cluster
  level does not capture the password lines.
- **Storage**: `WITH ENCRYPTED PASSWORD` forces `scram-sha-256` hashing
  in `pg_authid.rolpassword`. Plaintext never lands at rest.
- **Argv**: `psql -v` substitutes inside the SQL, not on the command
  line. The literal password is not visible in `/proc/<pid>/cmdline`.
- **Env**: the script `unset`s the four `*_PASSWORD` env vars before
  exiting so downstream init hooks in the same docker-entrypoint
  pipeline cannot read them.
- **Scrub**: `pg_stat_statements` canonicalises literals to `$1` etc;
  any post-mortem inspection of the table sees the parameterized
  shape.

---

## Open questions / future enhancements

- **OpenBao integration** (Story 28-13): once the secret store is in
  place, the bootstrap should fetch passwords directly from the vault
  rather than relying on env vars. Tracked under Story 28-13.
- **Cert-based auth**: long-term, the application roles could
  authenticate via client certs (no password storage at all). Punt
  until OpenBao lands.
- **Per-tenant role bootstrap**: per-tenant roles (`tamma_t_<hex>`)
  follow the same pattern but are minted at provision time, not in
  this script. The same `psql -v` + `SET LOCAL log_statement = 'none'`
  shape should be applied wherever those mints happen — see
  `PostgresRoleRotationHandler` for one such site.
