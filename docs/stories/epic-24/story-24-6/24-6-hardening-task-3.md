# Task 3: Session Recording + Transcript Persistence

**Story:** 24-6-hardening - Hardening + Production Readiness
**Epic:** 24

## Task Description

Persist full voice session transcripts to the `chat_messages` table, linked to the user's chat conversation. This enables transcript review after sessions end and provides audit trail for voice interactions.

## Acceptance Criteria

- Full transcript (user + assistant turns) persisted to `chat_messages` table on session end
- Each voice message linked to the user's conversation ID
- Voice messages marked with `source: 'voice'` to distinguish from text messages
- Transcript includes timestamps, speaker role, confidence score (for STT)
- Session summary persisted: duration, cost, STT/TTS providers used
- Transcripts retrievable via existing chat API (`GET /api/v1/voice/sessions/:id`)
- Graceful handling: if DB write fails, session continues (non-blocking)
- Unit tests for persistence logic

## Implementation Details

### Technical Requirements

- [ ] Create `packages/voice/src/transcript-persister.ts`:

```typescript
export interface PersistentTranscriptEntry {
  conversationId: string;
  sessionId: string;
  role: 'user' | 'assistant';
  content: string;
  source: 'voice' | 'text';
  timestamp: number;
  metadata: {
    confidence?: number;
    sttProvider?: string;
    ttsProvider?: string;
  };
}

export interface SessionSummary {
  sessionId: string;
  userId: string;
  conversationId: string;
  startedAt: number;
  endedAt: number;
  durationMs: number;
  turnCount: number;
  sttProvider: string;
  ttsProvider: string;
  costTracking: {
    sttMinutes: number;
    ttsCharacters: number;
    estimatedCostUsd: number;
  };
}

export interface ITranscriptPersister {
  /** Persist a single transcript entry. */
  persistEntry(entry: PersistentTranscriptEntry): Promise<void>;
  /** Persist session summary on session end. */
  persistSummary(summary: SessionSummary): Promise<void>;
  /** Get transcript for a session. */
  getTranscript(sessionId: string): Promise<PersistentTranscriptEntry[]>;
  /** Get session summary. */
  getSummary(sessionId: string): Promise<SessionSummary | null>;
}

/**
 * Database-backed transcript persister.
 * Uses the existing chat_messages table for transcript entries
 * and a voice_sessions table for session summaries.
 */
export class DatabaseTranscriptPersister implements ITranscriptPersister {
  constructor(private readonly db: import('pg').Pool);

  async persistEntry(entry: PersistentTranscriptEntry): Promise<void> {
    try {
      await this.db.query(
        `INSERT INTO chat_messages (conversation_id, session_id, role, content, source, timestamp, metadata)
         VALUES ($1, $2, $3, $4, $5, to_timestamp($6 / 1000.0), $7)`,
        [
          entry.conversationId,
          entry.sessionId,
          entry.role,
          entry.content,
          entry.source,
          entry.timestamp,
          JSON.stringify(entry.metadata),
        ],
      );
    } catch (err) {
      // Non-blocking: log error but don't crash the session
      console.error('Failed to persist transcript entry:', err);
    }
  }

  async persistSummary(summary: SessionSummary): Promise<void> {
    try {
      await this.db.query(
        `INSERT INTO voice_sessions
           (session_id, user_id, conversation_id, started_at, ended_at, duration_ms,
            turn_count, stt_provider, tts_provider, cost_data)
         VALUES ($1, $2, $3, to_timestamp($4 / 1000.0), to_timestamp($5 / 1000.0), $6, $7, $8, $9, $10)
         ON CONFLICT (session_id) DO UPDATE SET
           ended_at = EXCLUDED.ended_at,
           duration_ms = EXCLUDED.duration_ms,
           turn_count = EXCLUDED.turn_count,
           cost_data = EXCLUDED.cost_data`,
        [
          summary.sessionId,
          summary.userId,
          summary.conversationId,
          summary.startedAt,
          summary.endedAt,
          summary.durationMs,
          summary.turnCount,
          summary.sttProvider,
          summary.ttsProvider,
          JSON.stringify(summary.costTracking),
        ],
      );
    } catch (err) {
      console.error('Failed to persist session summary:', err);
    }
  }

  async getTranscript(sessionId: string): Promise<PersistentTranscriptEntry[]> {
    const result = await this.db.query(
      `SELECT conversation_id, session_id, role, content, source,
              extract(epoch from timestamp) * 1000 as timestamp, metadata
       FROM chat_messages
       WHERE session_id = $1
       ORDER BY timestamp ASC`,
      [sessionId],
    );
    return result.rows.map((row: Record<string, unknown>) => ({
      conversationId: row.conversation_id as string,
      sessionId: row.session_id as string,
      role: row.role as 'user' | 'assistant',
      content: row.content as string,
      source: row.source as 'voice' | 'text',
      timestamp: row.timestamp as number,
      metadata: typeof row.metadata === 'string' ? JSON.parse(row.metadata) : row.metadata,
    }));
  }

  async getSummary(sessionId: string): Promise<SessionSummary | null> {
    const result = await this.db.query(
      `SELECT * FROM voice_sessions WHERE session_id = $1`,
      [sessionId],
    );
    if (result.rows.length === 0) return null;
    const row = result.rows[0] as Record<string, unknown>;
    return {
      sessionId: row.session_id as string,
      userId: row.user_id as string,
      conversationId: row.conversation_id as string,
      startedAt: (row.started_at as Date).getTime(),
      endedAt: (row.ended_at as Date).getTime(),
      durationMs: row.duration_ms as number,
      turnCount: row.turn_count as number,
      sttProvider: row.stt_provider as string,
      ttsProvider: row.tts_provider as string,
      costTracking: typeof row.cost_data === 'string' ? JSON.parse(row.cost_data) : row.cost_data,
    };
  }
}
```

