# Finding 011: `GET /api/auth/me` reads Bearer header, not cookie; response shape changed

**Scope**: auth
**Severity**: P1 (feature broken)
**Status**: Behavioral drift
**Estimated port effort**: 3h

## 1. What's in TS

Pre-delete snapshots at `git show 9e9a57c~1:packages/api/src/routes/auth/me-route.ts` and `github-oauth.ts:212-221`.

- File: `packages/api/src/routes/auth/me-route.ts:1-69`.
- Contract: Verify JWT in `tamma_session` cookie (set with `domain=.tamma.dev`). Return the decoded payload wrapped as `{ user }`. The endpoint is designed to be called from any `*.tamma.dev` subdomain's navigation bar via `fetch('/api/auth/me', { credentials: 'include' })`.
- Key code:

```typescript
// packages/api/src/routes/auth/me-route.ts:55-65 (9e9a57c~1)
app.get('/api/auth/me', async (request, reply) => {
  try {
    const decoded = await request.jwtVerify<AuthMeUser>();
    return reply.send({ user: decoded });
  } catch {
    return reply.status(401).send({ error: 'Not authenticated' });
  }
});
```

- The `AuthMeUser` shape (`me-route.ts:32-37`):
  ```typescript
  { id: string; username: string; githubId: number; role: string }
  ```
- The JWT plugin is registered with `cookie: { cookieName: 'tamma_session', signed: false }` — so `jwtVerify()` reads from the cookie automatically.
- Tests: Referenced in Story 16-4 impl plan §189-192:
  > 13. Returns `{ user }` with valid JWT cookie | 200, payload shape
  > 15. Returns 401 when JWT invalid | Error message
  > 16. Returns 401 when JWT expired | Error message

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:350-368`.
- Contract: Read `ClaimTypes.NameIdentifier` from the ASP.NET-populated `ClaimsPrincipal` (which is set by `JwtBearerDefaults.AuthenticationScheme` — i.e. the `Authorization: Bearer` header). Look up the user by id. Return a flattened object with id, email, display name, role, tenantId, and full memberships list.
- Key code:

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:350-368
public static async Task<IResult> GetMe(
    ClaimsPrincipal principal,
    IUserRepository userRepo,
    ITenantMembershipRepository membershipRepo)
{
    var userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    if (userId is null || !Guid.TryParse(userId, out var id))
        return Results.Unauthorized();

    var user = await userRepo.GetByIdAsync(id);
    if (user is null)
        return Results.NotFound(new { error = "User not found" });

    var memberships = await membershipRepo.GetUserTenantsAsync(id);
    var membershipInfos = memberships.Select(m =>
        new MembershipInfo(m.TenantId, m.Tenant?.Name ?? "", m.Role)).ToList();

    return Results.Ok(new MeResponse(user.Id, user.Email, user.DisplayName, user.Role, user.TenantId, membershipInfos));
}
```

- Registered with `RequireAuthorization("MemberAccess")` at `Program.cs:332`, which uses the default JWT scheme (Bearer header) only.
- Dependencies: `IUserRepository`, `ITenantMembershipRepository`.
- Tests: No dashboard-nav integration test.

## 3. The gap

Three regressions bundled:

1. **Auth source**: TS reads JWT from cookie; C# reads from `Authorization: Bearer` header via the default JWT scheme. The unified nav at `app.tamma.dev`, `elsa.tamma.dev`, `logs.tamma.dev` calls `fetch('/api/auth/me', { credentials: 'include' })` — this attaches the cookie but no Authorization header. ASP.NET's JWT middleware doesn't look at cookies by default, so the principal is unauthenticated, and `RequireAuthorization("MemberAccess")` returns 401.
2. **Response shape**: TS returned `{ user: { id, username, githubId, role } }` — four fields under a wrapper. C# returns `new MeResponse(user.Id, user.Email, user.DisplayName, user.Role, user.TenantId, membershipInfos)` — six fields, flat (no wrapper). Fields renamed (`username` → no equivalent; `githubId` → absent; `DisplayName` is new; `TenantId` is new; `memberships` is new). Any dashboard code doing `response.user.username` or `response.user.githubId` reads `undefined`.
3. **404-on-missing-user**: TS never 404s — if JWT is valid, data is the JWT payload. C# does a DB lookup and can 404 if the user was soft-deleted between JWT issue and this call. This is arguably *better* (reflects current state), but it changes the contract.

