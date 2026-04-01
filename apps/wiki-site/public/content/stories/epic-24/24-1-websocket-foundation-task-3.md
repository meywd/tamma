---
title: "Task 3: VoiceEngineTransport + Fastify WebSocket Route"
sidebar:
  order: 240
---

**Story:** 24-1-websocket-foundation - WebSocket Foundation
**Epic:** 24

## Task Description

Create `VoiceEngineTransport` that implements `IEngineTransport` to bridge voice sessions to the `TammaEngine`. Create the Fastify WebSocket route at `GET /api/v1/voice` with JWT authentication that upgrades HTTP connections to WebSocket and instantiates `VoiceSession` instances.

## Acceptance Criteria

- `VoiceEngineTransport` implements `IEngineTransport` interface
- Transport bridges `VoiceSession` events to engine commands and engine state updates to voice messages
- Engine state updates forwarded as `engine.state` messages to the WebSocket client
- Engine log entries forwarded as `engine.log` messages (info+ only, not debug)
- Approval requests forwarded as `engine.approval` messages
- `GET /api/v1/voice` upgrades to WebSocket with JWT cookie or Bearer token auth
- Unauthenticated connections rejected with 401 before upgrade
- `@fastify/websocket` added to `packages/api` dependencies
- REST endpoints: `GET /api/v1/voice/config`, `PUT /api/v1/voice/config`
- Unit tests for transport, route auth, message forwarding

## Implementation Details

### Technical Requirements

- [ ] Create `packages/orchestrator/src/transports/voice.ts`:

```typescript
import { EventEmitter } from 'node:events';
import type {
  IEngineTransport, EngineCommand, CommandResult,
  EngineStateUpdate, EngineLogEntry,
} from '@tamma/shared/contracts';
import type { DevelopmentPlan, EngineEvent } from '@tamma/shared';
import type { VoiceSession } from '@tamma/voice';

type TransportEventMap = {
  stateUpdate: [EngineStateUpdate];
  log: [EngineLogEntry];
  approvalRequest: [DevelopmentPlan];
  event: [EngineEvent];
};

export class VoiceEngineTransport implements IEngineTransport {
  private readonly emitter = new EventEmitter();
  private session: VoiceSession | null = null;
  private disposed = false;
  private pendingApprovalResolve: ((decision: 'approve' | 'reject' | 'skip') => void) | null = null;
  private queuedDecision: 'approve' | 'reject' | 'skip' | null = null;

  /** Bind a VoiceSession to this transport. */
  bindSession(session: VoiceSession): void;

  /** Unbind the current session (e.g., on disconnect). */
  unbindSession(): void;

  // --- IEngineTransport commands ---
  async sendCommand(command: EngineCommand): Promise<CommandResult>;

  // --- IEngineTransport subscriptions ---
  onStateUpdate(listener: (update: EngineStateUpdate) => void): () => void;
  onLog(listener: (entry: EngineLogEntry) => void): () => void;
  onApprovalRequest(listener: (plan: DevelopmentPlan) => void): () => void;
  onEvent(listener: (event: EngineEvent) => void): () => void;

  // --- Wiring helpers (same pattern as InProcessTransport) ---
  createStateChangeHandler(): (newState, issue, stats) => void;
  createApprovalHandler(): (plan: DevelopmentPlan) => Promise<'approve' | 'reject' | 'skip'>;
  createLoggerProxy(): ILogger;

  // --- Voice-specific: forward engine events to session ---
  /** Called when engine state changes. Forwards as engine.state message. */
  private forwardStateUpdate(update: EngineStateUpdate): void;
  /** Called on engine log. Forwards info+ as engine.log message. */
  private forwardLog(entry: EngineLogEntry): void;
  /** Called on approval request. Forwards as engine.approval message. */
  private forwardApprovalRequest(plan: DevelopmentPlan): void;

  async dispose(): Promise<void>;
  private resolveApproval(decision: 'approve' | 'reject' | 'skip'): void;
}
```

- [ ] Create `packages/api/src/routes/voice/index.ts`:

```typescript
import type { FastifyInstance } from 'fastify';
import type { TammaEngine } from '@tamma/orchestrator';
import { VoiceSession } from '@tamma/voice';
import type { VoiceSessionConfig } from '@tamma/voice';
import { DEFAULT_VOICE_CONFIG } from '@tamma/voice';

export interface VoiceRouteOptions {
  engine: TammaEngine;
}

export async function registerVoiceRoutes(
  fastify: FastifyInstance,
  opts: VoiceRouteOptions,
): Promise<void> {

  // --- WebSocket upgrade: GET /api/v1/voice ---
  fastify.get('/api/v1/voice', { websocket: true }, (socket, request) => {
    // Auth: verify JWT from cookie or Authorization header
    // If invalid: socket.close(4001, 'Unauthorized'), return
    // Create VoiceSession with deps (llmProvider, userId, sessionId)
    // Wire session to VoiceEngineTransport
    // Handle socket close -> cleanup
  });

  // --- REST: GET /api/v1/voice/config ---
  fastify.get('/api/v1/voice/config', async (request, reply) => {
    // Return current user's voice config from DB or defaults
    return reply.send({ config: DEFAULT_VOICE_CONFIG });
  });

  // --- REST: PUT /api/v1/voice/config ---
  fastify.put('/api/v1/voice/config', async (request, reply) => {
    // Validate and persist voice config for user
    // Zod schema for partial VoiceSessionConfig
    return reply.send({ ok: true, config: mergedConfig });
  });
}
```

