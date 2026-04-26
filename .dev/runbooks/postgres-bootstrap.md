# Postgres Role Bootstrap Runbook

**Story**: 28-12 (R2 fix H1, R2 post-fix PF-S2)
**Audience**: platform operators
**Severity**: high — these roles own the entire control-plane DB
**Estimated downtime**: zero (one-shot init on a fresh cluster)
**Last reviewed**: 2026-04-26 (post-fix update)

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

## How passwords flow (R2 post-fix PF-S2)

Hardening intent: passwords never appear in `pg_stat_activity`, in
`/proc/<pid>/cmdline`, in shell history, or in the server log.

### What the previous shape (R2-H1) did and why PF-S2 changed it

R2-H1 originally threaded the role passwords into `psql` via
`--set name=value` argv elements. The runbook claimed those values
were "NEVER visible in `/proc/<pid>/cmdline`" — that claim was
**false**. Argv elements ARE visible in `/proc/<pid>/cmdline` for
the duration of the `psql` invocation, and a colocated process with
read access to the bootstrap container's `/proc` could capture the
plaintext during the (short) bootstrap window.

PF-S2 closes that vector. The current shape uses a chmod-0600
temporary preamble file + stdin pipe. The role passwords appear
in NO `psql` argv element. The only argv elements that remain are
the dbname, the username, the literal `--file=-`, the
`ON_ERROR_STOP=on` toggle, and the `cp_database=$POSTGRES_DB`
variable (not a secret).

### The four layers of defence

1. **`PGPASSWORD` env var** — the superuser password reaches `libpq`
   via the `PGPASSWORD` environment variable, which `libpq` scrubs
   from any visible state. The superuser password is NEVER passed as
   `--password=…` or `-W`.
2. **chmod-0600 preamble file + stdin pipe** — the three role
   passwords are written into a `mktemp`-generated file (umask 0077,
   chmod 0600 explicitly applied, `stat`-verified before use). The
   file contains three `\set name 'value'` directives. The script
   concatenates the preamble + `postgres-roles.sql` and pipes the
   combined SQL into psql via stdin (`--file=-`). After psql exits,
   the script `shred -uz`s the preamble file (with `rm -f` fallback)
   AND a `trap` on EXIT/INT/TERM unconditionally unlinks it. The
   file lives only on the local filesystem, owned by the invoking
   user, mode 0600.
3. **Server-side `SET LOCAL log_statement = 'none'`** — every CREATE
   ROLE in `postgres-roles.sql` runs inside a `BEGIN; SET LOCAL
   log_statement = 'none'; SET LOCAL log_min_duration_statement = -1;
   CREATE ROLE …; COMMIT;` block. Even if the cluster runs
   `log_statement=ddl` or `log_statement=all`, the `CREATE ROLE …
   PASSWORD …` line is not logged. `SET LOCAL` resets at end of
   transaction.
4. **`WITH ENCRYPTED PASSWORD`** — the storage form in
   `pg_authid.rolpassword` is `scram-sha-256` (cluster default since
   PG14). The plaintext is hashed at write time before reaching
   `pg_authid`.

### Threat model — what this prevents and what remains

**Prevented**:
- Plaintext leak via `/proc/<pid>/cmdline` (psql argv).
- Plaintext leak via the server-side log (`log_statement=ddl|all`).
- Plaintext leak via `pg_stat_activity` (the previous
  `--command="SELECT set_config(…)"` pattern; not used here).
- Plaintext at rest in `pg_authid` (hashed via `WITH ENCRYPTED PASSWORD`).
- Plaintext leak via shell history (operator export, never typed inline).
- Plaintext leak to downstream init hooks (env vars `unset` after use).

**Residual exposure** (acceptable for a fresh-cluster init script):
- The chmod-0600 preamble file exists on the local filesystem for
  the duration of the `psql` invocation. Same OS-process-owner as
  the bootstrap container. Trapped + `shred`d on exit.
- The SQL bytes themselves transit the bash pipe to psql's stdin —
  visible in `/proc/<psql-pid>/fd/0` for the parse window. Same
  process-owner constraint applies.
- The `pg_stat_activity` view sees the substituted text (the
  literal `'<password>'`) for the brief duration of the CREATE
  ROLE execution itself. The `SET LOCAL log_statement = 'none'`
  prevents server-log persistence; only an active `SELECT * FROM
  pg_stat_activity` query racing the bootstrap can capture it.

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

**Quoting constraint**: the `\set name '<value>'` directive uses
psql's single-quote literal form. The role passwords MUST NOT
contain a raw apostrophe (`'`). Generate them with a base64 / hex
character set or escape carefully. The docker-entrypoint pipeline
verifies the script's exit code; an unbalanced quote will fail the
SQL parse and surface immediately.

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

**Direct `psql` (NOT recommended — use the script)**: if you absolutely
must invoke psql by hand (e.g. for an out-of-band rotation), follow
the same chmod-0600 preamble pattern. **Do NOT** use
`psql --set name=value` for secrets — that regresses the PF-S2 fix.

