# Task 3: Wire TTS into VoiceSession + Sentence Streaming + Interruption Handling

**Story:** 24-3-text-to-speech - Text-to-Speech Integration
**Epic:** 24

## Task Description

Wire the TTS adapters into the `VoiceSession` class so that LLM text responses are streamed sentence-by-sentence to TTS and audio is forwarded to the browser. Implement interruption handling: when the user starts speaking during TTS playback, the server cancels the TTS stream and notifies the browser.

## Acceptance Criteria

- LLM streaming response split into sentences; TTS starts on first complete sentence (not full response)
- TTS audio chunks forwarded as binary WebSocket frames to browser
- `response.start` sent before first audio chunk
- `response.text` sent for each sentence of text being spoken
- `response.end` sent after last audio chunk
- Interruption: `input.start` from VAD during TTS playback triggers TTS cancel, sends `response.cancel`
- Interrupted text saved to conversation context (so LLM knows what was said)
- Voice selection configurable via `PUT /api/v1/voice/config`
- Provider fallback: ElevenLabs fails -> OpenAI TTS
- `ELEVENLABS_API_KEY` env var added to docker-compose
- Unit tests for sentence splitting, streaming, interruption

## Implementation Details

### Technical Requirements

- [ ] Create TTS factory (matching STT factory pattern):

```typescript
// packages/voice/src/tts/tts-factory.ts
import type { ITTSAdapter, TTSConfig } from '@tamma/shared/contracts';
import type { TTSProviderName } from '../types.js';
import { ElevenLabsAdapter } from './elevenlabs-adapter.js';
import { OpenAITTSAdapter } from './openai-tts-adapter.js';

export interface TTSFactoryConfig {
  elevenlabsApiKey?: string;
  openaiApiKey?: string;
}

export async function createTTSAdapter(
  provider: TTSProviderName,
  factoryConfig: TTSFactoryConfig,
  ttsConfig: TTSConfig,
): Promise<ITTSAdapter> {
  if (provider === 'elevenlabs' && factoryConfig.elevenlabsApiKey) {
    try {
      const adapter = new ElevenLabsAdapter({ apiKey: factoryConfig.elevenlabsApiKey });
      await adapter.connect(ttsConfig);
      return adapter;
    } catch {
      // Fall back to OpenAI TTS
    }
  }

  if (factoryConfig.openaiApiKey) {
    const adapter = new OpenAITTSAdapter({ apiKey: factoryConfig.openaiApiKey });
    await adapter.connect(ttsConfig);
    return adapter;
  }

  throw new Error('No TTS provider available: missing API keys');
}
```

- [ ] Create sentence splitter utility:

```typescript
// packages/voice/src/sentence-splitter.ts

/**
 * Split text into sentences for incremental TTS.
 * Yields sentences as they are detected from a stream of text deltas.
 */
export class SentenceSplitter {
  private buffer = '';

  /** Feed a text delta and get back any complete sentences. */
  feed(delta: string): string[] {
    this.buffer += delta;
    const sentences: string[] = [];

    // Split on sentence boundaries: . ! ? followed by space or end
    // Handle abbreviations (Mr. Dr. etc.) and decimal numbers
    const sentenceRegex = /[^.!?]*[.!?](?=\s|$)/g;
    let match: RegExpExecArray | null;

    while ((match = sentenceRegex.exec(this.buffer)) !== null) {
      const sentence = match[0].trim();
      if (sentence.length > 0) {
        sentences.push(sentence);
      }
    }

    if (sentences.length > 0) {
      // Remove matched sentences from buffer
      const lastMatch = sentences[sentences.length - 1]!;
      const lastIndex = this.buffer.lastIndexOf(lastMatch) + lastMatch.length;
      this.buffer = this.buffer.slice(lastIndex);
    }

    return sentences;
  }

  /** Flush any remaining text as a final sentence. */
  flush(): string | null {
    const remaining = this.buffer.trim();
    this.buffer = '';
    return remaining.length > 0 ? remaining : null;
  }

  /** Reset the splitter. */
  reset(): void {
    this.buffer = '';
  }
}
```

- [ ] Modify `VoiceSession.handleTextInput()` to stream TTS:

```typescript
async handleTextInput(text: string, source: 'voice' | 'text'): Promise<void> {
  this.context.addTurn({ role: 'user', content: text, source });
  this.setState('processing');

  const messages = this.context.toMessages();

  if (this.tts) {
    // Streaming mode: LLM streams text, TTS generates audio per sentence
    this.send({ type: 'response.start' });
    const splitter = new SentenceSplitter();
    let fullResponse = '';
    let interrupted = false;

    // Use LLM streaming (chat) instead of complete
    for await (const chunk of this.llm.chat({ messages })) {
      if (this.interruptRequested) {
        interrupted = true;
        break;
      }

      if (chunk.delta) {
        fullResponse += chunk.delta;
        const sentences = splitter.feed(chunk.delta);

        for (const sentence of sentences) {
          if (this.interruptRequested) { interrupted = true; break; }

          this.send({ type: 'response.text', text: sentence, isFinal: false });

          // Stream TTS audio for this sentence
          for await (const audio of this.tts.synthesize(sentence)) {
            if (this.interruptRequested) {
              this.tts.cancel();
              interrupted = true;
              break;
            }
            this.sendAudio(audio);
          }
          if (interrupted) break;
        }
        if (interrupted) break;
      }
    }

    // Flush remaining text
    if (!interrupted) {
      const remaining = splitter.flush();
      if (remaining) {
        this.send({ type: 'response.text', text: remaining, isFinal: false });
        for await (const audio of this.tts.synthesize(remaining)) {
          if (this.interruptRequested) { this.tts.cancel(); break; }
          this.sendAudio(audio);
        }
      }
    }

    // Final text message with complete response
    this.send({ type: 'response.text', text: fullResponse, isFinal: true });

    if (interrupted) {
      this.send({ type: 'response.cancel' });
      this.interruptRequested = false;
    } else {
      this.send({ type: 'response.end' });
    }

    this.context.addTurn({
      role: 'assistant',
      content: fullResponse + (interrupted ? ' [interrupted]' : ''),
      source: 'voice',
    });
  } else {
    // Text-only mode (no TTS): use complete() and send text response
    const response = await this.llm.complete({ messages });
    this.context.addTurn({ role: 'assistant', content: response.content, source: 'text' });
    this.send({ type: 'response.text', text: response.content, isFinal: true });
  }

  this.setState('idle');
}
```

