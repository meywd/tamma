# Finding 007: Refresh token rotation broken (no revoke, no new refresh, no reuse detection)

**Scope**: auth
**Severity**: P0 (cutover-blocking)
**Status**: Incomplete (three of four steps missing)
**Estimated port effort**: 3h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/auth/login.ts`.

- File: `packages/api/src/routes/auth/login.ts:206-276` (POST /api/v1/auth/refresh).
- Contract: Single-use refresh tokens with family-revoke-on-reuse. On every refresh call the server (1) looks up the token by SHA-256 hash, (2) if revoked → assumes compromise → revokes *all* refresh tokens for that user (family revoke) and returns 401, (3) checks expiry, (4) revokes the presented token, (5) mints a new access JWT AND a new refresh token, (6) stores the new refresh hash, (7) returns both.
- Key code:

```typescript
// packages/api/src/routes/auth/login.ts:218-276 (9e9a57c~1)
app.post('/api/v1/auth/refresh', async (request, reply) => {
  const { refreshToken } = request.body ?? {};
  if (!refreshToken) return reply.status(400).send({ error: 'refreshToken is required' });

  const tokenHash = createHash('sha256').update(refreshToken).digest('hex');
  const storedToken = await refreshTokenStore.getTokenByHash(tokenHash);

  if (!storedToken) return reply.status(401).send({ error: 'Invalid refresh token' });

  // Token reuse detection — revoke all for the user
  if (storedToken.revokedAt !== null) {
    await refreshTokenStore.revokeAllForUser(storedToken.userId);
    request.log.warn({
      event: 'USER.REFRESH_TOKEN_REUSE',
      userId: storedToken.userId,
    }, 'Refresh token reuse detected — all sessions revoked');
    return reply.status(401).send({ error: 'Refresh token has been revoked' });
  }

  if (new Date(storedToken.expiresAt) < new Date()) {
    return reply.status(401).send({ error: 'Refresh token has expired' });
  }

  // Revoke the old token (rotation)
  await refreshTokenStore.revokeToken(storedToken.id);

  // ... resolve user, build claims ...

  const accessToken = app.jwt.sign(claims as Record<string, unknown>);

  // Generate new refresh token
  const newRawRefreshToken = randomBytes(32).toString('hex');
  const newRefreshTokenHash = createHash('sha256').update(newRawRefreshToken).digest('hex');
  const newRefreshExpiresAt = new Date(Date.now() + refreshTokenExpiresIn * 1000).toISOString();

  await refreshTokenStore.createToken(user.id, newRefreshTokenHash, newRefreshExpiresAt);

  reply.setCookie('tamma_session', accessToken, { /* ... */ });
  return reply.send({ accessToken, refreshToken: newRawRefreshToken });
});
```

- Dependencies: `IRefreshTokenStore.revokeToken`, `revokeAllForUser`, `createToken`, `getTokenByHash`.
- Tests: `packages/api/src/routes/auth/login.test.ts` (not visible in directory listing but referenced) covers reuse-detection.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:233-261`.
- Contract: Look up token by hash → check not-null, not-revoked, not-expired → issue a new access token. Does not revoke the presented token. Does not issue a new refresh token. Has no reuse-detection branch.
- Key code (29 lines total; the entire method):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:233-261
public static async Task<IResult> Refresh(
    IRefreshTokenRepository refreshTokenRepo,
    IJwtService jwtService,
    IUserRepository userRepo,
    ITenantMembershipRepository membershipRepo,
    HttpContext httpContext)
{
    var refreshToken = httpContext.Request.Cookies["tamma_session"];
    if (string.IsNullOrEmpty(refreshToken))
        return Results.Unauthorized();

    var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken))).ToLowerInvariant();
    var token = await refreshTokenRepo.GetByTokenHashAsync(tokenHash);

    if (token is null || token.RevokedAt is not null || token.ExpiresAt < DateTime.UtcNow)
        return Results.Unauthorized();

    var user = token.User;
    var tenantId = user.TenantId ?? Guid.Empty;
    var role = "member";
    if (tenantId != Guid.Empty)
    {
        var memberRole = await membershipRepo.GetRoleAsync(tenantId, user.Id);
        if (memberRole is not null) role = memberRole;
    }

    var accessToken = jwtService.GenerateAccessToken(user, tenantId, role);
    return Results.Ok(new RefreshResponse(accessToken, 900));
}
```

- Dependencies: `IRefreshTokenRepository.GetByTokenHashAsync`. `RevokeAsync` exists but is not called here; `RevokeAllForUserAsync` exists but is not called.
- Tests: None assert rotation or reuse detection.

## 3. The gap

Four semantic gaps on one endpoint.

1. **No rotation (old token not revoked)**: After `Refresh`, the original refresh token is still active. A stolen token remains valid for 7 days of repeated refreshes.
2. **No new refresh issued**: The response `RefreshResponse(accessToken, 900)` carries only the new access token. The client cannot rotate. Over time, the client's refresh token ages until expiry (7 days), then the session dies silently — there is no rolling-window extension.
3. **No reuse detection**: The single most important property of single-use refresh tokens (as the story's AC 10 requires) is that presenting a revoked token → assume compromise → revoke the user's entire token family. C# has no such branch: if the token is already revoked, it returns a bare 401 with no side effects.
4. **Input source**: TS read from the request body; C# reads from `tamma_session` cookie — which (per Finding 004) is itself the refresh token. Inconsistent with the TS-documented body shape.

For an attacker who stole a refresh token (via an XSS, a stale developer laptop, a leaked browser backup):
- TS: The moment the legitimate user refreshes, the attacker's copy becomes invalid. On the *next* attacker-initiated refresh, the server sees `revokedAt != null`, revokes every active token for that user, and both parties are logged out. The user notices; IR workflow triggers.
- C#: The attacker can keep refreshing indefinitely. The legitimate user's tokens are also still valid. Both parties coexist until the raw expiry date (7 days). No alert.

Error paths:
- TS: 400 "refreshToken is required", 401 "Invalid refresh token", 401 "Refresh token has been revoked" (after family-revoke), 401 "Refresh token has expired", 401 "User not found".
- C#: 401 Unauthorized (bare, for any failure reason — no message).

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-2-user-login-session-management.md`
- Story AC 9 (line 21): *"Refresh token is an opaque token stored in DB (not a JWT); expires in 7 days; single-use (rotation on refresh)"*.
- Story AC 10 (line 22): *"Token refresh endpoint `POST /api/v1/auth/refresh` accepts `{ refreshToken }`, returns new access+refresh token pair, invalidates old refresh token"*.
- Security section (line 180): *"Refresh token rotation: Each refresh invalidates the previous token; reuse of an old token revokes ALL tokens for that user (compromise detection)"*.
- Subtask 5.4-5.5 (line 75-76): *"Revoke old refresh token (single-use rotation) / Generate new access + refresh token pair"*.
- Implementation Notes (line 189): *"Refresh token rotation is critical: if a stolen refresh token is reused, all sessions for that user should be revoked (family rotation pattern)"*.
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

