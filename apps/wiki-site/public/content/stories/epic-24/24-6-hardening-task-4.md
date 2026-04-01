---
title: "Task 4: Integration Tests + Load Test + Performance Verification"
sidebar:
  order: 240
---

**Story:** 24-6-hardening - Hardening + Production Readiness
**Epic:** 24

## Task Description

Write comprehensive integration tests with mock STT/TTS providers that verify the full voice pipeline end-to-end. Create a load test script that verifies 10 concurrent voice sessions work without degradation on the VPS. Verify the end-to-end latency target of <1.5s.

## Acceptance Criteria

- Integration tests cover: connection, STT transcription, TTS playback, intent classification, engine commands
- Tests use mock STT/TTS providers (no real API calls, no API keys needed)
- Mock STT provider: accepts audio, returns configurable transcripts after delay
- Mock TTS provider: accepts text, yields audio chunks after delay
- Load test: 10 concurrent WebSocket voice sessions with simulated audio
- Load test verifies: no session drops, no audio corruption, acceptable latency
- Performance test: measure end-to-end latency (speech end -> first audio response)
- Performance target: p95 <1.5s with mock providers
- All tests pass in CI without external dependencies

## Implementation Details

### Technical Requirements

- [ ] Create mock providers for testing:

```typescript
// packages/voice/src/__test-utils__/mock-stt-adapter.ts
import type { ISTTAdapter, STTConfig } from '@tamma/shared/contracts';

export class MockSTTAdapter implements ISTTAdapter {
  readonly name = 'mock-stt';
  private interimCallbacks: Array<(text: string) => void> = [];
  private finalCallbacks: Array<(text: string, confidence: number) => void> = [];
  private connected = false;

  /** Configurable response for the next endUtterance call. */
  nextTranscript: string = 'hello world';
  nextConfidence: number = 0.95;
  transcribeDelayMs: number = 100;

  async connect(config: STTConfig): Promise<void> {
    this.connected = true;
  }

  sendAudio(pcm16: Buffer): void {
    // Simulate interim transcript on audio receive
    if (this.connected && this.interimCallbacks.length > 0) {
      for (const cb of this.interimCallbacks) {
        cb(this.nextTranscript.slice(0, 5)); // Partial
      }
    }
  }

  onInterimTranscript(cb: (text: string) => void): () => void {
    this.interimCallbacks.push(cb);
    return () => { this.interimCallbacks = this.interimCallbacks.filter(c => c !== cb); };
  }

  onFinalTranscript(cb: (text: string, confidence: number) => void): () => void {
    this.finalCallbacks.push(cb);
    return () => { this.finalCallbacks = this.finalCallbacks.filter(c => c !== cb); };
  }

  endUtterance(): void {
    setTimeout(() => {
      for (const cb of this.finalCallbacks) {
        cb(this.nextTranscript, this.nextConfidence);
      }
    }, this.transcribeDelayMs);
  }

  async dispose(): Promise<void> {
    this.connected = false;
    this.interimCallbacks = [];
    this.finalCallbacks = [];
  }
}
```

```typescript
// packages/voice/src/__test-utils__/mock-tts-adapter.ts
import type { ITTSAdapter, TTSConfig } from '@tamma/shared/contracts';

export class MockTTSAdapter implements ITTSAdapter {
  readonly name = 'mock-tts';
  private cancelled = false;

  /** Configurable delay before yielding audio. */
  firstChunkDelayMs: number = 50;
  chunkCount: number = 3;
  chunkSizeBytes: number = 640;

  async connect(config: TTSConfig): Promise<void> {}

  async *synthesize(text: string): AsyncIterable<Buffer> {
    this.cancelled = false;

    await new Promise(r => setTimeout(r, this.firstChunkDelayMs));

    for (let i = 0; i < this.chunkCount; i++) {
      if (this.cancelled) return;
      yield Buffer.alloc(this.chunkSizeBytes); // Silent audio
      await new Promise(r => setTimeout(r, 20));
    }
  }

  cancel(): void {
    this.cancelled = true;
  }

  async dispose(): Promise<void> {}
}
```

