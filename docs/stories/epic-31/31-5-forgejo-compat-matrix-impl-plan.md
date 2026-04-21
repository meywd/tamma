# Story 31-5 Implementation Plan — Forgejo Compat Shim + Test-Matrix Extension

**Status**: Planned (2026-04-21)
**Story brief**: [`31-5-forgejo-compat-matrix.md`](./31-5-forgejo-compat-matrix.md)
**Epic 31 phase**: Layer 4 — thin addition after 31-4.
**Branch**: `feat/story-31-5-forgejo-compat-matrix`

---

## 1. Objective

Make the 31-4 Gitea driver work as-is against Forgejo 15.0+ with
two minimal changes: (1) a distinct `PlatformKind.Forgejo` entry so
the onboarding UI can brand Forgejo separately, and (2) a webhook
signature verifier configured to accept `X-Forgejo-Signature` (with
fallback to `X-Gitea-Signature` for older forks). Add a Forgejo
container to the 31-10 harness so the shared contract test fixture
runs against both drivers. Research §2 confirms Forgejo 15.0 (April
2026) retains Gitea DB + REST API compatibility by design — the
driver is a class, not a project.

## 2. Dependencies

Hard blockers:

- **Story 31-4** — Gitea driver provides the shared HTTP client,
  OAuth2 flow, endpoint coverage.
- **Story 31-1** — abstraction + capability matrix accepts
  `PlatformKind.Forgejo`.

Soft:

- **Story 31-10** — integration test harness. This story ships the
  Forgejo container fixture; 31-10 picks it up on its own schedule.

