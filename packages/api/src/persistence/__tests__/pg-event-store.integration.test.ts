/**
 * PgEventStore Integration Tests (Story 17-3)
 *
 * Requires INTEGRATION_TEST_PG=true and a running PostgreSQL test instance.
 * Tests verify that PgEventStore correctly reads/writes engine_events with
 * tenant isolation via RLS policies.
 */

import { describe, it, expect, beforeAll, afterAll, beforeEach } from 'vitest';
import { EngineEventType } from '@tamma/shared';
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
import { PgEventStore } from '../pg-event-store.js';
import type pg from 'pg';

const TENANT_A_ID = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const TENANT_B_ID = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';

describe.skipIf(!isPgTestEnabled())('PgEventStore Integration', () => {
  let pool: pg.Pool;
  let store: PgEventStore;

  beforeAll(async () => {
    pool = createTestPool();
    await dropTables(pool);
    await runMigrations(pool);
    store = new PgEventStore(pool);
  });

  afterAll(async () => {
    await pool.end();
  });

  beforeEach(async () => {
    await resetRole(pool);
    await resetTenantContext(pool);
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
  // 1. record() inserts with correct tenantId
  // -----------------------------------------------------------------------

  it('record() inserts with explicit tenantId', async () => {
    const event = await store.record({
      type: EngineEventType.ISSUE_SELECTED,
      tenantId: TENANT_A_ID,
      issueNumber: 42,
      data: { title: 'Fix bug' },
    });

    expect(event.id).toBeDefined();
    expect(event.type).toBe(EngineEventType.ISSUE_SELECTED);
    expect(event.tenantId).toBe(TENANT_A_ID);
    expect(event.issueNumber).toBe(42);
    expect(event.data).toEqual({ title: 'Fix bug' });
    expect(event.timestamp).toBeGreaterThan(0);
  });

  // -----------------------------------------------------------------------
  // 2. Cross-tenant insert rejected by RLS
  // -----------------------------------------------------------------------

  it('record() under wrong session tenant is rejected by RLS', async () => {
    // Use tamma_app role so RLS is enforced
    // withTenantContext sets app.current_tenant_id = TENANT_A inside the txn,
    // but the INSERT has tenant_id = TENANT_B which violates WITH CHECK
    // We need to test this at a lower level — insert directly with role
    await setAppRole(pool);
    await setTenantContext(pool, TENANT_B_ID);

    await expect(
      pool.query(
        `INSERT INTO engine_events (id, type, timestamp, tenant_id, issue_number, data)
         VALUES (gen_random_uuid(), 'ISSUE_SELECTED', 1000, $1, NULL, '{}')`,
        [TENANT_A_ID],
      ),
    ).rejects.toThrow();

    await resetTenantContext(pool);
    await resetRole(pool);
  });

  // -----------------------------------------------------------------------
  // 3. getEvents() returns only tenant-scoped rows
  // -----------------------------------------------------------------------

  it('getEvents() returns only the specified tenant events', async () => {
    await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_A_ID, issueNumber: 1, data: {} });
    await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_B_ID, issueNumber: 2, data: {} });
    await store.record({ type: EngineEventType.PLAN_GENERATED, tenantId: TENANT_A_ID, data: {} });

    const eventsA = await store.getEvents(TENANT_A_ID);
    expect(eventsA).toHaveLength(2);
    expect(eventsA.every((e) => e.tenantId === TENANT_A_ID)).toBe(true);

    const eventsB = await store.getEvents(TENANT_B_ID);
    expect(eventsB).toHaveLength(1);
    expect(eventsB[0]!.tenantId).toBe(TENANT_B_ID);
  });

  // -----------------------------------------------------------------------
  // 4. getEvents() filters by tenant + issue number
  // -----------------------------------------------------------------------

  it('getEvents() filters by tenant and issueNumber', async () => {
    await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_A_ID, issueNumber: 1, data: {} });
    await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_A_ID, issueNumber: 2, data: {} });
    await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_B_ID, issueNumber: 1, data: {} });

    const events = await store.getEvents(TENANT_A_ID, 1);
    expect(events).toHaveLength(1);
    expect(events[0]!.issueNumber).toBe(1);
    expect(events[0]!.tenantId).toBe(TENANT_A_ID);
  });

  // -----------------------------------------------------------------------
  // 5. getLastEvent() returns most recent for tenant
  // -----------------------------------------------------------------------

  it('getLastEvent() returns most recent event for tenant', async () => {
    await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_A_ID, data: { order: 1 } });
    await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_B_ID, data: { order: 2 } });
    await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_A_ID, data: { order: 3 } });

    const last = await store.getLastEvent(TENANT_A_ID, EngineEventType.ISSUE_SELECTED);
    expect(last).toBeDefined();
    expect(last!.data['order']).toBe(3);
    expect(last!.tenantId).toBe(TENANT_A_ID);
  });

  it('getLastEvent() returns undefined for tenant with no events of type', async () => {
    await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_A_ID, data: {} });
    const last = await store.getLastEvent(TENANT_A_ID, EngineEventType.PR_MERGED);
    expect(last).toBeUndefined();
  });

  // -----------------------------------------------------------------------
  // 6. clear() removes only tenant rows
  // -----------------------------------------------------------------------

  it('clear() removes only the specified tenant rows', async () => {
    await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_A_ID, data: {} });
    await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_B_ID, data: {} });
    await store.record({ type: EngineEventType.PLAN_GENERATED, tenantId: TENANT_A_ID, data: {} });

    await store.clear(TENANT_A_ID);

    const eventsA = await store.getEvents(TENANT_A_ID);
    expect(eventsA).toHaveLength(0);

    const eventsB = await store.getEvents(TENANT_B_ID);
    expect(eventsB).toHaveLength(1);
  });

  // -----------------------------------------------------------------------
  // 7. RLS fail-closed when session var unset (via raw query)
  // -----------------------------------------------------------------------

  it('fails closed when app.current_tenant_id is unset under tamma_app role', async () => {
    // Insert as superuser
    await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_A_ID, data: {} });

    // Switch to app role without setting tenant context
    await setAppRole(pool);
    await resetTenantContext(pool);

    // With no tenant context, the RLS policy casts empty string to UUID which
    // fails — this IS the correct fail-closed behavior
    await expect(
      pool.query('SELECT * FROM engine_events'),
    ).rejects.toThrow();

    await resetRole(pool);
  });

  // -----------------------------------------------------------------------
  // 8. UPDATE of tenant_id blocked by trigger
  // -----------------------------------------------------------------------

  it('blocks tenant_id mutation via trigger', async () => {
    const event = await store.record({
      type: EngineEventType.ISSUE_SELECTED,
      tenantId: TENANT_A_ID,
      data: {},
    });

    await setAppRole(pool);
    await setTenantContext(pool, TENANT_A_ID);

    await expect(
      pool.query('UPDATE engine_events SET tenant_id = $1 WHERE id = $2', [TENANT_B_ID, event.id]),
    ).rejects.toThrow(/cannot change tenant_id/i);

    await resetTenantContext(pool);
    await resetRole(pool);
  });

  // -----------------------------------------------------------------------
  // 9. JSONB data round-trip
  // -----------------------------------------------------------------------

  it('stores and retrieves JSONB data round-trip', async () => {
    const complexData = {
      title: 'Fix bug',
      nested: { key: 'value', arr: [1, 2, 3] },
      number: 42,
      bool: true,
    };

    const event = await store.record({
      type: EngineEventType.ISSUE_SELECTED,
      tenantId: TENANT_A_ID,
      issueNumber: 99,
      data: complexData,
    });

    expect(event.data).toEqual(complexData);

    const events = await store.getEvents(TENANT_A_ID, 99);
    expect(events[0]!.data).toEqual(complexData);
  });

  // -----------------------------------------------------------------------
  // 10. Events without issueNumber omit the field
  // -----------------------------------------------------------------------

  it('omits issueNumber when null (exactOptionalPropertyTypes)', async () => {
    const event = await store.record({
      type: EngineEventType.STATE_TRANSITION,
      tenantId: TENANT_A_ID,
      data: { from: 'IDLE', to: 'SELECTING' },
    });

    expect(event.issueNumber).toBeUndefined();
    expect('issueNumber' in event).toBe(false);
  });
});
