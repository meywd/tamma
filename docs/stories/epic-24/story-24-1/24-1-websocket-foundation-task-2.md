# Task 2: VoiceSession Class + Conversation Context

**Story:** 24-1-websocket-foundation - WebSocket Foundation
**Epic:** 24

## Task Description

Create the `VoiceSession` class that manages one WebSocket connection lifecycle: initialization, JSON message routing, text-only conversation mode, and cleanup. Also create the `ConversationContext` class that maintains multi-turn conversation history for the LLM.

## Acceptance Criteria

- `VoiceSession` class manages a single WebSocket connection lifecycle: init, message routing, cleanup
- JSON message routing dispatches `ClientMessage` types to appropriate handlers
- Binary frame routing forwards PCM16 audio to STT adapter (stubbed for now)
- Text-only conversation mode works: user sends `text.input`, receives `response.text` via existing `ILLMProvider.complete()`
- `ConversationContext` maintains a capped transcript history (messages array) for multi-turn LLM calls
- Session state machine transitions: `initializing -> ready -> (listening|processing|speaking|idle) -> closed`
- `dispose()` cleans up all resources (timers, adapters, listeners)
- Unit tests for session lifecycle, message routing, text conversation

## Implementation Details

### Technical Requirements

- [ ] Create `packages/voice/src/conversation-context.ts`:

```typescript
import type { Message } from '@tamma/providers';

export interface ConversationTurn {
  role: 'user' | 'assistant';
  content: string;
  source: 'voice' | 'text';
  timestamp: number;
}

export class ConversationContext {
  private readonly turns: ConversationTurn[] = [];
  private readonly maxTurns: number;
  private readonly systemPrompt: string;

  constructor(options: { maxTurns?: number; systemPrompt?: string });

  /** Add a user or assistant turn to the conversation. */
  addTurn(turn: Omit<ConversationTurn, 'timestamp'>): void;

  /** Get the last N turns. */
  getRecentTurns(count?: number): ConversationTurn[];

  /** Build the messages array for an LLM call, including system prompt. */
  toMessages(): Message[];

  /** Clear all turns (e.g., on session reset). */
  clear(): void;

  /** Get total turn count. */
  get length(): number;
}
```

- [ ] Create `packages/voice/src/voice-session.ts`:

```typescript
import type { WebSocket } from 'ws';
import type { ILLMProvider, MessageRequest } from '@tamma/providers';
import type { ISTTAdapter, ITTSAdapter } from '@tamma/shared/contracts';
import type {
  ClientMessage, ServerMessage, VoiceSessionConfig,
  VoiceSessionState, VoiceErrorCode,
} from './types.js';
import { ConversationContext } from './conversation-context.js';
import { DEFAULT_VOICE_CONFIG } from './types.js';

export interface VoiceSessionDeps {
  llmProvider: ILLMProvider;
  sttAdapter?: ISTTAdapter;     // null for text-only mode
  ttsAdapter?: ITTSAdapter;     // null for text-only mode
  userId: string;
  sessionId: string;
}

export class VoiceSession {
  readonly sessionId: string;
  readonly userId: string;
  private state: VoiceSessionState = 'initializing';
  private readonly ws: WebSocket;
  private readonly llm: ILLMProvider;
  private stt: ISTTAdapter | null;
  private tts: ITTSAdapter | null;
  private config: VoiceSessionConfig;
  private readonly context: ConversationContext;
  private disposed = false;

  // Unsubscribe handles for STT callbacks
  private unsubInterim: (() => void) | null = null;
  private unsubFinal: (() => void) | null = null;

  constructor(ws: WebSocket, deps: VoiceSessionDeps);

  /** Start session: wire up WebSocket handlers and send session.ready. */
  async initialize(): Promise<void>;

  /** Current session state. */
  getState(): VoiceSessionState;

  /** Access the conversation context (for VoiceEngineTransport). */
  getContext(): ConversationContext;

  /** Send a ServerMessage as JSON text frame. */
  send(message: ServerMessage): void;

  /** Send binary PCM16 audio frame. */
  sendAudio(pcm16: Buffer): void;

  /** Send error and optionally close the connection. */
  sendError(code: VoiceErrorCode, message: string, recoverable?: boolean): void;

  /** Process a text input (from either text.input or final STT transcript). */
  async handleTextInput(text: string, source: 'voice' | 'text'): Promise<void>;

  /** Clean up all resources. */
  async dispose(): Promise<void>;

  // --- Private handlers ---
  private onWsMessage(data: Buffer | string): void;
  private onWsClose(): void;
  private onWsError(error: Error): void;
  private handleClientMessage(msg: ClientMessage): void;
  private handleSessionStart(msg: SessionStartMessage): Promise<void>;
  private handleSessionEnd(): Promise<void>;
  private handleInputStart(): void;
  private handleInputEnd(): void;
  private handleInputCancel(): void;
  private handleAudioFrame(pcm16: Buffer): void;
  private setState(newState: VoiceSessionState): void;
  private assertNotDisposed(): void;
}
```