Blocks: **31-9** (picker includes Forgejo), **31-10** (test matrix).

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.Gitea/ForgejoPlatformDriver.cs` | Lives in the Gitea project. Sealed class inheriting or composing `GiteaPlatformDriver`; `Kind = PlatformKind.Forgejo`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.Gitea/ForgejoPlatformDriverFactory.cs` | Factory that builds the same `GiteaHttpClient` + driver stack, with the Forgejo-flavoured webhook verifier configured. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.Gitea/ForgejoDriverRegistrationExtensions.cs` | `services.AddForgejoPlatformDriver()` extension. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Platforms.Gitea.Tests/ForgejoContractTests.cs` | Subclass `GitPlatformClientContractTests<ForgejoFixture>`; verifies Forgejo driver passes the same contract as Gitea. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Platforms.Gitea.Tests/ForgejoWebhookSignatureVerifierTests.cs` | Accepts `X-Forgejo-Signature`; falls back to `X-Gitea-Signature`. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Platforms.IntegrationTests/ForgejoContainerFixture.cs` | IAsyncLifetime fixture for `codeberg.org/forgejo/forgejo:15-rootless`. Pre-work for 31-10; lives in the 31-10 test project. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.Gitea/GiteaWebhookSignatureVerifier.cs` | Constructor already accepts a header-name list (per 31-4); no code change. `ForgejoPlatformDriver` passes `["X-Forgejo-Signature","X-Gitea-Signature"]`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.Gitea/README.md` | New "Forgejo compatibility" section listing the two divergence points and future-drift policy. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.Abstractions/PlatformKindCapabilityMatrix.cs` | Confirm Forgejo row identical to Gitea row (already provisioned in 31-1; sanity-check here). |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Program.cs` | `services.AddForgejoPlatformDriver();` (alongside Gitea). |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.Abstractions/PlatformKind.cs` | Confirm `Forgejo` value exists (already added in 31-1). |

## 5. Sequence of changes

### Step 1 — `ForgejoPlatformDriver` class (1h)

- `ForgejoPlatformDriver : IGitPlatformDriver` — composition over
  inheritance; wraps a `GiteaPlatformDriver` internally.
- `Kind` returns `PlatformKind.Forgejo`.
- `Client`, `Actions`, `Capabilities` delegate to the wrapped Gitea
  driver — identical today; a future divergence overrides.
- Unit test: `Kind == Forgejo`, all delegated calls return correct
  results.
- **Commit**: `feat(platforms.gitea): ForgejoPlatformDriver wrapper`.

### Step 2 — Factory + DI extension (1h)

- `ForgejoPlatformDriverFactory.BuildAsync(installation, secrets, ct)`:
  same as Gitea factory but configures the webhook verifier with
  `["X-Forgejo-Signature", "X-Gitea-Signature"]`.
- `AddForgejoPlatformDriver()` registers factory under
  `PlatformKind.Forgejo`.
- **Commit**: `feat(platforms.gitea): Forgejo factory + DI`.

### Step 3 — Webhook verifier tests (2h)

- `ForgejoWebhookSignatureVerifierTests`:
  - Payload signed with secret; header `X-Forgejo-Signature` → valid.
  - Payload signed with secret; header `X-Gitea-Signature` only
    (older fork) → valid (fallback).
  - Payload signed with secret; header `X-Hub-Signature-256` (wrong
    platform) → rejected.
  - Signature mismatch → rejected.
  - Missing secret → fail-closed `ServiceUnavailable`.
- **Commit**: `test(platforms.gitea): Forgejo signature fallback`.

### Step 4 — Forgejo test container fixture (3h)

- `ForgejoContainerFixture`:
  - Container `codeberg.org/forgejo/forgejo:15-rootless`, port
    `3000`. Healthcheck `/api/v1/version`.
  - On `InitializeAsync()`: create admin user (via
    `forgejo admin user create` CLI invocation in a docker `exec`
    against the container), then REST-create bot user + PAT +
    fixture repo + sample workflow file.
  - Exposes `BaseUrl`, `BotToken`, `WebhookSecret`.
  - Lives in `Tamma.Platforms.IntegrationTests/` (the 31-10 project;
    this story creates the file early so the contract test can use
    it).
- Unit-level smoke test: fixture boots + bot user can GET own
  profile.
- **Commit**: `test(integration): Forgejo container fixture`.

### Step 5 — Contract test subclass (1h)

- `ForgejoContractTests : GitPlatformClientContractTests<ForgejoFixture>`
  — same test methods from the shared base class run against the
  Forgejo container. Skip reasons documented for any divergent
  test (today: none).
- Runs on the integration-tests CI workflow (31-10 owns the
  workflow, this story's subclass plugs in).
- **Commit**: `test(platforms.gitea): Forgejo contract suite`.

### Step 6 — Docs (1h)

- Append a "Forgejo compatibility" section to
  `Tamma.Platforms.Gitea/README.md`:
  - Two divergence points (header name fallback; distinct
    `PlatformKind`).
  - Future-drift policy: if Forgejo diverges in a way that breaks
    the wrapper, promote `ForgejoPlatformDriver` into a full driver
    with its own `GiteaHttpClient` subclass.
- **Commit**: `docs(platforms.gitea): Forgejo compatibility`.

## 6. Test strategy

### Unit

- Verifier fallback: `X-Forgejo-Signature` preferred; `X-Gitea-
  Signature` second preference.
- `ForgejoPlatformDriver.Kind == Forgejo`.
- Capability set matches Gitea's today.

### Integration (31-10 harness, runs nightly + per-PR on touch)

- Forgejo container fixture + contract test suite.
- `forgejo` + `gitea` run in the same CI job; timeouts per brief
  31-10 (≤15 min wall-clock combined).

### Regression

- Changing the shared `GiteaHttpClient` breaks both drivers
  simultaneously — that's a feature, not a bug: the shared
  codepath is covered twice.

## 7. Rollback plan

- **Revert commits**: removes `ForgejoPlatformDriver` + factory +
  DI extension. Any tenant that connected a Forgejo install loses
  connectivity. No DB state to rollback — the
  `tenant_platform_installations` row with `platform_kind='forgejo'`
  remains orphaned but causes no errors (resolver returns null).
- **Non-reversible**: none.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. `ForgejoPlatformDriver` class | 1 |
| 2. Factory + DI | 1 |
| 3. Signature verifier tests | 2 |
| 4. Container fixture | 3 |
| 5. Contract subclass | 1 |
| 6. Docs | 1 |
| Research/review buffer | — |
| **Total** | **9** (brief: 8 — 1h buffer added for fixture networking quirks). |

## 9. Open questions

- **Inheritance vs composition**: brief suggests "inheriting or
  wrapping". Plan: composition. Inheritance of `GiteaPlatformDriver`
  couples Forgejo to Gitea's concrete class — if Gitea diverges
  Forgejo breaks silently. Composition lets us swap `Gitea` for a
  `ForgejoSpecific` variant without touching callers.
- **Forgejo runner image**: research §2 mentions Forgejo 15's
  ephemeral runner support. Plan: `ForgejoContainerFixture` boots
  a single `gitea/act_runner` container pointed at the Forgejo
  instance (act_runner is compatible with both). Runner auto-
  registers using a shared secret at fixture init.
- **Codeberg vs self-hosted container image**: official image is
  at `codeberg.org/forgejo/forgejo`. Docker Hub carries mirrors via
  `forgejoclone/forgejo`. Plan: use `codeberg.org/forgejo/forgejo:
  15-rootless`. Add a fallback to the Docker Hub mirror in the
  fixture if Codeberg is unreachable (CI flake mitigation). Document.
- **Signature fallback priority**: current behaviour tries
  `X-Forgejo-Signature` first. If a malicious actor sends both
  headers with different signatures, we accept the first valid one.
  Plan: accept `X-Forgejo-Signature` only if the sender is a modern
  Forgejo; accept `X-Gitea-Signature` only if `X-Forgejo-Signature`
  is absent. Document.
- **When to promote to full driver**: "if Forgejo diverges" is
  subjective. Plan: a contract-test failure is the trigger. Until
  then, the wrapper is cheaper than duplication.
- **Capability divergence tracking**: `PlatformKindCapabilityMatrix`
  has a Forgejo row already. If Forgejo adds a capability Gitea
  doesn't (e.g. OIDC in v15), we'd extend the matrix + implement
  only in Forgejo driver. Plan: OIDC is out of scope for 31-5 (not
  used by Tamma today). Document as follow-up.
- **Container fixture ownership**: the fixture lives in the 31-10
  project. Plan: this story creates the file; 31-10 wires it into
  the CI workflow. Confirms 31-10 is not blocked waiting for it.
