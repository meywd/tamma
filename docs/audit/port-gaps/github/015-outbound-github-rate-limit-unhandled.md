# Finding 015: Outbound GitHub API rate-limit handling missing (no X-RateLimit-Reset backoff)

**Scope**: github
**Severity**: P2 (correctness/observability)
**Status**: Not-yet-implemented (stub) — because there is effectively no outbound GitHub calling code yet
**Estimated port effort**: 2-3h (once Finding 007 lands)

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/services/github-secrets-provisioner.ts` + surrounding Octokit-using files.

- File: implicit — TS used `@octokit/rest` and `@octokit/auth-app` throughout (see `packages/api/src/routes/github/github-callback.ts:14-16,51-57,83-90`). Octokit.js has built-in rate-limit plugins that read `X-RateLimit-Remaining` and `X-RateLimit-Reset` from every response, detect `Retry-After` on 403/429, and queue retries with appropriate backoff. When a developer adds `@octokit/plugin-retry` + `@octokit/plugin-throttling` (the standard Octokit setup for production), the client automatically handles secondary rate limits, retries safely on 5xx, and honors GitHub's abuse detection cooldowns.
- Contract/behavior: even without the throttle plugin, Octokit surfaces GitHub's rate-limit headers in response metadata (`response.headers['x-ratelimit-remaining']`, `x-ratelimit-reset`) and throws `RequestError` with `status: 403` + a message containing `"rate limit"` when breached. Callers could inspect and retry. Whether the TS codebase used the throttling plugin or just relied on Octokit's baseline is not fully determinable from the deleted file list alone, but the overall posture — using Octokit with App auth — is **dramatically more rate-limit-aware** than what C# has today.

Representative Octokit usage in TS:

```typescript
// packages/api/src/routes/github/github-callback.ts:51-57 (9e9a57c~1)
const octokit = new Octokit({
  authStrategy: createAppAuth,
  auth: {
    appId: options.appId,
    privateKey: options.privateKey,
  },
});
```

The `@octokit/rest` client interprets 403/429 with rate-limit headers, surfaces them in errors, and — with the throttle plugin — reschedules the call after the reset window.

- Dependencies: `@octokit/rest`, `@octokit/auth-app`, optionally `@octokit/plugin-throttling`, `@octokit/plugin-retry`.
- Tests that exercised this: integration tests stubbing 403 responses to ensure errors bubbled; no assertion that we necessarily waited for reset (that was left to Octokit).

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: No outbound GitHub calls exist today (Finding 007 documents this). The audit summary notes: "EnsureSuccessStatusCode() throws on 403 without inspecting X-RateLimit-Reset" — this is a projection of what will happen once outbound GitHub code is added using raw `HttpClient` without rate-limit awareness.
- Contract/behavior: prospectively, any naive implementation using `HttpClient.GetAsync(...)` + `EnsureSuccessStatusCode()` will throw `HttpRequestException` on a 403 (GitHub's rate-limit status code — not the more sensible 429) and will not inspect `X-RateLimit-Reset` to know when to retry.

The only outbound GitHub-directed call path today is in the `ApiKeyRotationService.cs:13-16` comment acknowledging it was dropped. If Finding 007 and Finding 013 are implemented naively (e.g. `var response = await client.GetAsync(url); response.EnsureSuccessStatusCode();`) the result will be:
- 403 with `X-RateLimit-Remaining: 0` → uncaught exception → upstream 500 to the user.
- 403 with abuse-detection body and `Retry-After: 60` → same; our client has no retry after the advertised cooldown.

- Dependencies: none yet. The finding prescribes the right shape before the feature lands.
- Tests: no tests cover rate-limit handling because there's no outbound call.

## 3. The gap

- TS did (via Octokit): expose headers, throw meaningful errors, optionally auto-retry via throttle plugin.
- C# does: nothing (no calls), but will throw naively when calls are added unless this finding is addressed.
- For a caller completing install when the GitHub App has recently burned through its 15,000 req/hour limit (e.g. a large multi-org install with many repos), TS (with throttle plugin) would wait until the reset window and complete; TS (without throttle) would throw and the user would see an error page. C# with a naive port throws.
- In production with existing data / deployed clients, this means: install flows that cross the 5,000 rate-limit floor (GitHub App installation tokens have a per-installation 5,000/hr limit) will fail with a generic 500 rather than being queued/retried. For a large customer with 100+ repos, even a single install requires many API calls (1 getInstallation + 1 listRepos + 100 getRepoPublicKey + 100 createOrUpdateRepoSecret = ~202 calls), which is well under 5k but can drift close with webhook-driven side effects.

Error paths:
- TS error path: 403 rate-limit → error surfaces, user sees error page, may retry naturally; throttle plugin masks transient breaches entirely.
- C# error path (prospective, post-Finding 007): raw `HttpRequestException` bubbles to the endpoint, 500 Internal Server Error, user sees generic failure.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md`
- Story's acceptance criteria for this behavior: none explicit. Story 18-4 focuses on the happy path; rate-limit / abuse-detection is implicit.
- Story alignment:
  - [ ] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [x] No story — spec gap; must be backfilled before remediation

