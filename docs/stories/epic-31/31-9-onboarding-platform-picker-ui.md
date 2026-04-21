# Story 31-9: Onboarding UI — tenant picks platform + enters credentials

Status: todo (planning brief, 2026-04-21)

## Story

As a **tenant owner setting up their Tamma tenant**,
I want to pick which git platform my repos live on (GitHub, Gitea,
Forgejo, GitLab) and walk through a platform-specific credential /
webhook setup flow,
so that my tenant is provisioned with working outbound calls +
inbound webhooks on my platform of choice by the time I hit
"Finish".

## Narrative

Today Story 18-4 hard-codes the GitHub App install flow. Every
tenant sees the same page, the same `github.com/apps/tamma-dev/installations/new`
redirect, and the same return-from-GitHub flow.

Post-31-9, the onboarding wizard step 2 ("Connect your git
platform") becomes platform-aware:

- **Picker** — shows each supported platform with branding + a short
  description. Platforms the tenant's Tamma-instance doesn't support
  (e.g. Bitbucket / Azure DevOps pre-31-11/31-12) are hidden.
- **Per-platform flow** — one of:
  - **GitHub** — unchanged; existing `GET /api/v1/onboarding/install-github`
    flow.
  - **Gitea / Forgejo** — user enters the base URL + OAuth2 app
    client ID/secret (copy-paste from their Gitea admin) OR a bot
    PAT. Tamma validates via `GET /api/v1/user` on the target
    instance + creates a webhook on a test repo to verify the
    webhook secret round-trip.
  - **GitLab** — user enters base URL + project access token or
    group access token. Tamma validates via `GET /api/v4/user`,
    registers the webhook secret, creates the tenant_platform_
    installation row.

Credential entry is **manual paste** — no automated OAuth redirects
for Gitea/GitLab in first cut. Manual paste is dead-simple to ship +
self-explanatory.

## Acceptance Criteria

1. New dashboard routes under `dash.tamma.dev/onboarding`:
   - `/onboarding/platform-picker` — replaces the hard-coded Step 2
     of 18-4.
   - `/onboarding/platform/github` — existing GitHub flow, moved
     under the new namespace with no behaviour change.
   - `/onboarding/platform/gitea` — new page.
   - `/onboarding/platform/forgejo` — new page.
   - `/onboarding/platform/gitlab` — new page.
2. Platform picker page:
   - Fetches the list of supported platforms from `GET
     /api/v1/platforms/supported` (new endpoint; returns the
     capability matrix entries backed by drivers registered in DI).
   - Renders a card per platform: logo, name, short description,
     "Connect" button.
   - Unsupported platforms (Bitbucket / Azure DevOps pre-31-11/
     31-12) are hidden — feature flag via the supported list.
3. **Gitea / Forgejo onboarding page** (same component, branded by
   `PlatformKind` passed as prop):
   - Form fields: `baseUrl` (required, validated as
     https://, non-localhost unless dev mode), `authMode`
     (radio: OAuth2 / BotToken), `clientId` +
     `clientSecret` (if OAuth2) OR `token` (if BotToken), plus a
     `webhookSecret` field pre-populated with a freshly-generated
     32-byte hex.
   - Submit calls new endpoint `POST /api/v1/onboarding/platform/{kind}/connect`
     which:
     - Validates credentials via a dry-run API call
       (`GET /api/v1/user` or equivalent).
     - Stores credentials in the secret store (Epic 29) as
       `tenant_secrets` rows with scope = tenant.
     - Creates a `tenant_platform_installations` row with references
       to the secret rows.
     - Returns `{ installationId, webhookUrl, webhookSecretDisplayOnce }`
       — the display-once URL + secret are shown in the UI with a
       "copy to clipboard" button + copy explicit instructions for
       the user to configure on their Gitea/Forgejo side.
   - After connect: page shows setup instructions "Add a webhook to
     your repo at `{webhookUrl}` with secret `{webhookSecret}` and
     event `push`, `issues`, `pull_request`, `workflow_run`".
4. **GitLab onboarding page**:
   - Form fields: `baseUrl` (required), `tokenType` (radio: Project
     token / Group token), `token`, `webhookSecret`.
   - Validation calls `GET /api/v4/user` on the target base URL
     with the provided token.
   - Stores in secret store, creates `tenant_platform_installations`
     row, returns webhook URL + token.
5. **GitHub onboarding page** — unchanged behaviour; path moved to
   `/onboarding/platform/github`. Keeps the existing
   `GET /api/v1/onboarding/install-github` redirect.
6. Credential validation error handling — if the dry-run call fails,
   the endpoint returns 400 with `{ error, hint }` and the UI
   renders the hint ("Gitea returned 401 — check your token has
   `repo` scope") inline on the failing field.
7. After connect, a "Select repos" sub-step fetches the repos via
   the driver's `IGitPlatformClient.ListAccessibleReposAsync` (new
   method on the abstraction — add to 31-1 if not already there) and
   renders a multi-select. Selected repos are marked active.
   (Note: GitHub flow already has this via 18-4; Gitea/Forgejo/GitLab
   reuse the same UI component with the platform-neutral list.)
8. Feature-flagged platforms — the `GET /api/v1/platforms/supported`
   endpoint returns only kinds where a driver is registered in DI
   **and** an `Onboarding:EnabledPlatforms` config key permits them.
   Lets operators roll out Gitea before GitLab independently.
9. Accessibility — every form field has a label, every error message
   is ARIA-associated, the picker's platform cards are buttons with
   accessible names.
10. RBAC — only tenant owner + admin can reach the onboarding flow
    (per `rbac-unified-model.md`). Member gets a 403 page with "ask
    your admin to finish tenant setup" copy.
11. Unit tests:
    - Picker hides unsupported platforms.
    - Each per-platform form validates required fields + calls the
      correct endpoint.
    - Credential-validation errors render inline hint.
    - Webhook secret display-once: refreshing the page after
      connect does **not** re-show the secret (matches Story 29-3
      reveal-once pattern).
12. Playwright E2E: Gitea container (from 31-10 harness) accessible
    at test-time. Onboarding flow picks Gitea, enters PAT, connects,
    sees webhook URL, configures webhook on the container, pushes a
    commit, asserts Tamma receives the webhook + links to the
    tenant.

## Technical Context

### Why manual paste vs OAuth redirect for Gitea/GitLab

OAuth redirects require Tamma to be pre-registered as an OAuth2 app
on each target instance. For self-hosted Gitea / GitLab, that's per-
tenant infrastructure the tenant owner would have to set up
beforehand. Manual paste is universal: one flow works for cloud +
self-hosted + air-gapped deployments. Later stories can add the
OAuth redirect as an upgrade path.

### Endpoint: `GET /api/v1/platforms/supported`

Lives in a new `PlatformsEndpoints.cs`. Reads the registered
`IGitPlatformDriver` set from DI, intersects with the
`Onboarding:EnabledPlatforms` config. Returns
`[{ kind, displayName, baseUrlRequired, authModes, capabilities,
description }]`.

### Reveal-once for webhook secret

Same pattern as Epic 29's reveal-once for new secrets (Story 29-3).
The freshly-generated webhook secret is returned once in the
connect response; stored only as a SHA-256 hash of itself in the
secret store for later verification. If the user loses it before
configuring the remote webhook, they can call
`POST /api/v1/onboarding/platform/{kind}/rotate-webhook-secret`
which rotates the value and re-reveals once.

### Dependencies on 29-5

Credential storage uses the Epic 29 tenant-secret store UI's
"reveal once on create" pattern from 29-3 via the back-end primitive.
UI component reuse of `RevealModal` from 29-5.

## Dependencies

- **31-2** — resolver (to look up the new installation after connect)
- **31-3** — GitHub driver (existing path unchanged)
- **31-4 / 31-5** — Gitea / Forgejo drivers
- **31-6** — GitLab driver
- **29-3 / 29-5** — reveal-once pattern + credential store
- **Story 18-4** — existing GitHub onboarding flow (gets moved + re-
  hosted under the new router)
- **Story 18-5** — dashboard shell (the router lives here)

## Estimated hours

**32h**

| Task | Hours |
|---|---|
| `GET /api/v1/platforms/supported` endpoint | 2 |
| Picker page + component | 4 |
| Gitea / Forgejo onboarding page (shared component) | 6 |
| GitLab onboarding page | 5 |
| `POST /onboarding/platform/{kind}/connect` endpoint × 3 kinds | 6 |
| Move GitHub onboarding path + legacy alias | 2 |
| RBAC guard + feature-flag wiring | 2 |
| Unit + component tests | 3 |
| Playwright E2E (Gitea container) | 2 |

## Files touched

- `packages/dashboard-user/src/pages/onboarding/platform-picker/page.tsx` (new)
- `packages/dashboard-user/src/pages/onboarding/platform/{github,gitea,forgejo,gitlab}/page.tsx` (new per-kind)
- `packages/dashboard-user/src/components/onboarding/PlatformCard.tsx` (new)
- `packages/dashboard-user/src/components/onboarding/CredentialEntryForm.tsx` (new)
- `packages/dashboard-user/src/components/onboarding/WebhookSetupInstructions.tsx` (new)
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/PlatformsEndpoints.cs` (new)
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/OnboardingEndpoints.cs` (new or modify)
- `apps/tamma-elsa/tests/Tamma.Api.Tests/Endpoints/PlatformsEndpointsTests.cs` (new)
- `packages/dashboard-user/tests/e2e/onboarding-gitea.spec.ts` (new)

## Non-goals

- Does not implement OAuth2 redirect flow for self-hosted
  Gitea/GitLab (follow-up story).
- Does not implement per-tenant IdP SSO (Epic 33, deferred).
- Does not support Bitbucket / Azure DevOps until 31-11 / 31-12
  land.

## References

- Research notes: [`../research/multi-git-platform-2026.md`](../research/multi-git-platform-2026.md) §8
- Existing GitHub flow: [`../epic-18/18-4-github-app-installation-onboarding.md`](../epic-18/18-4-github-app-installation-onboarding.md)
- Reveal-once pattern: [`../epic-29/29-3-reveal-once-on-create.md`](../epic-29/29-3-reveal-once-on-create.md)
- Unified RBAC: [`../rbac-unified-model.md`](../rbac-unified-model.md)
