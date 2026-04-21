# Wave 3 Implementation Plan Inventory

**Status**: Complete
**Authored**: 2026-04-21
**Scope**: every Epic 18 tenant-user-mgmt extension story (18-7, 18-8)
and every Epic 31 (Multi Git Platform Support) story that received
an implementation plan in the shape of
`docs/stories/epic-19/19-1-phase-1-impl-plan.md` during Wave 3.

Wave 3 covers:

- **Epic 18 backend + UI completion** for tenant-admin user
  management (2 stories).
- **Epic 31 Multi Git Platform Support** foundation + drivers
  (10 full plans + 2 deferred-driver planning blockers).

Both tracks land on branch `feat/auth-foundation`.

## Legend

- **yes (existing)** — plan already committed prior to Wave 3.
- **yes (new)** — plan written in this wave.
- **blocker (written)** — story is explicitly deferred; a planning
  blocker note is substituted for a full impl plan. Requires a
  human decision before writing.
- **skip (covered)** — story is already subsumed by a broader plan
  and does not need a standalone one (not applicable in Wave 3).

All paths below are absolute-from-repo-root.

## Layer 4 Team A — Epic 18 Tenant User Management Completion

| Story ID | Brief path | Has impl plan? | Action | Plan path |
|----------|-----------|:---:|--------|-----------|
| 18-7 | docs/stories/epic-18/18-7-tenant-admin-user-mgmt-api.md | no | yes (new) | docs/stories/epic-18/18-7-tenant-admin-user-mgmt-api-impl-plan.md |
| 18-8 | docs/stories/epic-18/18-8-tenant-admin-user-mgmt-ui.md | no | yes (new) | docs/stories/epic-18/18-8-tenant-admin-user-mgmt-ui-impl-plan.md |

## Layer 4 / Layer 5 — Epic 31 Multi Git Platform Support

| Story ID | Brief path | Has impl plan? | Action | Plan path |
|----------|-----------|:---:|--------|-----------|
| 31-1  | docs/stories/epic-31/31-1-git-platform-abstraction.md | no | yes (new) | docs/stories/epic-31/31-1-git-platform-abstraction-impl-plan.md |
| 31-2  | docs/stories/epic-31/31-2-platform-registry-routing.md | no | yes (new) | docs/stories/epic-31/31-2-platform-registry-routing-impl-plan.md |
| 31-3  | docs/stories/epic-31/31-3-github-driver-refactor.md | no | yes (new) | docs/stories/epic-31/31-3-github-driver-refactor-impl-plan.md |
| 31-4  | docs/stories/epic-31/31-4-gitea-driver.md | no | yes (new) | docs/stories/epic-31/31-4-gitea-driver-impl-plan.md |
| 31-5  | docs/stories/epic-31/31-5-forgejo-compat-matrix.md | no | yes (new) | docs/stories/epic-31/31-5-forgejo-compat-matrix-impl-plan.md |
| 31-6  | docs/stories/epic-31/31-6-gitlab-driver.md | no | yes (new) | docs/stories/epic-31/31-6-gitlab-driver-impl-plan.md |
| 31-7  | docs/stories/epic-31/31-7-webhook-receiver-abstraction.md | no | yes (new) | docs/stories/epic-31/31-7-webhook-receiver-abstraction-impl-plan.md |
| 31-8  | docs/stories/epic-31/31-8-ci-secrets-provisioner-abstraction.md | no | yes (new) | docs/stories/epic-31/31-8-ci-secrets-provisioner-abstraction-impl-plan.md |
| 31-9  | docs/stories/epic-31/31-9-onboarding-platform-picker-ui.md | no | yes (new) | docs/stories/epic-31/31-9-onboarding-platform-picker-ui-impl-plan.md |
| 31-10 | docs/stories/epic-31/31-10-integration-test-harness.md | no | yes (new) | docs/stories/epic-31/31-10-integration-test-harness-impl-plan.md |
| 31-11 | docs/stories/epic-31/31-11-bitbucket-driver.md | no | blocker (written) | docs/stories/epic-31/31-11-bitbucket-driver-planning-blocker.md |
| 31-12 | docs/stories/epic-31/31-12-azure-devops-driver.md | no | blocker (written) | docs/stories/epic-31/31-12-azure-devops-driver-planning-blocker.md |