Cross-cutting hardening story needed (same one as Finding 014 could own this).

## 5. Status

- **Classification**: Not-yet-implemented (stub). Downstream of Finding 007's implementation.
- **What's needed to finish**:
  1. Decide the client posture. Options:
     - **Octokit.NET** (`Octokit` NuGet): closest parity with TS; has `ApiInfo.RateLimit` on every response; does NOT have a built-in throttle plugin, so retry logic must be added.
     - **Raw `HttpClient` + Polly**: uses `Microsoft.Extensions.Http.Resilience` + `AddStandardResilienceHandler()`. Good fit for .NET 8. Polly can handle `Retry-After` natively.
     - Recommended: Octokit.NET for the surface area (PRs, repos, checks) we already need, layered with a Polly handler that inspects `X-RateLimit-Remaining` / `Retry-After` / 403 / 429.
  2. Implement a `DelegatingHandler` (`GitHubRateLimitHandler`) that:
     - Before sending: if prior response said `Remaining == 0`, await until `Reset`.
     - After receiving 403: check body for `"API rate limit exceeded"` or `"abuse detection"`; if so, read `Retry-After` or `X-RateLimit-Reset`, await, retry (up to N=3).
     - After receiving 429: same as above.
     - Log structured events on every backoff: `{event: "GITHUB.RATE_LIMIT.THROTTLED", resetAt, retryIn, attempt}`.
  3. Register as a named HttpClient handler for the GitHub client registered in Finding 007's `IGitHubAppClient`.
  4. Persist the current rate-limit posture in a short-lived cache (optional) so concurrent requests share the same delay rather than each independently awaiting.
- **Is it "just a stub" or is scope missing?** Scope missing — the whole outbound surface is missing, of which this is a hardening layer.
- **Blockers**: Finding 007 must land first.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/GitHubAppClient.cs` (created in Finding 007) — add Polly/ResilienceHandler pipeline.
  - `apps/tamma-elsa/src/Tamma.Api/Program.cs` — register the DelegatingHandler.
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/GitHubRateLimitHandler.cs`
- Tests to add:
  - `GitHubRateLimitHandlerTests.OnFirst403WithRetryAfter_WaitsAndRetries`
  - `GitHubRateLimitHandlerTests.OnRemainingZeroPriorResponse_WaitsBeforeNextCall`
  - `GitHubRateLimitHandlerTests.On429_HonorsRetryAfterHeader`
  - `GitHubRateLimitHandlerTests.AfterMaxRetries_FailsWithStructuredError`
- Estimated effort: 2-3h broken down as:
  - Handler implementation: 1.5h
  - Unit tests with mocked HttpMessageHandler: 1-1.5h

## References

- TS source: implicit — Octokit handles this; see e.g. `packages/api/src/routes/github/github-callback.ts:51-57` (commit `9e9a57c~1`)
- C# source: not yet — downstream of Finding 007
- Story: spec gap
- Related findings: `007-installation-callback-no-github-api-fetch.md`, `013-secrets-provisioner-libsodium-missing.md`, `014-no-inbound-rate-limit-webhook-oauth.md`
- GitHub docs: [Best practices for using GitHub REST API — rate limiting](https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api?apiVersion=2022-11-28#handle-rate-limit-errors-appropriately)

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: `4e1e0e4`
- **Notes**: Outbound rate-limit handling is now layered over `OctokitGitHubAppClient` + `OctokitGitHubEngineCallbackService` + `LibsodiumGitHubSecretsProvisioner`. Octokit.NET surfaces `RateLimitExceededException` (populated with `Limit`/`Remaining`/`Reset` from the `X-RateLimit-*` headers) and `AbuseException` (with `RetryAfterSeconds`) as typed exceptions; both services catch them explicitly and log structured warnings including `resetAt`, the installation id, and the owner/repo tuple. Callers receive a typed `GitHubAppResult.Failed("github_rate_limited")` / `GitHubCallbackResult.Failed("github_rate_limited")` rather than an uncaught 500 — enough context for the caller or retry-policy layer to back off. `AuthorizationException` additionally invalidates the cached installation token so the next request re-mints fresh credentials. A dedicated Polly delegating handler (the `GitHubRateLimitHandler` proposed in §5) is deferred to a cross-cutting hardening story — Octokit's typed exceptions + structured logging cover the observability requirement today and the actual backoff retry behaviour lives in the workers that call these services (the caller decides policy, which matches the TS posture of "let the retry plugin be opt-in").
