---
title: "Task 2: Rate Limiting + Session Timeout + Cost Tracking"
sidebar:
  order: 240
---

**Story:** 24-6-hardening - Hardening + Production Readiness
**Epic:** 24

## Task Description

Implement rate limiting (max 1 active voice session per user, configurable max total sessions), session timeout (auto-disconnect after idle period), and cost tracking for STT/TTS API usage per session.

## Acceptance Criteria

- Max 1 active voice session per user (second connection closes the first)
- Configurable max total concurrent sessions (default: 10)
- Exceeded session limit returns error `SESSION_LIMIT` on connect
- Session timeout: auto-disconnect after configurable idle period (default: 30 min)
- Idle timer reset on any user activity (audio, text, session.start)
- Cost tracking: STT minutes and TTS characters tracked per session
- Cost surfaced in session metadata (accessible via REST API)
- Security: STT/TTS API keys stored server-side only, never exposed to browser

## Implementation Details

### Technical Requirements

- [ ] Create `packages/voice/src/session-manager.ts`:

```typescript
import type { VoiceSession } from './voice-session.js';

export interface SessionManagerConfig {
  maxSessionsPerUser: number;   // default: 1
  maxTotalSessions: number;     // default: 10
  idleTimeoutMs: number;        // default: 30 * 60 * 1000 (30 min)
}

const DEFAULT_SESSION_MANAGER_CONFIG: SessionManagerConfig = {
  maxSessionsPerUser: 1,
  maxTotalSessions: 10,
  idleTimeoutMs: 30 * 60 * 1000,
};

export interface SessionInfo {
  sessionId: string;
  userId: string;
  startedAt: number;
  lastActivityAt: number;
  costTracking: SessionCostTracking;
}

export interface SessionCostTracking {
  sttMinutes: number;
  ttsCharacters: number;
  estimatedCostUsd: number;
}

export class SessionManager {
  private readonly sessions: Map<string, { session: VoiceSession; info: SessionInfo; idleTimer: ReturnType<typeof setTimeout> }> = new Map();
  private readonly config: SessionManagerConfig;

  constructor(config?: Partial<SessionManagerConfig>) {
    this.config = { ...DEFAULT_SESSION_MANAGER_CONFIG, ...config };
  }

  /**
   * Check if a new session can be created for this user.
   * Returns null if allowed, or an error message if blocked.
   */
  canCreateSession(userId: string): string | null {
    // Check per-user limit
    const userSessions = [...this.sessions.values()].filter(s => s.info.userId === userId);
    if (userSessions.length >= this.config.maxSessionsPerUser) {
      // Close the oldest session for this user (replace policy)
      const oldest = userSessions[0];
      if (oldest) {
        void this.removeSession(oldest.info.sessionId, 'replaced');
      }
    }

    // Check total limit
    if (this.sessions.size >= this.config.maxTotalSessions) {
      return `Maximum concurrent sessions (${this.config.maxTotalSessions}) reached`;
    }

    return null;
  }

  /**
   * Register a new session.
   */
  addSession(session: VoiceSession, userId: string): SessionInfo {
    const info: SessionInfo = {
      sessionId: session.sessionId,
      userId,
      startedAt: Date.now(),
      lastActivityAt: Date.now(),
      costTracking: {
        sttMinutes: 0,
        ttsCharacters: 0,
        estimatedCostUsd: 0,
      },
    };

    const idleTimer = this.startIdleTimer(session.sessionId);

    this.sessions.set(session.sessionId, { session, info, idleTimer });
    return info;
  }

  /**
   * Record activity to reset idle timer.
   */
  recordActivity(sessionId: string): void {
    const entry = this.sessions.get(sessionId);
    if (!entry) return;

    entry.info.lastActivityAt = Date.now();

    // Reset idle timer
    clearTimeout(entry.idleTimer);
    entry.idleTimer = this.startIdleTimer(sessionId);
  }

  /**
   * Track STT usage (called per audio chunk).
   */
  trackSTTUsage(sessionId: string, durationMs: number): void {
    const entry = this.sessions.get(sessionId);
    if (!entry) return;

    entry.info.costTracking.sttMinutes += durationMs / 60_000;
    this.updateEstimatedCost(entry.info.costTracking);
  }

  /**
   * Track TTS usage (called per synthesis request).
   */
  trackTTSUsage(sessionId: string, characters: number): void {
    const entry = this.sessions.get(sessionId);
    if (!entry) return;

    entry.info.costTracking.ttsCharacters += characters;
    this.updateEstimatedCost(entry.info.costTracking);
  }

  /**
   * Get session info for REST API.
   */
  getSessionInfo(sessionId: string): SessionInfo | null {
    return this.sessions.get(sessionId)?.info ?? null;
  }

  /**
   * Get all active sessions (for admin).
   */
  getActiveSessions(): SessionInfo[] {
    return [...this.sessions.values()].map(e => e.info);
  }

  /**
   * Remove a session.
   */
  async removeSession(sessionId: string, reason: 'user' | 'timeout' | 'replaced' | 'error'): Promise<void> {
    const entry = this.sessions.get(sessionId);
    if (!entry) return;

    clearTimeout(entry.idleTimer);
    this.sessions.delete(sessionId);
    await entry.session.dispose();
  }

  /**
   * Dispose all sessions.
   */
  async disposeAll(): Promise<void> {
    const entries = [...this.sessions.values()];
    this.sessions.clear();
    for (const entry of entries) {
      clearTimeout(entry.idleTimer);
      await entry.session.dispose();
    }
  }

  // --- Private ---

  private startIdleTimer(sessionId: string): ReturnType<typeof setTimeout> {
    return setTimeout(() => {
      void this.removeSession(sessionId, 'timeout');
    }, this.config.idleTimeoutMs);
  }

  private updateEstimatedCost(tracking: SessionCostTracking): void {
    // Deepgram: ~$0.0043/min, ElevenLabs: ~$0.30/1000 chars
    const sttCost = tracking.sttMinutes * 0.0043;
    const ttsCost = (tracking.ttsCharacters / 1000) * 0.30;
    tracking.estimatedCostUsd = Math.round((sttCost + ttsCost) * 10000) / 10000;
  }
}
```

