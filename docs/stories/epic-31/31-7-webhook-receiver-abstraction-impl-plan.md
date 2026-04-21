# Story 31-7 Implementation Plan — Webhook Receiver Abstraction

**Status**: Planned (2026-04-21)
**Story brief**: [`31-7-webhook-receiver-abstraction.md`](./31-7-webhook-receiver-abstraction.md)
**Epic 31 phase**: Layer 4 — serial after 31-3/31-4; parallel-ok with
31-8.
**Branch**: `feat/story-31-7-webhook-receiver-abstraction`

---

## 1. Objective

Generalise the webhook receiver off of GitHub-specific hard-coding.
Ship (1) per-platform paths `/api/webhooks/{platform}` with
`PlatformKind.{GitHub,Gitea,Forgejo,GitLab}` routing; (2) the
`IWebhookSignatureVerifier` interface with four concrete impls (HMAC
for GitHub/Gitea/Forgejo, static-token for GitLab); (3) platform-
scoped idempotency table `platform_webhook_deliveries` generalising
`github_webhook_deliveries`; (4) a neutral `IWebhookEventDispatcher`
with handler registration so the install-linking logic moves from a
monolith into per-event handlers. Audit invariant preserved: missing
secret → fail-closed 503.

## 2. Dependencies

Hard blockers:

- **Story 31-1** — abstraction + `IWebhookSignatureVerifier`
  interface in the abstraction project.
- **Story 31-2** — resolver for tenant enrichment.
- **Story 31-3 / 31-4 / 31-6** — drivers provide concrete verifier
  implementations per platform.
- **Audit finding 001** (GitHub webhook fail-closed invariant) —
  maintained via the new verifier contract.

Soft:

- **Story 31-5** (Forgejo) — verifier uses the Gitea impl configured
  with the Forgejo header list; no new impl in 31-7.

Blocks: **31-9** (onboarding UI reads the webhook-registration
callbacks).

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.Abstractions/IWebhookSignatureVerifier.cs` | Interface: `Task<WebhookVerificationResult> VerifyAsync(ReadOnlyMemory<byte> body, string secret, Func<string, string?> getHeader, CancellationToken ct)`. |
| `.../WebhookVerificationResult.cs` | `abstract record { Ok, BadSignature, MissingHeader, ServiceUnavailable }`. |
| `.../IWebhookHandler.cs` | Neutral handler contract: `Task HandleAsync(PlatformWebhookEvent evt, CancellationToken ct)`. |
| `.../PlatformWebhookEvent.cs` | `sealed record { PlatformKind, EventType, DeliveryId, RawBody, ParsedJson (JsonDocument), InstallationExternalId, RepoFullName, TenantId? }`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/WebhookEndpoints.cs` | New `POST /api/webhooks/{platform}` handler + legacy alias `POST /api/github/webhooks` → 301 redirect. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Webhooks/IWebhookEventDispatcher.cs` | Dispatcher interface. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Webhooks/WebhookEventDispatcher.cs` | Impl: handler registry + per-event dispatch; fire-and-forget with isolation. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Webhooks/WebhookHandlerRegistration.cs` | Fluent builder: `.RegisterHandler(PlatformKind, "installation.*", handler)`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Webhooks/GitHubInstallationCreatedHandler.cs` | Ported from the monolithic handler; implements `IWebhookHandler`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.GitHub/GitHubWebhookHmacVerifier.cs` | HMAC-SHA256 on `X-Hub-Signature-256`. Ported from the existing logic. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Entities/PlatformWebhookDelivery.cs` | EF entity. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Migrations/20260510000000_PlatformWebhookDeliveries.cs` | Migration: new table; backfill from `github_webhook_deliveries`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Repositories/IPlatformWebhookDeliveryRepository.cs` | `TryRecordAsync(PlatformKind, deliveryId, eventType, installationExternalId)` returning bool (idempotency). |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Repositories/PlatformWebhookDeliveryRepository.cs` | Impl. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Webhooks/WebhookRateLimitKeys.cs` | Key scheme for `webhook:{platform}:{ip}`. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/Endpoints/WebhookEndpointsTests.cs` | Per-platform happy path, missing signature 401, duplicate delivery 200-no-redispatch, legacy alias redirect, 429 over rate limit. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Platforms.{GitHub,Gitea,GitLab}.Tests/*WebhookVerifierTests.cs` | Per-verifier unit tests (new tests in respective driver test projects). |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/GitHubEndpoints.cs` | Strip the `Webhooks` handler body; leave a 301 redirect for the legacy path; mark handler `[Obsolete]`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Program.cs` | Register verifiers (keyed by `PlatformKind`), dispatcher (singleton), handlers, idempotency repo, rate-limit keys. Route `POST /api/webhooks/{platform}`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Repositories/GitHubWebhookDeliveryRepository.cs` | Deprecate in favor of platform-scoped repo. Keep for 30-day deprecation window. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.Gitea/GiteaWebhookSignatureVerifier.cs` | Moved/renamed to implement `IWebhookSignatureVerifier` from abstraction. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.GitLab/GitLabWebhookTokenVerifier.cs` | Moved/renamed to implement `IWebhookSignatureVerifier`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Logging/LogSanitizer.cs` | Ensure `Clean(string)` handles webhook payloads (tokens, secrets). |

