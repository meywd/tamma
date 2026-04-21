# Multi Git Platform Support (Epic 31)

**Status**: planning (briefs authored 2026-04-21; impl plans not yet written). 10 core stories + 2 deferred (~228h core / ~284h with optionals).
**Layer**: Layer 4 for 31-1..31-5, 31-7..31-10; Layer 5 for 31-6 (GitLab); deferred for 31-11 (Bitbucket) / 31-12 (Azure DevOps).
**Depends on**: Epic 28 (tenant + install-routing), Epic 19 stories 19-1..19-5 (current GitHub-only agent dispatch), Epic 17 (`tenants` + `github_installations` tables).
**Source**: `docs/stories/epic-31/` (12 briefs + README).

## Why this epic exists

Today's C# port ships `IGitHubAppClient`, `IGitHubActionsClient`, `IGitHubSecretsProvisioner`, and the Epic 19 agent-dispatch activities — **all GitHub-only**. The original TS `packages/platforms/` tree scaffolded 7 platforms; we are not porting TS. Epic 31 introduces the C# abstraction + real drivers for the platforms customers are asking about.

## User constraint (2026-04-21)

> The GitHub App is currently doing two things: (1) auth/sign-in, (2) API access for agent dispatch. These need to split. Epic 31 is purely about (2) API access.

Sign-in stays GitHub OAuth short-term; per-tenant IdP is [Epic 33 (deferred)](Identity-Providers). Epic 31 is the platform-for-the-repo plane, not the platform-for-the-user plane — a tenant signed in via GitHub OAuth can own repos on Gitea / Forgejo / GitLab.

## The abstraction

```csharp
public interface IGitPlatformClient
{
    GitPlatformCapabilities Capabilities { get; }

    Task<RepoInfo> GetRepoAsync(RepoRef repo, CancellationToken ct);
    Task<PullRequest> CreatePullRequestAsync(CreatePrRequest req, CancellationToken ct);
    Task<Issue> GetIssueAsync(IssueRef iref, CancellationToken ct);
    // ... common repo/PR/issue operations
}

public interface IGitPlatformActionsClient
{
    Task<WorkflowDispatchResult> DispatchWorkflowAsync(...);
    Task<RunStatus> GetRunAsync(...);
    Task<ArtifactStream> DownloadArtifactAsync(...);
}

public interface ICiSecretsProvisioner
{
    Task PutSecretAsync(RepoRef repo, string name, string plaintextValue, CancellationToken ct);
    // plaintext in; per-driver encrypts as needed
}
```

`GitPlatformCapabilities` declares per-driver differences (e.g. GitLab's protected+masked+env-scoped variables, GitHub's libsodium sealed-box secrets). Drivers that don't support an operation throw a typed `PlatformUnsupportedException` rather than silently failing.

## Research surprises that reshaped the story set

Research: `docs/stories/research/multi-git-platform-2026.md` (2025–2026 citations).

1. **Forgejo is not a separate driver.** Forgejo 15.0 (2026-04) keeps Gitea API compat by design. Story 31-5 shrank from full driver (~30h) to **compat shim + test matrix extension (8h)**.
2. **libsodium is GitHub-only.** All other platforms (Gitea / Forgejo / GitLab / Bitbucket / Azure DevOps) accept plaintext secrets over TLS and encrypt at rest server-side. `ICiSecretsProvisioner`'s interface is plaintext-in; libsodium becomes a GitHub-driver private detail.
3. **GitLab's variable model is richer.** Protected + masked + env-scoped is **not** a port of GitHub secrets. Story 31-6 carries the richer metadata surface into the abstraction.
4. **Azure DevOps mid-migration.** PAT-based auth is deprecating; Entra-backed service connections are the future. Confirms 31-12 is right to defer.

## Story map

| # | Title | Est. hours | Layer |
|---|---|---|---|
| 31-1 | `IGitPlatformClient` + `IGitPlatformActionsClient` + capability matrix | 22 | 4 |
| 31-2 | Platform registry + per-tenant platform routing resolver | 18 | 4 |
| 31-3 | GitHub driver refactor — wrap existing Octokit clients behind new interface | 16 | 4 |
| 31-4 | Gitea driver (repos / PRs / Actions dispatch / artifacts / webhooks) | 28 | 4 |
| 31-5 | Forgejo compat shim + test matrix extension | 8 | 4 |
| 31-6 | GitLab driver (MRs / Pipelines / variables / webhooks) | 36 | 5 |
| 31-7 | Webhook receiver abstraction — per-platform signature + routing | 18 | 4 |
| 31-8 | `ICiSecretsProvisioner` abstraction across libsodium / plaintext platforms | 20 | 4 |
| 31-9 | Onboarding UI — tenant picks platform + enters credentials | 32 | 4 |
| 31-10 | Integration test harness — Gitea + Forgejo + GitLab containers | 22 | 4 |
| 31-11 | Bitbucket Cloud driver (workspaces / pipelines / variables) | 28 | deferred |
| 31-12 | Azure DevOps driver (projects / pipelines runs / variable groups) | 36 | deferred |
| **Core total (31-1..31-10)** |  | **220h** |  |
| **With optionals** |  | **284h** |  |

