# Story 31-9 Implementation Plan — Onboarding Platform Picker UI

**Status**: Planned (2026-04-21)
**Story brief**: [`31-9-onboarding-platform-picker-ui.md`](./31-9-onboarding-platform-picker-ui.md)
**Epic 31 phase**: Layer 4 — Team C (dashboard-user shell).
**Branch**: `feat/story-31-9-onboarding-platform-picker-ui`

---

## 1. Objective

Replace the hard-coded GitHub step in onboarding with a platform
picker (GitHub / Gitea / Forgejo / GitLab), per-platform credential-
entry form (OAuth2 client pair OR bot PAT for Gitea-family; PAT +
base URL for GitLab; existing GitHub App redirect preserved), a
`POST /onboarding/platform/{kind}/connect` endpoint that validates
creds + persists to `tenant_secrets` + creates a
`tenant_platform_installations` row, and a display-once webhook
secret + URL panel so the tenant admin can configure their remote.
Manual paste for non-GitHub platforms (OAuth2 redirect flow is a
follow-up). Capabilities matrix drives which scopes onboarding
exposes for CI secrets.

## 2. Dependencies

Hard blockers:

- **Story 31-2** — resolver reads the new installation row after
  connect.
- **Story 31-3** — GitHub driver (existing flow unchanged but moved).
- **Story 31-4 / 31-5** — Gitea + Forgejo drivers.
- **Story 31-6** — GitLab driver.
- **Story 29-3** — reveal-once UX primitive (webhook secret display-
  once).
- **Story 29-5** — tenant admin secret-store UI + `RevealModal`
  component.
- **Story 18-4** — existing GitHub onboarding flow (its endpoints
  get re-homed under new router paths).
- **Story 18-5** — dashboard shell.

Soft:

- **Story 31-8** — capability matrix includes secret-scope support
  per driver (for future UI disabling).