## 5. Sequence of changes

### Step 1 — Abstraction interfaces + neutral event record (2h)

- `IWebhookSignatureVerifier`, `WebhookVerificationResult`,
  `IWebhookHandler`, `PlatformWebhookEvent` in
  `Tamma.Platforms.Abstractions`.
- Unit tests for record construction.
- **Commit**: `feat(platforms): webhook verifier + handler contracts`.

### Step 2 — GitHub HMAC verifier port (2h)

- `GitHubWebhookHmacVerifier` in `Tamma.Platforms.GitHub/`:
  - Reads `X-Hub-Signature-256`.
  - HMAC-SHA256 over body bytes.
  - Constant-time compare via
    `CryptographicOperations.FixedTimeEquals`.
  - Missing secret → `ServiceUnavailable` (fail-closed per audit
    finding 001).
- Unit tests (ports existing tests from `GitHubEndpoints.Webhooks`).
- **Commit**: `feat(platforms.github): webhook HMAC verifier`.

### Step 3 — Gitea/Forgejo/GitLab verifier interface conformance (1h)

- Existing verifiers from 31-4 (Gitea), 31-5 (Forgejo),
  31-6 (GitLab) already implement the HMAC/static-token logic.
  This step adjusts their signature/naming to implement the
  interface shipped in Step 1.
- **Commit**: `refactor(platforms): verifiers implement abstract contract`.

### Step 4 — Migration: `platform_webhook_deliveries` table (3h)

- `PlatformWebhookDelivery` entity + migration:
  - Columns: `id UUID PK`, `platform_kind TEXT NOT NULL`,
    `delivery_id TEXT NOT NULL`, `event_type TEXT`,
    `installation_external_id TEXT`, `received_at TIMESTAMPTZ`.
  - Unique `(platform_kind, delivery_id)`.
  - Backfill: `INSERT INTO platform_webhook_deliveries (id, platform_kind,
    delivery_id, event_type, installation_external_id, received_at)
    SELECT id, 'github', delivery_id, event_type, installation_id,
    received_at FROM github_webhook_deliveries;`
- Old table kept; 30-day deprecation window documented in
  migration comment.
- Integration test: idempotency record survives duplicate POST.
- **Commit**: `feat(data): platform_webhook_deliveries migration`.

### Step 5 — Delivery repository (2h)

- `TryRecordAsync(kind, deliveryId, eventType, installationExternalId)`:
  - Single-statement `INSERT … ON CONFLICT DO NOTHING RETURNING id`.
  - Returns `true` if inserted, `false` if duplicate.
- Unit test (Postgres testcontainer).
- **Commit**: `feat(data): platform webhook delivery repository`.

### Step 6 — Dispatcher + handler registration (3h)

- `WebhookEventDispatcher`:
  - In-memory handler list, keyed by `(PlatformKind, eventTypePattern)`.
  - `RegisterHandler(kind, pattern, handler)` — pattern supports
    exact match + wildcard (`installation.*`).
  - `DispatchAsync(evt)` — enumerate matching handlers; invoke each
    inside `Task.Run` + `try/catch` per handler; log failures;
    emit `PLATFORM.WEBHOOK.HANDLER_FAILED`; do not re-throw.
