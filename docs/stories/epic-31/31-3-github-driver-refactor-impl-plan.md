# Story 31-3 Implementation Plan — GitHub Driver Refactor

**Status**: Planned (2026-04-21)
**Story brief**: [`31-3-github-driver-refactor.md`](./31-3-github-driver-refactor.md)
**Epic 31 phase**: Foundation — after 31-1/31-2; blocks 31-7/31-8.
**Branch**: `feat/story-31-3-github-driver-refactor`

---

## 1. Objective

Wrap the existing `IGitHubAppClient` / `IGitHubActionsClient` /
`IGitHubSecretsProvisioner` behind the `IGitPlatformClient` /
`IGitPlatformActionsClient` / `IGitPlatformDriver` surface shipped
in 31-1. Refactor call sites (agent-dispatch activities, install-
metadata fetches) to take `IPlatformResolver` and resolve their
driver from the tenant context. Mechanical refactor; no feature
work. Post-31-3, GitHub is one of N drivers rather than the hard-
coded path.

## 2. Dependencies

Hard blockers:

- **Story 31-1** — abstraction interfaces + models + capability
  matrix.
- **Story 31-2** — `IPlatformResolver` + `tenant_platform_installations`
  table + credential store wiring.

Soft:

- **Story 29-2** — secret store backing; `InstallationRouterService`
  still works against its own secret-loading path until 31-3 migrates
  to `ISecretStore`.

