# Story 31-12 Planning Blocker — Azure DevOps Driver

**Status**: Deferred (2026-04-21) — no implementation plan authored.
**Story brief**: [`31-12-azure-devops-driver.md`](./31-12-azure-devops-driver.md)

---

## Why no impl plan

Story 31-12 is explicitly marked `Status: deferred / optional — ship
only if enterprise / Entra-aligned tenant requires it` in its brief,
and the Epic 31 placement doc
([`../plans/epic-31-33-placement.md`](../plans/epic-31-33-placement.md))
confirms the deferral. The research notes §5 single out Azure DevOps
as "the heaviest optional driver" because it is actively mid-
migration off Personal Access Tokens (PAT) to Entra-backed service
connections.

Authoring a full impl plan now would prematurely lock in:

- Auth strategy. PATs are deprecating but no hard end-of-life date
  has been published. Building a PAT-only pilot ships faster but
  risks a forced rewrite in 12-18 months. Building an Entra-first
  driver requires a tenant to pre-register an Entra app — per-tenant
  infrastructure lead time that onboarding UX cannot eliminate.
- Pipeline API version target. Azure DevOps API version `7.1-preview`
  is current stable; `7.2` is in rolling preview. A premature pin
  locks us.
- Service-hook HMAC shape. Azure service-hook verification differs
  per subscription configuration; capturing all variants requires
  real integrations during implementation.
- Variable groups vs pipeline variables. Library variable groups are
  the forward-recommended container; deciding which layer the
  provisioner writes to affects every secret rotation path.
- No self-hostable Docker image exists for Azure DevOps Cloud. An
  integration harness must call a real cloud test org — with all the
  ops overhead (service-principal rotation, quota, multi-tenant
  credential hygiene).

## What unblocks this story

Three decisions must be made before a full impl plan is worth
writing:

1. **Product decision**: an enterprise tenant with Azure DevOps-
   hosted repos commits contractually, or an explicit enterprise-
   grade "Tamma for Azure DevOps" product decision happens. Until
   then, the 36h spend has no payoff.
2. **Auth strategy decision**: Entra-first (forward-compat, 4h
   more implementation) vs PAT-first (ship fast, breakage in 12-
   18mo). Recommendation per research §5: Entra-first. Decision
   owner: tech lead + enterprise PM.
3. **Test-harness decision**: (a) dedicated real cloud test org
   with credentials rotated via Epic 29 — requires ops setup and
   ongoing cost; (b) pure WireMock unit tests only — skip live-
   integration coverage and accept the regression-risk gap. Decision
   owner: QA lead.

## Trigger conditions

This story activates when **any one** of:

1. First enterprise customer commits with "Azure DevOps support" as
   a contract term.
2. The Tamma sales funnel shows ≥3 enterprise prospects in 60 days
   asking for Azure DevOps integration.
3. Compliance / audit finding requires Tamma to support the
   customer's existing CI/CD platform (Azure DevOps is often
   required in regulated Microsoft-heavy environments).
4. "Tamma Enterprise" product plan launches and Azure DevOps is
   table stakes.

None has fired as of 2026-04-21.

## What the stub plan would look like (for reference)

If and when unblocked, the impl plan would cover:

- New driver project `Tamma.Platforms.AzureDevOps/` parallel in shape
  to `Tamma.Platforms.GitLab/` (similar CI-pipeline model).
- Auth: Entra-backed service connection (primary) via
  `Microsoft.Identity.Client` (MSAL.NET); resource scope
  `499b84ac-1321-427f-aa17-267ca6975798/.default`. PAT fallback
  documented as legacy.
- Endpoint coverage against `{orgUrl}/{project}/_apis/` surface at
  API version `7.1-preview`:
  - `_apis/git/repositories` + `_apis/git/repositories/{repoId}/refs`
    + `_apis/git/repositories/{repoId}/pullrequests`.
  - `_apis/pipelines/{pipelineId}/runs` (POST) with
    `{ resources, variables, templateParameters }`.
  - `_apis/pipelines/{pipelineId}/runs/{runId}` for monitor.
  - `_apis/distributedtask/variablegroups` for CI variable
    provisioning.
- Service-hook subscription wiring: register webhook endpoint on
  tenant connect; verify incoming via HMAC key from the
  subscription response.
- `ICiSecretsProvisioner` impl writes into Library variable groups
  (preferred) or pipeline variables with `isSecret: true`.
- Capabilities: `Actions`, `Artifacts`, `Secrets`, `ProtectedVariables`,
  `MaskedVariables`, `WebhookHmac` (service-hook), `PrFileReview`
  with caveat (the "threads" API is different from GitHub/GitLab
  inline comments).
- Onboarding UI (31-9) adds Azure DevOps card with step-by-step
  Entra-app-registration guide; one step cannot be automated by the
  UX and documentation is the bridge.
- Integration test: real Azure DevOps Cloud org with rotated
  service-principal credentials. Scheduled nightly only.
- Entra token cache per-process, 1h TTL, silent refresh.

Estimated hours when unblocked: **~36h** per brief — the heaviest
optional driver.

## Cross-references

- Story brief: [`31-12-azure-devops-driver.md`](./31-12-azure-devops-driver.md)
- Research notes: [`../research/multi-git-platform-2026.md §5`](../research/multi-git-platform-2026.md)
- Epic placement: [`../plans/epic-31-33-placement.md`](../plans/epic-31-33-placement.md)
- Capability matrix: `Tamma.Platforms.Abstractions/PlatformKindCapabilityMatrix.cs`
  (ships `PlatformKind.AzureDevOps` as a known-but-unimplemented kind).
- Microsoft PAT-less roadmap: [Azure DevOps roadmap 2025 — new service connection](https://learn.microsoft.com/en-us/azure/devops/release-notes/roadmap/2025/new-service-connection)

## Action for the caller

**Do not schedule this story until a trigger has fired and the three
decisions above are recorded.** When ready to unblock:

1. Append a "Trigger fired" section to this document with date,
   trigger (which of the four conditions), and ADR link.
2. Convert this document into a proper impl plan (rename to
   `31-12-azure-devops-driver-impl-plan.md`) matching the Epic-19 /
   31-6 exemplar shape.
3. Update the inventory in `../plans/` to flip the row from
   `blocker (written)` → `yes (new)`.

## Risks to consider at activation time

- Entra-migration uncertainty at impl time. Plan: re-run research §5
  right before implementation to reconcile any PAT deprecation
  progression.
- Self-hostable image unlikely to appear. Plan: budget for cloud-
  test-org ops cost.
- `ICiSecretsProvisioner` capability matrix is already defined; Azure
  DevOps driver must conform or the matrix needs new entries for
  library-vs-pipeline variable targets.
