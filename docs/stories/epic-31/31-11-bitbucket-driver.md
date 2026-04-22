# Story 31-11 (optional): Bitbucket Cloud driver

Status: **deferred / optional — ship when product priority justifies**
(planning brief, 2026-04-21)

## Story

As a **tenant whose repos live on Bitbucket Cloud**,
I want Tamma to read my repos, open pull requests, trigger Bitbucket
Pipelines, download artifacts, push pipeline variables, and verify
inbound webhooks,
so that Bitbucket-centric organisations can adopt Tamma without
migrating hosts.

## Narrative

Bitbucket has a materially different API shape from GitHub /
Gitea / GitLab:

- Workspace + repository slug model (no numeric IDs on the URL path
  in the public surface).
- App passwords are **deprecating** as of 2025-2026 in favour of
  API tokens; auth code has to handle both for transition.
- Pipelines REST is well-documented and stable; artifact model
  differs (Downloads API + 14-day retention + optional 3rd-party
  storage push).

Defer this story until there's a paying customer or explicit product
commitment. The capability matrix (31-1) already lists Bitbucket as
a known kind; dispatch + infra are ready.

## Acceptance Criteria

When implementation starts:

1. New driver project `apps/tamma-elsa/src/Tamma.Platforms.Bitbucket/`.
2. `BitbucketPlatformDriver : IGitPlatformDriver` — `Kind =
   PlatformKind.Bitbucket`.
3. Auth modes: workspace access token, repository access token,
   OAuth2 consumer. App password supported as a transitional mode
   with a deprecation-warning log.
4. Endpoint coverage parallel to 31-4 / 31-6 but against the
   Bitbucket 2.0 API:
   - Repos: `GET /2.0/repositories/{workspace}/{repo_slug}`
   - Branches: `GET /2.0/repositories/{workspace}/{repo_slug}/refs/branches`
   - Pull requests: `GET/POST /2.0/repositories/{workspace}/{repo_slug}/pullrequests`
   - Pipelines dispatch: `POST /2.0/repositories/{workspace}/{repo_slug}/pipelines/`
     with `{ target: { ref_type, ref_name, type:
     "pipeline_ref_target", selector: { type: "custom", pattern:
     "<yaml-pipeline-name>" } }, variables: [...] }`
   - Pipeline status: `GET /2.0/repositories/{workspace}/{repo_slug}/pipelines/{uuid}`
   - Artifact download: via Downloads API + signed URL pattern
   - Repository variables: `POST /2.0/repositories/{workspace}/{repo_slug}/pipelines_config/variables/`
5. Webhook verifier: Bitbucket sends `X-Hub-Signature`
   HMAC-SHA256 — new verifier class in the Bitbucket driver
   complies with 31-7 contract.
6. CI secrets provisioner impl per 31-8 — plaintext POST with
   `secured: true` flag on the variable.
7. Unit tests with WireMock fakes cover endpoints; contract-test
   suite from 31-10 runs against a local `bitbucketselfhosted/bitbucket-server`
   container on nightly-only (even heavier than GitLab CE).
8. Onboarding UI (31-9) adds Bitbucket card behind the
   `Onboarding:EnabledPlatforms` flag.
9. Deprecation handling: if the auth token is an app password, log
   a WARN per call with a link to Atlassian's migration guide.
   Configurable `Bitbucket:ApppasswordDeprecationMode`
   (`warn`|`block`) lets operators force migration.

## Technical Context

### Differences from Gitea/GitLab

- Workspace concept — the path `{workspace}/{repo_slug}` has no
  parallel in Gitea/GitLab. Neutral `PullRequest` / `Repo` records
  from 31-1 carry a `WorkspaceOrOwner` field that maps here.
- Pipeline variables are plaintext + `secured` flag — same shape as
  GitLab masking but simpler (no protected-branch flag).
- Artifacts via Downloads API is a two-step flow (upload to
  Downloads in the pipeline, then reference via build status API)
  — driver exposes a normalised `DownloadArtifactAsync` that wraps
  the two-step flow behind the neutral interface.

### Cost rationale for deferral

Writing the driver is ~28h. On its own that's not large. But the
test harness (Bitbucket Server CE image is 10GB+ and needs manual
license steps) + Atlassian's mid-migration auth surface (app
password → API token) mean time-to-confidence is high. Product
priority should drive pulling this in.

## Dependencies

- **31-1**, **31-2**
- Does not block anything else in Epic 31; shelved until priority

## Estimated hours

**28h** (when triggered)

## Files touched (when triggered)

- `apps/tamma-elsa/src/Tamma.Platforms.Bitbucket/*.cs` (new project)
- Standard DI + onboarding + test wiring per 31-4 pattern

## References

- Research notes: [`../research/multi-git-platform-2026.md`](../research/multi-git-platform-2026.md) §4
- [Bitbucket Cloud REST — Pipelines](https://developer.atlassian.com/cloud/bitbucket/rest/api-group-pipelines/)
- [Atlassian bbtrigger helper](https://github.com/elpy1/bbtrigger)
- [Bitbucket Deploy Artifacts](https://support.atlassian.com/bitbucket-cloud/docs/deploy-build-artifacts-to-bitbucket-downloads/)