- [ ] Create database migration for `voice_sessions` table:

```sql
-- database/migrations/XXXX_create_voice_sessions.sql
CREATE TABLE IF NOT EXISTS voice_sessions (
  session_id UUID PRIMARY KEY,
  user_id UUID NOT NULL REFERENCES users(id),
  conversation_id UUID,
  started_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  ended_at TIMESTAMPTZ,
  duration_ms INTEGER,
  turn_count INTEGER DEFAULT 0,
  stt_provider VARCHAR(50),
  tts_provider VARCHAR(50),
  cost_data JSONB DEFAULT '{}',
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Add source and session_id columns to chat_messages if not exist
ALTER TABLE chat_messages ADD COLUMN IF NOT EXISTS source VARCHAR(10) DEFAULT 'text';
ALTER TABLE chat_messages ADD COLUMN IF NOT EXISTS session_id UUID;
CREATE INDEX IF NOT EXISTS idx_chat_messages_session_id ON chat_messages(session_id);
CREATE INDEX IF NOT EXISTS idx_voice_sessions_user_id ON voice_sessions(user_id);
```

- [ ] Wire persister into VoiceSession:

```typescript
// On each user/assistant turn:
await this.persister.persistEntry({
  conversationId: this.conversationId,
  sessionId: this.sessionId,
  role: turn.role,
  content: turn.content,
  source: turn.source,
  timestamp: Date.now(),
  metadata: { confidence, sttProvider: this.stt?.name, ttsProvider: this.tts?.name },
});

// On session end:
await this.persister.persistSummary({
  sessionId: this.sessionId,
  userId: this.userId,
  conversationId: this.conversationId,
  startedAt: this.startedAt,
  endedAt: Date.now(),
  durationMs: Date.now() - this.startedAt,
  turnCount: this.context.length,
  sttProvider: this.stt?.name ?? 'none',
  ttsProvider: this.tts?.name ?? 'none',
  costTracking: this.sessionManager.getSessionInfo(this.sessionId)?.costTracking ?? { sttMinutes: 0, ttsCharacters: 0, estimatedCostUsd: 0 },
});
```

### Files to Modify/Create

- CREATE `packages/voice/src/transcript-persister.ts`
- CREATE `packages/voice/src/transcript-persister.test.ts`
- CREATE `database/migrations/XXXX_create_voice_sessions.sql`
- MODIFY `packages/voice/src/voice-session.ts` -- wire persister
- MODIFY `packages/api/src/routes/voice/index.ts` -- add transcript REST endpoints

### Dependencies

- [ ] Task 2: SessionManager with cost tracking
- [ ] PostgreSQL database (`pg` pool)
- [ ] Existing `chat_messages` table

## Testing Strategy

### Unit Tests -- transcript-persister.test.ts

- [ ] Test `persistEntry` inserts row into chat_messages
- [ ] Test `persistEntry` handles DB error without throwing (non-blocking)
- [ ] Test `persistSummary` inserts row into voice_sessions
- [ ] Test `persistSummary` upserts on conflict (idempotent)
- [ ] Test `persistSummary` handles DB error without throwing
- [ ] Test `getTranscript` returns entries ordered by timestamp
- [ ] Test `getTranscript` returns empty array for unknown session
- [ ] Test `getSummary` returns summary for valid session
- [ ] Test `getSummary` returns null for unknown session
- [ ] Test metadata serialized as JSONB correctly
- [ ] Test cost_data serialized as JSONB correctly

### Validation Steps

1. [ ] Create TranscriptPersister with DB operations
2. [ ] Create database migration
3. [ ] Wire persister into VoiceSession
4. [ ] Test transcript persistence on session end
5. [ ] Test non-blocking error handling
6. [ ] Run migration
7. [ ] Run all unit tests
8. [ ] Verify TypeScript compiles

## Notes & Considerations

- Transcript persistence is non-blocking. If the DB write fails, the voice session continues. This prevents database issues from degrading the voice experience.
- The `chat_messages` table is extended with `source` (voice/text) and `session_id` columns. This allows voice transcripts to interleave with text messages in the same conversation view.
- The `voice_sessions` table stores session-level metadata and cost tracking. This enables the admin dashboard to show voice session analytics.
- The upsert pattern on `voice_sessions` (ON CONFLICT DO UPDATE) ensures the session summary is updated if the session end is called multiple times (e.g., on reconnect).

## Completion Checklist

- [ ] TranscriptPersister interface and DB implementation
- [ ] Database migration for voice_sessions table
- [ ] chat_messages table extended with source and session_id
- [ ] Transcript entries persisted per turn
- [ ] Session summary persisted on session end
- [ ] Non-blocking error handling
- [ ] REST endpoints for transcript retrieval
- [ ] All unit tests passing
- [ ] TypeScript compiles