- [ ] Create integration test `packages/api/src/routes/voice/__tests__/voice-full-pipeline.test.ts`:

```typescript
describe('Voice Full Pipeline Integration', () => {
  // Setup Fastify with mock providers

  it('full voice conversation: connect -> speak -> transcribe -> respond -> TTS', async () => {
    // 1. Connect WebSocket with JWT
    // 2. Send session.start
    // 3. Receive session.ready
    // 4. Send binary audio frames (simulated PCM16)
    // 5. Send input.end
    // 6. Receive transcript.final
    // 7. Receive response.text
    // 8. Receive binary audio frames (TTS)
    // 9. Receive response.end
  });

  it('engine command via voice: speak "approve" -> engine receives approve', async () => {
    // 1. Connect + start session
    // 2. Mock STT returns "approve the plan"
    // 3. Intent classifier routes to engine command
    // 4. Verify transport.sendCommand({ type: 'approve' }) called
    // 5. Receive spoken confirmation
  });

  it('interruption: user speaks during TTS -> TTS cancelled', async () => {
    // 1. Connect + start session
    // 2. Send text.input -> triggers TTS
    // 3. During TTS, send input.start
    // 4. Receive response.cancel
    // 5. TTS audio stops
  });

  it('provider fallback: STT fails mid-session -> switches to fallback', async () => {
    // 1. Connect with failing mock STT
    // 2. First endUtterance -> STT throws
    // 3. Receive error with recoverable: true
    // 4. Second endUtterance uses fallback -> success
  });
});
```

- [ ] Create load test `packages/voice/src/__test-utils__/load-test.ts`:

```typescript
/**
 * Load test: simulate N concurrent voice sessions.
 * Run with: npx tsx packages/voice/src/__test-utils__/load-test.ts
 */
import WebSocket from 'ws';

const CONCURRENT_SESSIONS = 10;
const SESSION_DURATION_MS = 30_000;
const AUDIO_CHUNK_INTERVAL_MS = 20;

async function runSession(id: number, baseUrl: string): Promise<SessionResult> {
  const start = Date.now();
  let messagesReceived = 0;
  let errors = 0;

  return new Promise((resolve) => {
    const ws = new WebSocket(`${baseUrl}/api/v1/voice`, {
      headers: { Authorization: `Bearer ${testJWT}` },
    });

    ws.on('open', () => {
      ws.send(JSON.stringify({ type: 'session.start', config: {} }));

      // Simulate audio streaming
      const audioInterval = setInterval(() => {
        if (ws.readyState === WebSocket.OPEN) {
          ws.send(Buffer.alloc(640)); // Silent PCM16
        }
      }, AUDIO_CHUNK_INTERVAL_MS);

      // Simulate periodic speech end (every 5 seconds)
      const speechInterval = setInterval(() => {
        ws.send(JSON.stringify({ type: 'input.end' }));
      }, 5_000);

      setTimeout(() => {
        clearInterval(audioInterval);
        clearInterval(speechInterval);
        ws.send(JSON.stringify({ type: 'session.end' }));
        ws.close();
      }, SESSION_DURATION_MS);
    });

    ws.on('message', () => { messagesReceived++; });
    ws.on('error', () => { errors++; });
    ws.on('close', () => {
      resolve({
        sessionId: id,
        durationMs: Date.now() - start,
        messagesReceived,
        errors,
      });
    });
  });
}

interface SessionResult {
  sessionId: number;
  durationMs: number;
  messagesReceived: number;
  errors: number;
}

async function main(): Promise<void> {
  const baseUrl = process.env.BASE_URL ?? 'ws://localhost:3100';
  console.log(`Starting ${CONCURRENT_SESSIONS} concurrent voice sessions...`);

  const results = await Promise.all(
    Array.from({ length: CONCURRENT_SESSIONS }, (_, i) => runSession(i, baseUrl)),
  );

  const totalErrors = results.reduce((sum, r) => sum + r.errors, 0);
  const avgMessages = results.reduce((sum, r) => sum + r.messagesReceived, 0) / results.length;

  console.log(`Results:`);
  console.log(`  Sessions: ${results.length}`);
  console.log(`  Total errors: ${totalErrors}`);
  console.log(`  Avg messages per session: ${Math.round(avgMessages)}`);
  console.log(`  All sessions completed: ${results.every(r => r.errors === 0)}`);

  process.exit(totalErrors > 0 ? 1 : 0);
}

void main();
```

