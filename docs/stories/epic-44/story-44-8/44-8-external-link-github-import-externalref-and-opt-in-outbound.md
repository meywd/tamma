# Story 44-8: External Link — GitHub Import, `ExternalRef`, and Opt-In Outbound Comment/Label

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then, before writing any code, check the knowledge base:

- `.dev/spikes/` — existing research
- `.dev/bugs/` — known bugs
- `.dev/findings/` — pitfalls and best practices
- `.dev/decisions/` — architecture decisions

## User Story

As a **team adopting Tamma with an existing GitHub backlog**,
I want to import open issues into a project as work items that keep a link back to their origin, and optionally to post a comment or apply a label on the origin issue,
So that adoption does not start from an empty board — without Tamma pretending to offer a two-way sync it cannot deliver across the platform matrix.

## Priority

P2 — Wave 2. Adoption ergonomics, not a correctness dependency. Nothing in Epic 44 blocks on it.

## Architectural Context (READ FIRST)

**Read this section before proposing any sync feature. The scope of what is missing is the entire justification for the epic's D3.**

- **`IGitPlatformClient` has no issue CRUD.** `apps/tamma-elsa/src/Tamma.Platforms.Abstractions/IGitPlatformClient.cs:29` declares exactly **twelve** methods: `GetRepoAsync:34`, `ListRepoBranchesAsync:40`, `GetFileContentAsync:47`, `CreateBranchAsync:53`, `OpenPullRequestAsync:59`, `GetPullRequestAsync:65`, `ListPullRequestFilesAsync:71`, `CreatePullRequestReviewCommentAsync:81`, `MergePullRequestAsync:87`, `CreateIssueCommentAsync:93`, `RegisterWebhookAsync:101`, `ListAccessibleReposAsync:119`. **One touches an issue and it is a comment write.** No get, list, create, update, close; no labels, milestones, projects or assignees.
- **The normalized `Issue` record is dead code.** `Tamma.Platforms.Abstractions/Models/Issue.cs:7-13` (`Number`, `Title`, `Body`, `State`, `HtmlUrl`, `Labels`) is never returned by any interface method and never constructed anywhere in `src/`. Its own doc concedes assignee and milestone were deferred (`:4-5`).
- **Three of the seven claimed platforms have no driver.** `PlatformKind.cs:18-26` declares **six** members (`GitHub=1, Gitea=2, Forgejo=3, GitLab=4, Bitbucket=5, AzureDevOps=6`) — **there is no plain-Git value at all**. Implementations: `GitHubPlatformClient.cs:29`, `GiteaPlatformClient.cs:23`, `GitLabPlatformClient.cs:33`, and `ForgejoPlatformDriver.cs:34` which is a 100% delegating wrapper over Gitea (`_inner` at `:36`). **Bitbucket and Azure DevOps have an enum value, a capability-matrix row (`PlatformKindCapabilityMatrix.cs:84,94`) and a webhook slug (`WebhookEndpoints.cs:338`) — and no class.** `PlatformKind.cs:12-16` says the drivers "land in 31-11 / 31-12".
- **Issue webhooks are classified and then dropped.** `DefaultWebhookEventCategoryMapper.cs:30,42,53` maps GitHub `issues`/`issue_comment`, Gitea/Forgejo `issues`/`issue_comment` and GitLab `issue`/`note` to `WebhookEventCategory.Issue` (`PlatformWebhookEvent.cs:128`), and the `opened`/`closed`/`labeled` sub-action is captured (`WebhookEndpoints.cs:364,373,381`). **There are zero production `IWebhookHandler` implementations** — every one in the repo is a test double. An issue webhook is verified, deduped, categorized, dispatched to nothing, and reports `dispatched: 0`. The legacy path enqueues `github.issues.<action>` into `queued_tasks` (`InstallationRouterService.cs:354-357,556-558`) with **no observed consumer**.
- **Issue writes exist only on the GitHub/Octokit engine-callback path, outside the abstraction.** `Tamma.Api/Services/Engine/IGitHubEngineCallbackService.cs`: `ListIssuesAsync:45`, `PostIssueCommentAsync:54`, `AddIssueLabelsAsync:58`, `RemoveIssueLabelAsync:62`, `CreateIssueAsync:66`; results `IssueListResult:76` (raw `JsonElement`s, pass-through, not persisted), `CreatedIssueResult:79`. Impl `OctokitGitHubEngineCallbackService.cs:246,257,265,271`. Also `IGitMediationService.UpdateIssueAsync:16` (impl `GitMediationService.cs:73,330`), routed at `Program.cs:3060`, whose `UpdateIssueRequest` (`GitRequests.cs:66-72`) carries `Body`, `AddLabels`, `RemoveLabels` and a `Status` field commented **"today: no state change"**.
- **The existing write-back precedent:** `Tamma.Activities/ADL/ApplyTriageResultActivity.cs:229-245` sets labels, posts a comment, or creates an issue via `ITriageApplyClient` (`:285`), failing loud via `EnsureSuccess` (`:257`) so a swallowed 4xx never reports success.
- **No local issue mirror exists anywhere.** No `issues` table in either DbContext; the only webhook persistence is delivery-id dedupe (`PlatformWebhookDelivery`, `GitHubWebhookDelivery`).
- **Credential resolution is real and per-tenant:** `tenant_platform_installations` (`Tamma.Data/Entities/TenantPlatformInstallation.cs:35`) → `IPlatformCredentialReader.ReadActivePlaintextAsync` (`:46`) → `SecretStorePlatformCredentialReader.cs:35,60`. Note `GitTokenResolver.cs:28` hardcodes `private const string GitHubPlatformKind = "github"`.
- **`ExternalRefJson` is a `jsonb` column on `work_items`**, created by 44-1 with a partial index on `(repoFullName, number)`, deliberately left shapeless for this story (44-1 D7).

