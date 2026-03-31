---
title: "Task 5: Integration Test + End-to-End Text Conversation"
sidebar:
  order: 240
---

**Story:** 24-1-websocket-foundation - WebSocket Foundation
**Epic:** 24

## Task Description

Write integration tests that verify the full text-only voice conversation flow through the WebSocket endpoint. This covers connection establishment, JWT auth, session lifecycle (start/end), text input/response round-trip through the LLM, and engine state forwarding. This task validates that all components from Tasks 1-4 work together correctly.

## Acceptance Criteria

- Integration test connects to Fastify WebSocket endpoint via `ws` client
- Auth: test with valid JWT, test rejection with no JWT
- Session lifecycle: `session.start` -> `session.ready` -> text conversation -> `session.end` -> `session.ended`
- Text conversation: `text.input` -> LLM call -> `response.text` round-trip
- Engine state forwarding: engine state changes appear as `engine.state` messages
- Multi-turn context: second text input sees first turn in LLM messages
- Error handling: invalid JSON, unknown message types
- All tests pass with mock LLM provider (no real API calls)

## Implementation Details

### Technical Requirements

- [ ] Create `packages/api/src/routes/voice/__tests__/voice-integration.test.ts`:

```typescript
import { describe, it, expect, beforeAll, afterAll, vi } from 'vitest';
import Fastify from 'fastify';
import fastifyWebsocket from '@fastify/websocket';
import WebSocket from 'ws';
import { registerVoiceRoutes } from '../index.js';
import type { ServerMessage, ClientMessage } from '@tamma/voice';

// Helper: create a mock TammaEngine
function createMockEngine() { /* ... */ }

// Helper: create a mock ILLMProvider that returns canned responses
function createMockLLMProvider() { /* ... */ }

// Helper: create a test JWT
function createTestJWT(payload: { id: string; username: string }) { /* ... */ }

// Helper: connect WebSocket and wait for open
async function connectWS(url: string, token?: string): Promise<WebSocket> { /* ... */ }

// Helper: receive next JSON message from WebSocket
function nextMessage(ws: WebSocket): Promise<ServerMessage> { /* ... */ }

// Helper: send JSON message
function sendMessage(ws: WebSocket, msg: ClientMessage): void {
  ws.send(JSON.stringify(msg));
}
```

- [ ] Test scenarios:

```typescript
describe('Voice WebSocket Integration', () => {
  let app: FastifyInstance;
  let baseUrl: string;

  beforeAll(async () => {
    app = Fastify();
    await app.register(fastifyWebsocket);
    await app.register(registerVoiceRoutes, {
      engine: createMockEngine(),
    });
    await app.listen({ port: 0 });
    baseUrl = `ws://localhost:${(app.server.address() as AddressInfo).port}`;
  });

  afterAll(async () => {
    await app.close();
  });

  it('rejects unauthenticated connections', async () => {
    // Connect without JWT -> expect close with code 4001
  });

  it('accepts authenticated connections and sends session.ready', async () => {
    // Connect with JWT cookie
    // Send session.start
    // Receive session.ready with sessionId and config
  });

  it('handles text.input and returns response.text', async () => {
    // Connect + start session
    // Send { type: 'text.input', text: 'Hello' }
    // Receive { type: 'response.text', text: '...', isFinal: true }
  });

  it('maintains multi-turn conversation context', async () => {
    // Connect + start session
    // Send first text.input
    // Receive response
    // Send second text.input
    // Verify mock LLM received both turns in messages array
  });

  it('forwards engine state updates to client', async () => {
    // Connect + start session
    // Trigger engine state change on mock
    // Receive { type: 'engine.state', ... }
  });

  it('handles session.end gracefully', async () => {
    // Connect + start session
    // Send session.end
    // Receive session.ended with reason: 'user'
    // WebSocket closes cleanly
  });

  it('sends protocol error for invalid JSON', async () => {
    // Connect + start session
    // Send raw string 'not json'
    // Receive { type: 'error', code: 'PROTOCOL_ERROR', ... }
  });

  it('sends protocol error for unknown message type', async () => {
    // Send { type: 'unknown.type' }
    // Receive { type: 'error', code: 'PROTOCOL_ERROR', ... }
  });
});
```

- [ ] Create test helpers that can be reused by Story 24-2 and 24-3 integration tests

### Files to Modify/Create

- CREATE `packages/api/src/routes/voice/__tests__/voice-integration.test.ts`
- CREATE `packages/api/src/routes/voice/__tests__/test-helpers.ts` (mock engine, LLM, JWT helpers)

### Dependencies

- [ ] Tasks 1-4: all voice package code, routes, nginx config
- [ ] `ws` package (dev dependency for test client)
- [ ] `@fastify/websocket`
- [ ] Mock LLM provider (from `@tamma/providers` test utilities)

## Testing Strategy

### Integration Tests

- [ ] Test connection auth with valid JWT succeeds (101 upgrade)
- [ ] Test connection auth without JWT fails (4001 close)
- [ ] Test session lifecycle: start -> ready -> end -> ended
- [ ] Test text conversation round-trip with mock LLM
- [ ] Test multi-turn context preservation across text inputs
- [ ] Test engine state forwarding
- [ ] Test engine log forwarding (info level only)
- [ ] Test approval request forwarding
- [ ] Test protocol error for invalid JSON
- [ ] Test protocol error for unknown message type
- [ ] Test concurrent text inputs are processed sequentially
- [ ] Test session cleanup on unexpected disconnect

### Validation Steps

1. [ ] Create test helpers (mock engine, LLM, JWT)
2. [ ] Write all integration test cases
3. [ ] Run tests with `pnpm test --filter @tamma/api`
4. [ ] Verify all tests pass with zero real API calls
5. [ ] Verify no resource leaks (WebSocket handles, timers)

## Notes & Considerations

- Integration tests spin up a real Fastify server on port 0 (auto-assigned) and connect with the `ws` client library. This tests the actual HTTP upgrade path, not just unit-level mocks.
- The mock LLM provider should be configurable: set canned responses per test, verify that the correct messages were sent.
- JWT creation for tests should use the same signing key as the auth middleware. Import the JWT utility from `packages/api/src/auth/`.
- These integration tests serve as the acceptance test for Story 24-1. They prove that text-only voice conversation works end-to-end before audio is added in Stories 24-2 and 24-3.
- The test helpers (mock engine, mock LLM, WebSocket client wrappers) will be reused by subsequent story integration tests.

## Completion Checklist

- [ ] Integration test file created with all scenarios
- [ ] Test helpers created and reusable
- [ ] All integration tests pass
- [ ] Auth rejection verified
- [ ] Text conversation round-trip verified
- [ ] Engine state forwarding verified
- [ ] Multi-turn context verified
- [ ] Error handling verified
- [ ] No resource leaks in tests
- [ ] TypeScript strict mode compiles without errors