- [ ] Create latency measurement test:

```typescript
// In integration tests:
it('measures end-to-end latency < 1.5s', async () => {
  // Connect + start session with mock providers
  // Send audio frames
  // Send input.end
  // Measure time until first binary audio frame received
  const start = Date.now();

  // ... send input.end ...

  // Wait for first binary frame (TTS audio)
  const firstAudioFrame = await waitForBinaryMessage(ws, 5000);
  const latency = Date.now() - start;

  expect(latency).toBeLessThan(1500); // <1.5s
  console.log(`End-to-end latency: ${latency}ms`);
});
```

### Files to Modify/Create

- CREATE `packages/voice/src/__test-utils__/mock-stt-adapter.ts`
- CREATE `packages/voice/src/__test-utils__/mock-tts-adapter.ts`
- CREATE `packages/api/src/routes/voice/__tests__/voice-full-pipeline.test.ts`
- CREATE `packages/voice/src/__test-utils__/load-test.ts`

### Dependencies

- [ ] All previous stories (24-1 through 24-5)
- [ ] `ws` package for load test client

## Testing Strategy

### Integration Tests

- [ ] Full voice conversation pipeline (audio in -> transcript -> LLM -> TTS -> audio out)
- [ ] Engine command via voice (approve, reject, start, etc.)
- [ ] Interruption handling (user speaks during TTS)
- [ ] Provider fallback (STT/TTS failure mid-session)
- [ ] Session timeout (idle disconnect)
- [ ] Rate limiting (second session replaces first)
- [ ] Multi-turn context preservation
- [ ] Hybrid mode (voice + text input)
- [ ] WebSocket reconnection
- [ ] End-to-end latency measurement

### Load Test

- [ ] 10 concurrent sessions sustained for 30 seconds
- [ ] No session drops (all complete without errors)
- [ ] No significant latency increase under load
- [ ] Memory usage stable (no leaks)

### Performance Targets

| Metric | Target | Measurement |
|--------|--------|-------------|
| End-to-end latency (speech end -> first audio) | p95 < 1.5s | With mock providers (0 network latency to STT/TTS) |
| WebSocket upgrade | < 100ms | From HTTP request to WS open |
| Intent classification | < 300ms | LLM call with mock provider |
| Session creation | < 50ms | VoiceSession + adapter setup |
| Concurrent sessions | 10 | Without degradation on VPS |

### Validation Steps

1. [ ] Create mock STT/TTS adapters with configurable behavior
2. [ ] Write full pipeline integration tests
3. [ ] Write load test script
4. [ ] Run integration tests in CI
5. [ ] Run load test on local dev server
6. [ ] Verify latency targets met
7. [ ] Verify no resource leaks under load

## Notes & Considerations

- Mock providers simulate realistic delays (100ms STT transcription, 50ms TTS first chunk) to make latency measurements meaningful.
- The load test is a standalone script, not a Vitest test. It runs against a real Fastify server with mock providers. It can also be pointed at a deployed server for production testing.
- Memory leak detection: run the load test for 5+ minutes and monitor Node.js heap size. It should remain stable, not grow continuously.
- The latency measurement test uses mock providers, so the measured latency represents the server-side processing overhead only. Real-world latency will be higher due to network round-trips to STT/TTS APIs.
- CI runs integration tests but not the load test (too resource-intensive). Load testing is done manually before releases.

## Completion Checklist

- [ ] Mock STT adapter with configurable transcripts and delays
- [ ] Mock TTS adapter with configurable audio generation
- [ ] Full pipeline integration tests
- [ ] Engine command via voice test
- [ ] Interruption test
- [ ] Provider fallback test
- [ ] Latency measurement test (p95 < 1.5s)
- [ ] Load test script for 10 concurrent sessions
- [ ] All integration tests passing in CI
- [ ] Load test verified on dev server
- [ ] TypeScript compiles
