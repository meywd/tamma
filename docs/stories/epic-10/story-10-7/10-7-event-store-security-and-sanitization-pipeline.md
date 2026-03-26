# Story 10.7: Event Store Security & Sanitization Pipeline

Status: ready-for-dev

## Story

As a **platform architect**,
I want every piece of content flowing into the event store to go through a sanitization pipeline that records both raw and sanitized versions as separate events, where the sanitization action itself is an event, and where reading from the event store never exposes raw content without elevated access or causes rendering vulnerabilities,
so that the event store is secure by default, the full audit trail includes what was sanitized and why, and UI consumers can safely render event data.

## Acceptance Criteria

1. Every LLM call produces a chain of events: raw request stored → CONTENT_SANITIZED event → sanitized version stored → dispatched → raw response stored → CONTENT_SANITIZED event → sanitized version stored → completed
2. Raw content is stored in blob storage (not inline in event JSONB) with `classification: 'confidential'` and restricted access
3. Sanitized content is stored inline in the event payload — this is what all normal queries return
4. The CONTENT_SANITIZED event records: what was sanitized, how many items, what types (PII, API keys, scripts, prompt injection), and references both the raw blob and sanitized version
5. When reading events through the API, only sanitized content is returned by default
6. Raw content access requires explicit elevated permission (configurable role/token)
7. API responses apply output encoding at the boundary: HTML entities escaped in JSON string values for any field that could contain user/LLM content
8. Webhook payloads from external platforms go through the same sanitization pipeline before event store write
9. Existing `ContentSanitizer` (input and output) and `SecureAgentProvider` are integrated into the event pipeline — not duplicated
10. Event store read API sets `Content-Security-Policy: default-src 'none'` and `X-Content-Type-Options: nosniff` headers
11. Sanitization pipeline is async-safe — sanitizing content does not block event append (raw is stored first, sanitization event follows)
12. Audit trail: for any sanitized event, you can trace back to the raw version via `rawRef` field and the CONTENT_SANITIZED event via `causationId`

## Technical Context

### Sanitization Event Chain for LLM Call

```
Step 1: LLM_REQUEST_CREATED
  - payload.rawPromptRef → blob storage (confidential)
  - Trigger async sanitization

Step 2: CONTENT_SANITIZED (causation: Step 1)
  - payload.contentType: 'llm_prompt'
  - payload.rawRef: blob ID from Step 1
  - payload.sanitizedPromptRef: new blob ID (or inline if small)
  - payload.items: [{ type: 'api_key', action: 'redacted' }, ...]
  - payload.warnings: ['prompt_injection_pattern_detected']
  - payload.piiDetected: false

Step 3: LLM_CALL_DISPATCHED
  - payload.sanitizedPromptRef → references Step 2's sanitized version
  - This is what was actually sent to the LLM

Step 4: LLM_RESPONSE_RECEIVED
  - payload.rawResponseRef → blob storage (confidential)
  - Trigger async sanitization

Step 5: CONTENT_SANITIZED (causation: Step 4)
  - payload.contentType: 'llm_response'
  - payload.rawRef: blob ID from Step 4
  - payload.sanitizedResponseRef: new blob ID or inline
  - payload.items: [{ type: 'script', action: 'stripped' }, ...]
  - payload.scriptsStripped: 2
  - payload.piiRedacted: 1

Step 6: LLM_CALL_COMPLETED
  - payload.sanitizedResponseRef → references Step 5's sanitized version
  - This is what the engine and UI see
```

### Access Control for Raw Content

```typescript
interface IEventStoreReader {
  // Default: returns events with sanitized content only
  query(filter: EventFilter): Promise<TammaEvent[]>;

  // Elevated: includes raw blob references (requires permission)
  queryWithRawAccess(filter: EventFilter, accessToken: string): Promise<TammaEventWithRaw[]>;
}

interface TammaEventWithRaw extends TammaEvent {
  rawBlobs?: Array<{
    blobId: string;
    contentType: string;
    classification: string;
    // Content NOT included inline — must be fetched separately via blob API
  }>;
}
```

### Output Encoding at API Boundary

```typescript
// Applied to all API responses containing event data
function encodeEventForTransport(event: TammaEvent): TammaEvent {
  return deepMapStrings(event, (value, key) => {
    // Escape HTML entities in any string field that could contain external content
    if (isContentField(key)) {
      return escapeHtml(value);
    }
    return value;
  });
}

// Fields considered "content" (potentially containing external data):
const CONTENT_FIELDS = [
  'body', 'comment', 'title', 'description', 'message',
  'prompt', 'response', 'output', 'error', 'reasoning',
  'sanitizedPrompt', 'sanitizedResponse', 'commentBody',
];

function escapeHtml(str: string): string {
  return str
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#x27;');
}
```

### Security Headers on Event API

```typescript
// Applied to all /api/engine/events/* and /api/engine/history routes
fastify.addHook('onSend', (request, reply, payload, done) => {
  reply.header('Content-Security-Policy', "default-src 'none'");
  reply.header('X-Content-Type-Options', 'nosniff');
  reply.header('X-Frame-Options', 'DENY');
  reply.header('Cache-Control', 'no-store');
  done();
});
```

### Integration with Existing Security

The existing security stack is reused, not duplicated:

| Existing Component | How It's Used |
|-------------------|---------------|
| `ContentSanitizer.sanitize()` | Called for LLM input sanitization; produces CONTENT_SANITIZED event |
| `ContentSanitizer.sanitizeOutput()` | Called for LLM output sanitization; produces CONTENT_SANITIZED event |
| `SecureAgentProvider` | Wraps LLM providers; now also records sanitization events |
| `UrlValidator` | Validates URLs in webhook payloads; produces URL_BLOCKED events |
| `ActionGating` | Validates commands; produces ACTION_BLOCKED events |
| `sanitizeError()` | Sanitizes error messages for telemetry; integrated into error events |

