# Finding 020: GitHub install callback auth model — no `.RequireAuthorization`, unauthenticated users get a redirect instead of 401

**Scope**: github
**Severity**: P3 (drift/contract)
**Status**: Behavioral drift (ported but semantics diverged)
**Estimated port effort**: 1-2h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/github/github-callback.ts`.

- File: `packages/api/src/routes/github/github-callback.ts:34-47`
- Contract/behavior: The TS callback did NOT require authentication at the HTTP level. The route was wired with no auth preHandler. The handler consumed `installation_id` and `setup_action` from query params and proceeded regardless of whether the caller was authenticated. This is because GitHub's install redirect does not carry a session — the user arrives back from GitHub with a fresh browser state relative to your app. TS compensated by doing all the install work in an App-authenticated context (the App's own private key, not a user session) and persisting to a globally-visible table; the user was then expected to log in separately to see "their" installation.

Critically, TS did not link installation → tenant at this step, because there was no tenant model yet in the TS codebase (that came with Epic 17).

```typescript
// packages/api/src/routes/github/github-callback.ts:34-47 (9e9a57c~1)
app.get<{
  Querystring: { installation_id?: string; setup_action?: string };
}>('/api/github/callback', async (request, reply) => {
  const installationIdStr = request.query.installation_id;
  const setupAction = request.query.setup_action;

  if (!installationIdStr || !setupAction) {
    return reply.status(400).send({ error: 'Missing installation_id or setup_action' });
  }

  const installationId = parseInt(installationIdStr, 10);
  if (Number.isNaN(installationId)) {
    return reply.status(400).send({ error: 'Invalid installation_id' });
  }
```

- Dependencies: none (the route is public).
- Tests that exercised this: integration tests hit the callback with no cookies and asserted success given a valid `installation_id`.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Program.cs:467`; `apps/tamma-elsa/src/Tamma.Api/Endpoints/GitHubEndpoints.cs:30-89`
- Contract/behavior: The route is registered without `.RequireAuthorization`, so at the ASP.NET Core pipeline level it's public. But the handler itself does a claims-based user lookup:

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/GitHubEndpoints.cs:55-61 (current)
// Must be an authenticated user to link the install to a tenant.
var userIdClaim = context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
{
    var unauthedUri = $"{dashboardBase}{ErrorRedirectPath}?reason=unauthenticated";
    return Results.Redirect(unauthedUri);
}
```

So an unauthenticated user hitting `/api/github/callback?installation_id=12345` gets a 302 redirect to `https://dash.tamma.dev/onboarding/error?reason=unauthenticated` instead of a 401, and instead of the installation being accepted-and-orphaned (to be claimed later via a separate UI), the installation is dropped on the floor.

Route registration:

```csharp
// apps/tamma-elsa/src/Tamma.Api/Program.cs:467 (current)
github.MapGet("/callback", GitHubEndpoints.Callback);
```

And yes, middleware whitelists this path (`TenantContextMiddleware.cs:19`, `EnsurePersonalTenantMiddleware.cs:21`) confirming the intent is "public route, no tenant bound" — but the handler's internal check contradicts the public-route declaration.

- Dependencies: `HttpContext.User` (from whatever authentication scheme is active; the default JWT bearer middleware will populate this from cookies if present).
- Tests: `GitHubEndpointsIntegrationTests` covers the authenticated-user path. It does not cover the unauthenticated path (no test posts without a session).

## 3. The gap

- TS did: accept unauthenticated callbacks; persist installation as orphan; user claims later.
- C# does: accept unauthenticated callbacks at the routing layer; handler redirects to `/onboarding/error?reason=unauthenticated`; installation is not persisted at all.
- For a caller completing GitHub App install from the GitHub Marketplace (where the redirect back lands on our callback with **no** prior Tamma session), TS recorded the installation; C# discards it. The user sees an "unauthenticated" error page and must log in separately, then discover the "claim unlinked installation" UI (which doesn't exist yet per Finding 007/008) to rescue their install.
- In production with existing data / deployed clients, this means: the **Marketplace install flow is broken**. Epic 18 Story 18-4 Implementation Notes explicitly call this out:
  > "GitHub App installations can be initiated two ways: (1) from Tamma's onboarding flow (has `state` with `tenantId`), or (2) from GitHub Marketplace / the app page directly (no `state`). For case (2), the installation is stored but not linked to an org until the user claims it via a 'link installation' UI."
  The C# implementation handles case (1) correctly-ish (an authenticated user gets linked) but breaks case (2) by refusing to store the installation at all. The TS implementation handled both.

Error paths:
- TS error path: bad input (missing `installation_id`) → 400; everything else succeeds.
- C# error path: unauthenticated → 302 to error page (which is worse UX than a 401 with no body — because the redirect hides the underlying reason from the browser's network tab). Authenticated with wrong tenant → 302 to error page with `reason=no_active_tenant`. Authenticated with unknown user → same.

Additionally, the mix of 302-redirect error responses and handler-internal auth checks is confusing:
- Public routes that redirect on unauthenticated callers are atypical — either require auth (401) OR accept everyone (200/behavior). The middle ground breaks observability.
- The redirect makes security testing harder: an auth bypass would look like a redirect, not an error.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md` Implementation Notes (quoted above).
- Story's acceptance criteria for this behavior: AC #3 assumes a `state` parameter provides the tenant link. Implementation note explicitly preserves a "claim later" flow for Marketplace installs.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS) — the story's implementation note matches TS.
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story — spec gap

The story explicitly authorizes the TS "accept-and-orphan" behavior; C# lost it.

## 5. Status

- **Classification**: Behavioral drift — the port added a new (stricter) auth check that contradicts the story's "accept orphan installs" guidance.
- **What's needed to finish**:
  1. Decide: does the callback require auth or not? Three options:
     - **Option A (restore TS behavior)**: drop the unauthenticated redirect. When no user is authenticated, create an orphan row (no `TenantId`). Later, a "claim installation" UI lets the user adopt it.
     - **Option B (require `state` param)**: follow Epic 18 Story 18-4 Task 2.2 — the `state` is a signed JWT with `{tenantId, userId, nonce, exp}`. Validate the state and link to the enclosed tenant. No session cookie needed.
     - **Option C (both)**: accept authenticated caller OR valid state JWT; redirect unauth only if both are missing.
  2. Recommended: Option B, because it matches the story's spec. State is signed with `JWT_SECRET`, so it's unforgeable. The callback doesn't need to trust the browser session.
  3. If Option B: read `state` query param, verify as JWT, extract `tenantId`, link. If state is missing or invalid, fall back to session cookie (Option A) or orphan.
  4. At the ASP.NET Core routing layer, leave as `.MapGet` with no `.RequireAuthorization`. The route is legitimately public — GitHub redirects the user here without guaranteed session state.
- **Is it "just a stub" or is scope missing?** Scope drift. The port added a stricter check than the story specified.
- **Blockers**: Needs alignment with Finding 007 (install-time GitHub API fetch) and Finding 012 (OAuth callback — because the user's session may need to be set up before the install callback can complete).

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/GitHubEndpoints.cs:55-61` — replace the unauthed redirect with state-JWT verification + orphan-fallback logic.
  - `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs:41-107` — accept nullable `callingUserId` and `tenantId` (from state); handle orphan case by persisting with `TenantId = null`.
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/IInstallStateValidator.cs` + impl — verifies the signed state JWT.
- Tests to add:
  - `GitHubEndpointsIntegrationTests.Callback_ValidStateJwt_LinksTenant`
  - `GitHubEndpointsIntegrationTests.Callback_AuthenticatedUser_NoState_LinksToActiveTenant`
  - `GitHubEndpointsIntegrationTests.Callback_Unauthenticated_NoState_PersistsOrphan`
  - `GitHubEndpointsIntegrationTests.Callback_InvalidStateJwt_Rejects`
- Estimated effort: 1-2h broken down as:
  - State validator: 0.5h
  - Handler rewrite: 0.5h
  - Tests (4 cases): 0.5-1h

## References

- TS source: `packages/api/src/routes/github/github-callback.ts:34-47` (commit `9e9a57c~1`)
- C# source:
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/GitHubEndpoints.cs:55-61`
  - `apps/tamma-elsa/src/Tamma.Api/Program.cs:467`
- Story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md` Implementation Notes + Task 2.2, 2.5
- Related findings: `007-installation-callback-no-github-api-fetch.md`, `012-oauth-callback-literal-stub.md`, `021-installation-id-bigint-pk-vs-guid.md`
