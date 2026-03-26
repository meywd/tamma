# Story 10.6: Input Channel Unification (UI + Platform Events)

Status: ready-for-dev

## Story

As a **platform architect**,
I want all inputs — whether from a user typing in CLI, clicking in a web dashboard, or a GitHub/Gitea/GitLab webhook firing — normalized into a single `EngineIntakeEvent` format and processed through the same engine brain pipeline,
so that the engine has one entry point regardless of input source, and platform events (comments, assignments, approvals) are first-class triggers equal to user commands.

## Acceptance Criteria

1. All input channels produce `EngineIntakeEvent` (defined in Story 10.1) before reaching the engine brain
2. Existing transports (InProcessTransport, RemoteTransport) are adapted to produce normalized events instead of direct method calls
3. Webhook receivers exist for GitHub, Gitea, and GitLab — each normalizes platform-specific payloads to `NormalizedInput`
4. GitHub webhook events supported: `issues` (opened, assigned, commented), `pull_request` (opened, synchronize, review_submitted, merged, closed), `issue_comment`, `check_run`, `check_suite`
5. Gitea webhook events supported: equivalent issue, PR, and CI events
6. GitLab webhook events supported: equivalent issue, merge request, and pipeline events
7. Webhook signature verification implemented for all platforms (HMAC-SHA256 for GitHub/Gitea, token verification for GitLab)
8. Webhook payloads are sanitized (CONTENT_SANITIZED event) before processing — never pass raw external content to LLM or event store unsanitized
9. Platform events record WEBHOOK_RECEIVED event immediately on receipt (before processing)
10. Duplicate webhook detection based on delivery ID (GitHub `X-GitHub-Delivery`, etc.)
11. Rate limiting on webhook endpoints (configurable, default 100 req/min per source)
12. Each normalizer is independently testable with recorded webhook payloads

## Technical Context

### Normalization Architecture

```
GitHub Webhook ──► GitHubNormalizer ──┐
                                      │
Gitea Webhook  ──► GiteaNormalizer  ──┼──► EngineIntakeEvent ──► Engine Brain
                                      │
GitLab Webhook ──► GitLabNormalizer ──┤
                                      │
CLI Command    ──► CLINormalizer    ──┤
                                      │
Web UI Action  ──► WebNormalizer    ──┘
```

### Platform Event Normalization Examples

**GitHub issue comment → engine intake:**
```json
// GitHub webhook payload (abbreviated)
{
  "action": "created",
  "issue": { "number": 42, "title": "Fix login bug" },
  "comment": { "body": "@tamma approve", "user": { "login": "dev1" } }
}

// Normalized to:
{
  "source": "webhook",
  "channel": "github",
  "actor": { "type": "platform", "id": "github:dev1", "name": "dev1" },
  "payload": {
    "type": "platform_event",
    "event": "issue_comment",
    "data": {
      "action": "created",
      "issueNumber": 42,
      "issueTitle": "Fix login bug",
      "commentBody": "@tamma approve",
      "author": "dev1"
    }
  },
  "receivedAt": "2026-03-26T10:00:00.000Z"
}
```

**GitHub PR review approved → engine intake:**
```json
// Normalized to:
{
  "source": "webhook",
  "channel": "github",
  "actor": { "type": "platform", "id": "github:reviewer1", "name": "reviewer1" },
  "payload": {
    "type": "approval",
    "decision": "approve",
    "target": "pr:42"
  }
}
```

### Webhook Receiver Routes

```
POST /api/webhooks/github    → GitHubWebhookHandler → GitHubNormalizer
POST /api/webhooks/gitea     → GiteaWebhookHandler  → GiteaNormalizer
POST /api/webhooks/gitlab    → GitLabWebhookHandler  → GitLabNormalizer
```

### Normalizer Interface

```typescript
interface IInputNormalizer {
  platform: string;
  canNormalize(rawInput: unknown, headers: Record<string, string>): boolean;
  normalize(rawInput: unknown, headers: Record<string, string>): EngineIntakeEvent;
  verifySignature(rawBody: Buffer, headers: Record<string, string>, secret: string): boolean;
}
```

### Transport Adapter Pattern

The existing transports must be updated to produce `EngineIntakeEvent` instead of calling engine methods directly:

```typescript
// Before (current):
class InProcessTransport {
  async start() { await this.engine.run(); }
  async approve() { await this.engine.approve(); }
}

// After (new):
class InProcessTransport {
  async processCommand(command: string, args: Record<string, unknown>) {
    const intake: EngineIntakeEvent = {
      source: 'cli',
      channel: 'direct',
      actor: { type: 'user', id: this.userId, name: this.userName },
      payload: { type: 'command', command, args },
      receivedAt: new Date().toISOString(),
    };
    await this.engine.intake(intake);
  }
}
```

## Tasks / Subtasks

