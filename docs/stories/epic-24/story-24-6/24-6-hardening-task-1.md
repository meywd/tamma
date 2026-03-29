# Task 1: WebSocket Reconnection + Provider Fallback Chain

**Story:** 24-6-hardening - Hardening + Production Readiness
**Epic:** 24

## Task Description

Implement robust WebSocket reconnection with exponential backoff on the browser side, and server-side provider fallback chains that seamlessly switch between STT/TTS providers on failure mid-session.

## Acceptance Criteria

- Browser: auto-reconnect WebSocket with exponential backoff (1s, 2s, 4s, 8s, max 30s)
- Browser: session state preserved across reconnections (transcript, config)
- Browser: reconnection attempts shown in ConnectionStatus component
- Browser: max reconnection attempts configurable (default: 10), then show error
- Server: STT provider fallback: Deepgram error mid-session -> switch to Whisper
- Server: TTS provider fallback: ElevenLabs error mid-session -> switch to OpenAI TTS
- Server: fallback is seamless -- user gets a brief text notification, then voice continues
- Server: error recovery notifies client via `error` message with `recoverable: true`
- Session resume: on reconnect, server sends current state snapshot

## Implementation Details

### Technical Requirements

- [ ] Add reconnection logic to `useVoiceSession` hook:

```typescript
// In useVoiceSession:
const reconnectAttemptsRef = useRef(0);
const maxReconnectAttempts = options?.maxReconnectAttempts ?? 10;
const reconnectTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

function handleWsClose(event: CloseEvent): void {
  // Code 4001 = auth failed, don't reconnect
  // Code 1000 = normal close (user-initiated), don't reconnect
  if (event.code === 4001 || event.code === 1000) {
    cleanup();
    return;
  }

  // Attempt reconnection
  if (reconnectAttemptsRef.current < maxReconnectAttempts) {
    reconnectAttemptsRef.current++;
    const delay = Math.min(
      1000 * Math.pow(2, reconnectAttemptsRef.current - 1),
      30_000,
    );
    setState('connecting');

    reconnectTimerRef.current = setTimeout(() => {
      reconnectTimerRef.current = null;
      void reconnect();
    }, delay);
  } else {
    setState('error');
    options?.onError?.({
      code: 'RECONNECT_EXHAUSTED',
      message: `Failed to reconnect after ${maxReconnectAttempts} attempts`,
    });
  }
}

async function reconnect(): Promise<void> {
  try {
    // Reuse existing audio context and VAD (don't re-request mic permission)
    // Only reconnect WebSocket
    const ws = new WebSocket(wsUrl);
    ws.binaryType = 'arraybuffer';
    wsRef.current = ws;

    ws.onopen = () => {
      // Reset backoff counter on success
      reconnectAttemptsRef.current = 0;
      // Resume session
      ws.send(JSON.stringify({
        type: 'session.start',
        config: currentConfig,
        resumeSessionId: sessionId,  // Tell server we're resuming
      }));
    };
    // ... wire other handlers
  } catch {
    handleWsClose({ code: 0 } as CloseEvent);
  }
}
```

- [ ] Add provider fallback to VoiceSession (server-side):

```typescript
// In VoiceSession, wrap STT/TTS calls with fallback:

private async withSTTFallback<T>(fn: () => Promise<T>): Promise<T> {
  try {
    return await fn();
  } catch (err) {
    // Primary STT failed -- switch to fallback
    this.sendError('STT_ERROR', `${this.stt?.name} failed, switching to fallback`, true);

    const fallbackAdapter = await createSTTAdapter(
      'openai-whisper',
      this.factoryConfig,
      this.sttConfig,
    );
    await this.stt?.dispose();
    this.stt = fallbackAdapter;

    // Re-subscribe to transcript callbacks
    this.wireSTTCallbacks();

    return await fn();
  }
}

private async withTTSFallback(text: string): AsyncIterable<Buffer> {
  try {
    yield* this.tts!.synthesize(text);
  } catch {
    this.sendError('TTS_ERROR', `${this.tts?.name} failed, switching to fallback`, true);

    const fallbackAdapter = await createTTSAdapter(
      'openai-tts',
      this.factoryConfig,
      this.ttsConfig,
    );
    await this.tts?.dispose();
    this.tts = fallbackAdapter;

    yield* this.tts.synthesize(text);
  }
}
```

