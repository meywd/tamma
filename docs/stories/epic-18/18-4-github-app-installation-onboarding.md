# Story 18-4: GitHub App Installation Onboarding

Status: planned

## Story

As a **registered user who has created an organization**,
I want to install the Tamma GitHub App and connect my repositories,
so that Tamma can receive webhooks and operate on my code.

## Acceptance Criteria

1. **Onboarding status** endpoint `GET /api/v1/onboarding/status` returns the user's onboarding progress: `{ emailVerified, hasOrg, hasInstallation, hasRepo, hasFirstRun }`
2. **Install GitHub App** endpoint `GET /api/v1/onboarding/install-github` redirects to `https://github.com/apps/tamma-dev/installations/new` with `state` parameter encoding `tenantId`
3. **Installation callback** handles the GitHub App `installation.created` webhook and links the new installation to the user's org based on the `state` parameter
4. **Repository selection** endpoint `GET /api/v1/orgs/:tenantId/repos` lists repositories available through the installation; `POST /api/v1/orgs/:tenantId/repos/activate` activates selected repos
5. **Installation settings** endpoint `GET/PUT /api/v1/orgs/:tenantId/installation` returns and updates installation-level settings (default branch, auto-run, etc.)
6. **Guided first run** endpoint `POST /api/v1/orgs/:tenantId/repos/:repoId/first-run` triggers a test workflow on a selected repo to validate the setup
7. **Onboarding completion** fires `ONBOARDING.COMPLETED.SUCCESS` event when all steps are done
8. **Resumable**: If a user abandons onboarding, they can resume from where they left off (progress persisted)
9. **Multiple installations**: An org can have multiple GitHub App installations (one per GitHub org/account)
10. **Error handling**: If the GitHub App installation fails or is cancelled, the user sees a clear error and retry option

## Tasks / Subtasks

- [ ] Task 1: Implement onboarding status tracking
  - [ ] Subtask 1.1: Create `OnboardingStatus` interface: `{ emailVerified, hasOrg, tenantId, hasInstallation, installationCount, hasActiveRepo, repoCount, hasFirstRun }`
  - [ ] Subtask 1.2: Create `packages/api/src/routes/onboarding/index.ts` route plugin
  - [ ] Subtask 1.3: Implement `GET /api/v1/onboarding/status` that queries across user, org, installation, and repo stores
  - [ ] Subtask 1.4: Write tests for each onboarding state (new user, has org, has install, complete)

- [ ] Task 2: Implement GitHub App installation redirect
  - [ ] Subtask 2.1: Implement `GET /api/v1/onboarding/install-github` endpoint
  - [ ] Subtask 2.2: Generate `state` parameter: JWT-encoded `{ tenantId, userId, nonce }` with 10-minute expiry
  - [ ] Subtask 2.3: Redirect to `https://github.com/apps/{APP_SLUG}/installations/new?state=<encoded>`
  - [ ] Subtask 2.4: Create `GET /api/v1/onboarding/install-github/callback` to handle the `installation_id` query parameter after GitHub redirects back
  - [ ] Subtask 2.5: On callback, validate `state`, link installation to org via `tenantId` from state
  - [ ] Subtask 2.6: Redirect to `dash.tamma.dev/onboarding/repos` after successful linking
  - [ ] Subtask 2.7: Write tests for state validation, expiry, installation linking

- [ ] Task 3: Update webhook handler for org-scoped installations
  - [ ] Subtask 3.1: Modify `packages/api/src/routes/github/github-webhook.ts` to handle org-scoped `installation.created` events
  - [ ] Subtask 3.2: When `installation.created` webhook arrives with `state` parameter, extract `tenantId` and link
  - [ ] Subtask 3.3: When `installation.created` arrives without `state` (installed from GitHub directly), queue for manual org linking
  - [ ] Subtask 3.4: Handle `installation.deleted` by marking installation as removed in org context
  - [ ] Subtask 3.5: Write tests for webhook scenarios

- [ ] Task 4: Implement repository selection
  - [ ] Subtask 4.1: Implement `GET /api/v1/orgs/:tenantId/repos` — list all repos across org's installations (from stored repo data + live GitHub API fallback)
  - [ ] Subtask 4.2: Implement `POST /api/v1/orgs/:tenantId/repos/activate` — accepts `{ repoIds: number[] }`, marks repos as active
  - [ ] Subtask 4.3: Implement `POST /api/v1/orgs/:tenantId/repos/deactivate` — deactivates repos
  - [ ] Subtask 4.4: Active repos are the ones Tamma watches for issues and runs workflows on
  - [ ] Subtask 4.5: Write tests for repo listing, activation, deactivation

