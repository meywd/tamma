# Finding 010: OAuth start requests only `user:email` scope, missing `read:user`

**Scope**: github
**Severity**: P1 (feature broken)
**Status**: Behavioral drift (ported but semantics diverged)
**Estimated port effort**: 0.5h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/auth/github-oauth.ts`.

- File: `packages/api/src/routes/auth/github-oauth.ts:77`
- Contract/behavior: The TS authorize URL requested scope `read:user user:email` (two scopes, space-separated). `user:email` gives access to the authenticated user's email addresses (including private ones); `read:user` gives read access to the full user profile (`login`, `id`, `name`, `avatar_url`, public profile fields). Both were requested because the callback (Finding 012) used `GET https://api.github.com/user` to fetch `{id, login, email}` — and while `/user` returns a minimal profile for any OAuth token, richer fields require `read:user`.

```typescript
// packages/api/src/routes/auth/github-oauth.ts:76-77 (9e9a57c~1)
const callbackUrl = `${dashboardUrl}/oauth2/callback`;
const scope = 'read:user user:email';
```

```typescript
// packages/api/src/routes/auth/github-oauth.ts:93 (9e9a57c~1)
const githubUrl = `https://github.com/login/oauth/authorize?client_id=${clientId}&redirect_uri=${encodeURIComponent(callbackUrl)}&scope=${encodeURIComponent(scope)}&state=${encodeURIComponent(state)}`;
```

- Dependencies: Works with GitHub's `/user` and `/user/emails` endpoints. Notably `/user/emails` is gated by `user:email`; `/user` basic profile is returned unconditionally for any valid token; extended profile fields honor `read:user`.
- Tests that exercised this: integration test snapshotted the authorize URL and asserted `scope=read%3Auser%20user%3Aemail`.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:383`
- Contract/behavior: The authorize URL requests only `user:email`.

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:383 (current)
var url = $"https://github.com/login/oauth/authorize?client_id={clientId}&redirect_uri={Uri.EscapeDataString(redirectUri)}&scope=user:email";
```

- Dependencies: Same endpoints will work for email, but any read of the profile beyond the minimal set gets refused or returns partial data.
- Tests: No test asserts the scope string. A test that does would currently pin the wrong (narrower) scope.

## 3. The gap

Concrete behavioral difference — what a caller or user experiences differently.

- TS did: request `read:user user:email`. GitHub presents an authorize dialog listing both scopes. User grants both. Token can fetch full profile.
- C# does: request `user:email` only. Authorize dialog lists just "Email addresses (read-only)". Token can fetch email but is gated on profile fields.
- For a caller completing OAuth, the practical observable difference is two-fold:
  1. **Authorize dialog UX**: the user sees one scope in C# vs. two in TS. This is a branding/trust consideration — users tend to trust apps that request fewer scopes, so this is arguably a win on the UI side. But:
  2. **Callback's `/user` response fidelity**: when the C# callback (Finding 012) is eventually implemented and calls `GET https://api.github.com/user` with the returned token, certain fields may be missing or partial compared to the TS implementation. In practice, `id` and `login` are always present, and `email` depends on the user's privacy settings — but fields like `name`, `company`, `location`, `bio`, `blog`, `twitter_username`, `public_repos` count, etc. are returned as visible data based on the user's profile privacy AND the token scope. With `read:user` the token can access the authenticated user's view of their own profile; without it, the token sees only the public view.

- In production with existing data / deployed clients, this means: the current gap has limited immediate impact because the callback is a stub (Finding 012) and doesn't consume richer profile fields yet. But once Finding 012 is implemented, the callback's ability to populate user-store fields like `Name` (display name, not login) will be degraded. Users who hid their name publicly would have `Name` null even though it's visible to them via `read:user`.

Error paths:
- TS error path: scope grant denied at GitHub's consent screen → GitHub redirects to callback with `error=access_denied`; the callback branch at `github-oauth.ts:105-107` handles this and redirects to `/login?error=access_denied`.
- C# error path: same error mode would apply once the callback exists. Today the callback is a stub so `error=access_denied` is swallowed.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-2-user-login-session-management.md`
- Story's acceptance criteria for this behavior:
  > Task 4, Subtask 4.4: "Fetch GitHub user profile + verified emails"
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story — spec gap

The story mentions "user profile" — which requires `read:user` to be complete. The scope contract is implicit but clear.

## 5. Status

- **Classification**: Behavioral drift — a single character in the scope string was lost during the port.
- **What's needed to finish**:
  1. Change `scope=user:email` to `scope=read:user user:email` (URL-encode the space as `%20` or `+`).
  2. Pin the expected scope in a unit test so this doesn't silently drift again.
- **Is it "just a stub" or is scope missing?** Neither — this is a trivial port omission. Likely a copy/paste error.
- **Blockers**: None standalone, but bundle the fix with Finding 009 (state param) and Finding 011 (rd/invite) — all three changes touch the same 7-line method.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:383` — update scope string.
- Files to create: none.
- Tests to add:
  - `AuthEndpointsTests.GitHubAuth_RequestsReadUserAndUserEmailScopes` — assert the redirect URL contains `scope=read%3Auser%20user%3Aemail` (or the `+`-encoded variant).
- Estimated effort: 0.5h broken down as:
  - Code change: 0.1h
  - Test: 0.4h

## References

- TS source: `packages/api/src/routes/auth/github-oauth.ts:77,93` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:383`
- Story: `docs/stories/epic-18/18-2-user-login-session-management.md` (Task 4.4)
- Related findings: `009-oauth-start-no-csrf-state.md`, `011-oauth-start-no-rd-invite.md`, `012-oauth-callback-literal-stub.md`
- GitHub docs: [OAuth scopes — read:user vs user:email](https://docs.github.com/en/developers/apps/building-oauth-apps/scopes-for-oauth-apps)
