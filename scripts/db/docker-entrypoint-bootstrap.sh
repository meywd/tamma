#!/bin/bash
# Story 28-12 — docker-entrypoint hook that bootstraps Tamma's
# three-tier role separation on a fresh Postgres cluster. Mounts at
# /docker-entrypoint-initdb.d/ in the official postgres image.
#
# Idempotent: re-running on an already-bootstrapped cluster is a no-op
# because postgres-roles.sql guards every CREATE ROLE on a pg_roles
# probe.
#
# Required environment:
#   POSTGRES_DB                        — name of the CP database
#   POSTGRES_USER                      — superuser (defaults to postgres)
#   POSTGRES_PASSWORD                  — superuser password (consumed via PGPASSWORD)
#   TAMMA_ADMIN_PASSWORD               — password for tamma_admin
#   TAMMA_PROVISIONER_PASSWORD         — password for tamma_provisioner
#   TAMMA_APP_PASSWORD                 — password for tamma_app
#
# Operator note: the postgres init scripts run as the cluster's
# initial superuser ($POSTGRES_USER) before any other connections are
# accepted, so this is the safest place to grant SUPERUSER to
# tamma_admin without leaving a window where the role exists without
# its grants.
#
# R2-PF-S2 fix (Story 28-R2 post-fix): role-password values used to be
# threaded into psql via `--set name=value` argv elements, which made
# them visible in `/proc/<pid>/cmdline` for the duration of the psql
# invocation. The current shape:
#
#   1. Writes a chmod-0600 temporary "preamble" SQL file containing
#      the three `\set` directives that bind the role passwords to
#      psql variables. The file lives only on the local filesystem,
#      readable only by the invoking user (umask + chmod 0600), and
#      is unlinked via a trap on EXIT/INT/TERM.
#   2. Concatenates the preamble + `postgres-roles.sql` and pipes the
#      combined SQL to psql via stdin (`--file=-`). psql substitutes
#      :'admin_password' inline as a properly-quoted SQL literal at
#      parse time.
#   3. PGPASSWORD env var carries the superuser password to libpq —
#      libpq scrubs it from any visible state. The cluster never logs
#      the role passwords because every CREATE ROLE in the SQL runs
#      inside `BEGIN; SET LOCAL log_statement = 'none'; ... COMMIT;`.
#
# Net result: the role passwords appear in NO argv (the only argv
# elements left are the dbname, username, the literal `--file=-`, and
# the harmless `--set=cp_database=…`). Verified by running
# `ps auxe | grep psql` against this script during a fresh bootstrap.
#
# See `.dev/runbooks/postgres-bootstrap.md` for the full env-var
# threading model and operator-side guidance.

set -euo pipefail

: "${POSTGRES_DB:?POSTGRES_DB is required}"
: "${POSTGRES_USER:?POSTGRES_USER is required (cluster superuser)}"
: "${TAMMA_ADMIN_PASSWORD:?TAMMA_ADMIN_PASSWORD is required}"
: "${TAMMA_PROVISIONER_PASSWORD:?TAMMA_PROVISIONER_PASSWORD is required}"
: "${TAMMA_APP_PASSWORD:?TAMMA_APP_PASSWORD is required}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "[tamma-bootstrap] running postgres-roles.sql against database=$POSTGRES_DB"

# Use PGPASSWORD env-var auth so the superuser password never appears
# in argv. Inside the postgres docker-entrypoint, $POSTGRES_PASSWORD is
# set; outside of that context the operator either exports it or uses
# a pgpass file. We pick PGPASSWORD because it survives the single-
# invocation pattern we want here.
#
# When the script is invoked by the official postgres image's
# /docker-entrypoint-initdb.d/ pipeline, libpq is already pointing at
# the local socket as the superuser without a password. The fallback
# below preserves that path.
if [ -n "${POSTGRES_PASSWORD:-}" ]; then
    export PGPASSWORD="$POSTGRES_PASSWORD"
fi

# Belt-and-braces: tighten umask before mktemp so the preamble file
# cannot be created world-readable on platforms where mktemp respects
# the process umask.
umask 0077

# Portable mktemp invocation. GNU mktemp accepts `-t pattern.X.sql`
# with a suffix; busybox mktemp (alpine) does not — it requires the
# template to be a complete path with at least 6 trailing X's. The
# explicit `${TMPDIR:-/tmp}/…` template works on both. We then `mv` to
# add the .sql suffix so editors / forensic tools see the right type.
PREAMBLE_FILE="$(mktemp "${TMPDIR:-/tmp}/tamma-bootstrap-preamble.XXXXXXXX")"
mv "$PREAMBLE_FILE" "$PREAMBLE_FILE.sql"
PREAMBLE_FILE="$PREAMBLE_FILE.sql"
chmod 0600 "$PREAMBLE_FILE"

# Trap so the preamble file is unlinked even if psql fails or the
# script is killed mid-run. We deliberately do NOT trap on every
# signal individually — `trap … EXIT INT TERM` is the canonical
# minimum coverage.
trap 'rm -f "$PREAMBLE_FILE"' EXIT INT TERM

# Stream the three `\set` directives + the role-bootstrap SQL into a
# single pipe to psql. Quoting note: we use a heredoc with NO leading
# variable expansion delimiter (so $VAR expands but \-escapes are
# preserved) and quote the values via psql's own `'...'` form — psql
# treats anything inside `\set name 'value'` as a literal token. The
# values cannot themselves contain raw apostrophes; the runbook
# documents this constraint.
{
    cat <<EOF
\set admin_password '$TAMMA_ADMIN_PASSWORD'
\set provisioner_password '$TAMMA_PROVISIONER_PASSWORD'
\set app_password '$TAMMA_APP_PASSWORD'
EOF
    cat "$SCRIPT_DIR/postgres-roles.sql"
} >"$PREAMBLE_FILE"

# Sanity-check the file's mode — if a prior /tmp policy reset the
# permissions, fail closed rather than ship plaintext on a 0644 file.
PERMS="$(stat -c '%a' "$PREAMBLE_FILE" 2>/dev/null || stat -f '%Lp' "$PREAMBLE_FILE")"
if [ "$PERMS" != "600" ]; then
    echo "[tamma-bootstrap] FATAL: preamble file mode is $PERMS (expected 600)" >&2
    exit 1
fi

# Pipe via stdin (`--file=-`). The psql argv only contains the dbname,
# username, ON_ERROR_STOP toggle, the cp_database variable (NOT a
# secret), and the `--file=-` placeholder. None of the role passwords
# appear in argv — they live only in the preamble file (chmod 0600,
# invoking user only) and as `\set` variables inside psql's own state.
psql --dbname="$POSTGRES_DB" --username="$POSTGRES_USER" \
    --set=ON_ERROR_STOP=on \
    --set="cp_database=$POSTGRES_DB" \
    --file=- < "$PREAMBLE_FILE"

# Belt-and-braces: zero the preamble file before unlink (the trap
# above unlinks unconditionally; this overwrite reduces the window
# during which a forensic disk reader could recover the bytes).
shred -uz "$PREAMBLE_FILE" 2>/dev/null || rm -f "$PREAMBLE_FILE"
trap - EXIT INT TERM

# Scrub the env after we're done so anything that inherits this
# process's environment (e.g. a downstream init hook) does not see the
# plaintext.
unset PGPASSWORD
unset TAMMA_ADMIN_PASSWORD
unset TAMMA_PROVISIONER_PASSWORD
unset TAMMA_APP_PASSWORD

echo "[tamma-bootstrap] complete. Roles: tamma_admin, tamma_provisioner, tamma_app."
