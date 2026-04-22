# Story 18-4 Implementation Plan — GitHub App Installation Onboarding

**Status**: Planned (2026-04-20)
**Story brief**: [`18-4-github-app-installation-onboarding.md`](./18-4-github-app-installation-onboarding.md)
**Team**: Layer 4 Team C (Epic 18 completion)
**Branch**: `feat/story-18-4-github-app-onboarding`
**Worktree**: `/home/meywd/tamma-worktrees/layer-4-team-c-18-4-github-app`

---

## 1. Objective

Finish the GitHub App onboarding loop that stopped at "stub redirect" in
Epic 19 Phase 3. After this story, a user who has completed email
verification + org creation can click "Connect GitHub" in
`dash.tamma.dev`, land on `github.com/apps/tamma-dev/installations/new`,
pick repos, and return to Tamma with an `InstallationRepo` row linked to
their tenant. Repo activation, installation settings, and a guided
first-run test workflow complete the flow. All endpoints live in the
C# Minimal API (Epic 19 moved the API off TypeScript); the React
surface is owned by Story 18-5.

## 2. Dependencies

Hard blockers:

- **Story 18-2** (user login + session) — JWT issued on login is the
  caller credential for onboarding endpoints.
- **Story 18-3** (org creation) — `hasOrg` onboarding step feeds this
  story's `hasInstallation` step.
- **Story 28-3 / 28-7 / 28-8** — tenant routing + API-key prefix + middleware
  must resolve the caller's active tenantId.
- Stub `GitHubEndpoints.Callback` (already in repo from Phase 3) — we
  replace the TODO body.
- `Octokit` 8.x NuGet (already pinned) — for GitHub App JWT, installation
  tokens, repo listing.

Soft:

- **Story 18-5** (user dashboard shell) — onboarding UI. This story
  ships only the backend; the React pages land in 18-5 and call these
  endpoints.

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/OnboardingEndpoints.cs` | `GET /api/v1/onboarding/status`, `GET /api/v1/onboarding/install-github`, `GET /api/v1/onboarding/install-github/callback`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/ReposEndpoints.cs` | `GET /api/v1/orgs/:tenantId/repos`, `POST /api/v1/orgs/:tenantId/repos/activate`, `POST /api/v1/orgs/:tenantId/repos/deactivate`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/InstallationSettingsEndpoints.cs` | `GET/PUT /api/v1/orgs/:tenantId/installation/:installationId[/settings]`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/FirstRunEndpoints.cs` | `POST /api/v1/orgs/:tenantId/repos/:repoId/first-run` + SSE progress. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Onboarding/OnboardingStatusService.cs` | Cross-store query that computes the flags array. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Onboarding/InstallationStateToken.cs` | Short-lived (10-min) JWT issuer + verifier for the `state` param. Uses HS256 with the existing `JWT_SECRET`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/GitHub/GitHubAppAuth.cs` | Wraps `Octokit.GitHubAppsClient` to mint installation tokens, cached 55 min. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRepoSyncService.cs` | Pulls repo list for an installation + persists to `github_installation_repos`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Workflows/FirstRunWorkflow.cs` | Elsa workflow: clone repo → read README → post a "hello from Tamma" comment on a test issue. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/Onboarding/OnboardingStatusServiceTests.cs` | Every onboarding state combination. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/Onboarding/InstallationStateTokenTests.cs` | Replay protection, expiry, nonce uniqueness. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/GitHub/InstallationCallbackTests.cs` | Happy path, expired state, wrong tenant, no-state case. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/Repos/ReposEndpointsTests.cs` | List, activate, deactivate; RBAC; cross-tenant denial. |
| `/home/meywd/tamma/docs/runbooks/github-app-setup.md` | Operator runbook: app manifest URL, webhook URL, callback URL, env vars. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/GitHubEndpoints.cs` | Flesh out stub `Callback` method (redirect branch only; main flow handled by `InstallationCallbackController`). |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/GitHubWebhookEndpoints.cs` | Handle `installation.created` without `state`: persist as "unclaimed"; handle `installation.deleted`: soft-delete `github_installations`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Repositories/InstallationRepository.cs` | Add `LinkToTenantAsync(installationId, tenantId)` + `ListUnclaimedAsync()` + `MarkDeletedAsync`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Entities/GitHubInstallationRepo.cs` | Add `IsActive bool DEFAULT false` column + `ActivatedAt` timestamp. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Migrations/20260501000000_InstallationRepoActivation.cs` | EF migration adding the `IsActive`, `ActivatedAt`, `ActivatedBy` columns. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Program.cs` | Register new services + endpoints. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/appsettings.json` | Add `GitHubApp:AppId`, `GitHubApp:ClientId`, `GitHubApp:PrivateKeyPath`, `GitHubApp:InstallUrl`, `GitHubApp:WebhookSecret` — read via `IOptions<GitHubAppOptions>`. |
| `/home/meywd/tamma/docker/docker-compose.yml` | Inject the private key path via env var + volume-mount the PEM from Docker secrets. |

## 5. Sequence of changes