- [ ] Add interrupt handling to VoiceSession:

```typescript
private interruptRequested = false;

private handleInputStart(): void {
  // If currently speaking (TTS active), request interrupt
  if (this.state === 'speaking') {
    this.interruptRequested = true;
  }
  this.setState('listening');
}
```

- [ ] Add `ELEVENLABS_API_KEY` to `docker/docker-compose.yml`

### Files to Modify/Create

- CREATE `packages/voice/src/tts/tts-factory.ts`
- CREATE `packages/voice/src/tts/tts-factory.test.ts`
- CREATE `packages/voice/src/sentence-splitter.ts`
- CREATE `packages/voice/src/sentence-splitter.test.ts`
- MODIFY `packages/voice/src/voice-session.ts` -- wire TTS, add streaming + interruption
- CREATE `packages/voice/src/voice-session-tts.test.ts` -- TTS integration tests
- MODIFY `docker/docker-compose.yml` -- add `ELEVENLABS_API_KEY` env var

### Dependencies

- [ ] Task 1: ElevenLabsAdapter
- [ ] Task 2: OpenAITTSAdapter
- [ ] Story 24-1 Task 2: VoiceSession
- [ ] `ILLMProvider.chat()` for streaming LLM responses

## Testing Strategy

### Unit Tests -- sentence-splitter.test.ts

- [ ] Test single sentence: `"Hello world."` -> `["Hello world."]`
- [ ] Test multiple sentences: `"Hello. World."` -> `["Hello.", "World."]`
- [ ] Test incomplete sentence buffered: `"Hello"` -> `[]` (no complete sentence yet)
- [ ] Test incremental feed: `"Hel"` -> `[]`, then `"lo."` -> `["Hello."]`
- [ ] Test question mark: `"How are you?"` -> `["How are you?"]`
- [ ] Test exclamation: `"Great!"` -> `["Great!"]`
- [ ] Test flush returns remaining text
- [ ] Test flush with empty buffer returns null
- [ ] Test reset clears buffer

### Unit Tests -- tts-factory.test.ts

- [ ] Test creates ElevenLabsAdapter when API key present
- [ ] Test creates OpenAITTSAdapter when only OpenAI key present
- [ ] Test ElevenLabs failure falls back to OpenAI
- [ ] Test throws when no API keys available

### Unit Tests -- voice-session-tts.test.ts

- [ ] Test LLM streaming response split into sentences and sent to TTS
- [ ] Test `response.start` sent before first audio
- [ ] Test `response.text` sent for each sentence
- [ ] Test `response.end` sent after last audio
- [ ] Test binary audio frames sent to WebSocket client
- [ ] Test interruption: `input.start` during TTS sets interrupt flag
- [ ] Test interruption: TTS cancelled, `response.cancel` sent
- [ ] Test interrupted text saved to conversation context with `[interrupted]` marker
- [ ] Test interrupt flag reset after handling
- [ ] Test text-only mode (no TTS): complete() called, text response only
- [ ] Test flush: remaining text after LLM completes is synthesized

### Validation Steps

1. [ ] Create TTS factory with fallback
2. [ ] Create sentence splitter
3. [ ] Wire TTS streaming into VoiceSession
4. [ ] Implement interruption handling
5. [ ] Add ELEVENLABS_API_KEY to docker-compose
6. [ ] Run all unit tests
7. [ ] Verify TypeScript strict mode compilation

## Notes & Considerations

- Sentence-level streaming is critical for low latency. If we waited for the full LLM response before starting TTS, the user would wait 2-5 seconds of silence. By streaming sentence-by-sentence, TTS starts ~500ms after the LLM begins responding.
- The interruption flow: VAD detects user speech -> `input.start` -> server sets `interruptRequested = true` -> TTS loop checks flag -> calls `tts.cancel()` -> sends `response.cancel` to browser -> browser stops audio playback -> new user audio processed normally.
- The `[interrupted]` marker in conversation context tells the LLM that the previous response was cut short. This prevents the LLM from repeating itself or being confused about what was already communicated.
- Voice selection is part of `VoiceSessionConfig`. The `PUT /api/v1/voice/config` endpoint (from Story 24-1) allows changing the voice. This takes effect on the next TTS synthesis call.

## Completion Checklist

- [ ] TTS factory with ElevenLabs/OpenAI fallback
- [ ] Sentence splitter for incremental TTS
- [ ] VoiceSession wired to TTS with streaming
- [ ] response.start/text/end message flow
- [ ] Binary audio frames sent to client
- [ ] Interruption handling works
- [ ] ELEVENLABS_API_KEY in docker-compose
- [ ] All unit tests passing
- [ ] TypeScript strict mode compiles
