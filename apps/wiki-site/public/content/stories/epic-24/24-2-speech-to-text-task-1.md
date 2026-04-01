---
title: "Task 1: ISTTAdapter Interface + DeepgramAdapter Implementation"
sidebar:
  order: 240
---

**Story:** 24-2-speech-to-text - Speech-to-Text Integration
**Epic:** 24

## Task Description

Implement the `DeepgramAdapter` class that implements the `ISTTAdapter` interface (defined in Story 24-1). The adapter connects to Deepgram's Nova-3 streaming WebSocket API, sends PCM16 audio frames, and receives interim/final transcripts with sub-300ms latency.

## Acceptance Criteria

- `DeepgramAdapter` implements `ISTTAdapter` from `@tamma/shared/contracts`
- `connect()` opens a WebSocket to Deepgram's streaming API with auth header
- `sendAudio()` forwards PCM16 buffer to Deepgram WebSocket as binary frame
- `onInterimTranscript()` fires callback on Deepgram `SpeechStarted` + `Results` (is_final=false)
- `onFinalTranscript()` fires callback on Deepgram `Results` (is_final=true) with confidence score
- `endUtterance()` sends Deepgram's `Finalize` message to force final transcript
- `dispose()` sends `CloseStream` message and closes WebSocket cleanly
- Reconnection on Deepgram WebSocket drop (up to 3 retries with backoff)
- `DEEPGRAM_API_KEY` env var read from server config (never exposed to client)
- Unit tests with mock WebSocket (no real Deepgram calls)

## Implementation Details

### Technical Requirements

- [ ] Add `ws` as a dependency in `packages/voice/package.json` (for server-side WebSocket client to Deepgram)
- [ ] Create `packages/voice/src/stt/stt-adapter.ts` (re-export from shared contracts for convenience):

```typescript
// Re-export the interface from shared contracts
export type { ISTTAdapter, STTConfig } from '@tamma/shared/contracts/voice-transport.js';
```

- [ ] Create `packages/voice/src/stt/deepgram-adapter.ts`:

```typescript
import { WebSocket } from 'ws';
import type { ISTTAdapter, STTConfig } from '@tamma/shared/contracts';

/** Deepgram streaming API message types */
interface DeepgramResult {
  type: 'Results';
  channel_index: [number, number];
  duration: number;
  start: number;
  is_final: boolean;
  speech_final: boolean;
  channel: {
    alternatives: Array<{
      transcript: string;
      confidence: number;
      words: Array<{ word: string; start: number; end: number; confidence: number }>;
    }>;
  };
}

interface DeepgramSpeechStarted {
  type: 'SpeechStarted';
  channel_index: [number];
  timestamp: number;
}

interface DeepgramMetadata {
  type: 'Metadata';
  transaction_key: string;
  request_id: string;
  sha256: string;
  created: string;
  duration: number;
  channels: number;
  models: string[];
  model_info: Record<string, unknown>;
}

type DeepgramMessage = DeepgramResult | DeepgramSpeechStarted | DeepgramMetadata;

export interface DeepgramAdapterConfig {
  apiKey: string;
  model?: string;          // default: 'nova-3'
  baseUrl?: string;        // default: 'wss://api.deepgram.com/v1/listen'
  maxReconnectAttempts?: number;  // default: 3
}

export class DeepgramAdapter implements ISTTAdapter {
  readonly name = 'deepgram';

  private ws: WebSocket | null = null;
  private readonly apiKey: string;
  private readonly model: string;
  private readonly baseUrl: string;
  private readonly maxReconnectAttempts: number;
  private reconnectAttempts = 0;
  private disposed = false;
  private config: STTConfig | null = null;

  // Callback registries
  private interimCallbacks: Array<(text: string) => void> = [];
  private finalCallbacks: Array<(text: string, confidence: number) => void> = [];

  constructor(config: DeepgramAdapterConfig);

  async connect(config: STTConfig): Promise<void> {
    // Build URL with query params:
    // wss://api.deepgram.com/v1/listen?model=nova-3&language=en-US&encoding=linear16
    //   &sample_rate=16000&channels=1&interim_results=true&smart_format=true
    //   &endpointing=200&utterance_end_ms=1500
    //
    // Headers: { Authorization: 'Token <apiKey>' }
    //
    // Wire onmessage to handleDeepgramMessage()
    // Wire onclose to handleDisconnect()
    // Wire onerror to handleError()
    // Wait for WebSocket open event
  }

  sendAudio(pcm16: Buffer): void {
    // Guard: if ws is null or not OPEN, silently drop (audio during reconnect)
    // ws.send(pcm16) -- binary frame
  }

  onInterimTranscript(cb: (text: string) => void): () => void {
    this.interimCallbacks.push(cb);
    return () => {
      this.interimCallbacks = this.interimCallbacks.filter(c => c !== cb);
    };
  }

  onFinalTranscript(cb: (text: string, confidence: number) => void): () => void {
    this.finalCallbacks.push(cb);
    return () => {
      this.finalCallbacks = this.finalCallbacks.filter(c => c !== cb);
    };
  }

  endUtterance(): void {
    // Send Deepgram Finalize message to force a final transcript
    // { type: 'Finalize' }
    if (this.ws?.readyState === WebSocket.OPEN) {
      this.ws.send(JSON.stringify({ type: 'Finalize' }));
    }
  }

  async dispose(): Promise<void> {
    if (this.disposed) return;
    this.disposed = true;
    // Send CloseStream message
    if (this.ws?.readyState === WebSocket.OPEN) {
      this.ws.send(JSON.stringify({ type: 'CloseStream' }));
    }
    this.ws?.close();
    this.ws = null;
    this.interimCallbacks = [];
    this.finalCallbacks = [];
  }

  // --- Private ---

  private handleDeepgramMessage(data: string): void {
    const msg = JSON.parse(data) as DeepgramMessage;
    if (msg.type === 'Results') {
      const alt = msg.channel.alternatives[0];
      if (!alt || alt.transcript === '') return;
      if (msg.is_final) {
        for (const cb of this.finalCallbacks) cb(alt.transcript, alt.confidence);
      } else {
        for (const cb of this.interimCallbacks) cb(alt.transcript);
      }
    }
    // SpeechStarted and Metadata are informational; ignore for now
  }

  private handleDisconnect(code: number, reason: string): void {
    if (this.disposed) return;
    if (this.reconnectAttempts < this.maxReconnectAttempts) {
      this.reconnectAttempts++;
      const delay = Math.pow(2, this.reconnectAttempts) * 500;
      setTimeout(() => {
        if (!this.disposed && this.config) {
          void this.connect(this.config);
        }
      }, delay);
    }
  }

  private handleError(error: Error): void {
    // Log error, trigger reconnect
  }
}
```

