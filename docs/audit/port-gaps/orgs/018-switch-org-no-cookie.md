# Finding 018: `POST /auth/switch-org` — Does Not Set `tamma_session` Cookie

**Scope**: orgs
**Severity**: P1 (feature broken for dashboard)
**Status**: Behavioral drift (cookie side-effect dropped)
**Estimated port effort**: 2h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/orgs/index.ts`.

- File: `packages/api/src/routes/orgs/index.ts:607-668`.
- Contract/behavior: verify membership in target tenant, update `users.tenant_id`, build fresh JWT claims with the new `tenantId` + the user's role in that tenant, sign the JWT, **set the `tamma_session` httpOnly secure cookie** on `.tamma.dev` with a 900s TTL and `SameSite=Lax`, and return the access token in JSON. The dashboard at `app.tamma.dev` relies on this cookie: subsequent API calls automatically use the new tenant context because the browser attaches the cookie to every request.
- Key code (verbatim quote, annotated):

```typescript
// packages/api/src/routes/orgs/index.ts (9e9a57c~1) L625-L667
// Verify membership in target tenant
const membership = await membershipStore.getMembership(tenantId, jwt.sub);
if (!membership) {
  return reply.status(403).send({ error: 'Not a member of the target organization' });
}

// Update active tenant
await userStore.updateActiveTenant(jwt.sub, tenantId);

// Issue new JWT with updated tenant
const user = await userStore.getUser(jwt.sub);
if (!user) {
  return reply.status(401).send({ error: 'User not found' });
}

const displayName = user.githubLogin || (user.email?.split('@')[0]) || 'User';
const claims = buildJwtClaims(
  user.id,
  user.email ?? '',
  displayName,
  tenantId,
  membership.role,
  user.role === 'owner' ? 'platform_admin' : 'user',
  user.authMethod,
);

const accessToken = app.jwt.sign(claims as Record<string, unknown>);

// Set session cookie with new JWT
reply.setCookie('tamma_session', accessToken, {
  path: '/',
  httpOnly: true,
  secure: true,
  sameSite: 'lax' as const,
  maxAge: 900,
  domain: '.tamma.dev',
});

return reply.send({
  accessToken,
  tenantId,
  role: membership.role,
});
```

- Dependencies: `@fastify/cookie` plugin registered earlier in the route (L72-L74); `buildJwtClaims`.
- Tests: asserted the `Set-Cookie: tamma_session=...; Domain=.tamma.dev; HttpOnly; Secure; SameSite=Lax; Max-Age=900` header.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs:145-165`, `apps/tamma-elsa/src/Tamma.Api/Program.cs:330`.
- Contract/behavior: verifies membership via `GetRoleAsync`, updates active tenant, calls `jwtService.GenerateAccessToken(user, req.TenantId, role)`, and returns `{ accessToken, expiresIn }` as JSON. **No cookie is written.** The `HttpContext` is injected (suggesting intent) but never used.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs (current) L145-L165
public static async Task<IResult> SwitchOrg(
    SwitchOrgRequest req,
    ITenantMembershipRepository membershipRepo,
    IUserRepository userRepo,
    IJwtService jwtService,
    ClaimsPrincipal principal,
    HttpContext httpContext)
{
    var userId = Guid.Parse(principal.FindFirst(ClaimTypes.NameIdentifier)!.Value);
    var role = await membershipRepo.GetRoleAsync(req.TenantId, userId);
    if (role is null)
        return Results.Json(new { error = "Not a member of this organization" }, statusCode: 403);

    var user = await userRepo.GetByIdAsync(userId);
    if (user is null) return Results.NotFound(new { error = "User not found" });

    await userRepo.UpdateActiveTenantAsync(userId, req.TenantId);
    var accessToken = jwtService.GenerateAccessToken(user, req.TenantId, role);

    return Results.Ok(new { accessToken, expiresIn = 900 });   // ← no httpContext.Response.Cookies.Append
}
```

- Dependencies: `IJwtService`, `HttpContext` (unused).
- Tests: none.

## 3. The gap

Concrete behavioral difference.

- TS did: after switch, the browser automatically sent the new JWT on every subsequent request via the `tamma_session` cookie. No dashboard JS change needed.
- C# does: only returns the token in JSON. The dashboard must manually replace the cookie (which it cannot do for httpOnly cookies from JS) or switch to bearer-token auth. Without cookie support the user appears to successfully switch orgs but the next page load uses the stale `tid` claim from the prior cookie.
- For a dashboard user at `app.tamma.dev` clicking "Switch to Acme": TS's cookie update → next request carries the new tenant; C#'s missing cookie → next request carries the old tenant. The UX appears to freeze on the original org.
- In production: this is the primary reason the dashboard's org switcher is non-functional. The frontend can store the token in JS and attach `Authorization: Bearer ...`, but this changes the project-wide auth model and is not how the TS dashboard was written.

Error paths:
- TS error path: `403 { "error": "Not a member of the target organization" }`, `401 { "error": "User not found" }`.
- C# error path: `403 { "error": "Not a member of this organization" }`, `404 { "error": "User not found" }` (status drift: 401 → 404).

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-3-organization-tenant-creation.md` and `docs/stories/epic-18/18-2-user-login-session-management.md`.
- Story's acceptance criteria for this behavior:
  - 18-3 AC 12: "**Tenant context in JWT**: After login, the JWT `tenantId` claim is set to the user's active tenant; a `POST /api/v1/auth/switch-org` endpoint allows switching".
  - 18-3 Implementation notes L145-L149: "Users switch tenants via `POST /api/v1/auth/switch-org`, which: 1. Validates the user is a member of the target tenant via `tenant_memberships` 2. Updates `users.tenant_id` to the new active tenant 3. Reissues the JWT with the new `tenantId`".
  - 18-2 (login-session-management) established the `tamma_session` cookie convention.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Behavioral drift.
