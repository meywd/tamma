---
title: "Task 1: Voice Package Scaffold + Shared Types + WebSocket Protocol"
sidebar:
  order: 240
---

**Story:** 24-1-websocket-foundation - WebSocket Foundation
**Epic:** 24

## Task Description

Create the `packages/voice/` package scaffold and define all shared types for the voice conversation feature. This includes the WebSocket protocol message types, session configuration, voice provider types, and the shared contract in `packages/shared/src/contracts/voice-transport.ts`. This task establishes the type foundation that all subsequent tasks depend on.

## Acceptance Criteria

- `packages/voice/` initialized with `package.json`, `tsconfig.json`, `vitest.config.ts`
- `packages/voice/src/types.ts` defines all WebSocket protocol message types as a discriminated union
- `packages/shared/src/contracts/voice-transport.ts` defines `ISTTAdapter`, `ITTSAdapter`, `VoiceSessionConfig` interfaces
- All client-to-server and server-to-client message types are fully typed
- Binary frame markers for PCM16 audio are documented
- Subpath export `@tamma/shared/contracts` updated to include voice types
- `packages/voice/src/index.ts` barrel export created
- pnpm workspace includes `packages/voice`
- TypeScript strict mode compiles without errors

## Implementation Details

### Technical Requirements

- [ ] Create `packages/voice/package.json`:
  ```json
  {
    "name": "@tamma/voice",
    "version": "0.1.0",
    "type": "module",
    "main": "src/index.ts",
    "exports": { ".": "./src/index.ts" },
    "dependencies": {
      "@tamma/shared": "workspace:*"
    },
    "devDependencies": {
      "vitest": "^3.0.0",
      "typescript": "^5.7.0"
    }
  }
  ```
- [ ] Create `packages/voice/tsconfig.json` extending root tsconfig with strict mode
- [ ] Create `packages/voice/vitest.config.ts` with standard test configuration
- [ ] Create `packages/voice/src/types.ts` with:

```typescript
// ---- Session State ----
export type VoiceSessionState =
  | 'initializing'
  | 'ready'
  | 'listening'
  | 'processing'
  | 'speaking'
  | 'idle'
  | 'error'
  | 'closed';

// ---- STT/TTS Provider Names ----
export type STTProviderName = 'deepgram' | 'openai-whisper';
export type TTSProviderName = 'elevenlabs' | 'openai-tts';

// ---- Voice Configuration ----
export interface VoiceSessionConfig {
  sttProvider: STTProviderName;
  ttsProvider: TTSProviderName;
  voice: string;          // TTS voice ID (e.g., 'alloy', 'rachel')
  language: string;       // BCP-47 (e.g., 'en-US')
  sampleRate: number;     // PCM sample rate in Hz (default 16000)
  vadEnabled: boolean;    // Client-side VAD
}

export const DEFAULT_VOICE_CONFIG: VoiceSessionConfig = {
  sttProvider: 'deepgram',
  ttsProvider: 'elevenlabs',
  voice: 'alloy',
  language: 'en-US',
  sampleRate: 16_000,
  vadEnabled: true,
};

// ---- Client -> Server Messages ----
export interface SessionStartMessage {
  type: 'session.start';
  config: Partial<VoiceSessionConfig>;
}

export interface SessionEndMessage {
  type: 'session.end';
}

export interface InputStartMessage {
  type: 'input.start';
}

export interface InputEndMessage {
  type: 'input.end';
}

export interface InputCancelMessage {
  type: 'input.cancel';
}

export interface TextInputMessage {
  type: 'text.input';
  text: string;
}

export type ClientMessage =
  | SessionStartMessage
  | SessionEndMessage
  | InputStartMessage
  | InputEndMessage
  | InputCancelMessage
  | TextInputMessage;

// ---- Server -> Client Messages ----
export interface SessionReadyMessage {
  type: 'session.ready';
  sessionId: string;
  config: VoiceSessionConfig;
}

export interface SessionEndedMessage {
  type: 'session.ended';
  reason: 'user' | 'timeout' | 'error';
}

export interface TranscriptInterimMessage {
  type: 'transcript.interim';
  text: string;
}

export interface TranscriptFinalMessage {
  type: 'transcript.final';
  text: string;
  confidence: number;
}

export interface ResponseStartMessage {
  type: 'response.start';
}

export interface ResponseTextMessage {
  type: 'response.text';
  text: string;
  isFinal: boolean;
}

export interface ResponseEndMessage {
  type: 'response.end';
}

export interface ResponseCancelMessage {
  type: 'response.cancel';
}

export interface EngineStateMessage {
  type: 'engine.state';
  state: import('@tamma/shared').EngineState;
  issue: import('@tamma/shared').IssueData | null;
}

export interface EngineLogMessage {
  type: 'engine.log';
  level: 'debug' | 'info' | 'warn' | 'error';
  message: string;
  timestamp: number;
}

export interface EngineApprovalMessage {
  type: 'engine.approval';
  plan: import('@tamma/shared').DevelopmentPlan;
}

export interface VoiceErrorMessage {
  type: 'error';
  code: VoiceErrorCode;
  message: string;
  recoverable: boolean;
}

export type VoiceErrorCode =
  | 'AUTH_FAILED'
  | 'SESSION_LIMIT'
  | 'STT_ERROR'
  | 'TTS_ERROR'
  | 'LLM_ERROR'
  | 'ENGINE_ERROR'
  | 'PROTOCOL_ERROR'
  | 'TIMEOUT'
  | 'INTERNAL_ERROR';

export type ServerMessage =
  | SessionReadyMessage
  | SessionEndedMessage
  | TranscriptInterimMessage
  | TranscriptFinalMessage
  | ResponseStartMessage
  | ResponseTextMessage
  | ResponseEndMessage
  | ResponseCancelMessage
  | EngineStateMessage
  | EngineLogMessage
  | EngineApprovalMessage
  | VoiceErrorMessage;
```

