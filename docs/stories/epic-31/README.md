# Epic 31: Multi Git Platform Support

**Status**: planning (briefs only, 2026-04-21)
**Layer**: Layer 4 for 31-1..31-4 + 31-7..31-9 (GitHub refactor +
Gitea/Forgejo); Layer 5 for 31-6 (GitLab) and optional 31-11
(Bitbucket) / 31-12 (Azure DevOps) — see
[`plans/epic-31-33-placement.md`](../plans/epic-31-33-placement.md).
**Depends on**: Epic 28 (tenant model + install-routing), Epic 19
Stories 19-1..19-5 (current GitHub-only agent dispatch), Epic 17
(`tenants` + `github_installations` tables).
**Related**: Epic 1.5-23..1.5-26 (LLM-safe secret mirroring to CI
variable stores — different theme, overlapping surface), Epic 33
(deferred per-tenant IdP — separate concern from platform API access).

## Why this epic exists

Today's C# port ships `IGitHubAppClient`,
`IGitHubActionsClient`, `IGitHubSecretsProvisioner`, and the Epic 19
agent-dispatch activities — **all GitHub-only**. The original TS
`packages/platforms/` tree scaffolded 7 platforms; we are not porting
TS. Epic 31 introduces the C# abstraction + real drivers for the
platforms customers are asking about.

User constraint (2026-04-21):

> The GitHub App is currently doing two things: (1) auth/sign-in,
> (2) API access for agent dispatch. These need to split. Epic 31 is
> purely about (2) API access.

Sign-in stays GitHub OAuth short-term; per-tenant IdP is Epic 33
(deferred). Epic 31 is the platform-for-the-repo plane, not the
platform-for-the-user plane — a tenant signed in via GitHub OAuth can
own repos on Gitea / Forgejo / GitLab.

## Scope

- **In-scope**: `IGitPlatformClient` + `IGitPlatformActionsClient`
  abstraction + per-tenant platform registry, refactor existing
  GitHub clients behind the new interface, add Gitea/Forgejo/GitLab
  drivers, per-platform webhook signature verification, per-platform
  CI secrets provisioner, onboarding UI for credential entry,
  integration test harness with Gitea + Forgejo + GitLab containers.
- **Out-of-scope**: sign-in plane (Epic 33 covers it), LLM-safe
  secret mirroring to CI stores (Epic 1.5-23..1.5-26 owns that theme
  — this epic consumes its `IRotationHandler` contract where it
  overlaps), per-tenant infra provisioning (Epic 30 owns the compute
  plane), AI providers (orthogonal).

## Story map

| # | Title | Est. hours | Depends on | Blocks |
|---|---|---|---|---|
| [31-1](./31-1-git-platform-abstraction.md) | `IGitPlatformClient` + `IGitPlatformActionsClient` + capability matrix | 22 | — | 31-2 .. 31-12 |
| [31-2](./31-2-platform-registry-routing.md) | Platform registry + per-tenant platform routing resolver | 18 | 31-1, 28-9 | 31-3 .. 31-9 |
| [31-3](./31-3-github-driver-refactor.md) | GitHub driver refactor — wrap existing Octokit clients behind new interface | 16 | 31-1, 31-2 | 31-7, 31-8, 31-9 |
| [31-4](./31-4-gitea-driver.md) | Gitea driver (repos / PRs / Actions dispatch / artifacts / webhooks) | 28 | 31-1, 31-2 | 31-5, 31-7, 31-8, 31-9 |
| [31-5](./31-5-forgejo-compat-matrix.md) | Forgejo compat shim + test matrix extension | 8 | 31-4 | 31-9 |
| [31-6](./31-6-gitlab-driver.md) | GitLab driver (MRs / Pipelines / variables / webhooks) | 36 | 31-1, 31-2 | 31-7, 31-8, 31-9 |
| [31-7](./31-7-webhook-receiver-abstraction.md) | Webhook receiver abstraction — per-platform signature + routing | 18 | 31-1, 31-3, 31-4, 31-6 | 31-9 |
| [31-8](./31-8-ci-secrets-provisioner-abstraction.md) | `ICiSecretsProvisioner` abstraction across GitHub libsodium / plaintext platforms | 20 | 31-1, 31-3, 31-4, 31-6 | 31-9 |
| [31-9](./31-9-onboarding-platform-picker-ui.md) | Onboarding UI — tenant picks platform + enters credentials | 32 | 31-2, 31-3, 31-4, 31-6, 29-5 | — |
| [31-10](./31-10-integration-test-harness.md) | Integration test harness — Gitea + Forgejo + GitLab containers | 22 | 31-3, 31-4, 31-5, 31-6 | — |
| [31-11](./31-11-bitbucket-driver.md) (optional) | Bitbucket Cloud driver (workspaces / pipelines / variables) | 28 | 31-1, 31-2 | — |
| [31-12](./31-12-azure-devops-driver.md) (optional) | Azure DevOps driver (projects / pipelines runs / variable groups) | 36 | 31-1, 31-2 | — |
| **Core total (31-1..31-10)** | | **220** | | |
| **With optionals** | | **284** | | |

