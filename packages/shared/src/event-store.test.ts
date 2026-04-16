import { describe, it, expect, beforeEach } from 'vitest';
import { InMemoryEventStore } from './event-store.js';
import { EngineEventType } from './types/index.js';
import { DEFAULT_TENANT_ID } from './types/tenant.js';

const T = DEFAULT_TENANT_ID; // shorthand for default tenant in tests

describe('InMemoryEventStore', () => {
  let store: InMemoryEventStore;

  beforeEach(() => {
    store = new InMemoryEventStore();
  });

  describe('record', () => {
    it('should create events with id and timestamp', async () => {
      const event = await store.record({
        type: EngineEventType.ISSUE_SELECTED,
        tenantId: T,
        issueNumber: 42,
        data: { title: 'Fix bug' },
      });

      expect(event.id).toBeDefined();
      expect(typeof event.id).toBe('string');
      expect(event.id.length).toBeGreaterThan(0);
      expect(event.timestamp).toBeDefined();
      expect(typeof event.timestamp).toBe('number');
      expect(event.type).toBe(EngineEventType.ISSUE_SELECTED);
      expect(event.tenantId).toBe(T);
      expect(event.issueNumber).toBe(42);
      expect(event.data).toEqual({ title: 'Fix bug' });
    });

    it('should assign unique ids to each event', async () => {
      const e1 = await store.record({
        type: EngineEventType.ISSUE_SELECTED,
        tenantId: T,
        data: {},
      });
      const e2 = await store.record({
        type: EngineEventType.ISSUE_ANALYZED,
        tenantId: T,
        data: {},
      });

      expect(e1.id).not.toBe(e2.id);
    });
  });

  describe('getEvents', () => {
    it('should return all events for the tenant when no issueNumber is provided', async () => {
      await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: T, issueNumber: 1, data: {} });
      await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: T, issueNumber: 2, data: {} });
      await store.record({ type: EngineEventType.PLAN_GENERATED, tenantId: T, data: {} });

      const events = await store.getEvents(T);
      expect(events).toHaveLength(3);
    });

    it('should return empty array when store is empty', async () => {
      expect(await store.getEvents(T)).toEqual([]);
    });

    it('should filter by issueNumber', async () => {
      await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: T, issueNumber: 1, data: {} });
      await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: T, issueNumber: 2, data: {} });
      await store.record({ type: EngineEventType.PLAN_GENERATED, tenantId: T, issueNumber: 1, data: {} });

      const events = await store.getEvents(T, 1);
      expect(events).toHaveLength(2);
      expect(events.every((e) => e.issueNumber === 1)).toBe(true);
    });

    it('should return empty array when no events match issueNumber', async () => {
      await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: T, issueNumber: 1, data: {} });
      expect(await store.getEvents(T, 999)).toEqual([]);
    });

    it('should return a copy of events (not a reference)', async () => {
      await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: T, data: {} });
      const events = await store.getEvents(T);
      events.pop();
      expect(await store.getEvents(T)).toHaveLength(1);
    });
  });

  describe('getLastEvent', () => {
    it('should return the most recent event of the given type', async () => {
      await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: T, issueNumber: 1, data: { first: true } });
      await store.record({ type: EngineEventType.PLAN_GENERATED, tenantId: T, data: {} });
      await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: T, issueNumber: 2, data: { second: true } });

      const last = await store.getLastEvent(T, EngineEventType.ISSUE_SELECTED);
      expect(last).toBeDefined();
      expect(last!.issueNumber).toBe(2);
      expect(last!.data).toEqual({ second: true });
    });

    it('should return undefined when no matching events exist', async () => {
      await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: T, data: {} });
      const last = await store.getLastEvent(T, EngineEventType.ERROR_OCCURRED);
      expect(last).toBeUndefined();
    });

    it('should return undefined when store is empty', async () => {
      expect(await store.getLastEvent(T, EngineEventType.ISSUE_SELECTED)).toBeUndefined();
    });
  });

  describe('clear', () => {
    it('should remove only the specified tenant events', async () => {
      await store.record({ type: EngineEventType.ISSUE_SELECTED, tenantId: T, data: {} });
      await store.record({ type: EngineEventType.PLAN_GENERATED, tenantId: T, data: {} });
      expect(await store.getEvents(T)).toHaveLength(2);

      await store.clear(T);
      expect(await store.getEvents(T)).toHaveLength(0);
    });
  });

  describe('ordering', () => {
    it('should retrieve events in the order they were recorded', async () => {
      const types = [
        EngineEventType.ISSUE_SELECTED,
        EngineEventType.ISSUE_ANALYZED,
        EngineEventType.PLAN_GENERATED,
        EngineEventType.PLAN_APPROVED,
        EngineEventType.BRANCH_CREATED,
      ];

      for (const type of types) {
        await store.record({ type, tenantId: T, data: {} });
      }

      const events = await store.getEvents(T);
      expect(events).toHaveLength(5);
      expect(events.map((e) => e.type)).toEqual(types);
    });
  });
});
