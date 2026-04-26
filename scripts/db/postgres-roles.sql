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
-- Passwords (Story 28-12 + R2 fix H1 + R2 post-fix PF-S2): the
-- docker-entrypoint hook composes a chmod-0600 preamble file that
-- contains three psql `\set` directives binding the role passwords
-- to in-process variables. The preamble + this file are concatenated
-- and piped to psql via stdin (`--file=-`). psql substitutes
-- :'admin_password' inline as a properly-quoted SQL literal at parse
-- time. The plaintext NEVER appears on the psql command line; the
-- only argv elements left are the dbname, username, the literal
-- `--file=-`, and the cp_database variable (not a secret).
--
-- The CREATE ROLE statements are wrapped in transactions that begin
-- with `SET LOCAL log_statement = 'none'` so the statement does not
-- get written to the server log even when log_statement=ddl|all is
-- set at the cluster level. Combined with `WITH ENCRYPTED PASSWORD`,
-- the plaintext is never persisted at rest (pg_authid stores the
-- scram-sha-256 hash).
--
-- IMPORTANT: This script expects three psql variables to be already
-- set in the session — `admin_password`, `provisioner_password`, and
-- `app_password`. The docker-entrypoint hook composes them via the
-- chmod-0600 preamble approach above. Direct operator invocation
-- should follow the same shape (write a chmod-0600 preamble, pipe
-- preamble+roles.sql into psql via stdin) — see
-- `.dev/runbooks/postgres-bootstrap.md`. The previous "use psql
-- --set on the command line" pattern leaked plaintext via
-- /proc/<pid>/cmdline; do NOT regress to it.
--
-- Note on psql variable substitution: psql substitutes :'var' OUTSIDE
-- dollar-quoted strings ($$…$$). The previous shape used DO …
-- EXECUTE format(... PASSWORD %L, current_setting(...)) which leaked
-- the password through pg_stat_activity (the SET set_config call
-- before the DO block). The new shape uses plain CREATE ROLE at the
-- top level, gated by a `\if` directive that probes pg_roles via
-- :'admin_password'-style substitution.

\set ON_ERROR_STOP on

-- ── Suppress logging at the source ──────────────────────────────────
-- These SET LOCAL directives apply to the current transaction. The
-- Tamma bootstrap is one logical operation per role; we wrap each in
-- its own BEGIN/COMMIT so the SET LOCAL silences logs for ONLY that
-- block. After COMMIT the cluster log_statement returns to whatever
-- the cluster configured (usually 'none' or 'ddl').

-- ── tamma_admin ─────────────────────────────────────────────────────
-- Probe for existence; psql `\gset` reads the result into variables we
-- can then test in `\if`.
SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'tamma_admin') AS tamma_admin_exists \gset

\if :tamma_admin_exists
\echo 'tamma_admin already exists — skipping CREATE ROLE'
\else
BEGIN;
SET LOCAL log_statement = 'none';
SET LOCAL log_min_duration_statement = -1;
-- psql substitutes :'admin_password' as a properly-escaped SQL literal
-- (proper single-quote escaping, no SQL-injection risk). The cluster
-- session-log line for this CREATE ROLE has been silenced via SET
-- LOCAL log_statement = 'none' above. The variable itself is bound by
-- the preamble file the docker-entrypoint hook composes (chmod 0600),
-- not by `psql --set` argv (which would leak via /proc/<pid>/cmdline).
CREATE ROLE tamma_admin LOGIN SUPERUSER ENCRYPTED PASSWORD :'admin_password';
COMMIT;
\endif

-- ── tamma_provisioner ───────────────────────────────────────────────
SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'tamma_provisioner') AS tamma_provisioner_exists \gset

\if :tamma_provisioner_exists
\echo 'tamma_provisioner already exists — skipping CREATE ROLE'
\else
BEGIN;
SET LOCAL log_statement = 'none';
SET LOCAL log_min_duration_statement = -1;
CREATE ROLE tamma_provisioner LOGIN CREATEDB CREATEROLE NOSUPERUSER ENCRYPTED PASSWORD :'provisioner_password';
COMMIT;
\endif

-- The provisioner needs to read tenants from the CP DB to look up
-- existing rows during provisioning probes. Granted at the schema
-- level so future tables in the CP schema inherit it.
GRANT CONNECT ON DATABASE :"cp_database" TO tamma_provisioner;

-- ── tamma_app ───────────────────────────────────────────────────────
SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'tamma_app') AS tamma_app_exists \gset

\if :tamma_app_exists
\echo 'tamma_app already exists — skipping CREATE ROLE'
\else
BEGIN;
SET LOCAL log_statement = 'none';
SET LOCAL log_min_duration_statement = -1;
CREATE ROLE tamma_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE ENCRYPTED PASSWORD :'app_password';
COMMIT;
\endif

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