## Research surprises that reshaped the story set

1. **Forgejo is not a separate driver.** Forgejo 15.0 (2026-04) keeps
   Gitea API compat by design. Story 31-5 shrank from full driver
   (~30h) to compat shim (8h) — see
   [`research/multi-git-platform-2026.md §2`](../research/multi-git-platform-2026.md).
2. **libsodium is GitHub-only.** All other platforms accept plaintext
   secrets over TLS; Gitea/Forgejo/GitLab/Bitbucket/Azure DevOps
   encrypt at rest server-side. 31-8's interface is plaintext-in, and
   libsodium becomes a GitHub-driver private detail.
3. **GitLab's variable model is richer.** Protected + masked + env-
   scoped isn't a port of GitHub secrets. 31-6 carries the richer
   metadata surface into the abstraction.
4. **Azure DevOps mid-migration.** PAT-based auth is deprecating;
   Entra-backed service connections are the future. Confirms 31-12
   is right to defer.

## Split of layers

| Layer | Stories | Rationale |
|---|---|---|
| Layer 4 | 31-1, 31-2, 31-3, 31-4, 31-5, 31-7, 31-8, 31-9, 31-10 | Refactor + Gitea/Forgejo + onboarding UI + test harness — ships the foundation plus the first alternative platform without pulling in the full GitLab CI complexity |
| Layer 5 | 31-6 (GitLab) | Heavy driver, different CI model; best paired with Layer 5's cross-epic integration tests |
| Layer 5 (optional) | 31-11, 31-12 | Deferred until paying customer or product ask |

## Review findings this epic closes

- **"Self-hosted Git platforms" item from the 2026 roadmap** (not a
  numbered code-review finding) — closed by 31-4 + 31-5 + 31-6.
- **Split GitHub App sign-in vs API** (user constraint, 2026-04-21)
  — closed structurally by 31-1 + 31-3 (API-access is now
  platform-agnostic; sign-in stays on GitHub OAuth until Epic 33).
- **Webhook-endpoint security gap** (the current `GitHubEndpoints.Webhooks`
  is hard-coded to the GitHub HMAC shape) — closed by 31-7.

## Non-goals

- Does not introduce sign-in via Gitea / GitLab / Bitbucket. Users
  still sign into Tamma via email/password or GitHub OAuth; their
  tenant's repos can live on any supported platform. Per-tenant IdP
  is Epic 33.
- Does not port the existing Epic 1.5 secret-mirror track. 1.5 is
  LLM-safe-ops; Epic 31 is operator-facing platform drivers. The
  two consume each other's abstractions at clean seams.
- Does not change the agent-runtime surface. Epic 19's
  `IAgentExecutor` contract is platform-agnostic already — 31
  pushes the platform-specificity down into the client layer, not
  the executor.

## Risks

| Risk | Mitigation |
|---|---|
| Interface churn after first non-GitHub driver lands | Lock 31-1 before 31-3 merges; 31-4 ships with full compliance tests that double as the abstraction contract |
| Gitea / Forgejo version drift | 31-5 test-matrix runs against Gitea latest + Forgejo latest-LTS; any new divergence is a new compat-shim commit |
| GitLab pipeline-model mismatch burns budget on 31-6 | 31-6 brief calls out the richer variable surface up front; capability matrix (31-1) absorbs the shape differences without forcing an interface rewrite |
| Webhook abstraction 31-7 too generic | Keep the Fastify-style per-platform signature function; dispatcher routes on first path segment, not on header sniffing |
| Secrets provisioner 31-8 leaks plaintext through logs | Each driver must redact the plaintext value in logs per Epic 16 §Sensitive Data Redaction. Add a shared `RedactedSecret` type used in the interface |
| Onboarding UI 31-9 blows past estimate | Credential entry patterns diverge per platform. Ship in two passes: pass 1 GitHub + Gitea (already simple); pass 2 GitLab + Forgejo compat |

## Sources

- Research notes: [`../research/multi-git-platform-2026.md`](../research/multi-git-platform-2026.md)
- Current GitHub code: `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/`,
  `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/`
- Webhook endpoint: `apps/tamma-elsa/src/Tamma.Api/Endpoints/GitHubEndpoints.cs`
- User constraint: 2026-04-21 planning session ("split sign-in from
  API access")
