# Story 31-10: Integration test harness — Gitea + Forgejo + GitLab containers

Status: todo (planning brief, 2026-04-21)

## Story

As a **developer working on any of the platform drivers**,
I want a reproducible integration test harness that boots Gitea,
Forgejo, and GitLab test containers, creates a fixture repo + bot
user + webhook secret per container, and exercises the driver
against the live instance,
so that regression risks on the non-GitHub drivers are caught before
merge rather than in staging.

## Narrative

Today's CI runs unit tests with WireMock-style fakes. That's fine for
driver-logic regressions but misses real platform surprises: API
version drift, CI runner bootstrap issues, webhook payload schema
changes. Story 31-10 ships a harness that runs against real
containerised platforms on a schedule (nightly + per-PR for changed
driver code).

Using [testcontainers-git](https://github.com/sparsick/testcontainers-git)
patterns adapted for .NET:

- **Gitea**: `gitea/gitea:1.25` — boots in ~90s; creates a bot user
  and a fixture repo via the REST API at startup.
- **Forgejo**: `codeberg.org/forgejo/forgejo:15-rootless` — same shape.
- **GitLab**: `gitlab/gitlab-ce:latest` — heavier (~3GB image, 5-8
  min boot); used on scheduled nightly runs only.

## Acceptance Criteria

1. New test project `apps/tamma-elsa/tests/Tamma.Platforms.IntegrationTests/`
   with:
   - `GiteaContainerFixture` — IAsyncLifetime. Boots a Gitea
     container, seeds admin credentials, creates a bot user + PAT,
     creates a fixture repo with a sample workflow file, exposes
     `BaseUrl`, `BotToken`, `WebhookSecret` properties.
   - `ForgejoContainerFixture` — same shape against Forgejo image.
   - `GitLabContainerFixture` — same shape against GitLab image.
2. Fixture base class `PlatformContainerFixture<T>` shared between
   all three to avoid duplication.
3. Testcontainers configuration via `Testcontainers.PostgreSql` +
   `Testcontainers.Core` patterns the rest of the integration
   tests use; healthcheck waits for `/api/v1/version` 200 before
   proceeding.
4. `ContractTestSuite<TDriver>` — the shared xUnit theory that
   exercises every driver against its fixture:
   - Repo read / list branches / file content.
   - Branch create / PR (or MR) open / merge.
   - Issue comment create.
   - Actions dispatch (skipped on platforms without Actions runner
     running — documented skip reason in the test output).
   - Webhook register + hmac / static-token verify round-trip.
   - Secret push (plaintext for Gitea/Forgejo/GitLab; libsodium
     tested separately on the GitHub driver with a mocked API).
5. CI gating policy documented in
   `.github/workflows/integration-tests.yml`:
   - Gitea + Forgejo suites run **on PRs that touch
     `apps/tamma-elsa/src/Tamma.Platforms.{Gitea,GitLab}/**`**, and
     on every merge to `main`.
   - GitLab suite runs on **scheduled nightly only** because the
     image is heavy. PRs that touch only the GitLab driver can
     tag `run-gitlab-integration` on the PR to opt-in.
6. Docker-in-Docker support — the integration workflow runs inside a
   GitHub Actions runner with the Docker service available; per
   testcontainers-dotnet [docs](https://dotnet.testcontainers.org/cicd/).
7. Timeout handling — every test has a 5-minute max; a stuck
   container is teardown + a clear error message. CI total wall-
   clock ≤15 minutes for Gitea + Forgejo combined.
8. Fixture seed data is re-created on every test run (containers
   are ephemeral) — no state leaks between tests.
9. Webhook callback path for tests: the test harness exposes a
   temporary listener on a random port inside the test process.
   Each platform's webhook is configured against the
   listener URL. Test waits for a single delivery with timeout,
   verifies signature, and passes.
10. Test run output includes per-platform, per-test timing to
    surface regressions in the platforms' own performance (e.g. a
    Gitea change that makes run-dispatch take 10× longer).
11. Documentation `apps/tamma-elsa/tests/Tamma.Platforms.IntegrationTests/README.md`
    covers how to run the harness locally, how to add a new
    platform container, and how to debug failed container boots.

## Technical Context

### Heavy GitLab image — CI cost

A `gitlab-ce` container on a shared GitHub Actions runner costs ~8
minutes of boot + ~3 min of tests = 11 min wall-clock per run.
Nightly-only keeps it manageable; if a GitLab-specific bug gets
merged without detection, the tenant-facing fix window is <24h via
the nightly alert.

### Gitea runner for Actions tests

Gitea Actions dispatch requires a runner registered to the
instance. Harness boots an `act_runner` sidecar container that auto-
registers. Fixture workflow is a tiny 5-second step; dispatch → run
completion round-trip is ≤60s.

### Why not WireMock-only

The four drivers (31-3, 31-4, 31-5, 31-6) already have WireMock
unit tests. The integration harness catches what WireMock can't:
real OAuth/PAT token lifecycles, real webhook retries, real API
quirks. Complementary, not duplicate.

### Forgejo Actions runner

Forgejo 15's ephemeral runner support ([v15 release notes](https://forgejo.org/2026-04-release-v15-0/))
makes this cleaner than Gitea — runner boots, runs one job, exits.
Harness uses a single ephemeral runner per test.

## Dependencies

- **31-3, 31-4, 31-5, 31-6** — drivers to test
- Integration test infrastructure already running (containers used
  elsewhere in the repo)

## Estimated hours

**22h**

| Task | Hours |
|---|---|
| Base fixture + Gitea fixture | 4 |
| Forgejo fixture | 1 |
| GitLab fixture (heavy; networking quirks) | 6 |
| Contract-test suite shared across drivers | 5 |
| CI workflow integration + scheduled nightly | 3 |
| Gitea / Forgejo runners for Actions tests | 2 |
| Docs + flakes + review buffer | 1 |

## Files touched

- `apps/tamma-elsa/tests/Tamma.Platforms.IntegrationTests/*.cs` (new project)
- `apps/tamma-elsa/tests/Tamma.Platforms.IntegrationTests/README.md` (new)
- `.github/workflows/integration-tests.yml` (new or modify)
- `apps/tamma-elsa/Tamma.sln` (add project)

## References

- Research notes: [`../research/multi-git-platform-2026.md`](../research/multi-git-platform-2026.md) §7
- [testcontainers-git](https://github.com/sparsick/testcontainers-git)
- [Testcontainers for .NET CI/CD](https://dotnet.testcontainers.org/cicd/)
- [Forgejo v15 ephemeral runners](https://forgejo.org/2026-04-release-v15-0/)
