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
    'TRUNCATE TABLE user_installations, users, github_installation_repos, github_installations CASCADE',
  );
}

/**
 * Drop all test tables (for cleanup after all tests).
 */
export async function dropTables(pool: pg.Pool): Promise<void> {
  await pool.query(`
    DROP TABLE IF EXISTS user_installations CASCADE;
    DROP TABLE IF EXISTS users CASCADE;
    DROP TABLE IF EXISTS github_installation_repos CASCADE;
    DROP TABLE IF EXISTS github_installations CASCADE;
  `);
}
