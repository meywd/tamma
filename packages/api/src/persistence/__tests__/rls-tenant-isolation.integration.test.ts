/**
 * RLS Tenant Isolation Integration Tests (Story 17-2)
 *
 * Requires INTEGRATION_TEST_PG=true and a running PostgreSQL test instance.
 * Tests prove that Row-Level Security policies correctly isolate tenant data.
 */

import { describe, it, expect, beforeAll, afterAll, beforeEach } from 'vitest';
import {
  isPgTestEnabled,
  createTestPool,
  runMigrations,
  truncateTables,
  dropTables,
  setTenantContext,
  resetTenantContext,
  setAppRole,
  resetRole,
} from './pg-test-helper.js';
import type pg from 'pg';

const TENANT_A_ID = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const TENANT_B_ID = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
const DEFAULT_TENANT_ID = '00000000-0000-0000-0000-000000000000';

describe.skipIf(!isPgTestEnabled())('RLS Tenant Isolation', () => {
  let pool: pg.Pool;

  beforeAll(async () => {
    pool = createTestPool();
    await dropTables(pool);
    await runMigrations(pool);
  });

  afterAll(async () => {
    await pool.end();
  });

  beforeEach(async () => {
    await truncateTables(pool);

    // Create test tenants
    await pool.query(`
      INSERT INTO tenants (id, name, slug, plan)
      VALUES ($1, 'Tenant A', 'tenant-a', 'free'),
             ($2, 'Tenant B', 'tenant-b', 'free')
      ON CONFLICT (id) DO NOTHING
    `, [TENANT_A_ID, TENANT_B_ID]);
  });

  // -----------------------------------------------------------------------
  // Cross-tenant read isolation
  // -----------------------------------------------------------------------

  it('prevents cross-tenant reads on github_installations', async () => {
    // Insert as superuser
    await pool.query(`
      INSERT INTO github_installations (installation_id, account_login, account_type, app_id, tenant_id)
      VALUES (1001, 'acme', 'Organization', 1, $1)
    `, [TENANT_A_ID]);

    // Switch to app role for RLS enforcement
    await setAppRole(pool);

    await setTenantContext(pool, TENANT_B_ID);
    const result = await pool.query('SELECT * FROM github_installations');
    expect(result.rows).toHaveLength(0);

    await setTenantContext(pool, TENANT_A_ID);
    const resultA = await pool.query('SELECT * FROM github_installations');
    expect(resultA.rows).toHaveLength(1);

    await resetTenantContext(pool);
    await resetRole(pool);
  });

  // -----------------------------------------------------------------------
  // Same-tenant read
  // -----------------------------------------------------------------------

  it('allows same-tenant reads', async () => {
    await pool.query(`
      INSERT INTO github_installations (installation_id, account_login, account_type, app_id, tenant_id)
      VALUES (1002, 'same-tenant', 'Organization', 1, $1)
    `, [TENANT_A_ID]);

    await setAppRole(pool);
    await setTenantContext(pool, TENANT_A_ID);
    const result = await pool.query('SELECT * FROM github_installations WHERE account_login = $1', ['same-tenant']);
    expect(result.rows).toHaveLength(1);
    expect(result.rows[0].tenant_id).toBe(TENANT_A_ID);

    await resetTenantContext(pool);
    await resetRole(pool);
  });

  // -----------------------------------------------------------------------
  // Cross-tenant write rejection
  // -----------------------------------------------------------------------

  it('rejects cross-tenant inserts', async () => {
    await setAppRole(pool);
    await setTenantContext(pool, TENANT_B_ID);

    await expect(
      pool.query(`
        INSERT INTO github_installations (installation_id, account_login, account_type, app_id, tenant_id)
        VALUES (1003, 'sneaky', 'Organization', 1, $1)
      `, [TENANT_A_ID]),
    ).rejects.toThrow();

    await resetTenantContext(pool);
    await resetRole(pool);
  });

  // -----------------------------------------------------------------------
  // Fail-closed when unset
  // -----------------------------------------------------------------------

  it('fails closed when app.current_tenant_id is not set', async () => {
    await pool.query(`
      INSERT INTO github_installations (installation_id, account_login, account_type, app_id, tenant_id)
      VALUES (1004, 'fail-closed', 'Organization', 1, $1)
    `, [TENANT_A_ID]);

    await setAppRole(pool);
    await resetTenantContext(pool);

    // With no tenant context, the RLS policy casts empty string to UUID which
    // fails — this IS the correct fail-closed behavior (query errors, not leaks)
    await expect(
      pool.query('SELECT * FROM github_installations WHERE account_login = $1', ['fail-closed']),
    ).rejects.toThrow();

    await resetRole(pool);
  });

  // -----------------------------------------------------------------------
  // tenant_id mutation blocked
  // -----------------------------------------------------------------------

  it('blocks tenant_id mutation via trigger', async () => {
    await pool.query(`
      INSERT INTO github_installations (installation_id, account_login, account_type, app_id, tenant_id)
      VALUES (1005, 'immutable-tenant', 'Organization', 1, $1)
    `, [TENANT_A_ID]);

    await setAppRole(pool);
    await setTenantContext(pool, TENANT_A_ID);

    await expect(
      pool.query(`
        UPDATE github_installations SET tenant_id = $1 WHERE account_login = 'immutable-tenant'
      `, [TENANT_B_ID]),
    ).rejects.toThrow(/cannot change tenant_id/i);

    await resetTenantContext(pool);
    await resetRole(pool);
  });

  // -----------------------------------------------------------------------
  // RLS on tenants table (self-referencing)
  // -----------------------------------------------------------------------

  it('isolates tenants table by self-referencing policy', async () => {
    await setAppRole(pool);
    await setTenantContext(pool, TENANT_A_ID);
    const result = await pool.query('SELECT * FROM tenants');
    expect(result.rows).toHaveLength(1);
    expect(result.rows[0].id).toBe(TENANT_A_ID);

    await resetTenantContext(pool);
    await resetRole(pool);
  });

  // -----------------------------------------------------------------------
  // RLS on engine_events table
  // -----------------------------------------------------------------------

  it('isolates engine_events by tenant', async () => {
    await pool.query(`
      INSERT INTO engine_events (type, tenant_id, data)
      VALUES ('ISSUE_SELECTED', $1, '{}'),
             ('PLAN_GENERATED', $2, '{}')
    `, [TENANT_A_ID, TENANT_B_ID]);

    await setAppRole(pool);

    await setTenantContext(pool, TENANT_A_ID);
    const resultA = await pool.query('SELECT * FROM engine_events');
    expect(resultA.rows).toHaveLength(1);
    expect(resultA.rows[0].type).toBe('ISSUE_SELECTED');

    await setTenantContext(pool, TENANT_B_ID);
    const resultB = await pool.query('SELECT * FROM engine_events');
    expect(resultB.rows).toHaveLength(1);
    expect(resultB.rows[0].type).toBe('PLAN_GENERATED');

    await resetTenantContext(pool);
    await resetRole(pool);
  });

  // -----------------------------------------------------------------------
  // RLS on workflow_instances table
  // -----------------------------------------------------------------------

  it('isolates workflow_instances by tenant', async () => {
    await pool.query(`
      INSERT INTO workflow_instances (definition_id, tenant_id, status)
      VALUES ('def-1', $1, 'running'),
             ('def-1', $2, 'completed')
    `, [TENANT_A_ID, TENANT_B_ID]);

    await setAppRole(pool);

    await setTenantContext(pool, TENANT_A_ID);
    const resultA = await pool.query('SELECT * FROM workflow_instances');
    expect(resultA.rows).toHaveLength(1);
    expect(resultA.rows[0].status).toBe('running');

    await setTenantContext(pool, TENANT_B_ID);
    const resultB = await pool.query('SELECT * FROM workflow_instances');
    expect(resultB.rows).toHaveLength(1);
    expect(resultB.rows[0].status).toBe('completed');

    await resetTenantContext(pool);
    await resetRole(pool);
  });

  // -----------------------------------------------------------------------
  // Users table RLS
  // -----------------------------------------------------------------------

  it('isolates users by tenant', async () => {
    await pool.query(`
      INSERT INTO users (github_id, github_login, role, tenant_id)
      VALUES (100, 'user-a', 'admin', $1),
             (200, 'user-b', 'member', $2)
    `, [TENANT_A_ID, TENANT_B_ID]);

    await setAppRole(pool);

    await setTenantContext(pool, TENANT_A_ID);
    const resultA = await pool.query('SELECT * FROM users');
    expect(resultA.rows).toHaveLength(1);
    expect(resultA.rows[0].github_login).toBe('user-a');

    await setTenantContext(pool, TENANT_B_ID);
    const resultB = await pool.query('SELECT * FROM users');
    expect(resultB.rows).toHaveLength(1);
    expect(resultB.rows[0].github_login).toBe('user-b');

    await resetTenantContext(pool);
    await resetRole(pool);
  });
});
