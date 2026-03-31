# Epic 24: Realtime Voice Conversation with Orchestrator

**Status:** Drafted (research complete, 24 task plans ready)
**Stories:** 7 (24-0 through 24-6)
**Task Plans:** 24 detailed implementation breakdowns
**Packages:** `@tamma/api`, `@tamma/dashboard`

## Overview

Voice as a first-class input/output mode for the Tamma orchestrator. Users talk to Tamma through their browser and hear spoken responses -- like a voice call with an AI developer assistant.

## Architecture

**Option B (Pipeline):** Voice is an input mode layered on the existing text-based orchestrator.

- **STT:** Deepgram streaming (primary), OpenAI Whisper (fallback)
- **Brain:** Existing ILLMProvider chain (Claude, etc.) with tool use
- **TTS:** ElevenLabs streaming (primary), OpenAI TTS (fallback)
- **Transport:** New `VoiceEngineTransport` implements `IEngineTransport`

**Protocol:** WebSocket (`wss://api.tamma.dev/api/v1/voice`) with binary PCM16 audio frames + JSON control messages.

## Stories

All 6 implementation stories now have detailed task plan breakdowns.

| # | Story | Task Plans | Status | Description |
|---|-------|------------|--------|-------------|
| 24-0 | Voice API Research | -- | Done | Research of 7 realtime audio/voice APIs for building a voice interface |
| 24-1 | WebSocket Foundation | 5 | Planned | WebSocket endpoint at `/api/v1/voice` with JWT auth, session lifecycle, JSON message routing |
| 24-2 | Speech-to-Text Integration | 4 | Planned | Deepgram streaming STT with OpenAI Whisper fallback; realtime transcription |
| 24-3 | Text-to-Speech Integration | 4 | Planned | ElevenLabs streaming TTS with OpenAI TTS fallback; spoken responses |
| 24-4 | Intent Classification + Engine Integration | 3 | Planned | Understand spoken commands and execute orchestrator actions via voice |
| 24-5 | Dashboard Voice UI | 4 | Planned | Voice mode toggle in dashboard with visual feedback for text/voice switching |
| 24-6 | Hardening + Production Readiness | 4 | Planned | Reliability, cost tracking, and recoverability for production voice sessions |

**Total: 24 task plans across 6 implementation stories**

[Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-24)

## Cost Estimate

~$0.10-0.25 per 10-minute voice session (STT + TTS), plus existing LLM costs.

## Latency Target

< 1.5s end-to-end (speech end to first audio response).

## Data Flow

```
Browser Microphone
    |
    | PCM16 audio frames via WebSocket
    v
VoiceEngineTransport (packages/api)
    |
    +---> Deepgram STT (streaming) ---> transcript text
    |                                        |
    |                                        v
    |                                   Intent Classifier
    |                                        |
    |                                        v
    |                                   Engine / LLM Chain
    |                                        |
    |                                        v
    +<--- ElevenLabs TTS (streaming) <--- response text
    |
    | PCM16 audio frames via WebSocket
    v
Browser Speaker
```

---

_See the [Roadmap](Roadmap) for how this epic fits into the overall plan._
