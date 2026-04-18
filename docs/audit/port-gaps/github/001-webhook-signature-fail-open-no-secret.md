# Finding 001: Webhook signature verification fails open when secret is empty

**Scope**: github
**Severity**: P0 (cutover-blocking)
**Status**: Behavioral drift (ported but semantics diverged)
**Estimated port effort**: 1h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/github/github-webhook.ts`.

- File: `packages/api/src/routes/github/github-webhook.ts:42-46`, `:87-93`
- Contract/behavior: TS required `webhookSecret` as a plugin-registration option (`GitHubWebhookOptions.webhookSecret: string`, not optional). Missing signature header → 401. Bad signature → 401. There is no fail-open path — `verifySignature` is always called, and if the secret is an empty string the HMAC produces a non-matching digest so the check still fails closed.

```typescript
// packages/api/src/routes/github/github-webhook.ts:42-46 (9e9a57c~1)
function verifySignature(payload: string, signature: string, secret: string): boolean {
  const expected = 'sha256=' + createHmac('sha256', secret).update(payload).digest('hex');
  if (expected.length !== signature.length) return false;
  return timingSafeEqual(Buffer.from(expected), Buffer.from(signature));
}
```

```typescript
// packages/api/src/routes/github/github-webhook.ts:87-93 (9e9a57c~1)
if (!signatureHeader || typeof signatureHeader !== 'string') {
  return reply.status(401).send({ error: 'Missing signature' });
}

