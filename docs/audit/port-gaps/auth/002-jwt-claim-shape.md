# Finding 002: JWT claim shape incompatibility

**Scope**: auth
**Severity**: P0 (cutover-blocking)
**Status**: Behavioral drift (wire format incompatible with live cookies)
**Estimated port effort**: 2h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/auth/jwt.ts`.

- File: `packages/api/src/auth/jwt.ts:1-63`
- Contract: `UnifiedJwtPayload` is the single JWT claim shape produced by all three auth flows (email+password login, admin GitHub OAuth, end-user GitHub OAuth). Eight claims plus `iat`/`exp`.
- Key code:

```typescript
// packages/api/src/auth/jwt.ts:23-43 (9e9a57c~1)
export interface UnifiedJwtPayload {
  sub: string;                      // User UUID (primary identifier)
  tenantId: string | null;          // Active tenant/org ID
  role: TenantRole;                 // 'member' | 'admin' | 'owner' — role in the active tenant
  platformRole: PlatformRole;       // 'user' | 'platform_admin' — global
  email: string;
  name: string;                     // Display name
  authMethod: AuthMethod;           // 'email' | 'github' | 'both'
  iat: number;
  exp: number;
}

export function buildJwtClaims(
  userId: string, email: string, name: string,
  tenantId: string | null, tenantRole: TenantRole,
  platformRole: PlatformRole, authMethod: AuthMethod,
): Omit<UnifiedJwtPayload, 'iat' | 'exp'> {
  return { sub: userId, tenantId, role: tenantRole, platformRole, email, name, authMethod };
}
```

- Dependencies: `fastify-jwt` plugin signs with `expiresIn: '900s'` (15 min).
- Tests: `packages/api/src/routes/auth/login.test.ts` asserted shape including `platformRole`, `name`, `authMethod`.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Auth/JwtService.cs:29-51`
- Contract: Emits six claims: `sub`, `tid`, role (as `ClaimTypes.Role` which serializes to `http://schemas.microsoft.com/ws/2008/06/identity/claims/role`), `email`, `jti`, `iat`. 15-minute expiry.
- Key code:

```csharp
// apps/tamma-elsa/src/Tamma.Api/Auth/JwtService.cs:29-51
public string GenerateAccessToken(User user, Guid tenantId, string role)
{
    var claims = new[]
    {
        new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
        new Claim("tid", tenantId.ToString()),
        new Claim(ClaimTypes.Role, role),
        new Claim(JwtRegisteredClaimNames.Email, user.Email),
        new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        new Claim(JwtRegisteredClaimNames.Iat, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)
    };
    // ... HMAC-SHA256 with Jwt:Secret, issuer/audience from config, 15 min expiry ...
}
```

- Dependencies: `System.IdentityModel.Tokens.Jwt`.
- Tests: No test asserts the exact claim set.

## 3. The gap

Claim-by-claim diff:

| Claim | TS name | TS type | C# name | C# type | Status |
|---|---|---|---|---|---|
| User UUID | `sub` | string | `sub` | string | Match |
| Active tenant | `tenantId` | string\|null | `tid` | string (Guid.Empty if null) | **Renamed + null→empty-guid coercion** |
| Tenant role | `role` | `'member'\|'admin'\|'owner'` | `role` (via `ClaimTypes.Role` URI) | string | **Serializes under long URI, not bare `role`** |
| Platform role | `platformRole` | `'user'\|'platform_admin'` | — | — | **Missing** |
| Email | `email` | string | `email` | string | Match |
| Display name | `name` | string | — | — | **Missing** |
| Auth method | `authMethod` | `'email'\|'github'\|'both'` | — | — | **Missing** |
| Issued at | `iat` | number | `iat` | string | Match (type differs) |
| JWT ID | — | — | `jti` | string | **C# added** |

- TS did: return a payload the dashboard could read via `decoded.tenantId`, `decoded.role`, `decoded.platformRole`, `decoded.name`.
- C# does: emit a payload where `tenantId` doesn't exist (caller must read `tid`), `platformRole` doesn't exist, `name` doesn't exist, and `role` is hidden behind ASP.NET's `ClaimTypes.Role` URI unless the JSON serializer maps it.
- Caller-observable consequence: every currently-issued `tamma_session` cookie (signed with the TS key) is structurally valid (the signature still verifies if the secret is the same) but downstream code reading `.tenantId` sees `undefined`, reading `.name` sees `undefined`, reading `.platformRole` sees `undefined`. The dashboard top nav (Story 16-4) renders the user as anonymous; RBAC middleware that branched on `platformRole === 'platform_admin'` falls through. A cutover without a re-login forces every user into a degraded session.
- Even if everyone re-logs in after cutover: JWTs issued by C# lack `platformRole`, `name`, `authMethod`. Story 16-5 RBAC (which depends on `platformRole` per Story 18-3 section §294) is architecturally broken.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-2-user-login-session-management.md`
- Story AC 8 (line 20): *"JWT access token contains claims: `{ id, email, name, tenantId, role, authMethod }`; expires in 15 minutes"*.
- Technical Context §150-165 defines `UnifiedJwtPayload` with `{ sub, tenantId, role, platformRole, email, name, authMethod, iat, exp }` — explicitly seven non-time claims.
- Story alignment:
  - [x] Matches TS behavior (TS follows the story exactly)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

C# is a regression vs both the story AC and TS.

## 5. Status

- **Classification**: Behavioral drift (JWT is issued, just with the wrong shape).
- **What's needed to finish**:
  1. Rename the `tid` claim to `tenantId`; emit empty string/null literal for unassigned tenants rather than `Guid.Empty`.
  2. Add `platformRole` claim. Source: derive from `user.Role` — per TS `github-oauth.ts`, `'owner'` → `'platform_admin'`, else `'user'`. Or persist a dedicated column.
  3. Add `name` claim from `user.DisplayName ?? user.GitHubLogin ?? user.Email.Split('@')[0]`.
  4. Add `authMethod` claim from `user.AuthMethod`.
  5. Ensure the role claim emits as the literal string `"role"` in the JWT JSON, not `ClaimTypes.Role`'s URI. (Use the short claim type mapping `MapInboundClaims = false` + explicit `Claim("role", ...)`).
- **Is it "just a stub" or is scope missing?** Scope partially missing — the TS story required seven claims; C# writes four. This isn't a stub (it works end to end) but three story-required claims were never implemented.
- **Blockers**: Downstream consumers (dashboard `/api/auth/me`, unified nav, role-check) read specific claim names — any rename must land coordinated with client JS.

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Auth/JwtService.cs`, `Program.cs` (JWT middleware claim-type mapping).
- Files to create: None.
- Tests to add:
  - `JwtServiceTests.GenerateAccessToken_IncludesAllSevenRequiredClaims`.
  - `JwtServiceTests.TenantIdClaim_IsNull_WhenTenantIsEmpty`.
  - `JwtServiceTests.RoleClaim_IsShortName_NotUri`.
  - `AuthEndpointsTests.Login_IssuesJwtWithPlatformRoleClaim`.
- Estimated effort: 2h
  - Claim shape changes: 1h
  - Middleware claim-type mapping + tests: 1h

## References

- TS source: `packages/api/src/auth/jwt.ts` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Auth/JwtService.cs`
- Story: `docs/stories/epic-18/18-2-user-login-session-management.md` (AC 8, §150-176)
- Related findings: `004-session-cookie-payload-and-domain.md` (cookie carries this JWT), `011-get-me-reads-bearer-not-cookie.md` (reads these claims)
- CLAUDE.md section: "JWT payload contract" via `UnifiedJwtPayload`
