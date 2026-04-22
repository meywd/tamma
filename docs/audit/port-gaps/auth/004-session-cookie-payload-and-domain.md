# Finding 004: Session cookie contents and domain regression

**Scope**: auth
**Severity**: P0 (cutover-blocking)
**Status**: Semantic rewrite (cookie's purpose changed)
**Estimated port effort**: 2h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/auth/login.ts`.

- File: `packages/api/src/routes/auth/login.ts:180-189` (login); `:300-309` (refresh); `:322-326` (logout); `github-oauth.ts:194-203`.
- Contract: The `tamma_session` cookie carries the **access JWT** (not the refresh token). Its `domain` is `.tamma.dev` so it rides on every `*.tamma.dev` subdomain request (dashboard, elsa, logs, admin, api, wiki). Max-age matches the JWT's 15-minute expiry. The refresh token is returned in the response body, not the cookie.
- Key code:

```typescript
// packages/api/src/routes/auth/login.ts:180-189 (9e9a57c~1)
reply.setCookie('tamma_session', accessToken, {
  path: '/',
  httpOnly: true,
  secure: true,
  sameSite: 'lax' as const,
  maxAge: accessTokenExpiresIn,       // default 900s (15 min)
  domain: '.tamma.dev',
});

// response body carries the refresh token separately:
return reply.send({
  accessToken,
  refreshToken: rawRefreshToken,      // body-only; client stores in memory
  user: { ... },
});
```

```typescript
// packages/api/src/routes/auth/login.ts:322-326 (logout)
reply.clearCookie('tamma_session', { path: '/', domain: '.tamma.dev' });
```

- Dependencies: `@fastify/cookie`, `@fastify/jwt` with `cookie: { cookieName: 'tamma_session', signed: false }` config.
- Why domain matters: nginx at `elsa.tamma.dev` and `logs.tamma.dev` uses `auth_request` to hit `api.tamma.dev/api/auth/role-check`, which reads the `tamma_session` cookie via `request.jwtVerify()`. Without the `.tamma.dev` parent domain, the cookie doesn't cross subdomains.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:212-220`.
- Contract: The `tamma_session` cookie carries the **raw refresh token** (!). Its `MaxAge` is 7 days. There is no `Domain` set — the cookie defaults to the current origin (api.tamma.dev) only.
- Key code:

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:211-220 (Login)
await refreshTokenRepo.CreateAsync(user.Id, refreshHash, DateTime.UtcNow.AddDays(7));

// Set refresh token cookie
httpContext.Response.Cookies.Append("tamma_session", refreshToken, new CookieOptions
{
    HttpOnly = true,
    Secure = true,
    SameSite = SameSiteMode.Lax,
    Path = "/",
    MaxAge = TimeSpan.FromDays(7)
    // NOTE: no Domain set
});
```

- Refresh endpoint (`Refresh`, line 240) reads `httpContext.Request.Cookies["tamma_session"]` expecting a refresh token.
- Logout (line 276) calls `Cookies.Delete("tamma_session")` — without Domain, this only clears the api.tamma.dev-scoped cookie; if one with `Domain=.tamma.dev` also exists (legacy), it is not cleared.

## 3. The gap

Four independent regressions on a single cookie.

1. **Payload**: TS cookie = access JWT; C# cookie = raw refresh token. Any endpoint expecting to `jwtVerify` the cookie gets a 64-hex-char string that isn't a JWT at all. This directly breaks `GET /api/auth/me` (Finding 011) and the nginx role-check route (Finding 010).

2. **Domain**: TS `.tamma.dev` → available on every subdomain; C# unset → limited to whatever host served the `Set-Cookie`. If the API is at `api.tamma.dev`, the cookie won't arrive when the browser visits `elsa.tamma.dev` or `app.tamma.dev`.

3. **Max-age**: TS 900s (15 min, matches JWT expiry — cookie vanishes when the token would); C# 604800s (7 days, matches refresh). So even stale refresh tokens persist in the cookie for a week.

4. **Secret material exposure**: The refresh token is supposed to be a bearer credential with 7-day validity. Sticking it in a cross-request cookie (HttpOnly-protected but cross-path) is an unusual posture. In TS the refresh token lived in-memory only (returned in the JSON body; the client stored it in memory or a separate narrowly-scoped cookie). Putting it in `tamma_session` means a logout that clears `tamma_session` also invalidates the refresh — but if the JWT lived there too, now you can't have both at once.

For a caller calling `POST /api/v1/auth/login` and then navigating the browser to `elsa.tamma.dev`:
- TS: cookie attaches to the cross-subdomain request; `elsa.tamma.dev`'s nginx does `auth_request /api/auth/role-check?service=elsa`, which `jwtVerify`s and allows through.
- C#: cookie does not attach (wrong domain); nginx gets 401; user sees Elsa's 403 page — or, if they do attach it somehow, `jwtVerify` on a refresh-token string fails with "invalid JWT" and returns 401.

Error paths:
- TS `jwtVerify` failure (e.g. tampered token) → 401 "Not authenticated".
- C# reads refresh-token from cookie; on `/api/auth/me` it's using `RequireAuthorization("MemberAccess")` which expects `Authorization: Bearer`, not a cookie. The cookie is effectively ignored.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-2-user-login-session-management.md`
- Story AC 12 (line 24): *"Session cookie `tamma_session` set on `.tamma.dev` domain, `HttpOnly`, `Secure`, `SameSite=Lax`, 15-minute max-age (matches access token)"*.
- Related Story 16-4 (unified navigation) implementation plan §278: *"Cross-subdomain cookie: `tamma_session` is set by the GitHub OAuth callback (PR #328) with `Domain=.tamma.dev`, `HttpOnly`, `Secure`, `SameSite=Lax`. The nav script calling `/api/auth/me` with `credentials: 'include'` will attach it automatically across all `*.tamma.dev` subdomains."*
- Story 16-1 §22: *"The `tamma_session` JWT cookie (existing) coexists with the `_oauth2_proxy` cookie — the proxy handles 'who can access the page' while the JWT handles 'who are you for API calls'"*.
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

C# is a triple regression: payload, domain, and max-age all violate AC 12 explicitly. Story 16-4 also fails (cross-subdomain nav is architecturally blocked).

## 5. Status

- **Classification**: Semantic rewrite (the cookie was re-purposed from "access JWT for all subdomains" to "refresh token for this API origin only").
- **What's needed to finish**:
  1. Change `Login.cs:213` to set the cookie value to `accessToken` (the JWT), not `refreshToken`.
  2. Set `Domain = ".tamma.dev"` in `CookieOptions` (pull the domain from config so dev can use `localhost`).
  3. Set `MaxAge = TimeSpan.FromSeconds(900)` (or match the JWT expiry).
  4. Return `refreshToken` in the response body only (already done by `LoginResponse`? Check — currently the response returns `(accessToken, 900, new UserInfo(...))` with no refresh token at all — see note below).
  5. Update `Refresh` (line 240) to read the refresh token from the request body, not the cookie.
  6. Update `Logout` (line 276) to clear the cookie with `Domain=.tamma.dev` (use `Cookies.Append` with empty value + past expiry + matching Domain).
- **Subtle**: The current `LoginResponse(accessToken, 900, UserInfo)` omits the refresh token from the body entirely — so if the cookie is repurposed for JWT, the refresh token has NOWHERE to live. The remediation must also add a `refreshToken` field to `LoginResponse`.
- **Is it "just a stub" or is scope missing?** Scope was understood but a design decision was made to stuff refresh into the cookie. That decision contradicts AC 12. It's drift-verging-on-rewrite.
- **Blockers**: Finding 011 (`/api/auth/me`) and Finding 010 (role-check) both depend on the cookie carrying a JWT. Fix this first.

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs` (Login, Refresh, Logout), `apps/tamma-elsa/src/Tamma.Api/Dtos/Auth/LoginResponse.cs` (add refresh token), `appsettings.json` (add `Cookie:Domain` config).
- Files to create: None.
- Tests to add:
  - `AuthEndpointsTests.Login_SetsCookieWithJwtNotRefresh`.
  - `AuthEndpointsTests.Login_SetsCookieWithParentDomain`.
  - `AuthEndpointsTests.Login_CookieMaxAgeMatchesJwtExpiry`.
  - `AuthEndpointsTests.Logout_ClearsCookieWithMatchingDomain`.
  - `AuthEndpointsTests.Login_ReturnsRefreshTokenInBody`.
- Estimated effort: 2h
  - Cookie changes: 30m
  - Response DTO change: 30m
  - Tests: 1h

## References

- TS source: `packages/api/src/routes/auth/login.ts`, `packages/api/src/routes/auth/github-oauth.ts` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs`
- Story: `docs/stories/epic-18/18-2-user-login-session-management.md` (AC 12); `docs/stories/epic-16/16-4-unified-navigation-impl-plan.md` (§278)
- Related findings: `002-jwt-claim-shape.md`, `010-role-check-service-to-permission-map.md`, `011-get-me-reads-bearer-not-cookie.md`

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: `e56b04d`
- **Notes**: tamma_session cookie now carries the access JWT (not refresh), MaxAge=900s, Domain pulled from Cookie:Domain config (.tamma.dev in prod). LoginResponse adds RefreshToken to the body.
