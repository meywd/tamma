# Epic 24: Realtime Voice Conversation with Orchestrator

**Status:** Drafted — 24-0 research done; 6 implementation stories planned with 24 task plans. No voice-package code yet.
**Stories:** 7 (24-0 research + 24-1 through 24-6 implementation)
**Task plans:** 24 detailed implementation breakdowns
**Packages (planned):** `@tamma/voice` (new), `@tamma/api` (`routes/voice/*`), `@tamma/dashboard` (`components/voice/*`), `@tamma/orchestrator` (`transports/voice.ts`), `@tamma/shared` (`contracts/voice-transport.ts`)

## Overview

Epic 24 makes voice a first-class input/output mode for the Tamma orchestrator. A user opens `app.tamma.dev`, toggles voice mode, and starts talking — Tamma hears them, routes the transcript through the existing LLM + tool-use chain, and speaks responses back. It also proactively narrates engine state changes ("tests passed", "PR created") so the user can keep coding while Tamma runs in the background.

The approach is **Option B (Pipeline)** from Story 24-0's research: voice is layered as an input mode on top of the existing text-based orchestrator rather than as a replacement transport. The LLM "brain" is unchanged; STT and TTS sit on the edges. The transport layer is a new `VoiceEngineTransport` that implements the existing `IEngineTransport` contract — so the engine doesn't know or care that it's talking to a human through microphones.

**Targets**:
- End-to-end latency (speech end → first audio): **< 1.5s**
- Cost per 10-minute session: **~$0.10–$0.25** (STT + TTS, plus existing LLM costs)
- Graceful degradation: transcript + text response always works even when TTS fails

## Architecture

```
 ┌────────────────────────────────────────────────────────────────────────────┐
 │                             Browser (Dashboard)                             │
 │  ┌───────────────┐  ┌───────────────┐  ┌───────────────────────────────┐   │
 │  │ MediaRecorder │  │ AudioWorklet  │  │ VoiceUI (toggle, status, TX)  │   │
 │  │ (mic capture) │  │ (PCM16 frames)│  │                               │   │
 │  └───────┬───────┘  └───────┬───────┘  └───────┬───────────────────────┘   │
 │          │                  │                   │                           │
 │          └──────────────────┴───────────────────┘                           │
 │                              │                                              │
 │                              ▼  WebSocket  (PCM16 binary + JSON control)   │
 └──────────────────────────────┼──────────────────────────────────────────────┘
                                │
 ┌──────────────────────────────┼──────────────────────────────────────────────┐
 │                            Tamma API (Node)                                  │
 │  wss://api.tamma.dev/api/v1/voice  (@fastify/websocket)                      │
 │    │                                                                         │
 │    ▼                                                                         │
 │  VoiceSession (per WS connection)                                            │
 │    ├── ConversationContext (multi-turn history, mixed voice+text)           │
 │    ├── STT adapter  ────▶ Deepgram streaming   (primary)                    │
 │    │                └──▶ OpenAI Whisper        (fallback)                   │
 │    ├── IntentClassifier  (command / question / conversational feedback)      │
 │    ├── VoiceEngineTransport  ─▶  TammaEngine commands                        │
 │    │                         ◀─  engine state + approval requests           │
 │    └── TTS adapter  ────▶ ElevenLabs streaming (primary)                    │
 │                     └──▶ OpenAI TTS            (fallback)                   │
 │    ▲                                                                         │
 │    │ engine state changes / approval requests                                │
 │  TammaEngine (existing orchestrator, unchanged)                              │
 └──────────────────────────────────────────────────────────────────────────────┘
```

**Protocol** (WebSocket, `wss://api.tamma.dev/api/v1/voice`):
- **Binary frames** — raw PCM16 audio (16 kHz mono, 20ms chunks) in both directions.
- **JSON control frames** — `session.start`, `session.ready`, `session.end`, `text.input`, `response.text`, `engine.state`, `approval.request`, `approval.response`, `error`.

## Components

