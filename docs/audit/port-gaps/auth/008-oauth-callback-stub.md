# Finding 008: GitHub OAuth callback is a "not yet implemented" stub

**Scope**: auth
**Severity**: P0 (cutover-blocking)
**Status**: Not-yet-implemented (stub)
**Estimated port effort**: 10-14h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/auth/github-oauth.ts`.

- File: `packages/api/src/routes/auth/github-oauth.ts:86-220` (GET /api/auth/github/callback).
- Contract: 10 distinct responsibilities in a single route handler:
  1. Handle OAuth error redirect (`?error=...&code=...`).
  2. POST `https://github.com/login/oauth/access_token` to exchange `code` for `access_token`.
  3. GET `https://api.github.com/user` for the profile (`id`, `login`, `email`).
  4. Parse the `state` param (base64url of JSON `{ rd, invite }`).
  5. If `invite` token present, look up the invite, assign its role if still valid, mark accepted.
  6. Upsert the user in `IUserStore` (by `githubId`).
  7. If invite promoted the user to non-default role, explicitly `updateUserRole`.
  8. Check user's installations; if empty, auto-link to every active installation (bootstrap).
  9. Sign a JWT and set `tamma_session` cookie with `domain=.tamma.dev`.
  10. Redirect to the sanitized `rd` URL (or the dashboard default).
- Key code (abridged — 134 lines total):

```typescript
// packages/api/src/routes/auth/github-oauth.ts:95-109 (9e9a57c~1)
const tokenResponse = await fetch('https://github.com/login/oauth/access_token', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
  body: JSON.stringify({ client_id: clientId, client_secret: clientSecret, code }),
});

const tokenData = (await tokenResponse.json()) as { access_token?: string; error?: string };
if (!tokenData.access_token) {
  return reply.redirect(`${dashboardUrl}/login?error=token_exchange_failed`);
}
accessToken = tokenData.access_token;
```

```typescript
// packages/api/src/routes/auth/github-oauth.ts:146-161 (invite handling)
let assignedRole: 'owner' | 'admin' | 'member' = 'member';
if (inviteToken && inviteStore) {
  const invite = await inviteStore.getInviteByToken(inviteToken);
  if (invite && invite.acceptedAt === null && invite.expiresAt > new Date().toISOString()) {
    assignedRole = invite.role;
    await inviteStore.acceptInvite(invite.id);
    request.log.info({
      event: 'USER.INVITE_ACCEPTED.SUCCESS',
      inviteId: invite.id, role: invite.role, githubLogin: githubUser.login,
    }, 'Invite accepted during OAuth callback');
  }
}
```

```typescript
// packages/api/src/routes/auth/github-oauth.ts:180-210 (issue JWT, redirect)
const token = app.jwt.sign({
  id: user.id, username: user.githubLogin, githubId: user.githubId, role: user.role,
});

reply.setCookie('tamma_session', token, {
  path: '/', httpOnly: true, secure: true, sameSite: 'lax' as const,
  maxAge: tokenExpiresIn, domain: '.tamma.dev',
});

return reply.redirect(redirectTo ?? dashboardUrl);
```

- Dependencies: `IUserStore.upsertUser`, `updateUserRole`, `getUserInstallations`, `linkUserToInstallation`, `IInviteStore.getInviteByToken`, `acceptInvite`, `IGitHubInstallationStore.listActiveInstallations`, `@fastify/jwt`, `@fastify/cookie`.
- Tests: `packages/api/src/routes/auth/__tests__/` had fixtures mocking the GitHub API.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:387-391`.
- Contract: Returns the literal string `"GitHub callback - not yet implemented"`. Does nothing else.
- Key code (**five lines including the signature**):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:387-391
public static Task<IResult> GitHubCallback()
{
    // TODO: Implement GitHub OAuth callback
    return Task.FromResult(Results.Ok(new { message = "GitHub callback - not yet implemented" }));
}
```

- The method signature takes no parameters — it doesn't even accept `code`, `state`, or `error`.
- Registered in `Program.cs:335`: `app.MapGet("/api/auth/github/callback", AuthEndpoints.GitHubCallback);`.
- Dependencies: None (nothing is injected).
- Tests: None would meaningfully exercise it. An integration test would see 200 and pass.

## 3. The gap

- TS did: The entire OAuth 2.0 authorization code → user session flow.
- C# does: Return an "OK, not implemented" message. No session is created. No user is upserted. No invite is processed. Redirect doesn't happen.

For a caller clicking "Sign in with GitHub":
- Browser redirects to `github.com/login/oauth/authorize?client_id=...&...`.
- GitHub redirects back to `api.tamma.dev/api/auth/github/callback?code=abc&state=xyz`.
- TS: fetches access token, creates session, redirects to `app.tamma.dev` with the cookie set. User is signed in.
- C#: shows the user a JSON response body `{ "message": "GitHub callback - not yet implemented" }`. Browser displays raw JSON. No session. User cannot proceed.

Production impact:
- **GitHub OAuth login is 100% broken.** Every OAuth sign-in attempt lands on the JSON response page.
- **Invite-via-OAuth flow broken** (Finding 021 also affects this): admins who invite a user, and the user accepts by clicking the invite link that goes through GitHub OAuth, never get enrolled. The invite stays in `pending` forever.
- **Installation auto-linking broken**: new GitHub users don't get bootstrapped onto existing installations. The user has a brand-new account with zero installation access.
- **Dashboard redirect query param (`?rd=elsa.tamma.dev/...`) broken**: even if the callback worked, the sanitization code that allowed cross-subdomain post-auth redirect is gone.

