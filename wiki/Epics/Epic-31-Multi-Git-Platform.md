# Epic 31: Multi Git Platform Support

**Status:** Planning (briefs authored 2026-04-21; impl plans not yet written)
**Stories:** 10 core (31-1..31-10) + 2 deferred (31-11 Bitbucket, 31-12 Azure DevOps)
**Effort:** ~220h core / ~284h with optionals
**Layer:** Layer 4 for 31-1..31-5 + 31-7..31-10; Layer 5 for 31-6 (GitLab); deferred for 31-11/31-12
**Depends on:** Epic 28 (tenant + install-routing), Epic 19 stories 19-1..19-5 (current GitHub-only agent dispatch), Epic 17 (`tenants` + `github_installations` tables)

> **Overview**: [Multi Git Platform](Multi-Git-Platform) — root-level topic page with the `IGitPlatformClient` abstraction, per-platform driver semantics, and webhook routing details.

## Purpose

Today's C# port ships `IGitHubAppClient`, `IGitHubActionsClient`, `IGitHubSecretsProvisioner`, and the Epic 19 agent-dispatch activities — **all GitHub-only**. The original TS `packages/platforms/` tree scaffolded 7 platforms; we are not porting TS. Epic 31 introduces the C# abstraction + real drivers for the platforms customers are asking about.

User constraint (2026-04-21):

> The GitHub App is currently doing two things: (1) auth/sign-in, (2) API access for agent dispatch. These need to split. Epic 31 is purely about (2) API access.

Sign-in stays GitHub OAuth short-term; per-tenant IdP is [Epic 33](Epic-33-Per-Tenant-IdP.md) (deferred). Epic 31 is the platform-for-the-repo plane, not the platform-for-the-user plane — a tenant signed in via GitHub OAuth can own repos on Gitea / Forgejo / GitLab.

## Current state

- All git-platform code is GitHub-only via Octokit
- `OctokitGitHubAppClient`, `OctokitGitHubActionsClient`, `LibsodiumGitHubSecretsProvisioner` already shipped (auth-foundation sprint)
- `NullGitHubAppClient` / `NullGitHubActionsClient` seams in place — fail-fast when GitHub App not configured
- `GitHubEndpoints.Webhooks` is hard-coded to the GitHub HMAC shape (closed by 31-7)

## Stories

| # | Title | Effort | Depends on | Blocks | Status |
|---|-------|--------|------------|--------|--------|
| 31-1 | `IGitPlatformClient` + `IGitPlatformActionsClient` + capability matrix | 22h | — | 31-2..31-12 | Planned |
| 31-2 | Platform registry + per-tenant platform routing resolver | 18h | 31-1, 28-9 | 31-3..31-9 | Planned |
| 31-3 | GitHub driver refactor — wrap existing Octokit clients | 16h | 31-1, 31-2 | 31-7, 31-8, 31-9 | Planned |
| 31-4 | Gitea driver (repos / PRs / Actions dispatch / artifacts / webhooks) | 28h | 31-1, 31-2 | 31-5, 31-7, 31-8, 31-9 | Planned |
| 31-5 | Forgejo compat shim + test matrix extension | 8h | 31-4 | 31-9 | Planned |
| 31-6 | GitLab driver (MRs / Pipelines / variables / webhooks) | 36h | 31-1, 31-2 | 31-7, 31-8, 31-9 | Planned |
| 31-7 | Webhook receiver abstraction — per-platform signature + routing | 18h | 31-1, 31-3, 31-4, 31-6 | 31-9 | Planned |
| 31-8 | `ICiSecretsProvisioner` abstraction across libsodium / plaintext platforms | 20h | 31-1, 31-3, 31-4, 31-6 | 31-9 | Planned |
| 31-9 | Onboarding UI — tenant picks platform + enters credentials | 32h | 31-2, 31-3, 31-4, 31-6, 29-5 | — | Planned |
| 31-10 | Integration test harness — Gitea + Forgejo + GitLab containers | 22h | 31-3, 31-4, 31-5, 31-6 | — | Planned |
| 31-11 (optional) | Bitbucket Cloud driver | 28h | 31-1, 31-2 | — | Deferred |
| 31-12 (optional) | Azure DevOps driver | 36h | 31-1, 31-2 | — | Deferred |

**Core total** (31-1..31-10): 220h. **With optionals**: 284h.

## Architecture / key decisions

1. **Forgejo is not a separate driver**. Forgejo 15.0 (2026-04) keeps Gitea API compat by design. Story 31-5 shrank from full driver (~30h) to compat shim (8h) — see `docs/stories/research/multi-git-platform-2026.md §2`.
2. **libsodium is GitHub-only**. All other platforms accept plaintext secrets over TLS; Gitea/Forgejo/GitLab/Bitbucket/Azure DevOps encrypt at rest server-side. 31-8's interface is plaintext-in, and libsodium becomes a GitHub-driver private detail.
3. **GitLab's variable model is richer**. Protected + masked + env-scoped isn't a port of GitHub secrets. 31-6 carries the richer metadata surface into the abstraction (capability matrix from 31-1 absorbs the shape differences).
4. **Azure DevOps mid-migration**. PAT-based auth is deprecating; Entra-backed service connections are the future. Confirms 31-12 is right to defer.
5. **Sign-in plane is orthogonal**. Tenants sign in via Tamma's built-in auth (email/password + GitHub OAuth) regardless of where their repos live. Per-tenant SAML/OIDC is Epic 33 (deferred).
6. **Webhook abstraction is per-path-segment dispatch**, not header sniffing. `/webhooks/github/...` vs `/webhooks/gitea/...`; signature function bound to the platform driver.