| Component | Location (planned) | Responsibility |
|-----------|--------------------|----------------|
| `VoiceSession` | `packages/voice/src/voice-session.ts` | Owns one WebSocket; routes JSON + binary, drives STT/TTS adapters, commits to `ConversationContext`. |
| `ConversationContext` | `packages/voice/src/conversation-context.ts` | Multi-turn memory; lets voice and text interleave in the same history. |
| `IVoiceTransport` types | `packages/voice/src/types.ts` | Protocol types (session, message, frame). |
| `VoiceEngineTransport` | `packages/orchestrator/src/transports/voice.ts` | Implements `IEngineTransport`; bridges voice session ↔ `TammaEngine`. Handles proactive notifications (24-4 AC-5). |
| `IEngineTransport` contract | `packages/shared/src/contracts/voice-transport.ts` | Defines transport methods consumed by the engine. |
| Voice routes | `packages/api/src/routes/voice/index.ts` | Fastify `/api/v1/voice` (WebSocket upgrade) + `/voice/config` REST (24-1). |
| JWT auth | Fastify hook on upgrade | Rejects upgrade with 401 if no valid session cookie / bearer. |
| Deepgram STT adapter | `packages/voice/src/stt-deepgram.ts` | Streaming transcription; circuit-break → Whisper fallback (24-2). |
| Whisper STT adapter | `packages/voice/src/stt-whisper.ts` | Non-streaming fallback for Deepgram outages (24-2). |
| ElevenLabs TTS adapter | `packages/voice/src/tts-elevenlabs.ts` | Streaming synthesis; circuit-break → OpenAI TTS fallback (24-3). |
| OpenAI TTS adapter | `packages/voice/src/tts-openai.ts` | Fallback synthesis (24-3). |
| `IntentClassifier` | `packages/voice/src/intent-classifier.ts` | LLM-assisted classification: engine command / status question / conversational feedback (24-4). |
| Dashboard UI | `packages/dashboard/src/components/voice/*` | Voice toggle, live transcript, status indicator, approval prompt, hybrid voice+text switcher (24-5). |
| Hardening | tests + retries + cost tracking + session-resume | 24-6 — reliability, cost, recoverability. |
| Nginx proxy config | `docker/nginx-proxy.conf` | WebSocket proxy for `/api/v1/voice`, 1-hour timeout, buffering off. |

## Class diagram

```
                          ┌────────────────────────────┐
                          │      IEngineTransport      │
                          │ send(engineMessage)        │
                          │ onCommand(cb)              │
                          │ onApprovalRequest(cb)      │
                          └──────────────┬─────────────┘
                                         │ implements
                          ┌──────────────▼─────────────┐
                          │   VoiceEngineTransport     │
                          │  (proactive notifications) │
                          └──────────────┬─────────────┘
                                         │ owns 1:1
                                         ▼
                    ┌─────────────────────────────────────┐
                    │           VoiceSession              │
                    │  ws: WebSocket                      │
                    │  ctx: ConversationContext           │
                    │  stt: ISttAdapter                   │
                    │  tts: ITtsAdapter                   │
                    │  intent: IntentClassifier           │
                    │  onMessage(frame | json)            │
                    │  routeText(userText)                │
                    │  speak(responseText)                │
                    └───────┬───────┬───────┬─────────────┘
                            │       │       │
                  ┌─────────▼──┐ ┌──▼────┐ ┌▼──────────────────┐
                  │ISttAdapter │ │ITtsAd.│ │IntentClassifier   │
                  └─────┬──────┘ └───┬───┘ │ classify(text)    │
       ┌────────────────┴──┐      ┌──┴───────┐→ command | question | feedback
       │DeepgramSttAdapter │      │ElevenLabs│
       │WhisperSttAdapter  │      │OpenAiTts │
       └───────────────────┘      └──────────┘

 ┌───────────────────────────────────────────────────────┐
 │  TammaEngine (existing, unchanged)                    │
 │  executes workflows; emits engine.state + approvals   │
 │  ◀ VoiceEngineTransport ▶                             │
 └───────────────────────────────────────────────────────┘
```

## Sequence diagram — 10-second voice turn

