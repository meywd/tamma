/**
 * Prompt Store Event Sourcing
 *
 * DCB event emission for prompt mutations.
 * Events follow the AGGREGATE.ACTION.STATUS naming pattern.
 *
 * Event emission is best-effort: if the event store is unavailable,
 * the prompt mutation still succeeds (log a warning, do not throw).
 *
 * Story 27-7: Prompt Store Event Sourcing
 */

import type { PromptTemplate } from './default-prompts.js';

// ---------------------------------------------------------------------------
// Event Types
// ---------------------------------------------------------------------------

export const PROMPT_EVENT_TYPES = {
  CREATED: 'PROMPT.CREATED.SUCCESS',
  UPDATED: 'PROMPT.UPDATED.SUCCESS',
  DELETED: 'PROMPT.DELETED.SUCCESS',
  RESET: 'PROMPT.RESET.SUCCESS',
} as const;

export type PromptEventType = typeof PROMPT_EVENT_TYPES[keyof typeof PROMPT_EVENT_TYPES];

// ---------------------------------------------------------------------------
// Event Store Interface (minimal — compatible with any IEventStore)
// ---------------------------------------------------------------------------

/**
 * Minimal event store interface for prompt events.
 * Compatible with the full IEventStore from @tamma/shared but only
 * requires the append/record capability.
 */
export interface IPromptEventStore {
  append(event: PromptDomainEvent): Promise<void>;
}

export interface PromptDomainEvent {
  type: string;
  tags: Record<string, string | undefined>;
  metadata: {
    workflowVersion: string;
    eventSource: 'system' | 'plugin';
  };
  data: Record<string, unknown>;
}

// ---------------------------------------------------------------------------
// Event Tags
// ---------------------------------------------------------------------------

export interface PromptEventTags {
  tenantId?: string | undefined;
  role: string;
  action: string;
  userId?: string | undefined;
}

// ---------------------------------------------------------------------------
// Logger Interface
// ---------------------------------------------------------------------------

interface LoggerLike {
  warn: (obj: object, msg: string) => void;
}

// ---------------------------------------------------------------------------
// diffFields — compute which fields changed between two template versions
// ---------------------------------------------------------------------------

/**
 * Compare two PromptTemplate objects and return the list of changed field names.
 * Only checks mutable fields (not role, action, createdAt).
 */
export function diffFields(before: PromptTemplate, after: PromptTemplate): string[] {
  const fields: string[] = [];
  if (before.template !== after.template) fields.push('template');
  if (before.systemPrompt !== after.systemPrompt) fields.push('systemPrompt');
  if (before.enableTools !== after.enableTools) fields.push('enableTools');
  if (before.maxTokens !== after.maxTokens) fields.push('maxTokens');
  if (JSON.stringify(before.variables) !== JSON.stringify(after.variables)) fields.push('variables');
  return fields;
}

// ---------------------------------------------------------------------------
// emitPromptEvent — best-effort event emission
// ---------------------------------------------------------------------------

/**
 * Emit a prompt DCB event. Catches and logs any errors (best-effort).
 *
 * @param eventStore - The event store to append to
 * @param type - Event type (e.g., PROMPT.CREATED.SUCCESS)
 * @param tags - Event tags (tenantId, role, action, userId)
 * @param data - Event data payload
 * @param logger - Optional logger for warning on failure
 */
export async function emitPromptEvent(
  eventStore: IPromptEventStore,
  type: string,
  tags: PromptEventTags,
  data: Record<string, unknown>,
  logger?: LoggerLike,
): Promise<void> {
  try {
    await eventStore.append({
      type,
      tags: {
        ...(tags.tenantId !== undefined ? { tenantId: tags.tenantId } : {}),
        role: tags.role,
        action: tags.action,
        ...(tags.userId !== undefined ? { userId: tags.userId } : {}),
      },
      metadata: {
        workflowVersion: '1.0.0',
        eventSource: 'system',
      },
      data,
    });
  } catch (error) {
    // Best-effort: log the failure but do not block the mutation
    logger?.warn(
      { error: error instanceof Error ? error.message : String(error), type, tags },
      'Failed to emit prompt event',
    );
  }
}
