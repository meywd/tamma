---
title: "Task 2: OpenAITTSAdapter (Streaming Fallback)"
sidebar:
  order: 240
---

**Story:** 24-3-text-to-speech - Text-to-Speech Integration
**Epic:** 24

## Task Description

Implement the `OpenAITTSAdapter` as a streaming fallback TTS adapter using OpenAI's `tts-1` model. When ElevenLabs is unavailable, this adapter sends text to OpenAI's TTS API and streams PCM16 audio chunks back.

## Acceptance Criteria

- `OpenAITTSAdapter` implements `ITTSAdapter` from `@tamma/shared/contracts`
- `connect()` validates config and initializes state
- `synthesize(text)` returns `AsyncIterable<Buffer>` of PCM16 audio chunks via streaming response
- Uses OpenAI's `POST /v1/audio/speech` endpoint with `response_format: pcm`
- `cancel()` aborts in-progress HTTP request
- `dispose()` cleans up resources
- Uses `OPENAI_API_KEY` env var (shared with LLM provider)
- Unit tests with mock HTTP (no real OpenAI calls)

## Implementation Details

### Technical Requirements

- [ ] Create `packages/voice/src/tts/openai-tts-adapter.ts`:

```typescript
import type { ITTSAdapter, TTSConfig } from '@tamma/shared/contracts';

export interface OpenAITTSAdapterConfig {
  apiKey: string;
  model?: string;        // default: 'tts-1'
  baseUrl?: string;      // default: 'https://api.openai.com/v1'
}

export class OpenAITTSAdapter implements ITTSAdapter {
  readonly name = 'openai-tts';

  private readonly apiKey: string;
  private readonly model: string;
  private readonly baseUrl: string;
  private config: TTSConfig | null = null;
  private abortController: AbortController | null = null;
  private disposed = false;

  constructor(config: OpenAITTSAdapterConfig);

  async connect(config: TTSConfig): Promise<void> {
    this.config = config;
  }

  async *synthesize(text: string): AsyncIterable<Buffer> {
    if (this.disposed || !this.config) {
      throw new Error('OpenAITTSAdapter not connected');
    }

    const abort = new AbortController();
    this.abortController = abort;

    try {
      const response = await fetch(`${this.baseUrl}/audio/speech`, {
        method: 'POST',
        headers: {
          'Authorization': `Bearer ${this.apiKey}`,
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({
          model: this.model,
          input: text,
          voice: this.config.voice,
          response_format: 'pcm',    // Raw PCM16 at 24kHz
          speed: 1.0,
        }),
        signal: abort.signal,
      });

      if (!response.ok || !response.body) {
        throw new Error(`OpenAI TTS error: ${response.status}`);
      }

      // Stream the response body
      const reader = response.body.getReader();

      // OpenAI TTS returns PCM at 24kHz. If our pipeline needs 16kHz,
      // we need to downsample. For simplicity, yield as-is and let the
      // playback adjust, or resample here.
      // Note: OpenAI's PCM format is 24kHz 16-bit mono.
      // If sampleRate is 16000, we need to downsample 24k -> 16k.

      while (true) {
        const { done, value } = await reader.read();
        if (done) break;

        if (abort.signal.aborted) return;

        // value is Uint8Array of PCM16 audio data
        const buffer = Buffer.from(value);

        // Downsample from 24kHz to 16kHz if needed
        if (this.config.sampleRate === 16_000) {
          yield this.downsample24to16(buffer);
        } else {
          yield buffer;
        }
      }
    } catch (err: unknown) {
      if (abort.signal.aborted) return; // Intentional cancel
      throw err;
    } finally {
      this.abortController = null;
    }
  }

  cancel(): void {
    if (this.abortController) {
      this.abortController.abort();
      this.abortController = null;
    }
  }

  async dispose(): Promise<void> {
    if (this.disposed) return;
    this.disposed = true;
    this.cancel();
  }

  // --- Private ---

  /**
   * Downsample PCM16 from 24kHz to 16kHz using linear interpolation.
   * Ratio: 24000/16000 = 3/2, so we produce 2 samples for every 3 input samples.
   */
  private downsample24to16(input: Buffer): Buffer {
    const inputSamples = input.length / 2; // 16-bit = 2 bytes per sample
    const outputSamples = Math.floor(inputSamples * 16_000 / 24_000);
    const output = Buffer.alloc(outputSamples * 2);

    for (let i = 0; i < outputSamples; i++) {
      const srcIndex = (i * 24_000) / 16_000;
      const srcFloor = Math.floor(srcIndex);
      const srcCeil = Math.min(srcFloor + 1, inputSamples - 1);
      const frac = srcIndex - srcFloor;

      const s0 = input.readInt16LE(srcFloor * 2);
      const s1 = input.readInt16LE(srcCeil * 2);
      const interpolated = Math.round(s0 + frac * (s1 - s0));

      output.writeInt16LE(Math.max(-32768, Math.min(32767, interpolated)), i * 2);
    }

    return output;
  }
}
```

