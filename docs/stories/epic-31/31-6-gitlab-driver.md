# Story 31-6: GitLab driver (MRs / Pipelines / variables / webhooks)

Status: todo (planning brief, 2026-04-21)

## Story

As a **tenant whose repos live on GitLab (Cloud / self-managed
17.x+)**,
I want Tamma to read my projects, open merge requests, trigger CI
pipelines with typed inputs, monitor pipeline + job state, download
job artifacts, push masked + protected CI/CD variables, and verify
inbound webhooks,
so that GitLab-centric organisations can adopt Tamma without
migrating to GitHub.

## Narrative

GitLab is the heaviest non-GitHub driver. Its CI model differs
materially from GitHub Actions and from Gitea/Forgejo (which mimic
Actions). Research details in
[`research/multi-git-platform-2026.md` §3](../research/multi-git-platform-2026.md):

- **Dispatch**: `POST /api/v4/projects/:id/pipeline` with PAT, or
  `POST /api/v4/projects/:id/trigger/pipeline` with a pre-registered
  trigger token. GitLab 17.11 adds `ci_inputs_for_pipelines` for
  typed input arguments.
- **Monitoring**: pipelines → jobs → artifacts. Artifact lives on
  the job, not the pipeline. Driver resolves pipeline → jobs →
  artifact-bearing job → download.
- **Secrets**: CI/CD variables with `protected` + `masked` + env
  scope — richer than GitHub's secrets model.
- **Webhooks**: static token in `X-Gitlab-Token` header (not HMAC).
  Constant-time compare against the configured secret.
- **Terminology**: "merge request" (MR) instead of PR. Driver maps
  GitLab MR shape to the neutral `PullRequest` record from 31-1.

## Acceptance Criteria

1. New driver project `apps/tamma-elsa/src/Tamma.Platforms.GitLab/`
   with:
   - `GitLabPlatformDriver : IGitPlatformDriver` — `Kind =
     PlatformKind.GitLab`.
   - `GitLabPlatformClient : IGitPlatformClient`.
   - `GitLabActionsPlatformClient : IGitPlatformActionsClient`.
   - `Capabilities` returns `{ Actions, Artifacts, Secrets,
     WebhookStaticToken, ProtectedVariables, MaskedVariables }`.
     **No `LibsodiumSecrets`, no `WebhookHmac`.**
2. Auth — three modes:
   - **Project access token** (project-scoped, bot-like) — preferred
     for self-managed deployments.
   - **Group access token** — preferred when a tenant maps a whole
     GitLab group to Tamma.
   - **OAuth2 app + refresh token** — user-delegated; currently
     deferred to stretch if time allows; PAT paths cover the primary
     use case.
3. Endpoint coverage:
   - `GetRepoAsync` → `GET /api/v4/projects/:id` (id URL-encodes
     `group/project`)
   - `ListRepoBranchesAsync` → `GET /api/v4/projects/:id/repository/branches`
   - `GetFileContentAsync` → `GET /api/v4/projects/:id/repository/files/:path?ref=`
   - `CreateBranchAsync` → `POST /api/v4/projects/:id/repository/branches`
   - `OpenPullRequestAsync` (→ MR) → `POST /api/v4/projects/:id/merge_requests`
   - `GetPullRequestAsync` → `GET /api/v4/projects/:id/merge_requests/:iid`
   - `ListPullRequestFilesAsync` → `GET /api/v4/projects/:id/merge_requests/:iid/changes`
   - `CreatePullRequestReviewCommentAsync` → `POST /api/v4/projects/:id/merge_requests/:iid/discussions`
     (with `position` payload for line comments)
   - `MergePullRequestAsync` → `PUT /api/v4/projects/:id/merge_requests/:iid/merge`
   - `CreateIssueCommentAsync` → `POST /api/v4/projects/:id/issues/:iid/notes`
   - `RegisterWebhookAsync` → `POST /api/v4/projects/:id/hooks` with
     secret-token + selected event bool flags
   - `DispatchWorkflowAsync` → `POST /api/v4/projects/:id/pipeline`
     with `{ ref, variables, inputs (17.11+) }`
   - `GetRunStatusAsync` → `GET /api/v4/projects/:id/pipelines/:pipeline_id`
   - `ListRunJobsAsync` → `GET /api/v4/projects/:id/pipelines/:pipeline_id/jobs`
   - `DownloadArtifactAsync` → `GET /api/v4/projects/:id/jobs/:job_id/artifacts/:artifact_path`
     or `/api/v4/projects/:id/jobs/:job_id/artifacts` (zip)
   - `CancelRunAsync` → `POST /api/v4/projects/:id/pipelines/:pipeline_id/cancel`
