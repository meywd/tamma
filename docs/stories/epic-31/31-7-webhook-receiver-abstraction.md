# Story 31-7: Webhook receiver abstraction — per-platform signature + routing

Status: todo (planning brief, 2026-04-21)

## Story

As a **git platform (GitHub, Gitea, Forgejo, GitLab) delivering a
webhook to Tamma**,
I want Tamma to accept my delivery at a platform-specific URL, verify
my signature in my platform's native format, record idempotency, and
dispatch to the right agent-dispatch / onboarding handler,
so that cross-platform inbound events land reliably without the
single-shape HMAC hard-coding the current `GitHubEndpoints.Webhooks`
handler carries.

## Narrative

The current webhook endpoint at `/api/github/webhooks` is
GitHub-specific: hard-coded `X-Hub-Signature-256` header + HMAC-SHA256
with the GitHub webhook secret, with a fail-closed policy when the
secret is missing (audit finding 001).

31-7 generalises:

- Per-platform path: `/api/webhooks/github`, `/api/webhooks/gitea`,
  `/api/webhooks/forgejo`, `/api/webhooks/gitlab`. (The existing
  `/api/github/webhooks` path stays aliased for a deprecation
  window.)
- Signature verification per platform (HMAC-SHA256 for
  GitHub/Gitea/Forgejo/Bitbucket; static-token compare for GitLab;
  Entra-signed for Azure DevOps in the optional 31-12).
- Idempotency across platforms — the existing
  `GitHubWebhookDeliveryRepository` generalises to
  `PlatformWebhookDeliveryRepository` keyed by `(platformKind,
  deliveryId)`.
- Dispatch — normalised `PlatformWebhookEvent` record fed into a
  platform-agnostic event bus. Downstream handlers (install-linking,
  installation-created, repository-selection) subscribe to neutral
  event types.

## Acceptance Criteria

1. New interface `IWebhookSignatureVerifier` with impls per
   platform:
   - `GitHubWebhookHmacVerifier` — existing logic ported.
   - `GiteaWebhookHmacVerifier` — HMAC-SHA256 reading
     `X-Gitea-Signature`.
   - `ForgejoWebhookHmacVerifier` — HMAC-SHA256 reading
     `X-Forgejo-Signature`, falling back to `X-Gitea-Signature`.
   - `GitLabWebhookTokenVerifier` — static token compare on
     `X-Gitlab-Token`.
   Registered in DI keyed by `PlatformKind`.
2. Endpoint registration at `Program.cs`:
   - `POST /api/webhooks/{platform}` where `{platform}` ∈
     `github | gitea | forgejo | gitlab`.
   - Legacy alias `POST /api/github/webhooks` → 301 redirect to
     `/api/webhooks/github` with a deprecation header. Kept for 30
     days then removed.
3. Handler pipeline:
   1. Parse `{platform}` path param → resolve `PlatformKind` or 400.
   2. Resolve the right `IWebhookSignatureVerifier` via keyed DI.
   3. Verifier rejects on missing / invalid signature — 401.
     Fail-closed if secret not configured (audit finding 001
     invariant preserved).
   4. Parse body as JSON. Short-circuit on invalid JSON with 400.
   5. Idempotency: `PlatformWebhookDeliveryRepository.TryRecordAsync(platformKind,
     deliveryId, eventType, installationExternalId)` — duplicates
     return 200 without dispatching.
   6. Build `PlatformWebhookEvent` record
     `{ platformKind, eventType, deliveryId, rawBody, parsedJson,
     installationExternalId, repoFullName, tenantId? }` — enrich
     `tenantId` via `IPlatformResolver` when the installation is
     known.
   7. Publish to `IWebhookEventDispatcher` which routes to
     registered handlers.
4. New table `platform_webhook_deliveries` (migration) replaces or
   supersets the existing `github_webhook_deliveries`:
   `id UUID PK`, `platform_kind TEXT NOT NULL`, `delivery_id TEXT
   NOT NULL`, `event_type TEXT`, `installation_external_id TEXT`,
   `received_at TIMESTAMPTZ`.
   Uniqueness: `(platform_kind, delivery_id)`. Existing GitHub rows
   migrate with `platform_kind='github'`.
5. `IWebhookEventDispatcher` with handler registration:
   - `RegisterHandler(PlatformKind, eventTypePattern, IWebhookHandler)`.
   - Pattern supports exact match + wildcard (`installation.*`).
   - Multiple handlers can bind to the same event; dispatcher
     invokes all; handler failures are isolated (logged, not
     re-thrown).
