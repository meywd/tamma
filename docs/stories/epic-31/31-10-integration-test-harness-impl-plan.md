# Story 31-10 Implementation Plan — Integration Test Harness

**Status**: Planned (2026-04-21)
**Story brief**: [`31-10-integration-test-harness.md`](./31-10-integration-test-harness.md)
**Epic 31 phase**: Layer 4 — parallel throughout.
**Branch**: `feat/story-31-10-integration-test-harness`

---

## 1. Objective

Ship a reproducible integration test harness that boots Gitea,
Forgejo, and GitLab containers, seeds a bot user + PAT + fixture
repo + webhook secret per container, and runs the shared
`ContractTestSuite<TDriver>` against each driver's live backend.
Per-PR gating on touched-driver changes; nightly schedule for
GitLab (image is heavy). Complements existing WireMock unit tests
— catches real-API drift, auth-token lifecycle quirks, webhook
delivery retries.

## 2. Dependencies

Hard blockers:

- **Story 31-3** — GitHub driver (mocked in harness; live-API tests
  live in a separate org).
- **Story 31-4** — Gitea driver.
- **Story 31-5** — Forgejo driver + fixture (the fixture class lives
  in this project; 31-5 authored it).
- **Story 31-6** — GitLab driver.

Soft:

- **Docker + testcontainers-dotnet** already in use for existing
  integration tests.

Blocks: nothing directly; nightly runs produce early warnings for
driver regressions.

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Platforms.IntegrationTests/Tamma.Platforms.IntegrationTests.csproj` | Test project. |
| `.../Fixtures/PlatformContainerFixture.cs` | Generic base class for container fixtures. |
| `.../Fixtures/GiteaContainerFixture.cs` | Boots `gitea/gitea:1.25` + seeds admin + bot + repo. |
| `.../Fixtures/ForgejoContainerFixture.cs` | Boots `codeberg.org/forgejo/forgejo:15-rootless`. (Initial file created in 31-5; 31-10 wires it in.) |
| `.../Fixtures/GitLabContainerFixture.cs` | Boots `gitlab/gitlab-ce:latest`; heavy. |
| `.../Fixtures/ActRunnerFixture.cs` | Starts a single `gitea/act_runner` sidecar for Gitea / Forgejo. |
| `.../Fixtures/WebhookCallbackListener.cs` | Binds a random local port + captures incoming webhook deliveries for signature-verification tests. |
| `.../ContractTestSuite.cs` | xUnit theory exercising every driver against its fixture. |
| `.../GiteaContractTests.cs` | Wires fixture into the suite. |
| `.../ForgejoContractTests.cs` | (subclass created in 31-5; this story makes it discoverable by CI). |
| `.../GitLabContractTests.cs` | Wires fixture. Marked `[Trait("category","nightly")]`. |
| `.../README.md` | How to run locally, add a platform, debug failed boots. |
| `/home/meywd/tamma/.github/workflows/integration-tests-platforms.yml` | CI workflow: per-PR + nightly gating. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/Tamma.sln` | Add integration-tests project. |
| `/home/meywd/tamma/.github/workflows/integration-tests.yml` (if exists) or equivalent | Extend to cover the new platforms integration project with opt-in trait-based selection. |

## 5. Sequence of changes

### Step 1 — Base fixture + Gitea container (4h)

- `PlatformContainerFixture<TConfig>`: abstract base with
  `InitializeAsync`, `DisposeAsync`. Provides helpers for container
  healthcheck polling, admin-creation shell exec, bot-account REST
  creation, fixture-repo REST creation, webhook-secret generation.
- `GiteaContainerFixture`:
  - Uses `Testcontainers.Gitea` pattern (docker image `gitea/gitea:1.25`,
    port 3000).
  - Healthcheck polls `GET /api/v1/version` for 200; timeout 3 min.
  - Post-boot: exec `forgejo admin user create …` (wait, wrong —
    this is Gitea; use `gitea admin user create`). Then REST:
    - Create PAT for admin.
    - Create bot user via `POST /admin/users`.
    - Create PAT for bot user.
    - Create fixture repo `bot/test-repo` via `POST /user/repos`.
    - Commit a sample workflow file at `.gitea/workflows/echo.yaml`.
  - Exposes `BaseUrl`, `BotToken`, `WebhookSecret`.
- Unit-level smoke test: fixture boots → health check passes →
  REST seed completes within 3 min.
