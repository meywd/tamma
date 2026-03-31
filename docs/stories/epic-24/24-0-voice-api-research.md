# Story 24-0: Voice API Research

Status: done

## Summary

Researched 7 realtime audio/voice APIs for building a voice interface to the orchestrator.

## Findings

| Provider | Protocol | Latency | Cost/min | Tool Calling | Recommendation |
|----------|----------|---------|----------|-------------|----------------|
| OpenAI Realtime | WebSocket/WebRTC | ~2.2s turn | ~$0.30 | Yes (native) | Option A candidate |
| Gemini Live | WebSocket | 1-3s | ~$0.05 | Yes (native) | Cheapest native option |
| Deepgram | WebSocket | <300ms STT, 90ms TTS | $0.075 all-in | Yes (Voice Agent) | Best STT for pipeline |
| ElevenLabs | WebSocket | 75ms TTS | char-based | Yes (Conversational AI) | Best TTS quality |
| AssemblyAI | WebSocket | 150ms STT | $0.15/hr | No (STT only) | Best accuracy |
| Claude | No audio API | N/A | N/A | Yes (text only) | Brain in pipeline |
| Web Speech API | Browser JS | instant | Free | No | Fallback only |

## Decision

**Option B (Pipeline)**: Deepgram STT → Claude/LLM (with tools) → ElevenLabs TTS. Fits Tamma's multi-provider architecture. Can also support Option A (native) for OpenAI/Gemini when available.
