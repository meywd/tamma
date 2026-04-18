# Finding 009: OAuth start does not include a CSRF `state` parameter

**Scope**: github
**Severity**: P0 (cutover-blocking)
**Status**: Behavioral drift (ported but semantics diverged) — effectively a security regression
**Estimated port effort**: 2-3h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/auth/github-oauth.ts`.

- File: `packages/api/src/routes/auth/github-oauth.ts:73-95`
- Contract/behavior: TS built a base64url-encoded JSON `state` from the incoming `rd` (redirect destination) and `invite` (invite token) query params, after sanitizing the `rd` through `sanitizeRedirectUrl` (lines 262-287) which restricts the target to `*.tamma.dev` over HTTPS. The state doubled as a CSRF token AND a transport for the post-login redirect + invite semantics.

```typescript
// packages/api/src/routes/auth/github-oauth.ts:73-95 (9e9a57c~1)
app.get<{
  Querystring: { rd?: string; invite?: string };
}>('/api/auth/github', async (request: FastifyRequest<{ Querystring: { rd?: string; invite?: string } }>, reply: FastifyReply) => {
  const callbackUrl = `${dashboardUrl}/oauth2/callback`;
  const scope = 'read:user user:email';

  // Encode redirect destination and optional invite token in OAuth state param.
  // Sanitize the URL upfront so only reconstructed (non-tainted) values are stored.
  const rd = request.query.rd;
  const invite = request.query.invite;
  const sanitizedRd = rd ? sanitizeRedirectUrl(rd) : null;
  const statePayload: Record<string, string> = {};
  if (sanitizedRd) {
    statePayload['rd'] = sanitizedRd;
  }
  if (invite) {
    statePayload['invite'] = invite;
  }
  const state = Buffer.from(JSON.stringify(statePayload)).toString('base64url');

  const githubUrl = `https://github.com/login/oauth/authorize?client_id=${clientId}&redirect_uri=${encodeURIComponent(callbackUrl)}&scope=${encodeURIComponent(scope)}&state=${encodeURIComponent(state)}`;
  return reply.redirect(githubUrl);
});
```

On the return leg (`github-oauth.ts:148-160`) the state was parsed back. Note: TS did not bind state to a session-side nonce (e.g., a random value also stored in a short-lived cookie and verified on callback). So strictly speaking it was a transport envelope, not a full CSRF token — but it was at least present, attacker-unguessable-by-default (because state is base64url of a JSON blob with optional fields), and used to drive post-callback behavior. This is still a substantial improvement over having no state at all.

- Dependencies: `sanitizeRedirectUrl` (defensive URL parser, lines 262-287).
- Tests that exercised this: integration tests asserted the generated GitHub redirect URL carried `state=...` and that the callback round-tripped the `rd` and `invite` values.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:377-385`
- Contract/behavior: The C# endpoint builds a GitHub authorize URL with `client_id`, `redirect_uri`, and `scope=user:email` — and **no `state` parameter at all**. No `rd`. No `invite`.

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:377-385 (current)
public static Task<IResult> GitHubAuth(IConfiguration config)
{
    var clientId = config["GitHub:ClientId"];
    if (string.IsNullOrEmpty(clientId))
        return Task.FromResult(Results.BadRequest(new { error = "GitHub OAuth not configured" }));
    var redirectUri = config["GitHub:RedirectUri"] ?? "http://localhost:3000/api/auth/github/callback";
    var url = $"https://github.com/login/oauth/authorize?client_id={clientId}&redirect_uri={Uri.EscapeDataString(redirectUri)}&scope=user:email";
    return Task.FromResult(Results.Redirect(url));
}
```

The URL has no `state=` segment, no `rd` ingestion, no invite handling.

- Dependencies: `IConfiguration` only. No cookie store, no nonce generator, no JWT.
- Tests: No tests in `Tamma.Api.Tests` hit `GitHubAuth` beyond the "config-missing returns 400" case. There are no tests that assert the presence/absence of a state parameter.

## 3. The gap

Concrete behavioral difference — what a caller or user experiences differently.

- TS did: emit a GitHub authorize URL with `state=<base64url>`, where the payload carries sanitized `rd` and `invite`; scope was `read:user user:email`.
- C# does: emit a GitHub authorize URL with **no state**, scope `user:email` (see Finding 010 for the scope gap), no support for `rd`, no support for `invite`.
- For a caller hitting `GET /api/auth/github?rd=https://elsa.tamma.dev/&invite=abc123`:
  - TS: redirects to `github.com/login/oauth/authorize?...&state=eyJyZCI6Imh0dHBzOi8vZWxzYS50YW1tYS5kZXYvIiwiaW52aXRlIjoiYWJjMTIzIn0` — preserving both signals through the OAuth round-trip.
  - C#: redirects to `github.com/login/oauth/authorize?...` with `rd` and `invite` dropped on the floor, and no `state`.
