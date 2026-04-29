# Story 31-1 — Git Platform Abstraction Design Decisions

**Status**: Locked (2026-04-27)
**Branch**: `story-31-1-git-platform-abstraction`
**Author**: implementation pass on the impl-plan.

This ADR captures the five design decisions that downstream Epic 31
stories (31-2 through 31-12) will inherit. Once shipped, changing
these requires re-coordinating with every driver author.

---

## 1. Platform identity: enum, not string-keyed registry

**Decision**: `enum PlatformKind { GitHub, Gitea, Forgejo, GitLab,
Bitbucket, AzureDevOps }` is the registry key. Drivers register via
`AddKeyedSingleton<IGitPlatformDriver, X>(PlatformKind.X)`. The
keyed-DI helper takes `PlatformKind` directly, not `string`.

**Rationale**:

- Prevents typos (`"gitub"` vs `"GitHub"`).
- Lets `switch` expressions over `PlatformKind` get exhaustiveness
  checking.
- Bitbucket / Azure DevOps are encoded in the enum from day 1 even
  though drivers are deferred to 31-11/31-12 — onboarding picker
  (31-9) renders them as "coming soon" via the same matrix lookup.
- The enum closes the platform set: a third-party plugin author
  CANNOT add a new platform without modifying the abstractions
  project. We are explicitly OK with this; the trade-off (review
  every new platform's matrix entry centrally) outweighs the cost.

**Rejected alternative**: string-keyed registry with `Constants.GitHub
= "github"`. More extensible but loses exhaustiveness and invites
typo bugs the type system can't catch.

---

## 2. Capability discovery: per-flag enum, not feature-area records

**Decision**: `enum PlatformCapability` with 11 flags grouped
informally (CI, secrets, source-host) but stored as a single
`IReadOnlySet<PlatformCapability>`.

**Rationale**:

- Onboarding picker filters with one `Contains` call; richer types
  would force the UI to walk a tree.
- Adding a new flag is an enum append + one matrix-row update —
  doesn't ripple into driver interfaces.
- Set semantics make subset checks trivial (driver capabilities MUST
  be a subset of `PlatformKindCapabilityMatrix.DefaultsFor(kind)` —
  enforced by `GitPlatformClientContractTests`).

**Rejected alternative**: nested records like
`PullRequestCapabilities { Draft, RequiredReviews, ... }`. Cleaner in
isolation but explodes the abstraction surface (a driver would have
to populate 5 sub-records); also no clear cut between which flags
belong where (is `WebhookHmac` a "webhook" capability or an "auth"
capability?).

---

## 3. Auth model: per-installation driver instance, capability-flagged

**Decision**: A driver instance is bound to a specific
`PlatformInstallation` (record carrying tenant id, platform kind,
base URL, external installation id). The 31-2 registry composes a
keyed driver type with the per-tenant installation record at
resolution time. Per-app-installation auth is opt-in via the
`PerAppInstallationAuth` capability.

**Rationale**:

- Operating-modes rule (CLAUDE.md): single-user mode has one
  installation per `PlatformKind` for the lone user; SaaS mode has
  per-tenant installations. Same `PlatformInstallation` shape covers
  both — only the `TenantId` differs.
- Drivers don't need to know whether they're running in single-user
  or SaaS mode — they just consume their installation record. Mode
  awareness lives in 31-2 routing.
- A driver MAY narrow the capability set at runtime (e.g. GitHub
  driver running with a personal access token drops
  `PerAppInstallationAuth`). The capability set is the *effective*
  set, not the *kind*'s defaults — `Capabilities` MUST be a subset
  of the matrix defaults.

**Rejected alternative**: have drivers read credentials from a secret
store at call time. Forces every driver to take a tenant id
parameter on every method, polluting the interface. Also re-fetches
secrets on every call rather than caching the bound credential —
worse for rate-limit budgets.

---

## 4. Webhook abstraction: registration only in 31-1; receiver in 31-7

**Decision**: 31-1 ships `RegisterWebhookAsync` on
`IGitPlatformClient` plus `WebhookRegistration` /
`RegisterWebhookRequest` shapes. 31-7 ships the receiver
(signature verification, replay, dispatch into Tamma's event bus).
No `IGitPlatformWebhookReceiver` interface lands here.

**Rationale**:

- 31-7's receiver is HTTP-tier code (a `MapPost` handler) not a
  driver method — it parses an incoming HTTP request, verifies a
  signature, then routes by event type. Putting it in the driver
  interface would couple the abstraction to ASP.NET.
- The capability flags `WebhookHmac` and `WebhookStaticToken` are
  what the receiver branches on — sufficient for 31-7 without
  another interface here.

**Rejected alternative**: `IGitPlatformWebhookReceiver` on the
driver. Adds receiver logic into driver projects (3-4 of them),
duplicating signature-verification code instead of centralizing it.

---

## 5. Idempotency: best-effort detect-and-return-existing for OpenPR

**Decision**: `OpenPullRequestAsync` is at-least-once. Drivers
SHOULD detect an existing PR with the same `(sourceBranch,
targetBranch)` pair on the same repo and return it; this is
best-effort, not enforced. Workflows that need strict idempotency
must layer their own idempotency key (typically the
issue id from the orchestrator).

**Rationale**:

- GitHub's Octokit client is at-least-once (POST /pulls returns 422
  with "A pull request already exists" if you re-submit).
  Driver-level idempotency means catching that 422 and
  re-fetching — fine for the happy case but races with concurrent
  manual edits.
- Real idempotency lives in Tamma's workflow layer (the
  orchestrator already has issue-id-keyed dedup). Pushing it down
  into every driver would duplicate that logic.
