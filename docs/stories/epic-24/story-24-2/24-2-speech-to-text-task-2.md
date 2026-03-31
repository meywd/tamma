# Task 2: OpenAIWhisperAdapter (Batch Fallback)

**Story:** 24-2-speech-to-text - Speech-to-Text Integration
**Epic:** 24

## Task Description

Implement the `OpenAIWhisperAdapter` as a batch fallback STT adapter. When Deepgram is unavailable, this adapter collects audio buffers, sends them as a complete audio file to OpenAI's Whisper API, and returns the transcript. It implements the same `ISTTAdapter` interface but with higher latency (batch, not streaming).

## Acceptance Criteria

- `OpenAIWhisperAdapter` implements `ISTTAdapter` from `@tamma/shared/contracts`
- `connect()` validates config and initializes state (no persistent connection needed)
- `sendAudio()` appends PCM16 buffers to an internal buffer
- `onFinalTranscript()` fires after `endUtterance()` sends buffered audio to Whisper API
- `onInterimTranscript()` is a no-op (Whisper does not support interim results)
- `endUtterance()` triggers the batch transcription: converts buffered PCM16 to WAV, POSTs to `/v1/audio/transcriptions`
- Uses `OPENAI_API_KEY` env var for authentication
- Unit tests with mock HTTP calls (no real OpenAI API calls)

## Implementation Details

### Technical Requirements

- [ ] Create `packages/voice/src/stt/openai-whisper-adapter.ts`:

```typescript
import type { ISTTAdapter, STTConfig } from '@tamma/shared/contracts';

export interface WhisperAdapterConfig {
  apiKey: string;
  model?: string;          // default: 'whisper-1'
  baseUrl?: string;        // default: 'https://api.openai.com/v1'
}

export class OpenAIWhisperAdapter implements ISTTAdapter {
  readonly name = 'openai-whisper';

  private readonly apiKey: string;
  private readonly model: string;
  private readonly baseUrl: string;
  private audioBuffer: Buffer[] = [];
  private config: STTConfig | null = null;
  private disposed = false;

  private interimCallbacks: Array<(text: string) => void> = [];
  private finalCallbacks: Array<(text: string, confidence: number) => void> = [];

  constructor(config: WhisperAdapterConfig);

  async connect(config: STTConfig): Promise<void> {
    // Store config, reset audio buffer
    // No persistent connection needed (Whisper is REST-based)
  }

  sendAudio(pcm16: Buffer): void {
    // Append buffer to audioBuffer array
    // Guard: if disposed, ignore
    if (!this.disposed) {
      this.audioBuffer.push(pcm16);
    }
  }

  onInterimTranscript(cb: (text: string) => void): () => void {
    // Whisper does not support interim results
    // Still register to satisfy interface, but never fires
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
    // Take current audio buffer, clear it, then POST to Whisper
    const buffers = this.audioBuffer.splice(0);
    if (buffers.length === 0) return;

    // Fire-and-forget the async transcription
    void this.transcribe(buffers).catch((err) => {
      // Log error, do not crash
    });
  }

  async dispose(): Promise<void> {
    if (this.disposed) return;
    this.disposed = true;
    this.audioBuffer = [];
    this.interimCallbacks = [];
    this.finalCallbacks = [];
  }

  // --- Private ---

  private async transcribe(buffers: Buffer[]): Promise<void> {
    const pcm = Buffer.concat(buffers);
    const wav = this.pcm16ToWav(pcm, this.config?.sampleRate ?? 16_000);

    // Create form data for Whisper API
    const formData = new FormData();
    formData.append('file', new Blob([wav], { type: 'audio/wav' }), 'audio.wav');
    formData.append('model', this.model);
    if (this.config?.language) {
      formData.append('language', this.config.language.split('-')[0] ?? 'en');
    }
    formData.append('response_format', 'json');

    const response = await fetch(`${this.baseUrl}/audio/transcriptions`, {
      method: 'POST',
      headers: {
        'Authorization': `Bearer ${this.apiKey}`,
      },
      body: formData,
    });

    if (!response.ok) {
      throw new Error(`Whisper API error: ${response.status} ${await response.text()}`);
    }

    const result = (await response.json()) as { text: string };
    if (result.text.trim() !== '') {
      // Whisper does not return confidence; use 1.0 as default
      for (const cb of this.finalCallbacks) {
        cb(result.text.trim(), 1.0);
      }
    }
  }

  private pcm16ToWav(pcm: Buffer, sampleRate: number): Buffer {
    // WAV header for PCM16 mono
    const header = Buffer.alloc(44);
    const dataSize = pcm.length;
    const fileSize = 36 + dataSize;

    header.write('RIFF', 0);
    header.writeUInt32LE(fileSize, 4);
    header.write('WAVE', 8);
    header.write('fmt ', 12);
    header.writeUInt32LE(16, 16);        // fmt chunk size
    header.writeUInt16LE(1, 20);         // PCM format
    header.writeUInt16LE(1, 22);         // mono
    header.writeUInt32LE(sampleRate, 24);
    header.writeUInt32LE(sampleRate * 2, 28); // byte rate
    header.writeUInt16LE(2, 32);         // block align
    header.writeUInt16LE(16, 34);        // bits per sample
    header.write('data', 36);
    header.writeUInt32LE(dataSize, 40);

    return Buffer.concat([header, pcm]);
  }
}
```