4. `WorkflowRun` and `WorkflowJob` records from 31-1 gain GitLab-
   specific fields via a `RawMetadata JsonDocument?` slot — driver-
   specific data (e.g. `coverage`, `runner` fields) stays available
   without polluting the neutral shape.
5. `WorkflowDispatchRequest.Inputs` supports typed inputs when
   `ci_inputs_for_pipelines` is available on the target. Driver
   feature-detects via `GET /version` at startup; caches
   `GitLabVersion` for per-driver instance lifetime.
6. CI variable provisioner (consumed by 31-8):
   - `SetProjectVariableAsync(project, key, value, { protected, masked, environment, variable_type })`
   - `DeleteProjectVariableAsync(project, key)`
   - Masked-value validation per GitLab rules (min length 8,
     no newlines, no unsupported chars). On validation fail, emit
     `PlatformError.InvalidRequest` with the rule that failed.
7. Webhook verifier — `GitLabWebhookTokenVerifier` reads the
   `X-Gitlab-Token` header, constant-time compares against the
   stored secret. Implements 31-7's contract with a different
   verification shape.
8. Pagination — GitLab paginates with `page` + `per_page` + `Link`
   header. Auto-paginate via `IAsyncEnumerable<T>` using the `Link:
   <url>; rel="next"` header.
9. Error mapping — HTTP status → `PlatformError`:
   - 401 → `AuthExpired`
   - 403 → `PermissionDenied`
   - 404 → `NotFound`
   - 429 → `RateLimited` (GitLab returns `Retry-After`; surface it)
   - 5xx → `ServiceUnavailable`
   - 400 / 422 → `InvalidRequest`
10. DI extension `services.AddGitLabPlatformDriver()`.
11. Unit tests with WireMock-style fakes cover the happy path + each
    error branch, with focus on:
    - MR → PullRequest mapping correctness.
    - Pipeline-with-typed-inputs path (17.11+).
    - Pipeline-without-typed-inputs fallback (pre-17.11 — uses
      `variables` only).
    - Artifact resolution (pipeline → job → artifact download).
    - Masked variable validation + error mapping.
12. Contract tests shared with 31-3 + 31-4: same fixture runs
    against the GitLab driver. Any GitLab-specific gap (e.g. no
    HMAC webhook) is encoded in the capability matrix so the
    contract test skips correctly.

## Technical Context

### Token + project-access-token lifecycle

GitLab project/group access tokens expire at most 1 year out.
Driver stores the expiry in the secret-store row's metadata and
emits a `PLATFORM.INSTALLATION.CREDENTIAL_EXPIRING.WARN` event when
<30 days remain. A later story rotates automatically via Epic 29's
rotation primitive — out of scope here.

### Why no Octokit equivalent

GitLab's `GitLabApiClient` (`NuGet.CommandLine` namespace) exists
but is sparsely maintained. Same rationale as 31-4: typed HTTP
client with `HttpClient` + System.Text.Json keeps the surface under
control.

### Self-managed vs gitlab.com

Driver takes `base_url` from the installation record (31-2's
`tenant_platform_installations.base_url` column). Defaults to
`https://gitlab.com` if null. Self-managed URLs work out of the
box.

## Dependencies

- **31-1** — abstraction
- **31-2** — resolver
- Blocks 31-7, 31-8, 31-9, 31-10

## Estimated hours

**36h**

| Task | Hours |
|---|---|
| Typed HTTP client + auth + pagination | 8 |
| `IGitPlatformClient` endpoint coverage (MR mapping is non-trivial) | 10 |
| `IGitPlatformActionsClient` + pipeline → job → artifact | 8 |
| CI variable provisioner + masked validation | 4 |
| Webhook static-token verifier | 1 |
| Error mapping + rate-limit handling | 2 |
| Unit + contract tests | 3 |

## Files touched

- `apps/tamma-elsa/src/Tamma.Platforms.GitLab/*.cs` (new project)
- `apps/tamma-elsa/src/Tamma.Api/Program.cs` (DI)
- `apps/tamma-elsa/tests/Tamma.Platforms.GitLab.Tests/*.cs` (new)

## References

- Research notes: [`../research/multi-git-platform-2026.md`](../research/multi-git-platform-2026.md) §3
- [GitLab Triggers](https://docs.gitlab.com/ci/triggers/)
- [GitLab Pipelines API](https://docs.gitlab.com/api/pipelines/)
- [GitLab Project-level Variables API](https://docs.gitlab.com/api/project_level_variables/)
- [GitLab Webhooks](https://docs.gitlab.com/user/project/integrations/webhooks/)