### Files to Modify/Create

- CREATE `packages/voice/src/tts/openai-tts-adapter.ts`
- CREATE `packages/voice/src/tts/openai-tts-adapter.test.ts`
- MODIFY `packages/voice/src/index.ts` -- add OpenAI TTS export

### Dependencies

- [ ] Story 24-1 Task 1: `ITTSAdapter`, `TTSConfig` from `@tamma/shared/contracts`
- [ ] No additional npm packages (uses native `fetch`)

## Testing Strategy

### Unit Tests -- openai-tts-adapter.test.ts

- [ ] Test `connect()` stores config
- [ ] Test `synthesize()` sends POST to `/v1/audio/speech` with correct body
- [ ] Test `synthesize()` sends auth header with API key
- [ ] Test `synthesize()` request includes `response_format: 'pcm'`
- [ ] Test audio chunks yielded as Buffer from streaming response
- [ ] Test iteration ends when response body is fully consumed
- [ ] Test `cancel()` aborts the HTTP request
- [ ] Test `cancel()` during active synthesis stops yielding
- [ ] Test `dispose()` calls cancel and sets disposed flag
- [ ] Test `dispose()` is idempotent
- [ ] Test `synthesize()` after dispose throws
- [ ] Test HTTP error (non-200) throws with status code
- [ ] Test voice parameter sent from config
- [ ] Test downsampling from 24kHz to 16kHz produces correct buffer size
- [ ] Test downsampling preserves audio integrity (sample values in valid range)

### Mocking Strategy

```typescript
// Create a readable stream that yields chunks
function createMockStream(chunks: Uint8Array[]): ReadableStream {
  let index = 0;
  return new ReadableStream({
    pull(controller) {
      if (index < chunks.length) {
        controller.enqueue(chunks[index++]);
      } else {
        controller.close();
      }
    },
  });
}

const mockFetch = vi.fn().mockResolvedValue({
  ok: true,
  body: createMockStream([
    new Uint8Array([0x00, 0x01, 0x02, 0x03]), // PCM16 samples
  ]),
});
```

### Validation Steps

1. [ ] Create OpenAITTSAdapter with streaming HTTP response
2. [ ] Implement 24kHz -> 16kHz downsampling
3. [ ] Test streaming audio yield
4. [ ] Test cancellation via AbortController
5. [ ] Run all unit tests
6. [ ] Verify TypeScript strict mode compilation

## Notes & Considerations

- OpenAI's TTS API returns audio as a streaming HTTP response, not via WebSocket. We use `response.body.getReader()` to stream chunks as they arrive.
- OpenAI's `pcm` format outputs 24kHz 16-bit mono PCM. Our pipeline standardizes on 16kHz, so downsampling is needed. The linear interpolation method is simple and sufficient for speech audio.
- The `tts-1` model is optimized for low latency (vs `tts-1-hd` for quality). For conversational use, latency matters more than quality.
- Cost: OpenAI TTS at $0.015/1000 chars is cheaper than ElevenLabs. This makes it a good fallback.
- The `AbortController` pattern allows cancelling a streaming response mid-flight when the user interrupts (starts speaking during TTS playback).

## Completion Checklist

- [ ] `OpenAITTSAdapter` implements `ITTSAdapter`
- [ ] Streaming HTTP response consumed as AsyncIterable
- [ ] PCM16 audio chunks yielded correctly
- [ ] 24kHz to 16kHz downsampling works
- [ ] cancel() aborts HTTP request
- [ ] dispose() cleans up
- [ ] All unit tests passing
- [ ] TypeScript strict mode compiles
