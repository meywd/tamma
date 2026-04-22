# Story 31-11 Planning Blocker — Bitbucket Cloud Driver

**Status**: Deferred (2026-04-21) — no implementation plan authored.
**Story brief**: [`31-11-bitbucket-driver.md`](./31-11-bitbucket-driver.md)

---

## Why no impl plan

Story 31-11 is explicitly marked `Status: deferred / optional — ship
when product priority justifies` in its brief, and the Epic 31
placement doc
([`../plans/epic-31-33-placement.md`](../plans/epic-31-33-placement.md))
confirms "Deferred / optional (Bitbucket 31-11, Azure DevOps 31-12):
not on any layer. Activate post-launch when a paying customer or
explicit product priority justifies."

Authoring a full impl plan now would prematurely lock in:

- Bitbucket Cloud API version target (2.0 is stable but Atlassian
  publishes API surface updates on a rolling calendar; 2027 changes
  unknown).
- Auth strategy (app passwords are **deprecating** in 2025-2026 per
  Atlassian; API tokens are the forward path but the deprecation
  deadline for app passwords is itself a moving target).
- Test-harness topology (Bitbucket Server CE container is 10GB+ and
  requires a manual license; cloud-only testing is viable but means
  CI depends on a real cloud test workspace).
- Pipeline variable wire format (currently plaintext `secured: true`,
  but Atlassian has signaled "vault-backed secrets" on the roadmap).

## What unblocks this story

Three decisions must land before a full impl plan is worth writing:

1. **Product decision**: a paying customer with Bitbucket-hosted
   repos commits contractually, or an explicit product roadmap item
   promotes 31-11 to "required for the next release". Until then,
   Bitbucket remains a capability matrix entry + picker flag, not a
   driver.
2. **Auth strategy decision**: app passwords deprecating means the
   driver must ship API-token support from day 1, with app password
   as a transitional mode. Once the deprecation deadline is final,
   target API tokens only (simpler). Decision owner: tech lead.
3. **Test-harness topology**: (a) cloud test workspace on every
   nightly run — adds ops risk (credential rotation, quota
   management); (b) Bitbucket Server CE container — heavyweight
   license path. Decision owner: QA lead + ops.

## Trigger conditions

This story activates when **any one** of:

1. A paying customer with Bitbucket-hosted repos commits (first
   enterprise tenant on Bitbucket).
2. Product backlog formally promotes 31-11 into a numbered roadmap
   release.
3. ≥3 prospects independently request Bitbucket support within 60
   days.
4. A strategic integration partnership with Atlassian surfaces that
   requires Bitbucket + Tamma interop.

None of these has fired as of 2026-04-21.

## What the stub plan would look like (for reference)

If and when unblocked, the impl plan would cover:

- New driver project `Tamma.Platforms.Bitbucket/` parallel in shape
  to `Tamma.Platforms.Gitea/` (same typed-HTTP-client pattern).
- Auth: API token (primary) + workspace access token + OAuth2
  consumer + app password (transitional, with WARN log).
- Workspace + repo-slug model baked into the driver (no numeric ids
  in URL path; `PullRequest.Repo` carries `WorkspaceOrOwner` field).
- Pipelines REST coverage per research §4:
  - `POST /2.0/repositories/{ws}/{slug}/pipelines/` with
    `{ target: { ref_type, ref_name, type: "pipeline_ref_target",
    selector: { type: "custom", pattern: "<yaml>" } }, variables }`.
  - `GET …/pipelines/{uuid}` for status.
  - Downloads API + `build_status` two-step for artifacts.
- Pipeline variables via `POST …/pipelines_config/variables/`
  plaintext + `secured: true`.
- Webhook verification: `X-Hub-Signature` HMAC-SHA256 (similar to
  GitHub).
- Capabilities: `Actions`, `Artifacts`, `Secrets`, `WebhookHmac`,
  `PrFileReview` — no libsodium, no GitLab-style protected/masked
  flags (only `secured`).
- Integration test: scheduled nightly against a real Bitbucket
  Cloud test workspace OR a Bitbucket Server CE container (pending
  topology decision above).
- Onboarding UI (31-9) adds Bitbucket card behind `Onboarding:
  EnabledPlatforms` flag.

Estimated hours when unblocked: **~28h** per brief. Trigger the
re-estimation once the auth-strategy + test-harness decisions are
locked.

## Cross-references

- Story brief: [`31-11-bitbucket-driver.md`](./31-11-bitbucket-driver.md)
- Research notes: [`../research/multi-git-platform-2026.md §4`](../research/multi-git-platform-2026.md)
- Epic placement: [`../plans/epic-31-33-placement.md`](../plans/epic-31-33-placement.md)
- Capability matrix: `Tamma.Platforms.Abstractions/PlatformKindCapabilityMatrix.cs`
  (already ships `PlatformKind.Bitbucket` as a known-but-unimplemented
  kind).
- Atlassian migration guide: [Bitbucket Cloud API tokens](https://developer.atlassian.com/cloud/bitbucket/rest/)

## Action for the caller

**Do not schedule this story until a trigger has fired and the three
decisions above are recorded.** When ready to unblock:

1. Append a "Trigger fired" section to this document with date,
   trigger, and ADR link.
2. Convert this document into a proper impl plan (rename to
   `31-11-bitbucket-driver-impl-plan.md`) matching the Epic-19 /
   31-4 exemplar shape.
3. Update the inventory in `../plans/` to flip the row from
   `blocker (written)` → `yes (new)`.