Blocks: **31-7** (webhook endpoint port), **31-8** (secrets
provisioner abstraction), **31-9** (onboarding UI's GitHub branch
routes through the resolver too).

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.GitHub/Tamma.Platforms.GitHub.csproj` | New driver project, references `Tamma.Platforms.Abstractions` + `Tamma.Api` (for Octokit wrappers). |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.GitHub/GitHubPlatformDriver.cs` | `IGitPlatformDriver` impl. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.GitHub/GitHubPlatformClient.cs` | `IGitPlatformClient` impl — wraps `OctokitGitHubAppClient` + adds repo/PR/branch ops not yet covered. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.GitHub/GitHubActionsPlatformClient.cs` | `IGitPlatformActionsClient` impl — wraps `OctokitGitHubActionsClient`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.GitHub/GitHubPlatformDriverFactory.cs` | Factory consumed by 31-2's keyed-DI resolver; binds an installation's credential + base URL to a live driver. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.GitHub/OctokitErrorMapper.cs` | Moved from abstraction placeholder — real impl here. Maps Octokit exceptions to `PlatformError`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.GitHub/GitHubDriverRegistrationExtensions.cs` | `services.AddGitHubPlatformDriver()` extension. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Platforms.GitHub.Tests/Tamma.Platforms.GitHub.Tests.csproj` | Test project. |
| `.../GitHubPlatformDriverTests.cs` | Capabilities match matrix; Kind is GitHub. |
| `.../GitHubPlatformClientTests.cs` | Each new client method; Octokit → `PlatformError` mapping per known exception. |
| `.../GitHubActionsPlatformClientTests.cs` | Dispatch + run monitor paths exercised via WireMock. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/Tamma.sln` | Add Platforms.GitHub + test project. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/GitHub/IGitHubAppClient.cs` | Visibility: `public interface` → `internal interface`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/GitHub/IGitHubSecretsProvisioner.cs` | Mark `[Obsolete("Use ICiSecretsProvisioner via IGitPlatformDriver.", false)]` — 31-8 removes later. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/IGitHubActionsClient.cs` | Visibility → `internal`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/GitHubActionsExecutor.cs` | Constructor now takes `IPlatformResolver` + reads `tenantId` from the workflow context. Resolves driver at dispatch. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/DispatchAgentWorkflowActivity.cs` | Ditto — `IPlatformResolver` injection. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/MonitorAgentWorkflowActivity.cs` | Ditto. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/CollectAgentResultsActivity.cs` | Ditto. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/IAgentDispatchServices.cs` | If this aggregate interface lists GitHub clients, swap to resolver. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/GitHubEndpoints.cs` | Replace direct `IGitHubAppClient` usage with `IPlatformResolver` where metadata is fetched post-install. `Webhooks` handler untouched — 31-7 owns it. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Program.cs` | Replace `AddGitHubAppClient()` / `AddGitHubActionsClient()` / `AddGitHubSecretsProvisioner()` with a single `AddGitHubPlatformDriver()` call. |
| `/home/meywd/tamma/apps/tamma-elsa/README.md` | Platform-integration section updated: point to `Tamma.Platforms.GitHub/`. |

## 5. Sequence of changes

### Step 1 — Driver project scaffolding + capability set (2h)

- New csproj; reference `Tamma.Platforms.Abstractions`, `Tamma.Api`
  (for Octokit clients), `Tamma.Data` (for secret loading).
- `GitHubPlatformDriver` shell returning capabilities from the 31-1
  matrix for `PlatformKind.GitHub`.
- Test: capabilities match matrix (serialise both sets → compare
  string-keyed).
- **Commit**: `feat(platforms.github): driver shell`.

### Step 2 — `OctokitErrorMapper` (2h)

- Move placeholder from `Tamma.Platforms.Abstractions` to this project.
- Mapping:
  - `AuthorizationException` → `PermissionDenied` or `AuthExpired`
    (distinguish via `HttpStatusCode`).
  - `NotFoundException` → `NotFound`.
  - `RateLimitExceededException` → `RateLimited(retryAfter)` computed
    from `e.Reset - DateTimeOffset.UtcNow`.
  - `AbuseException` → `RateLimited` with abuse reset.
  - `ApiValidationException` → `InvalidRequest`.
  - `ApiException` with 5xx → `ServiceUnavailable`.
  - Others → `Unknown(e.Message)`.
- Table-driven unit tests: one case per exception.
- **Commit**: `feat(platforms.github): Octokit error mapper`.

### Step 3 — `GitHubPlatformClient` (4h)

- Impl of 12 `IGitPlatformClient` methods, delegating to
  `OctokitGitHubAppClient` where the method exists and filling gaps
  (branch create, file content with ref) with direct Octokit calls.
- Each method wraps call in `try / catch (Exception e) → return
  OctokitErrorMapper.Map(e)`.
- Pagination for `ListAccessibleReposAsync` uses
  `ApiConnection.GetAllPages<Repository>(…)` → projected to `Repo`.
- Unit tests: happy path per method via WireMock; error mapping
  per method.
- **Commit**: `feat(platforms.github): IGitPlatformClient impl`.

### Step 4 — `GitHubActionsPlatformClient` (2h)

- Wraps `OctokitGitHubActionsClient`. Same error-handling pattern.
- `DownloadArtifactAsync` returns a `Stream` from the existing
  `LimitedStream`-wrapped artifact API.
- **Commit**: `feat(platforms.github): IGitPlatformActionsClient impl`.

### Step 5 — Driver factory + credential load (2h)

- `GitHubPlatformDriverFactory.BuildAsync(PlatformInstallation, ISecretStore, ct)`:
  1. Load private key via `secrets.GetAsync(row.CredentialSecretId)`.
  2. Build `OctokitGitHubAppClient` configured with PEM + app id.
  3. Wrap in `GitHubPlatformClient` + `GitHubActionsPlatformClient`.
  4. Return `GitHubPlatformDriver`.
- Missing-creds path: factory returns a driver backed by
  `NullGitHubAppClient` (today's fallback); `Capabilities` excludes
  `Actions`/`Artifacts`/`Secrets`.
- **Commit**: `feat(platforms.github): driver factory`.

### Step 6 — DI extension (1h)

- `AddGitHubPlatformDriver()` extension:
  - Registers factory as keyed singleton under `PlatformKind.GitHub`.
  - Registers internal Octokit clients (previously public).
  - Registers the mapper.
- `Program.cs` now has a single `services.AddGitHubPlatformDriver()`
  call replacing three previous registrations.
- **Commit**: `feat(api): AddGitHubPlatformDriver DI extension`.

### Step 7 — Call-site refactor: agent dispatch (4h)

- For each of `GitHubActionsExecutor`, `DispatchAgentWorkflowActivity`,
  `MonitorAgentWorkflowActivity`, `CollectAgentResultsActivity`:
  1. Replace constructor param `IGitHubActionsClient` with
     `IPlatformResolver`.
  2. In each method that used the old client, resolve:
     `var driver = await _resolver.ResolveForTenantAsync(ctx.TenantId, ct);`
  3. Use `driver.Actions.DispatchWorkflowAsync(…)` etc.
  4. If `driver` is null or `Actions` is null → same failure path
     as today's `ServiceUnavailable` branch.
- Elsa `WorkflowExecutionContext` carries `TenantId`; read via the
  existing `ctx.GetTenantIdOrThrow()` helper (post-28).
- Existing unit tests use `IGitHubActionsClient` mock — swap to
  `IPlatformResolver` mock returning a fake `IGitPlatformDriver`.
- **Commit**: `refactor(activities): resolver-based agent dispatch`.

### Step 8 — Call-site refactor: endpoints + router (2h)

- `GitHubEndpoints` paths that fetch installation metadata (e.g.
  `GET /api/v1/github/installations/:id/repos`) now resolve via
  `IPlatformResolver.ResolveForWebhookAsync(installationId)`.
- `InstallationRouterService` stays (internal, used by the factory)
  but is no longer called by external code.
- **Commit**: `refactor(api): resolver-based GitHub endpoint fetch`.

### Step 9 — Visibility changes + obsolete flags (1h)

- `IGitHubAppClient`, `IGitHubActionsClient` → `internal`.
- `IGitHubSecretsProvisioner` → `[Obsolete]` (still public;
  consumers inside 31-3 use it; 31-8 replaces).
- Fix compilation fallout.
- **Commit**: `chore(github): seal internal client interfaces`.

### Step 10 — Tests: driver + refactored activities (3h)

- `GitHubPlatformDriverTests.CapabilitiesMatchMatrix`.
- `GitHubPlatformClientTests.MapsOctokitErrorsToPlatformError` —
  table-driven test per exception.
- Existing activity tests: update mocks + assert identical behaviour.
- **Commit**: `test(platforms.github): driver + activity coverage`.

### Step 11 — Docs (1h)

- Update `apps/tamma-elsa/README.md` platform-integration section.
- Add `Tamma.Platforms.GitHub/README.md` describing the driver layout
  + mapping conventions.
- **Commit**: `docs(platforms.github): driver README`.

## 6. Test strategy

### Unit

- Every `GitHubPlatformClient` method: happy path + each mapped
  exception.
- `OctokitErrorMapper`: table-driven per exception kind.
- Activity tests mock `IPlatformResolver` returning a fake driver;
  assert same behaviour as pre-refactor (no regressions).

### Integration

- Existing `OctokitGitHubAppClient` + `OctokitGitHubActionsClient`
  integration tests (with real GitHub test org credentials) keep
  running unchanged — they cover the inner layer.

### Behaviour-drift check

- Run all pre-existing agent-dispatch end-to-end tests (from Epic 9)
  against the refactored activities. Zero assertion changes.

## 7. Rollback plan

- **Revert commits**: every call-site refactor is a separate commit,
  so a partial revert (just the driver wiring) works if the activity
  refactor needs tuning.
- **Internal visibility flip**: reverting restores public surface
  on `IGitHubAppClient`/`IGitHubActionsClient`; no external consumers
  exist outside the solution.
- **Behaviour drift safety**: the old `Null*Client` fallbacks are
  preserved inside the driver factory. A missing-creds install
  behaves the same pre/post-31-3.
- **Non-reversible**: none.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Driver project scaffolding | 2 |
| 2. Error mapper | 2 |
| 3. `IGitPlatformClient` impl | 4 |
| 4. `IGitPlatformActionsClient` impl | 2 |
| 5. Driver factory + credential load | 2 |
| 6. DI extension | 1 |
| 7. Agent-dispatch call-site refactor | 4 |
| 8. Endpoint call-site refactor | 2 |
| 9. Visibility + obsolete | 1 |
| 10. Tests | 3 |
| 11. Docs | 1 |
| **Total** | **24** (brief: 16 — variance: call-site fan-out is wider than initial estimate; 5 activity files + endpoint fetches). |

## 9. Open questions

- **`Tamma.Api` reference cycle**: `Tamma.Platforms.GitHub` needs
  `OctokitGitHubAppClient` which lives in `Tamma.Api`. If
  `Tamma.Api` later references `Tamma.Platforms.GitHub` for DI we
  have a cycle. Plan: move `Tamma.Api/Services/GitHub/` into
  `Tamma.Platforms.GitHub/Octokit/` as an internal folder inside
  the new driver project. One atomic move in Step 1. Document the
  file movement in the commit message.
- **Workflow context tenant id**: Elsa activities need
  `ctx.GetTenantIdOrThrow()`. Existing Epic 28 work should provide
  this. If not yet plumbed, add to 31-3 scope (extra ~2h). Confirm
  by reading `Tamma.Activities/WorkflowContext.cs` at impl start.
- **Null driver path for installations with no credential**: the
  `NullGitHubAppClient` today returns `ServiceUnavailable`. The
  driver factory must preserve this path. Plan: factory checks
  `Capabilities` — if `GitHub:AppId` config is missing, return a
  driver with `Capabilities = { }` (empty). Callers that check
  `driver.Capabilities.Contains(Actions)` before dispatching are
  safe.
- **Dual-write to `github_installations` during transition**: 31-2
  keeps `github_installations` writable for backward-compat. 31-3's
  refactored code reads through `tenant_platform_installations`.
  The legacy table stays in sync only via the migration backfill in
  31-2 — no ongoing dual-write. Acceptable because onboarding (31-9)
  writes to the new table going forward.
- **`IGitHubSecretsProvisioner` migration window**: this story keeps
  it callable (just `[Obsolete]`). 31-8 replaces. If 31-8 is scheduled
  more than one sprint later, leave the Obsolete flag but don't
  hard-break. Plan: remove Obsolete flag only once 31-8 ships and
  all callers migrate.