## Totals

- Epic 18 extensions: 2 stories — 0 existing, 2 new.
- Epic 31: 12 stories — 0 existing, 10 new, 2 deferred planning
  blockers.

Grand total for Wave 3: **14 stories**; 12 full plans written, 2
planning blockers written. 0 stories subsumed.

## Commit sequence

1. `docs(stories): Epic 18 tenant user-mgmt impl plans (18-7, 18-8)`
2. `docs(stories): Epic 31 Multi Git Platform impl plans (31-1 through 31-10)`
3. `docs(stories): Epic 31 deferred-driver planning blockers (31-11, 31-12)`
4. `docs(plans): wave-3 impl plan inventory update`

## Planned hours summary

| Epic / scope | Plans | Planned hours (per plan) |
|---|---:|---:|
| 18 (18-7) | 1 | 14 |
| 18 (18-8) | 1 | 32 |
| 31 (31-1) | 1 | 22 |
| 31 (31-2) | 1 | 26 |
| 31 (31-3) | 1 | 24 |
| 31 (31-4) | 1 | 28 |
| 31 (31-5) | 1 | 9 |
| 31 (31-6) | 1 | 37 |
| 31 (31-7) | 1 | 23 |
| 31 (31-8) | 1 | 20 |
| 31 (31-9) | 1 | 37 |
| 31 (31-10) | 1 | 27 |
| **Wave-3 total (full plans)** | **12** | **~299h** |
| 31-11 (deferred blocker) | 1 | — |
| 31-12 (deferred blocker) | 1 | — |

Note: the "per-plan" hours in this inventory may differ slightly
from the brief-level estimates because the impl plans reflect a
more careful step-by-step decomposition. Variances are documented
in each plan's §8 with rationale.

## Stories marked as planning blockers

- **31-11 Bitbucket Cloud driver** — deferred per epic-31-33-placement
  doc + brief. Three decisions required to unblock (product
  commitment, auth strategy, test-harness topology). See
  `docs/stories/epic-31/31-11-bitbucket-driver-planning-blocker.md`.
- **31-12 Azure DevOps driver** — deferred per epic-31-33-placement
  doc + brief. Three decisions required to unblock (product
  commitment, Entra vs PAT strategy, test-harness decision). See
  `docs/stories/epic-31/31-12-azure-devops-driver-planning-blocker.md`.

## Research findings that reshaped plans