### Step 1 — Onboarding status endpoint (3h)

- `OnboardingStatusService.GetAsync(userId)`:
  - `emailVerified` = `users.EmailVerified`.
  - `hasOrg` = exists `tenant_memberships` for userId.
  - `hasInstallation` = exists `github_installations` for any of user's tenants.
  - `hasActiveRepo` = exists `github_installation_repos` with `IsActive=true`.
  - `hasFirstRun` = exists workflow run tagged `onboarding-first-run` for user's tenant.
  - Returns a single DTO.
- `GET /api/v1/onboarding/status` handler; requires JWT; returns 200 with DTO.
- xUnit: state permutations (new user, has org only, has install only, …).
- **Commit**: `feat(onboarding): status endpoint + service`.

### Step 2 — Install state token + redirect (3h)

- `InstallationStateToken.Issue(userId, tenantId)` → HS256 JWT with
  `{ sub, tenantId, nonce, exp }`, 10-min TTL. Nonce stored in
  `platform_queued_tasks` table with a `UNIQUE` constraint.
- `InstallationStateToken.VerifyAndConsumeAsync(token)` → validates
  signature + expiry, asserts nonce not consumed, marks consumed.
- `GET /api/v1/onboarding/install-github`:
  - Requires JWT.
  - Reads active tenantId (from claims or `X-Tenant-Id`).
  - Issues state token.
  - `302 Location: https://github.com/apps/{AppSlug}/installations/new?state=<jwt>`.
- Unit tests: signature verification, replay detection, expiry.
- **Commit**: `feat(onboarding): install-github redirect + state token`.

### Step 3 — Installation callback (4h)

- `GET /api/v1/onboarding/install-github/callback?installation_id=&setup_action=&state=`
  - Verify + consume `state` → yields `(userId, tenantId)`.
  - Call `InstallationRepository.LinkToTenantAsync(installation_id, tenantId)`
    (upserts if the webhook beat us).
  - Trigger `InstallationRepoSyncService.SyncAsync(installationId)`.
  - Emit `INSTALLATION.LINKED.SUCCESS` event.
  - Redirect to `https://dash.tamma.dev/onboarding/repos`.
- xUnit: expired state → 400; missing state → 400 with "installation
  pending claim" message + `?claimUrl`; mismatched tenant → 403.
- **Commit**: `feat(onboarding): installation callback links to tenant`.

### Step 4 — GitHub App auth + Octokit wiring (3h)

- `GitHubAppAuth.GetInstallationTokenAsync(installationId)`:
  - Mints app JWT with `AppId` + private key (RS256, 10-min TTL per
    current GitHub App spec).
  - Calls `Octokit.GitHubAppsClient.CreateInstallationToken(installationId)`.
  - Caches the token in-memory (`IMemoryCache`) with 55-min expiry
    (tokens actually expire after 60 min; 5-min buffer).
- Unit tests using mocked `IGitHubClient`.
- **Commit**: `feat(github): app JWT + installation token caching`.

### Step 5 — Repo sync service (3h)

- `InstallationRepoSyncService.SyncAsync(installationId)`:
  - Fetches installation token.
  - Lists repos via `GET /installation/repositories` (paginated; Octokit
    handles pagination).
  - Upserts rows in `github_installation_repos`:
    `(installationId, repoId, fullName, defaultBranch, private, ...)`.
  - New rows have `IsActive=false` (user opts in later).
- Integration test against a mocked Octokit client (covers pagination).
- **Commit**: `feat(github): sync installation repos to DB`.

### Step 6 — Repo listing + activation endpoints (3h)

- `GET /api/v1/orgs/:tenantId/repos` — returns repos joined across all
  installations for tenant; includes `IsActive` flag; RBAC: member+.
- `POST /api/v1/orgs/:tenantId/repos/activate` — body `{ repoIds: number[] }`;
  RBAC: admin+; sets `IsActive=true, ActivatedAt=NOW(), ActivatedBy=userId`.
- `POST /api/v1/orgs/:tenantId/repos/deactivate` — same but sets `false`.
- xUnit: listing; activation; RBAC; cross-tenant 404.
- **Commit**: `feat(repos): list + activate + deactivate endpoints`.

### Step 7 — Installation settings (2h)

- `InstallationSettings` entity: JSON column `Settings TEXT` on
  `github_installations`. Schema: `defaultBranch`, `autoRunOnIssueAssign`,
  `autoRunOnPR`, `triggerLabels`, `ignorePaths`.
- `GET /api/v1/orgs/:tenantId/installation/:installationId`.
- `PUT /api/v1/orgs/:tenantId/installation/:installationId/settings`.
- RBAC: admin+.
- xUnit: RBAC denial, JSON validation.
- **Commit**: `feat(installations): settings endpoints`.

### Step 8 — Webhook updates (2h)

- `installation.created` with `state` → already handled by callback path.
- `installation.created` without `state` → persist as unclaimed
  (TenantId=NULL); emit `INSTALLATION.CREATED.UNCLAIMED` event.