- Matcher: compiled `Regex` derived from pattern (replace `*` with
  `.*`, anchor `^$`).
- Unit tests:
  - Handler bound to `installation.*` receives both
    `installation.created` and `installation.deleted`.
  - Handler failure isolated from siblings.
  - Wrong platform not dispatched.
- **Commit**: `feat(api): webhook dispatcher + handler registration`.

### Step 7 — Port GitHub install-linking handler (2h)

- `GitHubInstallationCreatedHandler : IWebhookHandler`:
  - On `evt.EventType == "installation.created"`, parse
    `evt.ParsedJson` for `installation.id` + `state` (OAuth state
    param) and link to tenant via `InstallationRepository.LinkToTenantAsync`.
  - Same logic lifted from the monolithic `GitHubEndpoints.Webhooks`.
- Remaining GitHub event types (push, pr.opened, etc.) get stub
  handlers that emit `PLATFORM.WEBHOOK.RECEIVED.SUCCESS` + TODO
  handler linkage — follow-up stories.
- **Commit**: `feat(api): port GitHub installation-created handler`.

### Step 8 — `WebhookEndpoints` + legacy alias (3h)

- Route `POST /api/webhooks/{platform}`:
  1. Parse `{platform}` → `PlatformKind`; 400 on unknown.
  2. Resolve verifier via `IKeyedServiceProvider.GetRequiredKeyedService<IWebhookSignatureVerifier>(kind)`.
  3. Read body bytes (capped at 10MB — configurable).
  4. `VerifyAsync(body, secret, getHeader)`:
     - `Ok` → continue.
     - `BadSignature` / `MissingHeader` → 401 + rate-limit key bump.
     - `ServiceUnavailable` → 503 (fail-closed).
  5. Parse JSON; 400 on invalid.
  6. Resolve tenantId via `IPlatformResolver.ResolveForWebhookAsync(installationExternalId)`.
  7. `TryRecordAsync(kind, deliveryId, …)` → if false (duplicate),
     return 200 without dispatching.
  8. Build `PlatformWebhookEvent` + `_dispatcher.DispatchAsync(evt)`
     fire-and-forget.
  9. Return 200.
- Legacy alias `POST /api/github/webhooks` → 301 redirect to
  `/api/webhooks/github` with header `Deprecation: true` +
  `Sunset: <30-days-out>`.
- Rate limit keyed by `webhook:{platform}:{ip}` — 60/min, 429 with
  `Retry-After` on exceed.
- **Commit**: `feat(api): WebhookEndpoints + legacy alias`.

### Step 9 — Log sanitization + idempotency guard (1h)

- In each webhook handler log statement, apply `LogSanitizer.Clean(...)`
  to any string that might contain token/email.
- Never log raw body; only `{platformKind, eventType, deliveryId,
  installationExternalId}`.
- Unit test: feed a body with `"token": "secret"`; assert logs do
  not contain `"secret"`.
- **Commit**: `feat(api): webhook log sanitization`.

### Step 10 — DI wiring (1h)

- `Program.cs`:
  - `services.AddKeyedSingleton<IWebhookSignatureVerifier, GitHubWebhookHmacVerifier>(PlatformKind.GitHub);`
  - `services.AddKeyedSingleton<…>(PlatformKind.Gitea)` etc.
  - `services.AddSingleton<IWebhookEventDispatcher, WebhookEventDispatcher>();`
  - `services.AddScoped<IWebhookHandler, GitHubInstallationCreatedHandler>();`
  - Startup call: dispatcher reads every registered `IWebhookHandler`
    via `Attribute` (e.g. `[HandlesWebhook(PlatformKind.GitHub,
    "installation.*")]`) and auto-registers.
- **Commit**: `feat(api): webhook DI wiring`.

### Step 11 — Tests: integration per platform (3h)

