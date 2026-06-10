# Tamma.Platforms.Abstractions

Story 31-1 — platform-neutral git hosting abstraction. Drivers
(GitHub, Gitea, Forgejo, GitLab, Bitbucket, Azure DevOps) implement
the interfaces here and register via keyed DI under their
`PlatformKind`.

## Why this exists

Today every git-platform call goes through GitHub-specific types
(`IGitHubAppClient`, `IGitHubActionsClient`, `IGitHubSecretsProvisioner`).
Activities in `Tamma.Activities/AgentDispatch/` take
`IGitHubActionsClient` directly; webhook handling is hard-coded to
GitHub HMAC. This package is the seam that lets Tamma drive any of
the supported git hosts behind one shape.

31-1 ships the seam ONLY. Driver implementations land in 31-3
through 31-6 (GitHub refactor, Gitea, Forgejo, GitLab); the routing
registry lands in 31-2. Bitbucket and Azure DevOps are deferred
(31-11/31-12) but are encoded in the capability matrix today so the
onboarding picker (31-9) can render them as "coming soon".

## Interfaces

### `IGitPlatformDriver`

Top-level facade. Composes:

- `Kind: PlatformKind` — discriminator the registry routes on.
- `Client: IGitPlatformClient` — source-host surface (mandatory).
- `Actions: IGitPlatformActionsClient?` — CI surface (optional —
  null when the platform has no CI or Tamma is read-only).
- `Capabilities: IReadOnlySet<PlatformCapability>` — effective
  capability set for THIS driver instance.

### `IGitPlatformClient`

12 methods covering source-host operations:

| Method | Purpose |
|---|---|
| `GetRepoAsync` | Fetch repo metadata. |
| `ListRepoBranchesAsync` | List branches. |
| `GetFileContentAsync` | Read a file at a ref. |
| `CreateBranchAsync` | Branch from SHA. |
| `OpenPullRequestAsync` | Open a PR/MR. |
| `GetPullRequestAsync` | Fetch one PR. |
| `ListPullRequestFilesAsync` | List file diffs. |
| `CreatePullRequestReviewCommentAsync` | File/line-anchored review comment. |
| `MergePullRequestAsync` | Merge with method (merge / squash / rebase). |
| `CreateIssueCommentAsync` | Top-level comment. |
| `RegisterWebhookAsync` | Register inbound webhook. |
| `ListAccessibleReposAsync` | Onboarding (31-9 AC7). |

### `IGitPlatformActionsClient`

5 methods covering the CI loop:

- `DispatchWorkflowAsync`
- `GetRunStatusAsync`
- `ListRunJobsAsync`
- `DownloadArtifactAsync`
- `CancelRunAsync`

## Capability matrix

Source: `PlatformKindCapabilityMatrix.DefaultsFor(PlatformKind)`.

| Capability | GitHub | Gitea | Forgejo | GitLab | Bitbucket | Azure DevOps |
|---|---|---|---|---|---|---|
| `Actions` | yes | yes | yes | yes | yes | yes |
| `Artifacts` | yes | yes | yes | yes | yes | yes |
| `Secrets` | yes | yes | yes | yes | yes | yes |
| `LibsodiumSecrets` | yes | no | no | no | no | no |
| `ProtectedVariables` | no | no | no | yes | no | no |
| `MaskedVariables` | no | no | no | yes | no | yes |
| `PrFileReview` | yes | yes | yes | yes | yes | yes |
| `WebhookHmac` | yes | yes | yes | no | yes | yes |
| `WebhookStaticToken` | no | no | no | yes | no | no |
| `PerAppInstallationAuth` | yes | no | no | no | no | no |
| `ListAccessibleRepos` | yes | yes | yes | yes | yes | yes |

Drivers MAY narrow the set at runtime based on actual config (e.g.
GitHub driver removes `PerAppInstallationAuth` when running with a
PAT). Drivers MUST NOT advertise capabilities they don't implement.

## Error contract

Every method returns `PlatformResult<T>`:

- `Ok(T value)` — call succeeded.
- `Failed(PlatformError error)` — platform returned a known error.
- `ServiceUnavailable` — driver isn't wired (no creds, dev mode).

`PlatformError` discriminated union:

- `AuthExpired` — invalidate token, retry once; if still failing,
  reauthorize.
- `PermissionDenied` — credential lacks scope. NOT retryable.
- `NotFound` — 404. NOT retryable.
- `RateLimited(TimeSpan? retryAfter)` — back off; respect `RetryAfter`.
- `ServiceUnavailable` — upstream 5xx after the driver tried.
  (Distinct from the result-level `ServiceUnavailable` which means
  the driver itself isn't configured.)
- `InvalidRequest(string code, string? hint)` — 4xx that wasn't
  auth/perm/notfound/rate-limit. `code` is driver-stable (e.g.
  `merge_conflict`, `capability_unsupported`); `hint` is human text.
- `Unknown(string reason)` — unmapped failure. Treat as non-retryable
  so genuine bugs aren't masked by backoff.

## Idempotency

`OpenPullRequestAsync` is at-least-once. Drivers SHOULD detect an
existing PR with the same `(sourceBranch, targetBranch)` and return
it; this is best-effort. Workflows that need strict idempotency
should layer their own idempotency key.

## DI registration

```csharp
// Driver project (e.g. 31-3 GitHub):
services.AddGitPlatformDriver<GitHubDriver>(PlatformKind.GitHub);

// Or via the helper for keyed-fallback to NullGitPlatformDriver:
services.AddNullGitPlatformDriver(PlatformKind.Bitbucket);
```

The Story 31-2 platform registry resolves
`IGitPlatformDriver` via
`IKeyedServiceProvider.GetKeyedService<IGitPlatformDriver>(kind)`.

## Operating modes

| Mode | Driver scope |
|---|---|
| **single-user** | One driver per `PlatformKind` for the entire process. The lone user owns all bindings. The keyed registration shape is unchanged — a single `(PlatformKind) → IGitPlatformDriver` map is enough. |
| **SaaS** | One driver per `(tenantId, PlatformKind)` pair. The 31-2 registry composes the tenant's installation record + the keyed driver type to construct a per-tenant `IGitPlatformDriver` instance. |

The interface itself is mode-agnostic. The brief's Operating Modes
rule is upheld by 31-2 (per-tenant routing) and the
`PlatformInstallation` model in this project (which encodes the
tenant binding).

## Relationship to existing GitHub code

| Existing | Becomes |
|---|---|
| `IGitHubAppClient` (in `Tamma.Api`) | Internal detail of the GitHub driver (31-3). |
| `IGitHubActionsClient` (in `Tamma.Activities`) | Adapted to `IGitPlatformActionsClient` (31-3). |
| `IGitHubSecretsProvisioner` (in `Tamma.Api`) | Internal detail behind `ICiSecretsProvisioner` (31-8). |
| `OctokitGitHubAppClient`, `OctokitGitHubActionsClient` | Stay; wrapped by the driver in 31-3. |
| `Activities/AgentDispatch/*` taking `IGitHubActionsClient` directly | Refactored in 31-3 to take the abstraction. |

31-1 does **not** modify any of those. The seam ships disconnected
so reverting is a no-op for production code.

## See also

- Epic 31 README: `docs/stories/epic-31/README.md`
- Story brief: `docs/stories/epic-31/31-1-git-platform-abstraction.md`
- Impl plan: `docs/stories/epic-31/31-1-git-platform-abstraction-impl-plan.md`
- Locked design decisions: `.dev/decisions/story-31-1-git-platform-design.md`