Blocks: production usability for any non-GitHub tenant.

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/packages/dashboard-user/src/pages/onboarding/platform-picker/page.tsx` | Picker page; fetches supported platforms; renders cards. |
| `/home/meywd/tamma/packages/dashboard-user/src/pages/onboarding/platform/github/page.tsx` | Re-homed GitHub install flow. |
| `/home/meywd/tamma/packages/dashboard-user/src/pages/onboarding/platform/gitea/page.tsx` | Gitea connect flow. |
| `/home/meywd/tamma/packages/dashboard-user/src/pages/onboarding/platform/forgejo/page.tsx` | Forgejo connect flow (near-identical component to Gitea). |
| `/home/meywd/tamma/packages/dashboard-user/src/pages/onboarding/platform/gitlab/page.tsx` | GitLab connect flow. |
| `/home/meywd/tamma/packages/dashboard-user/src/components/onboarding/PlatformCard.tsx` | Picker card. |
| `/home/meywd/tamma/packages/dashboard-user/src/components/onboarding/PlatformBrandTheme.ts` | Per-kind logo + colour tokens. |
| `/home/meywd/tamma/packages/dashboard-user/src/components/onboarding/CredentialEntryForm.tsx` | Shared form component; props select fields per platform. |
| `/home/meywd/tamma/packages/dashboard-user/src/components/onboarding/WebhookSetupInstructions.tsx` | Display webhook URL + secret + per-platform instructions + "copy to clipboard". |
| `/home/meywd/tamma/packages/dashboard-user/src/components/onboarding/RepoSelectorMultiSelect.tsx` | Select-repos step shared across platforms. |
| `/home/meywd/tamma/packages/dashboard-user/src/api-client/platforms.ts` | Thin wrappers: `listSupportedPlatforms`, `connectPlatform(kind, body)`, `rotateWebhookSecret(kind)`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/PlatformsEndpoints.cs` | `GET /api/v1/platforms/supported`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/OnboardingEndpoints.cs` (extends if exists) | `POST /api/v1/onboarding/platform/{kind}/connect`, `POST /api/v1/onboarding/platform/{kind}/rotate-webhook-secret`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Onboarding/PlatformConnectService.cs` | Credential validation + secret storage + installation-row creation. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/Endpoints/PlatformsEndpointsTests.cs` | `GET /supported` filtering via feature flag. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/Endpoints/PlatformConnectEndpointTests.cs` | Per-platform connect paths + validation errors. |
| `/home/meywd/tamma/packages/dashboard-user/tests/e2e/onboarding-gitea.spec.ts` | Playwright: Gitea container flow. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/packages/dashboard-user/src/router.tsx` | Add routes under `/onboarding/platform-picker` and `/onboarding/platform/{kind}`. Preserve existing `/onboarding/install-github` as alias. |
| `/home/meywd/tamma/packages/dashboard-user/src/pages/onboarding/step-2/page.tsx` (old GitHub step) | Replace body with redirect to `/onboarding/platform-picker`. |
| `/home/meywd/tamma/packages/dashboard-user/src/i18n/en.ts` | Strings for picker, forms, webhook instructions, error hints. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Program.cs` | Register `PlatformsEndpoints` + extended `OnboardingEndpoints` routes; register `PlatformConnectService`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/appsettings.json` | `Onboarding:EnabledPlatforms` list (default `["github"]` on fresh install, operators opt-in to others). |

## 5. Sequence of changes

### Step 1 — `GET /api/v1/platforms/supported` endpoint (2h)

- `PlatformsEndpoints.ListSupported`:
  1. Read `IKeyedServiceProvider` for all `IGitPlatformDriver`
     factories registered.
  2. Intersect with `Onboarding:EnabledPlatforms` config.
  3. For each, return `{ kind, displayName, baseUrlRequired,
     authModes, capabilities, description }`.
- Unit test: filter hides driver not in `EnabledPlatforms`; includes
  kind when both conditions match.
- **Commit**: `feat(api): GET /platforms/supported endpoint`.

### Step 2 — Picker page (4h)

- `PlatformCard.tsx`: logo, name, short description, "Connect" button,
  keyboard-focusable, ARIA accessible name.
- `page.tsx`: fetches `listSupportedPlatforms`; renders card grid;
  unsupported kinds hidden (feature flag).
- Route registered under `/onboarding/platform-picker`.
- Component test: card onClick navigates to `/onboarding/platform/{kind}`.
- **Commit**: `feat(onboarding): platform picker page`.

### Step 3 — Gitea/Forgejo credential form (6h)

- `CredentialEntryForm.tsx` — shared component for Gitea-family:
  - Fields: `baseUrl` (required, must start with `https://`),
    `authMode` (radio: OAuth2 / BotToken), conditional fields.
  - `webhookSecret` field pre-populated with freshly-generated
    32-byte hex (client-side `crypto.getRandomValues`).
- Gitea page + Forgejo page mount the component with respective
  `PlatformKind`.
- Form submits to `POST /api/v1/onboarding/platform/{kind}/connect`.
- On success: render `WebhookSetupInstructions` with URL +
  display-once secret.
- On validation error: render hint inline on offending field per
  backend response.
- Component tests: required-field validation; non-https rejected in
  dev-mode off; dev-mode on allows localhost.
- **Commit**: `feat(onboarding): Gitea + Forgejo credential forms`.

### Step 4 — GitLab credential form (5h)

- Gitea component reused with GitLab-specific config:
  - Fields: `baseUrl`, `tokenType` (radio: Project token / Group
    token), `token` (password-type input), `webhookSecret`.
- Submits to `POST /api/v1/onboarding/platform/gitlab/connect`.
- Tests.
- **Commit**: `feat(onboarding): GitLab credential form`.

### Step 5 — GitHub page (re-home) (2h)

- `pages/onboarding/platform/github/page.tsx`:
  - Mounts existing install-redirect logic.
  - `/onboarding/platform/github` replaces `/onboarding/install-github`
    as the canonical path; legacy path 308-redirects.
- **Commit**: `refactor(onboarding): re-home GitHub flow under picker`.

### Step 6 — `POST /onboarding/platform/{kind}/connect` endpoint (6h)

