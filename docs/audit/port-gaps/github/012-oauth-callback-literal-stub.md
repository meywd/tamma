# Finding 012: OAuth callback is a literal stub — entire flow not implemented

**Scope**: github
**Severity**: P0 (cutover-blocking)
**Status**: Not-yet-implemented (stub)
**Estimated port effort**: 10-14h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/auth/github-oauth.ts`.

- File: `packages/api/src/routes/auth/github-oauth.ts:97-216`
- Contract/behavior: On `GET /api/auth/github/callback?code=X&state=Y`, TS executed the full OAuth completion dance: parse state, exchange code for access token, fetch GitHub user profile, apply invite (if present), upsert the user, auto-link to installations if none exist, issue a JWT, set an HTTP-only cookie on `.tamma.dev`, and redirect to the sanitized `rd` (or the dashboard default).

```typescript
// packages/api/src/routes/auth/github-oauth.ts:97-216 (9e9a57c~1)
  // -------------------------------------------------------------------
  // GET /api/auth/github/callback — exchange code, create/update user, issue JWT
  // -------------------------------------------------------------------
  app.get<{
    Querystring: { code?: string; error?: string; state?: string };
  }>('/api/auth/github/callback', async (request, reply) => {
    const { code, error, state } = request.query;

    if (error || !code) {
      return reply.redirect(`${dashboardUrl}/login?error=${encodeURIComponent(error ?? 'missing_code')}`);
    }

    // Exchange code for access token
    let accessToken: string;
    try {
      const tokenResponse = await fetch('https://github.com/login/oauth/access_token', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          Accept: 'application/json',
        },
        body: JSON.stringify({
          client_id: clientId,
          client_secret: clientSecret,
          code,
        }),
      });

      const tokenData = (await tokenResponse.json()) as { access_token?: string; error?: string };
      if (!tokenData.access_token) {
        return reply.redirect(`${dashboardUrl}/login?error=token_exchange_failed`);
      }
      accessToken = tokenData.access_token;
    } catch {
      return reply.redirect(`${dashboardUrl}/login?error=github_unavailable`);
    }

    // Fetch GitHub user profile
    let githubUser: { id: number; login: string; email: string | null };
    try {
      const userResponse = await fetch('https://api.github.com/user', {
        headers: { Authorization: `Bearer ${accessToken}`, Accept: 'application/json' },
      });
      githubUser = (await userResponse.json()) as typeof githubUser;
    } catch {
      return reply.redirect(`${dashboardUrl}/login?error=github_user_fetch_failed`);
    }

    // Parse OAuth state to extract redirect and invite token
    let redirectTo: string | null = null;
    let inviteToken: string | null = null;
    if (state) {
      try {
        const parsed = JSON.parse(Buffer.from(state, 'base64url').toString()) as { rd?: string; invite?: string };
        if (parsed.rd) {
          redirectTo = sanitizeRedirectUrl(parsed.rd);
        }
        if (parsed.invite) {
          inviteToken = parsed.invite;
        }
      } catch {
        // Invalid state — fall back to defaults
      }
    }

    // Determine role from invite token if present
    let assignedRole: 'owner' | 'admin' | 'member' = 'member';
    if (inviteToken && inviteStore) {
      const invite = await inviteStore.getInviteByToken(inviteToken);
      if (invite && invite.acceptedAt === null && invite.expiresAt > new Date().toISOString()) {
        assignedRole = invite.role;
        await inviteStore.acceptInvite(invite.id);
        request.log.info({
          event: 'USER.INVITE_ACCEPTED.SUCCESS',
          inviteId: invite.id,
          role: invite.role,
          githubLogin: githubUser.login,
        }, 'Invite accepted during OAuth callback');
      }
    }

    // Upsert user in our store
    const user = await userStore.upsertUser({
      githubId: githubUser.id,
      githubLogin: githubUser.login,
      email: githubUser.email,
      role: assignedRole,
    });

    // If invite assigned a non-default role and user already existed with 'member',
    // explicitly promote them (upsert may not change role on conflict)
    if (assignedRole !== 'member' && user.role !== assignedRole) {
      await userStore.updateUserRole(user.id, assignedRole);
      user.role = assignedRole;
    }

    // Check if user has access to any installation
    const installations = await userStore.getUserInstallations(user.id);
    if (installations.length === 0) {
      // Auto-link: check if any installation matches the user's GitHub orgs
      // For now, link to all active installations (first-user-gets-access bootstrap)
      const allInstallations = await installationStore.listActiveInstallations();
      for (const inst of allInstallations) {
        await userStore.linkUserToInstallation(user.id, inst.installationId, 'member');
      }
    }

    // Issue JWT
    const token = app.jwt.sign({
      id: user.id,
      username: user.githubLogin,
      githubId: user.githubId,
      role: user.role,
    });

    // Single cookie on parent domain — covers all *.tamma.dev subdomains.
    // Browsers reject Set-Cookie for domains that don't match the current origin,
    // so per-subdomain cookies from api.tamma.dev would be silently dropped.
    reply.setCookie('tamma_session', token, {
      path: '/',
      httpOnly: true,
      secure: true,
      sameSite: 'lax' as const,
      maxAge: tokenExpiresIn,
      domain: '.tamma.dev',
    });

    // Use the sanitized URL if valid, otherwise fall back to the server-controlled dashboardUrl
    return reply.redirect(redirectTo ?? dashboardUrl);
  });