```bash
# Compose a chmod-0600 preamble file with the three \set directives
preamble="$(mktemp -t tamma-preamble.XXXXXXXX.sql)"
chmod 0600 "$preamble"
trap 'shred -uz "$preamble" 2>/dev/null || rm -f "$preamble"' EXIT

cat <<EOF >"$preamble"
\set admin_password '$(my-secrets-cli get tamma-admin)'
\set provisioner_password '$(my-secrets-cli get tamma-provisioner)'
\set app_password '$(my-secrets-cli get tamma-app)'
\set cp_database 'tamma_control'
EOF

# Pipe the preamble + roles.sql via stdin.
PGPASSWORD="$(my-secrets-cli get pg-superuser)" \
  cat "$preamble" scripts/db/postgres-roles.sql \
  | psql --dbname=tamma_control --username=postgres \
         --set=ON_ERROR_STOP=on --file=-
```

`PGPASSWORD` should be cleared from the operator shell as soon as the
`psql` call returns; consider using a one-shot wrapper like
`pgpassfile`-based auth if your environment supports it.

---

## Verification

### 1. Verify roles exist with expected attributes

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

### 2. Verify password storage is hashed (not plaintext)

```sql
SELECT rolname,
       substring(rolpassword for 14) AS prefix,
       octet_length(rolpassword)     AS hash_len
FROM pg_authid
WHERE rolname IN ('tamma_admin', 'tamma_provisioner', 'tamma_app')
ORDER BY rolname;
```

The `prefix` should start with `SCRAM-SHA-256$` for every role.

### 3. PF-S2 verification — argv leak proof

The point of PF-S2 is that role passwords never appear in psql's
argv. To verify on a real bootstrap run:

```bash
# Terminal A — start the bootstrap.
TAMMA_ADMIN_PASSWORD='canary-admin-XYZ' \
TAMMA_PROVISIONER_PASSWORD='canary-prov-XYZ' \
TAMMA_APP_PASSWORD='canary-app-XYZ' \
POSTGRES_DB=tamma_control \
POSTGRES_USER=postgres \
POSTGRES_PASSWORD="$(my-secrets-cli get pg-superuser)" \
bash scripts/db/docker-entrypoint-bootstrap.sh

# Terminal B — WHILE the bootstrap is running, watch argv:
while sleep 0.05; do
  ps -e -o pid,cmd | grep -E '[p]sql' || true
done

# Terminal B should show ONLY the safe argv (no canary-XYZ values):
#   12345 psql --dbname=tamma_control --username=postgres --set=ON_ERROR_STOP=on --set=cp_database=tamma_control --file=-
#
# It must NOT show:
#   12345 psql ... --set=admin_password=canary-admin-XYZ ...
```

Likewise:

```bash
# pg_stat_statements should canonicalise literal values to $1 etc.
SELECT query
FROM pg_stat_statements
WHERE query ILIKE '%CREATE ROLE%'
   OR query ILIKE '%PASSWORD%';
```

Every `query` row should show the parameterized form — the actual
plaintext should never appear there.

---

## Failure recovery

### "Role already exists" on re-run

Expected. The `IF NOT EXISTS (SELECT 1 FROM pg_roles …)` guard makes the
script idempotent. To force a password rotation, run a separate
`ALTER ROLE … WITH ENCRYPTED PASSWORD …` statement (NOT a re-run of
`postgres-roles.sql`), following the same chmod-0600 preamble +
stdin pipe pattern.

### "permission denied to create role"

The connecting user (`POSTGRES_USER`) is not a SUPERUSER. The
docker-entrypoint pipeline runs as `POSTGRES_USER=postgres` against
the cluster's initial superuser — that path always works. Outside the
pipeline, the operator must connect as a SUPERUSER.

### "password authentication failed for user"

Either `PGPASSWORD` was not exported, or the wrong superuser is being
used. Check `echo "$PGPASSWORD" | wc -c` (should print the length of
the password + 1 for the newline). Never `echo "$PGPASSWORD"` directly.

### "preamble file mode is XYZ (expected 600)"

The script's belt-and-braces `stat`-verification refused to proceed
because `mktemp` produced a non-0600 file. Most likely cause: a
broken `umask` in the calling shell, or a non-standard `/tmp`
mount with permission overrides. Fix the umask
(`umask 0077; bash scripts/db/docker-entrypoint-bootstrap.sh`) or
point `TMPDIR` at a directory you control.

---

## Compensating controls

- **Argv**: chmod-0600 preamble + stdin pipe. Role passwords never
  appear in `/proc/<pid>/cmdline`. The previous `psql --set` shape
  was the leak vector PF-S2 closed.
- **Logging**: every DO block silences DDL logging via `SET LOCAL
  log_statement = 'none'`. Even `log_statement=ddl|all` at the cluster
  level does not capture the password lines.
- **Storage**: `WITH ENCRYPTED PASSWORD` forces `scram-sha-256` hashing
  in `pg_authid.rolpassword`. Plaintext never lands at rest.
- **Filesystem**: the preamble file is `mktemp` + `chmod 0600` +
  `stat`-verified, then `shred -uz` on exit (with `rm -f` fallback).
  A `trap` on EXIT/INT/TERM unconditionally unlinks it.
- **Env**: the script `unset`s the four `*_PASSWORD` env vars before
  exiting so downstream init hooks in the same docker-entrypoint
  pipeline cannot read them.
- **Statement-level scrubbing**: `pg_stat_statements` canonicalises
  literals to `$1` etc; any post-mortem inspection of the table sees
  the parameterized shape, not the plaintext.

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
  this script. The same chmod-0600 preamble + stdin-pipe + `SET
  LOCAL log_statement = 'none'` shape should be applied wherever
  those mints happen — see `PostgresRoleRotationHandler` for one
  such site.
