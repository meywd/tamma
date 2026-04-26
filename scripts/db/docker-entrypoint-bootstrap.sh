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
#   TAMMA_ADMIN_PASSWORD               — password for tamma_admin
#   TAMMA_PROVISIONER_PASSWORD         — password for tamma_provisioner
#   TAMMA_APP_PASSWORD                 — password for tamma_app
#
# Operator note: the postgres init scripts run as the cluster's
# initial superuser ($POSTGRES_USER) before any other connections are
# accepted, so this is the safest place to grant SUPERUSER to
# tamma_admin without leaving a window where the role exists without
# its grants.

set -euo pipefail

: "${POSTGRES_DB:?POSTGRES_DB is required}"
: "${POSTGRES_USER:?POSTGRES_USER is required (cluster superuser)}"
: "${TAMMA_ADMIN_PASSWORD:?TAMMA_ADMIN_PASSWORD is required}"
: "${TAMMA_PROVISIONER_PASSWORD:?TAMMA_PROVISIONER_PASSWORD is required}"
: "${TAMMA_APP_PASSWORD:?TAMMA_APP_PASSWORD is required}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "[tamma-bootstrap] running postgres-roles.sql against database=$POSTGRES_DB"

# psql -v sets server-side variables that postgres-roles.sql reads via
# current_setting(). The settings are session-only — they don't
# persist on the server.
psql --dbname="$POSTGRES_DB" --username="$POSTGRES_USER" \
    --set=ON_ERROR_STOP=on \
    --set="cp_database=$POSTGRES_DB" \
    --command="SELECT set_config('tamma.admin_password', '$TAMMA_ADMIN_PASSWORD', false)" \
    --command="SELECT set_config('tamma.provisioner_password', '$TAMMA_PROVISIONER_PASSWORD', false)" \
    --command="SELECT set_config('tamma.app_password', '$TAMMA_APP_PASSWORD', false)" \
    --file="$SCRIPT_DIR/postgres-roles.sql"

echo "[tamma-bootstrap] complete. Roles: tamma_admin, tamma_provisioner, tamma_app."
