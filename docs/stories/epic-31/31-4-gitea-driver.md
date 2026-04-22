# Story 31-4: Gitea driver (repos / PRs / Actions dispatch / artifacts / webhooks)

Status: todo (planning brief, 2026-04-21)

## Story

As a **tenant whose repos live on a self-hosted Gitea instance**,
I want Tamma to read my repos, open PRs, dispatch Gitea Actions
workflows, monitor runs, collect artifacts, and verify inbound
webhooks,
so that I can use Tamma without moving my source to GitHub.

## Narrative

Research confirms Gitea Actions is intentionally GitHub-Actions
compatible: workflow YAML is identical, dispatch endpoint shape is
1:1, artifact protocols v1-v4 are supported, webhook HMAC is the
same SHA-256 shape. The driver is **thin**: one auth wrapper + one
REST client against the Gitea API surface.

See [`research/multi-git-platform-2026.md` §1](../research/multi-git-platform-2026.md)
for the API endpoint details.

## Acceptance Criteria

1. New driver project `apps/tamma-elsa/src/Tamma.Platforms.Gitea/`
   with:
   - `GiteaPlatformDriver : IGitPlatformDriver` — `Kind =
     PlatformKind.Gitea`.
   - `GiteaPlatformClient : IGitPlatformClient` — implemented against
     the Gitea 1.24+ REST API via a typed HTTP client (no third-
     party SDK; Gitea's REST surface is simple enough to implement
     directly with `HttpClient` + System.Text.Json).
   - `GiteaActionsPlatformClient : IGitPlatformActionsClient` —
     dispatch / runs / jobs / artifacts.
   - `Capabilities` returns `{ Actions, Artifacts, Secrets,
     WebhookHmac }` (no `LibsodiumSecrets`).
2. Auth — two modes, picked by onboarding config:
   - **OAuth2 app + refresh-token** for tenant installations (user
     consents to a Tamma OAuth2 app registered on the Gitea
     instance). Access token 1h default; refresh token managed by
     the driver and persisted to the secret store (Epic 29).
   - **Bot PAT** (personal access token on a dedicated bot account)
     for simpler deployments. Token stored in the secret store.
   Driver accepts `GiteaAuth` union: `OAuth2(clientId, clientSecret,
   refreshToken)` or `BotToken(token)`.
3. Rate-limit awareness — `X-RateLimit-*` response headers parsed
   into `RateLimitInfo` and surfaced via driver telemetry.
4. Endpoint coverage:
   - `GetRepoAsync` → `GET /api/v1/repos/{owner}/{repo}`
   - `ListRepoBranchesAsync` → `GET /api/v1/repos/{owner}/{repo}/branches`
   - `GetFileContentAsync` → `GET /api/v1/repos/{owner}/{repo}/contents/{path}?ref=`
   - `CreateBranchAsync` → `POST /api/v1/repos/{owner}/{repo}/branches`
   - `OpenPullRequestAsync` → `POST /api/v1/repos/{owner}/{repo}/pulls`
   - `GetPullRequestAsync` → `GET /api/v1/repos/{owner}/{repo}/pulls/{index}`
   - `ListPullRequestFilesAsync` → `GET /api/v1/repos/{owner}/{repo}/pulls/{index}/files`
   - `CreatePullRequestReviewCommentAsync` → `POST /api/v1/repos/{owner}/{repo}/pulls/{index}/reviews`
   - `MergePullRequestAsync` → `POST /api/v1/repos/{owner}/{repo}/pulls/{index}/merge`
   - `CreateIssueCommentAsync` → `POST /api/v1/repos/{owner}/{repo}/issues/{index}/comments`
   - `RegisterWebhookAsync` → `POST /api/v1/repos/{owner}/{repo}/hooks`
   - `DispatchWorkflowAsync` → `POST /api/v1/repos/{owner}/{repo}/actions/workflows/{workflowname}/dispatches`
   - `GetRunStatusAsync` → `GET /api/v1/repos/{owner}/{repo}/actions/runs/{run_id}`
   - `ListRunJobsAsync` → `GET /api/v1/repos/{owner}/{repo}/actions/runs/{run_id}/jobs`
   - `DownloadArtifactAsync` → `GET /api/v1/repos/{owner}/{repo}/actions/artifacts/{artifact_id}/zip`
   - `CancelRunAsync` → `POST /api/v1/repos/{owner}/{repo}/actions/runs/{run_id}/cancel`