- **Commit**: `test(platforms.integration): Gitea container fixture`.

### Step 2 — Forgejo fixture wire-up (1h)

- `ForgejoContainerFixture` was authored in 31-5; this step places
  it in this project, adds to the solution, and registers discovery
  metadata.
- **Commit**: `test(platforms.integration): wire Forgejo fixture`.

### Step 3 — GitLab fixture (6h)

- `GitLabContainerFixture`:
  - Image `gitlab/gitlab-ce:latest` — heavy (~3GB).
  - Healthcheck polls `GET /-/readiness` for 200; timeout 10 min.
  - Post-boot: exec `gitlab-rails runner` to create admin root
    password; then REST:
    - Create root PAT.
    - Create bot user + project-access-token on a fixture project.
    - Create fixture project with sample `.gitlab-ci.yml`.
- Memory cap inside CI: increase runner memory allocation or use
  `runs-on: ubuntu-latest-xl` if needed.
- **Commit**: `test(platforms.integration): GitLab container fixture`.

### Step 4 — `ActRunnerFixture` for Gitea/Forgejo Actions tests (2h)

- Boots `gitea/act_runner:latest` container sharing a docker
  network with the target Gitea/Forgejo container.
- Runner auto-registers using a shared secret passed via env var.
- Used by contract tests that exercise dispatch → run-complete loop.
- Skip runner-dependent tests if `ActRunnerFixture` is disabled via
  `Platforms:SkipRunnerTests=true`.
- **Commit**: `test(platforms.integration): act_runner sidecar`.

### Step 5 — Webhook callback listener (2h)

- `WebhookCallbackListener`:
  - Binds an ephemeral port via `TcpListener`.
  - Runs a lightweight `HttpListener` handler that captures a single
    incoming POST + exposes the body + signature header via
    `Task<WebhookDelivery>`.
  - Driver fixture registers the listener URL as the webhook
    destination on the target platform.
- Used in signature-verification round-trip tests.
- **Commit**: `test(platforms.integration): webhook callback listener`.

### Step 6 — `ContractTestSuite<TDriver>` (5h)

- xUnit theory with methods:
  - `Repo_GetRead`
  - `Branch_List`
  - `Branch_Create`
  - `File_ReadWithRef`
  - `PullRequest_OpenAndGet`
  - `PullRequest_Merge`
  - `IssueComment_Create`
  - `Webhook_RegisterAndReceive` — uses `WebhookCallbackListener`.
  - `ActionsDispatch_And_RunComplete` — skipped if no runner.
  - `Artifact_Download`.
  - `Secrets_PushPlaintextOrLibsodium` — branches per driver.
- Each subclass wires its fixture + expected capability set.
- Capability gates: if driver lacks `WebhookHmac`, `Webhook_
  RegisterAndReceive` skips with a documented reason instead of
  fails.
- **Commit**: `test(platforms.integration): ContractTestSuite`.

### Step 7 — Per-driver contract-test classes (2h)

- `GiteaContractTests : ContractTestSuite<GiteaContainerFixture>`.
- `ForgejoContractTests : ContractTestSuite<ForgejoContainerFixture>`.
- `GitLabContractTests : ContractTestSuite<GitLabContainerFixture>`
  — tagged `[Trait("category","nightly")]`.
- **Commit**: `test(platforms.integration): per-driver contract classes`.

### Step 8 — CI workflow (3h)

- `.github/workflows/integration-tests-platforms.yml`:
  - Trigger: `pull_request` with path filter
    `apps/tamma-elsa/src/Tamma.Platforms.{Gitea,GitLab}/**` +
    `workflow_dispatch` + `schedule: "0 3 * * *"` for nightly.
  - Job `gitea-forgejo`:
    - Runs `dotnet test --filter "TestCategory!=nightly&FullyQualifiedName~Gitea|FullyQualifiedName~Forgejo"`.
    - Expected wall-clock ≤15 min.
  - Job `gitlab-nightly`:
    - `if: github.event_name == 'schedule' || contains(github.event.pull_request.labels.*.name, 'run-gitlab-integration')`.
    - Runs `dotnet test --filter "FullyQualifiedName~GitLab"`.
    - Expected wall-clock ~15 min.
  - Docker-in-docker: uses `docker/setup-buildx-action` per
    testcontainers-dotnet docs.
  - Artifact upload on failure: container logs + fixture state
    snapshot.
