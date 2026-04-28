import { drizzle } from 'drizzle-orm/d1';
import { eq, and, lt, isNull, desc, sql, or } from 'drizzle-orm';
import { sqliteTable, text, integer } from 'drizzle-orm/sqlite-core';
import { createEmailService } from './service.server';

// Email queue schema
export const emailQueue = sqliteTable('email_queue', {
  id: text('id').primaryKey(),
  to: text('to').notNull(),
  subject: text('subject').notNull(),
  html: text('html').notNull(),
  text: text('text'),
  userId: text('user_id'),
  type: text('type').notNull(), // comment_notification, suggestion_notification, review_request, digest
  status: text('status').notNull().default('pending'), // pending, processing, sent, failed
  attempts: integer('attempts').notNull().default(0),
  lastAttemptAt: integer('last_attempt_at', { mode: 'number' }),
  sentAt: integer('sent_at', { mode: 'number' }),
  failedAt: integer('failed_at', { mode: 'number' }),
  error: text('error'),
  metadata: text('metadata'), // JSON string for additional data
  createdAt: integer('created_at', { mode: 'number' }).notNull(),
  scheduledFor: integer('scheduled_for', { mode: 'number' }),
});

// Email log schema for sent emails
export const emailLog = sqliteTable('email_log', {
  id: text('id').primaryKey(),
  queueId: text('queue_id').notNull(),
  to: text('to').notNull(),
  subject: text('subject').notNull(),
  type: text('type').notNull(),
  userId: text('user_id'),
  sentAt: integer('sent_at', { mode: 'number' }).notNull(),
  resendId: text('resend_id'), // Resend's email ID for tracking
  metadata: text('metadata'),
});

export interface QueuedEmail {
  to: string;
  subject: string;
  html: string;
  text?: string;
  userId?: string;
  type: 'comment_notification' | 'suggestion_notification' | 'review_request' | 'digest' | 'other';
  metadata?: Record<string, any>;
  scheduledFor?: Date;
}

export interface EmailQueueRecord {
  id: string;
  to: string;
  subject: string;
  html: string;
  text: string | null;
  userId: string | null;
  type: string;
  status: string;
  attempts: number;
  lastAttemptAt: number | null;
  sentAt: number | null;
  failedAt: number | null;
  error: string | null;
  metadata: string | null;
  createdAt: number;
  scheduledFor: number | null;
}

// Queue an email for sending
export async function queueEmail(email: QueuedEmail, db: D1Database): Promise<string> {
  const id = crypto.randomUUID();
  const now = Date.now();

  const dbClient = drizzle(db);

  await dbClient.insert(emailQueue).values({
    id,
    to: email.to,
    subject: email.subject,
    html: email.html,
    text: email.text || null,
    userId: email.userId || null,
    type: email.type,
    status: 'pending',
    attempts: 0,
    metadata: email.metadata ? JSON.stringify(email.metadata) : null,
    createdAt: now,
    scheduledFor: email.scheduledFor ? email.scheduledFor.getTime() : null,
  });

  return id;
}

// Process email queue
export async function processEmailQueue(
  db: D1Database,
  env: any,
  options: {
    batchSize?: number;
    maxAttempts?: number;
  } = {}
): Promise<{ processed: number; failed: number }> {
  const { batchSize = 10, maxAttempts = 3 } = options;
  const dbClient = drizzle(db);
  const now = Date.now();

  // Get pending emails that are due to be sent
  const pendingEmails = await dbClient
    .select()
    .from(emailQueue)
    .where(
      and(
        eq(emailQueue.status, 'pending'),
        lt(emailQueue.attempts, maxAttempts),
        or(
          isNull(emailQueue.scheduledFor),
          lt(emailQueue.scheduledFor, now)
        )
      )
    )
    .limit(batchSize);

  const emailService = createEmailService(env);
  let processed = 0;
  let failed = 0;

  for (const email of pendingEmails) {
    try {
      // Update status to processing
      await dbClient
        .update(emailQueue)
        .set({
          status: 'processing',
          lastAttemptAt: now,
        })
        .where(eq(emailQueue.id, email.id));

      // Send the email
      await emailService.sendEmail({
        to: email.to,
        subject: email.subject,
        html: email.html,
        text: email.text || undefined,
      });

      // Mark as sent
      await dbClient
        .update(emailQueue)
        .set({
          status: 'sent',
          sentAt: now,
          attempts: email.attempts + 1,
        })
        .where(eq(emailQueue.id, email.id));

      // Log the sent email
      await dbClient.insert(emailLog).values({
        id: crypto.randomUUID(),
        queueId: email.id,
        to: email.to,
        subject: email.subject,
        type: email.type,
        userId: email.userId,
        sentAt: now,
        metadata: email.metadata,
      });

      processed++;
    } catch (error: any) {
      failed++;

      const attempts = email.attempts + 1;
      const isFinalAttempt = attempts >= maxAttempts;

      // Update with error and potentially mark as failed
      await dbClient
        .update(emailQueue)
        .set({
          status: isFinalAttempt ? 'failed' : 'pending',
          attempts,
          lastAttemptAt: now,
          failedAt: isFinalAttempt ? now : null,
          error: error.message || 'Unknown error',
        })
        .where(eq(emailQueue.id, email.id));

      console.error(`Failed to send email ${email.id}:`, error);
    }
  }

  return { processed, failed };
}