if (!verifySignature(rawBody, signatureHeader, options.webhookSecret)) {
  return reply.status(401).send({ error: 'Invalid signature' });
}
```

The secret comes in as a required constructor option at plugin-registration time. There is no conditional that skips verification when the secret is unset — the route cannot be registered without one.

- Dependencies: `node:crypto` (`createHmac`, `timingSafeEqual`), `GitHubWebhookOptions.webhookSecret: string` (required).
- Tests that exercised this: Tests validated valid-signature acceptance and invalid-signature rejection against a non-empty test secret; no test explicitly asserted behavior with `webhookSecret === ''` because the type signature made that state unreachable.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/GitHubEndpoints.cs:122-128`
- Contract/behavior: The handler reads `GitHub:WebhookSecret` from `IConfiguration` and only performs verification if the secret is **non-empty**. An empty or missing secret means every signature passes (the handler skips the check entirely and proceeds to dispatch).

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/GitHubEndpoints.cs:122-128 (current)
var secret = config["GitHub:WebhookSecret"];
if (!string.IsNullOrEmpty(secret) && !VerifySignature(secret, body, signature))
{
    logger.LogWarning("Webhook rejected: invalid signature");
    return Results.Unauthorized();
}
```

The algorithm itself (lines 191-205) is ported correctly — `HMACSHA256`, hex-lowercase encoding, `CryptographicOperations.FixedTimeEquals` is a true constant-time compare. The sole defect is the outer short-circuit: `!string.IsNullOrEmpty(secret) && ...`. When the secret is unset, the whole conjunction evaluates false and the body of the `if` is skipped, meaning the caller-supplied `X-Hub-Signature-256` is never compared against anything.

- Dependencies: `IConfiguration`, `System.Security.Cryptography.HMACSHA256`, `CryptographicOperations.FixedTimeEquals`. Route registered at `Program.cs:468` (`github.MapPost("/webhooks", GitHubEndpoints.Webhooks);`) — no auth, no middleware ordering that could catch this.
- Tests: `apps/tamma-elsa/tests/Tamma.Api.Tests/GitHub/GitHubEndpointsIntegrationTests.cs` fixes the secret at `WebhookSecret = "test-webhook-secret-value"` (line 25) and exercises the signed-path. **No test covers the empty-secret scenario** — a test that boots the host with `GitHub:WebhookSecret` unset and posts an arbitrary body with a garbage signature would demonstrate the fail-open.

## 3. The gap

Concrete behavioral difference — what a caller or user experiences differently.

- TS did: reject every webhook with a bad signature (401) regardless of configuration state; cannot even register the route without a secret.
- C# does: when `GitHub:WebhookSecret` is empty/missing, accept every webhook with any `X-Hub-Signature-256` header value, parse the JSON, and dispatch to `InstallationRouterService` — side-effecting the DB, emitting domain events, enqueueing tasks.
- For a caller sending `POST /api/github/webhooks` with `X-Hub-Signature-256: sha256=deadbeef` and a crafted `installation` payload against a host where the secret is unset, TS returns 401 and C# returns 200 with full dispatch.
- In production with existing data / deployed clients, this means: any misconfiguration (secret not supplied to Docker, ConfigMap rename, typo in `appsettings.Production.json`, accidentally promoted dev-mode config) silently becomes a **public, unauthenticated webhook endpoint** that an attacker can use to register fake installations, trigger `INSTALLATION.CREATED.SUCCESS` events tagged with arbitrary tenants, enqueue arbitrary `github.issues.*` / `github.pull_request.*` tasks, and corrupt the `github_installations` table via `UpsertAsync`.

Error paths:
- TS error path: missing secret → route fails to register at startup (type error) or 500 at request time; bad signature → 401 `{"error":"Invalid signature"}`.
- C# error path: missing secret → 200 `{received:true,...}` (fail open); bad signature with secret present → 401 (no body); missing header → 401 (no body).

## 4. Gap from stories

Which Epic / story file describes what this surface SHOULD be?

- Referenced story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md`
- Story's acceptance criteria for this behavior: The story's AC focuses on onboarding state/redirect semantics and does not explicitly mandate HMAC verification, because HMAC verification is treated as table stakes inherited from the existing TS code (see README: "existing system has ... installation model"). The webhook handler is referenced at Task 3 ("Update webhook handler for org-scoped installations") which modifies `packages/api/src/routes/github/github-webhook.ts` — that file's verification is the contract.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior (story was updated during port; TS was ahead of spec)
  - [ ] Describes a third behavior (neither TS nor C# matches the story)
  - [ ] No story — spec gap; must be backfilled before remediation

Also governed by CLAUDE.md `Security Requirements → Network Security`: "Webhook signature verification for platform events" — stated unconditionally.

## 5. Status

- **Classification**: Behavioral drift — the algorithm was ported correctly, but a bypass was introduced by making the secret optional at configuration-read time instead of at registration time.
- **What's needed to finish**:
  1. On application startup (or inside the handler, before any body parsing), fail fast if `GitHub:WebhookSecret` is null/empty. Prefer startup validation: throw in `Program.cs` when `builder.Configuration["GitHub:WebhookSecret"]` is missing and the GitHub route group is mapped.
  2. Remove the `!string.IsNullOrEmpty(secret) &&` short-circuit from `GitHubEndpoints.cs:124`. Verification must run unconditionally.
  3. Add an integration test that boots `WebApplicationFactory` with `GitHub:WebhookSecret` unset and asserts `401` (or host startup failure) for any webhook POST.
- **Is it "just a stub" or is scope missing?** This is a bug in a ported behavior, not missing scope. The scope was understood; the port inverted the semantics by treating empty-secret as a dev-mode escape hatch.
- **Blockers**: None. Independent fix. Does not require schema or data migration.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/GitHubEndpoints.cs` (remove short-circuit at line 124)
  - `apps/tamma-elsa/src/Tamma.Api/Program.cs` (add startup guard around line 465-468)
- Files to create: none
- Tests to add:
  - `GitHubEndpointsIntegrationTests.Webhook_RejectsWhenSecretMissing` — configure factory with `["GitHub:WebhookSecret"] = ""` and POST valid body + any signature → expect `401` or startup error.
  - `GitHubEndpointsIntegrationTests.Webhook_RejectsWhenSecretNull` — same but config entry unset.
- Estimated effort: 1h broken down as:
  - Remove short-circuit + add startup guard: 0.5h
  - Add two integration tests: 0.5h

## References

- TS source: `packages/api/src/routes/github/github-webhook.ts:42-46,87-93` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/GitHubEndpoints.cs:122-128,191-205`
- Story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md` (Task 3)
- Related findings: `docs/audit/port-gaps/github/014-no-inbound-rate-limit-webhook-oauth.md` (also webhook-surface hardening)
- CLAUDE.md section: `Security Requirements → Network Security`
