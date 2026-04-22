# Finding 014: No inbound rate limiting on `/api/github/webhooks` or `/api/auth/github`

**Scope**: github
**Severity**: P2 (correctness/observability)
**Status**: Not-yet-implemented (stub)
**Estimated port effort**: 2-3h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/github/github-webhook.ts` and `.../auth/github-oauth.ts`.

- File: `packages/api/src/routes/github/github-webhook.ts:66,78-81`; `packages/api/src/routes/auth/github-oauth.ts:54-57`
- Contract/behavior: Both routes registered `@fastify/rate-limit` with route-level config. Webhook: 300 requests/minute. OAuth start: 60 requests/minute. These were per-IP (default key) with a 429 response on breach.

Webhook:

```typescript
// packages/api/src/routes/github/github-webhook.ts:66,78-81 (9e9a57c~1)
// Register rate-limit plugin so route-level config takes effect
await app.register(rateLimit, { max: 300, timeWindow: '1 minute' });

// ...

app.post('/api/github/webhooks', {
  config: {
    rateLimit: { max: 300, timeWindow: '1 minute' },
  },
}, async (request, reply) => {
```

OAuth:

```typescript
// packages/api/src/routes/auth/github-oauth.ts:53-57 (9e9a57c~1)
// Rate limiting for auth routes
await app.register((await import('@fastify/rate-limit')).default, {
  max: 60,
  timeWindow: '1 minute',
});
```

Additionally `/api/auth/me` at line 231 had its own route-level `{ max: 60, timeWindow: '1 minute' }` config.

- Dependencies: `@fastify/rate-limit` plugin; defaults to in-memory counters (Redis backend optional).
- Tests that exercised this: `/auth` tests included a "exceeds rate limit returns 429" assertion.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Program.cs` (lookup for middleware registration) and both endpoint files.
- Contract/behavior: No ASP.NET Core rate-limiting middleware is registered. No `app.UseRateLimiter()`, no `AddRateLimiter(...)`, no `EnableRateLimiting` attribute, no policy definitions. The webhook and OAuth endpoints accept unbounded request rates from any origin.

Grep the repository for `AddRateLimiter`, `UseRateLimiter`, `RateLimiterOptions` → zero hits in `apps/tamma-elsa/src/`. The only rate-limit reference in the solution is the one in `ApiKeyRotationService.cs` about outbound GitHub rate limits, and the one in this audit's finding files.

- Dependencies: none wired. ASP.NET Core 8+ ships `Microsoft.AspNetCore.RateLimiting` — no additional NuGet needed, just DI + middleware config.
- Tests: none.

## 3. The gap

- TS did: enforce 300 req/min on webhook, 60 req/min on OAuth routes; respond with 429 after breach.
- C# does: accept unbounded requests on all GitHub-facing public routes.
- For a caller spamming `POST /api/github/webhooks` at 10,000 req/sec with valid but throwaway signatures (or, after Finding 001 is fixed, with invalid signatures), TS rate-limited to 5 req/sec per IP and returned 429; C# happily processes every request, including signature verification (CPU), body parsing, and DB lookups.
- In production with existing data / deployed clients, this means:
  - **DoS surface**: an attacker who learns a tenant's webhook URL (trivial — it's `https://api.tamma.dev/api/github/webhooks`) can flood it. Each request requires HMAC compute + body parse + DB lookup. Concurrent flood → Postgres pool exhaustion → legitimate requests timeout.
  - **Invalid-signature floods are not constrained**: even with Finding 001 fixed, an attacker who sends 1000 req/sec of `X-Hub-Signature-256: sha256=garbage` causes HMAC compute per request. HMAC is cheap individually but 1000/sec aggregates to noticeable CPU.
  - **OAuth-start flood**: an attacker who spams `GET /api/auth/github` can exhaust any outbound rate we may have (once Finding 009 adds state cookies, each start allocates a random value and a Set-Cookie response — modest but DoS-able).
  - **No 429 observability**: without rate limiting, the metric "clients exceeding reasonable rate" isn't visible. Abuse is invisible until Postgres starts complaining.

Error paths:
- TS error path: 429 response with `Retry-After` header per `@fastify/rate-limit` defaults.
- C# error path: none — floods reach the handler.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md`
- Story's acceptance criteria for this behavior: not explicit in story 18-4. Rate limiting is cross-cutting. Story 18-2 (`18-2-user-login-session-management.md`) mentions lockout (per-email failed-login counter) which is the application-layer cousin of rate limiting, but does not require HTTP-layer rate limiting.
- Story alignment:
  - [ ] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [x] No story — spec gap; must be backfilled before remediation

CLAUDE.md `Security Requirements` is the governing doc — but it does not explicitly mandate rate-limiting either. Add this to a cross-cutting story for platform hardening.

## 5. Status

- **Classification**: Not-yet-implemented (stub). Available in-framework, just not wired.
- **What's needed to finish**:
  1. In `Program.cs`: `builder.Services.AddRateLimiter(options => { ... })` with named policies:
     - `github-webhook`: fixed window 300/min, partitioned by client IP (`PartitionedRateLimiter.Create<HttpContext, IPAddress>`). Consider partitioning by `X-GitHub-Hook-Installation-Target-ID` header if GitHub sends it (check docs) for per-installation fairness.
     - `oauth`: fixed window 60/min, partitioned by IP.
  2. `app.UseRateLimiter()` early in the pipeline.
  3. Apply: `github.MapPost("/webhooks", ...).RequireRateLimiting("github-webhook");` and `app.MapGet("/api/auth/github", ...).RequireRateLimiting("oauth");`.
  4. Configure 429 response body shape to match clients' expectations: ASP.NET emits 429 with `Retry-After` by default; that matches what Fastify did.
  5. Expose rate-limit metrics via OpenTelemetry for observability.
- **Is it "just a stub" or is scope missing?** Stub — the capability is built into ASP.NET, just not registered.
- **Blockers**: None standalone. Ordering: should land alongside Finding 017 (which is the same concern framed from a different angle — webhook route has no rate-limit plugin equivalent).

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Program.cs` — add `AddRateLimiter`, `UseRateLimiter`, apply policies to routes (lines 334, 335, 467, 468).
- Files to create:
  - Optional: `apps/tamma-elsa/src/Tamma.Api/Extensions/RateLimitingServiceCollectionExtensions.cs` to centralize the policy definitions.
- Tests to add:
  - `GitHubEndpointsIntegrationTests.Webhook_ExceedsLimit_Returns429` — post 301 requests in under a minute, assert the 301st receives 429 with `Retry-After`.
  - `AuthEndpointsTests.GitHubAuth_ExceedsLimit_Returns429`
  - `AuthEndpointsTests.GitHubAuth_NormalTraffic_Succeeds`
- Estimated effort: 2-3h broken down as:
  - Middleware registration + two policies: 1h
  - Apply to routes + verify: 0.5h
  - Integration tests (3 cases): 1-1.5h

## References

- TS source: `packages/api/src/routes/github/github-webhook.ts:66,78-81`; `packages/api/src/routes/auth/github-oauth.ts:54-57` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Program.cs` (no rate-limit registration anywhere)
- Story: no story exists — spec gap
- Related findings: `015-outbound-github-rate-limit-unhandled.md` (outbound), `017-webhook-route-no-rate-limit-plugin.md` (same surface, different angle)
- ASP.NET docs: [Rate limiting middleware](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit)

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: `6dead62`
- **Notes**: Added two named ASP.NET Core RateLimiter policies in `Program.cs`: `GitHubWebhook` (300/min) and `OAuthStart` (60/min), both fixed-window. Bound to:
  - `POST /api/github/webhooks` → `GitHubWebhook`
  - `GET  /api/github/callback` → `OAuthStart`
  - `GET  /api/auth/github` → `OAuthStart`
  - `GET  /api/auth/github/callback` → `OAuthStart`
  Per-IP partitioning is the framework default; 429 returned with `RejectionStatusCode = 429`.
