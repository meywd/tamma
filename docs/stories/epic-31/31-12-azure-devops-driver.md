# Story 31-12 (optional): Azure DevOps driver

Status: **deferred / optional — ship only if enterprise / Entra-
aligned tenant requires it** (planning brief, 2026-04-21)

## Story

As a **tenant whose repos live in Azure DevOps (with or without
Entra ID)**,
I want Tamma to read my repos, open pull requests, trigger pipeline
runs, download artifacts, manage variable groups, and verify
inbound service-hook events,
so that organisations standardised on Azure DevOps can adopt Tamma.

## Narrative

Azure DevOps is the heaviest optional driver:

- Auth is **mid-migration**: Personal Access Tokens (PAT) are being
  deprecated in favour of Entra-backed service connections.
  ([Azure DevOps PAT-less auth roadmap](https://learn.microsoft.com/en-us/azure/devops/release-notes/roadmap/2025/new-service-connection))
- Pipeline model is YAML (unified with Classic for runs API); the
  driver exercises the YAML API surface.
- Artifact model: pipeline run artifacts served via
  `{orgUrl}/{project}/_apis/pipelines/{pipelineId}/runs/{runId}/artifacts`.
- Webhooks ("service hooks") have a different subscription model and
  signature shape than the other platforms.
- Variable groups are managed through the Library UI + the Azure
  DevOps Library API.

Defer this story until an enterprise tenant explicitly requires it.
Even then, scope the pilot to Entra-backed service connections only
— PAT support is a backward-compat liability.

## Acceptance Criteria

When implementation starts:

1. New driver project `apps/tamma-elsa/src/Tamma.Platforms.AzureDevOps/`.
2. `AzureDevOpsPlatformDriver : IGitPlatformDriver` — `Kind =
   PlatformKind.AzureDevOps`.
3. Auth: **Entra-backed service connection** (primary). PAT support
   is a secondary mode documented as "legacy — PAT-less is
   recommended." `AzureDevOpsAuth` union:
   `Entra(tenantId, clientId, clientSecret, resource)` or
   `PersonalAccessToken(token)`.
4. Endpoint coverage against `{orgUrl}/{project}/_apis/` surface at
   API version `7.1-preview` or the then-current stable version:
   - Repos: `_apis/git/repositories`
   - Branches: `_apis/git/repositories/{repoId}/refs?filter=heads`
   - Pull requests: `_apis/git/repositories/{repoId}/pullrequests`
   - Pipeline runs: `_apis/pipelines/{pipelineId}/runs` (`POST`
     with `{ resources, variables, templateParameters }`)
   - Run status: `_apis/pipelines/{pipelineId}/runs/{runId}`
   - Variable groups: `_apis/distributedtask/variablegroups`
5. Service hooks subscription API wiring — Tamma registers its
   webhook endpoint when a tenant connects, and the Azure DevOps
   service-hook payloads are verified via the HMAC key returned in
   the subscription response.
6. CI secrets provisioner per 31-8 — writes into a variable group
   or pipeline variables with `isSecret: true`. Driver owns
   namespace choice (Library variable groups preferred).
7. Webhook verifier per 31-7 — service-hook HMAC shape.
8. Unit tests with WireMock; contract-test suite runs against
   **Azure DevOps Services (cloud)** in a dedicated test org —
   no local container (Microsoft does not ship one).
9. Onboarding UI (31-9) adds Azure DevOps card behind the
   `Onboarding:EnabledPlatforms` flag.
10. Documentation includes the Entra-app-registration setup guide
    for tenant admins — the one step no amount of UX can automate.

## Technical Context

### Why Entra-first

Microsoft's 2025 roadmap commits to PAT-less auth. Building a PAT-
based pilot is cheaper but likely to be deprecated in 12-18
months. The effort delta Entra → PAT is one auth mode + an extra
library dependency; not worth saving the 2-3h when Entra is the
forward path.

### Why no container

Azure DevOps has no self-hostable Docker image for the cloud
product. Azure DevOps **Server** (on-prem) is Windows + requires
manual license — unusable in CI. Contract tests run against a
real cloud test org with a service-principal credentials secret in
CI; scheduled nightly only.

### Entra auth flow

Driver uses `Microsoft.Identity.Client` (MSAL.NET) + the
`499b84ac-1321-427f-aa17-267ca6975798/.default` resource scope.
Credentials persisted in the secret store (Epic 29). Token cache
is per-process, 1h TTL with silent refresh.

## Dependencies

- **31-1**, **31-2**
- Does not block anything else in Epic 31

## Estimated hours

**36h** (when triggered) — heaviest optional driver due to Entra
setup + service hooks subscription API complexity.

## Files touched (when triggered)

- `apps/tamma-elsa/src/Tamma.Platforms.AzureDevOps/*.cs` (new project)
- `Tamma.sln`
- Standard DI + onboarding + test wiring

## References

- Research notes: [`../research/multi-git-platform-2026.md`](../research/multi-git-platform-2026.md) §5
- [Azure DevOps PAT-less Auth Roadmap](https://learn.microsoft.com/en-us/azure/devops/release-notes/roadmap/2025/new-service-connection)
- [Azure DevOps Use PATs](https://learn.microsoft.com/en-us/azure/devops/organizations/accounts/use-personal-access-tokens-to-authenticate)
- [Azure DevOps Pipelines API](https://learn.microsoft.com/en-us/rest/api/azure/devops/pipelines/?view=azure-devops-rest-7.1)