### Files to Modify/Create

- CREATE `packages/voice/src/stt/stt-adapter.ts`
- CREATE `packages/voice/src/stt/deepgram-adapter.ts`
- CREATE `packages/voice/src/stt/deepgram-adapter.test.ts`
- MODIFY `packages/voice/src/index.ts` -- add stt exports
- MODIFY `packages/voice/package.json` -- add `ws` dependency

### Dependencies

- [ ] Story 24-1 Task 1: `ISTTAdapter`, `STTConfig` from `@tamma/shared/contracts`
- [ ] `ws` package for server-side WebSocket client

## Testing Strategy

### Unit Tests -- deepgram-adapter.test.ts

- [ ] Test `connect()` opens WebSocket to correct URL with model/language/encoding params
- [ ] Test `connect()` sends auth header with API key
- [ ] Test `sendAudio()` forwards Buffer as binary WebSocket frame
- [ ] Test `sendAudio()` silently drops data when WebSocket is not open
- [ ] Test interim transcript callback fires on `Results` with `is_final: false`
- [ ] Test final transcript callback fires on `Results` with `is_final: true`
- [ ] Test confidence score passed to final transcript callback
- [ ] Test empty transcript (`transcript: ''`) is ignored
- [ ] Test `endUtterance()` sends `{ type: 'Finalize' }` JSON message
- [ ] Test `dispose()` sends `{ type: 'CloseStream' }` and closes WebSocket
- [ ] Test `dispose()` is idempotent
- [ ] Test callback unsubscribe works (returned function removes callback)
- [ ] Test reconnection on WebSocket close (attempts < maxReconnectAttempts)
- [ ] Test reconnection stops after maxReconnectAttempts
- [ ] Test reconnection not attempted after dispose()
- [ ] Test `Metadata` message type is safely ignored
- [ ] Test `SpeechStarted` message type is safely ignored
- [ ] Test malformed JSON from Deepgram does not crash (caught and logged)

### Mocking Strategy

```typescript
// Mock ws.WebSocket with EventEmitter
class MockWebSocket extends EventEmitter {
  static OPEN = 1;
  readyState = MockWebSocket.OPEN;
  send = vi.fn();
  close = vi.fn();
}

// Mock Deepgram messages
const mockFinalResult: DeepgramResult = {
  type: 'Results',
  channel_index: [0, 1],
  duration: 1.5,
  start: 0,
  is_final: true,
  speech_final: true,
  channel: {
    alternatives: [{
      transcript: 'hello world',
      confidence: 0.98,
      words: [],
    }],
  },
};
```

### Validation Steps

1. [ ] Create DeepgramAdapter with WebSocket connection to Deepgram URL
2. [ ] Verify URL construction with all query parameters
3. [ ] Test audio forwarding and transcript callbacks
4. [ ] Test reconnection logic
5. [ ] Run all unit tests with mock WebSocket
6. [ ] Verify TypeScript strict mode compilation

## Notes & Considerations

- Deepgram's streaming API URL format: `wss://api.deepgram.com/v1/listen?model=nova-3&language=en-US&encoding=linear16&sample_rate=16000&channels=1&interim_results=true&smart_format=true&endpointing=200&utterance_end_ms=1500`
- `endpointing=200` means Deepgram will detect end of speech after 200ms of silence. `utterance_end_ms=1500` is the max time before forcing a final result.
- The `Finalize` control message tells Deepgram to immediately produce a final transcript for any buffered audio. This is sent when client-side VAD detects speech end.
- API key is stored in `DEEPGRAM_API_KEY` env var and accessed via server config. It is never sent to the browser.
- PCM16 at 16kHz mono = 32KB/s. Each `sendAudio()` call typically sends 20ms chunks (640 bytes).

## Completion Checklist

- [ ] `DeepgramAdapter` implements `ISTTAdapter`
- [ ] WebSocket connection to Deepgram with correct URL params
- [ ] Audio forwarding works
- [ ] Interim and final transcript callbacks fire correctly
- [ ] `endUtterance()` sends Finalize message
- [ ] Reconnection on disconnect
- [ ] `dispose()` sends CloseStream and cleans up
- [ ] All unit tests passing with mock WebSocket
- [ ] TypeScript strict mode compiles