- `PlatformConnectService.ConnectAsync(kind, request, ct)`:
  1. `RequireTenantAdmin` (or owner).
  2. Build a provisional `IGitPlatformDriver` via driver factory
     + in-memory `GiteaAuth` / `GitLabAuth` from request.
  3. Dry-run validation:
     - Gitea/Forgejo: `driver.Client.GetAuthenticatedUserAsync()`
       (returns 401 if creds invalid).
     - GitLab: `driver.Client.GetAuthenticatedUserAsync()`.
     - GitHub: unused — GitHub uses the existing install redirect.
  4. If validation fails → 400 with `{ error, hint }`. Hint
     templates map backend errors:
     - 401 → "{platform} returned 401 — check your token has `repo` scope."
     - 403 → "{platform} returned 403 — token lacks required
       permissions."
     - Network error → "Cannot reach {baseUrl}. Check the URL + TLS
       config."
  5. Generate a 32-byte hex webhook secret if not supplied by
     request; hash for storage, retain raw for display-once response.
  6. Persist credentials to `tenant_secrets` (via 29-3 reveal-once
     pattern — secret is stored encrypted; metadata points to
     consumer).
  7. Insert `tenant_platform_installations` row: `platform_kind`,
     `base_url`, `credential_secret_id`, `webhook_secret_id`.
  8. Emit `PLATFORM.INSTALLATION.CONNECTED.SUCCESS` event via
     31-2's emitter.
  9. Return `{ installationId, webhookUrl:
     "https://<host>/api/webhooks/{kind}", webhookSecretDisplayOnce }`.
- Endpoint tests: dry-run 401 rejection; happy path; duplicate
  connect returns existing installation (idempotent).
- **Commit**: `feat(api): platform-connect endpoint`.

### Step 7 — Webhook secret rotate endpoint (1h)

- `POST /api/v1/onboarding/platform/{kind}/rotate-webhook-secret`:
  - Owner/admin only.
  - Generates new 32-byte hex.
  - Updates `webhook_secret_id` secret value via secret store.
  - Returns `{ webhookSecretDisplayOnce }` — display-once pattern.
- **Commit**: `feat(api): rotate-webhook-secret endpoint`.

### Step 8 — Webhook setup instructions component (2h)

- `WebhookSetupInstructions.tsx`:
  - Shows `webhookUrl`, reveals-once `webhookSecret`, copy-to-
    clipboard buttons.
  - Per-platform instructions:
    - Gitea: "Settings → Webhooks → Add Gitea Webhook → paste URL
      + secret + enable events: push, issues, pull_request,
      workflow_run."
    - GitLab: "Settings → Webhooks → URL + Secret token + event
      checkboxes."
    - GitHub: no action (webhook auto-registered via App install).
- Post-connect flow: show instructions + "I've configured the webhook"
  button → proceeds to repo selection.
- **Commit**: `feat(onboarding): WebhookSetupInstructions component`.

### Step 9 — Repo selection (multi-select) (2h)

- `RepoSelectorMultiSelect.tsx`:
  - Calls `driver.Client.ListAccessibleReposAsync` via
    `GET /api/v1/onboarding/platform/{kind}/repos` (new backend
    endpoint that proxies the driver call).
  - Multi-select checkboxes; "Select all" + search box.
  - Submit → `POST /api/v1/onboarding/platform/{kind}/repos/activate`
    with repo list (stub endpoint for Gitea/GitLab; mirrors existing
    GitHub activation logic).
- Component test: repos load, selected list submits correctly.
- **Commit**: `feat(onboarding): repo selection for non-GitHub platforms`.

### Step 10 — RBAC + feature-flag + a11y (2h)

- `TenantAdminGuard` wrap around all onboarding routes — member role
  gets 403 page.
- `Onboarding:EnabledPlatforms` config flag honoured at both endpoint
  and UI layers.
- A11y: every form field labelled; error messages aria-associated;
  focus restored on dialog close.
- **Commit**: `feat(onboarding): RBAC + feature flag + a11y`.

### Step 11 — Unit + component tests (3h)

- Picker hides unsupported platforms.
- Each form validates required fields.
- Credential-validation error renders inline hint.
- Webhook secret display-once: refresh does not re-show.
- **Commit**: `test(onboarding): component coverage`.

### Step 12 — Playwright E2E against Gitea container (2h)

