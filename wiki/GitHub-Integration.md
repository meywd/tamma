# GitHub Integration

Tamma talks to GitHub via three surfaces:

1. **GitHub App** — installation-scoped JWTs for repo operations, webhook delivery, PR/issue automation.
2. **GitHub OAuth** — user sign-in for the dashboard + Studio.
3. **GitHub Actions** — per-tenant workflow_dispatch runs for the SaaS executor.

All three share the same `GitHub:AppId` + `GitHub:PrivateKey` env vars. If either is unset, every GitHub surface swaps to a Null seam that reports `github_client_not_configured` — clean operator error, never silent success.

## GitHub App client

`OctokitGitHubAppClient` (`apps/tamma-elsa/src/Tamma.Api/Services/GitHub/OctokitGitHubAppClient.cs`) is the real implementation. It uses the GitHub App authentication flow:

1. Mint a 10-minute **app JWT** from the App's private key (`RS256`).
2. Exchange for an installation access token per installation (`POST /app/installations/{id}/access_tokens`).
3. Cache the installation token until expiry (per Octokit's built-in cache).
4. All repo operations go through the installation client — permissions scoped to just that installation.

DI picks the real client when `GitHub:AppId > 0` AND `GitHub:PrivateKey` is non-empty. Otherwise `NullGitHubAppClient` returns 503.

### Covered operations

| Operation | Purpose |
|-----------|---------|
| List installation repos | Used by OAuth callback to auto-link user to installations |
| Get repo + branch protection | Pre-flight for PR creation |
| Create / update / merge PR | Autonomous Development Loop core |
| Create issue, add comment, apply labels | Triage workflow |
| Create / update branch | Branch creation activity |
| Upload / download artifacts | Agent result collection (indirect, via Actions client) |
| Write repo secrets | Via `LibsodiumGitHubSecretsProvisioner` (see below) |

## GitHub OAuth (user sign-in)

`/api/auth/github` redirects to GitHub's authorize URL with:

- `client_id` — `GitHub:ClientId`.
- `scope` — `read:user user:email` (nothing write-scoped — write access comes via the App).
- `state` — base64url-encoded `{ rd, invite }` JSON. **Required** (auth/009 fix); without state the flow is CSRF-vulnerable and can't carry invite / redirect-destination metadata.

`/api/auth/github/callback`:

1. Verify the `state` decodes and the signed portion round-trips.
2. `POST /login/oauth/access_token` to exchange code → token.
3. Fetch user profile + primary email (may be null for users who hide it).
4. Process invite (`state.invite`) if present — attach the new user to the invited tenant with the invited role.
5. Upsert user by `github_id`. Users without a public email get `email = NULL` (auth/026 fix — schema allows it).
6. Auto-link user to every active installation the user belongs to (`user_installations` table, auth/023 fix).
7. Issue the session JWT as an HttpOnly cookie `tamma_session` on `Domain=.tamma.dev` (auth/004 fix).
8. Redirect to `state.rd` (or `/` by default).

## GitHub webhooks

`POST /api/github/webhooks` verifies the `X-Hub-Signature-256` HMAC using `GitHub:WebhookSecret`. **Fail-closed**: if the secret is unset, every webhook is rejected (github/all-11-findings fix — the TS port was accepting them).

Supported events:

| Event | Action | Purpose |
|-------|--------|---------|
| `installation` | `created`, `deleted`, `suspend`, `unsuspend` | Install router state machine |
| `installation_repositories` | `added`, `removed` | Update installed-repo cache |
| `workflow_run` | `completed` | Wake `AgentMonitorService` (faster than polling) |
| `pull_request` | `opened`, `synchronize`, `closed` | Feed the review / merge-complete activities |
| `issue_comment` | `created` | Triage hook |

Delivery IDs are idempotency-keyed — the same `X-GitHub-Delivery` UUID is rejected on second delivery.

## GitHub Actions client

`OctokitGitHubActionsClient` (same `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/` directory) wraps `workflow_dispatch` + run polling + artifact download. It's consumed by `GitHubActionsExecutor` — see [Agent Dispatch](Agent-Dispatch).

Null seam: `NullGitHubActionsClient` returns `NotConfigured` so dispatches fail loudly when the App isn't wired.

## Libsodium secrets provisioner

To run the SaaS executor on a tenant's repo, Tamma writes agent secrets (API keys, callback tokens) into the repo's Actions secrets. **Never plaintext** — `LibsodiumGitHubSecretsProvisioner`:

1. `GET /repos/{owner}/{repo}/actions/secrets/public-key` — fetch repo's libsodium public key.
2. Seal plaintext in a libsodium sealed box (public-key, no shared secret).
3. `PUT /repos/{owner}/{repo}/actions/secrets/{name}` with the base64-encoded ciphertext.

Depends on `Sodium.Core`. `NullGitHubSecretsProvisioner` is the fallback when sodium isn't wired.

## Installation router

`InstallationRouterService` maps `(owner, repo)` → installation ID. Caches in-memory, invalidated by `installation_repositories` webhooks. See engine/023 fix notes for the cache-invalidation hardening that landed this sprint.

## Related source files

| Path | Purpose |
|------|---------|
| `Services/GitHub/OctokitGitHubAppClient.cs` | Real GitHub App client |
| `Services/GitHub/NullGitHubAppClient.cs` | Null seam |
| `Services/GitHub/OctokitGitHubActionsClient.cs` | Real Actions client |
| `Services/GitHub/LibsodiumGitHubSecretsProvisioner.cs` | Secrets encryption |
| `Services/GitHub/InstallationRouterService.cs` | Owner/repo → installation lookup |
| `Services/Engine/OctokitGitHubEngineCallbackService.cs` | Engine callback surface (posts PR comments, closes issues, etc.) |

## Related

- [Agent Dispatch](Agent-Dispatch)
- [Deployment → GitHub App env vars](Deployment#github-app-optional-activates-saas--actions-executor)
- [Security → Libsodium secrets provisioning](Security#github-secrets-provisioning-libsodium)