6. Existing GitHub install-callback handler ports to the new
   dispatcher — the install-linking logic moves from the monolithic
   webhook handler into a `GitHubInstallationCreatedHandler`
   implementing `IWebhookHandler`. Gitea + Forgejo + GitLab ship
   with stub handlers that emit `PLATFORM.WEBHOOK.RECEIVED.SUCCESS`
   events and a TODO for onboarding linkage (the onboarding flow
   31-9 lands the real handlers).
7. Log sanitization — webhook body may contain secrets (tokens,
   email addresses); `LogSanitizer.Clean(...)` applied to every
   logged string. Never log the raw body — only `{platformKind,
   eventType, deliveryId, installationExternalId}`.
8. Rate limiting — `IRateLimitService` keyed by source IP per
   platform (`webhook:{platform}:{ip}`). 60 per minute per IP by
   default; configurable. 429 with `Retry-After`.
9. Unit tests:
   - Each verifier accepts its valid signature, rejects mismatches,
     rejects when secret is missing.
   - Dispatcher routes correctly by `PlatformKind` + event type
     pattern.
   - Idempotency table returns 200 on duplicate without dispatching.
   - Legacy `/api/github/webhooks` path redirects to
     `/api/webhooks/github` with deprecation header.
10. Integration test: end-to-end flow per platform. POST a
    signed fixture payload (per-platform). Assert 200 + dispatcher
    received correct event + duplicate POST returns 200 but doesn't
    re-dispatch.

## Technical Context

### Why not route by header sniffing

Two reasons: (1) GitHub + Gitea use different-named headers but
similar payload shapes — a header sniff is fragile. (2) Path-based
routing makes per-platform rate limiting and per-platform URL
whitelists in nginx simpler. Path is the cleanest signal.

### Fail-closed invariant

Audit finding 001 (the one that flipped the GitHub path to reject on
missing secret) applies to every new verifier. The DI factory asserts
at startup that a secret is configured for every registered
platform; if not, the platform's webhook path returns 503 until
configured. No silent fail-open.

### Dispatcher threading

Fire-and-forget dispatch inside a `Task.Run` on the thread pool —
same pattern as the email send in `OrgEndpoints.CreateInvite`. A
failing handler logs + emits `PLATFORM.WEBHOOK.HANDLER_FAILED` but
does not fail the 200 response to the sender (webhook senders
re-deliver on 5xx; a handler bug shouldn't trigger re-delivery
storms).

## Dependencies

- **31-1** — abstraction
- **31-2** — resolver (for tenant enrichment)
- **31-3 / 31-4 / 31-6** — drivers (for verifier impls)
- Blocks 31-9 (onboarding UI uses webhook-registration callbacks)

## Estimated hours

**18h**

| Task | Hours |
|---|---|
| `IWebhookSignatureVerifier` + four impls | 4 |
| Endpoint + routing + legacy alias | 3 |
| Delivery table migration + repo | 3 |
| Dispatcher + handler registration | 3 |
| Log sanitization + rate limit | 2 |
| Tests | 3 |

## Files touched

- `apps/tamma-elsa/src/Tamma.Api/Endpoints/WebhookEndpoints.cs` (new)
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/GitHubEndpoints.cs` (deprecate webhook handler; keep alias)
- `apps/tamma-elsa/src/Tamma.Platforms.Abstractions/IWebhookSignatureVerifier.cs` (new)
- `apps/tamma-elsa/src/Tamma.Platforms.{GitHub,Gitea,GitLab}/*WebhookVerifier.cs` (new in each driver)
- `apps/tamma-elsa/src/Tamma.Data/Migrations/*_PlatformWebhookDeliveries.cs` (new)
- `apps/tamma-elsa/src/Tamma.Data/Repositories/PlatformWebhookDeliveryRepository.cs` (new)
- `apps/tamma-elsa/tests/Tamma.Api.Tests/Endpoints/WebhookEndpointsTests.cs` (new)

## References

- Research notes: [`../research/multi-git-platform-2026.md`](../research/multi-git-platform-2026.md) §1, §2, §3
- Existing webhook handler: `apps/tamma-elsa/src/Tamma.Api/Endpoints/GitHubEndpoints.cs` `Webhooks` method