```
Browser          WS / VoiceSession        Deepgram         LLM chain         Engine        ElevenLabs
   │                    │                    │                 │                │               │
   │ session.start      │                    │                 │                │               │
   │ (JWT cookie)       │                    │                 │                │               │
   │───────────────────▶│ authenticate       │                 │                │               │
   │                    │────┐               │                 │                │               │
   │ session.ready      │    │               │                 │                │               │
   │◀───────────────────│    │               │                 │                │               │
   │                    │                    │                 │                │               │
   │ PCM16 frames (mic) │                    │                 │                │               │
   │══════════════════▶ │ forward stream    │                 │                │               │
   │                    │───────────────────▶│ streaming STT   │                │               │
   │                    │                    │◀────────────────│ partials       │               │
   │                    │                    │                 │                │               │
   │                    │ final transcript   │                 │                │               │
   │                    │◀───────────────────│                 │                │               │
   │                    │ classify intent    │                 │                │               │
   │                    │ → command          │                 │                │               │
   │                    │ route to engine    │                 │                │               │
   │                    │────────────────────────────────────────────────────▶  │               │
   │                    │                    │                 │                │ execute cmd  │
   │                    │                    │                 │                │────┐          │
   │                    │                    │                 │                │◀───┘          │
   │                    │ engine.state       │                 │                │ emit state    │
   │                    │◀───────────────────────────────────────────────────────               │
   │ engine.state JSON  │                                                                       │
   │◀═══════════════════│                                                                       │
   │                    │ generate TTS       │                 │                                │
   │                    │──────────────────────────────────────────────────────────────────────▶│
   │                    │◀══════════════════════════════════════════════════════════════════════│ streaming audio
   │ PCM16 frames (out) │                                                                       │
   │◀═══════════════════│                                                                       │
   │                                                                                             │
```

## Use cases

1. **Solo dev pair-codes with Tamma**: toggles voice on, says "implement issue 42", Tamma narrates plan, asks "approve?", user says "yes", Tamma starts implementing and proactively announces "tests pass" a few minutes later.
2. **Dev feedback during review**: Tamma says "the plan is to refactor auth-service", user says "that won't work — we need to preserve the old cookie format for 30 days" — `IntentClassifier` routes this as conversational feedback, `VoiceEngineTransport` rejects the plan with that context, a new plan is generated.
3. **Hybrid voice + text**: user starts with voice, switches to typing mid-conversation for a code snippet, context is preserved.
4. **Proactive narration**: user is in the kitchen; Tamma announces "tests passed" / "PR created" / "review requested" over the speakers without any prompt from the user.
5. **Approval by voice**: engine emits `approval.request` for a risky destructive command; user says "approve" or "reject with feedback: not safe in prod", routed back to the engine's `approvalHandler`.
6. **Network hiccup recovery (24-6)**: WebSocket drops mid-turn; client reconnects with the same `sessionId`; `ConversationContext` is rehydrated; conversation resumes without losing state.
7. **Cost cap**: session exceeds the per-session cost cap; `VoiceSession` emits `error: cost_limit_exceeded`, UI surfaces a friendly message and a "continue in text mode" button.
8. **TTS provider outage**: ElevenLabs 5xx; circuit-breaker opens; subsequent responses use OpenAI TTS fallback; user is never blocked.

## Stories

