# Story 31-1: IGitPlatformClient + IGitPlatformActionsClient + capability matrix

Status: todo (planning brief, 2026-04-21)

## Story

As a **platform engineer**,
I want a single C# abstraction that every git platform (GitHub,
Gitea, Forgejo, GitLab, eventually Bitbucket / Azure DevOps) can
implement, with an explicit capability matrix so callers know which
features are real on which platform,
so that Tamma's agent-dispatch + webhook + repo-listing code is not
forever GitHub-specific and so that adding the next platform is a
driver implementation rather than a control-flow refactor.

## Narrative

Today every call into a git hosting platform goes through one of
three GitHub-specific interfaces: `IGitHubAppClient`,
`IGitHubActionsClient`, `IGitHubSecretsProvisioner`. The activities in
`Tamma.Activities/AgentDispatch/` take `IGitHubActionsClient`
directly. Webhook handling is hard-coded to the GitHub HMAC shape in
`GitHubEndpoints.Webhooks`.

31-1 lands the abstraction layer. Two interfaces:

- **`IGitPlatformClient`** — repos, PRs/MRs, issues, branches, file
  content, webhooks (the "source-host" surface every platform has).
- **`IGitPlatformActionsClient`** — CI dispatch, run monitoring,
  artifact download (the "CI surface" — not every platform has one;
  pure-git forges implement only the first).

A capability matrix describes what each driver supports so the
onboarding UI (31-9) + workflow runtime can route correctly.

## Acceptance Criteria

1. New project / folder `apps/tamma-elsa/src/Tamma.Platforms.Abstractions/`
   (or equivalent — either a new csproj or a namespace inside
   `Tamma.Api`; see technical context) defines:
   - `IGitPlatformClient` with methods covering: `GetRepoAsync`,
     `ListRepoBranchesAsync`, `GetFileContentAsync` (ref-scoped),
     `CreateBranchAsync`, `OpenPullRequestAsync`, `GetPullRequestAsync`,
     `ListPullRequestFilesAsync`, `CreatePullRequestReviewCommentAsync`,
     `MergePullRequestAsync`, `CreateIssueCommentAsync`, `RegisterWebhookAsync`.
   - `IGitPlatformActionsClient` with methods covering:
     `DispatchWorkflowAsync`, `GetRunStatusAsync`, `ListRunJobsAsync`,
     `DownloadArtifactAsync`, `CancelRunAsync`.
   - Result envelope `PlatformResult<T>` matching the existing
     `GitHubAppResult<T>` pattern (`ServiceUnavailable | Ok | Failed`)
     so drivers can fail-soft when creds are missing.
2. Typed models: `Repo`, `Branch`, `PullRequest`, `PrFile`, `Issue`,
   `IssueComment`, `WebhookRegistration`, `WorkflowDispatchRequest`,
   `WorkflowRun`, `WorkflowJob`, `Artifact`, `RateLimitInfo`. All
   platform-neutral — e.g. `PullRequest` has a `sourceBranch /
   targetBranch / number / title / body / state` shape that maps to
   both GitHub PR and GitLab MR.
3. `PlatformCapability` enum exposed per driver — e.g. `Actions`,
   `Artifacts`, `Secrets`, `LibsodiumSecrets`, `PrFileReview`,
   `WebhookHmac`, `WebhookStaticToken`. Interface surface
   `IGitPlatform.GetCapabilities()` returns a readonly set.
4. `PlatformKind` enum: `GitHub`, `Gitea`, `Forgejo`, `GitLab`,
   `Bitbucket`, `AzureDevOps`. Paired with `PlatformKindCapabilityMatrix`
   static class holding the default capabilities for each — the
   onboarding UI uses this to filter picker options without
   instantiating drivers.
5. `IGitPlatformDriver` top-level interface: `PlatformKind Kind { get; }`,
   `IGitPlatformClient Client { get; }`,
   `IGitPlatformActionsClient? Actions { get; }`,
   `ISet<PlatformCapability> Capabilities { get; }`. Drivers register
   via DI keyed on `PlatformKind`.
