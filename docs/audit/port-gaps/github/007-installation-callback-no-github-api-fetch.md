# Finding 007: Installation callback no longer calls the GitHub API (no `getInstallation`, no repo enumeration)

**Scope**: github
**Severity**: P0 (cutover-blocking)
**Status**: Semantic rewrite (structure changed, not a port)
**Estimated port effort**: 5-6h (excluding secrets provisioner — Finding 013)

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/github/github-callback.ts`.

- File: `packages/api/src/routes/github/github-callback.ts:34-105`
- Contract/behavior: On `GET /api/github/callback?installation_id=X&setup_action=install`, the TS handler (a) built an App-JWT-authenticated Octokit, (b) called `apps.getInstallation` to fetch the installation's real `accountLogin`, `accountType`, `permissions`, `suspendedAt`, and to VALIDATE the installation exists and the app is installed on the account, (c) built a second installation-authenticated Octokit, (d) called `apps.listReposAccessibleToInstallation` to enumerate the repos the installation grants access to — this is the authoritative list, the webhook-delivered repo list is a subset or may not have arrived yet, and (e) persisted both.

```typescript
// packages/api/src/routes/github/github-callback.ts:49-105 (9e9a57c~1)
if (setupAction === 'install' || setupAction === 'update') {
  // Create an App-authenticated Octokit to fetch installation details
  const octokit = new Octokit({
    authStrategy: createAppAuth,
    auth: {
      appId: options.appId,
      privateKey: options.privateKey,
    },
  });

  try {
    // Fetch installation details
    const { data: installation } = await octokit.rest.apps.getInstallation({
      installation_id: installationId,
    });

    const account = installation.account;
    const accountLogin = account && 'login' in account ? (account.login ?? 'unknown') : 'unknown';
    const accountType = account && 'type' in account ? (account.type ?? 'User') : 'User';

    // Store installation
    await options.installationStore.upsertInstallation({
      installationId,
      accountLogin,
      accountType: accountType as 'User' | 'Organization',
      appId: options.appId,
      permissions: (installation.permissions ?? {}) as Record<string, string>,
      suspendedAt: installation.suspended_at ?? null,
      apiKeyHash: null,
      apiKeyPrefix: null,
      apiKeyEncrypted: null,
    });

    // Fetch and store repos for this installation
    const installationOctokit = new Octokit({
      authStrategy: createAppAuth,
      auth: {
        appId: options.appId,
        privateKey: options.privateKey,
        installationId,
      },
    });

    const { data: reposData } = await installationOctokit.rest.apps.listReposAccessibleToInstallation({
      per_page: 100,
    });

    const repos = reposData.repositories.map((repo) => ({
      repoId: repo.id,
      owner: repo.owner.login,
      name: repo.name,
      fullName: repo.full_name,
    }));

    await options.installationStore.setRepos(installationId, repos);
```

- Dependencies: `@octokit/rest`, `@octokit/auth-app`, `GitHubCallbackOptions.appId`, `GitHubCallbackOptions.privateKey`, `IGitHubInstallationStore`.
- Tests that exercised this: integration tests used `msw` (Mock Service Worker) to stub GitHub's `GET /app/installations/{id}` and `GET /installation/repositories`, and asserted the store received the fetched values.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs:41-107`; `apps/tamma-elsa/src/Tamma.Api/Endpoints/GitHubEndpoints.cs:30-89`
- Contract/behavior: The C# `HandleCallbackAsync` does not call the GitHub API at all. Instead, it resolves the caller's user and tenant from the JWT, then creates-or-updates a `GitHubInstallation` row with **placeholder values derived from the local tenant**, not from GitHub.

```csharp
// apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs:71-89 (current)
var existing = await _installations.GetByInstallationIdAsync(installationId);
GitHubInstallation stored;

if (existing is null)
{
    stored = await _installations.CreateAsync(new GitHubInstallation
    {
        InstallationId = installationId,
        AccountLogin = user.GitHubLogin ?? tenant.Slug,
        AccountType = "User",
        AppId = 0,
        TenantId = tenant.Id
    });
}
else
{
    existing.TenantId = tenant.Id;
    stored = await _installations.UpsertAsync(existing);
}
```

Look at the fields written when `existing is null` — these are effectively hardcoded:
- `AccountLogin = user.GitHubLogin ?? tenant.Slug` — **not the GitHub account the App was installed on**. If the user is installing on an org (not their personal account) this is wrong. If the user has no `GitHubLogin`, the tenant slug is used as a stand-in.
- `AccountType = "User"` — **hardcoded**. The installation might be on an `Organization`.
- `AppId = 0` — **hardcoded zero**. This is the app's numeric ID; it's not knowable without either configuration or an API call.
- `Permissions` not set — defaults to `"{}"`.
- `SuspendedAt` not set — defaults to null, which is correct for a fresh install but wrong if GitHub tells us the app was installed-and-immediately-suspended.
- No repos are enumerated or persisted on this leg. The webhook does carry repos in `payload.repositories` (Finding 006), so if the webhook arrives first, repos get seeded; if the callback arrives first (rare but possible, webhook retries happen), repos remain unseeded until a later webhook arrives.

- Dependencies: `IUserRepository`, `ITenantRepository`, `IInstallationRepository`. **Notably missing**: no Octokit dependency registered, no `GITHUB_APP_PRIVATE_KEY` configuration read, no JWT-as-App-identity generation path, no HTTP client to GitHub.
- Tests: `InstallationRouterServiceTests.HandleCallback_*` asserts the tenant linking. No test asserts the `AccountLogin`/`AccountType`/`AppId` values because they're known placeholders.

## 3. The gap

Concrete behavioral difference — what a caller or user experiences differently.

- TS did: Octokit App-JWT call to `GET /app/installations/{id}` + Octokit installation-token call to `GET /installation/repositories` → persist real values.
- C# does: local-DB-only linking → persist placeholder values (`AccountLogin = tenant.Slug`, `AccountType = "User"`, `AppId = 0`).
- For a caller completing the GitHub App install UI and being redirected to `GET /api/github/callback?installation_id=12345`, TS returns with the DB populated with the real `AccountLogin` ("my-org-name"), real `AccountType` ("Organization"), real `AppId` (e.g. 567890), real permissions (`{"contents":"write","metadata":"read",...}`). C# returns with `AccountLogin = "alice"` (the tenant slug or user GitHub login), `AccountType = "User"`, `AppId = 0`, `Permissions = "{}"`.
- In production with existing data / deployed clients, this means:
  - Multi-org support is broken: if Alice installs the app on her org `acme-corp`, the row records `AccountLogin = "alice"` (her personal login), not `"acme-corp"`. Downstream systems that key off `AccountLogin` to route webhooks back to the right tenant will misroute.
  - `AppId = 0` breaks any JWT generation path that needs the app's numeric ID (e.g., when later calling GitHub as the installation — you need the appId to build the JWT).
  - Permissions validation is impossible: the system can't check "does this installation have `contents:write`?" because the row says `"{}"`.
  - The webhook-vs-callback race creates silent data loss: the webhook's repo list is used only if it arrives; there's no authoritative fallback.

Error paths:
- TS error path: Octokit call fails (network, 404, 403) → the try/catch at `github-callback.ts:156-159` sends `500 {"error":"Failed to process installation"}`. The user sees an error page and can retry.
- C# error path: no outbound call, so no network-related error. Only DB errors can happen.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md`
- Story's acceptance criteria for this behavior: AC #3 ("Installation callback handles the `installation.created` webhook and links the new installation to the user's org based on the `state` parameter"). AC #4 ("Repository selection endpoint … lists repositories available through the installation") implies an authoritative list must exist somewhere; Task 4 Subtask 4.1 says repos come "from stored repo data + live GitHub API fallback" — which presupposes both a stored list AND the ability to call GitHub. The C# version has the stored list (if the webhook delivered it) and no GitHub fallback.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story — spec gap

## 5. Status

- **Classification**: Semantic rewrite. The TS handler was about "fetch from GitHub + persist"; the C# handler is about "link an existing row to a tenant". These are different operations that both happen to live behind the same URL.
- **What's needed to finish**:
  1. Add an App-JWT generator service in C#. The simplest approach is `System.IdentityModel.Tokens.Jwt` + an RS256 signing key loaded from `GitHub:PrivateKey` config. JWT claims: `iat`, `exp` (10 min max), `iss = GitHub:AppId`. Octokit.NET has a helper, but a 30-line custom generator is fine.
  2. Introduce `IGitHubAppClient` service that returns an `HttpClient` (or Octokit.NET instance) authenticated as the App or as an Installation.
  3. In `HandleCallbackAsync` after resolving the user+tenant, call `GET /app/installations/{id}` to fetch truth, populate `AccountLogin`/`AccountType`/`AppId`/`Permissions`/`SuspendedAt` from the response.
  4. Get an installation access token (`POST /app/installations/{id}/access_tokens`) and call `GET /installation/repositories` to enumerate repos. Persist via `_installations.AddRepoAsync` for each.
  5. Only after (3)+(4) succeed, do the tenant-link write. Error handling: on a 404 from GitHub, redirect to `error?reason=installation_not_found`; on 5xx, `error?reason=github_unavailable` (retryable).
- **Is it "just a stub" or is scope missing?** Neither, strictly — the existing code does something (tenant link) but it's not what the surface was supposed to do. Closest classification is semantic rewrite.
- **Blockers**:
  - Decide whether to take a dependency on Octokit.NET or write raw `HttpClient` calls. Octokit.NET is mature and handles auth/rate-limit headers, which helps Finding 015.
  - Requires `GitHub:PrivateKey` config to be set (it isn't today — see absence in `ApiKeyRotationService.cs:13-16` comment).
  - Coordinates with Finding 008 (API key gen) and Finding 013 (secrets provisioner).

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs:41-107` — call the new GitHub client, populate real fields.
  - `apps/tamma-elsa/src/Tamma.Api/Program.cs` — register new services in DI.
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/IGitHubAppClient.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/GitHubAppClient.cs` — App JWT + installation token retrieval + typed responses.
  - Tests fixture that stubs GitHub via `WireMock.Net` or a `TestHttpMessageHandler`.
- Tests to add:
  - `GitHubAppClientTests.GetInstallation_ReturnsParsedAccount`
  - `GitHubAppClientTests.ListInstallationRepos_PaginatesOver100`
  - `InstallationRouterServiceTests.HandleCallback_FetchesInstallationAndRepos_PersistsRealValues`
  - `InstallationRouterServiceTests.HandleCallback_GitHub404_RedirectsWithReason`
- Estimated effort: 5-6h broken down as:
  - GitHubAppClient (JWT + HTTP + typed DTOs): 3h
  - Router service integration: 1h
  - Tests (mocked GitHub + repository asserts): 1-2h

## References

- TS source: `packages/api/src/routes/github/github-callback.ts:49-105` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs:41-107`
- Story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md` (AC #3, #4; Task 4.1)
- Related findings: `006-installation-created-no-provisioning.md`, `008-installation-callback-no-api-key-generation.md`, `013-secrets-provisioner-libsodium-missing.md`, `015-outbound-github-rate-limit-unhandled.md`
