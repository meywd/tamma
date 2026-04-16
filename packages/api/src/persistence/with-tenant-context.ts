/**
 * Tenant-scoped transaction helper (Story 17-3)
 *
 * Executes `fn` with `app.current_tenant_id` set on a dedicated pool client
 * inside a transaction. Uses `SET LOCAL` (via `set_config(..., true)`) so the
 * session variable is scoped to the transaction only -- the connection is safe
 * to return to the pool afterwards without contaminating the next caller.
 */

import type pg from 'pg';

/**
 * Execute `fn` with `app.current_tenant_id` set on a dedicated pool client.
 *
 * Always uses `set_config(..., true)` inside a transaction so the session
 * variable is scoped to this unit of work only.
 */
export async function withTenantContext<T>(
  pool: pg.Pool,
  tenantId: string,
  fn: (client: pg.PoolClient) => Promise<T>,
): Promise<T> {
  const client = await pool.connect();
  try {
    await client.query('BEGIN');
    await client.query("SELECT set_config('app.current_tenant_id', $1, true)", [tenantId]);
    const result = await fn(client);
    await client.query('COMMIT');
    return result;
  } catch (err) {
    await client.query('ROLLBACK');
    throw err;
  } finally {
    client.release();
  }
}
