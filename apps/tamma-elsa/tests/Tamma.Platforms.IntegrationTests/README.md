# Tamma.Platforms.IntegrationTests

Story 31-10 — integration test harness for Epic 31 git-platform drivers.
Boots real platform servers via Testcontainers and exercises every
`IGitPlatformClient` + `IGitPlatformActionsClient` method against the
live API.

## What's tested today

| Platform | Status | Image / version | Coverage |
|---|---|---|---|
| Gitea    | LIVE   | `gitea/gitea:1.21` | 12 client + 5 actions = 17 driver methods + 1 capability assertion |
| Forgejo  | stub   | TBD by Story 31-5 | filled in when 31-5 lands its driver |
| GitLab   | stub   | TBD by Story 31-6 | filled in when 31-6 lands its driver |
| GitHub   | stub   | n/a (no Docker image) | recorded-cassette pattern, follow-up story |

The Gitea image is pinned to **1.21** intentionally — that's the
minimum version Story 31-4's `GiteaPlatformDriver.MinimumActionsVersion`
requires for the Actions API. Bumping the pin to a newer image is a
deliberate decision (newer is not automatically better — we want the
harness to flag regressions in the driver's compat range against the
oldest version we support).

## Running locally

```bash
cd apps/tamma-elsa
dotnet test tests/Tamma.Platforms.IntegrationTests/Tamma.Platforms.IntegrationTests.csproj
```

Requires Docker on the host. Without Docker, every test is
**skipped** (not failed) — see `DockerAvailability.cs`. To force a
hard failure when Docker is missing (e.g. on CI where missing Docker
is a runner config bug, not a "skip silently" condition), set:

```bash
export PLATFORMS_REQUIRE_DOCKER=true
```

### Running just the Gitea suite

```bash
dotnet test tests/Tamma.Platforms.IntegrationTests/Tamma.Platforms.IntegrationTests.csproj \
    --filter "FullyQualifiedName~GiteaIntegration"
```

A successful run takes ~15s after the Gitea image is cached. First
run pulls the image (~150 MB) and adds ~30s.

### Running everything except nightly-tagged platforms

```bash
dotnet test tests/Tamma.Platforms.IntegrationTests/Tamma.Platforms.IntegrationTests.csproj \
    --filter "TestCategory!=Nightly"
```

GitLab is tagged `Nightly` because the `gitlab/gitlab-ce` image is
3 GB and slow to boot — running it on every PR is gratuitous. The CI
workflow runs Nightly-tagged tests only on the scheduled run + manual
dispatch.

## Adding a new platform

1. Add a `Fixtures/{Platform}ContainerFixture.cs` extending
   `PlatformIntegrationFixture`. Implement `StartAsync` to:
   - boot the container with `ContainerBuilder.WithImage(...)`,
   - poll a healthcheck endpoint until 200 OK,
   - seed an admin (typically via `docker exec` of a platform CLI),
   - mint a bot PAT,
   - create a fixture repo + record its default-branch tip SHA.

2. Add `{Platform}IntegrationTests.cs` (sibling of
   `GiteaIntegrationTests.cs`). Wire the fixture in `OneTimeSetUp`,
   construct the production driver via the platform's
   `IGitPlatformDriverFactory` registered in DI, and add one `[Test]`
   per `IGitPlatformClient` + `IGitPlatformActionsClient` method.

3. Tag the test class:
   ```csharp
   [Category("Integration")]
   [Category("Platforms")]
   [Category("{PlatformName}")]
   ```
   Add `[Category("Nightly")]` for heavy images that you don't want
   to run on every PR.

4. Update the CI workflow `.github/workflows/git-platform-integration-tests.yml`
   if the platform belongs in a different job (e.g. heavy images get a
   separate workflow_dispatch / schedule trigger).

## Common failure modes

| Symptom | Cause | Fix |
|---|---|---|
| `TimeoutException: healthcheck did not return 200 within 3 min` | Slow CI runner / image pull | Increase the timeout in `StartAsync` (subclass-level). |
| `gitea admin user create exit=2: syntax error: unexpected "("` | `/bin/sh -lc` loads the buggy `/etc/profile.d/gitea_bash_autocomplete.sh` | Fixture uses `/bin/su git -c "..."` (non-login shell) — avoid `-l`. |
| `MergePullRequestAsync` returns `Failed` | Gitea's merge endpoint occasionally rejects first call while computing merge base | The test retries up to 5x with 500 ms delay. |
| `429 Too Many Requests` pulling `gitea/gitea:1.21` from Docker Hub | Unauthenticated Docker Hub rate limit (CI shared IP) | Mirror to GHCR via a scheduled re-tag workflow. |
| Test passes locally but fails in CI | Likely orphan container from previous run holding port | Testcontainers' Ryuk container handles this — make sure Ryuk isn't disabled. |

## Skip-vs-fail cheat sheet

| Condition | Behavior |
|---|---|
| Docker unavailable, `PLATFORMS_REQUIRE_DOCKER` unset | Every test in fixture **skipped** (`Assert.Ignore`) |
| Docker unavailable, `PLATFORMS_REQUIRE_DOCKER=true`  | `OneTimeSetUp` **fails** (CI signal) |
| Container boots but seed step throws | `OneTimeSetUp` fails with container log tail in error message |
| Container boots, seed succeeds, individual driver call fails | Test fails with the typed `PlatformResult.Failed` payload |

The Docker probe runs once at type init (cached for the whole test
run lifetime) — no per-test daemon round-trips. See
`DockerAvailability.cs:1` for the implementation pattern, mirroring
the wave-A `chromadb.integration.test.ts` gating.