- Treating `InvalidRequest("pr_already_exists", ...)` as
  retryable-with-fetch is a per-driver implementation detail — the
  abstraction doesn't mandate it.

**Rejected alternative**: enforce idempotency in the abstraction
(reject the call if no idempotency key is provided). Heavier API
shape; doesn't match how GitHub / GitLab actually behave.

---

## Ancillary locked decisions

- **Multitarget**: shipped `net8.0` only to match the existing
  codebase (Directory.Build.props pins net8.0; the impl-plan
  suggested `netstandard2.1` multitarget but no consumer outside the
  net8.0 host needs it today). If a CLI plugin or Rider
  integration emerges, revisit.
- **Test framework**: NUnit, not xUnit (the impl-plan specified
  xUnit but the rest of the C# port standardizes on NUnit + Moq +
  FluentAssertions — using xUnit would split the test infrastructure).
- **`PullRequestState`**: `Open / Closed / Merged` as enum + a
  separate `bool IsDraft` property on `PullRequest`. GitHub treats
  draft as a flag on Open; GitLab treats it as a title prefix (WIP).
  Merging the two into one enum would lose information.
- **`PlatformResult.ServiceUnavailable`**: distinct *result* variant
  (not a `Failed(ServiceUnavailable)` wrapping). Mirrors today's
  `GitHubAppResult<T>.NotConfigured()` shape so callers can no-op
  cheaply on the dev-mode null seam without unwrapping.
- **`RawMetadata`**: `JsonDocument?` on `WorkflowRun` /
  `WorkflowJob` from day 1 so 31-6 (GitLab) doesn't have to
  retrofit a way to surface platform-specific fields.

---

## Constraints inherited from existing code

- Existing `IGitHubAppClient`, `IGitHubActionsClient`,
  `IGitHubSecretsProvisioner` and their Octokit implementations
  STAY UNCHANGED in 31-1. 31-3 wraps them inside the new
  `GitHubDriver`. 31-1 does not modify a single line of existing
  GitHub integration.
- The existing `OctokitGitHubActionsClient` enforces a 4 MB
  artifact-download cap (review-session 2026-04-20 finding 6). The
  new `IGitPlatformActionsClient.DownloadArtifactAsync` returns
  `Stream` rather than `byte[]` — this is a design upgrade, not a
  regression. The 31-3 wrapper will preserve the 4 MB limit by
  capping the stream before returning it.
- `Tamma.Activities/AgentDispatch/*` activities take
  `IGitHubActionsClient` directly. 31-3 will refactor them to take
  `IGitPlatformActionsClient` instead. 31-1 leaves these untouched.

## See also

- Story brief: `docs/stories/epic-31/31-1-git-platform-abstraction.md`
- Impl plan: `docs/stories/epic-31/31-1-git-platform-abstraction-impl-plan.md`
- Package README: `apps/tamma-elsa/src/Tamma.Platforms.Abstractions/README.md`
- Operating Modes rule: `CLAUDE.md` § "Operating Modes"