- [ ] Key implementation detail for `handleTextInput()`:
  1. Add user turn to `ConversationContext`
  2. Build messages via `context.toMessages()`
  3. Call `llm.complete({ messages })` (non-streaming for text mode)
  4. Add assistant turn to context
  5. Send `response.text` message to client with `isFinal: true`
  6. If TTS adapter is available, stream TTS audio to client

### Files to Modify/Create

- CREATE `packages/voice/src/conversation-context.ts`
- CREATE `packages/voice/src/conversation-context.test.ts`
- CREATE `packages/voice/src/voice-session.ts`
- CREATE `packages/voice/src/voice-session.test.ts`

### Dependencies

- [ ] Task 1: All types from `packages/voice/src/types.ts`
- [ ] `ILLMProvider`, `MessageRequest`, `Message` from `@tamma/providers`
- [ ] `ISTTAdapter`, `ITTSAdapter` from `@tamma/shared/contracts`
- [ ] `ws` package (installed transitively via `@fastify/websocket`)

## Testing Strategy

### Unit Tests -- conversation-context.test.ts

- [ ] Test `addTurn()` adds a turn with auto-generated timestamp
- [ ] Test `getRecentTurns()` returns last N turns in order
- [ ] Test `getRecentTurns()` with no argument returns all turns
- [ ] Test max turns cap: adding beyond `maxTurns` drops the oldest turn
- [ ] Test `toMessages()` includes system prompt as first message
- [ ] Test `toMessages()` converts turns to `Message[]` format
- [ ] Test `clear()` removes all turns
- [ ] Test `length` getter returns correct count

### Unit Tests -- voice-session.test.ts

- [ ] Test constructor sets state to `initializing`
- [ ] Test `initialize()` transitions state to `ready` and sends `session.ready`
- [ ] Test `send()` serializes `ServerMessage` as JSON and writes to WebSocket
- [ ] Test `sendAudio()` writes binary Buffer to WebSocket
- [ ] Test `sendError()` sends `VoiceErrorMessage` with correct fields
- [ ] Test `sendError()` with `recoverable: false` closes the WebSocket
- [ ] Test JSON text frame routed to `handleClientMessage()` dispatcher
- [ ] Test binary frame routed to `handleAudioFrame()` (or ignored in text-only mode)
- [ ] Test `session.start` message merges config with defaults
- [ ] Test `session.end` message triggers cleanup and sends `session.ended`
- [ ] Test `text.input` message triggers LLM call and sends `response.text` back
- [ ] Test text conversation round-trip: input -> LLM -> response with conversation context preserved
- [ ] Test multi-turn: second input sees first turn in context
- [ ] Test `dispose()` cleans up WebSocket handlers and STT/TTS adapters
- [ ] Test `dispose()` is idempotent (second call is a no-op)
- [ ] Test WebSocket close event triggers cleanup
- [ ] Test WebSocket error event sends error message to client
- [ ] Test invalid JSON message sends protocol error
- [ ] Test unknown message type sends protocol error

### Validation Steps

1. [ ] Create ConversationContext with turn management and LLM message building
2. [ ] Create VoiceSession with WebSocket handler wiring
3. [ ] Verify text-only conversation works: send text.input, receive response.text
4. [ ] Verify session lifecycle: init -> ready -> idle -> closed
5. [ ] Run all unit tests
6. [ ] Verify TypeScript strict mode compilation

## Notes & Considerations

- The `VoiceSession` is intentionally designed to work without STT/TTS adapters. When `sttAdapter` and `ttsAdapter` are null, the session operates in text-only mode. This allows Phase 1 to be fully functional before audio is added.
- `handleTextInput()` is the shared code path for both text input and STT final transcripts. This ensures hybrid mode (voice + text) works seamlessly.
- The `ConversationContext.maxTurns` cap prevents unbounded memory growth during long sessions. Default: 50 turns.
- The system prompt for `ConversationContext` includes context about Tamma's capabilities and the user's current engine state, enabling the LLM to give relevant responses.
- Mock the `ws` WebSocket in tests using a simple EventEmitter with `send()` and `close()` stubs.

## Completion Checklist

- [ ] `conversation-context.ts` created with turn management
- [ ] `voice-session.ts` created with full lifecycle management
- [ ] Text-only conversation mode works end-to-end
- [ ] State machine transitions are correct
- [ ] dispose() cleans up all resources
- [ ] All unit tests written and passing
- [ ] TypeScript strict mode compiles without errors