1. **GitLab has no `workflow_dispatch` equivalent** (31-6 plan §9).
   GitHub Actions' per-file `workflow_dispatch` endpoint maps 1:1
   on Gitea/Forgejo but has no GitLab analogue. Plan reframes
   dispatch as "manual pipeline trigger via `POST /api/v4/projects/
   {pid}/pipeline`" with typed `inputs` on 17.11+ falling back to
   `variables` on older versions. Driver feature-detects via
   `GET /version` at startup.

2. **Forgejo 15.0 keeps full Gitea API compatibility** (31-5 plan).
   Research §2 confirms 15.0's DB + REST-API compatibility is by
   design. The original suggested story shape (full Forgejo driver)
   shrinks to a compat-shim (~8h) + test-matrix addition. Header
   fallback (`X-Forgejo-Signature` → `X-Gitea-Signature`) is the
   only divergence today.

3. **libsodium is GitHub-only** (31-8 plan). Research §6 confirms
   every non-GitHub platform accepts plaintext secrets over TLS +
   encrypts server-side. The `ICiSecretsProvisioner` interface now
   exposes plaintext-in; libsodium becomes a GitHub-driver private
   implementation detail. Net effect: interface is cleaner + cross-
   platform secret-push is a single code path at the contract
   level.

4. **GitLab static-token webhook verification** (31-7 + 31-6 plans).
   Unlike GitHub/Gitea/Forgejo/Bitbucket (HMAC-SHA256), GitLab ships
   a static token in `X-Gitlab-Token`. 31-7's `IWebhookSignatureVerifier`
   contract covers both shapes via a `VerifyAsync(body, secret,
   getHeader) → WebhookVerificationResult` signature; GitLab's impl
   is constant-time compare rather than HMAC.

5. **Gitea secrets have 4 scope levels** (31-8 plan §9). Research
   §1 + §10 surprise: Gitea supports Global / User / Org / Repo
   scopes vs GitHub's 3 (Org / Repo / Environment). `CiSecretScope`
   enum now exposes `User` + `Global`; each driver returns
   `scope_not_supported_on_platform` for unsupported scopes per-
   target (non-throwing).

6. **Azure DevOps auth mid-migration** (31-12 blocker). Microsoft's
   2025 roadmap commits to PAT-less via Entra-backed service
   connections. Confirms 31-12 is right to defer until an enterprise
   tenant asks, at which point Entra-first is the recommended path
   (building PAT-first is 2-3h cheaper but likely to be deprecated).

7. **Gitea runner is required for Actions integration tests** (31-10
   plan). Dispatch does not run without a registered runner. Plan
   boots `gitea/act_runner` as a sidecar in the test harness; each
   fixture creates a runner token + auto-registers at fixture init.

## Cross-plan dependencies surfaced

1. **31-3 GitHub refactor blocks 31-4 Gitea driver call sites**:
   31-3 migrates every `IGitHubActionsClient` + `IGitHubAppClient`
   caller to `IPlatformResolver`. Gitea driver (31-4) is a new
   driver — no call-site migration. But **31-7 webhook handlers
   rely on the post-31-3 internal visibility change** to land the
   verifier moves. Documented in 31-7 §dependencies.

2. **31-2 resolver cache subscribes to 31-7's webhook dispatcher
   events + 28-9 switch-org events**: the cache invalidator needs
   both inputs. If 28-9 slips, the cache can still self-heal via
   5-min TTL but stale-credential requests may see a delayed
   rotation. Documented in 31-2 plan §9.

3. **31-9 onboarding UI depends on 29-3 (reveal-once) + 29-5
   (tenant secret UI primitive)**: the webhook-secret display-once
   flow reuses 29-3's server-side hash-and-return-once pattern +
   29-5's `RevealModal` React component. If 29-5 hasn't extracted
   the component into `packages/dashboard-ui/`, 31-9 temporarily
   duplicates it. Documented in 31-9 §dependencies.

4. **31-10 harness fixtures created by 31-5 (Forgejo) and authored
   in this wave**: the Forgejo fixture file is authored inside
   31-5's plan but the CI workflow wiring happens in 31-10. Prevents
   31-10 from blocking waiting for 31-5 to finish fixture work.

5. **18-7's tenant audit endpoint depends on Epic 28 Phase B RLS
   for defence-in-depth**: the endpoint filters by `tenantId`
   explicitly; the RLS policy on `events` table is the belt-and-
   braces. If 28-B slips, 18-7 ships with backend-filter-only
   defence (still correct, just less robust).

6. **31-3 depends on `ctx.GetTenantIdOrThrow()` helper from Epic
   28**: agent-dispatch activities read tenantId from the workflow
   context. Documented in 31-3 §9; if the helper isn't yet plumbed,
   31-3 adds it (+2h).

## Stories marked "planning blocker" after analysis

- **31-11 Bitbucket** — was already marked deferred in the brief
  + placement doc. Analysis confirmed: auth strategy decision
  (app password deprecation) + test-harness decision are both
  open; ship the blocker note.
- **31-12 Azure DevOps** — was already marked deferred in the
  brief + placement doc. Analysis confirmed: Microsoft's PAT-less
  migration means a premature impl would lock in the wrong auth
  model; ship the blocker note.

No previously-planned story was re-classified as blocker in Wave 3.

## Change log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-04-21 | 1.0 | Initial inventory — Wave 3 plans written | Wave 3 docs |