| # | Story | Task plans | Description |
|---|-------|-----------:|-------------|
| 24-0 | [Voice API Research](https://github.com/meywd/tamma/blob/main/docs/stories/epic-24/24-0-voice-api-research.md) | — | Comparison of 7 realtime audio/voice APIs; picks Deepgram + ElevenLabs with OpenAI as the fallback pair. **Done.** |
| 24-1 | [WebSocket Foundation](https://github.com/meywd/tamma/blob/main/docs/stories/epic-24/24-1-websocket-foundation.md) | 5 | `@fastify/websocket` endpoint, JWT auth, `VoiceSession` lifecycle, JSON protocol, `VoiceEngineTransport`, text-only mode, nginx config. |
| 24-2 | [Speech-to-Text Integration](https://github.com/meywd/tamma/blob/main/docs/stories/epic-24/24-2-speech-to-text.md) | 4 | Deepgram streaming STT + Whisper fallback; partial + final transcript routing. |
| 24-3 | [Text-to-Speech Integration](https://github.com/meywd/tamma/blob/main/docs/stories/epic-24/24-3-text-to-speech.md) | 4 | ElevenLabs streaming TTS + OpenAI TTS fallback; streaming audio frames back to browser. |
| 24-4 | [Intent Classification + Engine Integration](https://github.com/meywd/tamma/blob/main/docs/stories/epic-24/24-4-intent-engine.md) | 3 | Classify command vs question vs feedback; multi-turn memory; proactive notifications; approval-by-voice. |
| 24-5 | [Dashboard Voice UI](https://github.com/meywd/tamma/blob/main/docs/stories/epic-24/24-5-dashboard-voice-ui.md) | 4 | Voice toggle, transcript pane, status indicator, hybrid voice+text switcher. |
| 24-6 | [Hardening + Production Readiness](https://github.com/meywd/tamma/blob/main/docs/stories/epic-24/24-6-hardening.md) | 4 | Reconnect, cost tracking, session resume, chaos testing. |

**Total:** 24 task plans across 6 implementation stories.

## Dependency order

```
 24-0 (Research) → shapes all decisions

 24-1 (WebSocket Foundation)
    │
    ├──▶ 24-2 (STT)  ──┐
    ├──▶ 24-3 (TTS)  ──┤
    │                  ▼
    └──▶ 24-4 (Intent + Engine)
               │
               ├──▶ 24-5 (Dashboard UI)
               │
               └──▶ 24-6 (Hardening)
```

## Dependencies

| Dependency | Epic | Reason |
|-----------|------|--------|
| AI Providers | Epic 1 | LLM chain consumed by voice turns and `IntentClassifier` |
| Engine Core | Epic 10 | `TammaEngine` drives commands + emits state |
| Unified Auth & RBAC | Epic 16 | JWT cookie or bearer gate on WebSocket upgrade |
| Cost Monitor | Epic 1.5 / Epic 6 | Per-session cost caps + billing |
| Observability Dashboard | Epic 5 | Dashboard is the surface for voice UI |
| Agent Dispatch | Epic 19 | `VoiceEngineTransport` may approve dispatches by voice |

## Current state

- **Drafted**: all 6 implementation stories with 24 task plans in `docs/stories/epic-24/`. No voice-package code exists yet (`packages/voice/` is not created, and `packages/api/src/routes/voice/` is empty).
- **Research complete**: Story 24-0 selected Deepgram (primary STT) + Whisper (fallback), ElevenLabs (primary TTS) + OpenAI TTS (fallback), Option B pipeline, and the WebSocket binary-PCM16 protocol.
- **External API keys required** (planned): `DEEPGRAM_API_KEY`, `OPENAI_API_KEY`, `ELEVENLABS_API_KEY` — all per-tenant via the secret cabinet (Epic 29).
- **Not yet chosen**: VAD (voice activity detection) strategy — Deepgram's built-in VAD is the default plan; client-side fallback via WebRTC VAD kept as a spike.

## Open risks

1. **Latency budget** — end-to-end < 1.5s is tight; early benchmarks on Deepgram streaming + ElevenLabs streaming put the floor around 700ms, leaving ~800ms for LLM + network.
2. **Cost surprises** — ElevenLabs is the expensive edge; hardening (24-6) ships per-session cost caps and a live cost meter in the UI.
3. **Accent / noise robustness** — Deepgram's streaming model is best-in-class today, but real-world noise will still produce mis-transcription; `IntentClassifier` must handle garbled input gracefully.
4. **WebSocket through nginx** — 24-1 includes the nginx fix (buffering off, 1-hour `proxy_read_timeout`) to prevent idle disconnects.
5. **Mobile browsers** — microphone permissions and background audio behaviour vary; 24-5 adds a "keep tab active" hint.

## See also

- [Epic 1 — Foundation](Epic-1-Foundation.md) — multi-provider AI abstraction used by the brain.
- [Epic 10 — Engine Core](Epic-10-Engine-Core.md) — `TammaEngine` and `IEngineTransport`.
- [Epic 16 — Unified Auth & RBAC](Epic-16-Auth-Admin.md) — session gating on WebSocket upgrade.
- [Epic 5 — Observability Dashboard](Epic-5-Observability.md) — where the voice UI lives.
- [Epic 23 — System Monitoring](Epic-23-System-Monitoring.md) — session-level metrics + cost observability.
- [Roadmap](Roadmap.md) — overall plan.

## Story files

[Epic 24 stories on GitHub](https://github.com/meywd/tamma/tree/main/docs/stories/epic-24)

---

_Last updated: 2026-04-22_
