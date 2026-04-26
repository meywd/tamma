-- Story 28-12 — Postgres role bootstrap for Tamma's three-tier
-- privilege separation. Run ONCE on a fresh Postgres cluster as a
-- superuser (e.g. the `postgres` initial role). Idempotent — every
-- statement guards on `pg_roles` so re-runs are safe.
--
-- Three roles, in order of decreasing privilege:
--
--   tamma_admin       — SUPERUSER. Cluster bootstrap + emergency-only.
--                        Used by the docker-entrypoint init script and
--                        by operators when running `psql` against the
--                        cluster directly. NEVER used by application
--                        code.
--
--   tamma_provisioner — CREATEDB, CREATEROLE, NOT SUPERUSER. Used by
--                        the global-Elsa CreateTenantWorkflow to
--                        provision new per-tenant databases + roles.
--                        Wired via ConnectionStrings:TenantAdmin /
--                        ITenantAdminConnection.
--
--   tamma_app         — Plain login role with SELECT/INSERT/UPDATE/
--                        DELETE on the control-plane schema only. Used
--                        by the Tamma.Api process at run-time. Per-
--                        tenant roles (tamma_t_<hex>) get their own
--                        privileges scoped to their own database.
--
-- Passwords: this script DOES NOT set passwords — it expects them to
-- come from environment variables sourced before psql is invoked. The
-- docker-entrypoint hook substitutes them in. Direct operator use
-- requires the operator to set TAMMA_PROVISIONER_PASSWORD +
-- TAMMA_APP_PASSWORD before running this script.

\set ON_ERROR_STOP on

-- ── tamma_admin ─────────────────────────────────────────────────────
-- Skip when the role already exists. SUPERUSER is sticky — we don't
-- attempt to ALTER it on every re-run because changing SUPERUSER on
-- a live cluster is too sharp.
DO
$$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'tamma_admin') THEN
        EXECUTE format(
            'CREATE ROLE tamma_admin LOGIN SUPERUSER PASSWORD %L',
            current_setting('tamma.admin_password', true));
    END IF;
END
$$;

-- ── tamma_provisioner ───────────────────────────────────────────────
DO
$$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'tamma_provisioner') THEN
        EXECUTE format(
            'CREATE ROLE tamma_provisioner LOGIN CREATEDB CREATEROLE NOSUPERUSER PASSWORD %L',
            current_setting('tamma.provisioner_password', true));
    END IF;
END
$$;

-- The provisioner needs to read tenants from the CP DB to look up
-- existing rows during provisioning probes. Granted at the schema
-- level so future tables in the CP schema inherit it.
GRANT CONNECT ON DATABASE :"cp_database" TO tamma_provisioner;

-- ── tamma_app ───────────────────────────────────────────────────────
DO
$$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'tamma_app') THEN
        EXECUTE format(
            'CREATE ROLE tamma_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD %L',
            current_setting('tamma.app_password', true));
    END IF;
END
$$;

GRANT CONNECT ON DATABASE :"cp_database" TO tamma_app;

-- The actual table-level grants (SELECT/INSERT/UPDATE/DELETE on the
-- CP tables) are issued by the EF migration pipeline AFTER the
-- migrations have created the tables. This script bootstraps the role
-- itself; the migration runner extends the grants as the schema
-- evolves.

-- ── Sanity report ───────────────────────────────────────────────────
\echo
\echo 'Tamma roles after bootstrap:'
SELECT rolname,
       rolsuper AS is_superuser,
       rolcreatedb AS can_create_db,
       rolcreaterole AS can_create_role,
       rolcanlogin AS can_login
FROM pg_roles
WHERE rolname IN ('tamma_admin', 'tamma_provisioner', 'tamma_app')
ORDER BY rolname;
\echo
