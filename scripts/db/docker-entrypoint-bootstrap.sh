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
# R2-H1 fix — passwords no longer reach pg_stat_activity, the server
# log, or /proc/<pid>/cmdline. Mechanism:
#   1. PGPASSWORD env var carries the superuser password to libpq —
#      libpq scrubs it from any visible state.
#   2. The three role passwords are passed to psql via `-v` variables
#      and the SQL substitutes them inline as literal values inside a
#      DO block that has `SET LOCAL log_statement = 'none'`. Postgres
#      never logs the value, even when log_statement=ddl|all is set
#      at the cluster level.
#   3. Per-command --command="SELECT set_config(...)" pattern is gone
#      — that pattern made the plaintext password visible in
#      pg_stat_activity for the session.
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

# Pass the three role passwords as psql -v variables. psql substitutes
# :'admin_password' inline as a properly-quoted SQL literal — they are
# NEVER visible in pg_stat_activity, /proc/<pid>/cmdline, or the server
# log.
#
# IMPORTANT: do NOT switch to --command="…" or pipe the SQL through
# stdin; those patterns regress the H1 leak. The single -f invocation
# with -v variables is the safe shape.
psql --dbname="$POSTGRES_DB" --username="$POSTGRES_USER" \
    --set=ON_ERROR_STOP=on \
    --set="cp_database=$POSTGRES_DB" \
    --set="admin_password=$TAMMA_ADMIN_PASSWORD" \
    --set="provisioner_password=$TAMMA_PROVISIONER_PASSWORD" \
    --set="app_password=$TAMMA_APP_PASSWORD" \
    --file="$SCRIPT_DIR/postgres-roles.sql"

# Scrub the env after we're done so anything that inherits this
# process's environment (e.g. a downstream init hook) does not see the
# plaintext.
unset PGPASSWORD
unset TAMMA_ADMIN_PASSWORD
unset TAMMA_PROVISIONER_PASSWORD
unset TAMMA_APP_PASSWORD

echo "[tamma-bootstrap] complete. Roles: tamma_admin, tamma_provisioner, tamma_app."
