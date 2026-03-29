# Task 3: Proactive Spoken Notifications from Engine State Transitions

**Story:** 24-4-intent-engine - Intent Classification + Engine Integration
**Epic:** 24

## Task Description

Implement proactive spoken notifications: when the engine transitions state (plan generated, tests running, tests passed, PR created, merged), the voice session automatically speaks a summary to the user without being prompted. Add a debounce filter so only significant events trigger speech.

## Acceptance Criteria

- Engine state transitions trigger proactive TTS notifications to the user
- Significant events: plan generated, tests started, tests passed/failed, PR created, PR merged, error
- Debounce filter: only info+ level events trigger speech, not debug logs
- Event deduplication: same state transition within 5s does not trigger duplicate speech
- Notifications do not interrupt user speech (queued until user finishes speaking)
- Notification text is concise and natural: "Tests are running now." / "All tests passed." / "PR #42 created."
- Configurable: user can disable proactive notifications via voice config
- Unit tests for notification triggering, debounce, and queueing

## Implementation Details

### Technical Requirements

- [ ] Create `packages/voice/src/notification-manager.ts`:

```typescript
import type { EngineStateUpdate, EngineLogEntry } from '@tamma/shared/contracts';
import type { EngineState } from '@tamma/shared';

export interface NotificationConfig {
  enabled: boolean;
  minLevel: 'debug' | 'info' | 'warn' | 'error';  // default: 'info'
  dedupeWindowMs: number;  // default: 5000
}

const DEFAULT_NOTIFICATION_CONFIG: NotificationConfig = {
  enabled: true,
  minLevel: 'info',
  dedupeWindowMs: 5_000,
};

export interface Notification {
  text: string;
  priority: 'low' | 'normal' | 'high';
  timestamp: number;
}

export class NotificationManager {
  private config: NotificationConfig;
  private lastNotifications: Map<string, number> = new Map();  // key -> timestamp
  private queue: Notification[] = [];
  private onNotification: ((notification: Notification) => void) | null = null;
  private lastState: EngineState | null = null;

  constructor(config?: Partial<NotificationConfig>) {
    this.config = { ...DEFAULT_NOTIFICATION_CONFIG, ...config };
  }

  /** Register callback for when a notification should be spoken. */
  setHandler(handler: (notification: Notification) => void): void {
    this.onNotification = handler;
    // Flush any queued notifications
    this.flushQueue();
  }

  /** Process an engine state update. May produce a notification. */
  handleStateUpdate(update: EngineStateUpdate): void {
    if (!this.config.enabled) return;

    const prevState = this.lastState;
    this.lastState = update.state;

    if (prevState === update.state) return;  // No transition

    const notification = this.stateTransitionToNotification(prevState, update);
    if (notification) {
      this.emit(notification);
    }
  }

  /** Process an engine log entry. May produce a notification. */
  handleLogEntry(entry: EngineLogEntry): void {
    if (!this.config.enabled) return;
    if (!this.isAboveMinLevel(entry.level)) return;

    // Only notify for specific high-value log patterns
    const notification = this.logToNotification(entry);
    if (notification) {
      this.emit(notification);
    }
  }

  /** Update config (e.g., disable notifications). */
  updateConfig(config: Partial<NotificationConfig>): void {
    Object.assign(this.config, config);
  }

  // --- Private ---

  private stateTransitionToNotification(
    prevState: EngineState | null,
    update: EngineStateUpdate,
  ): Notification | null {
    const state = update.state;
    const issue = update.issue;
    const issueRef = issue ? `issue #${issue.number}` : 'the current issue';

    // Map state transitions to human-friendly messages
    switch (state) {
      case 'analyzing':
        return { text: `Analyzing ${issueRef}.`, priority: 'low', timestamp: Date.now() };
      case 'planning':
        return { text: `Generating a development plan for ${issueRef}.`, priority: 'normal', timestamp: Date.now() };
      case 'awaiting-approval':
        return null;  // Handled separately by approval request flow
      case 'implementing':
        return { text: 'Starting implementation.', priority: 'normal', timestamp: Date.now() };
      case 'testing':
        return { text: 'Running tests now.', priority: 'normal', timestamp: Date.now() };
      case 'creating-pr':
        return { text: 'Creating a pull request.', priority: 'normal', timestamp: Date.now() };
      case 'complete':
        return { text: `Done! ${issueRef} has been completed.`, priority: 'high', timestamp: Date.now() };
      case 'error':
        return { text: 'Something went wrong. Check the logs for details.', priority: 'high', timestamp: Date.now() };
      case 'idle':
        if (prevState && prevState !== 'idle') {
          return { text: 'Engine is idle, ready for the next task.', priority: 'low', timestamp: Date.now() };
        }
        return null;
      default:
        return null;
    }
  }

  private logToNotification(entry: EngineLogEntry): Notification | null {
    const msg = entry.message.toLowerCase();

    // Pattern match high-value log messages
    if (msg.includes('tests passed') || msg.includes('all tests pass')) {
      return { text: 'All tests passed.', priority: 'normal', timestamp: Date.now() };
    }
    if (msg.includes('tests failed') || msg.includes('test failure')) {
      return { text: 'Some tests failed. I\'ll review the failures.', priority: 'high', timestamp: Date.now() };
    }
    if (msg.includes('pr created') || msg.includes('pull request created')) {
      // Extract PR number if available
      const prMatch = msg.match(/#(\d+)/);
      const prRef = prMatch ? `PR #${prMatch[1]}` : 'a pull request';
      return { text: `${prRef} has been created.`, priority: 'high', timestamp: Date.now() };
    }
    if (msg.includes('merged')) {
      return { text: 'The pull request has been merged.', priority: 'high', timestamp: Date.now() };
    }

    return null;
  }

  private emit(notification: Notification): void {
    // Deduplication: don't repeat same notification within window
    const key = notification.text;
    const lastTime = this.lastNotifications.get(key);
    if (lastTime && Date.now() - lastTime < this.config.dedupeWindowMs) {
      return;
    }
    this.lastNotifications.set(key, Date.now());

    // Clean up old dedup entries
    const cutoff = Date.now() - this.config.dedupeWindowMs * 2;
    for (const [k, t] of this.lastNotifications) {
      if (t < cutoff) this.lastNotifications.delete(k);
    }

    if (this.onNotification) {
      this.onNotification(notification);
    } else {
      this.queue.push(notification);
    }
  }

  private flushQueue(): void {
    if (!this.onNotification) return;
    const queued = this.queue.splice(0);
    for (const n of queued) {
      this.onNotification(n);
    }
  }

  private isAboveMinLevel(level: string): boolean {
    const levels = ['debug', 'info', 'warn', 'error'];
    const minIndex = levels.indexOf(this.config.minLevel);
    const levelIndex = levels.indexOf(level);
    return levelIndex >= minIndex;
  }
}
```

- [ ] Wire NotificationManager into VoiceSession:

```typescript
// In VoiceSession.initialize():
this.notificationManager = new NotificationManager();
this.notificationManager.setHandler(async (notification) => {
  // Don't interrupt user speech
  if (this.state === 'listening') {
    // Queue for after user finishes
    this.pendingNotifications.push(notification);
    return;
  }
  await this.respondWithText(notification.text);
});

