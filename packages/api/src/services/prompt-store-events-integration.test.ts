/**
 * Integration Tests for Prompt Store Event Sourcing
 *
 * Uses InMemoryPromptStore with a mock event store to verify
 * end-to-end event emission across mutation operations.
 *
 * Story 27-7: Prompt Store Event Sourcing
 */

import { describe, it, expect, vi, beforeEach } from 'vitest';
import type { IPromptEventStore, PromptDomainEvent } from './prompt-store-events.js';
import { InMemoryPromptStore } from './in-memory-prompt-store.js';

// ---------------------------------------------------------------------------
// Mock event store that records all appended events
// ---------------------------------------------------------------------------

function createMockEventStore(): IPromptEventStore & { events: PromptDomainEvent[] } {
  const events: PromptDomainEvent[] = [];
  return {
    events,
    append: vi.fn(async (event: PromptDomainEvent) => {
      events.push(event);
    }),
  };
}

// ---------------------------------------------------------------------------
// Integration Tests
// ---------------------------------------------------------------------------

describe('Prompt Store Event Sourcing — Integration', () => {
  let eventStore: ReturnType<typeof createMockEventStore>;
  let store: InMemoryPromptStore;

  beforeEach(() => {
    eventStore = createMockEventStore();
    store = new InMemoryPromptStore({ skipDefaults: true, eventStore });
  });

  // -------------------------------------------------------------------------
  // Test 1: upsert() new prompt emits PROMPT.CREATED.SUCCESS
  // -------------------------------------------------------------------------

  it('should emit PROMPT.CREATED.SUCCESS on first upsert', async () => {
    await store.upsert(null, 'developer', 'implement', {
      template: 'Hello {{name}}',
    }, 'user-1');

    expect(eventStore.events).toHaveLength(1);
    const event = eventStore.events[0]!;
    expect(event.type).toBe('PROMPT.CREATED.SUCCESS');
    expect(event.data['version']).toBe(1);
    expect(event.tags['userId']).toBe('user-1');
    expect(event.tags['role']).toBe('developer');
    expect(event.tags['action']).toBe('implement');
  });

  // -------------------------------------------------------------------------
  // Test 2: upsert() existing prompt emits PROMPT.UPDATED.SUCCESS
  // -------------------------------------------------------------------------

  it('should emit PROMPT.UPDATED.SUCCESS on update with changedFields', async () => {
    await store.upsert(null, 'developer', 'implement', {
      template: 'v1 {{name}}',
    });
    await store.upsert(null, 'developer', 'implement', {
      template: 'v2 {{name}}',
      maxTokens: 8192,
    });

    expect(eventStore.events).toHaveLength(2);
    const updateEvent = eventStore.events[1]!;
    expect(updateEvent.type).toBe('PROMPT.UPDATED.SUCCESS');
    expect(updateEvent.data['previousVersion']).toBe(1);
    expect(updateEvent.data['newVersion']).toBe(2);
    const changedFields = updateEvent.data['changedFields'] as string[];
    expect(changedFields).toContain('template');
    expect(changedFields).toContain('maxTokens');
  });

  // -------------------------------------------------------------------------
  // Test 3: upsert() with no actual changes still emits (version bumps)
  // -------------------------------------------------------------------------

  it('should emit PROMPT.UPDATED.SUCCESS even with only version bump', async () => {
    await store.upsert(null, 'developer', 'plan', {
      template: 'Plan {{task}}',
      maxTokens: 4096,
      enableTools: false,
    });
    // Re-upsert same content — version still bumps but template is the same
    await store.upsert(null, 'developer', 'plan', {
      template: 'Plan {{task}}',
      maxTokens: 4096,
      enableTools: false,
    });

    expect(eventStore.events).toHaveLength(2);
    const updateEvent = eventStore.events[1]!;
    expect(updateEvent.type).toBe('PROMPT.UPDATED.SUCCESS');
    // changedFields should be empty since nothing actually changed
    expect(updateEvent.data['changedFields']).toEqual([]);
  });

  // -------------------------------------------------------------------------
  // Test 4: delete() emits PROMPT.DELETED.SUCCESS
  // -------------------------------------------------------------------------

  it('should emit PROMPT.DELETED.SUCCESS on delete', async () => {
    await store.upsert('tenant-1', 'developer', 'implement', {
      template: 'Hello {{name}}',
    });
    const beforeCount = eventStore.events.length;

    await store.delete('tenant-1', 'developer', 'implement', 'admin-user');

    const deleteEvents = eventStore.events.slice(beforeCount);
    expect(deleteEvents).toHaveLength(1);
    const deleteEvent = deleteEvents[0]!;
    expect(deleteEvent.type).toBe('PROMPT.DELETED.SUCCESS');
    expect(deleteEvent.data['deletedVersion']).toBe(1);
    expect(deleteEvent.tags['tenantId']).toBe('tenant-1');
    expect(deleteEvent.tags['userId']).toBe('admin-user');
  });

  // -------------------------------------------------------------------------
  // Test 5: resetSystemDefault() emits PROMPT.RESET.SUCCESS
  // -------------------------------------------------------------------------

  it('should emit PROMPT.RESET.SUCCESS on resetSystemDefault', async () => {
    // Use store with defaults so we have something to reset
    const storeWithDefaults = new InMemoryPromptStore({ skipDefaults: false, eventStore });

    // Modify a system default
    await storeWithDefaults.upsert(null, 'developer', 'context-scan', {
      template: 'Custom override {{x}}',
    });
    const beforeCount = eventStore.events.length;

    // Reset to hardcoded
    await storeWithDefaults.resetSystemDefault('developer', 'context-scan', 'admin-user');

    const resetEvents = eventStore.events.slice(beforeCount);
    const resetEvent = resetEvents.find((e) => e.type === 'PROMPT.RESET.SUCCESS');
    expect(resetEvent).toBeDefined();
    expect(resetEvent!.data['resetFrom']).toBe('custom');
    expect(resetEvent!.data['resetTo']).toBe('hardcoded');
    expect(resetEvent!.tags['userId']).toBe('admin-user');
    expect(resetEvent!.tags['role']).toBe('developer');
    expect(resetEvent!.tags['action']).toBe('context-scan');
  });

  // -------------------------------------------------------------------------
  // Test 6: Events are queryable by tenantId tag
  // -------------------------------------------------------------------------

  it('should include tenantId tag for tenant-scoped events', async () => {
    await store.upsert('acme-org', 'tester', 'write-tests', {
      template: 'Test {{feature}}',
    });

    const event = eventStore.events[0]!;
    expect(event.tags['tenantId']).toBe('acme-org');
    expect(event.tags['role']).toBe('tester');
    expect(event.tags['action']).toBe('write-tests');
  });

  it('should omit tenantId tag for system default events', async () => {
    await store.upsert(null, 'developer', 'plan', {
      template: 'Plan {{task}}',
    });

    const event = eventStore.events[0]!;
    expect('tenantId' in event.tags).toBe(false);
  });

  // -------------------------------------------------------------------------
  // Test 7: Events are queryable by role + action tags
  // -------------------------------------------------------------------------

  it('should tag events with role and action for filtering', async () => {
    await store.upsert(null, 'architect', 'plan', {
      template: 'Architect plan {{spec}}',
    });
    await store.upsert(null, 'tester', 'write-tests', {
      template: 'Write tests {{suite}}',
    });

    expect(eventStore.events).toHaveLength(2);

    // Filter by role
    const architectEvents = eventStore.events.filter((e) => e.tags['role'] === 'architect');
    expect(architectEvents).toHaveLength(1);
    expect(architectEvents[0]!.tags['action']).toBe('plan');

    // Filter by action
    const testEvents = eventStore.events.filter((e) => e.tags['action'] === 'write-tests');
    expect(testEvents).toHaveLength(1);
    expect(testEvents[0]!.tags['role']).toBe('tester');
  });

  // -------------------------------------------------------------------------
  // Event store failure does not block mutations
  // -------------------------------------------------------------------------

  it('should not block mutation when event store fails', async () => {
    const failingStore: IPromptEventStore = {
      append: vi.fn().mockRejectedValue(new Error('Event store unavailable')),
    };
    const logger = {
      info: vi.fn(),
      warn: vi.fn(),
      error: vi.fn(),
    };
    const storeWithFailingEvents = new InMemoryPromptStore({
      skipDefaults: true,
      eventStore: failingStore,
      logger,
    });

    // Should not throw even though event emission fails
    const result = await storeWithFailingEvents.upsert(null, 'developer', 'implement', {
      template: 'Hello',
    });

    expect(result.version).toBe(1);
    expect(result.template).toBe('Hello');
    // Logger should have been called with a warning
    expect(logger.warn).toHaveBeenCalled();
  });

  // -------------------------------------------------------------------------
  // No event store configured — silently skips
  // -------------------------------------------------------------------------

  it('should work without event store (events silently skipped)', async () => {
    const storeNoEvents = new InMemoryPromptStore({ skipDefaults: true });

    const result = await storeNoEvents.upsert(null, 'developer', 'implement', {
      template: 'Hello',
    });
    expect(result.version).toBe(1);

    // No errors, no events
    const deleted = await storeNoEvents.delete('tenant-1', 'developer', 'implement');
    expect(deleted).toBe(false);
  });
});