### Files to Modify/Create

- CREATE `packages/voice/src/stt/openai-whisper-adapter.ts`
- CREATE `packages/voice/src/stt/openai-whisper-adapter.test.ts`
- MODIFY `packages/voice/src/index.ts` -- add Whisper adapter export

### Dependencies

- [ ] Story 24-1 Task 1: `ISTTAdapter`, `STTConfig` from `@tamma/shared/contracts`
- [ ] No additional npm packages (uses native `fetch` and `FormData`)

## Testing Strategy

### Unit Tests -- openai-whisper-adapter.test.ts

- [ ] Test `connect()` stores config and resets audio buffer
- [ ] Test `sendAudio()` appends buffer to internal array
- [ ] Test `sendAudio()` is ignored after dispose()
- [ ] Test `endUtterance()` concatenates buffers and POSTs to Whisper API
- [ ] Test `endUtterance()` with empty buffer does nothing (no API call)
- [ ] Test Whisper response triggers finalTranscript callback with text
- [ ] Test Whisper response with empty text does not trigger callback
- [ ] Test confidence defaults to 1.0 (Whisper does not return confidence)
- [ ] Test `onInterimTranscript()` registers callback but never fires
- [ ] Test callback unsubscribe works
- [ ] Test `dispose()` clears buffers and callbacks
- [ ] Test `dispose()` is idempotent
- [ ] Test `pcm16ToWav()` produces valid WAV header (44 bytes + PCM data)
- [ ] Test WAV header has correct sample rate, mono, 16-bit fields
- [ ] Test Whisper API error (non-200) is caught and does not crash
- [ ] Test language extraction from BCP-47 (e.g., 'en-US' -> 'en')

### Mocking Strategy

```typescript
// Mock fetch for Whisper API
const mockFetch = vi.fn().mockResolvedValue({
  ok: true,
  json: () => Promise.resolve({ text: 'hello world' }),
  text: () => Promise.resolve(''),
});
vi.stubGlobal('fetch', mockFetch);
```

### Validation Steps

1. [ ] Create OpenAIWhisperAdapter with buffer collection
2. [ ] Implement pcm16ToWav conversion
3. [ ] Implement endUtterance -> Whisper API call
4. [ ] Verify FormData construction with WAV file
5. [ ] Run all unit tests with mocked fetch
6. [ ] Verify TypeScript strict mode compilation

## Notes & Considerations

- Whisper is batch-only: it cannot return interim results. The adapter silently registers interim callbacks but never fires them. This means the UI will not show real-time transcription when Whisper is active. The UI should handle this gracefully (e.g., show a "processing..." indicator).
- Latency is significantly higher than Deepgram (1-3 seconds vs <300ms) because audio must be fully buffered before sending. This is acceptable as a fallback.
- The `endUtterance()` method is fire-and-forget (async work happens in the background). The callback fires when the Whisper response arrives. This matches the ISTTAdapter contract.
- PCM16 to WAV conversion is needed because Whisper's API expects a file upload, not raw PCM. The WAV header is 44 bytes with standard RIFF format.
- OpenAI's API key (`OPENAI_API_KEY`) may already exist in the env for the LLM provider. Reuse the same key.

## Completion Checklist

- [ ] `OpenAIWhisperAdapter` implements `ISTTAdapter`
- [ ] Audio buffer collection works
- [ ] PCM16 to WAV conversion produces valid WAV
- [ ] Whisper API call with FormData upload
- [ ] Final transcript callback fires on successful transcription
- [ ] No interim transcripts (by design)
- [ ] Error handling for API failures
- [ ] All unit tests passing
- [ ] TypeScript strict mode compiles
