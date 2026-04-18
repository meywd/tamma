# Finding 009: OAuth `state` parameter missing (CSRF + redirect + invite carrier)

**Scope**: auth
**Severity**: P0 (cutover-blocking)
**Status**: Incomplete (parameter entirely absent)
**Estimated port effort**: 4-6h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/auth/github-oauth.ts`.

- File: `packages/api/src/routes/auth/github-oauth.ts:64-86` (GET /api/auth/github).
- Contract: Accept `?rd=<postLoginRedirect>&invite=<inviteToken>`, sanitize the redirect URL against the `*.tamma.dev` allow-list, base64url-encode a JSON object `{ rd, invite }` into the `state` query parameter, and redirect to `github.com/login/oauth/authorize` with that state.
- Key code:

```typescript
// packages/api/src/routes/auth/github-oauth.ts:69-86 (9e9a57c~1)
app.get('/api/auth/github', async (request, reply) => {
  const callbackUrl = `${dashboardUrl}/oauth2/callback`;
  const scope = 'read:user user:email';

  const rd = request.query.rd;
  const invite = request.query.invite;
  const sanitizedRd = rd ? sanitizeRedirectUrl(rd) : null;
  const statePayload: Record<string, string> = {};
  if (sanitizedRd) statePayload['rd'] = sanitizedRd;
  if (invite) statePayload['invite'] = invite;
  const state = Buffer.from(JSON.stringify(statePayload)).toString('base64url');

  const githubUrl = `https://github.com/login/oauth/authorize` +
    `?client_id=${clientId}` +
    `&redirect_uri=${encodeURIComponent(callbackUrl)}` +
    `&scope=${encodeURIComponent(scope)}` +
    `&state=${encodeURIComponent(state)}`;
  return reply.redirect(githubUrl);
});
```

- The `sanitizeRedirectUrl` helper rebuilds the URL from parsed components (so CodeQL sees untainted output) and enforces `https:` + `*.tamma.dev` hostname.
- Dependencies: none (pure routing).
- Tests: `packages/api/src/routes/auth/__tests__/` had unit tests for `sanitizeRedirectUrl` covering open-redirect attempts.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:377-385`.
- Contract: Build `https://github.com/login/oauth/authorize?client_id=...&redirect_uri=...&scope=user:email`. No `state` parameter. No `rd` query param acceptance. No `invite` query param acceptance.
- Key code (nine lines total):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:377-385
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

- Dependencies: `IConfiguration`.
- Tests: None — the method is trivially returns-a-redirect.

## 3. The gap

Three orthogonal things carried by `state` in TS are all dropped:

1. **CSRF protection**: Without `state`, an attacker can craft a `github.com/login/oauth/authorize?client_id=tamma&redirect_uri=api.tamma.dev/api/auth/github/callback&code=<attackerCode>` URL and trick a victim into visiting it. The callback will execute under the victim's browser — if it were implemented (Finding 008). OAuth 2.0 requires state for this exact reason (RFC 6749 §10.12).
2. **Post-login redirect**: The TS flow supported `/api/auth/github?rd=https://elsa.tamma.dev/studio/workflows/123` — a user arrives at Elsa Studio without a session, gets bounced through OAuth, and lands back where they started. C# has no `rd` mechanism; the callback can only redirect to a single configured URL.
3. **Invite carrier**: Users invited via email receive a link like `https://app.tamma.dev/invite/<token>` which, for GitHub-OAuth signup, triggers `/api/auth/github?invite=<token>`. The invite token rides in state through GitHub back to the callback. Without state, this flow is impossible — the invite is forgotten at the moment the browser leaves tamma.dev for github.com.

Also: the C# URL uses `scope=user:email` (reads email only). TS used `scope=read:user user:email` (also needs `read:user` to fetch `id` and `login`). If the callback (Finding 008) were implemented, `read:user` would be required for `GET /user`. The scope is also under-powered.

Error paths:
- TS: Sanitizer rejects non-tamma.dev `rd` → stores nothing in state → callback redirects to default.
- C#: No `rd` handling at all. No validation surface.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-2-user-login-session-management.md`
- Subtask 4.2 (line 61): *"Use `state` parameter with CSRF token (stored in short-lived cookie)"*.
- Story 16-4 implementation plan §278 expects `rd` to round-trip through OAuth so the user lands back on their starting subdomain.
- Story 18-3 implementation plan §82-88 describes the invite flow that hands off `state.invite` through OAuth.
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

Story subtask 4.2 mandates state for CSRF specifically. C# dropped it entirely.

## 5. Status

- **Classification**: Incomplete — the parameter isn't merely rewritten, it's simply absent. CSRF protection never existed in the C# flow.
- **What's needed to finish**:
  1. Accept `?rd=` and `?invite=` as query params on `GitHubAuth`.
  2. Generate a 32-byte random CSRF nonce, store in a short-lived httpOnly cookie (`tamma_oauth_csrf`, `MaxAge=600`, `SameSite=Strict`).
  3. Sanitize `rd` against `*.tamma.dev` (port the `sanitizeRedirectUrl` logic — this is 20 lines of TS in github-oauth.ts:239-270).
  4. Build `state` as base64url(JSON `{ rd, invite, csrf }`).
  5. Append `&state=<encoded>` to the authorize URL.
  6. Change `scope` from `user:email` to `read:user user:email`.
  7. On callback (Finding 008), verify the `state.csrf` matches the cookie's value. Reject otherwise.
- **Is it "just a stub" or is scope missing?** Scope was visibly not implemented — not even stub-comment acknowledges CSRF.
- **Blockers**: Finding 008 (callback) must also land for state to be meaningful. They must ship together.

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs` (GitHubAuth + GitHubCallback).
- Files to create: `apps/tamma-elsa/src/Tamma.Api/Auth/OAuthStateCodec.cs` (shared with Finding 008), `apps/tamma-elsa/src/Tamma.Api/Auth/RedirectUrlSanitizer.cs`.
- Tests to add:
  - `GitHubAuth_WithoutParams_RedirectsWithState`.
  - `GitHubAuth_WithRd_SanitizesToTammaDev`.
  - `GitHubAuth_WithUnsafeRd_DropsRdFromState` (e.g. `rd=https://evil.com`).
  - `GitHubAuth_WithInvite_EmbedsInState`.
  - `RedirectUrlSanitizer_RelativePath_Preserved`.
  - `RedirectUrlSanitizer_AbsoluteNonTammaDev_Rejected`.
  - `RedirectUrlSanitizer_ProtocolRelative_Rejected`.
  - `GitHubCallback_CsrfMismatch_Rejects` (ties to Finding 008).
- Estimated effort: 4-6h
  - State codec + sanitizer: 2h
  - Endpoint wiring + cookie: 1h
  - Unit tests (7 cases): 2h
  - Callback CSRF verify (part of Finding 008): 1h — not double-counted here

## References

- TS source: `packages/api/src/routes/auth/github-oauth.ts:64-86` and `:239-272` (sanitizer) (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:377-385`
- Story: `docs/stories/epic-18/18-2-user-login-session-management.md` (subtask 4.2); `docs/stories/epic-16/16-4-unified-navigation-impl-plan.md` (§278); `docs/stories/epic-18/18-3-organization-tenant-creation-impl-plan.md` (§82-88)
- Related findings: `008-oauth-callback-stub.md` (callback needs state)
- RFC 6749 §10.12 — CSRF protection via `state`.