// Retry failed emails with exponential backoff
export async function retryFailedEmails(
  db: D1Database,
  _env: any,
  options: {
    maxRetries?: number;
    baseDelay?: number; // in milliseconds
  } = {}
): Promise<{ retried: number }> {
  const { maxRetries = 3, baseDelay = 60000 } = options; // Default 1 minute base delay
  const dbClient = drizzle(db);
  const now = Date.now();

  // Get failed emails that haven't exceeded max retries
  const failedEmails = await dbClient
    .select()
    .from(emailQueue)
    .where(
      and(
        eq(emailQueue.status, 'failed'),
        lt(emailQueue.attempts, maxRetries)
      )
    );

  let retried = 0;

  for (const email of failedEmails) {
    // Calculate delay with exponential backoff
    const delay = baseDelay * Math.pow(2, email.attempts);
    const nextAttemptTime = (email.lastAttemptAt || email.createdAt) + delay;

    if (now >= nextAttemptTime) {
      // Reset to pending for retry
      await dbClient
        .update(emailQueue)
        .set({
          status: 'pending',
          error: null,
          failedAt: null,
        })
        .where(eq(emailQueue.id, email.id));

      retried++;
    }
  }

  return { retried };
}

// Get queue statistics
export async function getQueueStats(db: D1Database): Promise<{
  pending: number;
  processing: number;
  sent: number;
  failed: number;
  total: number;
}> {
  const dbClient = drizzle(db);

  const stats = await dbClient
    .select({
      status: emailQueue.status,
      count: sql<number>`count(*)`,
    })
    .from(emailQueue)
    .groupBy(emailQueue.status);

  const result = {
    pending: 0,
    processing: 0,
    sent: 0,
    failed: 0,
    total: 0,
  };

  for (const stat of stats) {
    const count = Number(stat.count);
    result[stat.status as keyof typeof result] = count;
    result.total += count;
  }

  return result;
}

// Get recent email activity
export async function getRecentEmails(
  db: D1Database,
  options: {
    limit?: number;
    offset?: number;
    userId?: string;
    type?: string;
    status?: string;
  } = {}
): Promise<EmailQueueRecord[]> {
  const { limit = 50, offset = 0, userId, type, status } = options;
  const dbClient = drizzle(db);

  const query = dbClient
    .select()
    .from(emailQueue)
    .orderBy(desc(emailQueue.createdAt))
    .limit(limit)
    .offset(offset);

  const conditions = [];
  if (userId) conditions.push(eq(emailQueue.userId, userId));
  if (type) conditions.push(eq(emailQueue.type, type));
  if (status) conditions.push(eq(emailQueue.status, status));

  if (conditions.length > 0) {
    return await query.where(and(...conditions));
  }

  return await query;
}

// Clear old sent emails (for cleanup)
export async function cleanupOldEmails(
  db: D1Database,
  daysToKeep = 30
): Promise<{ deleted: number }> {
  const dbClient = drizzle(db);
  const cutoffTime = Date.now() - (daysToKeep * 24 * 60 * 60 * 1000);

  // Delete old sent emails from queue
  await dbClient
    .delete(emailQueue)
    .where(
      and(
        eq(emailQueue.status, 'sent'),
        lt(emailQueue.sentAt, cutoffTime)
      )
    );

  // Count how many were deleted by querying
  const remaining = await dbClient
    .select({ count: sql<number>`count(*)` })
    .from(emailQueue)
    .where(eq(emailQueue.status, 'sent'));

  return { deleted: remaining[0]?.count || 0 };
}