## Layer split

| Layer | Stories | Rationale |
|-------|---------|-----------|
| Layer 4 | 31-1, 31-2, 31-3, 31-4, 31-5, 31-7, 31-8, 31-9, 31-10 | Refactor + Gitea/Forgejo + onboarding UI + test harness — ships the foundation plus the first alternative platform without pulling in full GitLab CI complexity |
| Layer 5 | 31-6 (GitLab) | Heavy driver, different CI model; best paired with Layer 5's cross-epic integration tests |
| Layer 5 (optional) | 31-11, 31-12 | Deferred until paying customer or product ask |

## Dependencies

**Upstream**:
- [Epic 28](Epic-28-DB-Per-Tenant.md) — tenant + install-routing model (28-9)
- [Epic 19](Epic-19-Agent-Dispatch.md) — current GitHub-only agent dispatch (19-1..19-5)
- [Epic 17](Epic-17-Multi-Tenancy.md) — `tenants` + `github_installations` tables
- [Epic 29](Epic-29-Secret-Management.md) Story 29-5 — tenant-admin UI for credentials entry

**Downstream / related**:
- Epic 1.5-23..1.5-26 (LLM-safe secret mirroring to CI variable stores) — different theme, overlapping surface; this epic consumes its `IRotationHandler` contract where it overlaps
- [Epic 33](Epic-33-Per-Tenant-IdP.md) — separate concern from platform API access; sign-in plane is orthogonal

## Review findings closed

- **"Self-hosted Git platforms" item from the 2026 roadmap** — closed by 31-4 + 31-5 + 31-6
- **Split GitHub App sign-in vs API** (user constraint, 2026-04-21) — closed structurally by 31-1 + 31-3 (API-access is now platform-agnostic; sign-in stays on GitHub OAuth until Epic 33)
- **Webhook-endpoint security gap** (the current `GitHubEndpoints.Webhooks` is hard-coded to GitHub HMAC) — closed by 31-7

## Non-goals

- Does not introduce sign-in via Gitea / GitLab / Bitbucket. Users still sign into Tamma via email/password or GitHub OAuth; their tenant's repos can live on any supported platform. Per-tenant IdP is Epic 33.
- Does not port the existing Epic 1.5 secret-mirror track. 1.5 is LLM-safe-ops; Epic 31 is operator-facing platform drivers. The two consume each other's abstractions at clean seams.
- Does not change the agent-runtime surface. Epic 19's `IAgentExecutor` contract is platform-agnostic already — 31 pushes the platform-specificity down into the client layer, not the executor.

## Risks

| Risk | Mitigation |
|------|------------|
| Interface churn after first non-GitHub driver lands | Lock 31-1 before 31-3 merges; 31-4 ships with full compliance tests that double as the abstraction contract |
| Gitea / Forgejo version drift | 31-5 test-matrix runs against Gitea latest + Forgejo latest-LTS; new divergence = new compat-shim commit |
| GitLab pipeline-model mismatch burns budget on 31-6 | 31-6 brief calls out the richer variable surface up front; capability matrix (31-1) absorbs shape differences without forcing an interface rewrite |
| Webhook abstraction 31-7 too generic | Keep the per-platform signature function; dispatcher routes on first path segment, not on header sniffing |
| Secrets provisioner 31-8 leaks plaintext through logs | Each driver must redact the plaintext value in logs per Epic 16 §Sensitive Data Redaction. Add a shared `RedactedSecret` type used in the interface |
| Onboarding UI 31-9 blows past estimate | Credential entry patterns diverge per platform. Ship in two passes: pass 1 GitHub + Gitea (already simple); pass 2 GitLab + Forgejo compat |

## Open questions

1. **Forgejo divergence**: if Forgejo 16.0 breaks Gitea API compat, do we promote 31-5 to a full driver? Decision deferred to first real divergence.
2. **GitHub Enterprise Server vs GitHub.com**: GHES customers use the same Octokit client with a different base URL. V1 covers GHES via config; if GHES feature drift becomes a problem, fork the driver.
3. **Multi-platform per tenant**: can a tenant connect repos on multiple platforms simultaneously (e.g. some on GitHub, some on Gitea)? V1 = one platform per tenant; revisit when a customer asks.

## Sources

- Research notes: `docs/stories/research/multi-git-platform-2026.md`
- Current GitHub code: `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/`, `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/`
- Webhook endpoint: `apps/tamma-elsa/src/Tamma.Api/Endpoints/GitHubEndpoints.cs`
- User constraint: 2026-04-21 planning session ("split sign-in from API access")

## Story files

[Epic 31 stories on GitHub](https://github.com/meywd/tamma/tree/main/docs/stories/epic-31)

---

_Last updated: 2026-04-21_
