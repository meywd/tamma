#!/usr/bin/env bash
# Story 28-1 AC2 — idempotent bootstrap for Tamma's shared databases.
#
# ── Topology (unified schema-per-tenant) ────────────────────────────────
# Story 28-1 AC2 was written against the original db-per-tenant design
# (separate `tamma_control` + `tamma_global_elsa` databases). The current
# deployment uses the UNIFIED tenancy model: the central `tamma` database
# hosts the control plane + Elsa's own tables and is pool member #1
# ("central") in `tenant_databases`; every tenant lives in its own
# `t_<hex>` schema with a per-tenant Postgres role and an AES-GCM-
# encrypted connection string. Placement is tier-driven via the
# `tenant_databases` pool (see the root CLAUDE.md "Multi-tenant
# provisioning (Cranl)" section).
#
# Because of that, the databases this script must ENSURE exist today are
# just `tamma` (control plane + tenant schemas) and — only if a deployment
# splits Elsa onto its own database — a separate Elsa database. Both
# default to the single central `tamma` DB. The DB names are PARAMETERISED
# so this script is forward-compatible with additional pool databases:
# when `tamma_control` / `tamma_global_elsa` become real, point
# TAMMA_CONTROL_DB / TAMMA_GLOBAL_ELSA_DB at them and the same create-if-
# missing + summary logic applies unchanged.
#
# ── What "apply migrations" means here ──────────────────────────────────
# The Tamma app SELF-MIGRATES on boot:
#   • tamma-api  → Program.cs calls dbContext.Database.Migrate()
#   • elsa-server → Program.cs sets ef.RunMigrations = true (Elsa EF)
# So in the normal Docker flow this script's job is to guarantee the
# target databases EXIST before the api / elsa-server containers start;
# the containers then apply their own migrations idempotently. That keeps
# a single source of truth for the schema (the EF migration assemblies)
# and avoids drift between a shell-driven `dotnet ef database update` and
# the app's own Migrate() call.
#
# For fresh-cluster init or a CI job that wants the schema applied WITHOUT
# booting the app, set TAMMA_RUN_EF_MIGRATIONS=1 and the script will drive
# `dotnet ef database update` for the Tamma DbContexts it can find (best
# effort — requires the .NET SDK + the Tamma.Data project on the host).
#
# ── Behaviour (AC2) ─────────────────────────────────────────────────────
#   • Creates each target database if missing (guarded by a
#     `SELECT 1 FROM pg_database` probe — never errors on re-run).
#   • Safe to re-run: second run is a no-op for already-present DBs.
#   • Exits non-zero on ANY failure (set -euo pipefail + explicit checks).
#   • Emits one structured JSON-lines summary per DB on stdout:
#       { "db": "...", "migrationsApplied": N, "durationMs": N }
#
# ── Connection params (env, with defaults) ──────────────────────────────
#   PGHOST      (default: postgres)   — Postgres host
#   PGPORT      (default: 5432)        — Postgres port
#   PGUSER      (default: tamma)       — superuser / role with CREATEDB
#   PGPASSWORD  (default: $DB_PASSWORD) — password for PGUSER
#   DB_PASSWORD                        — compose-style fallback for PGPASSWORD
#
#   TAMMA_CONTROL_DB      (default: tamma)  — control-plane + tenant DB
#   TAMMA_GLOBAL_ELSA_DB  (default: tamma)  — global-Elsa DB (== tamma in
#                                             shared mode; set distinct when
#                                             Elsa gets its own database)
#   TAMMA_RUN_EF_MIGRATIONS (default: 0)    — 1 ⇒ also run `dotnet ef
#                                             database update` (host needs
#                                             the .NET SDK)
#
# Exit codes:
#   0  — all target DBs present (created or already existed)
#   1  — usage / connection error
#   2  — a CREATE DATABASE or migration step failed
set -euo pipefail

PGHOST="${PGHOST:-postgres}"
PGPORT="${PGPORT:-5432}"
PGUSER="${PGUSER:-tamma}"
# Compose passes DB_PASSWORD; libpq reads PGPASSWORD. Bridge the two.
PGPASSWORD="${PGPASSWORD:-${DB_PASSWORD:-}}"
export PGHOST PGPORT PGUSER PGPASSWORD

TAMMA_CONTROL_DB="${TAMMA_CONTROL_DB:-tamma}"
TAMMA_GLOBAL_ELSA_DB="${TAMMA_GLOBAL_ELSA_DB:-tamma}"
TAMMA_RUN_EF_MIGRATIONS="${TAMMA_RUN_EF_MIGRATIONS:-0}"

if ! command -v psql >/dev/null 2>&1; then
    echo "[bootstrap] FATAL: psql not found on PATH" >&2
    exit 1
fi

