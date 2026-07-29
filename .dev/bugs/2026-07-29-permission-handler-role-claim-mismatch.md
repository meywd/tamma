# PermissionHandler never matches production JWT role claims — every PermissionRequirement route is dead for real bearer tokens

**Date**: 2026-07-29
**Status**: ✅ Resolved (2026-07-29) — see "Resolution" below
**Found by**: Story 41-30's endpoint tests (all admin writes 403'd through the production-JWT test factory)

## The defect

Production JwtBearer options (`Tamma.Api/Program.cs` ~1457) set `MapInboundClaims = false` and
`TokenValidationParameters.RoleClaimType = "role"` — so a real bearer JWT's role arrives as the
bare claim type `"role"` (the shape `JwtService` mints). But `PermissionHandler`
(`src/Tamma.Api/Auth/PermissionHandler.cs:18`) matches `context.User.FindFirst(ClaimTypes.Role)`
— the long `http://schemas.microsoft.com/...` URI. The two never meet.

**Consequence**: every `PermissionRequirement`-gated policy — `PromptManage`, `AgentManage`,
`PricingManage`, `SettingsView`, `ScheduleManage`, … — returns 403 for every real bearer-JWT
user regardless of role. The only path through is `PermissionHandler`'s
`platformRole=platform_admin` superuser rule.

## Why it was never caught

- The existing RBAC suites pin `Permissions.HasPermission` handler-direct (e.g.
  `PricingByokRbacTests`) — they never cross the HTTP claim-mapping boundary.
- The one full-HTTP JWT precedent (`PlatformOwnerAccessPolicyTests`) only exercises the
  platform-admin superuser rule, which reads a different claim.
- Proxy-header identities (the deployed admin console) build their principal with
  `ClaimTypes.Role` directly, so the deployed UI works — the break is specific to bearer JWTs.

## The fix (follow-up, small)

`PermissionHandler` should resolve roles via `context.User.IsInRole(...)` (which respects each
identity's own `RoleClaimType` — bare `"role"` for JWT identities, `ClaimTypes.Role` for
proxy/cookie identities) instead of a hardcoded `FindFirst(ClaimTypes.Role)`. Ship with:
handler-direct tests for both claim shapes AND one over-HTTP test per shape (the 41-30 endpoint
fixture's dual-claim `MintToken` documents the quirk and is the template).

## Resolution (2026-07-29)

Role resolution now goes through `ClaimsPrincipal.IsInRole` via a new principal-shaped overload
`Permissions.HasPermission(ClaimsPrincipal, string)` (`src/Tamma.Api/Auth/Permissions.cs`) that
probes the closed role hierarchy (`member`/`admin`/`owner`) — claim-shape-agnostic (each identity's
own `RoleClaimType` wins: bare `"role"` for JWT identities, `ClaimTypes.Role` for default-built
identities) and still fail-closed for unknown role values.

**Sites fixed (authorization-gating):**
- `src/Tamma.Api/Auth/PermissionHandler.cs` — the platform-wide defect. The API-key
  `permission`-claim path and the `platformRole=platform_admin` superuser rule are unchanged.
- `src/Tamma.Api/Auth/SelfOrPermissionRequirement.cs` (`SelfOrPermissionHandler`) — same
  hardcoded `FindFirst(ClaimTypes.Role)` in its permission branch.
- `src/Tamma.Api/Endpoints/AgentEndpoints.cs` `IsTenantAdminOrOwner` — gated
  `?includeDisabled=true`; same dead read for bearer JWTs.
- `src/Tamma.Api/Middleware/ProxyHeaderAuthMiddleware.cs` `BuildPrincipalFromJwt` — the
  first-bridged-request principal was built from RAW jwt claims (bare `"role"`) on an identity
  with the DEFAULT role claim type, so `IsInRole`/`Identity.Name` missed on that one request;
  the identity now declares `nameType: sub, roleType: "role"` like the JwtBearer options.

**Report-only (already dual-read `"role"` → `ClaimTypes.Role` fallback, so not broken):**
`AuthEndpoints.cs` `/api/auth/me` (~1646, payload/telemetry), `AuthEndpoints.cs` `RoleCheck`
(~1693), `AdminEndpoints.cs` caller-role read (~168, defense-in-depth behind PlatformOwnerAccess).
Also noted: `ApiKeyAuthHandler` mints a bare `"role"` claim into a default-role-type identity, so
API-key principals still never match the role branch — they are (and always were) gated by their
explicit `permission` claims; deliberately NOT changed to avoid widening API-key grants.

**Tests:**
- Handler-direct: `tests/Tamma.Api.Tests/Auth/PermissionHandlerRoleClaimTests.cs` — both claim
  shapes × allowed/denied roles for `PermissionHandler` AND `SelfOrPermissionHandler`, plus pins
  for the preserved API-key and platform-admin paths.
- Over-HTTP: `ScheduledTriggerEndpointsTests.ProductionShapeBearerJwt_BareRoleClaimOnly_...`
  proves a real production-shape JWT (bare `"role"` only) passes the ScheduleManage
  `PermissionRequirement` and a member still 403s on writes.
- The 41-30 fixture's dual-claim `MintToken` workaround is REMOVED — the whole suite now runs on
  single-shape production tokens and stays green, so its RBAC assertions pass for the right reason.

## Interim state (historical)

Story 41-30's tests minted tokens carrying BOTH claim shapes so its RBAC assertions tested the
intended policy semantics while the handler was broken. That workaround is now removed (see
Resolution) — production token minting was never changed.