## Layer split rationale

| Layer | Stories | Rationale |
|-------|---------|-----------|
| Layer 4 | 31-1, 31-2, 31-3, 31-4, 31-5, 31-7, 31-8, 31-9, 31-10 | Foundation + first alternative platform (Gitea + Forgejo) + onboarding UI + test harness — ships without the GitLab CI complexity |
| Layer 5 | 31-6 (GitLab) | 36h driver with different CI dispatch model; best paired with Layer 5's cross-epic integration tests |
| Deferred | 31-11, 31-12 | Activate post-launch when paying customer or product priority justifies |

## Review findings closed

| Finding | Severity | Closes via |
|---------|----------|------------|
| `IGitHubAppClient` + `IGitHubActionsClient` hard-code GitHub surface on every agent-dispatch call site | P2 | 31-3 (driver refactor) — fans out call-site refactor |
| `GitHubEndpoints.Webhooks` hard-coded to GitHub HMAC shape | P2 | 31-7 (webhook abstraction) |
| Self-hosted-git-platform tenants unsupported (product roadmap) | — | 31-4 + 31-5 (Gitea + Forgejo); 31-6 (GitLab) for Layer 5 |
| Sign-in plane and API-access plane conflated under one GitHub App | — | 31-1 + 31-3 structurally split API-access off; sign-in stays GitHub OAuth until Epic 33 |

## Non-goals

- Does not introduce sign-in via Gitea / GitLab / Bitbucket. Users still sign into Tamma via email/password or GitHub OAuth; their tenant's repos can live on any supported platform. Per-tenant IdP is Epic 33 (deferred).
- Does not port the Epic 1.5 secret-mirror track. 1.5 is LLM-safe-ops; Epic 31 is operator-facing platform drivers. They consume each other's abstractions at clean seams.
- Does not change the agent-runtime surface. Epic 19's `IAgentExecutor` contract is platform-agnostic already — Epic 31 pushes the platform-specificity down into the client layer, not the executor.

## Risks

| Risk | Mitigation |
|------|------------|
| Interface churn after first non-GitHub driver lands | Lock 31-1 before 31-3 merges; 31-4 ships with full compliance tests that double as the abstraction contract |
| Gitea / Forgejo version drift | 31-5 test matrix runs against Gitea latest + Forgejo latest-LTS; new divergence is a new compat-shim commit |
| GitLab pipeline-model mismatch burns budget on 31-6 | 31-6 brief calls out the richer variable surface up front; capability matrix (31-1) absorbs differences without interface rewrite |
| Webhook abstraction 31-7 too generic | Per-platform signature function; dispatcher routes on first path segment, not header sniffing |
| Secrets provisioner 31-8 leaks plaintext through logs | Each driver redacts plaintext per Epic 16 § Sensitive Data Redaction; shared `RedactedSecret` type |
| Onboarding UI 31-9 blows past estimate | Two-pass ship plan: pass 1 GitHub + Gitea (both simple); pass 2 GitLab + Forgejo compat |

## Related

- [Identity Providers](Identity-Providers) — Epic 33 deferred stub; orthogonal sign-in plane
- [Security → GitHub secrets provisioning (libsodium)](Security#github-secrets-provisioning-libsodium)
- [Agent Dispatch](Agent-Dispatch) — Epic 19's `IAgentExecutor` is platform-agnostic already
- Source: [`docs/stories/epic-31/README.md`](https://github.com/meywd/tamma/tree/main/docs/stories/epic-31)
- Layer placement: [`docs/stories/plans/epic-31-33-placement.md`](https://github.com/meywd/tamma/blob/main/docs/stories/plans/epic-31-33-placement.md)
- Research: [`docs/stories/research/multi-git-platform-2026.md`](https://github.com/meywd/tamma/blob/main/docs/stories/research/multi-git-platform-2026.md)