- For each platform (GitHub, Gitea, Forgejo, GitLab): POST signed
  fixture payload. Assert:
  - 200 + dispatcher received correct event.
  - Duplicate POST returns 200 without re-dispatch.
  - Bad signature returns 401.
  - Missing-secret config returns 503.
  - Legacy `POST /api/github/webhooks` redirects with deprecation
    headers.
  - 429 after 61 requests from same IP in a minute.
- **Commit**: `test(webhooks): per-platform integration`.

## 6. Test strategy

### Unit

- Each verifier: valid, wrong signature, missing header, missing secret.
- Dispatcher: pattern matching (exact + wildcard), handler failure
  isolation, wrong-platform exclusion.
- Idempotency repo: duplicate detection.

### Integration

- Per-platform signed-fixture round trip through the endpoint.
- Legacy alias redirect.
- Rate limiting.

### Security

- Audit finding 001 invariant: missing secret → 503, not 200.
- Startup assertion: a verifier registered under `PlatformKind.Gitea`
  **must** have a secret configured or startup fails with
  `InvalidOperationException`. Test.
- Log-sanitization: no raw body + no secret substrings leak.

## 7. Rollback plan

- **Revert commits**: restore `GitHubEndpoints.Webhooks` monolithic
  handler; remove new endpoint; drop `platform_webhook_deliveries`
  table (data loss minimal: all rows are idempotency records, low
  value).
- **Migration rollback**: EF `Down()` drops the new table. Old
  `github_webhook_deliveries` remains intact. No data loss.
- **Deprecation alias**: 301 redirect can remain indefinitely if
  the new endpoint is rolled back — it just points at a 404 until
  the old handler is restored.
- **Non-reversible**: none.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Abstraction interfaces | 2 |
| 2. GitHub HMAC verifier port | 2 |
| 3. Gitea/Forgejo/GitLab verifier conformance | 1 |
| 4. Migration | 3 |
| 5. Delivery repository | 2 |
| 6. Dispatcher + registration | 3 |
| 7. GitHub installation-created handler | 2 |
| 8. Endpoint + legacy alias | 3 |
| 9. Log sanitization | 1 |
| 10. DI wiring | 1 |
| 11. Integration tests | 3 |
| **Total** | **23** (brief: 18 — variance: idempotency migration + fail-closed assertions add work). |

## 9. Open questions

- **Startup fail-closed for missing secrets**: brief §technical-context
  says operator must configure a secret per registered platform else
  platform's path returns 503. Plan: startup **does not** fail — it
  logs a warning and the platform's path returns 503 at request
  time. Rationale: allow staged rollout where Gitea support registers
  but an operator hasn't yet entered the Gitea webhook secret.
  Document decision.
- **Handler attribute vs explicit registration**: auto-register
  via `[HandlesWebhook]` attribute is clean but reflection-heavy.
  Plan: start with explicit registration in `Program.cs`; migrate
  to attribute if the list grows large. Attribute support is a
  follow-up.
- **Legacy alias 301 vs 308**: 301 does not preserve POST
  semantics; 308 does. Plan: use 308 (preserves POST + body).
  Document as a correction to the brief.
- **JSON body size cap**: 10MB is arbitrary. GitHub ships webhooks
  <=25MB. Plan: configurable `Webhooks:MaxBodyBytes` default 25MB;
  reject >25MB with 413.
- **Dispatcher threading**: `Task.Run` inside request loop can
  saturate the thread pool under webhook storms. Plan: use
  `Channel<PlatformWebhookEvent>` + background processor for
  handlers; endpoint returns 200 as soon as event is queued. Failure
  path for queue-full: log + 503. Document in dispatcher class.
- **Tenant enrichment race**: a webhook arrives before the onboarding
  UI finishes connecting (brief edge case for 31-9). Plan: if
  `ResolveForWebhookAsync` returns null, set `evt.TenantId = null`
  and dispatch anyway. Handlers that need tenantId handle null
  gracefully (e.g. `GitHubInstallationCreatedHandler` sets the
  tenantId when it processes the link).
- **Retention policy for delivery table**: idempotency rows grow
  forever. Plan: nightly pruner drops rows older than 30 days.
  Add scheduled job in a follow-up; current implementation does
  not prune.
