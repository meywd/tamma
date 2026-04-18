# Finding 011: OAuth start has no `rd` (redirect destination) or `invite` token support

**Scope**: github
**Severity**: P1 (feature broken)
**Status**: Incomplete (partial port, missing N behaviors)
**Estimated port effort**: 2-3h (couples with Finding 009's state-param work)

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/auth/github-oauth.ts`.

- File: `packages/api/src/routes/auth/github-oauth.ts:73-95,145-176,224-225`
- Contract/behavior: TS consumed two optional query params on `GET /api/auth/github`:
  - `rd` (redirect destination): where to send the user after callback completes. Must be `https://*.tamma.dev` (validated by `sanitizeRedirectUrl`). Default: `dashboardUrl`.
  - `invite`: an invite token granting a specific role (`owner` / `admin` / `member`) on acceptance. The callback looked up the token, applied the role, and marked the invite accepted.

The start endpoint serialized both into the OAuth `state` (see Finding 009). The callback parsed them back, acted on them, and used the sanitized `rd` as the final redirect target.

```typescript
// packages/api/src/routes/auth/github-oauth.ts:73-95 (9e9a57c~1)
app.get<{
  Querystring: { rd?: string; invite?: string };
}>('/api/auth/github', async (request: FastifyRequest<{ Querystring: { rd?: string; invite?: string } }>, reply: FastifyReply) => {
  const callbackUrl = `${dashboardUrl}/oauth2/callback`;
  const scope = 'read:user user:email';

  // Encode redirect destination and optional invite token in OAuth state param.
  // Sanitize the URL upfront so only reconstructed (non-tainted) values are stored.
  const rd = request.query.rd;
  const invite = request.query.invite;
  const sanitizedRd = rd ? sanitizeRedirectUrl(rd) : null;
  const statePayload: Record<string, string> = {};
  if (sanitizedRd) {
    statePayload['rd'] = sanitizedRd;
  }
  if (invite) {
    statePayload['invite'] = invite;
  }
  const state = Buffer.from(JSON.stringify(statePayload)).toString('base64url');
```

Invite handling on the callback leg (for context):

```typescript
// packages/api/src/routes/auth/github-oauth.ts:162-176 (9e9a57c~1)
// Determine role from invite token if present
let assignedRole: 'owner' | 'admin' | 'member' = 'member';
if (inviteToken && inviteStore) {
  const invite = await inviteStore.getInviteByToken(inviteToken);
  if (invite && invite.acceptedAt === null && invite.expiresAt > new Date().toISOString()) {
    assignedRole = invite.role;
    await inviteStore.acceptInvite(invite.id);
```

- Dependencies: `IInviteStore.getInviteByToken`, `IInviteStore.acceptInvite`, `sanitizeRedirectUrl`.
- Tests that exercised this: integration tests asserted that `?rd=https://elsa.tamma.dev/foo` round-tripped through callback to a final redirect; that `?invite=...` resulted in a role assignment; that a bad `rd` (`https://evil.com`) was silently discarded.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:377-385`
- Contract/behavior: The start endpoint takes no query params. `IConfiguration` is the only argument beyond the return type. `rd` and `invite` are not parsed, not validated, not forwarded.

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:377-385 (current)
public static Task<IResult> GitHubAuth(IConfiguration config)
{
    var clientId = config["GitHub:ClientId"];
    if (string.IsNullOrEmpty(clientId))
        return Task.FromResult(Results.BadRequest(new { error = "GitHub OAuth not configured" }));
    var redirectUri = config["GitHub:RedirectUri"] ?? "http://localhost:3000/api/auth/github/callback";
    var url = $"https://github.com/login/oauth/authorize?client_id={clientId}&redirect_uri={Uri.EscapeDataString(redirectUri)}&scope=user:email";
    return Task.FromResult(Results.Redirect(url));
}
```

No `HttpRequest` reading, no `HttpContext.Request.Query` access. The signature doesn't even expose the request object.

- Dependencies: `IConfiguration` only. No invite repository, no URL sanitizer.
- Tests: none covering `rd`/`invite` passthrough.

## 3. The gap

Concrete behavioral difference — what a caller or user experiences differently.

- TS did: accept `rd` + `invite`, sanitize `rd`, serialize into state, act on them in callback.
- C# does: ignore both.
- For a caller hitting `GET /api/auth/github?rd=https://elsa.tamma.dev/dashboards/main&invite=inv_abc123`:
  - TS: after successful OAuth, user lands at `https://elsa.tamma.dev/dashboards/main`, their user row is assigned the invite's role (`owner`/`admin`/`member`), the invite is marked accepted.
  - C#: after a hypothetical successful OAuth (callback is a stub today — Finding 012), user lands wherever the callback eventually redirects to (nowhere specific), and the invite is neither looked up nor applied.
- In production with existing data / deployed clients, this means:
  - **Cross-subdomain SSO is broken**: the `app.tamma.dev` → `elsa.tamma.dev` → login-redirect-back-to-`elsa.tamma.dev` flow cannot preserve the original destination. Users who are bounced through OAuth land on the default dashboard and must navigate back manually.
  - **Invite acceptance via GitHub OAuth is broken**: an invited user clicking an invite link that ultimately goes through GitHub OAuth (the expected UX for invite-a-new-user-who-doesn't-have-a-password-yet) will not have their role upgraded. They join as default `member` and an admin must manually promote them — defeating the invite's purpose.
  - **Business impact**: for a SaaS platform expecting invites to be a primary growth mechanic, losing automatic invite-role-application on OAuth is a material regression.

Error paths:
- TS error path: bad `rd` → sanitized to null, silently stripped. Expired invite → ignored, user gets default role. Invite store unavailable → role remains default, request succeeds.
- C# error path: no handling paths, no errors — the features simply don't exist.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-2-user-login-session-management.md`
- Story's acceptance criteria for this behavior: Story 18-2 focuses on login/session. Invite semantics are in story 18-3 (org/tenant creation) and are referenced via the broader onboarding flow. However, redirect-after-login is a login-story concern and is implied but not explicit in AC.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [x] No story — spec gap; must be backfilled before remediation

Story 18-2 should be amended to explicitly include `rd` and `invite` handling, or a new story in Epic 18 should own the invite-via-OAuth flow.

## 5. Status

- **Classification**: Incomplete — the scope was understood (TS had it) and dropped.
- **What's needed to finish**:
  1. Change `GitHubAuth` signature to accept `HttpContext` (or a typed binding of query params).
  2. Read `rd` and `invite` from `context.Request.Query`.
  3. Implement `SanitizeRedirectUrl` in C# mirroring `github-oauth.ts:262-287`. Must:
     - Accept relative paths (`/foo/bar`) after stripping authority.
     - Accept only `https://` for absolute URLs.
     - Accept only `tamma.dev` or `*.tamma.dev` hosts.
     - Return `null` on any violation, so the caller drops the value silently.
  4. Serialize `{rd?, invite?}` into the state JWT/cookie (see Finding 009). Do NOT concatenate into the GitHub URL directly — that would leak invite tokens into the GitHub-side redirect chain and their server logs.
  5. On callback (Finding 012), extract and act on both.
- **Is it "just a stub" or is scope missing?** Scope missing — the port cut this feature.
- **Blockers**: Pairs with Finding 009 (state) and Finding 012 (callback). All three should land together.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:377-385` — extend signature, parse query, serialize.
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Api/Services/Auth/RedirectUrlSanitizer.cs` — port of `sanitizeRedirectUrl`.
  - Tests for the sanitizer covering all TS edge cases (protocol-relative URLs, userinfo attacks, punycode, trailing-dot hosts, etc.).
- Tests to add:
  - `RedirectUrlSanitizerTests.Accepts_RelativePath`
  - `RedirectUrlSanitizerTests.Accepts_TammaDevAbsolute`
  - `RedirectUrlSanitizerTests.Rejects_HttpScheme`
  - `RedirectUrlSanitizerTests.Rejects_NonTammaHost`
  - `RedirectUrlSanitizerTests.Rejects_ProtocolRelative` (`//evil.com/`)
  - `RedirectUrlSanitizerTests.Rejects_HostWithUserinfo` (`https://a@evil.com@tamma.dev/`)
  - `AuthEndpointsTests.GitHubAuth_PassesRdAndInvite_ToState`
  - `AuthEndpointsTests.GitHubAuth_RejectsNonTammaRd_DropsIt`
- Estimated effort: 2-3h broken down as:
  - Sanitizer + tests: 1-1.5h
  - Endpoint query wiring: 0.5h
  - Integration tests: 0.5-1h

## References

- TS source: `packages/api/src/routes/auth/github-oauth.ts:73-95,145-176,224-225,262-287` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:377-385`
- Story: `docs/stories/epic-18/18-2-user-login-session-management.md`
- Related findings: `009-oauth-start-no-csrf-state.md`, `012-oauth-callback-literal-stub.md`
