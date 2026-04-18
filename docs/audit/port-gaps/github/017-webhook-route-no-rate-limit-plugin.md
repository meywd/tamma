# Finding 017: Webhook route has no per-route rate-limit binding equivalent

**Scope**: github
**Severity**: P2 (correctness/observability)
**Status**: Not-yet-implemented (stub) — overlaps with Finding 014
**Estimated port effort**: 1h (on top of Finding 014's setup)

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/github/github-webhook.ts`.

- File: `packages/api/src/routes/github/github-webhook.ts:65-82`
- Contract/behavior: TS registered the `@fastify/rate-limit` plugin scoped to the webhook route and attached a per-route configuration object. The `{max: 300, timeWindow: '1 minute'}` value was specified twice — once at plugin registration (the global default within this plugin scope) and again at route registration (the route-level override). This dual specification is idiomatic Fastify: the plugin registration makes the middleware available, the route config enables it for the specific handler.

```typescript
// packages/api/src/routes/github/github-webhook.ts:65-82 (9e9a57c~1)
  // Register rate-limit plugin so route-level config takes effect
  await app.register(rateLimit, { max: 300, timeWindow: '1 minute' });

  // Fastify needs raw body for signature verification.
  // We register a content-type parser to capture raw body.
  app.addContentTypeParser(
    'application/json',
    { parseAs: 'string' },
    (_req, body, done) => {
      done(null, body);
    },
  );

  app.post('/api/github/webhooks', {
    config: {
      rateLimit: { max: 300, timeWindow: '1 minute' },
    },
  }, async (request, reply) => {
```

- Dependencies: `@fastify/rate-limit` (default: in-memory per-IP counter with exponential backoff on repeated breach).
- Tests that exercised this: webhook integration tests asserted 429 response on rate breach; asserted headers `x-ratelimit-limit: 300`, `x-ratelimit-remaining: N`.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Program.cs:467-468`
- Contract/behavior: The webhook route is registered with no rate-limit policy. This finding is the per-route angle on Finding 014 (which covers the middleware as a whole).

```csharp
// apps/tamma-elsa/src/Tamma.Api/Program.cs:465-468 (current)
// ── GitHub App (no auth, webhook signature verification) ──
var github = app.MapGroup("/api/github");
github.MapGet("/callback", GitHubEndpoints.Callback);
github.MapPost("/webhooks", GitHubEndpoints.Webhooks);
```

No `.RequireRateLimiting("policy-name")`. No `EnableRateLimiting` attribute on the endpoint method. Nothing in the `MapGroup("/api/github")` declaration binds a policy.

- Dependencies: none at the per-route binding level.
- Tests: none.

## 3. The gap

This finding is a restatement of Finding 014 focused specifically on the webhook route's per-route enforcement, for two reasons:

1. The TS code specified the rate-limit config at **two layers** — plugin-level and route-level. A faithful port must do both (global middleware + per-route policy), not just one.
2. Webhooks and OAuth have **different** allowed rates (300/min vs. 60/min). If we only wired a global rate-limiter (per Finding 014's broad solution), we'd have to pick one rate for all. The correct solution is named policies attached per-route.

- TS did: route-level rate-limit config of `{max: 300, timeWindow: '1 minute'}` on `POST /api/github/webhooks`.
- C# does: no route-level binding. Without Finding 014's middleware, there's no enforcement. Even with Finding 014's middleware, without the `.RequireRateLimiting("github-webhook")` call, the specific policy for this route isn't active.
- For a caller sending 500 webhook POSTs/min from a single IP, TS returned 429 after the 301st request (per the 300/min budget). C# accepts all 500.
- In production with existing data / deployed clients: see Finding 014's impact section. This finding's specific concern is that even after Finding 014 is addressed, we must remember to attach the policy here or the webhook route remains unguarded.

Error paths:
- TS error path: 429 with `Retry-After`.
- C# error path: none.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md`
- Story's acceptance criteria for this behavior: none — cross-cutting.
- Story alignment:
  - [ ] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [x] No story — spec gap; must be backfilled before remediation

Same as Finding 014.

## 5. Status

- **Classification**: Not-yet-implemented (stub). Depends entirely on Finding 014.
- **What's needed to finish**:
  1. Land Finding 014 (middleware + named policies).
  2. Modify `Program.cs:468` to `github.MapPost("/webhooks", GitHubEndpoints.Webhooks).RequireRateLimiting("github-webhook");`.
  3. Verify the policy name matches what Finding 014 registers. The policy should be partitioned by client IP with a fixed-window of 300 req/min per partition.
  4. Consider a second partition dimension: `X-GitHub-Hook-Installation-Target-ID` (if sent) or `X-GitHub-Hook-ID` header, so one noisy installation doesn't starve others.
- **Is it "just a stub" or is scope missing?** Stub, dependent on Finding 014.
- **Blockers**: Finding 014.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Program.cs:468` — append `.RequireRateLimiting("github-webhook");`.
  - (Finding 014 handles the `AddRateLimiter` config.)
- Files to create: none.
- Tests to add:
  - `GitHubEndpointsIntegrationTests.Webhook_RateLimitPolicyBound_Returns429OnBreach` — configure the factory, post 301 requests, assert 429.
  - `GitHubEndpointsIntegrationTests.Webhook_DifferentIPs_GetSeparateBuckets` — two IPs both at 250/min, both succeed (no cross-contamination).
- Estimated effort: 1h on top of Finding 014. Broken down:
  - Per-route binding + test: 1h.

## References

- TS source: `packages/api/src/routes/github/github-webhook.ts:65-82` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Program.cs:465-468`
- Story: no story — cross-cutting
- Related findings: `014-no-inbound-rate-limit-webhook-oauth.md` (parent)
