import { desc, eq } from 'drizzle-orm';
import type { UserWithRole } from '~/lib/auth/permissions';
import { getDb, hasDatabase } from './client.server';
import { reviewSessions } from './schema';
import { getGitProvider } from '~/lib/git/provider.server';

export type ReviewSession = {
  id: string;
  title: string;
  summary?: string | null;
  docPaths: string[];
  primaryDocPath: string;
  branch?: string | null;
  prNumber?: number | null;
  prUrl?: string | null;
  status: string;
  ownerId: string;
  createdAt: number;
};

export async function listSessions(env: { DB?: D1Database; CACHE?: KVNamespace; [key: string]: unknown }, docPath?: string): Promise<ReviewSession[]> {
  if (!hasDatabase(env)) {
    return [];
  }

  const db = getDb(env);
  const rows = await db.select().from(reviewSessions).orderBy(desc(reviewSessions.createdAt)).all();

  const enriched = await Promise.all(
    rows.map(async (row) => {
      if (row.prNumber && row.branch && row.prUrl) {
        return row;
      }
      return ensureSessionPr(env, row);
    })
  );

  return enriched
    .map((row) => ({
      ...row,
      docPaths: parseDocPaths(row.docPaths),
    }))
    .filter((session) => (!docPath ? true : session.docPaths.includes(docPath)));
}

export async function createSession(
  env: { DB?: D1Database; CACHE?: KVNamespace; [key: string]: unknown },
  user: UserWithRole,
  input: { title: string; summary?: string; docPaths: string[] }
): Promise<ReviewSession> {
  if (!hasDatabase(env)) {
    throw new Error('Database not configured.');
  }

  if (input.docPaths.length === 0) {
    throw new Error('At least one document path is required.');
  }

  const db = getDb(env);
  const id = crypto.randomUUID();
  const now = Date.now();

  await db.insert(reviewSessions).values({
    id,
    title: input.title,
    summary: input.summary ?? null,
    docPaths: JSON.stringify(Array.from(new Set(input.docPaths))),
    primaryDocPath: input.docPaths[0],
    branch: null,
    prNumber: null,
    prUrl: null,
    status: 'draft',
    ownerId: user.id,
    createdAt: now,
    updatedAt: now,
  });

  let record = await db.select().from(reviewSessions).where(eq(reviewSessions.id, id)).get();

  if (!record) {
    throw new Error('Failed to create review session.');
  }

  record = await ensureSessionPr(env, record);

  return {
    ...record,
    docPaths: parseDocPaths(record.docPaths),
  };
}

export async function ensureSessionPr(env: { DB?: D1Database; CACHE?: KVNamespace; [key: string]: unknown }, record: typeof reviewSessions.$inferSelect) {
  if (!hasDatabase(env)) {
    return record;
  }

  if (record.prNumber && record.branch && record.prUrl) {
    return record;
  }

  const provider = getGitProvider(env);
  const meta = await provider.ensureSessionPullRequest({
    sessionId: record.id,
    title: record.title,
    summary: record.summary,
    docPaths: parseDocPaths(record.docPaths),
  });

  const db = getDb(env);
  await db
    .update(reviewSessions)
    .set({
      branch: meta.branch,
      prNumber: meta.prNumber,
      prUrl: meta.prUrl,
      status: meta.status,
      updatedAt: Date.now(),
    })
    .where(eq(reviewSessions.id, record.id));

  return {
    ...record,
    branch: meta.branch,
    prNumber: meta.prNumber,
    prUrl: meta.prUrl,
    status: meta.status,
  };
}

export function parseDocPaths(docPaths: string | null): string[] {
  if (!docPaths) {
    return [];
  }

  try {
    const parsed = JSON.parse(docPaths);
    return Array.isArray(parsed) ? (parsed.filter((item) => typeof item === 'string')) : [];
  } catch (error) {
    console.warn('Failed to parse docPaths JSON', error);
    return [];
  }
}
