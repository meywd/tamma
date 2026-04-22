# Story 31-4 Implementation Plan — Gitea Driver

**Status**: Planned (2026-04-21)
**Story brief**: [`31-4-gitea-driver.md`](./31-4-gitea-driver.md)
**Epic 31 phase**: Layer 4 — parallel with 31-5, 31-9 picker.
**Branch**: `feat/story-31-4-gitea-driver`

---

## 1. Objective

Ship `GiteaPlatformDriver` against Gitea 1.25+ REST API: typed HTTP
client, OAuth2-or-bot-PAT auth, repo/PR/issue/branch endpoints,
Actions dispatch + run monitor + artifact download + cancel, webhook
signature verification (shared with 31-5 Forgejo), plaintext secret
provisioning (consumed by 31-8). Driver is thin because Gitea
Actions is intentionally GitHub-Actions-compatible — URL shapes are
1:1 except where research §1 called out divergence.

## 2. Dependencies

Hard blockers:

- **Story 31-1** — abstraction interfaces + models.
- **Story 31-2** — resolver + `tenant_platform_installations` +
  `ISecretStore` credential load.

Soft:

- **Story 29-2** — secret store backing.

Blocks: **31-5** (Forgejo shim), **31-7** (webhook abstraction reuses
the Gitea HMAC verifier), **31-8** (secrets provisioner consumes
this driver), **31-9** (onboarding picker shows Gitea), **31-10**
(integration harness includes Gitea container).

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.Gitea/Tamma.Platforms.Gitea.csproj` | New driver project. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.Gitea/GiteaPlatformDriver.cs` | `IGitPlatformDriver` impl; `Kind = PlatformKind.Gitea`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.Gitea/GiteaPlatformClient.cs` | `IGitPlatformClient` impl. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.Gitea/GiteaActionsPlatformClient.cs` | `IGitPlatformActionsClient` impl. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.Gitea/GiteaHttpClient.cs` | Typed HTTP client wrapping `HttpClient` — auth header, rate-limit parse, error mapping, retries. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.Gitea/GiteaAuth.cs` | Union: `OAuth2(clientId, clientSecret, refreshToken)` or `BotToken(token)`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.Gitea/GiteaOAuth2TokenCache.cs` | Short-lived `(installationId → accessToken)` cache; refresh on 401. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.Gitea/GiteaWebhookSignatureVerifier.cs` | HMAC-SHA256 verifier; configurable header-name list (for Forgejo reuse). |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.Gitea/GiteaErrorMapper.cs` | HTTP status → `PlatformError`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.Gitea/GiteaPlatformDriverFactory.cs` | Factory plugged into 31-2 resolver via keyed DI. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.Gitea/Dtos/*.cs` | Internal DTOs for wire deserialization (Repo, Branch, PR, Issue, Run, Job, Artifact, Webhook, Secret). |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.Gitea/GiteaDriverRegistrationExtensions.cs` | `services.AddGiteaPlatformDriver()` extension. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.Gitea/README.md` | Driver docs: endpoint mapping, OAuth2 flow, pagination conventions. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Platforms.Gitea.Tests/Tamma.Platforms.Gitea.Tests.csproj` | Test project. |
| `.../GiteaPlatformClientTests.cs` | WireMock-based unit tests per endpoint + error branch. |
| `.../GiteaActionsPlatformClientTests.cs` | Dispatch + runs + artifacts. |
| `.../GiteaWebhookSignatureVerifierTests.cs` | Valid / invalid / missing-secret. |
| `.../GiteaOAuth2TokenCacheTests.cs` | Refresh-on-401 path. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/Tamma.sln` | Add Platforms.Gitea + test project. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Program.cs` | `services.AddGiteaPlatformDriver();` |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.Abstractions/PlatformKindCapabilityMatrix.cs` | Ensure Gitea row matches implemented surface (no `LibsodiumSecrets`, has `WebhookHmac`, `Actions`, `Artifacts`, `Secrets`). |

## 5. Sequence of changes

### Step 1 — Driver project scaffolding (1h)

- csproj referencing `Tamma.Platforms.Abstractions`, `System.Text.Json`,
  `Microsoft.Extensions.Http`.
- `GiteaPlatformDriver` shell returning capability set.
- Wire to solution.
- **Commit**: `feat(platforms.gitea): project scaffolding`.

### Step 2 — Typed HTTP client + auth (5h)

- `GiteaHttpClient` constructor: `HttpClient` (named client from
  `IHttpClientFactory`), `GiteaAuth`, `GiteaOAuth2TokenCache`.
- `SendAsync(HttpRequestMessage)`:
  1. Add `Authorization: token <value>` (bot) or
     `Authorization: token <access>` (OAuth2; loaded from cache or
     refreshed).
  2. On 401 with OAuth2 mode, attempt one refresh via
     `POST /login/oauth/access_token` with `grant_type=refresh_token`.
     Retry once.
  3. On all responses, parse `X-RateLimit-*` into `RateLimitInfo` and
     attach to outgoing diagnostic scope.
- `GiteaOAuth2TokenCache`: in-memory dict keyed by `installationId`,
  with TTL = `expires_in - 60s`.
- Unit tests: 401→refresh→retry happy path; double-401 path →
  `AuthExpired` + `PLATFORM.INSTALLATION.AUTH_FAILED` emission.
- **Commit**: `feat(platforms.gitea): typed HTTP client + OAuth2 refresh`.

### Step 3 — Error mapper (1h)

- HTTP status → `PlatformError` per brief AC7:
  - 401 → `AuthExpired`
  - 403 → `PermissionDenied`
  - 404 → `NotFound`
  - 422 → `InvalidRequest(parsed_error_code, hint)` — parse Gitea's
    `{ "message": "…", "url": "…" }` JSON.
  - 429 → `RateLimited(retryAfter from Retry-After header)`
  - 5xx → `ServiceUnavailable`
- Table-driven unit test.
- **Commit**: `feat(platforms.gitea): error mapper`.

### Step 4 — `GiteaPlatformClient` — repo + branch + file (4h)

- `GetRepoAsync`: `GET /api/v1/repos/{owner}/{repo}` → DTO →
  `Models.Repo`.
- `ListRepoBranchesAsync`: paginated via `page=` + `limit=50`.
  Returns `IAsyncEnumerable<Branch>`.
- `GetFileContentAsync`: `GET /api/v1/repos/{owner}/{repo}/contents/{path}?ref=`.
  Gitea returns base64-encoded content in JSON; decode before
  returning.
- `CreateBranchAsync`: `POST /api/v1/repos/{owner}/{repo}/branches`
  with `{ new_branch_name, old_branch_name }`.
- WireMock tests for each.
- **Commit**: `feat(platforms.gitea): repo + branch + file endpoints`.

### Step 5 — `GiteaPlatformClient` — PR + issue comments + webhooks (4h)

- `OpenPullRequestAsync`: `POST /api/v1/repos/{owner}/{repo}/pulls`.
- `GetPullRequestAsync`: `GET …/pulls/{index}`.
- `ListPullRequestFilesAsync`: `GET …/pulls/{index}/files`.
- `CreatePullRequestReviewCommentAsync`: `POST …/pulls/{index}/reviews`
  with `{ body, event:"COMMENT", comments:[{ path, new_position, body }] }`.
- `MergePullRequestAsync`: `POST …/pulls/{index}/merge`.
- `CreateIssueCommentAsync`: `POST …/issues/{index}/comments`.
- `RegisterWebhookAsync`: `POST …/hooks` with `{ type:"gitea", config:{ url, content_type, secret }, events, active:true }`.
- `ListAccessibleReposAsync`: `GET /api/v1/user/repos?page=&limit=50` paginated.
- **Commit**: `feat(platforms.gitea): PR + issue + webhook endpoints`.

### Step 6 — `GiteaActionsPlatformClient` (4h)

- `DispatchWorkflowAsync`: `POST /api/v1/repos/{owner}/{repo}/actions/workflows/{workflowname}/dispatches`
  with `{ ref, inputs }`. Maps 1:1 to GitHub.
- `GetRunStatusAsync`: `GET …/actions/runs/{run_id}`.
- `ListRunJobsAsync`: `GET …/actions/runs/{run_id}/jobs`.
- `DownloadArtifactAsync`: `GET …/actions/artifacts/{artifact_id}/zip` —
  returns a `Stream` wrapped in the existing `LimitedStream` (default
  4MB cap, configurable via `Agent:MaxArtifactBytes`).
- `CancelRunAsync`: `POST …/actions/runs/{run_id}/cancel`.
- WireMock tests for each.
- **Commit**: `feat(platforms.gitea): Actions endpoints`.

### Step 7 — Webhook signature verifier (2h)

- `GiteaWebhookSignatureVerifier`:
  - Constructor takes `ILogger<>` + `Func<string, string?>` header
    reader + header-name list (default `["X-Gitea-Signature"]`).
  - `VerifyAsync(bodyBytes, secret, getHeader)`:
    1. Find first non-null header from the list.
    2. Compute HMAC-SHA256(bodyBytes, secret) → hex lowercase.
    3. Constant-time compare via `CryptographicOperations.FixedTimeEquals`.
  - Missing secret → `ServiceUnavailable` (fail-closed per audit
    finding 001).
- Unit tests: valid signature accepted, wrong signature rejected,
  missing header rejected, missing secret → fail-closed.
- Exposed to 31-7 via `Tamma.Platforms.Abstractions.IWebhookSignatureVerifier`
  (declared in 31-1; this class implements).
- **Commit**: `feat(platforms.gitea): webhook HMAC verifier`.

### Step 8 — Driver factory + DI extension (2h)

- `GiteaPlatformDriverFactory.BuildAsync(installation, secrets, ct)`:
  1. Load credential via `secrets.GetAsync(row.CredentialSecretId)`.
     Decode into `GiteaAuth`.
  2. Build `GiteaHttpClient` targeting `row.BaseUrl`.
  3. Wrap in `GiteaPlatformClient` + `GiteaActionsPlatformClient`.
  4. Return `GiteaPlatformDriver`.
- `AddGiteaPlatformDriver()` registers factory + all helpers under
  keyed DI `PlatformKind.Gitea`.
- **Commit**: `feat(platforms.gitea): factory + DI extension`.

### Step 9 — Contract test harness hook (2h)

- Implement `abstract class GitPlatformClientContractTests<TFixture>`
  subclass for Gitea — hooks into WireMock fixture for unit
  (Step 10 below adds container harness).
- Executes the 12 contract methods + verifies identical responses
  to the baseline (GitHub-style shape).
- **Commit**: `test(platforms.gitea): contract test suite`.

### Step 10 — Unit + WireMock coverage pass (2h)

- Run coverage; ensure ≥85% line coverage on the driver surface.
- Document gaps + accept.
- **Commit**: `test(platforms.gitea): coverage`.

### Step 11 — Docs (1h)

- `README.md` in driver project — endpoint mapping table, auth flow,
  known caveats (e.g. 1.25 secrets-scope-4 support, artifact
  protocols v1-v4 both supported).
- **Commit**: `docs(platforms.gitea): driver README`.

## 6. Test strategy

### Unit (WireMock)

- Every `IGitPlatformClient` method: happy path + error mapping per
  HTTP status.
- Every `IGitPlatformActionsClient` method: dispatch, monitor,
  artifacts.
- `GiteaHttpClient`: OAuth2 refresh happy path, double-401 failure.
- `GiteaWebhookSignatureVerifier`: valid / wrong / missing / no
  secret.
- Pagination: auto-paginate yields across `page=1,2,3` test fixtures.

### Integration (31-10 harness)

- Separate story owns the container harness. This story ships unit
  tests that 31-10 re-runs against the Gitea container.

### Contract

- `GitPlatformClientContractTests<GiteaFixture>` — shared suite
  from 31-1 + subclassed here.

## 7. Rollback plan

- **Revert commits**: removes project + DI registration. Empty
  capability set for `PlatformKind.Gitea` in the resolver means any
  tenant that connected a Gitea install stops working but doesn't
  corrupt anything. Onboarding UI (31-9) hides the Gitea card when
  driver not registered — so reverts cleanly.
- **Non-reversible**: none. No migration in this story.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Project scaffolding | 1 |
| 2. Typed HTTP client + OAuth2 refresh | 5 |
| 3. Error mapper | 1 |
| 4. Repo/branch/file | 4 |
| 5. PR/issue/webhook endpoints | 4 |
| 6. Actions endpoints | 4 |
| 7. Webhook verifier | 2 |
| 8. Factory + DI | 2 |
| 9. Contract test subclass | 2 |
| 10. Coverage pass | 2 |
| 11. Docs | 1 |
| **Total** | **28** (matches brief). |

## 9. Open questions

- **Third-party SDK vs hand-rolled client**: brief recommends hand-
  rolled (`Gitea.Net` NuGet sparsely maintained). Plan: hand-rolled
  with System.Text.Json. Documented trade-off: dependency drift risk
  is on our side; test coverage is our safety net.
- **OAuth2 app auto-registration**: Gitea OAuth2 apps must be pre-
  registered on the target instance by the tenant (research §1,
  §8). Plan: onboarding UI (31-9) provides instructions; driver
  assumes clientId + clientSecret already provisioned.
- **Pagination limit 50 vs 1000**: Gitea default `limit=50`, max
  `50`. GitHub uses `per_page=100`. Different constants → auto-
  paginator uses platform-specific max.
- **Actions runner dependency at runtime**: Actions dispatch requires
  a registered runner on the target instance. Driver assumes so;
  if no runner, dispatch succeeds but run sits in "queued" forever.
  Plan: document as operator concern; onboarding UI eventually runs
  a pre-flight check that a runner exists. First cut skips the
  check.
- **Artifact v1-v4 dual support**: research §1 notes Gitea supports
  both protocol v1-v3 and v4. v4 is a single compressed zip;
  v1-v3 is multi-file. Plan: driver unconditionally GETs the `/zip`
  endpoint — Gitea handles the translation server-side. Document.
- **Webhook secret format**: Gitea accepts arbitrary strings;
  research suggests 32-byte hex. Plan: onboarding UI generates 32-
  byte hex, driver just passes through.
- **Base URL normalization**: `https://gitea.example.com/` vs
  `https://gitea.example.com` (trailing slash). Plan: `GiteaHttpClient`
  trims trailing slash on construction. Document.
- **Secrets scope 4 (global, user, org, repo)**: research §1 + §10
  surprise — Gitea has more scopes than GitHub. Plan: 31-8 exposes
  `CiSecretScope.User` in the enum; Gitea driver accepts it,
  GitHub driver rejects with `scope_not_supported_on_platform`.
