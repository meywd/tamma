# Task 4: Wire STT into VoiceSession + Server-side Audio Pipeline

**Story:** 24-2-speech-to-text - Speech-to-Text Integration
**Epic:** 24

## Task Description

Wire the STT adapters (DeepgramAdapter, OpenAIWhisperAdapter) into the `VoiceSession` class so that binary audio frames received from the browser WebSocket are forwarded to the active STT adapter, and transcripts flow back to the client. Also implement provider fallback: if Deepgram fails, fall back to Whisper.

## Acceptance Criteria

- Binary PCM16 frames from browser WebSocket forwarded to `sttAdapter.sendAudio()`
- `input.start` message from VAD starts a new utterance context
- `input.end` message calls `sttAdapter.endUtterance()` to finalize transcript
- `input.cancel` message discards current utterance
- Interim transcripts forwarded as `transcript.interim` to client
- Final transcripts forwarded as `transcript.final` to client, then trigger LLM response
- Provider fallback: if DeepgramAdapter throws on `connect()`, fall back to `OpenAIWhisperAdapter`
- STT adapter selection based on `VoiceSessionConfig.sttProvider`
- Unit tests for audio pipeline, transcript flow, and fallback

## Implementation Details

### Technical Requirements

- [ ] Modify `packages/voice/src/voice-session.ts` to wire STT:

```typescript
// In VoiceSession.initialize():
// 1. Create STT adapter based on config.sttProvider
// 2. Call sttAdapter.connect({ language, sampleRate, interimResults: true })
// 3. Subscribe to sttAdapter.onInterimTranscript -> send transcript.interim
// 4. Subscribe to sttAdapter.onFinalTranscript -> commit to context, trigger LLM

// In handleAudioFrame(pcm16: Buffer):
// Forward to sttAdapter.sendAudio(pcm16)

// In handleInputStart():
// setState('listening')
// (STT is already listening; this is informational)

// In handleInputEnd():
// sttAdapter.endUtterance()
// setState('processing')

// In handleInputCancel():
// Reset current utterance tracking
// setState('idle')
```

- [ ] Create STT adapter factory:

```typescript
// packages/voice/src/stt/stt-factory.ts
import type { ISTTAdapter, STTConfig } from '@tamma/shared/contracts';
import type { STTProviderName } from '../types.js';
import { DeepgramAdapter } from './deepgram-adapter.js';
import { OpenAIWhisperAdapter } from './openai-whisper-adapter.js';

export interface STTFactoryConfig {
  deepgramApiKey?: string;
  openaiApiKey?: string;
}

export async function createSTTAdapter(
  provider: STTProviderName,
  factoryConfig: STTFactoryConfig,
  sttConfig: STTConfig,
): Promise<ISTTAdapter> {
  // Try primary provider
  if (provider === 'deepgram' && factoryConfig.deepgramApiKey) {
    try {
      const adapter = new DeepgramAdapter({ apiKey: factoryConfig.deepgramApiKey });
      await adapter.connect(sttConfig);
      return adapter;
    } catch (err) {
      // Log warning: Deepgram connection failed, falling back to Whisper
    }
  }

  // Fallback to Whisper
  if (factoryConfig.openaiApiKey) {
    const adapter = new OpenAIWhisperAdapter({ apiKey: factoryConfig.openaiApiKey });
    await adapter.connect(sttConfig);
    return adapter;
  }

  throw new Error('No STT provider available: missing API keys');
}
```

- [ ] Wire transcript flow in VoiceSession:

```typescript
// After final transcript received:
async onFinalTranscript(text: string, confidence: number): Promise<void> {
  // 1. Send transcript.final to client
  this.send({ type: 'transcript.final', text, confidence });

  // 2. Process through intent classifier (Story 24-4, stubbed for now)
  // For now, treat all final transcripts as text input
  await this.handleTextInput(text, 'voice');
}
```

### Files to Modify/Create

- CREATE `packages/voice/src/stt/stt-factory.ts`
- CREATE `packages/voice/src/stt/stt-factory.test.ts`
- MODIFY `packages/voice/src/voice-session.ts` -- wire STT adapters, handle audio frames
- CREATE `packages/voice/src/voice-session-audio.test.ts` -- audio pipeline tests

### Dependencies

- [ ] Task 1: DeepgramAdapter
- [ ] Task 2: OpenAIWhisperAdapter
- [ ] Story 24-1 Task 2: VoiceSession class

## Testing Strategy

### Unit Tests -- stt-factory.test.ts

- [ ] Test `createSTTAdapter('deepgram', ...)` returns DeepgramAdapter when API key present
- [ ] Test `createSTTAdapter('openai-whisper', ...)` returns WhisperAdapter
- [ ] Test Deepgram connection failure falls back to Whisper
- [ ] Test throws when no API keys are available
- [ ] Test Deepgram selected when both keys present and provider is 'deepgram'
- [ ] Test Whisper selected directly when provider is 'openai-whisper'

### Unit Tests -- voice-session-audio.test.ts

- [ ] Test binary WebSocket frame forwarded to sttAdapter.sendAudio()
- [ ] Test `input.start` message sets session state to 'listening'
- [ ] Test `input.end` message calls sttAdapter.endUtterance()
- [ ] Test `input.end` message sets session state to 'processing'
- [ ] Test `input.cancel` resets state to 'idle'
- [ ] Test interim transcript from STT sent as `transcript.interim` to client
- [ ] Test final transcript from STT sent as `transcript.final` to client
- [ ] Test final transcript triggers handleTextInput with source 'voice'
- [ ] Test LLM response sent back after final transcript processing
- [ ] Test audio frames silently dropped when STT adapter is null (text-only mode)
- [ ] Test audio frames silently dropped when session is disposed
- [ ] Test STT adapter disposed on session cleanup

### Validation Steps

1. [ ] Create STT factory with fallback logic
2. [ ] Wire STT adapter into VoiceSession initialization
3. [ ] Wire audio frame handling in VoiceSession
4. [ ] Wire transcript callbacks to client messages
5. [ ] Test full flow: audio -> STT -> transcript -> LLM -> response
6. [ ] Test fallback: Deepgram failure -> Whisper takeover
7. [ ] Run all unit tests
8. [ ] Verify TypeScript strict mode compilation

## Notes & Considerations

- The STT factory implements the provider fallback chain: Deepgram (primary) -> Whisper (fallback). The factory catches connection errors from the primary and transparently switches to the fallback. This is transparent to VoiceSession.
- `input.end` from client-side VAD tells the server that the user stopped speaking. The server calls `endUtterance()` to get the final transcript. This is important because Deepgram's streaming mode may still have buffered audio that hasn't been finalized.
- The transcript flow is: browser audio -> STT adapter -> interim/final callbacks -> VoiceSession -> (final only) LLM call -> response -> TTS (Story 24-3).
- Audio frames received before STT adapter is connected should be buffered briefly or silently dropped. Given the async nature of STT connection, a small buffer (100ms) prevents losing the first syllable.

## Completion Checklist

- [ ] STT factory with provider fallback created
- [ ] VoiceSession wired to STT adapter for audio forwarding
- [ ] Binary frames routed from WebSocket to STT
- [ ] Interim transcripts forwarded to client
- [ ] Final transcripts committed and trigger LLM response
- [ ] Provider fallback works (Deepgram -> Whisper)
- [ ] All unit tests passing
- [ ] TypeScript strict mode compiles