- [ ] Auth implementation detail for WebSocket upgrade:
  1. Check `request.headers.cookie` for `tamma_session` JWT
  2. Fallback: check `request.headers.authorization` for `Bearer <token>`
  3. Verify JWT using same logic as existing auth middleware
  4. Extract `userId` from JWT payload
  5. If auth fails: send JSON `{ type: 'error', code: 'AUTH_FAILED' }` then close with code 4001

- [ ] Add `@fastify/websocket` to `packages/api/package.json`:
  ```
  "@fastify/websocket": "^11.0.0"
  ```

- [ ] Register `@fastify/websocket` plugin in Fastify app setup (likely `packages/api/src/serve.ts` or route registration)

### Files to Modify/Create

- CREATE `packages/orchestrator/src/transports/voice.ts`
- CREATE `packages/orchestrator/src/transports/voice.test.ts`
- CREATE `packages/api/src/routes/voice/index.ts`
- CREATE `packages/api/src/routes/voice/__tests__/voice-routes.test.ts`
- MODIFY `packages/api/package.json` -- add `@fastify/websocket`
- MODIFY `packages/api/src/serve.ts` (or route registration file) -- register websocket plugin and voice routes

### Dependencies

- [ ] Task 1: Voice types, ISTTAdapter, ITTSAdapter
- [ ] Task 2: VoiceSession, ConversationContext
- [ ] `@fastify/websocket` (new dependency)
- [ ] Existing auth middleware from `packages/api/src/auth/`
- [ ] `IEngineTransport`, `EngineCommand`, etc. from `@tamma/shared/contracts`
- [ ] `InProcessTransport` as reference implementation

## Testing Strategy

### Unit Tests -- voice.test.ts (VoiceEngineTransport)

- [ ] Test `bindSession()` stores session reference
- [ ] Test `unbindSession()` clears session reference
- [ ] Test `sendCommand('approve')` resolves pending approval
- [ ] Test `sendCommand('reject')` resolves pending approval
- [ ] Test `sendCommand('skip')` resolves pending approval
- [ ] Test `sendCommand('start')` returns `{ ok: true }` (delegates to engine)
- [ ] Test `onStateUpdate` listener receives engine state changes
- [ ] Test `onLog` listener receives engine log entries
- [ ] Test `onApprovalRequest` listener receives approval requests
- [ ] Test `createStateChangeHandler()` emits stateUpdate events
- [ ] Test `createApprovalHandler()` emits approvalRequest and waits for resolution
- [ ] Test `createLoggerProxy()` forwards log calls as EngineLogEntry events
- [ ] Test state update forwarded to VoiceSession as `engine.state` message
- [ ] Test log entry (info level) forwarded as `engine.log` message
- [ ] Test log entry (debug level) NOT forwarded (filtered out)
- [ ] Test approval request forwarded as `engine.approval` message
- [ ] Test `dispose()` is idempotent
- [ ] Test `dispose()` resolves pending approval with 'skip'
- [ ] Test queued decision pattern (approval sent before request arrives)

### Unit Tests -- voice-routes.test.ts

- [ ] Test WebSocket upgrade with valid JWT cookie succeeds
- [ ] Test WebSocket upgrade with valid Bearer token succeeds
- [ ] Test WebSocket upgrade with no auth returns 401
- [ ] Test WebSocket upgrade with expired JWT returns 401
- [ ] Test `GET /api/v1/voice/config` returns default config
- [ ] Test `PUT /api/v1/voice/config` updates and returns merged config
- [ ] Test `PUT /api/v1/voice/config` validates input (rejects invalid provider names)

### Validation Steps

1. [ ] Create VoiceEngineTransport following InProcessTransport patterns
2. [ ] Create voice route with WebSocket upgrade and JWT auth
3. [ ] Register @fastify/websocket plugin
4. [ ] Wire VoiceSession creation inside WebSocket handler
5. [ ] Test auth rejection for unauthenticated connections
6. [ ] Test text-only conversation through WebSocket
7. [ ] Verify engine state forwarding to voice session
8. [ ] Run all unit tests
9. [ ] Verify TypeScript strict mode compilation

## Notes & Considerations

- The `VoiceEngineTransport` follows the same patterns as `InProcessTransport` (EventEmitter, pending approval resolve/queue, createStateChangeHandler/createApprovalHandler/createLoggerProxy). The key difference is that it also forwards events to the bound `VoiceSession` as WebSocket messages.
- The `@fastify/websocket` plugin needs to be registered before the voice route. Check the Fastify plugin registration order in `serve.ts`.
- JWT auth for WebSocket is tricky because the browser's `new WebSocket()` API doesn't support custom headers. Auth must come from: (a) the `tamma_session` cookie (set by GitHub OAuth), or (b) a token in the query string `?token=xxx` as a fallback. The `Authorization` header approach works for non-browser clients.
- The REST config endpoints use the same auth middleware as other API routes. No special handling needed.
- Voice sessions should be tracked in a `Map<string, VoiceSession>` for rate limiting (max 1 per user) and cleanup.

## Completion Checklist

- [ ] `VoiceEngineTransport` implements `IEngineTransport`
- [ ] Transport forwards engine events to bound VoiceSession
- [ ] WebSocket route created with JWT authentication
- [ ] `@fastify/websocket` added and registered
- [ ] REST config endpoints created
- [ ] Auth rejection works for unauthenticated connections
- [ ] All unit tests written and passing
- [ ] TypeScript strict mode compiles without errors
