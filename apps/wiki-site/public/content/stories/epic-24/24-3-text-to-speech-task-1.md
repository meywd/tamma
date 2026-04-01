---
title: "Task 1: ITTSAdapter Interface + ElevenLabsAdapter Implementation"
sidebar:
  order: 240
---

**Story:** 24-3-text-to-speech - Text-to-Speech Integration
**Epic:** 24

## Task Description

Implement the `ElevenLabsAdapter` class that implements the `ITTSAdapter` interface (defined in Story 24-1). The adapter connects to ElevenLabs' streaming WebSocket API, sends text, and yields PCM16 audio chunks as they arrive, targeting ~75ms time-to-first-byte.

## Acceptance Criteria

- `ElevenLabsAdapter` implements `ITTSAdapter` from `@tamma/shared/contracts`
- `connect()` validates config (voice ID, model)
- `synthesize(text)` returns `AsyncIterable<Buffer>` of PCM16 audio chunks
- Streaming: audio chunks yielded as they arrive from ElevenLabs WebSocket, not after full synthesis
- `cancel()` aborts in-progress synthesis and closes the WebSocket stream
- `dispose()` cleans up all resources
- Uses ElevenLabs Flash v2.5 model for low latency
- `ELEVENLABS_API_KEY` env var read from server config
- Unit tests with mock WebSocket (no real ElevenLabs calls)

## Implementation Details

### Technical Requirements

- [ ] Create `packages/voice/src/tts/tts-adapter.ts` (re-export for convenience):

```typescript
export type { ITTSAdapter, TTSConfig } from '@tamma/shared/contracts/voice-transport.js';
```

- [ ] Create `packages/voice/src/tts/elevenlabs-adapter.ts`:

```typescript
import { WebSocket } from 'ws';
import type { ITTSAdapter, TTSConfig } from '@tamma/shared/contracts';

export interface ElevenLabsAdapterConfig {
  apiKey: string;
  model?: string;            // default: 'eleven_flash_v2_5'
  voiceId?: string;          // default from TTSConfig.voice
  baseUrl?: string;          // default: 'wss://api.elevenlabs.io/v1/text-to-speech'
  optimizeStreamingLatency?: number;  // default: 4 (max optimization)
}

export class ElevenLabsAdapter implements ITTSAdapter {
  readonly name = 'elevenlabs';

  private readonly apiKey: string;
  private readonly model: string;
  private readonly baseUrl: string;
  private readonly optimizeStreamingLatency: number;
  private config: TTSConfig | null = null;
  private currentWs: WebSocket | null = null;
  private cancelled = false;
  private disposed = false;

  constructor(config: ElevenLabsAdapterConfig);

  async connect(config: TTSConfig): Promise<void> {
    // Store config, validate voice/language
    this.config = config;
  }

  async *synthesize(text: string): AsyncIterable<Buffer> {
    if (this.disposed || !this.config) {
      throw new Error('ElevenLabsAdapter not connected');
    }

    this.cancelled = false;

    // Build WebSocket URL:
    // wss://api.elevenlabs.io/v1/text-to-speech/{voiceId}/stream-input
    //   ?model_id=eleven_flash_v2_5
    //   &output_format=pcm_16000
    //   &optimize_streaming_latency=4
    const voiceId = this.config.voice;
    const url = `${this.baseUrl}/${voiceId}/stream-input` +
      `?model_id=${this.model}` +
      `&output_format=pcm_${this.config.sampleRate}` +
      `&optimize_streaming_latency=${this.optimizeStreamingLatency}`;

    const ws = new WebSocket(url, {
      headers: { 'xi-api-key': this.apiKey },
    });
    this.currentWs = ws;

    // Create an async queue to yield audio chunks
    const audioQueue: Array<Buffer | null> = []; // null = end of stream
    let resolve: (() => void) | null = null;

    ws.on('open', () => {
      // Send BOS (beginning of stream) message
      ws.send(JSON.stringify({
        text: ' ',
        voice_settings: {
          stability: 0.5,
          similarity_boost: 0.8,
          use_speaker_boost: true,
        },
        generation_config: {
          chunk_length_schedule: [120, 160, 250, 290],
        },
        xi_api_key: this.apiKey,
      }));

      // Send the actual text
      ws.send(JSON.stringify({ text: text + ' ', flush: true }));

      // Send EOS (end of stream)
      ws.send(JSON.stringify({ text: '' }));
    });

    ws.on('message', (data: Buffer | string) => {
      const msg = JSON.parse(data.toString()) as {
        audio?: string;         // base64 encoded audio
        isFinal?: boolean;
        alignment?: unknown;
        normalizedAlignment?: unknown;
      };

      if (msg.audio) {
        const pcm = Buffer.from(msg.audio, 'base64');
        audioQueue.push(pcm);
        if (resolve) { resolve(); resolve = null; }
      }

      if (msg.isFinal) {
        audioQueue.push(null); // Signal end
        if (resolve) { resolve(); resolve = null; }
      }
    });

    ws.on('close', () => {
      audioQueue.push(null);
      if (resolve) { resolve(); resolve = null; }
      this.currentWs = null;
    });

    ws.on('error', () => {
      audioQueue.push(null);
      if (resolve) { resolve(); resolve = null; }
      this.currentWs = null;
    });

    // Yield audio chunks from queue
    while (true) {
      if (this.cancelled) {
        ws.close();
        return;
      }

      if (audioQueue.length > 0) {
        const chunk = audioQueue.shift()!;
        if (chunk === null) return; // End of stream
        yield chunk;
      } else {
        // Wait for next chunk
        await new Promise<void>((r) => { resolve = r; });
      }
    }
  }

  cancel(): void {
    this.cancelled = true;
    if (this.currentWs?.readyState === WebSocket.OPEN) {
      this.currentWs.close();
    }
    this.currentWs = null;
  }

  async dispose(): Promise<void> {
    if (this.disposed) return;
    this.disposed = true;
    this.cancel();
  }
}
```

