/**
 * Server-side event publisher for real-time collaboration
 */

import type { AppLoadContext } from 'react-router';
import type { EventMessage } from './event-broadcaster';

export type EventType =
  | 'comment.created'
  | 'comment.updated'
  | 'comment.deleted'
  | 'suggestion.created'
  | 'suggestion.updated'
  | 'suggestion.approved'
  | 'discussion.created'
  | 'discussion.message'
  | 'user.presence';

export interface PublishOptions {
  docPath: string;
  type: EventType;
  data: any;
  userId?: string;
}

/**
 * Get the Durable Object namespace for event broadcasting
 */
function getEventBroadcasterNamespace(context: AppLoadContext): DurableObjectNamespace {
  const cloudflare = context.cloudflare as { env: any };
  const env = cloudflare.env;

  if (!env.EVENT_BROADCASTER) {
    throw new Error('EVENT_BROADCASTER Durable Object namespace not configured');
  }

  return env.EVENT_BROADCASTER;
}

/**
 * Get or create a Durable Object ID for a document
 */
function getDurableObjectId(
  namespace: DurableObjectNamespace,
  docPath: string
): DurableObjectId {
  // Use document path as the unique identifier
  // This ensures all events for the same document go to the same DO instance
  return namespace.idFromName(docPath);
}

/**
 * Publish an event to the event stream
 */
export async function publishEvent(
  context: AppLoadContext,
  options: PublishOptions
): Promise<void> {
  try {
    const namespace = getEventBroadcasterNamespace(context);
    const id = getDurableObjectId(namespace, options.docPath);
    const stub = namespace.get(id);

    const event: EventMessage = {
      type: options.type,
      docPath: options.docPath,
      data: options.data,
      timestamp: Date.now(),
      userId: options.userId
    };

    const response = await stub.fetch('https://internal/publish', {
      method: 'POST',
      body: JSON.stringify(event),
      headers: {
        'Content-Type': 'application/json'
      }
    });

    if (!response.ok) {
      const error = await response.text();
      throw new Error(`Failed to publish event: ${error}`);
    }
  } catch (error) {
    console.error('Error publishing event:', error);
    // Don't throw - we don't want event publishing failures to break the main flow
    // Real-time updates are nice to have but not critical
  }
}

/**
 * Subscribe to events for a document (used by SSE endpoint)
 */
export async function subscribeToEvents(
  context: AppLoadContext,
  docPath: string,
  userId: string
): Promise<Response> {
  const namespace = getEventBroadcasterNamespace(context);
  const id = getDurableObjectId(namespace, docPath);
  const stub = namespace.get(id);

  // Forward the SSE request to the Durable Object
  return stub.fetch(`https://internal/sse?userId=${encodeURIComponent(userId)}`, {
    method: 'GET'
  });
}

/**
 * Get recent events for a document (for catch-up on reconnect)
 */
export async function getRecentEvents(
  context: AppLoadContext,
  docPath: string
): Promise<EventMessage[]> {
  try {
    const namespace = getEventBroadcasterNamespace(context);
    const id = getDurableObjectId(namespace, docPath);
    const stub = namespace.get(id);

    const response = await stub.fetch('https://internal/recent', {
      method: 'GET'
    });

    if (!response.ok) {
      throw new Error('Failed to get recent events');
    }

    return response.json();
  } catch (error) {
    console.error('Error getting recent events:', error);
    return [];
  }
}

/**
 * Helper function to publish a comment event
 */
export async function publishCommentEvent(
  context: AppLoadContext,
  docPath: string,
  type: 'created' | 'updated' | 'deleted',
  comment: any,
  userId: string
): Promise<void> {
  await publishEvent(context, {
    docPath,
    type: `comment.${type}`,
    data: type === 'deleted' ? { id: comment.id || comment } : comment,
    userId
  });
}

/**
 * Helper function to publish a suggestion event
 */
export async function publishSuggestionEvent(
  context: AppLoadContext,
  docPath: string,
  type: 'created' | 'updated' | 'approved',
  suggestion: any,
  userId: string
): Promise<void> {
  await publishEvent(context, {
    docPath,
    type: `suggestion.${type}`,
    data: suggestion,
    userId
  });
}

/**
 * Helper function to publish a discussion event
 */
export async function publishDiscussionEvent(
  context: AppLoadContext,
  docPath: string,
  type: 'created' | 'message',
  data: any,
  userId: string
): Promise<void> {
  await publishEvent(context, {
    docPath,
    type: `discussion.${type}`,
    data,
    userId
  });
}

/**
 * Helper function to publish a user presence event
 */
export async function publishPresenceEvent(
  context: AppLoadContext,
  docPath: string,
  userId: string,
  status: 'online' | 'offline'
): Promise<void> {
  await publishEvent(context, {
    docPath,
    type: 'user.presence',
    data: { userId, status, timestamp: Date.now() },
    userId
  });
}