- `onboarding-gitea.spec.ts`:
  1. Launch Gitea container (from 31-10 harness).
  2. Navigate to picker → Gitea → fill baseUrl + PAT + webhook
     secret.
  3. Assert 200 + webhook URL shown.
  4. Configure webhook on the Gitea container via its API using
     the returned secret.
  5. Push a commit.
  6. Assert Tamma receives the webhook + tenant id enriched.
- **Commit**: `test(e2e): Gitea onboarding Playwright flow`.

## 6. Test strategy

### Unit + component (Vitest)

- `PlatformCard`, `CredentialEntryForm`, `WebhookSetupInstructions`,
  `RepoSelectorMultiSelect`.
- Form validation (zod schemas).
- Error-copy mapping per backend response.
- Display-once behaviour (refresh doesn't re-show).

### Endpoint (xUnit)

- `PlatformsEndpoints` — feature flag + registered driver filter.
- `PlatformConnectService` — dry-run 401 / happy path / duplicate.
- `RotateWebhookSecret` — owner/admin only.

### Integration

- Subset via the Gitea container-backed Playwright flow.

### a11y

- Axe-core on each onboarding page inside the Playwright spec.

## 7. Rollback plan

- **Revert commits**: dashboard UI reverts to the hard-coded GitHub
  flow; backend `PlatformsEndpoints` + connect endpoints removed.
- **Data**: any `tenant_platform_installations` rows created during
  the rollout remain; the resolver still uses them. A full rollback
  would require `DELETE FROM tenant_platform_installations WHERE
  platform_kind != 'github';` — documented in rollback runbook.
- **Non-reversible**: customers who connected Gitea/GitLab during the
  window would lose that integration on revert. Plan: feature-flag
  `Onboarding:EnabledPlatforms` defaults to `["github"]`; operators
  opt-in per deployment. Reduces revert blast radius.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. `/platforms/supported` endpoint | 2 |
| 2. Picker page | 4 |
| 3. Gitea + Forgejo forms | 6 |
| 4. GitLab form | 5 |
| 5. Re-home GitHub page | 2 |
| 6. Connect endpoint | 6 |
| 7. Rotate webhook secret | 1 |
| 8. Webhook setup instructions | 2 |
| 9. Repo selection component | 2 |
| 10. RBAC + feature flag + a11y | 2 |
| 11. Component tests | 3 |
| 12. Playwright E2E | 2 |
| **Total** | **37** (brief: 32 — variance: form fan-out per platform is slightly larger than brief estimate; split Gitea+Forgejo shared component saves 2h; GitLab form is larger). |

## 9. Open questions

- **OAuth2 redirect for Gitea/GitLab**: brief defers to follow-up.
  Plan: first cut manual-paste. Document how a later story would
  add automatic redirect (requires per-instance OAuth app pre-
  registration by tenant owner — not universal). Follow-up story
  31-9-b.
- **Webhook URL format**: `https://<host>/api/webhooks/{kind}`. The
  `<host>` is the tenant's API origin. If Tamma is multi-tenant with
  path-based tenant routing, the URL must include tenant context.
  Plan: use the per-tenant API origin from tenant settings (Epic 30).
  For first cut, use the single Tamma API host; per-tenant routing
  follow-up.
- **Dry-run validation side effects**: `GetAuthenticatedUserAsync`
  has no write side effects but counts against the target's rate
  limit. Plan: cap validation to 5 attempts per minute per tenant.
  Document.
- **Display-once secret storage**: the raw webhook secret is returned
  once in the response. 29-3's pattern stores only the hash. If the
  user closes the modal without copying, `rotate-webhook-secret`
  re-reveals a new one. Document as recovery path.
- **Platform capability feature gating**: brief says capabilities
  matrix drives which secret scopes are pre-disabled. First cut
  does not pre-disable in the onboarding form (only connect step).
  Follow-up story for the per-secret scope picker in the secrets UI.
- **Multi-platform tenant UX**: brief supports "tenant connects one
  platform at a time; multiple platforms possible later". Plan: first
  cut shows a "connected" banner on the picker if a platform is
  already connected; user can disconnect + switch. Multi-platform
  simultaneously is a follow-up.
- **Error hint copy i18n**: hints in backend response vs localized
  client-side. Plan: backend returns an `errorCode` (enum); client
  maps to localized string via i18n catalog. Simpler than backend
  i18n.