```

- Dependencies: `fetch`, `@fastify/jwt`, `@fastify/cookie`, `IUserStore`, `IGitHubInstallationStore`, `IInviteStore`, `sanitizeRedirectUrl`.
- Tests that exercised this: integration tests using `msw` stubbed `https://github.com/login/oauth/access_token` and `https://api.github.com/user`, asserted the `tamma_session` cookie was set with correct attributes, asserted the redirect target was the sanitized `rd`, asserted invite acceptance end-to-end.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:387-391`
- Contract/behavior: Literal stub. Returns a JSON body explaining it's not implemented.

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:387-391 (current)
public static Task<IResult> GitHubCallback()
{
    // TODO: Implement GitHub OAuth callback
    return Task.FromResult(Results.Ok(new { message = "GitHub callback - not yet implemented" }));
}
```

That is the entire body. No argument binding, no context, no HTTP client, no cookie, no JWT, no user upsert.

- Dependencies: none (doesn't depend on anything).
- Tests: no integration test exercises this because the output is a known-stub response.

## 3. The gap

The contrast is dramatic. Quoting the two side-by-side:

**TS**: 120-line handler exchanging tokens, fetching profiles, applying invites, upserting users, auto-linking installations, signing JWTs, setting cookies on the parent domain, honoring sanitized post-login redirects.

**C#**:
```csharp
public static Task<IResult> GitHubCallback()
{
    // TODO: Implement GitHub OAuth callback
    return Task.FromResult(Results.Ok(new { message = "GitHub callback - not yet implemented" }));
}
```

- TS did: complete end-to-end OAuth login, resulting in an authenticated session usable across `*.tamma.dev` subdomains.
- C# does: returns a stub body, no session, no cookie, no user record created.
- For a caller whose browser is redirected from GitHub to `/api/auth/github/callback?code=gho_xyz`, TS issued a session cookie and redirected to the dashboard or the stored `rd`. C# returns `200 OK` with body `{"message":"GitHub callback - not yet implemented"}`. The browser remains unauthenticated. Subsequent requests to any protected endpoint return 401 because no `tamma_session` cookie was ever set.
- In production with existing data / deployed clients, this means: **GitHub OAuth login is entirely broken**. Users cannot log in via the `/api/auth/github` flow. `GET /api/auth/me` (at `Program.cs:332`) always returns 401 because the cookie is never issued. The admin dashboard auth flow is dead.

Error paths:
- TS error path: many — `error=access_denied`, `token_exchange_failed`, `github_unavailable`, `github_user_fetch_failed`, each resulting in a redirect to `/login?error=...` so the UI can surface a message.
- C# error path: always `200 OK` with the stub body, regardless of whether `code` is present or the user actually completed OAuth. Completely nonsensical for an OAuth callback.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-2-user-login-session-management.md`
- Story's acceptance criteria for this behavior:
  > AC #5: "**GitHub OAuth login** endpoint `GET /api/v1/auth/github` initiates OAuth flow, `GET /api/v1/auth/github/callback` completes it"
  > Task 4.3: "Create `GET /api/v1/auth/github/callback` to exchange code for token"
  > Task 4.4: "Fetch GitHub user profile + verified emails"
  > Task 4.5-4.6: (not shown in snippet but implied) user upsert, JWT issuance, cookie set
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS) — emphatically. Both TS and the story spec the full flow.
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story — spec gap

Additionally CLAUDE.md's `Security Requirements → Credential Management` demands HTTPS/TLS, cookie hygiene, and no credentials in logs — any implementation must honor these.

## 5. Status

