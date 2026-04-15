/**
 * Tests for Prompt Store Event Sourcing
 *
 * Story 27-7: Prompt Store Event Sourcing
 */

import { describe, it, expect, vi } from 'vitest';
import type { PromptTemplate } from './default-prompts.js';
import {
  PROMPT_EVENT_TYPES,
  diffFields,
  emitPromptEvent,
} from './prompt-store-events.js';
import type { IPromptEventStore, PromptDomainEvent } from './prompt-store-events.js';

// ---------------------------------------------------------------------------
// Mock event store
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

function makeTemplate(overrides: Partial<PromptTemplate> = {}): PromptTemplate {
  return {
    role: 'developer',
    action: 'implement',
    version: 1,
    template: 'Hello {{name}}',
    variables: ['name'],
    systemPrompt: 'You are a developer.',
    enableTools: false,
    maxTokens: 4096,
    createdAt: '2026-01-01T00:00:00.000Z',
    updatedAt: '2026-01-01T00:00:00.000Z',
    ...overrides,
  };
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('PROMPT_EVENT_TYPES', () => {
  it('should have the correct event type constants', () => {
    expect(PROMPT_EVENT_TYPES.CREATED).toBe('PROMPT.CREATED.SUCCESS');
    expect(PROMPT_EVENT_TYPES.UPDATED).toBe('PROMPT.UPDATED.SUCCESS');
    expect(PROMPT_EVENT_TYPES.DELETED).toBe('PROMPT.DELETED.SUCCESS');
    expect(PROMPT_EVENT_TYPES.RESET).toBe('PROMPT.RESET.SUCCESS');
  });
});

describe('diffFields', () => {
  it('should return empty array when nothing changed', () => {
    const template = makeTemplate();
    expect(diffFields(template, template)).toEqual([]);
  });

  it('should detect template change', () => {
    const before = makeTemplate({ template: 'Hello {{name}}' });
    const after = makeTemplate({ template: 'Goodbye {{name}}' });
    expect(diffFields(before, after)).toEqual(['template']);
  });

  it('should detect systemPrompt change', () => {
    const before = makeTemplate({ systemPrompt: 'You are a developer.' });
    const after = makeTemplate({ systemPrompt: 'You are a senior developer.' });
    expect(diffFields(before, after)).toEqual(['systemPrompt']);
  });

  it('should detect enableTools change', () => {
    const before = makeTemplate({ enableTools: false });
    const after = makeTemplate({ enableTools: true });
    expect(diffFields(before, after)).toEqual(['enableTools']);
  });

  it('should detect maxTokens change', () => {
    const before = makeTemplate({ maxTokens: 4096 });
    const after = makeTemplate({ maxTokens: 8192 });
    expect(diffFields(before, after)).toEqual(['maxTokens']);
  });

  it('should detect variables change', () => {
    const before = makeTemplate({ variables: ['name'] });
    const after = makeTemplate({ variables: ['name', 'lang'] });
    expect(diffFields(before, after)).toEqual(['variables']);
  });

  it('should detect multiple changes', () => {
    const before = makeTemplate({
      template: 'Hello {{name}}',
      maxTokens: 4096,
      enableTools: false,
    });
    const after = makeTemplate({
      template: 'Goodbye {{name}}',
      maxTokens: 8192,
      enableTools: true,
    });
    expect(diffFields(before, after)).toEqual(['template', 'enableTools', 'maxTokens']);
  });
});

describe('emitPromptEvent', () => {
  it('should call eventStore.append with correct event structure', async () => {
    const store = createMockEventStore();

    await emitPromptEvent(
      store,
      PROMPT_EVENT_TYPES.CREATED,
      { tenantId: 'tenant-1', role: 'developer', action: 'implement', userId: 'user-1' },
      { version: 1, enableTools: false, maxTokens: 4096 },
    );

    expect(store.append).toHaveBeenCalledOnce();
    expect(store.events).toHaveLength(1);

    const event = store.events[0]!;
    expect(event.type).toBe('PROMPT.CREATED.SUCCESS');
    expect(event.tags).toEqual({
      tenantId: 'tenant-1',
      role: 'developer',
      action: 'implement',
      userId: 'user-1',
    });
    expect(event.metadata).toEqual({
      workflowVersion: '1.0.0',
      eventSource: 'system',
    });
    expect(event.data).toEqual({
      version: 1,
      enableTools: false,
      maxTokens: 4096,
    });
  });

  it('should include tenantId tag when provided', async () => {
    const store = createMockEventStore();

    await emitPromptEvent(
      store,
      PROMPT_EVENT_TYPES.UPDATED,
      { tenantId: 'acme-uuid', role: 'tester', action: 'write-tests' },
      { previousVersion: 1, newVersion: 2, changedFields: ['template'] },
    );

    const event = store.events[0]!;
    expect(event.tags['tenantId']).toBe('acme-uuid');
  });

  it('should omit tenantId tag when undefined (system default)', async () => {
    const store = createMockEventStore();

    await emitPromptEvent(
      store,
      PROMPT_EVENT_TYPES.UPDATED,
      { role: 'developer', action: 'plan' },
      { previousVersion: 1, newVersion: 2, changedFields: ['template'] },
    );

    const event = store.events[0]!;
    expect('tenantId' in event.tags).toBe(false);
  });

  it('should omit userId tag when undefined', async () => {
    const store = createMockEventStore();

    await emitPromptEvent(
      store,
      PROMPT_EVENT_TYPES.DELETED,
      { tenantId: 'tenant-1', role: 'developer', action: 'implement' },
      { deletedVersion: 3 },
    );

    const event = store.events[0]!;
    expect('userId' in event.tags).toBe(false);
  });

  it('should not throw when eventStore.append fails (best-effort)', async () => {
    const store: IPromptEventStore = {
      append: vi.fn().mockRejectedValue(new Error('DB connection lost')),
    };
    const logger = { warn: vi.fn() };

    // Should not throw
    await expect(
      emitPromptEvent(
        store,
        PROMPT_EVENT_TYPES.CREATED,
        { role: 'developer', action: 'plan' },
        { version: 1 },
        logger,
      ),
    ).resolves.toBeUndefined();

    // Should log a warning
    expect(logger.warn).toHaveBeenCalledOnce();
    expect(logger.warn.mock.calls[0]![1]).toBe('Failed to emit prompt event');
  });

  it('should not throw when eventStore.append fails even without logger', async () => {
    const store: IPromptEventStore = {
      append: vi.fn().mockRejectedValue(new Error('DB connection lost')),
    };

    // Should not throw even without logger
    await expect(
      emitPromptEvent(
        store,
        PROMPT_EVENT_TYPES.CREATED,
        { role: 'developer', action: 'plan' },
        { version: 1 },
      ),
    ).resolves.toBeUndefined();
  });
});

describe('InMemoryPromptStore event emission', () => {
  // These tests use InMemoryPromptStore with an event store to verify
  // end-to-end event emission from the store layer

  it('should emit PROMPT.CREATED.SUCCESS on first upsert', async () => {
    const { InMemoryPromptStore } = await import('./in-memory-prompt-store.js');

    const eventStore = createMockEventStore();
    const store = new InMemoryPromptStore({ skipDefaults: true, eventStore });

    await store.upsert(null, 'developer', 'implement', {
      template: 'Hello {{name}}',
    }, 'user-1');

    expect(eventStore.events).toHaveLength(1);
    const event = eventStore.events[0]!;
    expect(event.type).toBe('PROMPT.CREATED.SUCCESS');
    expect(event.data['version']).toBe(1);
    expect(event.tags['userId']).toBe('user-1');
  });

  it('should emit PROMPT.UPDATED.SUCCESS on subsequent upsert', async () => {
    const { InMemoryPromptStore } = await import('./in-memory-prompt-store.js');

    const eventStore = createMockEventStore();
    const store = new InMemoryPromptStore({ skipDefaults: true, eventStore });

    await store.upsert(null, 'developer', 'implement', {
      template: 'v1 {{name}}',
    });
    await store.upsert(null, 'developer', 'implement', {
      template: 'v2 {{name}}',
    });

    expect(eventStore.events).toHaveLength(2);
    expect(eventStore.events[0]!.type).toBe('PROMPT.CREATED.SUCCESS');
    expect(eventStore.events[1]!.type).toBe('PROMPT.UPDATED.SUCCESS');
    expect(eventStore.events[1]!.data['previousVersion']).toBe(1);
    expect(eventStore.events[1]!.data['newVersion']).toBe(2);
    expect(eventStore.events[1]!.data['changedFields']).toEqual(['template']);
  });

  it('should emit PROMPT.DELETED.SUCCESS on delete', async () => {
    const { InMemoryPromptStore } = await import('./in-memory-prompt-store.js');

    const eventStore = createMockEventStore();
    const store = new InMemoryPromptStore({ skipDefaults: true, eventStore });

    await store.upsert('tenant-1', 'developer', 'implement', {
      template: 'Hello {{name}}',
    });
    await store.delete('tenant-1', 'developer', 'implement', 'user-2');

    expect(eventStore.events).toHaveLength(2);
    const deleteEvent = eventStore.events[1]!;
    expect(deleteEvent.type).toBe('PROMPT.DELETED.SUCCESS');
    expect(deleteEvent.data['deletedVersion']).toBe(1);
    expect(deleteEvent.tags['tenantId']).toBe('tenant-1');
    expect(deleteEvent.tags['userId']).toBe('user-2');
  });

  it('should emit PROMPT.RESET.SUCCESS on resetSystemDefault', async () => {
    const { InMemoryPromptStore } = await import('./in-memory-prompt-store.js');

    const eventStore = createMockEventStore();
    const store = new InMemoryPromptStore({ skipDefaults: false, eventStore });

    // Modify a system default first
    await store.upsert(null, 'developer', 'context-scan', {
      template: 'Custom template',
    });
    const beforeEvents = eventStore.events.length;

    // Reset to hardcoded
    await store.resetSystemDefault('developer', 'context-scan', 'admin-user');

    const resetEvents = eventStore.events.slice(beforeEvents);
    const resetEvent = resetEvents.find(e => e.type === 'PROMPT.RESET.SUCCESS');
    expect(resetEvent).toBeDefined();
    expect(resetEvent!.data['resetFrom']).toBe('custom');
    expect(resetEvent!.data['resetTo']).toBe('hardcoded');
    expect(resetEvent!.tags['userId']).toBe('admin-user');
  });

  it('should work without event store (events skipped)', async () => {
    const { InMemoryPromptStore } = await import('./in-memory-prompt-store.js');

    // No event store configured
    const store = new InMemoryPromptStore({ skipDefaults: true });

    // Should not throw
    const result = await store.upsert(null, 'developer', 'implement', {
      template: 'Hello',
    });
    expect(result.version).toBe(1);

    const deleted = await store.delete('tenant-1', 'developer', 'implement');
    expect(deleted).toBe(false); // doesn't exist for that tenant
  });

  it('should include tenantId tag for tenant override events', async () => {
    const { InMemoryPromptStore } = await import('./in-memory-prompt-store.js');

    const eventStore = createMockEventStore();
    const store = new InMemoryPromptStore({ skipDefaults: true, eventStore });

    await store.upsert('tenant-abc', 'tester', 'write-tests', {
      template: 'Test {{feature}}',
    });

    const event = eventStore.events[0]!;
    expect(event.tags['tenantId']).toBe('tenant-abc');
    expect(event.tags['role']).toBe('tester');
    expect(event.tags['action']).toBe('write-tests');
  });
});