- [ ] Task 5: Implement installation settings
  - [ ] Subtask 5.1: Define `InstallationSettings` model: `{ defaultBranch, autoRunOnIssueAssign, autoRunOnPR, triggerLabels, ignorePaths }`
  - [ ] Subtask 5.2: Implement `GET /api/v1/orgs/:tenantId/installation/:installationId` — return installation details + settings
  - [ ] Subtask 5.3: Implement `PUT /api/v1/orgs/:tenantId/installation/:installationId/settings` — update settings (admin+)
  - [ ] Subtask 5.4: Write tests

- [ ] Task 6: Implement guided first run
  - [ ] Subtask 6.1: Implement `POST /api/v1/orgs/:tenantId/repos/:repoId/first-run`
  - [ ] Subtask 6.2: Create a lightweight test workflow: clone repo -> analyze README -> create a test issue comment
  - [ ] Subtask 6.3: Return `{ runId, status: 'started' }` with SSE endpoint for progress updates
  - [ ] Subtask 6.4: On completion, mark onboarding as complete
  - [ ] Subtask 6.5: Emit `ONBOARDING.COMPLETED.SUCCESS` event
  - [ ] Subtask 6.6: Write tests for first-run trigger and completion

## Technical Context

### Existing Code to Modify

| File | Change |
|------|--------|
| `packages/api/src/persistence/installation-store.ts` | Add `tenantId` field to `GitHubInstallation`, add `listByOrgId()` method |
| `packages/api/src/routes/github/github-webhook.ts` | Handle org-scoped installation linking |
| `packages/api/src/routes/github/github-callback.ts` | Handle installation callback with state parameter |

### New Files to Create

| File | Purpose |
|------|---------|
| `packages/api/src/routes/onboarding/index.ts` | Onboarding status + GitHub install redirect + callback |
| `packages/api/src/routes/orgs/repos.ts` | Repo listing + activation endpoints |
| `packages/api/src/routes/orgs/installation-settings.ts` | Installation settings endpoints |

### GitHub App Installation Flow

```
User clicks "Connect GitHub" on dash.tamma.dev
    |
    v
GET /api/v1/onboarding/install-github
    |  (generates state JWT with tenantId, userId)
    v
Redirect to: https://github.com/apps/tamma-dev/installations/new?state=<jwt>
    |
    v
User selects org/repos on GitHub, clicks "Install"
    |
    v  (GitHub sends two signals simultaneously)
    |
    +---> Webhook: POST /api/github/webhooks (installation.created)
    |       |
    |       v
    |     Store installation, try to link to org via setup_action metadata
    |
    +---> Redirect: GET /api/v1/onboarding/install-github/callback?installation_id=123&state=<jwt>
            |
            v
          Validate state JWT, link installation to tenantId
            |
            v
          Redirect to dash.tamma.dev/onboarding/repos
```

### State Parameter Security

The `state` parameter is a compact JWT (HS256) with:
```typescript
{
  tenantId: string;
  userId: string;
  nonce: string;  // Prevents replay
  exp: number;    // 10-minute expiry
}
```

This is signed with the same `JWT_SECRET` used for session tokens. On callback, the state is verified and the nonce is checked against a short-lived store to prevent replay.

### Interaction with Existing Installation Model

The current `GitHubInstallation` model has no `tenantId`. This story adds it:

The `github_installations` table already has a `tenant_id` column from Epic 17 Story 17-1 (referencing `tenants(id)`). No new column is needed. The onboarding flow links new installations to the user's active tenant by setting `tenant_id` on the installation row.

## Implementation Notes

- GitHub App installations can be initiated two ways: (1) from Tamma's onboarding flow (has `state` with `tenantId`), or (2) from GitHub Marketplace / the app page directly (no `state`). For case (2), the installation is stored but not linked to an org until the user claims it via a "link installation" UI.
- The `setup_url` in the GitHub App manifest should be set to `https://api.tamma.dev/api/v1/onboarding/install-github/callback` so GitHub redirects back after installation.
- The first-run test workflow is intentionally lightweight -- it should complete in under 30 seconds and produce a visible result (e.g., a comment on a test issue) so the user knows the setup works.
- Repository activation is separate from GitHub App permissions. Even if the GitHub App has access to all repos, the user chooses which ones Tamma actively monitors.

## Dependencies

- **18-2**: User authentication (JWT sessions)
- **18-3**: Tenant membership model (using `tenants` table from Epic 17)
- **Epic 17 Story 17-1**: Tenant model (the `tenants` table)

## Estimated Effort

**Medium (3 days)**:
- Day 1: Onboarding status + GitHub App install redirect/callback + state management
- Day 2: Repo listing + activation + installation settings
- Day 3: First-run workflow + webhook updates + integration tests

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-28 | 1.0.0 | Initial story creation | Architecture Team |
