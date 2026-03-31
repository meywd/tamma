# Epic 24: Realtime Voice Conversation with Orchestrator

Voice as a first-class input/output mode for the Tamma orchestrator. Users talk to Tamma through their browser and hear spoken responses — like a voice call with an AI developer assistant.

## Architecture

**Option B (Pipeline)**: Voice is an input mode layered on the existing text-based orchestrator.
- **STT**: Deepgram streaming (primary), OpenAI Whisper (fallback)
- **Brain**: Existing ILLMProvider chain (Claude, etc.) with tool use
- **TTS**: ElevenLabs streaming (primary), OpenAI TTS (fallback)
- **Transport**: New `VoiceEngineTransport` implements `IEngineTransport`

**Protocol**: WebSocket (`wss://api.tamma.dev/api/v1/voice`) with binary PCM16 audio frames + JSON control messages.

## Stories

| # | Story | Status |
|---|-------|--------|
| 24-0 | [Voice API Research](24-0-voice-api-research.md) | done |
| 24-1 | [WebSocket Foundation](24-1-websocket-foundation.md) | planned |
| 24-2 | [Speech-to-Text Integration](24-2-speech-to-text.md) | planned |
| 24-3 | [Text-to-Speech Integration](24-3-text-to-speech.md) | planned |
| 24-4 | [Intent Classification + Engine Integration](24-4-intent-engine.md) | planned |
| 24-5 | [Dashboard Voice UI](24-5-dashboard-voice-ui.md) | planned |
| 24-6 | [Hardening + Production Readiness](24-6-hardening.md) | planned |

## Cost Estimate

~$0.10-0.25 per 10-minute voice session (STT + TTS), plus existing LLM costs.

## Latency Target

<1.5s end-to-end (speech end → first audio response).