- **Classification**: Not-yet-implemented (stub). Literal: the TODO is right there in the code.
- **What's needed to finish**:
  1. Change signature to `public static async Task<IResult> GitHubCallback(HttpContext context, IConfiguration config, IUserRepository users, IInviteRepository invites, IInstallationRepository installations, IOptions<JwtOptions> jwtOptions, ILogger<...> logger, CancellationToken ct)`.
  2. Parse query: `code`, `error`, `state`.
  3. If `error` present or `code` missing → redirect to `${Dashboard:Url}/login?error=...`.
  4. Read and verify CSRF state cookie (paired with Finding 009); extract `rd` + `invite` from state payload.
  5. Token exchange via `HttpClient` POST to `https://github.com/login/oauth/access_token` with `Accept: application/json`. Use `IHttpClientFactory` + a named client; register in Program.cs.
  6. Fetch user profile via `HttpClient` GET to `https://api.github.com/user` with bearer token. Also fetch `/user/emails` to find verified primary email (see story 18-2 Task 4.4).
  7. Apply invite: look up by token via `IInviteRepository`; if valid and not expired/accepted, pin role; mark accepted.
  8. Upsert user: match on `githubId`; populate `GitHubLogin`, `Email`, `Role`, `AuthMethod = 'github_oauth'`.
  9. Issue JWT matching the `UnifiedJwtPayload` contract from story 18-2 (fields: `sub`, `email`, `githubId`, `githubLogin`, `tenantId`, `platformRole`, `iat`, `exp`).
  10. Set cookie `tamma_session` on `.tamma.dev`, `Secure`, `HttpOnly`, `SameSite=Lax`.
  11. Redirect to sanitized `rd ?? Dashboard:Url`.
  12. Emit `USER.LOGIN.SUCCESS` domain event via `IEventRepository`.
- **Is it "just a stub" or is scope missing?** Pure stub. The TODO comment is explicit. Scope was well understood; implementation was simply skipped during the port phase.
- **Blockers**:
  - Finding 009 (state) must land first or alongside; the callback validates state.
  - Finding 011 (rd/invite start-side) must also be wired.
  - Minor: cookie policy config / `CookiePolicyOptions` must allow `SameSite=Lax` cross-subdomain.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:387-391` — replace stub with full implementation (~100-150 lines of C#).
  - `apps/tamma-elsa/src/Tamma.Api/Program.cs` — register named `HttpClient` for GitHub OAuth, ensure JWT + cookie services are configured.
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Api/Services/Auth/GitHubOAuthClient.cs` — wraps token exchange + profile fetch + email fetch.
  - `apps/tamma-elsa/src/Tamma.Api/Services/Auth/SessionTokenIssuer.cs` — encapsulates the JWT-sign + cookie-set flow (reusable with email/password login from story 18-2).
  - Integration test fixture using `WireMock.Net` to stub GitHub endpoints.
- Tests to add:
  - `AuthEndpointsTests.GitHubCallback_InvalidState_RedirectsToLoginError`
  - `AuthEndpointsTests.GitHubCallback_TokenExchangeFail_RedirectsToLoginError`
  - `AuthEndpointsTests.GitHubCallback_Success_SetsSessionCookie`
  - `AuthEndpointsTests.GitHubCallback_Success_RedirectsToSanitizedRd`
  - `AuthEndpointsTests.GitHubCallback_WithInvite_AppliesRoleAndMarksAccepted`
  - `AuthEndpointsTests.GitHubCallback_NewUser_CreatesUserRow`
  - `AuthEndpointsTests.GitHubCallback_ExistingUser_UpdatesRow`
  - `AuthEndpointsTests.GitHubCallback_CookieDomain_IsParentTammaDev`
  - `AuthEndpointsTests.GitHubCallback_JwtPayload_MatchesUnifiedContract`
- Estimated effort: 10-14h broken down as:
  - GitHubOAuthClient + tests: 3h
  - SessionTokenIssuer + tests: 2h
  - Callback endpoint glue + state validation: 3h
  - Invite + auto-link flow: 2h
  - Integration tests (9 cases): 3-4h

## References

- TS source: `packages/api/src/routes/auth/github-oauth.ts:97-216` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:387-391`
- Story: `docs/stories/epic-18/18-2-user-login-session-management.md` (AC #5, Task 4, Security Considerations)
- Related findings: `009-oauth-start-no-csrf-state.md`, `010-oauth-start-missing-read-user-scope.md`, `011-oauth-start-no-rd-invite.md`
- CLAUDE.md section: `Security Requirements`

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Already-fixed
- **Commit**: `e56b04d` (auth scope)
- **Notes**: `AuthEndpoints.GitHubCallback` is fully implemented in auth scope: parses `code`/`state`/`error`, verifies CSRF cookie ↔ state.csrf, exchanges the code via `IGitHubOAuthService.ExchangeCodeForTokenAsync`, fetches the profile via `GetUserProfileAsync`, applies invite role via `IInviteRepository`, upserts user (matching by GitHub id then email for account-linking), generates JWT + refresh token, sets the `tamma_session` cookie on the configured cookie domain, and redirects to the sanitized `rd` (or dashboard default). One contract drift to flag: the C# uses `IGitHubOAuthService` (token exchange + `/user` only) and does not also fetch `/user/emails` for verified email; non-public-email GitHub users get a placeholder `<id>+<login>@users.noreply.github.com`. This matches Story 18-1 AC 26 (Email NOT NULL) but is a slight functional drift from TS that fetched `/user/emails`. Acceptable trade-off.
