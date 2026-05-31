#!/usr/bin/env bash
# Story 28-1 AC3 — wipe-and-replay for Tamma's shared databases.
#
# Drops + recreates the shared control / global-Elsa databases, then
# invokes bootstrap-shared-dbs.sh to re-ensure them. Intended for CI /
# local-dev resets and the integration-test setup — NEVER for production.
#
# ── Topology reconciliation (READ bootstrap-shared-dbs.sh header first) ──
# Current deployment is SHARED-INFRASTRUCTURE mode: control-plane + tenant
# + Elsa data all live in the ONE central `tamma` DB. So "drop the shared
# DBs" today means dropping `tamma` (and, when Elsa is split onto its own
# database, that DB too). Both default to `tamma` and are parameterised so
# this script extends unchanged to the future tamma_control /
# tamma_global_elsa split.
#
# ── DOES NOT touch per-tenant databases ─────────────────────────────────
# Per-tenant DBs (`tamma_tenant_<guid>` / `..._elsa`) are workflow-
# provisioned and live OUTSIDE this script's remit. There are none in
# shared mode, but as a hard guard this script REFUSES to drop any
# database whose name matches `tamma_tenant_*` — even if an operator
# mis-points TAMMA_CONTROL_DB / TAMMA_GLOBAL_ELSA_DB at one.
#
# ── Idempotency (AC3) ───────────────────────────────────────────────────
# Running twice in succession yields an identical final schema: the drop
# is `DROP DATABASE IF EXISTS`, the recreate is bootstrap's create-if-
# missing, and the app (or TAMMA_RUN_EF_MIGRATIONS=1) applies the same EF
# migration set each time.
#
# ── Safety gate ─────────────────────────────────────────────────────────
# This is DESTRUCTIVE. It refuses to run unless explicitly confirmed via
# EITHER:
#   • TAMMA_RESET_CONFIRM=yes   (env), or
#   • --force                    (flag)
# It additionally refuses to run when ASPNETCORE_ENVIRONMENT=Production.
#
# ── Connection params ───────────────────────────────────────────────────
# Same env contract as bootstrap-shared-dbs.sh (PGHOST/PGPORT/PGUSER/
# PGPASSWORD or DB_PASSWORD; TAMMA_CONTROL_DB / TAMMA_GLOBAL_ELSA_DB).
#
# Exit codes:
#   0  — drop + recreate + bootstrap succeeded
#   1  — usage / safety-gate / connection error
#   2  — a DROP / CREATE / bootstrap step failed
set -euo pipefail

PGHOST="${PGHOST:-postgres}"
PGPORT="${PGPORT:-5432}"
PGUSER="${PGUSER:-tamma}"
PGPASSWORD="${PGPASSWORD:-${DB_PASSWORD:-}}"
export PGHOST PGPORT PGUSER PGPASSWORD

TAMMA_CONTROL_DB="${TAMMA_CONTROL_DB:-tamma}"
TAMMA_GLOBAL_ELSA_DB="${TAMMA_GLOBAL_ELSA_DB:-tamma}"

# ── Parse --force flag ──────────────────────────────────────────────────
force=0
case "${1:-}" in
    "")            ;;
    --force)       force=1 ;;
    *)
        echo "ERROR: unknown flag '${1}'" >&2
        echo "Usage: TAMMA_RESET_CONFIRM=yes $0   |   $0 --force" >&2
        exit 1
        ;;
esac

# ── Safety gate ─────────────────────────────────────────────────────────
if [ "${ASPNETCORE_ENVIRONMENT:-}" = "Production" ]; then
    echo "ERROR: refusing to reset databases while ASPNETCORE_ENVIRONMENT=Production." >&2
    exit 1
fi
if [ "$force" != "1" ] && [ "${TAMMA_RESET_CONFIRM:-}" != "yes" ]; then
    echo "ERROR: reset-all is destructive. Confirm with TAMMA_RESET_CONFIRM=yes or --force." >&2
    echo "Usage: TAMMA_RESET_CONFIRM=yes $0   |   $0 --force" >&2
    exit 1
fi

if ! command -v psql >/dev/null 2>&1; then
    echo "[reset-all] FATAL: psql not found on PATH" >&2
    exit 1
fi

_psql_maint() {
    psql --dbname=postgres --no-psqlrc --quiet --tuples-only --no-align \
        --set=ON_ERROR_STOP=on "$@"
}

# Refuse to ever target a per-tenant database.
_assert_not_tenant_db() {
    local db="$1"
    case "$db" in
        tamma_tenant_*)
            echo "[reset-all] FATAL: refusing to drop per-tenant database '${db}'." >&2
            echo "            Per-tenant DBs are workflow-provisioned; reset-all only" >&2
            echo "            touches the shared control / global-Elsa databases." >&2
            exit 1
            ;;
    esac
}

_drop_db() {
    local db="$1"
    _assert_not_tenant_db "$db"

    # Terminate other backends so DROP DATABASE doesn't fail on open
    # connections (the app may hold a pool). Best-effort; ignore if the DB
    # is already gone.
    _psql_maint --command="
        SELECT pg_terminate_backend(pid)
        FROM pg_stat_activity
        WHERE datname = '${db}' AND pid <> pg_backend_pid();" >/dev/null 2>&1 || true

    if ! _psql_maint --command="DROP DATABASE IF EXISTS \"${db}\";"; then
        echo "[reset-all] FATAL: DROP DATABASE ${db} failed" >&2
        exit 2
    fi
    echo "[reset-all] dropped database ${db} (if it existed)" >&2
}

# De-dupe the target set (shared mode collapses both names to `tamma`).
declare -a dbs=()
dbs+=("$TAMMA_CONTROL_DB")
if [ "$TAMMA_GLOBAL_ELSA_DB" != "$TAMMA_CONTROL_DB" ]; then
    dbs+=("$TAMMA_GLOBAL_ELSA_DB")
fi

echo "[reset-all] dropping shared database(s): ${dbs[*]}" >&2
for db in "${dbs[@]}"; do
    _drop_db "$db"
done

# Recreate + (optionally) migrate by delegating to the bootstrap script —
# single source of truth for the create-if-missing + summary logic.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
echo "[reset-all] invoking bootstrap-shared-dbs.sh to recreate + ensure schema" >&2
if ! TAMMA_CONTROL_DB="$TAMMA_CONTROL_DB" \
     TAMMA_GLOBAL_ELSA_DB="$TAMMA_GLOBAL_ELSA_DB" \
     "$SCRIPT_DIR/bootstrap-shared-dbs.sh"; then
    echo "[reset-all] FATAL: bootstrap step failed" >&2
    exit 2
fi

echo "[reset-all] complete — shared database(s) reset and bootstrapped." >&2
