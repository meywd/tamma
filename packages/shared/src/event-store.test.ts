import { describe, it, expect, beforeEach } from 'vitest';
import { InMemoryEventStore } from './event-store.js';
import { EngineEventType, DEFAULT_TENANT_ID } from './types/index.js';

const TENANT_A = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const TENANT_B = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';

describe('InMemoryEventStore', () => {
  let store: InMemoryEventStore;

  beforeEach(() => {
    store = new InMemoryEventStore();
  });

  describe('record', () => {
    it('should create events with id, timestamp, and tenantId', () => {
      const event = store.record({
        type: EngineEventType.ISSUE_SELECTED,
        tenantId: DEFAULT_TENANT_ID,
        issueNumber: 42,
        data: { title: 'Fix bug' },
      });

      expect(event.id).toBeDefined();
      expect(typeof event.id).toBe('string');
      expect(event.id.length).toBeGreaterThan(0);
      expect(event.timestamp).toBeDefined();
      expect(typeof event.timestamp).toBe('number');
      expect(event.type).toBe(EngineEventType.ISSUE_SELECTED);
      expect(event.tenantId).toBe(DEFAULT_TENANT_ID);
      expect(event.issueNumber).toBe(42);
      expect(event.data).toEqual({ title: 'Fix bug' });
    });

    it('should assign unique ids to each event', () => {
      const e1 = store.record({
        type: EngineEventType.ISSUE_SELECTED,
        tenantId: DEFAULT_TENANT_ID,
        data: {},
      });
      const e2 = store.record({
        type: EngineEventType.ISSUE_ANALYZED,
        tenantId: DEFAULT_TENANT_ID,
        data: {},
      });

      expect(e1.id).not.toBe(e2.id);
    });
  });

  describe('getEvents', () => {
    it('should return all events for a tenant when no issueNumber is provided', () => {
      store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: DEFAULT_TENANT_ID, issueNumber: 1, data: {} });
      store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: DEFAULT_TENANT_ID, issueNumber: 2, data: {} });
      store.record({ type: EngineEventType.PLAN_GENERATED, tenantId: DEFAULT_TENANT_ID, data: {} });

      const events = store.getEvents(DEFAULT_TENANT_ID);
      expect(events).toHaveLength(3);
    });

    it('should return empty array when store is empty', () => {
      expect(store.getEvents(DEFAULT_TENANT_ID)).toEqual([]);
    });

    it('should filter by issueNumber', () => {
      store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: DEFAULT_TENANT_ID, issueNumber: 1, data: {} });
      store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: DEFAULT_TENANT_ID, issueNumber: 2, data: {} });
      store.record({ type: EngineEventType.PLAN_GENERATED, tenantId: DEFAULT_TENANT_ID, issueNumber: 1, data: {} });

      const events = store.getEvents(DEFAULT_TENANT_ID, 1);
      expect(events).toHaveLength(2);
      expect(events.every((e) => e.issueNumber === 1)).toBe(true);
    });

    it('should return empty array when no events match issueNumber', () => {
      store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: DEFAULT_TENANT_ID, issueNumber: 1, data: {} });
      expect(store.getEvents(DEFAULT_TENANT_ID, 999)).toEqual([]);
    });
  });

  describe('getLastEvent', () => {
    it('should return the most recent event of the given type for the tenant', () => {
      store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: DEFAULT_TENANT_ID, issueNumber: 1, data: { first: true } });
      store.record({ type: EngineEventType.PLAN_GENERATED, tenantId: DEFAULT_TENANT_ID, data: {} });
      store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: DEFAULT_TENANT_ID, issueNumber: 2, data: { second: true } });

      const last = store.getLastEvent(DEFAULT_TENANT_ID, EngineEventType.ISSUE_SELECTED);
      expect(last).toBeDefined();
      expect(last!.issueNumber).toBe(2);
      expect(last!.data).toEqual({ second: true });
    });

    it('should return undefined when no matching events exist', () => {
      store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: DEFAULT_TENANT_ID, data: {} });
      const last = store.getLastEvent(DEFAULT_TENANT_ID, EngineEventType.ERROR_OCCURRED);
      expect(last).toBeUndefined();
    });

    it('should return undefined when store is empty', () => {
      expect(store.getLastEvent(DEFAULT_TENANT_ID, EngineEventType.ISSUE_SELECTED)).toBeUndefined();
    });
  });

  describe('clear', () => {
    it('should clear only the specified tenant events', () => {
      store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_A, data: {} });
      store.record({ type: EngineEventType.PLAN_GENERATED, tenantId: TENANT_A, data: {} });
      store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_B, data: {} });

      expect(store.getEvents(TENANT_A)).toHaveLength(2);
      expect(store.getEvents(TENANT_B)).toHaveLength(1);

      store.clear(TENANT_A);
      expect(store.getEvents(TENANT_A)).toHaveLength(0);
      expect(store.getEvents(TENANT_B)).toHaveLength(1);
    });
  });

  describe('ordering', () => {
    it('should retrieve events in the order they were recorded', () => {
      const types = [
        EngineEventType.ISSUE_SELECTED,
        EngineEventType.ISSUE_ANALYZED,
        EngineEventType.PLAN_GENERATED,
        EngineEventType.PLAN_APPROVED,
        EngineEventType.BRANCH_CREATED,
      ];

      for (const type of types) {
        store.record({ type, tenantId: DEFAULT_TENANT_ID, data: {} });
      }

      const events = store.getEvents(DEFAULT_TENANT_ID);
      expect(events).toHaveLength(5);
      expect(events.map((e) => e.type)).toEqual(types);
    });
  });

  // -------------------------------------------------------------------
  // Tenant isolation tests (Story 17-3)
  // -------------------------------------------------------------------

  describe('tenant isolation', () => {
    it('should isolate events between tenants', () => {
      store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_A, issueNumber: 1, data: {} });
      store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_B, issueNumber: 2, data: {} });
      store.record({ type: EngineEventType.PLAN_GENERATED, tenantId: TENANT_A, issueNumber: 1, data: {} });

      const tenantAEvents = store.getEvents(TENANT_A);
      expect(tenantAEvents).toHaveLength(2);
      expect(tenantAEvents.every((e) => e.tenantId === TENANT_A)).toBe(true);

      const tenantBEvents = store.getEvents(TENANT_B);
      expect(tenantBEvents).toHaveLength(1);
      expect(tenantBEvents.every((e) => e.tenantId === TENANT_B)).toBe(true);
    });

    it('should return zero results when querying wrong tenant', () => {
      store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_A, data: {} });
      expect(store.getEvents(TENANT_B)).toEqual([]);
    });

    it('should scope getEvents by tenant and issueNumber', () => {
      store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_A, issueNumber: 1, data: {} });
      store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_B, issueNumber: 1, data: {} });
      store.record({ type: EngineEventType.PLAN_GENERATED, tenantId: TENANT_A, issueNumber: 2, data: {} });

      const result = store.getEvents(TENANT_A, 1);
      expect(result).toHaveLength(1);
      expect(result[0]!.tenantId).toBe(TENANT_A);
      expect(result[0]!.issueNumber).toBe(1);
    });

    it('should scope getLastEvent by tenant', () => {
      store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_A, data: { a: true } });
      store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_B, data: { b: true } });

      const lastA = store.getLastEvent(TENANT_A, EngineEventType.ISSUE_SELECTED);
      expect(lastA).toBeDefined();
      expect(lastA!.data).toEqual({ a: true });

      const lastB = store.getLastEvent(TENANT_B, EngineEventType.ISSUE_SELECTED);
      expect(lastB).toBeDefined();
      expect(lastB!.data).toEqual({ b: true });
    });

    it('should return undefined for getLastEvent when tenant has no matching events', () => {
      store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_A, data: {} });
      expect(store.getLastEvent(TENANT_B, EngineEventType.ISSUE_SELECTED)).toBeUndefined();
    });

    it('should handle interleaved events from multiple tenants', () => {
      store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_A, data: {} });
      store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_B, data: {} });
      store.record({ type: EngineEventType.PLAN_GENERATED, tenantId: TENANT_A, data: {} });
      store.record({ type: EngineEventType.PLAN_GENERATED, tenantId: TENANT_B, data: {} });
      store.record({ type: EngineEventType.BRANCH_CREATED, tenantId: TENANT_A, data: {} });

      expect(store.getEvents(TENANT_A)).toHaveLength(3);
      expect(store.getEvents(TENANT_B)).toHaveLength(2);
    });

    it('should return empty array for empty tenant (no events)', () => {
      const nonexistentTenant = 'cccccccc-cccc-cccc-cccc-cccccccccccc';
      expect(store.getEvents(nonexistentTenant)).toEqual([]);
    });

    it('clear should only remove specified tenant events — other tenants unaffected', () => {
      store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_A, data: {} });
      store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: TENANT_B, data: {} });
      store.record({ type: EngineEventType.PLAN_GENERATED, tenantId: TENANT_A, data: {} });

      store.clear(TENANT_A);
      expect(store.getEvents(TENANT_A)).toHaveLength(0);
      expect(store.getEvents(TENANT_B)).toHaveLength(1);

      // Clear again (no-op)
      store.clear(TENANT_A);
      expect(store.getEvents(TENANT_B)).toHaveLength(1);
    });
  });
});