- [ ] Task 1: Define normalizer interfaces and types (AC: 1)
  - [ ] Subtask 1.1: Define `IInputNormalizer` interface
  - [ ] Subtask 1.2: Define normalizer registry for multi-platform support
  - [ ] Subtask 1.3: Define platform-specific webhook payload types (GitHub, Gitea, GitLab)
  - [ ] Subtask 1.4: Define mapping tables: platform event type → NormalizedInput type

- [ ] Task 2: Implement GitHub normalizer (AC: 4, 7, 8)
  - [ ] Subtask 2.1: Implement `GitHubNormalizer` for issue events (opened, assigned, commented)
  - [ ] Subtask 2.2: Implement normalization for PR events (opened, synchronize, review, merged, closed)
  - [ ] Subtask 2.3: Implement normalization for CI events (check_run, check_suite)
  - [ ] Subtask 2.4: Implement HMAC-SHA256 signature verification
  - [ ] Subtask 2.5: Detect bot mentions (@tamma) in comments and convert to commands

- [ ] Task 3: Implement Gitea normalizer (AC: 5, 7)
  - [ ] Subtask 3.1: Map Gitea webhook events to normalized format
  - [ ] Subtask 3.2: Implement Gitea HMAC signature verification
  - [ ] Subtask 3.3: Handle Gitea-specific fields and differences from GitHub

- [ ] Task 4: Implement GitLab normalizer (AC: 6, 7)
  - [ ] Subtask 4.1: Map GitLab webhook events (issues, merge requests, pipelines) to normalized format
  - [ ] Subtask 4.2: Implement GitLab token verification (X-Gitlab-Token)
  - [ ] Subtask 4.3: Handle GitLab-specific concepts (merge requests vs PRs)

- [ ] Task 5: Implement webhook receiver routes (AC: 9, 10, 11)
  - [ ] Subtask 5.1: Create Fastify routes for each platform webhook endpoint
  - [ ] Subtask 5.2: Verify signature before any processing
  - [ ] Subtask 5.3: Record WEBHOOK_RECEIVED event immediately on valid receipt
  - [ ] Subtask 5.4: Implement duplicate detection via delivery ID header
  - [ ] Subtask 5.5: Implement rate limiting per source IP/platform
  - [ ] Subtask 5.6: Sanitize webhook payload before passing to normalizer (Story 10.7)

- [ ] Task 6: Adapt existing transports (AC: 2)
  - [ ] Subtask 6.1: Update `InProcessTransport` to produce `EngineIntakeEvent`
  - [ ] Subtask 6.2: Update `RemoteTransport` to produce `EngineIntakeEvent`
  - [ ] Subtask 6.3: Update engine API routes to use intake pipeline
  - [ ] Subtask 6.4: Preserve backward compatibility for CLI client commands

- [ ] Task 7: Testing (AC: all)
  - [ ] Subtask 7.1: Unit test each normalizer with recorded webhook payloads
  - [ ] Subtask 7.2: Unit test signature verification (valid, invalid, missing)
  - [ ] Subtask 7.3: Unit test duplicate detection
  - [ ] Subtask 7.4: Unit test rate limiting
  - [ ] Subtask 7.5: Integration test: GitHub webhook → normalize → engine brain processes
  - [ ] Subtask 7.6: Test bot mention detection in comments (@tamma approve, @tamma status)
  - [ ] Subtask 7.7: Capture real webhook payloads from each platform for test fixtures

## Dev Notes

### Project Structure Notes

- New implementation: `packages/api/src/routes/webhooks/github.ts`
- New implementation: `packages/api/src/routes/webhooks/gitea.ts`
- New implementation: `packages/api/src/routes/webhooks/gitlab.ts`
- New implementation: `packages/shared/src/normalizers/github-normalizer.ts`
- New implementation: `packages/shared/src/normalizers/gitea-normalizer.ts`
- New implementation: `packages/shared/src/normalizers/gitlab-normalizer.ts`
- New implementation: `packages/shared/src/normalizers/cli-normalizer.ts`
- New implementation: `packages/shared/src/normalizers/web-normalizer.ts`
- Modified: `packages/orchestrator/src/transports/in-process.ts`
- Modified: `packages/orchestrator/src/transports/remote.ts`
- Modified: `packages/api/src/routes/engine/index.ts`

### References

- **Epic 10 Tech Spec:** `docs/stories/epic-10/tech-spec-epic-10.md`
- **Story 10.1:** Engine brain processes normalized intake events
- **Story 10.7:** Sanitization pipeline for webhook payloads
- **Existing Transports:** `packages/orchestrator/src/transports/`
- **GitHub Webhooks Docs:** https://docs.github.com/en/webhooks
- **Existing GitHub Platform:** `packages/platforms/src/github/`

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-26 | 1.0 | Initial story creation | Architecture Team |