- [ ] Add session resume on server:

```typescript
// In voice route handler:
// If session.start includes resumeSessionId, look up existing session
// If found, reattach WebSocket and send state snapshot
// If not found, create new session (resume not possible)
```

### Files to Modify/Create

- MODIFY `packages/dashboard/src/hooks/useVoiceSession.ts` -- add reconnection logic
- MODIFY `packages/voice/src/voice-session.ts` -- add provider fallback wrappers
- CREATE `packages/voice/src/voice-session-reconnect.test.ts`
- CREATE `packages/dashboard/src/hooks/useVoiceSession-reconnect.test.ts`

### Dependencies

- [ ] Story 24-2: STT adapters and factory
- [ ] Story 24-3: TTS adapters and factory
- [ ] Story 24-5 Task 3: ConnectionStatus component (shows reconnect attempts)

## Testing Strategy

### Unit Tests -- useVoiceSession-reconnect.test.ts

- [ ] Test WebSocket close (non-1000) triggers reconnection
- [ ] Test reconnection uses exponential backoff (1s, 2s, 4s, 8s, ...)
- [ ] Test backoff caps at 30s
- [ ] Test successful reconnect resets attempt counter
- [ ] Test max attempts exceeded transitions to error state
- [ ] Test auth failure (4001) does not trigger reconnect
- [ ] Test normal close (1000) does not trigger reconnect
- [ ] Test reconnect does not re-request mic permission
- [ ] Test reconnect sends resumeSessionId
- [ ] Test state set to 'connecting' during reconnect
- [ ] Test reconnect timer cleared on manual disconnect
- [ ] Test reconnect timer cleared on component unmount

### Unit Tests -- voice-session-reconnect.test.ts

- [ ] Test STT error triggers fallback to Whisper
- [ ] Test STT fallback sends recoverable error to client
- [ ] Test STT callbacks re-wired after fallback
- [ ] Test TTS error triggers fallback to OpenAI TTS
- [ ] Test TTS fallback sends recoverable error to client
- [ ] Test fallback adapter used for subsequent calls
- [ ] Test session resume with existing sessionId
- [ ] Test session resume with unknown sessionId creates new session

### Validation Steps

1. [ ] Implement browser reconnection with exponential backoff
2. [ ] Implement server-side provider fallback
3. [ ] Implement session resume
4. [ ] Test reconnection by simulating WebSocket drop
5. [ ] Test provider fallback by simulating adapter errors
6. [ ] Run all unit tests
7. [ ] Verify TypeScript compiles

## Notes & Considerations

- Reconnection preserves the AudioContext and MediaStream. Re-requesting mic permission on every reconnect would be a terrible user experience. Only the WebSocket is reconnected.
- The `resumeSessionId` in `session.start` tells the server to reattach to an existing session if possible. If the server has already cleaned up the session (e.g., after timeout), it creates a new one.
- Provider fallback is designed to be seamless: the user gets a brief text notification ("Switching to fallback provider") but voice continues without interruption. The fallback adapter is used for all subsequent calls until the session ends.
- The exponential backoff pattern (1s, 2s, 4s, 8s, 16s, 30s) matches the existing `RemoteTransport` reconnection pattern.

## Completion Checklist

- [ ] Browser reconnection with exponential backoff
- [ ] Max attempts with error state
- [ ] Auth failure exempted from reconnect
- [ ] Session state preserved across reconnects
- [ ] STT provider fallback (Deepgram -> Whisper)
- [ ] TTS provider fallback (ElevenLabs -> OpenAI)
- [ ] Recoverable error notifications to client
- [ ] Session resume on reconnect
- [ ] All unit tests passing
- [ ] TypeScript compiles