Story is explicit about the family-rotation pattern. C# skipped it.

## 5. Status

- **Classification**: Incomplete. Lookup + expiry-check + JWT-mint is present; rotation, reuse-detection, and new-refresh-issuance are missing.
- **What's needed to finish**:
  1. After the `GetByTokenHashAsync` lookup, if `token.RevokedAt is not null`, call `refreshTokenRepo.RevokeAllForUserAsync(token.UserId)`, log a warn with `USER.REFRESH_TOKEN_REUSE`, and return 401 "Refresh token has been revoked".
  2. On success path, call `refreshTokenRepo.RevokeAsync(token.Id)` to rotate.
  3. Generate a new refresh token: `Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()` then SHA-256-hash it, call `refreshTokenRepo.CreateAsync(user.Id, newHash, DateTime.UtcNow.AddDays(7))`.
  4. Return both tokens in `RefreshResponse` — extend the DTO to include `RefreshToken`.
  5. Change the input source from `Cookies["tamma_session"]` to `req.RefreshToken` (per body-based contract).
  6. Transactionally wrap revoke + create (avoid a window where neither is valid).
- **Is it "just a stub" or is scope missing?** Scope was partially understood (lookup is there) but three of the four required actions were dropped.
- **Blockers**: Finding 004 (cookie) — the input source change depends on the cookie being the access JWT again, not the refresh token.

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs` (Refresh), `apps/tamma-elsa/src/Tamma.Api/Dtos/Auth/RefreshResponse.cs` (add refresh token), `apps/tamma-elsa/src/Tamma.Api/Dtos/Auth/RefreshRequest.cs` (new DTO if not present).
- Files to create: `RefreshRequest.cs` if missing.
- Tests to add:
  - `AuthEndpointsTests.Refresh_ValidToken_IssuesNewAccessAndRefresh`.
  - `AuthEndpointsTests.Refresh_ValidToken_RevokesOldRefresh`.
  - `AuthEndpointsTests.Refresh_RevokedToken_RevokesAllUserSessions`.
  - `AuthEndpointsTests.Refresh_ExpiredToken_Returns401`.
  - `AuthEndpointsTests.Refresh_InvalidToken_Returns401`.
- Estimated effort: 3h
  - Logic changes: 1h
  - DTO extensions: 0.5h
  - Test suite (5 cases): 1.5h

## References

- TS source: `packages/api/src/routes/auth/login.ts:206-276` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:233-261`
- Story: `docs/stories/epic-18/18-2-user-login-session-management.md` (AC 9-10, §180, subtask 5.4-5.5)
- Related findings: `004-session-cookie-payload-and-domain.md` (source of refresh-token location)

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: `e56b04d`
- **Notes**: Refresh now: detects reuse (revoked-token replay → revoke entire user family + WARN log), revokes presented token, mints+persists new refresh, returns both tokens, updates session cookie.
