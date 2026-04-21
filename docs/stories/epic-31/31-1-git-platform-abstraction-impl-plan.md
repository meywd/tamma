# Story 31-1 Implementation Plan — Git Platform Abstraction + Capability Matrix

**Status**: Planned (2026-04-21)
**Story brief**: [`31-1-git-platform-abstraction.md`](./31-1-git-platform-abstraction.md)
**Epic 31 phase**: Foundation — blocks every other Epic 31 story.
**Branch**: `feat/story-31-1-git-platform-abstraction`

---

## 1. Objective

Ship the `IGitPlatformClient` + `IGitPlatformActionsClient` +
`IGitPlatformDriver` interface trio plus the platform-neutral model
records and capability matrix that every git hosting driver in Epic 31
will implement. No driver wiring yet — this is the seam. Once
shipped, Stories 31-3..31-6 wrap each real platform behind the
interface and the agent-dispatch + webhook + onboarding code
consuming them becomes platform-agnostic.

## 2. Dependencies

Hard blockers:

- **Story 19-1** (C# API port) — project structure + solution
  layout.

Soft:

- **Epic 29-1** (secret store abstraction) — the driver factory
  injected in 31-2 resolves credentials via `ISecretStore`; 31-1
  only defines shapes, so it does not consume the store directly.

Blocks: 31-2, 31-3, 31-4, 31-5, 31-6, 31-7, 31-8, 31-9, 31-10, 31-11,
31-12.

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Platforms.Abstractions/Tamma.Platforms.Abstractions.csproj` | New csproj, slim reference project. |
| `.../IGitPlatformDriver.cs` | Top-level driver interface with `Kind`, `Client`, `Actions?`, `Capabilities`. |
| `.../IGitPlatformClient.cs` | Source-host surface (repos, PRs, branches, file content, webhooks, issue comments). |
| `.../IGitPlatformActionsClient.cs` | CI surface (dispatch, run monitor, jobs, artifacts). |
| `.../PlatformKind.cs` | Enum: `GitHub`, `Gitea`, `Forgejo`, `GitLab`, `Bitbucket`, `AzureDevOps`. |
| `.../PlatformCapability.cs` | Enum: `Actions`, `Artifacts`, `Secrets`, `LibsodiumSecrets`, `ProtectedVariables`, `MaskedVariables`, `PrFileReview`, `WebhookHmac`, `WebhookStaticToken`, `PerAppInstallationAuth`, `ListAccessibleRepos`. |
| `.../PlatformKindCapabilityMatrix.cs` | Static `IReadOnlySet<PlatformCapability> DefaultsFor(PlatformKind)` — onboarding UI reads this without DI. |
| `.../PlatformError.cs` | Discriminated union via `abstract record` + nested records: `AuthExpired`, `PermissionDenied`, `NotFound`, `RateLimited(TimeSpan? retryAfter)`, `ServiceUnavailable`, `InvalidRequest(string code, string? hint)`, `Unknown(string reason)`. |
| `.../PlatformResult.cs` | `abstract record PlatformResult<T>` with `Ok(T)`, `Failed(PlatformError)`, `ServiceUnavailable` variants — parallels existing `GitHubAppResult<T>`. |
| `.../Models/Repo.cs` | `sealed record Repo(string Host, string Owner, string Name, string DefaultBranch, bool IsPrivate, string? Description, string CloneUrl, string HtmlUrl)`. |
| `.../Models/Branch.cs` | `sealed record Branch(string Name, string Sha, bool Protected)`. |
| `.../Models/PullRequest.cs` | `sealed record PullRequest(string Number, string Title, string? Body, string SourceBranch, string TargetBranch, PullRequestState State, string HtmlUrl, string AuthorLogin, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)` + `enum PullRequestState { Open, Closed, Merged, Draft }`. |
| `.../Models/PrFile.cs` | `sealed record PrFile(string Path, PrFileStatus Status, int Additions, int Deletions)`. |
| `.../Models/Issue.cs` | `sealed record Issue(string Number, string Title, string? Body, IssueState State, string HtmlUrl, IReadOnlyList<string> Labels)`. |
| `.../Models/IssueComment.cs` | `sealed record IssueComment(string Id, string Body, string AuthorLogin, DateTimeOffset CreatedAt)`. |
| `.../Models/WebhookRegistration.cs` | `sealed record WebhookRegistration(string Id, string Url, IReadOnlyList<string> Events, bool Active)`. |
| `.../Models/WorkflowDispatchRequest.cs` | `sealed record WorkflowDispatchRequest(string Ref, string? WorkflowFileName, IReadOnlyDictionary<string, string> Inputs, IReadOnlyDictionary<string, string>? Variables = null)`. |
| `.../Models/WorkflowRun.cs` | `sealed record WorkflowRun(string RunId, string Status, string? Conclusion, string HtmlUrl, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, JsonDocument? RawMetadata)`. |
| `.../Models/WorkflowJob.cs` | `sealed record WorkflowJob(string JobId, string Name, string Status, string? Conclusion, JsonDocument? RawMetadata)`. |
| `.../Models/Artifact.cs` | `sealed record Artifact(string Id, string Name, long SizeBytes, string DownloadUrl)`. |
| `.../Models/RateLimitInfo.cs` | `sealed record RateLimitInfo(int? Limit, int? Remaining, DateTimeOffset? ResetsAt)`. |
| `.../Models/PlatformInstallation.cs` | `sealed record PlatformInstallation(Guid Id, Guid TenantId, PlatformKind Kind, string BaseUrl, string? InstallationExternalId)`. |
| `.../Mapping/OctokitErrorMapper.cs` | Placeholder — moves to 31-3 when GitHub driver lands. Included as an extension point so 31-3 lands without restructuring. |
| `.../README.md` | Design doc: interface shape, capability matrix, error contract, DI registration convention. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Platforms.Abstractions.Tests/Tamma.Platforms.Abstractions.Tests.csproj` | Test project. |
| `.../PlatformKindCapabilityMatrixTests.cs` | Assert defaults match the brief's matrix for every `PlatformKind`. |
| `.../PlatformErrorTests.cs` | Round-trip + exhaustiveness on pattern match. |
| `.../PlatformResultTests.cs` | Construction + pattern matching. |
| `.../ModelRecordsTests.cs` | Immutability (via xUnit `Assert.ThrowsAny<>` on reflection `IsInitOnly`), null-guard on `required` fields. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/Tamma.sln` | Add `Tamma.Platforms.Abstractions` + test project. |
| `/home/meywd/tamma/apps/tamma-elsa/Directory.Packages.props` | Ensure `System.Text.Json` + `xunit` + `FluentAssertions` versions consistent. |

No modifications to existing drivers — they become the abstraction's
clients in 31-3.

## 5. Sequence of changes

### Step 1 — csproj + solution wiring (1h)

- Create `Tamma.Platforms.Abstractions.csproj` with `netstandard2.1`
  + `net9.0` multitarget (slim reference; no framework deps).
- Add to `Tamma.sln`.
- Test project targets `net9.0` only.
- **Commit**: `feat(platforms): abstractions project scaffolding`.

### Step 2 — Enums + capability matrix (3h)

- `PlatformKind`, `PlatformCapability`.
- `PlatformKindCapabilityMatrix.DefaultsFor(PlatformKind)` returning
  `IReadOnlySet<PlatformCapability>` — populate per the brief's
  matrix table.
- Test every `PlatformKind` returns a non-empty set; no typos.
- **Commit**: `feat(platforms): PlatformKind + capability matrix`.

### Step 3 — Error + result types (3h)

- `PlatformError` discriminated union via `abstract record` pattern
  Tamma already uses in `Tamma.Data`.
- `PlatformResult<T>` with three variants.
- Pattern-match exhaustiveness: add an analyzer rule (via
  `CSharpIsCompleteSwitchAnalyzer`) or static helper
  `PlatformErrors.Match<T>`.
- Tests: every variant constructible; switch expression covers all.
- **Commit**: `feat(platforms): error + result types`.

### Step 4 — Model records (4h)

- All `Models/*.cs` records. `required` on non-nullable fields;
  `init` only.
- Each model has `JsonPropertyName` attributes sized to the most
  common wire shape (GitHub's — other drivers deserialize into their
  own DTOs then project).
- Tests: record equality, `with`-expression projection, null guards
  via `ArgumentNullException.ThrowIfNull`.
- **Commit**: `feat(platforms): neutral model records`.

### Step 5 — `IGitPlatformClient` surface (4h)

- Declare 11 methods per brief AC1 (GetRepo, ListRepoBranches,
  GetFileContent, CreateBranch, OpenPullRequest, GetPullRequest,
  ListPullRequestFiles, CreatePullRequestReviewComment,
  MergePullRequest, CreateIssueComment, RegisterWebhook).
- Add a 12th method `ListAccessibleReposAsync(CancellationToken)` per
  31-9 AC7 note — needed by onboarding UI.
- All methods return `Task<PlatformResult<T>>`.
- Parameter records where the call has >3 params (e.g.
  `OpenPullRequestRequest`).
- No impl in this story — interface only.
- **Commit**: `feat(platforms): IGitPlatformClient interface`.

### Step 6 — `IGitPlatformActionsClient` surface (2h)

- 5 methods per brief AC1: `DispatchWorkflowAsync`, `GetRunStatusAsync`,
  `ListRunJobsAsync`, `DownloadArtifactAsync`, `CancelRunAsync`.
- `DownloadArtifactAsync` returns `Task<PlatformResult<Stream>>` —
  caller owns disposal.
- **Commit**: `feat(platforms): IGitPlatformActionsClient interface`.

### Step 7 — `IGitPlatformDriver` top-level + DI pattern (2h)

- Top-level driver interface with properties `Kind`,
  `Client`, `Actions?`, `Capabilities`.
- Document keyed-DI convention: each driver registers via
  `services.AddKeyedSingleton<IGitPlatformDriver, XDriver>(PlatformKind.X)`.
- 31-2 consumes via `IKeyedServiceProvider`.
- **Commit**: `feat(platforms): IGitPlatformDriver + DI convention`.

### Step 8 — README design doc (2h)

- Sections: interface shape rationale, capability matrix table,
  error contract, DI pattern, relationship to existing GitHub clients,
  pointer to 31-3 for the first impl.
- Referenced from epic README.
- **Commit**: `docs(platforms): abstractions design doc`.

### Step 9 — Test coverage pass (1h)

- Run `dotnet test` with coverage. Target ≥95% on non-trivial logic
  (matrix builder, error-mapping helper). Interface-only files have no
  executable lines.
- **Commit**: `test(platforms): coverage pass`.

## 6. Test strategy

### Unit

- **Capability matrix**: every `PlatformKind` returns a distinct set;
  set membership matches brief's table (assert by string names to
  survive enum renumbering).
- **PlatformError**: each variant constructible, pattern-match
  exhaustiveness covered.
- **PlatformResult**: `Map`, `Bind`, `IsOk` helpers.
- **Model records**: `required` fields throw when omitted; `with`
  expression creates proper copy; equality by value.
- **DI pattern**: fake driver registered under keyed DI; resolved by
  `IKeyedServiceProvider.GetKeyedService<IGitPlatformDriver>(PlatformKind.GitHub)`.

### Integration

- None — this is an interface-only story.

### Contract-test scaffolding

- Define `abstract class GitPlatformClientContractTests<TFixture>`
  that 31-3/31-4/31-6 will subclass. Methods remain abstract;
  concrete fixtures come from driver stories.
- Ship the base class without concrete subclasses; exercised later.

## 7. Rollback plan

- **Revert**: single chain of commits. Removing the project has no
  downstream effect because no caller references it yet. The only
  change to existing code is the `.sln` addition; reverting drops
  the line.
- **Non-reversible**: none.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. csproj + solution | 1 |
| 2. Enums + capability matrix | 3 |
| 3. Error + result types | 3 |
| 4. Model records | 4 |
| 5. `IGitPlatformClient` | 4 |
| 6. `IGitPlatformActionsClient` | 2 |
| 7. `IGitPlatformDriver` + DI | 2 |
| 8. README design doc | 2 |
| 9. Coverage | 1 |
| **Total** | **22** (matches brief). |

## 9. Open questions

- **Multitarget `netstandard2.1` + `net9.0` vs `net9.0` only**: the
  brief suggests a slim reference. `netstandard2.1` multitarget
  supports future Rider plugin or CLI-distributed uses; costs
  nothing structurally. Plan: ship multitarget unless a Directory.Build.props
  rule forbids (no such rule exists). Document choice.
- **Models pre-commit to `RawMetadata JsonDocument?`**: used by 31-6
  (GitLab) for driver-specific fields. Adds a dependency on
  `System.Text.Json` in the reference assembly. Plan: include up
  front so 31-6 doesn't need to retrofit.
- **`PullRequestState.Draft` vs. separate `bool IsDraft`**: GitHub
  has state + draft flag independently (a PR can be "open + draft").
  GitLab MRs use "WIP" which is a flag on state. Plan: `enum
  PullRequestState` + separate `bool IsDraft` property. Document
  in README.
- **`PlatformResult.ServiceUnavailable` vs. `PlatformError.ServiceUnavailable`**:
  current `GitHubAppResult<T>` uses `ServiceUnavailable` as a distinct
  result variant for missing-creds. Plan: `PlatformResult<T>` exposes
  `ServiceUnavailable` as a result variant (not wrapped in Failed)
  so callers can cheaply detect the null-config path. Mirrors the
  today's pattern; no regression.
- **`ListAccessibleReposAsync` pagination**: GitHub pages via
  `IAsyncEnumerable<Repo>`. GitLab uses link-header pagination.
  Plan: return `IAsyncEnumerable<Repo>` in the interface; driver
  handles pagination mechanics.
- **Naming collision**: `Tamma.Platforms.Abstractions` vs. existing
  `packages/platforms/` TS package. The TS package is not being
  ported. Plan: accept collision; TS package is docs-only until
  Epic 19 Phase 3 deletion.
