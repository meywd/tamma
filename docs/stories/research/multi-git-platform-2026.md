# Research Notes — Multi Git Platform Support (2025-2026)

**Author**: planning sweep, 2026-04-21
**Purpose**: ground Epic 31 (Multi Git Platform Support) in current
vendor API state rather than training memory. Each section: findings +
impact on the Epic 31 story set.

## Context — what we're deciding

Today's C# code ships `IGitHubAppClient`, `IGitHubActionsClient`,
`IGitHubSecretsProvisioner`, and webhook/agent-dispatch activities
that are GitHub-only. The original TS `packages/platforms/` tree
shipped stubs for 7 platforms but we are not porting TS. Epic 31 adds
a C# abstraction + real drivers. The user's constraint: GitHub App
currently does **sign-in** + **API access**; Epic 31 is purely about
the API-access half. Sign-in stays GitHub OAuth + future Epic 33.

## 1. Gitea Actions (1.24-1.25) — API-access state

**Compatibility posture**: Gitea Actions is intentionally
GitHub-Actions-compatible. Workflow YAML is identical, most
`actions/*` marketplace actions run as-is because Gitea maps them to
its own mirror (`gitea.com/actions/…`).
([Gitea Actions Comparison](https://docs.gitea.com/usage/actions/comparison),
[DeepWiki Gitea Actions](https://deepwiki.com/go-gitea/gitea/6-gitea-actions-system))

**Workflow dispatch**: supported via `POST
/repos/{owner}/{repo}/actions/workflows/{workflowname}/dispatches`
with `{ ref, inputs }`. Maps 1:1 to GitHub's endpoint.
([Gitea FAQ](https://docs.gitea.com/usage/actions/faq))

**Run monitoring**: `GET
/repos/{owner}/{repo}/actions/runs/{run_id}` returns state; Gitea
exposes runs list + jobs list endpoints. Same shape as GitHub
Actions REST v3. Status values: `queued`, `in_progress`, `completed`.

**Artifacts**: supports **both** v1-v3 (multi-file) and v4 (single
compressed archive) protocols. Runner POSTs to
`/api/actions_pipeline/_apis/pipelines/workflows/:run/artifacts`;
download over web UI or API.
([Gitea artifacts PR #22345](https://github.com/go-gitea/gitea/pull/22345))

**Secrets**: 4 scope levels — global, user, org, repo — with precedence
lowest wins. API endpoints exist to list / create / update / delete
secrets at each scope. `POST /repos/{owner}/{repo}/actions/secrets/{name}`
accepts `{ "data": "<plaintext>" }`; **no libsodium encryption
required** (that's a GitHub-specific wire-format detail).
([Gitea Secrets](https://docs.gitea.com/usage/actions/secrets))

**Auth for API access**: two paths — (1) OAuth2 application (user
consents, third-party app gets bearer token), (2) personal-access
token or bot-account PAT. Gitea as an OAuth2 provider is
documented and widely used; token format is `token <access>` in
Authorization header.
([Gitea OAuth2 Provider](https://docs.gitea.com/development/oauth2-provider))

**Webhooks**: payload signed with HMAC-SHA256 using the webhook
secret. Header: `X-Gitea-Signature` (hex-encoded). Event header:
`X-Gitea-Event`.
([Gitea/Forgejo webhook verification discussion](https://github.com/tektoncd/pipelines-as-code/issues/2422))

**Impact on Epic 31**: 31-4 (Gitea driver) is a **thin** driver. No
libsodium; URL shapes nearly identical to GitHub. `IGitPlatformClient`
interface shape works 1:1 for repos, commits, PRs, issues; Actions
client shape works 1:1 for dispatch / monitor / artifacts; secrets
provisioner shape changes (plain POST vs libsodium + PUT).

## 2. Forgejo Actions (15.0 — April 2026)

**Relationship to Gitea**: Forgejo is a hard fork of Gitea (late
2024) that keeps database + API compatibility. Forgejo Actions is
API-compatible with Gitea Actions — workflow YAML, dispatch endpoint,
artifacts, secrets endpoints all share the shape.

**v15 additions**: expand reusable workflows, OIDC support (secure
access to third-party systems from Forgejo workflows), ephemeral
runners for autoscaling.
([Forgejo v15.0 release](https://forgejo.org/2026-04-release-v15-0/),
[Linuxiac Forgejo 15](https://linuxiac.com/forgejo-15-0-dev-platform-released-with-oidc-and-ephemeral-runners/))

**Workflow dispatch + inputs**: `POST
/repos/{owner}/{repo}/actions/workflows/{workflowname}/dispatches`
— same endpoint as Gitea. Inputs typed via workflow YAML; rendered in
UI or accepted via API request body.
([Forgejo Actions Reference](https://forgejo.org/docs/next/user/actions/reference/))

**Webhook signature**: `X-Forgejo-Signature` (HMAC-SHA256, same
computation as Gitea). Falls back to `X-Gitea-Signature` on older
forks. Event header: `X-Forgejo-Event` / `X-Gitea-Event`.

**Impact on Epic 31**: **31-5 is a test-matrix extension, not a new
driver.** Gitea driver + a Forgejo-flavour base URL + a signature
header name override. The original suggested story shape (full driver)
is overweight. **Recommendation: merge 31-5 into 31-4 as a "Gitea +
Forgejo driver family" story, or keep 31-5 as a thin Forgejo
integration + CI matrix extension (8h, not a full driver).**

## 3. GitLab 17+ CI API — programmatic pipeline workflow

**Pipeline triggers**: `POST
/api/v4/projects/:id/trigger/pipeline` with a trigger token in the
body. Supports `ref` + `variables[VAR_NAME]` payload. Inputs feature
added in 17.11 (`ci_inputs_for_pipelines` flag, default enabled)
gives typed parameterized pipelines.
([GitLab Triggers](https://docs.gitlab.com/ci/triggers/),
[GitLab Pipeline Triggers API](https://docs.gitlab.com/api/pipeline_triggers/))

**Alternative dispatch**: `POST /api/v4/projects/:id/pipeline` with
personal access token or project access token — same effect without a
trigger token. Preferred when calling from a server component (no need
to pre-create trigger tokens).
([GitLab Pipelines API](https://docs.gitlab.com/api/pipelines/))

**Pipeline monitoring**: `GET /api/v4/projects/:id/pipelines/:pipeline_id`
for state; `/jobs` + `/jobs/:job_id/artifacts` for artifact download.
Status lifecycle: `created → pending → running → success/failed/canceled`.

**Secrets model (masked variables)**: distinct from GitHub-style
"secrets". Use CI/CD variables at instance, group, project level with
flags `protected: true`, `masked: true`. Protected = restricted to
protected branches/tags; masked = hidden in logs (must meet length +
character constraints). Set via `POST /api/v4/projects/:id/variables`
with `{ key, value, protected, masked, variable_type }`.
([GitLab CI Variables API](https://docs.gitlab.com/api/project_level_variables/))

**Auth**: personal access token (user-level, broad scope), project
access token (project-scoped, bot-like), group access token, OAuth2
app (user-delegated). OAuth2 app is the closest analogue to GitHub
App installation tokens but is user-delegated rather than
installation-delegated.
([GitLab Token Management](https://about.gitlab.com/blog/the-ultimate-guide-to-token-management-at-gitlab/))

**Webhook verification**: `X-Gitlab-Token` header carries the static
configured secret (not HMAC). Compare plaintext. A 2022 issue
(#19367) tracks adding HMAC-digest webhooks but static token is still
the standard in 17.x.
([GitLab Webhook HMAC issue](https://gitlab.com/gitlab-org/gitlab/-/work_items/19367),
[Hookdeck GitLab Webhooks](https://hookdeck.com/webhooks/platforms/how-to-secure-and-verify-gitlab-webhooks-with-hookdeck))

**Impact on Epic 31**: **31-6 is the biggest driver** — GitLab CI is a
materially different model from GitHub Actions:

- No libsodium; masked CI variables use their own model.
- Webhook verification is static-token compare, not HMAC.
- Dispatch path uses pipelines (YAML-defined jobs) not
  `workflow_dispatch`-on-a-file. Agent dispatch maps to "trigger a
  specific pipeline and pass variables".
- Artifacts live on jobs, not runs. Driver has to resolve
  pipeline → jobs → artifact-bearing job → download.

**Story 31-6 stays at full-driver weight** (estimate 32-40h). Keep at
Layer 5.

## 4. Bitbucket Pipelines — programmatic pipeline trigger

**Trigger**: `POST
https://api.bitbucket.org/2.0/repositories/{workspace}/{repo_slug}/pipelines/`
with body `{ target: { ref_type: "branch", ref_name: "main",
type: "pipeline_ref_target", selector: { type: "custom", pattern:
"name-in-yaml" } }, variables: [...] }`.
([Bitbucket Cloud REST API — Pipelines](https://developer.atlassian.com/cloud/bitbucket/rest/api-group-pipelines/),
[Atlassian bbtrigger helper](https://github.com/elpy1/bbtrigger))

**Auth**: app passwords (user-scoped), workspace access tokens,
OAuth2 consumers. App passwords are being deprecated in favour of
API tokens as of 2025-2026.

**Artifacts**: downloaded from the pipeline result view or published
to Bitbucket Downloads; 14-day retention unless exported to 3rd-party
storage.
([Bitbucket Deploy Artifacts](https://support.atlassian.com/bitbucket-cloud/docs/deploy-build-artifacts-to-bitbucket-downloads/))

**Secrets**: repository variables + workspace variables, set via the
UI or `POST /2.0/repositories/{workspace}/{repo_slug}/pipelines_config/variables/`.
Plain JSON body, no encryption wire format.

**Webhook signature**: HMAC-SHA256 with the configured secret; header
`X-Hub-Signature`. Very similar to GitHub.

**Impact on Epic 31**: **31-11 (Bitbucket) is distinct enough from
Gitea/GitLab** to warrant its own driver when product demand arrives.
Atlassian's JSON shape + workspace model don't map 1:1 to the others.
**Leave 31-11 as optional** per user instruction.

## 5. Azure DevOps Pipelines

**Dispatch**: `POST {orgUrl}/{project}/_apis/pipelines/{pipelineId}/runs?api-version=7.1`
with body `{ resources: { repositories: { self: { refName: "refs/heads/main" }}} , variables: {...}, templateParameters: {...} }`.

**Auth**: PAT (personal access token) or Azure AD / Entra ID OAuth
token via service connection. Microsoft is actively deprecating PAT
for new integrations in favour of Entra-backed service connections.
([Azure DevOps PAT-less auth roadmap](https://learn.microsoft.com/en-us/azure/devops/release-notes/roadmap/2025/new-service-connection),
[Azure DevOps PAT docs](https://learn.microsoft.com/en-us/azure/devops/organizations/accounts/use-personal-access-tokens-to-authenticate))

**Run monitoring**: `GET {orgUrl}/{project}/_apis/pipelines/{pipelineId}/runs/{runId}?api-version=7.1`.

**Secrets**: variable groups + pipeline variables. Set via the Core
Pipelines API. Masked + protected attributes are similar to GitLab.

**Webhook model**: different — called "service hooks" — with
configurable HMAC-style signature.

**Impact on Epic 31**: **31-12 is the heaviest optional driver.**
Microsoft is mid-migration off PAT; targeting Entra-backed auth is
more work than the other four drivers combined for a single-customer
payoff. **Leave 31-12 as optional, defer until a paying customer
asks.**

## 6. Secret provisioning — per-platform wire format

Epic 1.5-23..1.5-26 already own mirroring LLM-safe secrets to CI
variable stores. Epic 31 needs the same surface but for
agent-dispatch-authored secrets (the existing
`IGitHubSecretsProvisioner` surface):

| Platform | Encryption | API call |
|---|---|---|
| GitHub | libsodium sealed box (Curve25519 + XSalsa20-Poly1305) via recipient's repo public key | `GET /repos/{owner}/{repo}/actions/secrets/public-key` → `PUT /repos/{owner}/{repo}/actions/secrets/{name}` with `{ encrypted_value, key_id }` ([libsodium sealed boxes](https://libsodium.gitbook.io/doc/public-key_cryptography/sealed_boxes)) |
| Gitea / Forgejo | **None** — plaintext POST, encrypted at rest by server | `PUT /repos/{owner}/{repo}/actions/secrets/{name}` with `{ data: <plaintext> }` |
| GitLab | **None** — plaintext POST, masked flag hides in logs | `POST /api/v4/projects/:id/variables` with `{ key, value, masked: true, protected: true }` |
| Bitbucket | **None** — plaintext POST, secured flag hides in logs | `POST /2.0/repositories/{ws}/{slug}/pipelines_config/variables/` with `{ key, value, secured: true }` |
| Azure DevOps | **None** — variables marked `isSecret: true` hide in logs | Variable group + pipeline variable API |

**Impact on Epic 31**: 31-8 (`ISecretsProvisioner` abstraction) is
the story that factors out the GitHub-specific libsodium step.
Interface contract: "given a platform, repo, name, plaintext, push
it." Each driver owns its wire format. Tests can verify the libsodium
encryption round-trip on GitHub; plaintext path on others.

## 7. Testing harness — container images

**Gitea**: `gitea/gitea` official Docker image + testcontainers-git
(Java) / testcontainers-dotnet support the container pattern.
Forgejo: `codeberg.org/forgejo/forgejo` or `forgejoclone/forgejo`.
GitLab: `gitlab/gitlab-ce` (Docker) — heavy (~3GB, several minutes to
boot), runs inside testcontainers with `dind` orchestration.
([testcontainers-git](https://github.com/sparsick/testcontainers-git))

**Impact on Epic 31**: Story 31-10 (integration test harness) is real
work — Gitea + Forgejo containers are light (2-3 min startup); GitLab
is heavy enough that per-PR runs are slow. Plan: Gitea + Forgejo on
every integration test run; GitLab on scheduled nightly only.

## 8. Onboarding — split sign-in from API access

Key user constraint: sign-in stays GitHub OAuth today; per-tenant IdP
choice is Epic 33 territory. Epic 31 onboarding is **platform-for-
the-repo**, not platform-for-the-user:

- Tenant creation: user signs in via GitHub OAuth (unchanged).
- Tenant picks the git platform their **repos** live on. A GitHub-OAuth
  user can own a tenant whose repos live on Gitea.
- Per-platform install flow:
  - GitHub → GitHub App install (current flow, Story 18-4).
  - Gitea / Forgejo → OAuth2 app consent flow + bot PAT entry +
    webhook secret generation.
  - GitLab → group access token entry + webhook secret entry.
  - Bitbucket → app password or API token entry + webhook secret.

**Impact on Epic 31**: Story 31-9 (onboarding UI) is 3× the per-
driver work of 30-7 (per-provisioning-backend picker) because each
platform has its own credential-entry UX.

## 9. Summary — adjustments to the suggested story shape

| User-suggested | Refined |
|---|---|
| 31-1 interface + capability matrix | **Keep** — expanded to split `IGitPlatformClient` (repos/PRs/branches/webhooks) from `IGitPlatformActionsClient` (CI dispatch/monitor/artifacts/secrets). Platforms that lack CI still implement the former. |
| 31-2 platform registry + per-tenant routing | **Keep** — but split from 30-8 (provisioning routing); 31-2 is platform-level (git plane), 30-8 is infra-level (compute plane). |
| 31-3 GitHub driver refactor | **Keep** — wraps today's `Octokit*Client` behind the new abstraction. |
| 31-4 Gitea driver | **Keep** — thin driver; no libsodium. |
| 31-5 Forgejo driver (full) | **Reframe** — "Gitea/Forgejo test-matrix + compat shim" (8h). Forgejo API ≡ Gitea API in 2026. |
| 31-6 GitLab driver | **Keep** — heaviest driver; different CI model. |
| 31-7 Webhook receiver abstraction | **Keep** — routes by path-segment or by host header; per-platform signature verification. |
| 31-8 Actions/CI secrets provisioner abstraction | **Keep** — per §6. |
| 31-9 Onboarding UI | **Keep** — per §8. |
| 31-10 Integration test harness | **Keep** — per §7. |
| 31-11 Bitbucket driver (optional) | **Keep optional** — deferred. |
| 31-12 Azure DevOps driver (optional) | **Keep optional** — heavier (Entra-backed auth), deferred until an enterprise ask. |

**Net change**: 31-5 drops from full-driver to compat-shim
(-28h). Total Epic 31 hours with default scope (31-1..31-10) ≈
**~228h**; with optional 31-11 + 31-12 ≈ **~324h**.

## 10. Research surprises — things that changed scope

1. **Forgejo is not a separate driver.** Gitea API compatibility is
   by design; 31-5 shrinks to a CI matrix entry.
2. **GitLab's variable model is not a "GitHub secrets port".**
   Protected + masked + environment-scoped is a richer model that
   can't be collapsed to "name / value / encrypted blob". Story 31-6
   includes the richer variable-metadata surface in the interface.
3. **libsodium is only on GitHub.** All four other platforms accept
   plaintext secrets over TLS + encrypt-at-rest server-side. 31-8's
   interface accepts plaintext; libsodium becomes a GitHub-driver
   private implementation detail, not a cross-platform concern.
4. **Azure DevOps is mid-auth-migration.** Entra-backed service
   connections are the future; PAT-based dispatch is deprecating.
   Confirms 31-12 is right to defer.
5. **Gitea secrets have 4 scope levels.** Global / user / org / repo
   with "lowest wins" precedence. More scopes than GitHub's 3 (org /
   repo / environment). Interface needs to expose `scope` on create.

## Sources

- [Gitea Actions Comparison with GitHub Actions](https://docs.gitea.com/usage/actions/comparison)
- [Gitea Actions FAQ](https://docs.gitea.com/usage/actions/faq)
- [DeepWiki Gitea Actions System](https://deepwiki.com/go-gitea/gitea/6-gitea-actions-system)
- [Gitea Actions artifacts (PR #22345)](https://github.com/go-gitea/gitea/pull/22345)
- [Gitea Actions Secrets](https://docs.gitea.com/usage/actions/secrets)
- [Gitea OAuth2 Provider](https://docs.gitea.com/development/oauth2-provider)
- [Gitea API Usage](https://docs.gitea.com/development/api-usage)
- [Forgejo Actions Reference](https://forgejo.org/docs/next/user/actions/reference/)
- [Forgejo v15.0 Release 2026-04](https://forgejo.org/2026-04-release-v15-0/)
- [Linuxiac Forgejo 15.0 release](https://linuxiac.com/forgejo-15-0-dev-platform-released-with-oidc-and-ephemeral-runners/)
- [Tekton Pipelines-as-Code Forgejo/Gitea signature enforcement](https://github.com/tektoncd/pipelines-as-code/issues/2422)
- [GitLab Pipeline Triggers](https://docs.gitlab.com/ci/triggers/)
- [GitLab Pipeline Trigger Tokens API](https://docs.gitlab.com/api/pipeline_triggers/)
- [GitLab Pipelines API](https://docs.gitlab.com/api/pipelines/)
- [GitLab Pipeline Schedules API](https://docs.gitlab.com/api/pipeline_schedules/)
- [GitLab Project-level CI/CD Variables API](https://docs.gitlab.com/api/project_level_variables/)
- [GitLab Ultimate Guide to Token Management](https://about.gitlab.com/blog/the-ultimate-guide-to-token-management-at-gitlab/)
- [GitLab Webhooks](https://docs.gitlab.com/user/project/integrations/webhooks/)
- [Hookdeck GitLab Webhooks](https://hookdeck.com/webhooks/platforms/how-to-secure-and-verify-gitlab-webhooks-with-hookdeck)
- [Bitbucket Cloud REST API — Pipelines](https://developer.atlassian.com/cloud/bitbucket/rest/api-group-pipelines/)
- [Bitbucket Deploy Artifacts](https://support.atlassian.com/bitbucket-cloud/docs/deploy-build-artifacts-to-bitbucket-downloads/)
- [Azure DevOps PAT usage](https://learn.microsoft.com/en-us/azure/devops/organizations/accounts/use-personal-access-tokens-to-authenticate)
- [Azure DevOps PAT-less Auth Roadmap (2025)](https://learn.microsoft.com/en-us/azure/devops/release-notes/roadmap/2025/new-service-connection)
- [Libsodium Sealed Boxes](https://libsodium.gitbook.io/doc/public-key_cryptography/sealed_boxes)
- [testcontainers-git (Sparsick)](https://github.com/sparsick/testcontainers-git)
- [Gitea vs Forgejo vs GitLab CE (2026 guide)](https://mylinux.work/guides/gitea-vs-forgejo-vs-gitlab/)
- [2026 Self-Hosted Git: Gitea, Forgejo (ServerSpan)](https://www.serverspan.com/en/blog/the-2026-guide-to-self-hosted-git-gitea-forgejo-and-the-future-of-code-hosting)