- [ ] Create `packages/shared/src/contracts/voice-transport.ts` with:

```typescript
// ---- STT Adapter Interface ----
export interface STTConfig {
  language: string;
  sampleRate: number;
  interimResults: boolean;
  model?: string;
}

export interface ISTTAdapter {
  readonly name: string;
  connect(config: STTConfig): Promise<void>;
  sendAudio(pcm16: Buffer): void;
  onInterimTranscript(cb: (text: string) => void): () => void;
  onFinalTranscript(cb: (text: string, confidence: number) => void): () => void;
  endUtterance(): void;
  dispose(): Promise<void>;
}

// ---- TTS Adapter Interface ----
export interface TTSConfig {
  voice: string;
  language: string;
  sampleRate: number;
  model?: string;
}

export interface ITTSAdapter {
  readonly name: string;
  connect(config: TTSConfig): Promise<void>;
  synthesize(text: string): AsyncIterable<Buffer>;
  cancel(): void;
  dispose(): Promise<void>;
}
```

- [ ] Create `packages/voice/src/index.ts` barrel export
- [ ] Update `packages/shared/src/contracts/index.ts` to re-export voice-transport types
- [ ] Add `packages/voice` to `pnpm-workspace.yaml` if not already covered by glob

### Files to Modify/Create

- CREATE `packages/voice/package.json`
- CREATE `packages/voice/tsconfig.json`
- CREATE `packages/voice/vitest.config.ts`
- CREATE `packages/voice/src/types.ts`
- CREATE `packages/voice/src/index.ts`
- CREATE `packages/shared/src/contracts/voice-transport.ts`
- MODIFY `packages/shared/src/contracts/index.ts` -- add voice-transport re-export
- MODIFY `pnpm-workspace.yaml` -- verify `packages/*` glob covers voice

### Dependencies

- No external dependencies required
- `@tamma/shared` types: `EngineState`, `IssueData`, `DevelopmentPlan`

## Testing Strategy

### Unit Tests -- types.test.ts

- [ ] Test `VoiceSessionConfig` has all required fields with correct types
- [ ] Test `DEFAULT_VOICE_CONFIG` has valid defaults for all fields
- [ ] Test `ClientMessage` discriminated union: each `type` field maps to the correct shape
- [ ] Test `ServerMessage` discriminated union: each `type` field maps to the correct shape
- [ ] Test `VoiceErrorCode` only accepts defined values
- [ ] Test `VoiceSessionState` only accepts defined values

### Validation Steps

1. [ ] Create all package scaffold files
2. [ ] Create types.ts with full protocol definition
3. [ ] Create voice-transport.ts with ISTTAdapter and ITTSAdapter
4. [ ] Create barrel exports
5. [ ] Run `pnpm install` to link workspace
6. [ ] Run `pnpm build` or `tsc --noEmit` to verify strict mode compilation
7. [ ] Write and run type-level tests

## Notes & Considerations

- Binary WebSocket frames (PCM16 audio) are not part of the JSON message protocol. They are distinguished by the WebSocket frame opcode (binary vs text). The server checks `typeof data === 'string'` for JSON text frames vs `Buffer` for binary audio frames.
- PCM16 at 16kHz mono = 32,000 bytes/second. Each WebSocket frame carries ~20ms of audio = 640 bytes. This is efficient enough for real-time streaming.
- The `VoiceSessionConfig` type is used both by the client (to send preferences) and the server (to initialize STT/TTS adapters). The `Partial<VoiceSessionConfig>` in `session.start` allows clients to override only the fields they care about; the server fills in defaults.
- `ISTTAdapter` and `ITTSAdapter` are placed in `@tamma/shared/contracts` (not in `@tamma/voice`) because they define cross-package contracts that the API routes, voice sessions, and tests all depend on.

## Completion Checklist

- [ ] `packages/voice/` package scaffold complete with package.json, tsconfig.json, vitest.config.ts
- [ ] `types.ts` defines all client/server message types as discriminated unions
- [ ] `voice-transport.ts` defines ISTTAdapter, ITTSAdapter, STTConfig, TTSConfig
- [ ] Barrel exports in place for both packages
- [ ] pnpm workspace recognizes the new package
- [ ] TypeScript strict mode compiles without errors
- [ ] Type-level tests passing