- In production with existing data / deployed clients, this means:
  - **CSRF exposure on OAuth login**: RFC 6749 §10.12 requires a `state` parameter on the authorization request as the canonical defense against OAuth login-CSRF. Without it, an attacker can craft a link to `github.com/login/oauth/authorize?client_id=OURS&redirect_uri=OURS&scope=...` and a victim who clicks it will, after authorizing on GitHub, be redirected to **our** callback with an attacker-owned `code`. When our callback completes, the attacker's GitHub identity is bound to the victim's session (classic login-CSRF / account-confusion). This is a real vulnerability in the current C# code, not a theoretical one.
  - **Invite flow unusable via OAuth**: if the user has an invite link like `dash.tamma.dev/accept?token=X`, the old TS flow sent them through `/api/auth/github?invite=X` and applied the role on callback. C# loses the invite at the start — the invite ceremony cannot complete through GitHub login.
  - **Redirect-to-subdomain unusable**: the multi-subdomain SSO pattern (user logs in from `elsa.tamma.dev`, gets redirected to `/api/auth/github?rd=https://elsa.tamma.dev/`, returns to the right place) is broken. Users land at the dashboard root instead.

Error paths:
- TS error path: malformed `rd` → `sanitizeRedirectUrl` returns null, state omits it; request still succeeds.
- C# error path: no `rd`/`invite` handling, so nothing to fail — silently ignored.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-2-user-login-session-management.md`
- Story's acceptance criteria for this behavior:
  > AC #5: "**GitHub OAuth login** endpoint `GET /api/v1/auth/github` initiates OAuth flow, `GET /api/v1/auth/github/callback` completes it"
  > Task 4, Subtask 4.2: "Use `state` parameter with CSRF token (stored in short-lived cookie)"
  > Security Considerations: "**CSRF on OAuth**: The `state` parameter includes a random value stored in a short-lived cookie, verified on callback"
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story — spec gap

Story 18-2 is explicit and unambiguous: state is required, and the story even goes further than TS by specifying a cookie-bound nonce (a stronger CSRF defense). The current C# code meets neither bar.

Note: story 18-2 talks about `/api/v1/auth/github` (end-user route) while the current C# route is the admin path `/api/auth/github`. That's a separate concern (route path vs. behavior), but the behavioral contract for the admin route is inherited from the TS `github-oauth.ts` which did have state.

## 5. Status

- **Classification**: Behavioral drift — the C# handler does not meet the security contract stated in TS or in the story.
- **What's needed to finish**:
  1. Generate a cryptographically random `state` value (32 bytes, base64url-encoded). Prefer `RandomNumberGenerator.GetBytes(32)` over `Guid.NewGuid()`.
  2. Persist the state either in a signed short-lived cookie (per story 18-2) OR embed it inside a JWT alongside `rd`/`invite`. Cookie is stronger; JWT is simpler. Recommend cookie: `oauth_state=<value>; Max-Age=600; HttpOnly; Secure; SameSite=Lax; Path=/api/auth/github`.
  3. Also accept query-string `rd` and `invite` from the caller; serialize them into the state (via JWT payload or a parallel short-lived server-side state store keyed by the random value).
  4. On callback (Finding 012), read both the cookie and the query `state`, verify they match with constant-time comparison, then parse the JWT / lookup the server record to recover `rd`+`invite`.
  5. Add the scope fix from Finding 010 at the same time.
- **Is it "just a stub" or is scope missing?** Scope was understood (story 18-2 is explicit) and not implemented. This is a shortcut taken during the port.
- **Blockers**: None standalone; pairs with Finding 012 (callback implementation) because state is only useful if callback validates it.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:377-385` — rewrite `GitHubAuth` to generate state, set cookie, embed `rd`+`invite`, include state in URL.
  - `apps/tamma-elsa/src/Tamma.Api/Program.cs:334` — may need a cookie policy tweak for the new cookie.
- Files to create:
  - Optional: `apps/tamma-elsa/src/Tamma.Api/Services/Auth/OAuthStateService.cs` — encapsulates state generation, cookie read/write, verification.
- Tests to add:
  - `AuthEndpointsTests.GitHubAuth_IncludesStateParameter` — assert redirect URL contains `state=` and that a `Set-Cookie: oauth_state=` header is emitted.
  - `AuthEndpointsTests.GitHubAuth_StateIsRandomAndDifferentPerRequest` — two back-to-back calls produce different state values.
  - `AuthEndpointsTests.GitHubAuth_PassesRd` — `?rd=https://elsa.tamma.dev/` survives into state payload.
  - `AuthEndpointsTests.GitHubAuth_RejectsNonTammaRd` — `?rd=https://evil.com/` is dropped (sanitized).
  - `AuthEndpointsTests.GitHubAuth_PassesInvite` — `?invite=abc` survives into state payload.
- Estimated effort: 2-3h broken down as:
  - State cookie service: 1h
  - Rewrite `GitHubAuth`: 0.5h
  - Tests (5 cases): 1-1.5h

## References

- TS source: `packages/api/src/routes/auth/github-oauth.ts:73-95,262-287` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:377-385`
- Story: `docs/stories/epic-18/18-2-user-login-session-management.md` (AC #5, Task 4.2, Security Considerations)
- Related findings: `010-oauth-start-missing-read-user-scope.md`, `011-oauth-start-no-rd-invite.md`, `012-oauth-callback-literal-stub.md`
- CLAUDE.md section: `Security Requirements → Input Validation`