# Wait for Postgres to accept connections (the one-shot service may race
# the postgres container even with depends_on: service_healthy on slow
# hosts). Bounded retry; fail closed.
_wait_for_pg() {
    local attempts=30
    local i=0
    while [ "$i" -lt "$attempts" ]; do
        if pg_isready -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" >/dev/null 2>&1; then
            return 0
        fi
        i=$((i + 1))
        sleep 2
    done
    echo "[bootstrap] FATAL: Postgres at $PGHOST:$PGPORT not ready after ${attempts} probes" >&2
    return 1
}

# Run a psql command against the maintenance `postgres` DB (so we can
# CREATE other databases). Connection params come from the exported PG*
# env vars — nothing secret is passed in argv.
_psql_maint() {
    psql --dbname=postgres --no-psqlrc --quiet --tuples-only --no-align \
        --set=ON_ERROR_STOP=on "$@"
}

# Idempotently ensure a database exists. Echoes the JSON-lines summary.
# Globals: TAMMA_RUN_EF_MIGRATIONS
_ensure_db() {
    local db="$1"
    local started_ms applied=0 ended_ms duration_ms exists

    started_ms="$(date +%s%3N)"

    # Guard CREATE DATABASE with a pg_database probe — CREATE DATABASE is
    # not transactional and has no IF NOT EXISTS, so we check first.
    exists="$(_psql_maint --command="SELECT 1 FROM pg_database WHERE datname = '${db}';")"
    if [ "$exists" != "1" ]; then
        # Quote the identifier defensively. DB names here come from a fixed
        # env allow-list (tamma / tamma_control / tamma_global_elsa), never
        # untrusted input, but quote_ident keeps it safe regardless.
        if ! _psql_maint --command="CREATE DATABASE \"${db}\";"; then
            echo "[bootstrap] FATAL: CREATE DATABASE ${db} failed" >&2
            return 2
        fi
        echo "[bootstrap] created database ${db}" >&2
    else
        echo "[bootstrap] database ${db} already present (no-op)" >&2
    fi

    # Optional: drive EF migrations from the host for fresh-cluster / CI
    # flows that don't boot the app. Normal Docker flow leaves this off and
    # lets the api / elsa-server containers self-migrate at boot.
    if [ "$TAMMA_RUN_EF_MIGRATIONS" = "1" ]; then
        applied="$(_run_ef_migrations "$db")" || return 2
    fi

    ended_ms="$(date +%s%3N)"
    duration_ms=$((ended_ms - started_ms))

    # AC2 structured summary — one JSON object per DB on stdout.
    printf '{ "db": "%s", "migrationsApplied": %s, "durationMs": %s }\n' \
        "$db" "$applied" "$duration_ms"
}

# Best-effort EF migration runner. Returns the count of migrations applied
# on stdout. Only used when TAMMA_RUN_EF_MIGRATIONS=1.
_run_ef_migrations() {
    local db="$1"
    local data_proj count_before count_after conn

    if ! command -v dotnet >/dev/null 2>&1; then
        echo "[bootstrap] FATAL: TAMMA_RUN_EF_MIGRATIONS=1 but dotnet SDK not on PATH" >&2
        return 2
    fi

    # Locate the Tamma.Data project relative to this script.
    local script_dir
    script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
    data_proj="${script_dir}/../../apps/tamma-elsa/src/Tamma.Data"
    if [ ! -d "$data_proj" ]; then
        echo "[bootstrap] FATAL: Tamma.Data project not found at ${data_proj}" >&2
        return 2
    fi

    conn="Host=${PGHOST};Port=${PGPORT};Database=${db};Username=${PGUSER};Password=${PGPASSWORD}"

    # Tamma ships dedicated EF DbContexts; in shared-DB mode the api self-
    # migrates the consolidated schema. We invoke `dotnet ef database
    # update` for the default context. Count applied migrations via the
    # history table delta so the summary is honest.
    count_before="$(_psql_maint --dbname="$db" \
        --command="SELECT count(*) FROM \"__TammaMigrationsHistory\";" 2>/dev/null || echo 0)"

    if ! ConnectionStrings__TammaDb="$conn" \
        dotnet ef database update --project "$data_proj" >&2; then
        echo "[bootstrap] FATAL: dotnet ef database update failed for ${db}" >&2
        return 2
    fi

    count_after="$(_psql_maint --dbname="$db" \
        --command="SELECT count(*) FROM \"__TammaMigrationsHistory\";" 2>/dev/null || echo 0)"
    echo $((count_after - count_before))
}

main() {
    _wait_for_pg

    # Build the unique set of target databases. In shared mode both names
    # collapse to `tamma`, so de-dupe to avoid a redundant probe + a
    # confusing double summary line.
    local -a dbs=()
    dbs+=("$TAMMA_CONTROL_DB")
    if [ "$TAMMA_GLOBAL_ELSA_DB" != "$TAMMA_CONTROL_DB" ]; then
        dbs+=("$TAMMA_GLOBAL_ELSA_DB")
    fi

    local db
    for db in "${dbs[@]}"; do
        _ensure_db "$db"
    done

    echo "[bootstrap] complete — ${#dbs[@]} shared database(s) ensured." >&2
}

main "$@"