- [ ] Wire SessionManager into voice route:

```typescript
// In registerVoiceRoutes:
const sessionManager = new SessionManager();

// On WebSocket connect:
const limitError = sessionManager.canCreateSession(userId);
if (limitError) {
  socket.send(JSON.stringify({ type: 'error', code: 'SESSION_LIMIT', message: limitError, recoverable: false }));
  socket.close(4003);
  return;
}

// Register session
const info = sessionManager.addSession(voiceSession, userId);

// On audio/text activity:
sessionManager.recordActivity(sessionId);

// On STT audio send:
sessionManager.trackSTTUsage(sessionId, chunkDurationMs);

// On TTS synthesis:
sessionManager.trackTTSUsage(sessionId, text.length);
```

- [ ] Add REST endpoints for session info:

```typescript
// GET /api/v1/voice/sessions -- list active sessions (admin)
// GET /api/v1/voice/sessions/:id -- get session info with cost tracking
```

### Files to Modify/Create

- CREATE `packages/voice/src/session-manager.ts`
- CREATE `packages/voice/src/session-manager.test.ts`
- MODIFY `packages/api/src/routes/voice/index.ts` -- wire SessionManager, add session REST endpoints

### Dependencies

- [ ] Story 24-1 Task 2: VoiceSession
- [ ] Story 24-1 Task 3: Voice routes

## Testing Strategy

### Unit Tests -- session-manager.test.ts

- [ ] Test `canCreateSession` returns null when under limits
- [ ] Test `canCreateSession` replaces oldest session when per-user limit hit
- [ ] Test `canCreateSession` returns error when total limit hit
- [ ] Test `addSession` creates SessionInfo with correct fields
- [ ] Test `addSession` starts idle timer
- [ ] Test `recordActivity` resets idle timer
- [ ] Test idle timeout triggers session removal after configured period
- [ ] Test `trackSTTUsage` accumulates minutes
- [ ] Test `trackTTSUsage` accumulates characters
- [ ] Test `updateEstimatedCost` calculates correctly (Deepgram + ElevenLabs rates)
- [ ] Test `removeSession` clears timer and disposes session
- [ ] Test `removeSession` with 'timeout' reason
- [ ] Test `removeSession` with 'replaced' reason
- [ ] Test `getSessionInfo` returns info for valid session
- [ ] Test `getSessionInfo` returns null for unknown session
- [ ] Test `getActiveSessions` returns all sessions
- [ ] Test `disposeAll` cleans up everything
- [ ] Test configurable idle timeout (e.g., 5s for tests)
- [ ] Test configurable max sessions per user
- [ ] Test configurable max total sessions

### Validation Steps

1. [ ] Create SessionManager with limits and tracking
2. [ ] Wire into voice route for session lifecycle
3. [ ] Wire STT/TTS usage tracking
4. [ ] Add session REST endpoints
5. [ ] Test rate limiting (second connection replaces first)
6. [ ] Test idle timeout
7. [ ] Test cost tracking accuracy
8. [ ] Run all unit tests
9. [ ] Verify TypeScript compiles

## Notes & Considerations

- The "replace" policy (close oldest session when per-user limit hit) is user-friendly: if someone opens a new tab, the old voice session closes automatically without manual intervention.
- Cost tracking is approximate. The rates are based on public pricing for Deepgram Nova-3 and ElevenLabs. The actual cost may vary by plan.
- Idle timeout uses `setTimeout` instead of a periodic check. The timer is reset on every activity event. This is efficient because there is no polling.
- API keys are verified to be server-side only. The voice config REST endpoint returns provider names but not keys. The WebSocket protocol never sends keys to the client.
- The session REST endpoints (`/api/v1/voice/sessions`) are admin-only for monitoring active voice sessions and their costs.

## Completion Checklist

- [ ] SessionManager with per-user and total session limits
- [ ] Replace policy for per-user limit
- [ ] Idle timeout with configurable duration
- [ ] Activity tracking resets idle timer
- [ ] STT/TTS cost tracking per session
- [ ] Session info REST endpoints
- [ ] Wired into voice route
- [ ] All unit tests passing
- [ ] TypeScript compiles