For a caller:
- Unified nav on `elsa.tamma.dev` → fetches `/api/auth/me` with cookie → C# sees no Bearer → 401 → nav renders user as anonymous.
- A dashboard that authenticates via local JS → calls `/api/auth/me` with `Authorization: Bearer <jwt>` → reads `response.user.username` → `undefined`.

Error paths:
- TS: 401 "Not authenticated".
- C#: 401 from MemberAccess policy, or 401 from inside (NameIdentifier missing), or 404 "User not found".

## 4. Gap from stories

- Referenced story: `docs/stories/epic-16/16-4-unified-navigation-impl-plan.md`
- §12: *"Both paths fetch user identity from `GET /api/auth/me` (already implemented in `packages/api/src/routes/auth/me-route.ts` — PR #328), which returns `{ user: { id, username, githubId, role } }` by verifying the `tamma_session` JWT cookie. Because the cookie is set with `domain=.tamma.dev`, it is transmitted cross-subdomain automatically."*
- §189-192 test table specifies status codes and payload shape.
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

Story is explicit about both the cookie source and the `{ user: { id, username, githubId, role } }` shape.

## 5. Status

- **Classification**: Behavioral drift — endpoint exists, reads a different source, returns a different shape.
- **What's needed to finish**:
  1. Add a cookie-based JWT authentication scheme to `Program.cs`: either configure `JwtBearerOptions.Events.OnMessageReceived` to pull from `Request.Cookies["tamma_session"]`, or register a separate `CookieJwt` scheme and stack policies on both.
  2. Change the `RequireAuthorization` policy to accept the cookie scheme.
  3. Ensure the JWT middleware populates `NameIdentifier` from the `sub` claim (`MapInboundClaims = false`).
  4. Change the response to match `{ user: { id, username, githubId, role } }` (or deliberately update the story).
  5. Return 401 (not 404) if the user is missing — preserves the TS semantic that JWT-valid implies identity-known.
- **Is it "just a stub" or is scope missing?** Scope was understood (endpoint exists) but reimagined. It's drift.
- **Blockers**: Finding 004 (cookie must actually be the JWT), Finding 002 (claim names so the nav can read expected fields).

## Remediation

- Files to modify: `Program.cs` (JWT scheme config), `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs` (GetMe), `apps/tamma-elsa/src/Tamma.Api/Dtos/Auth/MeResponse.cs` (align shape).
- Files to create: None.
- Tests to add:
  - `GetMe_WithValidCookie_ReturnsWrappedUser`.
  - `GetMe_WithBearer_AlsoWorks` (keep Bearer path for programmatic callers).
  - `GetMe_NoAuth_Returns401`.
  - `GetMe_ExpiredJwt_Returns401`.
  - `GetMe_ResponseWrapperShapeMatchesStory` — asserts top-level `user` key and the four nested fields.
- Estimated effort: 3h
  - Cookie scheme: 1h
  - Endpoint + DTO rework: 1h
  - Tests: 1h

## References

- TS source: `packages/api/src/routes/auth/me-route.ts`, `packages/api/src/routes/auth/github-oauth.ts:212-221` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:350-368`
- Story: `docs/stories/epic-16/16-4-unified-navigation-impl-plan.md` (§12, §189-192)
- Related findings: `002-jwt-claim-shape.md`, `004-session-cookie-payload-and-domain.md`, `010-role-check-service-to-permission-map.md`
