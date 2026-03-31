---
title: "Story 16.6: ELSA Studio Auto-Login (Bypass Internal Login Page)"
sidebar:
  order: 160
---

Status: in-progress

## Story

As an **admin/owner user**,
I want ELSA Studio to automatically log me in when I access elsa.tamma.dev,
so that I do not need to enter separate ELSA credentials after already authenticating via GitHub OAuth.

## Context

nginx at elsa.tamma.dev already gates access — only users with a valid `tamma_session` JWT cookie and admin/owner role can reach ELSA Studio (Story 16.5). However, ELSA Studio has its own internal identity system (`Elsa.Identity`) that shows a separate login page requiring username/password. This is redundant friction.

The ELSA server admin credentials are configured via environment variables (`ELSA_ADMIN_PASSWORD`). Since only pre-authenticated admins can reach the Studio, auto-logging in with these credentials is safe.

## Acceptance Criteria

1. When an authenticated admin/owner visits elsa.tamma.dev, ELSA Studio loads directly to the workflow dashboard — no login page appears
2. The auto-login calls ELSA's `/elsa/api/identity/login` endpoint with the admin credentials
3. The resulting ELSA JWT tokens (access + refresh) are stored in `IJwtAccessor` (localStorage) so subsequent API calls work normally
4. If auto-login fails (wrong password, ELSA server down), the user is redirected to the standard ELSA login page as fallback
5. The admin password is injected into `appsettings.json` at container startup via `docker-entrypoint.sh` (Blazor WASM cannot read environment variables)
6. Token refresh continues to work normally via the existing `AuthenticatingApiHttpMessageHandler`
7. Re-entrant calls to `RedirectToAuthorizationServer()` are guarded (Blazor can call this multiple times during initial auth state resolution)

## Technical Approach

Replace ELSA's `IAuthorizationService` (which navigates to `/login`) with a custom `AutoLoginAuthorizationService` that:
1. Calls POST `/elsa/api/identity/login` with `{ username: "admin", password: "<from config>" }`
2. On success, writes access/refresh tokens to `IJwtAccessor`
3. Triggers `NotifyAuthenticationStateChanged()` on the `AuthenticationStateProvider`
4. Navigates to the dashboard

Keep `AddLoginModule().UseElsaIdentity()` for token management infrastructure (refresh, JWT accessor, auth state provider, message handler).

## Files

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Studio/Auth/AutoLoginAuthorizationService.cs` | **CREATE** |
| `apps/tamma-elsa/src/Tamma.Studio/Program.cs` | **MODIFY** — register override + named HttpClient |
| `apps/tamma-elsa/src/Tamma.Studio/wwwroot/appsettings.json` | **MODIFY** — add AutoLogin section |
| `apps/tamma-elsa/src/Tamma.Studio/docker-entrypoint.sh` | **MODIFY** — inject ELSA_ADMIN_PASSWORD |
| `docker/docker-compose.yml` | **MODIFY** — pass ELSA_ADMIN_PASSWORD to elsa-studio |

## Security

- nginx role-check already ensures only admin/owner users reach ELSA Studio
- The admin password in appsettings.json is only accessible to authenticated admins
- The password is for ELSA's internal identity system, not any external service
- This is equivalent to the original Story 16.1 AC #7: "ELSA Studio bypasses its own ELSA Identity login"

## Dependencies

- Story 16.5 (Role-Based Access Control) — completed
- Story 16.1 (Unified Auth) — partially completed (oauth2-proxy removed, JWT auth is sole mechanism)