Error paths:
- TS: 6 distinct error paths via redirect — `?error=missing_code`, `?error=token_exchange_failed`, `?error=github_unavailable`, `?error=github_user_fetch_failed`, and 2 fallbacks.
- C#: no error path; always 200.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-2-user-login-session-management.md` (Task 4 for end-user OAuth); `docs/stories/epic-16/16-1-oauth2-proxy-unified-auth.md` (admin OAuth coexistence); `docs/stories/epic-18/18-3-organization-tenant-creation-impl-plan.md` (invite-via-OAuth).
- Story 18-2 AC 5 (line 17): *"GitHub OAuth login endpoint `GET /api/v1/auth/github` initiates OAuth flow, `GET /api/v1/auth/github/callback` completes it"*.
- Story 18-2 AC 6-7 (line 18-19): *"GitHub OAuth creates a new user if none exists (with `emailVerified: true`, `authMethod: 'github'`), or links to existing email-matched user / If a GitHub OAuth user's email matches an existing email-registered user, the accounts are linked (`authMethod: 'both'`)"*.
- Story 18-2 subtasks 4.1-4.10 (line 59-69) enumerate the 10 responsibilities listed above.
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

The story is exhaustive. Zero percent of it is implemented in C#.

## 5. Status

- **Classification**: Not-yet-implemented (stub). Literal `// TODO` comment on line 389.
- **What's needed to finish** — a port of the ten responsibilities:
  1. Accept `code`, `state`, `error` as query parameters; handle GitHub error redirect.
  2. Add an `HttpClient` (typed `"github-oauth"`) for the token exchange and profile fetch. Register in `Program.cs`.
  3. Add a DI-injected `IInviteRepository`, `IUserRepository`, `IInstallationRepository`, `IJwtService`, `IConfiguration`.
  4. Deserialize state (the format must match whatever `GitHubAuth` emits — see Finding 009).
  5. Invite handling: look up by `InviteTokenHash` (after hashing the raw token from state; note Finding 021 regression), validate not accepted + not expired, extract role, call `inviteRepo.MarkAcceptedAsync(invite.Id)`.
  6. Upsert user: call `userRepo.GetByGitHubIdAsync(profile.id)`; if null, `CreateAsync` with `EmailVerified=true`, `AuthMethod="github"`; else update login/email.
  7. If invite-assigned role differs from current role, update it.
  8. Link to installations: blocked by Finding 023 (`user_installations` table absent). Must be implemented as part of remediation.
  9. Mint JWT via `IJwtService.GenerateAccessToken`, set `tamma_session` cookie with `Domain=.tamma.dev`, `MaxAge=900`.
  10. Redirect to sanitized `rd` (from state) or `Dashboard:Url`.
- **Is it "just a stub" or is scope missing?** Literally a stub AND scope-missing: even if the code were written, some of the required repository methods don't exist (Finding 022, 023).
- **Blockers**: Finding 009 (state must be set by the start endpoint to deserialize here), Finding 021 (invite lookup semantics), Finding 022 (`GetByGitHubIdAsync` exists but `SetGitHubIdAsync` doesn't for the account-linking path), Finding 023 (no `user_installations` table), Finding 004 (cookie shape).

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs` (GitHubCallback), `Program.cs` (register `github-oauth` HttpClient), `appsettings.json` (`GitHub:ClientSecret`, `GitHub:CallbackUrl`).
- Files to create: `apps/tamma-elsa/src/Tamma.Api/Auth/OAuthStateCodec.cs` (encode/decode + validate + sanitize — pairs with Finding 009); `apps/tamma-elsa/src/Tamma.Api/Dtos/Auth/GitHubUser.cs`; `apps/tamma-elsa/src/Tamma.Api/Services/GitHubOAuthService.cs` (wraps the token-exchange + profile-fetch).
- Tests to add:
  - `GitHubCallback_ValidCode_CreatesNewUser_IssuesJwt_Redirects`.
  - `GitHubCallback_ExistingUser_LogsInAndRedirects`.
  - `GitHubCallback_WithValidInvite_AssignsRole_AcceptsInvite`.
  - `GitHubCallback_WithExpiredInvite_CreatesUserAsMember`.
  - `GitHubCallback_GitHubReturns4xx_RedirectsWithError`.
  - `GitHubCallback_MissingCode_RedirectsWithError`.
  - `GitHubCallback_InvalidState_FallsBackToDashboardUrl`.
  - `OAuthStateCodec_RoundTrip` (with malicious payload).
- Estimated effort: 10-14h
  - State codec + tests: 2h
  - GitHub token exchange + profile fetch HttpClient + tests (with WireMock): 3h
  - Callback orchestration + tests: 4h
  - Dependency on user_installations (Finding 023) blocker: 2-3h
  - End-to-end cookie + redirect test: 1h
  - Buffer for integration work: 1-2h

## References

- TS source: `packages/api/src/routes/auth/github-oauth.ts:86-220` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:387-391`
- Story: `docs/stories/epic-18/18-2-user-login-session-management.md` (AC 5-7, Task 4); `docs/stories/epic-16/16-1-oauth2-proxy-unified-auth.md` (§286-299)
- Related findings: `009-oauth-state-csrf-missing.md`, `021-invite-token-raw-vs-hash.md`, `022-user-repository-missing-methods.md`, `023-user-installations-table-absent.md`, `004-session-cookie-payload-and-domain.md`

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: `e56b04d`
- **Notes**: GitHubCallback ports the primary paths: code→token, profile fetch, CSRF verify, invite hash-lookup with role assignment, user upsert (new + email-linking + GitHub placeholder email), JWT + cookie + sanitized rd. Installation-auto-link path skipped per admin-db decision (tenant_memberships replaces user_installations).
