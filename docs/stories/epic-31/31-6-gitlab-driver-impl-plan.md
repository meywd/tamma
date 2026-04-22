# Story 31-6 Implementation Plan — GitLab Driver

**Status**: Planned (2026-04-21)
**Story brief**: [`31-6-gitlab-driver.md`](./31-6-gitlab-driver.md)
**Epic 31 phase**: Layer 5 — heaviest non-optional driver; different
CI model from GitHub Actions / Gitea.
**Branch**: `feat/story-31-6-gitlab-driver`

---

## 1. Objective

Ship `GitLabPlatformDriver` against GitLab 17.x+ REST API (Cloud +
self-managed). Unlike Gitea (which mimics GitHub Actions), GitLab has
a materially different CI model: pipeline-first rather than workflow-
file-first; `X-Gitlab-Token` static-token webhook instead of HMAC;
masked + protected CI/CD variables instead of opaque secrets. Driver
covers MRs (mapped to neutral `PullRequest`), typed pipeline inputs
(17.11+), pipeline→job→artifact resolution, CI variable provisioning
(consumed by 31-8), and static-token webhook verification (consumed
by 31-7). Research §3 is the basis; no re-research needed.

## 2. Dependencies

Hard blockers:

- **Story 31-1** — abstraction + models (including
  `WorkflowRun.RawMetadata JsonDocument?` for GitLab-specific fields).
- **Story 31-2** — resolver + installation table.
- **Story 29-2** — secret store backing credential loads.

Soft:

- **Story 31-4** — Gitea driver is a reference for the typed HTTP-
  client pattern; GitLab reuses the shape but not the code.