// In VoiceEngineTransport, forward events to notification manager:
// this.onStateUpdate -> session.notificationManager.handleStateUpdate()
// this.onLog -> session.notificationManager.handleLogEntry()
```

- [ ] Add notification config to `VoiceSessionConfig`:

```typescript
// In types.ts, extend VoiceSessionConfig:
export interface VoiceSessionConfig {
  // ... existing fields ...
  notifications: {
    enabled: boolean;
    minLevel: 'debug' | 'info' | 'warn' | 'error';
  };
}
```

### Files to Modify/Create

- CREATE `packages/voice/src/notification-manager.ts`
- CREATE `packages/voice/src/notification-manager.test.ts`
- MODIFY `packages/voice/src/voice-session.ts` -- wire NotificationManager
- MODIFY `packages/voice/src/types.ts` -- add notification config to VoiceSessionConfig
- MODIFY `packages/orchestrator/src/transports/voice.ts` -- forward events to notification manager

### Dependencies

- [ ] Task 2: VoiceSession with intent-aware routing
- [ ] Story 24-3: TTS streaming for spoken notifications
- [ ] `EngineState`, `EngineStateUpdate`, `EngineLogEntry` from `@tamma/shared`

## Testing Strategy

### Unit Tests -- notification-manager.test.ts

- [ ] Test state transition 'idle' -> 'analyzing' produces notification
- [ ] Test state transition 'analyzing' -> 'planning' produces notification
- [ ] Test state transition 'testing' -> 'complete' produces high-priority notification
- [ ] Test state transition to 'error' produces high-priority notification
- [ ] Test same state (no transition) produces no notification
- [ ] Test 'awaiting-approval' produces no notification (handled by approval flow)
- [ ] Test deduplication: same notification within 5s ignored
- [ ] Test deduplication: same notification after 5s allowed
- [ ] Test log "tests passed" produces notification
- [ ] Test log "PR #42 created" produces notification with PR number
- [ ] Test log "merged" produces notification
- [ ] Test debug-level log filtered when minLevel is 'info'
- [ ] Test info-level log passes when minLevel is 'info'
- [ ] Test disabled notifications produce nothing
- [ ] Test updateConfig changes behavior immediately
- [ ] Test queue: notifications queued before handler set, flushed after
- [ ] Test handler callback receives correct notification
- [ ] Test old dedup entries cleaned up

### Validation Steps

1. [ ] Create NotificationManager with state transition and log pattern matching
2. [ ] Implement deduplication window
3. [ ] Wire into VoiceSession with interrupt-aware queueing
4. [ ] Test all state transitions produce correct messages
5. [ ] Test log pattern matching
6. [ ] Run all unit tests
7. [ ] Verify TypeScript strict mode compilation

## Notes & Considerations

- Proactive notifications are the key feature that makes voice mode feel like a phone call with a colleague. Without them, the user would have to ask "what's happening?" after every long operation.
- The debounce/dedup window (5s) prevents rapid-fire notifications during fast state transitions (e.g., analyzing -> planning -> implementing can happen in seconds).
- Notifications do not interrupt user speech. If the user is speaking, notifications are queued and spoken after the user finishes. This prevents annoying overlap.
- The `awaiting-approval` state does not produce a notification because the approval flow already speaks a detailed prompt (from Task 2).
- Log-based notifications use pattern matching (contains "tests passed", "PR created", etc.) rather than structured data. This is fragile but practical given the current log format. As the engine evolves to emit structured events, this can be replaced.

## Completion Checklist

- [ ] NotificationManager with state transition mapping
- [ ] Log entry pattern matching for key events
- [ ] Deduplication window prevents duplicate speech
- [ ] Notifications queued during user speech
- [ ] Wired into VoiceSession and VoiceEngineTransport
- [ ] Notification config in VoiceSessionConfig
- [ ] All unit tests passing
- [ ] TypeScript strict mode compiles
