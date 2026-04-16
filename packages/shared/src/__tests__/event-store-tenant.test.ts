/**
 * InMemoryEventStore Tests — Tenant Scoping (Story 17-3)
 *
 * Verifies that the event store correctly isolates events by tenantId.
 */

import { describe, it, expect, beforeEach } from 'vitest';
import { InMemoryEventStore } from '../event-store.js';
import { EngineEventType, DEFAULT_TENANT_ID } from '../index.js';

const TENANT_A = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const TENANT_B = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';

describe('InMemoryEventStore — tenant scoping', () => {
  let store: InMemoryEventStore;

  beforeEach(() => {
    store = new InMemoryEventStore();
  });

  // -----------------------------------------------------------------------
  // record
  // -----------------------------------------------------------------------

  describe('record', () => {
    it('stores event with correct tenantId', async () => {
      const event = await store.record({
        type: EngineEventType.ISSUE_SELECTED,
        tenantId: TENANT_A,
        issueNumber: 1,
        data: { title: 'Fix bug' },
      });

      expect(event.tenantId).toBe(TENANT_A);
      expect(event.id).toBeDefined();
      expect(event.timestamp).toBeGreaterThan(0);
    });

    it('stores events from multiple tenants interleaved', async () => {
      await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_A, data: {} });
      await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_B, data: {} });
      await store.record({ type: EngineEventType.PLAN_GENERATED, tenantId: TENANT_A, data: {} });

      const eventsA = await store.getEvents(TENANT_A);
      const eventsB = await store.getEvents(TENANT_B);

      expect(eventsA).toHaveLength(2);
      expect(eventsB).toHaveLength(1);
    });
  });

  // -----------------------------------------------------------------------
  // getEvents
  // -----------------------------------------------------------------------

  describe('getEvents', () => {
    it('returns only the specified tenant events', async () => {
      await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_A, issueNumber: 1, data: {} });
      await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_B, issueNumber: 2, data: {} });
      await store.record({ type: EngineEventType.PLAN_GENERATED, tenantId: TENANT_A, issueNumber: 1, data: {} });

      const eventsA = await store.getEvents(TENANT_A);
      expect(eventsA).toHaveLength(2);
      expect(eventsA.every((e) => e.tenantId === TENANT_A)).toBe(true);
    });

    it('filters by both tenant and issue number', async () => {
      await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_A, issueNumber: 1, data: {} });
      await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_A, issueNumber: 2, data: {} });
      await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_B, issueNumber: 1, data: {} });

      const events = await store.getEvents(TENANT_A, 1);
      expect(events).toHaveLength(1);
      expect(events[0]!.issueNumber).toBe(1);
      expect(events[0]!.tenantId).toBe(TENANT_A);
    });

    it('returns empty array for tenant with no events', async () => {
      await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_A, data: {} });
      const events = await store.getEvents(TENANT_B);
      expect(events).toEqual([]);
    });

    it('returns empty array for empty store', async () => {
      expect(await store.getEvents(TENANT_A)).toEqual([]);
    });
  });

  // -----------------------------------------------------------------------
  // getLastEvent
  // -----------------------------------------------------------------------

  describe('getLastEvent', () => {
    it('returns last event of the given type for the specified tenant', async () => {
      await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_A, data: { order: 1 } });
      await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_B, data: { order: 2 } });
      await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_A, data: { order: 3 } });

      const last = await store.getLastEvent(TENANT_A, EngineEventType.ISSUE_SELECTED);
      expect(last).toBeDefined();
      expect(last!.data['order']).toBe(3);
      expect(last!.tenantId).toBe(TENANT_A);
    });

    it('returns undefined when tenant has no events of the given type', async () => {
      await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_A, data: {} });
      const last = await store.getLastEvent(TENANT_B, EngineEventType.ISSUE_SELECTED);
      expect(last).toBeUndefined();
    });

    it('returns undefined for non-matching type', async () => {
      await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_A, data: {} });
      const last = await store.getLastEvent(TENANT_A, EngineEventType.PR_MERGED);
      expect(last).toBeUndefined();
    });
  });

  // -----------------------------------------------------------------------
  // clear
  // -----------------------------------------------------------------------

  describe('clear', () => {
    it('removes only the specified tenant events', async () => {
      await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_A, data: {} });
      await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_B, data: {} });
      await store.record({ type: EngineEventType.PLAN_GENERATED, tenantId: TENANT_A, data: {} });

      await store.clear(TENANT_A);

      expect(await store.getEvents(TENANT_A)).toHaveLength(0);
      expect(await store.getEvents(TENANT_B)).toHaveLength(1);
    });

    it('is safe to call on empty store', async () => {
      await expect(store.clear(TENANT_A)).resolves.toBeUndefined();
    });

    it('is safe to call for tenant with no events', async () => {
      await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_A, data: {} });
      await store.clear(TENANT_B);
      expect(await store.getEvents(TENANT_A)).toHaveLength(1);
    });
  });

  // -----------------------------------------------------------------------
  // DEFAULT_TENANT_ID backward compat
  // -----------------------------------------------------------------------

  describe('DEFAULT_TENANT_ID backward compatibility', () => {
    it('CLI mode events use DEFAULT_TENANT_ID', async () => {
      await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: DEFAULT_TENANT_ID, issueNumber: 42, data: {} });
      const events = await store.getEvents(DEFAULT_TENANT_ID, 42);
      expect(events).toHaveLength(1);
    });
  });
});