Blocks: **31-7** (static-token verifier consumed), **31-8** (CI
variable provisioner impl), **31-9** (onboarding picker), **31-10**
(integration harness — nightly GitLab container run).

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.GitLab/Tamma.Platforms.GitLab.csproj` | New driver project. |
| `.../GitLabPlatformDriver.cs` | `IGitPlatformDriver` impl; `Kind = PlatformKind.GitLab`. |
| `.../GitLabPlatformClient.cs` | `IGitPlatformClient` impl. |
| `.../GitLabActionsPlatformClient.cs` | `IGitPlatformActionsClient` impl. |
| `.../GitLabHttpClient.cs` | Typed HTTP client: auth header (`PRIVATE-TOKEN` for PAT/project-token/group-token or `Authorization: Bearer` for OAuth2), pagination via `Link` header, error mapping, rate-limit awareness. |
| `.../GitLabAuth.cs` | Union: `ProjectAccessToken`, `GroupAccessToken`, `PersonalAccessToken`, `OAuth2` (last one stretch). |
| `.../GitLabPipelineResolver.cs` | Resolves `pipeline → jobs → artifact-bearing job → download URL`. |
| `.../GitLabVersionProbe.cs` | `GET /version` at startup; caches `GitLabVersion` for feature detection (17.11+ typed inputs). |
| `.../GitLabWebhookTokenVerifier.cs` | Static-token compare against `X-Gitlab-Token`; constant-time. |
| `.../GitLabErrorMapper.cs` | HTTP status → `PlatformError`. |
| `.../GitLabPlatformDriverFactory.cs` | Factory for 31-2 keyed DI. |
| `.../GitLabDriverRegistrationExtensions.cs` | `services.AddGitLabPlatformDriver()` extension. |
| `.../Dtos/*.cs` | Wire DTOs: Project, Branch, MergeRequest, MrChange, Issue, Note, Pipeline, Job, Artifact, Variable, Hook. |
| `.../Mapping/MrToPullRequestMapper.cs` | Maps GitLab MR shape to the neutral `PullRequest` record. |
| `.../README.md` | Driver docs: CI model differences, masked-variable rules, auth modes. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Platforms.GitLab.Tests/Tamma.Platforms.GitLab.Tests.csproj` | Test project. |
| `.../GitLabPlatformClientTests.cs` | WireMock-based endpoint tests. |
| `.../GitLabActionsPlatformClientTests.cs` | Pipeline dispatch (with + without typed inputs). |
| `.../MrToPullRequestMapperTests.cs` | MR fields → PR fields. |
| `.../GitLabPipelineResolverTests.cs` | Job-with-artifacts selection. |
| `.../GitLabWebhookTokenVerifierTests.cs` | Constant-time compare + fail-closed on missing secret. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/Tamma.sln` | Add Platforms.GitLab + test project. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Program.cs` | `services.AddGitLabPlatformDriver();` |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.Abstractions/PlatformKindCapabilityMatrix.cs` | GitLab row: `{ Actions, Artifacts, Secrets, ProtectedVariables, MaskedVariables, WebhookStaticToken, PrFileReview, ListAccessibleRepos }` — no `LibsodiumSecrets`, no `WebhookHmac`. |

## 5. Sequence of changes

### Step 1 — Driver project scaffolding (1h)

- csproj + solution wiring.
- `GitLabPlatformDriver` shell with capability set from matrix.
- **Commit**: `feat(platforms.gitlab): project scaffolding`.

### Step 2 — Typed HTTP client + auth + link pagination (6h)

- `GitLabHttpClient` constructor: `HttpClient`, `GitLabAuth`, base URL.
- Auth header:
  - `ProjectAccessToken` / `GroupAccessToken` / `PersonalAccessToken`
    → `PRIVATE-TOKEN: <value>`.
  - `OAuth2(access)` → `Authorization: Bearer <value>`.
- `SendAsync(HttpRequestMessage)`:
  1. Attach auth header.
  2. On response, parse `RateLimit-Remaining` / `RateLimit-Reset` (if
     present) + `Retry-After` on 429.
  3. On paginated GET, read `Link: <url>; rel="next"` header; expose
     `AsyncEnumerable` helper `EnumeratePagesAsync<T>(startUrl)` that
     yields DTOs page by page.
- OAuth2 refresh path: stretch; first cut skips.
- Unit tests: auth header correctness; link-header pagination correct;
  429 with Retry-After surfaces.
- **Commit**: `feat(platforms.gitlab): typed HTTP client + pagination`.

### Step 3 — Error mapper (1h)

- HTTP status → `PlatformError` per brief AC9:
  - 401 → `AuthExpired`
  - 403 → `PermissionDenied`
  - 404 → `NotFound`
  - 429 → `RateLimited(retryAfter)`
  - 400 / 422 → `InvalidRequest(code, message)` — parse
    `{ "error": "…", "error_description": "…" }` or
    `{ "message": {...} }` JSON shape.
  - 5xx → `ServiceUnavailable`
- **Commit**: `feat(platforms.gitlab): error mapper`.

### Step 4 — Version probe + feature gate (1h)

- `GitLabVersionProbe.ProbeAsync(httpClient)` hits `GET /api/v4/version`
  once per-driver-instance, caches `{ Version: "17.11.3-ee", Major, Minor }`.
- `IsTypedInputsSupported()` returns `Major > 17 || (Major == 17 && Minor >= 11)`.
- **Commit**: `feat(platforms.gitlab): version probe`.

### Step 5 — `GitLabPlatformClient` — repo + branch + file (4h)

- Project IDs are URL-encoded `group%2Fproject` strings. Helper
  `UrlEncodeProjectId(projectRef)` used by every call.
- `GetRepoAsync`: `GET /api/v4/projects/{pid}`.
- `ListRepoBranchesAsync`: `GET /api/v4/projects/{pid}/repository/branches`
  auto-paginated.
- `GetFileContentAsync`: `GET /api/v4/projects/{pid}/repository/files/{path:url}?ref=` — content is base64; decode.
- `CreateBranchAsync`: `POST /api/v4/projects/{pid}/repository/branches`
  with `{ branch, ref }`.
- WireMock tests.
- **Commit**: `feat(platforms.gitlab): repo + branch + file`.

### Step 6 — `GitLabPlatformClient` — MR/PR mapping (5h)

- `OpenPullRequestAsync` (→ `POST /merge_requests`): maps
  `PullRequest` → GitLab MR payload `{ source_branch, target_branch,
  title, description }`.
- `GetPullRequestAsync`: `GET /merge_requests/{iid}` →
  `MrToPullRequestMapper.Map(dto)`.
- `ListPullRequestFilesAsync`: `GET /merge_requests/{iid}/changes` →
  each change → `PrFile`.
- `CreatePullRequestReviewCommentAsync`: `POST /merge_requests/{iid}/discussions`
  with `{ body, position: { base_sha, start_sha, head_sha, position_type:
  "text", new_path, new_line } }`. Position construction from the
  neutral `Comment` record.
- `MergePullRequestAsync`: `PUT /merge_requests/{iid}/merge`.
- `CreateIssueCommentAsync`: `POST /issues/{iid}/notes`.
- `RegisterWebhookAsync`: `POST /hooks` with
  `{ url, token, push_events, merge_requests_events, issues_events,
  pipeline_events, enable_ssl_verification }` — event list is a set of
  boolean flags, not an array like GitHub.
- Mapper unit tests cover every field including:
  - `state: "opened" | "closed" | "merged" | "locked"` → `PullRequestState`.
  - `work_in_progress: true` → `IsDraft: true`.
- **Commit**: `feat(platforms.gitlab): MR + PR mapping + webhook registration`.

### Step 7 — `GitLabActionsPlatformClient` — dispatch + monitor (5h)

- `DispatchWorkflowAsync`:
  - If `IsTypedInputsSupported()` → `POST /api/v4/projects/{pid}/pipeline`
    with `{ ref, variables: [ { key, value }, …], inputs: { … } }`.
  - Else → `{ ref, variables: […] }` only; inputs merged into
    variables with a warning.
- `GetRunStatusAsync`: `GET /pipelines/{id}`. Status lifecycle:
  `created|pending|running|success|failed|canceled|skipped`. Map to
  `WorkflowRun.Status`.
- `ListRunJobsAsync`: `GET /pipelines/{id}/jobs`.
- `CancelRunAsync`: `POST /pipelines/{id}/cancel`.
- WireMock tests for dispatch with + without typed inputs; status
  mapping for each lifecycle state.
- **Commit**: `feat(platforms.gitlab): pipeline dispatch + monitor`.

### Step 8 — Pipeline artifact resolution (3h)

- `GitLabPipelineResolver.ResolveArtifactAsync(pipelineId, artifactName?)`:
  1. `ListRunJobsAsync(pipelineId)`.
  2. Filter to jobs with `artifacts_file != null`.
  3. If `artifactName` provided, filter by name match.
  4. Return the first matching job id.
- `DownloadArtifactAsync(pipelineId, artifactName?)`:
  1. Resolve job id.
  2. `GET /api/v4/projects/{pid}/jobs/{jobId}/artifacts` — returns a
     zip stream.
  3. Wrap in `LimitedStream` (4MB cap default).
- Tests: multi-artifact pipeline → resolver picks named match; empty
  pipeline → `NotFound`.
- **Commit**: `feat(platforms.gitlab): artifact resolution`.

### Step 9 — CI variable provisioner (4h)

- `SetProjectVariableAsync(projectRef, key, value, { protected, masked,
  environmentScope, variableType })`:
  1. POST `/api/v4/projects/{pid}/variables` with payload.
  2. On 400 "masked-value constraints violated" → return
     `PlatformError.InvalidRequest("masked_value_invalid", rule)`.
- Masked-value validation (per GitLab 17 rules):
  - Minimum 8 characters.
  - Must not contain newlines.
  - Must contain only base64-compatible characters + some special
    chars: `A-Za-z0-9+/=@:.~_-`.
  - Driver enforces client-side before POST to fail fast.
- `DeleteProjectVariableAsync`.
- Consumed by 31-8's `ICiSecretsProvisioner` impl.
- Tests: each masked-value rule rejection; protected flag round-trip;
  environment-scope round-trip.
- **Commit**: `feat(platforms.gitlab): CI variable provisioner`.

### Step 10 — Webhook static-token verifier (1h)

- `GitLabWebhookTokenVerifier`:
  - `VerifyAsync(bodyBytes, secret, getHeader)`:
    1. Read `X-Gitlab-Token` via `getHeader`.
    2. Constant-time compare via `CryptographicOperations.FixedTimeEquals`.
    3. Missing secret config → `ServiceUnavailable` (fail-closed).
- Tests: valid, wrong, missing, no-secret.
- **Commit**: `feat(platforms.gitlab): webhook static-token verifier`.

### Step 11 — Factory + DI extension (2h)

- `GitLabPlatformDriverFactory.BuildAsync(installation, secrets, ct)`:
  1. Load credential → `GitLabAuth`.
  2. Build `GitLabHttpClient`.
  3. Run `GitLabVersionProbe` (cached).
  4. Wrap in `GitLabPlatformClient` + `GitLabActionsPlatformClient`.
  5. Return `GitLabPlatformDriver`.
- DI extension `AddGitLabPlatformDriver()` registers factory keyed
  on `PlatformKind.GitLab`.
- **Commit**: `feat(platforms.gitlab): factory + DI extension`.

### Step 12 — Contract test + coverage (3h)

- `GitLabContractTests : GitPlatformClientContractTests<GitLabFixture>`.
- Skip reasons for:
  - `WebhookHmac` capability — GitLab uses static token; test marked
    `Skip("static-token not HMAC")`.
- Coverage ≥85%.
- **Commit**: `test(platforms.gitlab): contract + coverage`.

### Step 13 — Docs (1h)

- README: CI-model differences, masked-variable rules, auth modes
  table, "self-managed vs gitlab.com" setup notes.
- **Commit**: `docs(platforms.gitlab): driver README`.

## 6. Test strategy

### Unit (WireMock)

- `GitLabHttpClient`: link-pagination traversal.
- Every `IGitPlatformClient` method: happy path + error mapping.
- Every `IGitPlatformActionsClient` method including pipeline
  dispatch with and without typed inputs.
- MR → PR mapper: every MR state + draft/WIP flag.
- Pipeline resolver: multi-artifact picking, named match.
- Masked-value validator: every rule tested individually.
- Webhook verifier: valid, wrong, missing, no-secret.
- Error mapper: every status code in AC9.

### Integration (31-10 harness — nightly only)

- `gitlab/gitlab-ce:latest` container → contract tests. Heavy image
  (~3GB, ~8min boot); scheduled nightly to keep per-PR runs
  manageable.
- Live-API fallback: tag `run-gitlab-integration` on a PR to opt in.

### Contract

- Full `GitPlatformClientContractTests<GitLabFixture>` passes; skipped
  cases documented.

## 7. Rollback plan

- **Revert commits**: removes project + DI + matrix row adjustments.
  Any tenant that connected a GitLab install loses connectivity.
  Orphan rows remain harmless.
- **Non-reversible**: none.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Project scaffolding | 1 |
| 2. HTTP client + link pagination | 6 |
| 3. Error mapper | 1 |
| 4. Version probe | 1 |
| 5. Repo/branch/file | 4 |
| 6. MR/PR mapping + webhooks | 5 |
| 7. Pipeline dispatch + monitor | 5 |
| 8. Artifact resolution | 3 |
| 9. CI variable provisioner | 4 |
| 10. Static-token verifier | 1 |
| 11. Factory + DI | 2 |
| 12. Contract test + coverage | 3 |
| 13. Docs | 1 |
| **Total** | **37** (brief: 36 — within error bars). |

## 9. Open questions

- **`workflow_dispatch` equivalent**: research §3 confirms GitLab
  CI has no literal equivalent to GitHub's `workflow_dispatch`. Plan
  uses the manual pipeline trigger API (`POST /api/v4/projects/{pid}/pipeline`)
  with `inputs` on 17.11+, variables otherwise. Document. **This
  reshapes the driver's dispatch call relative to 31-4** (Gitea has
  a `/dispatches` endpoint; GitLab does not).
- **OAuth2 refresh**: brief defers to stretch. Plan: first cut ships
  PAT + project token + group token; OAuth2 flagged for follow-up
  story (31-6-b) once a self-managed tenant asks.
- **GitLab 17.11 inputs schema**: 17.11 added `ci_inputs_for_pipelines`
  flag; default enabled. Plan: driver assumes enabled on 17.11+ and
  falls back to variables on <17.11. If the target instance has the
  flag explicitly disabled, typed inputs still fail — driver detects
  via the 400 response and retries as variables (log-warn).
- **Masked-value rules changes**: GitLab has historically added new
  allowed characters (16.x added `@`, `:`, `.`). Plan: driver's
  validation uses the most-current rule set (2026-04: base64 + `@:.~_-`).
  Document so operator knows to update the validator if GitLab
  loosens rules.
- **Link-header pagination buffer size**: deep repos have 1000s of
  branches. Plan: `per_page=100` default; max 100 per GitLab docs.
  Async enumerable caps at 10K yielded items by default as a DoS
  guard.
- **Project ID URL encoding**: `group/subgroup/project` has two
  slashes. Must `%2F`-encode both. Helper `UrlEncodeProjectId`
  handles; test with nested-group fixture.
- **Self-managed base URL normalization**: `https://gitlab.example.com/`
  vs `https://gitlab.example.com` vs `https://gitlab.example.com/api/v4/`.
  Plan: driver accepts any of the three; strips trailing slash;
  appends `/api/v4` only if the URL doesn't already include it.
  Document.
- **Artifact download max size**: GitLab jobs can produce multi-
  gigabyte artifacts. 4MB default `LimitedStream` cap will reject
  large ones with `RateLimited`/`InvalidRequest` — wrong error.
  Plan: specific `PlatformError.ArtifactTooLarge(bytes)` — add to
  31-1's error union as follow-up (extend the union without
  breaking). For this story, document the cap; callers that need
  larger artifacts must override via `Agent:MaxArtifactBytes`.