## Acceptance Criteria

1. **`ExternalRef` is a typed value in `Tamma.Core.Tracking`** — `(PlatformKind Platform, string RepoFullName, string Number, string Url, DateTimeOffset LinkedAt)` — serialized into `work_items.ExternalRefJson`. Nullable; native items carry none.

2. **Import** `POST /api/projects/{projectId:guid}/import` accepting `{ repoFullName, state?, labels? }`, GitHub-only in v1, reading through `IGitHubEngineCallbackService.ListIssuesAsync`. Creates one work item per issue with `Kind` inferred from labels (via `TriageIssueType`'s vocabulary), `Priority` from labels (via `TriageVocabulary.TryParsePriority`, so `critical`/`medium` fold), `Status = backlog`, and an `ExternalRef`.

3. **Import is a snapshot, explicitly, and is re-runnable.** Re-running skips issues already linked in the project (matched on `(repoFullName, number)` via the partial index) and reports them as `skipped-already-linked`. It **never updates** an existing work item from the external issue — that would be a sync, and there is none.

4. **Import is bulk-efficient.** It uses `CreateManyAsync` (44-1's block-allocating key minter), not a loop over `CreateAsync`, and paginates the source. A test imports 500 issues and asserts contiguous keys and a bounded statement count.

5. **A non-GitHub platform is rejected with a specific, honest error**, not a generic failure: `PLATFORM_IMPORT_UNSUPPORTED` naming the platform and stating that only GitHub import ships in v1. A test drives a Gitea installation and asserts the code.

6. **Manual link and unlink.** `POST /api/work-items/{id:guid}/link` (`{ repoFullName, number }`) resolves and stores an `ExternalRef`; `DELETE .../link` clears it. Linking an issue already linked to another work item in the same tenant is rejected `409`.

7. **Outbound is opt-in per project, GitHub-only, and limited to what already ships.** A `projects.OutboundMode` field (`off | comment | comment-and-label`, default `off`). When enabled:
   - a status change to a terminal status posts a comment on the linked issue via `PostIssueCommentAsync:54`;
   - `comment-and-label` additionally syncs a single configured label via `AddIssueLabelsAsync:58` / `RemoveIssueLabelAsync:62`.
   **No title, body, state, assignee or milestone write-back.** A test asserts no other platform method is called.

8. **Outbound failures never fail the tracker mutation.** A platform 4xx/5xx is caught, emits `WORKITEM.OUTBOUND.FAILED` with the status code (**never a response body — the `ApplyTriageResultActivity.cs:257-263` secret-free rule**), and the status change stands. The tracker is the source of truth; the platform is a courtesy.

9. **Divergence is visible, not reconciled.** The work-item detail response includes `externalRef` plus a `lastKnownExternalState` captured at import/link time. **Nothing refreshes it.** The UI (44-6) shows the link and the capture timestamp; there is no reconciliation loop, no webhook consumption, and no conflict model. A test asserts no background job or webhook handler is registered by this story.

10. **Catalog descriptors** for import, link, unlink and outbound, with `DefaultMinAutonomy` reflecting that these reach an external system.

## Technical Notes

- Import maps `IssueListResult`'s raw `JsonElement`s (`IGitHubEngineCallbackService.cs:76`) rather than the dead `Issue` record — using a type nothing constructs would be adopting dead code, and its `string Number` / missing assignee would need mapping anyway.
- The `state` filter default is `open`. Importing closed issues by default would fill a new board with historical noise.
- Outbound uses the engine-callback service, the same seam `ApplyTriageResultActivity` uses, not `IGitPlatformClient` — the abstraction has no label methods at all.
- `GitTokenResolver.cs:28`'s hardcoded `"github"` is one of the reasons AC5 is a clean rejection rather than a partial multi-platform attempt.

## Dependencies

- **Stories 44-0, 44-1** (`ExternalRefJson`, `CreateManyAsync`), **44-2** (endpoints, RBAC), **44-5** (`WORKITEM.LINKED.SUCCESS`, `WORKITEM.OUTBOUND.FAILED`) — blocking.
- **Existing, no change required:** `IGitHubEngineCallbackService`, `tenant_platform_installations`, `IPlatformCredentialReader`.
- **Blocks:** nothing.

## Out of Scope — and this is the epic's largest deliberate exclusion

- **Two-way sync, on any platform.** It would require: six issue methods added to `IGitPlatformClient` × four existing drivers; two drivers that do not exist (Bitbucket, Azure DevOps); the `IWebhookHandler` layer that has never been built; a conflict-resolution model; and a reconciliation sweep. Larger than this entire epic. Epic README, Decisions D3.
- **Inbound webhook consumption.** Issue webhooks are classified today and dispatched to nothing; building the first `IWebhookHandler` in the repo to feed a tracker is a platform story, not a tracker story.
- **Import from Gitea, GitLab, Forgejo, Bitbucket, Azure DevOps or plain Git.** The first three would need issue-read methods on the abstraction; the last three have no driver, and plain Git is not even a `PlatformKind`.
- **Jira import.** `IJiraMediationService.cs:14-15` offers get + update ticket only, per-tenant BYOK. Real, but a third external surface before the first is proven.
- **Creating an external issue from a work item.** `CreateIssueAsync:66` exists, but pushing native items outward makes the external tracker a second source of truth, which is the thing D3 exists to prevent.
- **Refreshing `lastKnownExternalState`.** Explicitly never, in v1. A stale field the user can see is honest; a half-refreshed one is a sync with no conflict model.

## Estimated Effort

4 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-25 | 1.0.0   | Initial story creation | Claude |
