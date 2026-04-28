import { and, count, desc, eq, isNull, sql } from 'drizzle-orm';
import { requireAuthWithRole } from '~/lib/auth/middleware';
import { Permission, hasPermission, logPermissionViolation } from '~/lib/auth/permissions';
import { getDb, hasDatabase } from '~/lib/db/client.server';
import { discussions, discussionMessages, reviewSessions, users } from '~/lib/db/schema';
import { syncUserRecord } from '~/lib/db/users.server';
import {
  validateDiscussionCreatePayload,
  ValidationError
} from '~/lib/collaboration/validators';
import { jsonResponse } from '~/lib/utils/responses';
import { parseRequestPayload } from '~/lib/utils/request.server';
import { publishDiscussionEvent } from '~/lib/events/publisher.server';

// GET /api/discussions - List discussions with filtering
export async function loader({ request, context }: any) {
  const env = context.env ?? context.cloudflare?.env ?? {};
  // Anyone authenticated can read discussions
  await requireAuthWithRole(request, context);

  if (!hasDatabase(env)) {
    return jsonResponse({ error: 'Database not configured.' }, { status: 503 });
  }

  const url = new URL(request.url);
  const db = getDb(env);

  const docPath = url.searchParams.get('docPath');
  const status = url.searchParams.get('status');
  const sessionId = url.searchParams.get('sessionId');
  const userId = url.searchParams.get('userId');
  const limit = Math.min(parseInt(url.searchParams.get('limit') || '50'), 50);
  const offset = parseInt(url.searchParams.get('offset') || '0');

  // Build dynamic query with filters
  const conditions = [isNull(discussions.deletedAt)];

  if (docPath) {
    conditions.push(eq(discussions.docPath, docPath));
  }
  if (status && ['open', 'resolved', 'closed'].includes(status)) {
    conditions.push(eq(discussions.status, status));
  }
  if (sessionId) {
    conditions.push(eq(discussions.sessionId, sessionId));
  }
  if (userId) {
    conditions.push(eq(discussions.userId, userId));
  }

  // Get discussions with author info and message count
  const query = db
    .select({
      id: discussions.id,
      docPath: discussions.docPath,
      title: discussions.title,
      description: discussions.description,
      status: discussions.status,
      userId: discussions.userId,
      sessionId: discussions.sessionId,
      createdAt: discussions.createdAt,
      updatedAt: discussions.updatedAt,
      author: {
        id: users.id,
        name: users.name,
        avatarUrl: users.avatarUrl,
      },
      session: sessionId ? {
        id: reviewSessions.id,
        title: reviewSessions.title,
        status: reviewSessions.status,
        prNumber: reviewSessions.prNumber,
        prUrl: reviewSessions.prUrl,
      } : sql`NULL`,
      messageCount: sql<number>`
        (SELECT COUNT(*)
         FROM ${discussionMessages}
         WHERE ${discussionMessages.discussionId} = ${discussions.id}
         AND ${discussionMessages.deletedAt} IS NULL)
      `.as('messageCount'),
    })
    .from(discussions)
    .leftJoin(users, eq(users.id, discussions.userId))
    .where(and(...conditions))
    .limit(limit)
    .offset(offset)
    .orderBy(desc(discussions.createdAt));

  if (sessionId) {
    query.leftJoin(reviewSessions, eq(reviewSessions.id, discussions.sessionId));
  }

  try {
    const results = await query.all();

    // Get total count for pagination
    const totalQuery = await db
      .select({ count: count() })
      .from(discussions)
      .where(and(...conditions))
      .get();

    return jsonResponse({
      discussions: results,
      pagination: {
        total: totalQuery?.count || 0,
        limit,
        offset,
      },
    });
  } catch (error) {
    console.error('Failed to fetch discussions:', error);
    return jsonResponse({ error: 'Failed to fetch discussions.' }, { status: 500 });
  }
}

// POST /api/discussions - Create a new discussion
export async function action({ request, context }: any) {
  if (request.method !== 'POST') {
    return new Response('Method not allowed', { status: 405 });
  }

  const env = context.env ?? context.cloudflare?.env ?? {};
  const user = await requireAuthWithRole(request, context);

  // Check if user has permission to create discussions (comment permission)
  if (!hasPermission(user, Permission.COMMENT)) {
    logPermissionViolation(user, Permission.COMMENT, {
      action: 'create_discussion',
      endpoint: '/api/discussions',
    });
    return jsonResponse(
      {
        error: 'Forbidden',
        message: 'You need reviewer role or higher to create discussions',
        requiredPermission: Permission.COMMENT,
      },
      { status: 403 }
    );
  }

  if (!hasDatabase(env)) {
    return jsonResponse({ error: 'Database not configured.' }, { status: 503 });
  }

  const db = getDb(env);
  const body = await parseRequestPayload(request);

  try {
    const payload = validateDiscussionCreatePayload(body);
    await syncUserRecord(env, user);

    // If sessionId is provided, verify it exists and document is part of it
    if (payload.sessionId) {
      const session = await db
        .select()
        .from(reviewSessions)
        .where(eq(reviewSessions.id, payload.sessionId))
        .get();

      if (!session) {
        return jsonResponse({ error: 'Review session not found.' }, { status: 404 });
      }

      // Parse docPaths from JSON string
      const docPaths = JSON.parse(session.docPaths || '[]');
      if (!docPaths.includes(payload.docPath)) {
        return jsonResponse({ error: 'Document is not part of the selected session.' }, { status: 400 });
      }
    }

    const id = crypto.randomUUID();
    const now = Date.now();

    // Create the discussion
    await db.insert(discussions).values({
      id,
      docPath: payload.docPath,
      title: payload.title,
      description: payload.description || null,
      status: 'open',
      userId: user.id,
      sessionId: payload.sessionId || null,
      createdAt: now,
      updatedAt: now,
    });

    // Get the created discussion with author info
    const created = await db
      .select({
        id: discussions.id,
        docPath: discussions.docPath,
        title: discussions.title,
        description: discussions.description,
        status: discussions.status,
        userId: discussions.userId,
        sessionId: discussions.sessionId,
        createdAt: discussions.createdAt,
        updatedAt: discussions.updatedAt,
        author: {
          id: users.id,
          name: users.name,
          avatarUrl: users.avatarUrl,
        },
      })
      .from(discussions)
      .leftJoin(users, eq(users.id, discussions.userId))
      .where(eq(discussions.id, id))
      .get();

    // Publish real-time event
    await publishDiscussionEvent(context, payload.docPath, 'created', created, user.id);

    return jsonResponse({ discussion: created }, { status: 201 });
  } catch (error) {
    if (error instanceof ValidationError) {
      return jsonResponse({ error: error.message }, { status: 400 });
    }

    console.error('Failed to create discussion:', error);
    return jsonResponse({ error: 'Failed to create discussion.' }, { status: 500 });
  }
}