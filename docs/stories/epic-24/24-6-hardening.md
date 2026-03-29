# Story 24-6: Hardening + Production Readiness

Status: planned

## Story

As an operator, I need voice sessions to be reliable, cost-tracked, and recoverable so the feature works in production.

## Acceptance Criteria

1. WebSocket reconnection: auto-reconnect with exponential backoff on connection drop
2. Provider fallback chain: Deepgram fails → Whisper, ElevenLabs fails → OpenAI TTS — seamless switchover
3. Rate limiting: max 1 active voice session per user, configurable max total sessions
4. Cost tracking: STT/TTS API usage tracked per session, surfaced in budget dashboard
5. Session recording: full transcript persisted to `chat_messages` table (linked to chat conversation)
6. Session timeout: auto-disconnect after configurable idle period (default 30 min)
7. Error recovery: if STT/TTS provider errors mid-session, gracefully degrade to text mode with user notification
8. Performance: end-to-end latency <1.5s (speech end → first audio response) verified under load
9. Security: STT/TTS API keys stored server-side only, never exposed to browser
10. Integration tests with mock STT/TTS providers
11. Load test: 10 concurrent voice sessions on VPS without degradation

## Estimated Effort

1 week