### Files to Modify/Create

- CREATE `packages/voice/src/tts/tts-adapter.ts`
- CREATE `packages/voice/src/tts/elevenlabs-adapter.ts`
- CREATE `packages/voice/src/tts/elevenlabs-adapter.test.ts`
- MODIFY `packages/voice/src/index.ts` -- add tts exports

### Dependencies

- [ ] Story 24-1 Task 1: `ITTSAdapter`, `TTSConfig` from `@tamma/shared/contracts`
- [ ] `ws` package (already in voice package from STT task)

## Testing Strategy

### Unit Tests -- elevenlabs-adapter.test.ts

- [ ] Test `connect()` stores config
- [ ] Test `synthesize()` opens WebSocket to correct URL with voice ID, model, output_format
- [ ] Test `synthesize()` sends BOS message with voice settings on open
- [ ] Test `synthesize()` sends text with flush=true
- [ ] Test `synthesize()` sends EOS (empty text)
- [ ] Test audio chunks yielded as they arrive (base64 decoded to Buffer)
- [ ] Test iteration ends when `isFinal: true` received
- [ ] Test iteration ends when WebSocket closes
- [ ] Test `cancel()` closes WebSocket and stops iteration
- [ ] Test `cancel()` during active synthesis stops yielding
- [ ] Test `dispose()` calls cancel and sets disposed flag
- [ ] Test `dispose()` is idempotent
- [ ] Test `synthesize()` after dispose throws
- [ ] Test WebSocket error closes stream gracefully
- [ ] Test multiple sequential synthesize calls (second after first completes)
- [ ] Test API key passed as xi-api-key header

### Mocking Strategy

```typescript
class MockWebSocket extends EventEmitter {
  static OPEN = 1;
  readyState = MockWebSocket.OPEN;
  send = vi.fn();
  close = vi.fn();

  // Simulate ElevenLabs response
  simulateAudio(base64: string) {
    this.emit('message', JSON.stringify({ audio: base64 }));
  }
  simulateEnd() {
    this.emit('message', JSON.stringify({ isFinal: true }));
  }
}
```

### Validation Steps

1. [ ] Create ElevenLabsAdapter with WebSocket streaming
2. [ ] Verify URL construction with all parameters
3. [ ] Test audio chunk yielding from AsyncIterable
4. [ ] Test cancellation mid-stream
5. [ ] Run all unit tests
6. [ ] Verify TypeScript strict mode compilation

## Notes & Considerations

- ElevenLabs' streaming WebSocket API uses a text-in/audio-out protocol. You send text chunks and receive base64-encoded audio chunks back. The `flush: true` flag tells ElevenLabs to start generating audio immediately rather than waiting for more text.
- The `output_format=pcm_16000` parameter tells ElevenLabs to return raw PCM16 audio at 16kHz, matching our pipeline. No transcoding needed.
- `optimize_streaming_latency=4` is the maximum optimization level, reducing first-byte latency at the cost of slightly lower quality. For conversational use, this tradeoff is worth it.
- The `chunk_length_schedule` in the BOS message controls how much text ElevenLabs buffers before starting audio generation. Smaller values = lower latency.
- The AsyncIterable pattern allows the caller to consume audio chunks as they arrive using `for await...of`. This is important for streaming TTS audio to the browser.

## Completion Checklist

- [ ] `ElevenLabsAdapter` implements `ITTSAdapter`
- [ ] WebSocket connection to ElevenLabs with correct URL/params
- [ ] BOS/text/EOS message sequence sent correctly
- [ ] Audio chunks yielded as AsyncIterable
- [ ] Base64 decoding of audio chunks
- [ ] cancel() stops in-progress synthesis
- [ ] dispose() cleans up all resources
- [ ] All unit tests passing
- [ ] TypeScript strict mode compiles