## Epic 31 P5 M3 — the Gitea full-stack E2E vehicle

`GiteaFullStackE2ETests` (+ `Fixtures/GiteaFullStackFixture`) is the
compose-style acceptance vehicle for the epic's headline: the cycle's
git surface completes on Gitea with ZERO GitHub configuration.

Topology (one logical deployment):

```
┌ containers ────────────────────────────┐   ┌ host processes ───────────┐
│ gitea/gitea:1.21   postgres:17 ×2      │   │ Tamma.Api (REAL binary,   │
│  └ webhooks → host.docker.internal ────┼──▶│  dotnet Tamma.Api.dll,    │
│    (--add-host host-gateway)           │◀──┼─ driver HTTP → Gitea)     │
└────────────────────────────────────────┘   └───────────────────────────┘
```

- Single-user activation via the `Platform:` config tier (kind=gitea +
  the fixture bot PAT). Nothing persisted; no onboarding call.
- Every `GitHub*`/`GitHub__*` env var is scrubbed from the API child
  process; the zero-GitHub test pins it (plus: no tenant platform
  installations, and the only `github_installations` row is the
  mediation guard's repo-GRANT row with AppId=0 — the guard registry
  is GitHub-named but platform-agnostic in role; naming cleanup is a
  recorded follow-up).
- `Tamma:PublicBaseUrl=http://host.docker.internal:{port}` +
  `Webhooks:Secrets:gitea` make the P4 startup registrar leave a live
  hook whose merged-PR delivery crosses the container gateway back
  into the 31-7 receiver (asserted via `platform_webhook_deliveries`).
- The suite is `Category=GiteaE2E` + `Nightly`: it rides the
  `gitea-e2e-nightly` job (schedule / workflow_dispatch / PR label
  `run-gitea-e2e`). Run locally with:

  ```bash
  dotnet build Tamma.sln          # the fixture launches src/Tamma.Api's own bin output
  dotnet test tests/Tamma.Platforms.IntegrationTests --filter "TestCategory=GiteaE2E"
  ```

## The engine-driven autonomous E2E (2026-08-13 — the recorded gap, closed)

`GiteaEngineDrivenE2ETests` (+ `Fixtures/EngineFullStackFixture`) is the
formerly-Ignored headline made real: the LLM stub the gap was recorded
against now exists (the opt-in **scripted LLM provider**,
`Llm:EnableScriptedProvider` — deterministic in-process responses keyed
on role/action/document-type, structurally un-enablable on any
production-shaped host), so the REAL `Tamma.ElsaServer` binary joins the
P5 topology as a second host process and the ACTUAL
AdlOrchestrator → SingleIssueCycle workflows drive one seeded issue:

```
┌ containers ────────────────────────────┐  ┌ host processes ──────────────┐
│ gitea/gitea:1.21   postgres:17 ×2      │  │ Tamma.Api  (real binary)     │
│  └ webhooks → host.docker.internal ────┼─▶│   ▲ llm/git/issue mediation  │
│                                        │◀─┼─  │  + scripted LLM provider │
│  (engine DB = 3rd database on the      │  │ Tamma.ElsaServer (real       │
│   app-DB container)                    │  │   binary, drives the cycle)  │
└────────────────────────────────────────┘  └──────────────────────────────┘
```

- **Engine config:** `Llm:DefaultProviderChain=[scripted]` (the config-tier
  provider selection), `Testing:UseMock=true` (CI-stub trigger),
  `Agent:Local:*` → a python3 scripted agent that makes ONE real commit
  per task via the Gitea API (empirically required: Gitea opens a
  zero-diff draft PR but refuses to merge a commit-less one).
- **The harness plays only the BY-DESIGN external actors**, each through
  its shipped seam: the document decider
  (`POST /api/documents/decisions/{sessionId}/resume`, sessions discovered
  from APPROVAL.REQUESTED audit rows), the CI system (the engine's DG-5
  `/elsa/api/ci/waits` seam, now forwarding the full result fields), and
  the human merge approver (`/elsa/api/adl/merge-approval/resume`).
- **What it proves end-to-end:** work selected off the seeded label →
  typed documents (plan/tasks/test-spec) produced against the REAL
  validators and accepted → branch + draft PR in Gitea → scripted agent
  commits → CI-stub leg green → un-draft → merge decision → REAL
  squash-merge → the merged-PR webhook crosses the container gateway and
  resumes `WaitForPRMerged` → scripted deployment pipeline →
  `CYCLE.COMPLETED`. Zero GitHub configuration, zero network LLM.
- Same nightly gating as the P5 suite (`GiteaE2E` + `Nightly`; the
  `gitea-e2e-nightly` job / `run-gitea-e2e` PR label). Locally:
  `dotnet build Tamma.sln` then
  `dotnet test tests/Tamma.Platforms.IntegrationTests --filter "TestCategory=GiteaE2E"`.
- The engine runs as `ASPNETCORE_ENVIRONMENT=Production` (matching the
  deployed engine) — one pre-existing engine DI composition defect
  (`HourlyAnalyticsRollupScheduler`, a captive dependency) refuses a
  Development boot and is outside this suite's scope. Re-entry is
  ENABLED: the engine host now defaults to the HTTP-backed
  latest-accepted read (`HttpLifecycleReEntryService`) — the plan-review
  shim REQUIRES that read, so the old `Documents:ReEntryDisabled=true`
  posture made every cycle terminate needs-human. The full defect
  inventory this suite surfaced (23 items) is recorded in
  `.dev/findings/engine-di-composition-gaps-found-by-e2e.md`.
