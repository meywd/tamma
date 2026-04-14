/**
 * PostgreSQL test helper for integration tests.
 *
 * Provides connection management, migration execution, and table truncation.
 * Gated by INTEGRATION_TEST_PG=true environment variable.
 */

import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import pg from 'pg';

const MIGRATIONS_DIR = join(import.meta.dirname, '..', '..', '..', '..', '..', 'database', 'migrations');

const TEST_PG_CONFIG = {
  host: process.env['PG_TEST_HOST'] ?? 'localhost',
  port: parseInt(process.env['PG_TEST_PORT'] ?? '5433', 10),
  user: process.env['PG_TEST_USER'] ?? 'tamma_test',
  password: process.env['PG_TEST_PASSWORD'] ?? 'tamma_test',
  database: process.env['PG_TEST_DB'] ?? 'tamma_test',
};

/** Check if Postgres integration tests are enabled. */
export function isPgTestEnabled(): boolean {
  return process.env['INTEGRATION_TEST_PG'] === 'true';
}

/** Create a pg.Pool connected to the test database. */
export function createTestPool(): pg.Pool {
  return new pg.Pool(TEST_PG_CONFIG);
}

/**
 * Run all database migrations in order against the test database.
 * Migration files are read from database/migrations/ sorted by name.
 */
export async function runMigrations(pool: pg.Pool): Promise<void> {
  const migrationFiles = [
    '001_github_installations.sql',
    '002_users.sql',
    '003_api_keys.sql',
    '004_user_settings.sql',
    '005_user_api_keys.sql',
    '006_user_invites.sql',
    '007_users_soft_delete.sql',
    '008_tenants.sql',
    '009_unified_api_keys.sql',
    '010_rls_tenant_isolation.sql',
    '011_tenant_scoped_stores.sql',
    '012_prompt_store.sql',
  ];

  for (const file of migrationFiles) {
    const sql = readFileSync(join(MIGRATIONS_DIR, file), 'utf-8');
    await pool.query(sql);
  }
}

/**
 * Truncate all test tables in dependency order (child tables first).
 * Called between tests to ensure isolation.
 */
export async function truncateTables(pool: pg.Pool): Promise<void> {
  await pool.query(
    'TRUNCATE TABLE action_prompts, system_prompts, prompts, workflow_instances, engine_events, user_invites, user_api_keys, api_keys, user_installations, users, github_installation_repos, github_installations, tenants CASCADE',
  );
  // Re-insert default tenant sentinel (needed by FK constraints)
  await pool.query(`
    INSERT INTO tenants (id, name, slug, external_id, plan)
    VALUES ('00000000-0000-0000-0000-000000000000', 'Default', 'default', NULL, 'free')
    ON CONFLICT (id) DO NOTHING
  `);
}

/**
 * Set the PostgreSQL session variable for RLS tenant scoping.
 * Must be called on each connection before running tenant-scoped queries.
 */
export async function setTenantContext(pool: pg.Pool, tenantId: string): Promise<void> {
  await pool.query("SELECT set_config('app.current_tenant_id', $1, false)", [tenantId]);
}

/**
 * Reset the PostgreSQL session variable for RLS tenant scoping.
 */
export async function resetTenantContext(pool: pg.Pool): Promise<void> {
  await pool.query('RESET app.current_tenant_id');
}

/**
 * Switch to the tamma_app role so RLS policies are enforced.
 * The test DB superuser bypasses RLS — this is needed for RLS tests.
 */
export async function setAppRole(pool: pg.Pool): Promise<void> {
  await pool.query('SET ROLE tamma_app');
}

/**
 * Reset back to the superuser role (for data setup/teardown).
 */
export async function resetRole(pool: pg.Pool): Promise<void> {
  await pool.query('RESET ROLE');
}

/**
 * Drop all test tables (for cleanup after all tests).
 */
export async function dropTables(pool: pg.Pool): Promise<void> {
  await pool.query(`
    DROP TABLE IF EXISTS action_prompts CASCADE;
    DROP TABLE IF EXISTS system_prompts CASCADE;
    DROP TABLE IF EXISTS prompts CASCADE;
    DROP TABLE IF EXISTS workflow_instances CASCADE;
    DROP TABLE IF EXISTS engine_events CASCADE;
    DROP TABLE IF EXISTS api_keys CASCADE;
    DROP TABLE IF EXISTS user_invites CASCADE;
    DROP TABLE IF EXISTS user_api_keys CASCADE;
    DROP TABLE IF EXISTS user_installations CASCADE;
    DROP TABLE IF EXISTS users CASCADE;
    DROP TABLE IF EXISTS github_installation_repos CASCADE;
    DROP TABLE IF EXISTS github_installations CASCADE;
    DROP TABLE IF EXISTS tenants CASCADE;
    DROP FUNCTION IF EXISTS prevent_tenant_id_change() CASCADE;
  `);
}
