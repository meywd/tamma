---
title: "Story 24-2: Speech-to-Text Integration"
sidebar:
  order: 240
---

Status: planned

## Story

As a user, I want to speak into my microphone and see my words transcribed in realtime so I can interact with Tamma by voice.

## Acceptance Criteria

1. `ISTTAdapter` interface defined with `connect()`, `sendAudio()`, `onInterimTranscript()`, `onFinalTranscript()`, `endUtterance()`, `dispose()`
2. `DeepgramAdapter` implements `ISTTAdapter` using Deepgram's streaming WebSocket API (Nova-3, <300ms latency)
3. `OpenAIWhisperAdapter` implements `ISTTAdapter` as batch fallback
4. Browser: `useVoiceSession` React hook captures mic audio via AudioWorklet, outputs PCM16 chunks
5. Browser: `@ricky0123/vad-web` (Silero VAD) detects speech start/end locally — only sends audio during speech
6. Binary PCM16 frames flow from browser → WebSocket → server → Deepgram streaming
7. Interim transcripts displayed in realtime in the dashboard
8. Final transcripts committed to conversation context
9. `DEEPGRAM_API_KEY` env var added to docker-compose for tamma-api
10. Provider fallback: if Deepgram fails, falls back to Whisper
11. Unit tests for both adapters with mock WebSocket

## Files

| File | Action |
|------|--------|
| `packages/voice/src/stt/stt-adapter.ts` | CREATE |
| `packages/voice/src/stt/deepgram-adapter.ts` | CREATE |
| `packages/voice/src/stt/openai-whisper-adapter.ts` | CREATE |
| `packages/dashboard/src/hooks/useVoiceSession.ts` | CREATE |

## Estimated Effort

1 week