- **What's needed to finish**:
  1. After `GenerateAccessToken`, call `httpContext.Response.Cookies.Append("tamma_session", accessToken, new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Domain = ".tamma.dev", MaxAge = TimeSpan.FromSeconds(900), Path = "/" });`.
  2. Consider making the domain configurable (dev vs prod — `.tamma.dev` only works in prod).
  3. Align status codes with TS (`401` for missing user; currently `404`).
  4. Add membership gate already handled by the existing `GetRoleAsync` call, but the status is 403 on `role is null` which matches.
- **Is it "just a stub" or is scope missing?** Scope defined in AC 12 and in the companion login story. The `HttpContext` parameter is a strong hint the author planned to write the cookie but didn't.
- **Blockers**: Need to confirm how dev/CI environments handle `Domain=.tamma.dev` (must fall back to no domain for localhost).

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs`, possibly `apps/tamma-elsa/src/Tamma.Api/Auth/JwtService.cs` or a new `SessionCookieWriter` helper.
- Files to create: `apps/tamma-elsa/src/Tamma.Api/Auth/SessionCookieWriter.cs`, `apps/tamma-elsa/tests/Tamma.Api.Tests/Orgs/SwitchOrgCookieTests.cs`.
- Tests to add:
  - `SwitchOrg_SetsTammaSessionCookie_WithExpectedAttributes`
  - `SwitchOrg_CookieAttributes_MatchProdProfile` (Secure, HttpOnly, SameSite=Lax, Domain=.tamma.dev, MaxAge=900)
  - `SwitchOrg_Returns403_WhenNotMember` (already covered, but ensures no cookie is set in that branch)
  - `SwitchOrg_Returns401_WhenUserNotFound` (aligned with TS)
  - `SwitchOrg_DoesNotSetCookie_InCliMode`
- Estimated effort: 2h broken down as:
  - Helper + wiring: 0.75h
  - Env-specific domain handling: 0.5h
  - Tests: 0.75h

## References

- TS source: `packages/api/src/routes/orgs/index.ts:607-668` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs:145-165`, `apps/tamma-elsa/src/Tamma.Api/Program.cs:330`
- Story: `docs/stories/epic-18/18-3-organization-tenant-creation.md` (AC 12), `docs/stories/epic-18/18-2-user-login-session-management.md`
- Related findings: `009-post-orgs-no-active-tenant-update.md`, `023-tenant-context-middleware-shallow.md`