5. Webhook signature verification helper (`GiteaWebhookSignatureVerifier`)
   implementing the 31-7 contract: reads `X-Gitea-Signature` header,
   computes HMAC-SHA256 over body with the stored webhook secret,
   constant-time compare. Shared with Forgejo driver (31-5) via
   header-name override.
6. Artifact downloads are bounded by the same `Agent:MaxArtifactBytes`
   configuration key `OctokitGitHubActionsClient` enforces (default
   4 MB). Gitea `/zip` response is a single stream — same LimitedStream
   wrapper applies.
7. Error mapping — HTTP status codes to `PlatformError`:
   - 401 → `AuthExpired`
   - 403 → `PermissionDenied`
   - 404 → `NotFound`
   - 429 → `RateLimited`
   - 5xx → `ServiceUnavailable`
   - 422 → `InvalidRequest`
   - Other → `Unknown`
8. DI extension `services.AddGiteaPlatformDriver()` registers the
   driver in the keyed collection `PlatformResolver` reads.
9. Unit tests with WireMock-style faked endpoints cover the happy
   path + each error branch of every endpoint surface. Coverage
   target ≥85% line.
10. Contract tests (shared with 31-3 + 31-6) prove
    `GiteaPlatformDriver` satisfies the same `IGitPlatformClient` +
    `IGitPlatformActionsClient` behaviour contract — the same test
    fixture runs against all drivers with their respective fake
    backends.

## Technical Context

### Typed HTTP client vs third-party SDK

Gitea's official Go client isn't C#. Third-party `Gitea.Net` exists
but is sparsely maintained. A typed HTTP client hand-written against
the surface we need (12 endpoints) is ~300 lines; better control,
no dependency drift. Same pattern `OctokitGitHubActionsClient` uses
for endpoints Octokit doesn't cover.

### Pagination

Gitea uses `page` + `limit` query params (same as GitLab). Default
`limit=50`, max `50`. Driver's listing methods auto-paginate via
`IAsyncEnumerable<T>` — same pattern Octokit uses.

### OAuth2 refresh handling

OAuth2 access tokens expire in 1h by default. Driver maintains a
short-lived cache of `(installationId → access_token)` and refreshes
on 401. Refresh failure emits
`PLATFORM.INSTALLATION.AUTH_FAILED` and fails the call with
`AuthExpired`.

## Dependencies

- **31-1** — abstraction
- **31-2** — resolver
- Blocks 31-5 (Forgejo shim reuses this driver), 31-7, 31-8, 31-9

## Estimated hours

**28h**

| Task | Hours |
|---|---|
| Typed HTTP client + auth modes + refresh | 8 |
| `IGitPlatformClient` endpoint coverage | 8 |
| `IGitPlatformActionsClient` endpoint coverage | 5 |
| Webhook signature verifier | 2 |
| Error mapping + rate-limit parsing | 2 |
| Unit + contract tests | 3 |

## Files touched

- `apps/tamma-elsa/src/Tamma.Platforms.Gitea/*.cs` (new project)
- `apps/tamma-elsa/src/Tamma.Api/Program.cs` (DI registration)
- `apps/tamma-elsa/tests/Tamma.Platforms.Gitea.Tests/*.cs` (new)

## References

- Research notes: [`../research/multi-git-platform-2026.md`](../research/multi-git-platform-2026.md) §1
- [Gitea API Usage docs](https://docs.gitea.com/development/api-usage)
- [Gitea OAuth2 Provider docs](https://docs.gitea.com/development/oauth2-provider)