6. Every model is immutable `record` or `sealed record` with `required`
   + `init` properties. No mutation after construction.
7. Error-mapping contract: each driver must map platform errors to a
   `PlatformError` discriminated union (`AuthExpired`,
   `PermissionDenied`, `NotFound`, `RateLimited`, `ServiceUnavailable`,
   `InvalidRequest`, `Unknown`). Retry policies (31-3..31-6) key off
   this type rather than string-matching messages.
8. xUnit tests in `Tamma.Platforms.Abstractions.Tests` exercising the
   model + capability set logic; no driver dependency yet. Coverage
   target: 100% on non-trivial methods (capability-set builder,
   error-mapping helper).
9. Design doc `apps/tamma-elsa/src/Tamma.Platforms.Abstractions/README.md`
   written alongside the code: shape of the interfaces, capability
   matrix, error contract. Referenced from the epic README.

## Technical Context

### Project layout

Recommend a new csproj `Tamma.Platforms.Abstractions` under
`apps/tamma-elsa/src/`. Separate project keeps drivers out of the
critical-path API assembly and lets consumers take a slim reference
when they only need the interface.

### Relationship to existing code

- `IGitHubAppClient` stays — becomes the GitHub driver's internal
  detail (31-3 wraps it).
- `IGitHubActionsClient` stays as an internal interface of the GitHub
  driver — 31-3 adapts to the new `IGitPlatformActionsClient`.
- `IGitHubSecretsProvisioner` becomes an internal detail of the
  GitHub driver's `ICiSecretsProvisioner` impl (31-8).

Existing call sites (`Activities/AgentDispatch/*`, `GitHubEndpoints.Webhooks`)
are **not** refactored in this story. 31-3 does that when the GitHub
driver lands.

### Capability matrix table (starter — drivers refine at impl time)

| Capability | GitHub | Gitea | Forgejo | GitLab | Bitbucket | Azure DevOps |
|---|---|---|---|---|---|---|
| Actions / CI dispatch | ✅ | ✅ | ✅ | ✅ (pipelines) | ✅ | ✅ |
| Artifacts API | ✅ | ✅ (v1-v4) | ✅ (v1-v4) | ✅ (job artifacts) | ⚠️ (Downloads API) | ✅ |
| Repo secrets via API | ✅ | ✅ | ✅ | ✅ (CI vars) | ✅ | ✅ (var groups) |
| Libsodium sealed-box secrets | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Webhook HMAC signature | ✅ SHA-256 | ✅ SHA-256 | ✅ SHA-256 | ❌ (static token) | ✅ SHA-256 | ✅ (service hooks) |
| Per-app installation auth | ✅ (GitHub App) | partial (OAuth2 app) | partial | partial (OAuth2 app) | partial (OAuth2) | partial (Entra) |
| PR file-level review comments | ✅ | ✅ | ✅ | ✅ | ✅ | ⚠️ |

## Dependencies

- None — foundational
- Blocks 31-2..31-12

## Estimated hours

**22h**

| Task | Hours |
|---|---|
| Interface definitions + typed models | 8 |
| Capability matrix + error union | 4 |
| Driver registration DI pattern | 2 |
| xUnit tests | 4 |
| Design doc | 2 |
| Review feedback buffer | 2 |

## Files touched

- `apps/tamma-elsa/src/Tamma.Platforms.Abstractions/*.cs` (new project)
- `apps/tamma-elsa/src/Tamma.Platforms.Abstractions/README.md` (new)
- `apps/tamma-elsa/Tamma.sln` (add project reference)
- `apps/tamma-elsa/tests/Tamma.Platforms.Abstractions.Tests/*.cs` (new)

## References

- Research notes: [`../research/multi-git-platform-2026.md`](../research/multi-git-platform-2026.md)
- Existing GitHub interfaces: `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/`
- Epic README: [`./README.md`](./README.md)
