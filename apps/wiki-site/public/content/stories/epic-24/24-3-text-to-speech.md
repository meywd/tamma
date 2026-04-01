---
title: "Story 24-3: Text-to-Speech Integration"
sidebar:
  order: 240
---

Status: planned

## Story

As a user, I want to hear Tamma's responses spoken aloud so I can have a natural voice conversation.

## Acceptance Criteria

1. `ITTSAdapter` interface defined with `connect()`, `synthesize()` (returns `AsyncIterable<Buffer>` of PCM16), `cancel()`, `dispose()`
2. `ElevenLabsAdapter` implements `ITTSAdapter` using ElevenLabs streaming WebSocket (Flash v2.5, ~75ms first-byte)
3. `OpenAITTSAdapter` implements `ITTSAdapter` as fallback (tts-1, streaming)
4. Server streams TTS audio chunks as binary WebSocket frames to browser
5. Browser: AudioWorklet-based playback with ring buffer for smooth output
6. Streaming: TTS starts as soon as the first sentence of LLM output arrives (don't wait for full response)
7. Interruption handling: when user starts speaking (VAD fires), server stops TTS stream, sends `response.cancel`, browser stops playback
8. `ELEVENLABS_API_KEY` env var added to docker-compose for tamma-api
9. Voice selection: configurable via `PUT /api/v1/voice/config`
10. Provider fallback: if ElevenLabs fails, falls back to OpenAI TTS
11. Unit tests for both adapters

## Files

| File | Action |
|------|--------|
| `packages/voice/src/tts/tts-adapter.ts` | CREATE |
| `packages/voice/src/tts/elevenlabs-adapter.ts` | CREATE |
| `packages/voice/src/tts/openai-tts-adapter.ts` | CREATE |

## Estimated Effort

1 week
