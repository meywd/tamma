# PermissionHandler never matches production JWT role claims — every PermissionRequirement route is dead for real bearer tokens

**Date**: 2026-07-29
**Status**: 🐛 Open — fail-closed (functionality broken, no privilege escalation)
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

## Interim state

Story 41-30's tests mint tokens carrying BOTH claim shapes (documented in the fixture) so its
RBAC assertions test the intended policy semantics. Do not copy that dual-claim workaround into
production token minting — fix the handler.