- `installation.deleted` → soft-delete row, orphan active repos.
- `installation.suspend` / `unsuspend` → update `SuspendedAt`.
- xUnit: 4 event cases + malformed payload → 400.
- **Commit**: `feat(webhooks): installation lifecycle events`.

### Step 9 — First-run workflow (4h)

- `FirstRunWorkflow.cs`:
  - Inputs: `tenantId`, `repoId`, `userId`.
  - Step 1: resolve installation token.
  - Step 2: `git clone` the repo into a workspace.
  - Step 3: parse README → 3-sentence summary via cheap LLM call.
  - Step 4: `POST /repos/{owner}/{repo}/issues/comments` on a seeded
    test issue (issue #1 if exists, else create one).
  - Step 5: emit `ONBOARDING.FIRST_RUN.SUCCESS`.
  - SSE progress: each step posts to `/first-run/{runId}/events`.
- `POST /api/v1/orgs/:tenantId/repos/:repoId/first-run` triggers it.
- Integration test: mocked GitHub API + Elsa test harness.
- **Commit**: `feat(onboarding): first-run test workflow`.

### Step 10 — Runbook + deployment config (1h)

- `docs/runbooks/github-app-setup.md`: operator checklist for app
  manifest URL, webhook URL, callback URL, env var list.
- `docker-compose.yml`: volume mount for PEM + env var wiring.
- **Commit**: `docs(runbooks): github app setup`.

## 6. Test strategy

### Unit tests

- `OnboardingStatusServiceTests` — 8 state combinations.
- `InstallationStateTokenTests` — issue/verify roundtrip, expired, replayed, tampered.
- `InstallationRepoSyncServiceTests` — pagination, upsert semantics.
- `GitHubAppAuthTests` — cache hit/miss; JWT claims correct; RS256 verified.
- `WebhookHandlerTests` — the 4 lifecycle events + malformed.

### Integration tests (Testcontainers + Octokit mocked)

- Full callback → link → sync → list flow.
- Claim-after-install: webhook lands first with no state, user claims
  via `POST /api/v1/onboarding/install-github/claim` (out-of-scope for
  this story — tracked as follow-up; mock test only).
- Cross-tenant denial: member of tenant A tries to list repos for
  tenant B → 404.

### Manual verification

- Local dev: spin up ngrok for webhook delivery; install app on a
  personal org; confirm row appears; activate one repo; fire first-run.

## 7. Rollback plan

- **Feature flag**: `Onboarding:Enabled` (default `true` at ship).
  Flipping to `false` disables new onboarding entries but keeps
  existing installations functional.
- **Migration rollback**: single additive migration (new columns).
  Revert drops `IsActive`, `ActivatedAt`, `ActivatedBy`.
- **Non-reversible**: GitHub App private key rotation. Once a new PEM
  is deployed, older server instances fail app JWT verification.
  Documented in runbook: rotate via blue/green, not in-place.
- **Unclaimed installations** created during soak remain in
  `github_installations` with `TenantId=NULL`. Safe: the claim UI
  will surface them; no data loss.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Onboarding status | 3 |
| 2. Install redirect + state token | 3 |
| 3. Installation callback | 4 |
| 4. GitHub App auth + Octokit wiring | 3 |
| 5. Repo sync service | 3 |
| 6. Repo listing + activation | 3 |
| 7. Installation settings | 2 |
| 8. Webhook updates | 2 |
| 9. First-run workflow | 4 |
| 10. Runbook + deploy config | 1 |
| **Total** | **28** (brief estimated 24; +4 for first-run SSE + runbook) |

## 9. Open questions

- **GitHub App private key storage**: Hetzner sealed secret? env var?
  Current plan: PEM file mounted from Docker secret at
  `/run/secrets/github-app-key.pem`; path injected via env var.
  Rotates via redeploy. Confirm with Deploy Coordinator before ship.
- **Rate-limit tolerance on repo sync**: `GET /installation/repositories`
  returns up to 30 per page. Large orgs (500 repos) → 17 API calls.
  GitHub's `installation` rate limit is 5000/hour; syncing 100 orgs
  simultaneously exceeds per-IP throughput. Plan: serialise sync per
  installation via a bounded semaphore (`SemaphoreSlim(4)`). Requires
  confirmation that the hard limit (5000/h) is the correct 2025 value.
- **Claim UI for no-state installations** — tracked as a follow-up
  story (call it `18-4b-claim-installation`). Not in this story's
  scope; the endpoint returns a `claimUrl` pointer.
- **First-run workflow cost**: LLM summarisation of README runs on
  every onboarding. Estimate 100 tokens in, 200 out per call. At
  $0.002/1k input + $0.006/1k output ≈ $0.0014 per onboarding. Safe
  as a fixed overhead. Budget alarm at 1000 onboardings/day.
- **SSE endpoint vs. polling**: Dashboard (18-5) may prefer polling
  for simplicity. Both endpoints ship; 18-5 decides.
- **Multi-installation-per-tenant semantics**: a user with multiple
  GitHub orgs may install the app multiple times into the same Tamma
  tenant. The schema allows this (many-to-one installation→tenant).
  UI disambiguates by installation account login in listings.