## Tasks / Subtasks

- [ ] Task 1: Implement sanitization event pipeline for LLM calls (AC: 1, 3, 11)
  - [ ] Subtask 1.1: Create `SanitizationPipeline` class that wraps `ContentSanitizer`
  - [ ] Subtask 1.2: Implement pre-LLM flow: store raw blob → sanitize → record CONTENT_SANITIZED → return sanitized
  - [ ] Subtask 1.3: Implement post-LLM flow: store raw response blob → sanitize → record CONTENT_SANITIZED
  - [ ] Subtask 1.4: Ensure raw blob store does not block sanitization event (async pipeline)
  - [ ] Subtask 1.5: Wire into `SecureAgentProvider` so existing provider wrapping produces events

- [ ] Task 2: Implement raw content blob storage with access control (AC: 2, 6)
  - [ ] Subtask 2.1: Store raw LLM prompts/responses in blob storage with `classification: 'confidential'`
  - [ ] Subtask 2.2: Store raw webhook payloads in blob storage with `classification: 'internal'`
  - [ ] Subtask 2.3: Implement `queryWithRawAccess()` that requires elevated token
  - [ ] Subtask 2.4: Create blob API endpoint with authentication for raw content retrieval
  - [ ] Subtask 2.5: Configure retention policies (raw LLM: 30 days, webhooks: 90 days, security: 7 years)

- [ ] Task 3: Implement output encoding at API boundary (AC: 7, 10)
  - [ ] Subtask 3.1: Create `encodeEventForTransport()` function with HTML entity escaping
  - [ ] Subtask 3.2: Define CONTENT_FIELDS list for fields requiring encoding
  - [ ] Subtask 3.3: Wire encoding into all event-returning API routes
  - [ ] Subtask 3.4: Add security headers (CSP, X-Content-Type-Options, X-Frame-Options) to event routes
  - [ ] Subtask 3.5: Add `Cache-Control: no-store` to prevent sensitive event data caching

- [ ] Task 4: Implement webhook payload sanitization (AC: 8)
  - [ ] Subtask 4.1: Sanitize all webhook payloads through `ContentSanitizer` before event store write
  - [ ] Subtask 4.2: Record CONTENT_SANITIZED for webhook payloads that had content modified
  - [ ] Subtask 4.3: Detect and flag potential injection in issue/comment bodies
  - [ ] Subtask 4.4: URL validation on any URLs in webhook payloads

- [ ] Task 5: Implement audit trail traceability (AC: 4, 12)
  - [ ] Subtask 5.1: Ensure CONTENT_SANITIZED events have `causationId` pointing to triggering event
  - [ ] Subtask 5.2: Ensure `rawRef` in sanitization events points to blob storage
  - [ ] Subtask 5.3: Create utility function: given a sanitized event, trace back to raw and sanitization events
  - [ ] Subtask 5.4: Verify chain integrity: every sanitized content field has a corresponding CONTENT_SANITIZED event

- [ ] Task 6: Default-safe event queries (AC: 5)
  - [ ] Subtask 6.1: Default `query()` returns only sanitized content (no rawRef, no blob inline)
  - [ ] Subtask 6.2: Blob references stripped from default query results
  - [ ] Subtask 6.3: SSE event streams deliver sanitized-only content
  - [ ] Subtask 6.4: Dashboard API endpoints return sanitized-only content

- [ ] Task 7: Testing (AC: all)
  - [ ] Subtask 7.1: Unit test sanitization pipeline produces correct event chain
  - [ ] Subtask 7.2: Unit test output encoding escapes all HTML entities
  - [ ] Subtask 7.3: Unit test raw access requires elevated token
  - [ ] Subtask 7.4: Unit test default query strips raw references
  - [ ] Subtask 7.5: Integration test: LLM call → 6 events produced → UI reads sanitized only
  - [ ] Subtask 7.6: Security test: XSS payload in LLM response → stripped before event store → safe to render
  - [ ] Subtask 7.7: Security test: API key in LLM prompt → redacted in sanitized event → raw in blob with restricted access
  - [ ] Subtask 7.8: Security test: verify CSP and security headers present on all event routes

## Dev Notes

### Project Structure Notes

- New implementation: `packages/shared/src/security/sanitization-pipeline.ts`
- New implementation: `packages/api/src/middleware/event-output-encoding.ts`
- New implementation: `packages/api/src/middleware/security-headers.ts`
- Modified: `packages/providers/src/secure-agent-provider.ts` (emit sanitization events)
- Modified: `packages/api/src/routes/engine/index.ts` (add output encoding, security headers)
- Modified: `packages/api/src/routes/webhooks/` (sanitize payloads before processing)

### References

- **Epic 10 Tech Spec:** `docs/stories/epic-10/tech-spec-epic-10.md`
- **Existing ContentSanitizer:** `packages/shared/src/security/content-sanitizer.ts`
- **Existing SecureAgentProvider:** `packages/providers/src/secure-agent-provider.ts`
- **Existing UrlValidator:** `packages/shared/src/security/url-validator.ts`
- **Story 10.2:** Event catalog defines CONTENT_SANITIZED and security event types
- **Story 10.3:** Blob storage for raw content
- **Story 4.4:** `docs/stories/epic-4/story-4-4/` (AI provider event capture spec)

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-26 | 1.0 | Initial story creation | Architecture Team |
