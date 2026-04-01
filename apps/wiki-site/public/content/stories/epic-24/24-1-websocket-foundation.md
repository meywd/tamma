---
title: "Story 24-1: WebSocket Foundation"
sidebar:
  order: 240
---

Status: planned

## Story

As a developer, I need a WebSocket endpoint at `/api/v1/voice` with JWT authentication, session lifecycle management, and JSON message routing so that voice features can be built on a solid foundation.

## Acceptance Criteria

1. `@fastify/websocket` added to `packages/api`
2. `GET /api/v1/voice` upgrades to WebSocket with JWT cookie or Bearer token auth — rejects unauthenticated connections with 401
3. `VoiceSession` class manages one WebSocket connection lifecycle: init, message routing, cleanup
4. JSON protocol implemented: `session.start`, `session.end`, `session.ready`, `error` message types
5. Text-only conversation mode works: user sends `text.input`, receives `response.text` via existing `ILLMProvider` chain
6. `VoiceEngineTransport` implements `IEngineTransport`, bridges voice session to `TammaEngine` commands
7. Engine state updates forwarded as `engine.state` messages to the WebSocket client
8. REST endpoints: `GET /api/v1/voice/config`, `PUT /api/v1/voice/config`
9. Nginx config updated with WebSocket proxy for `/api/v1/voice` (1hr timeout, buffering off)
10. Unit tests for session lifecycle, auth, message routing

## Files

| File | Action |
|------|--------|
| `packages/voice/src/voice-session.ts` | CREATE |
| `packages/voice/src/conversation-context.ts` | CREATE |
| `packages/voice/src/types.ts` | CREATE |
| `packages/voice/src/index.ts` | CREATE |
| `packages/api/src/routes/voice/index.ts` | CREATE |
| `packages/orchestrator/src/transports/voice.ts` | CREATE |
| `packages/shared/src/contracts/voice-transport.ts` | CREATE |
| `docker/nginx-proxy.conf` | MODIFY |
| `packages/api/package.json` | MODIFY (add @fastify/websocket) |

## Estimated Effort

1-2 weeks