- **Commit**: `ci: platform integration workflow`.

### Step 9 — Timeout handling + teardown + retry (1h)

- Every test has 5-min timeout via `[Fact(Timeout=300_000)]`.
- `DisposeAsync` unconditionally kills containers (preventing
  zombie container).
- Flaky retries via `[Retry(2)]` attribute on platform-side bugs
  only (documented in each affected test).
- **Commit**: `test(platforms.integration): timeouts + teardown`.

### Step 10 — Docs (1h)

- `README.md` in the test project:
  - How to run locally: `dotnet test --filter "Category!=nightly"`.
  - How to add a new platform container.
  - Common failure modes + debugging tips (Docker memory, port
    conflicts, image-pull rate limits).
- **Commit**: `docs(platforms.integration): harness README`.

## 6. Test strategy

### Self-test

- Fixture smoke tests (container boot + REST seed).
- `WebhookCallbackListener` receives a GET within 5 seconds.

### Contract (primary)

- Each driver's `ContractTestSuite` subclass runs all methods.
- Capability-gated skips documented in xunit `Skip` reasons.

### Performance

- Per-platform, per-test timing logged. Regression: a Gitea
  dispatch → completion time >60s triggers a slack alert in
  nightly pipeline (ops follow-up; not gating).

### Stability

- CI nightly for 2 weeks must show <5% flake rate before GA.
  Flakes quarantined via `Skip("Flaky: <issue>")`.

## 7. Rollback plan

- **Revert commits**: removes test project + workflow. No production
  code touched. No rollback risk.
- **Workflow failure**: a flaky harness must not block merges.
  Per-PR runs gated on touched-driver paths only; broader merges
  ship regardless.
- **Non-reversible**: none.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Base + Gitea fixture | 4 |
| 2. Forgejo fixture wire-up | 1 |
| 3. GitLab fixture | 6 |
| 4. Act runner sidecar | 2 |
| 5. Webhook callback listener | 2 |
| 6. ContractTestSuite | 5 |
| 7. Per-driver subclasses | 2 |
| 8. CI workflow | 3 |
| 9. Timeouts + teardown | 1 |
| 10. Docs | 1 |
| **Total** | **27** (brief: 22 — variance: webhook callback listener + act runner sidecar underestimated in brief). |

## 9. Open questions

- **GitLab CE image size + CI quotas**: `gitlab-ce` is ~3GB pulled
  + ~4GB running. GitHub Actions `ubuntu-latest` free runner has
  7GB RAM — tight. Plan: use `ubuntu-latest-xl` (16GB) for GitLab
  jobs; falls back to `ubuntu-latest` with smaller `gitlab-ce`
  `initial-root-password` + minimal services via `GITLAB_OMNIBUS_CONFIG`.
  Document config.
- **Docker Hub rate-limit on free runners**: pulling
  `gitlab/gitlab-ce:latest` on every nightly hits unauth pull
  limit fast. Plan: mirror the image to GHCR via a scheduled
  weekly pull workflow. Document.
- **Forgejo Codeberg image availability**: `codeberg.org/forgejo/
  forgejo:15-rootless` may have intermittent availability. Plan:
  add `forgejoclone/forgejo` Docker Hub mirror as fallback inside
  the fixture. Document.
- **Act runner auto-registration**: requires a shared secret
  generated inside Gitea and passed to the runner container.
  Plan: fixture creates the runner token via
  `POST /api/v1/repos/{owner}/{repo}/runners/registration-token`
  then passes via env to the `act_runner` container.
- **Dispatch → run-complete wall-clock cost**: sample workflow must
  be <5s. Plan: workflow is a single `run: echo hello` step.
  Verified locally to run in ~3s on a cold `act_runner`.
- **Artifact size in fixtures**: fixture workflows upload a 1-byte
  text file. Sidesteps LimitedStream cap + keeps runs fast.
- **Static-token webhook fixture for GitLab**: no HMAC; fixture
  sets a static webhook secret; test verifies `X-Gitlab-Token`
  header is correct. Maps cleanly to contract suite.
- **Cleanup on aborted runs**: testcontainers-dotnet's `Ryuk`
  container handles orphan container cleanup. Plan: `Ryuk` enabled
  (default). Document.
- **Retry quarantine policy**: how long does a quarantined test
  stay before re-enabling? Plan: 30 days or until root cause
  identified, whichever first. Ownership: QA lead.
