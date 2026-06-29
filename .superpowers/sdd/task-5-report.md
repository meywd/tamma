# Task 5 — Documentation truth-up: execution report

**Branch:** `feat/epic-30-phase-a-v1v2-cutover`
**Date:** 2026-06-29

## Files changed

### 1. `CLAUDE.md` — Multi-tenant provisioning (Cranl) section

**What changed:**
- Replaced the stale `NullTenantProvisioner` reference with the correct V2 seam:
  `NullTenantProvider` registered under key `"null"`, routed through
  `ProvisionTenantV2Dispatcher` / `TenantProviderRegistry`.
- Noted that admin endpoints now ride the V2 dispatcher (commit range
  `c25cd980`–`d69c42bb`).
- Described the null-provider short-circuit: provision → `Ready` /
  `shared_infrastructure_no_backend_configured` (no enqueue, no-op under unified
  schema-per-tenant); deprovision → `Deprovisioned` (no-op).
- Described the Cranl path: `CranlProvisionPlatformTaskHandler` /
  `CranlDeprovisionPlatformTaskHandler` → `CranlProvisioningWorkflow`.
- Corrected the auth policy name from `OwnerAccess` to `PlatformOwnerAccess` (the
  correct policy for platform-owner-only routes — `OwnerAccess` admits every
  personal-tenant owner, which is wrong here).
- Added an ops note on `PlatformTaskWorker.RunOnStartup = false`.
- Added a "Deferred to Phase B" callout for pool-row registration, `provider_resource_ids`
  persistence, and the hard-blocked `RegisterSecrets` step (Epic 29 `ISecretStore`).

**Claims softened:** None. All facts verified against the commit log and the
verified-appendix section of the execution plan.

---

### 2. `docs/stories/epic-30/README.md` — Epic status + Wave C execution record

**What changed:**
- Updated epic status from "planning (briefs only, 2026-04-20)" to
  "in-progress (Phase A / Wave C complete 2026-06-29; Phases B–E outstanding)".
- Added a full "Execution status" section at the top with per-commit records for
  `c25cd980`, `ca4a3879`, `7678e794`, `d69c42bb`, and `c9f2c353`.
- Explicitly recorded the Cranl-handler deviation (plan called for deleting
  `CranlProvisioningWorkflow`; instead it was kept and two `IPlatformTaskHandler`s
  were wired).
- Listed all Phase B deferred items with blockers named (`RegisterSecrets` →
  hard-blocked on Epic 29 `ISecretStore`; pool-row registration; quota; key persistence).
- Listed Phases C–E as outstanding with blockers/names.
- Preserved the original "Why this epic exists" body with a framing note that it
  describes the pre-Phase A state.

**Claims softened:** The original task brief said the Cranl provision "completes
end-to-end (project→db→app→Ready)". This has not been verified via a live Cranl
test run (no `CRANL_API_KEY_TEST` available in CI). The report says the path "is
functional" — meaning the wiring is in place and the existing `CranlProvisioningWorkflow`
tests pass — without claiming a live end-to-end run was verified.

---

### 3. `docs/superpowers/plans/2026-06-11-epic-30-pluggable-provisioning.md` — Phase A status

**What changed:**
- Status line: `PLANNED (2026-06-11). Not started.` → `PHASE A DONE (2026-06-29); Phases B–E PLANNED.`
- Phase A section: replaced three `- [ ]` task bullets with checked `- [x]` bullets
  plus per-task commit refs and brief descriptions of what was delivered.
- Added a prominent **Deviation** note under Task A3: `CranlProvisioningWorkflow.cs`
  was NOT deleted (as the plan specified); instead it was kept and two new
  `IPlatformTaskHandler`s were wired, making Cranl functional in Phase A rather than
  Phase B.
- Phases B–E task bullets left as `- [ ]` (unchanged, still PLANNED).

**Claims softened:** None. Deviation accurately reflects what `c9f2c353` does
(verified against the commit subject and the execution plan appendix).

---

### 4. `docs/superpowers/plans/2026-06-29-epic-30-phase-a-v1-to-v2-cutover.md` — Task 4 deviation note

**What changed:**
- Added a "Execution note (deviation from plan)" paragraph to the Task 4 section
  explaining that `CranlProvisioningWorkflow.cs` was kept (not deleted), why
  (would have orphaned V2 Cranl path), what was done instead (wired two
  `IPlatformTaskHandler`s in `c9f2c353`), and cross-referencing the parent plan's
  deviation record.
- Updated the "Files: Delete" bullet to remove `CranlProvisioningWorkflow.cs` from
  the list and note it was not deleted.

---

## Consistency check

- Commit SHAs used (`c25cd980`, `ca4a3879`, `7678e794`, `d69c42bb`, `c9f2c353`) were
  verified against `git log --oneline` on the branch before editing.
- No `.cs` files were touched. All four files edited are Markdown (`.md`) or `CLAUDE.md`.
- `PlatformOwnerAccess` policy name corrected in CLAUDE.md (was `OwnerAccess` in the
  old text — a pre-existing error, not introduced by Phase A; corrected here as part of
  the truth-up).

---

## Accuracy-review fixes (2026-06-29, post-initial task-5)

Three docs-only corrections applied on branch `feat/epic-30-phase-a-v1v2-cutover`:

### Fix 1 — CLAUDE.md deprovision sentence (factual error)

**File:** `CLAUDE.md` (~line 651)

The original sentence conflated the two-level Cranl deprovision routing by claiming
`CranlDeprovisionPlatformTaskHandler` handles the `provisioning.tenant.v2` task directly.
The actual routing is two-level:

1. `ProvisionTenantV2TaskHandler` handles `provisioning.tenant.v2` (branches on `Operation=Deprovision`) → calls `CranlTenantProviderV2.DeprovisionAsync`
2. `CranlTenantProviderV2.DeprovisionAsync` enqueues `provisioning.tenant.deprovision`
3. `CranlDeprovisionPlatformTaskHandler` handles `provisioning.tenant.deprovision` → `CranlProvisioningWorkflow`

**New text:**
> Deprovision follows the same pattern: the null seam returns `Deprovisioned` (no-op); for a Cranl-configured deployment the V2 task handler (task type `provisioning.tenant.v2`) branches on `Operation=Deprovision` and calls `CranlTenantProviderV2.DeprovisionAsync`, which enqueues a `provisioning.tenant.deprovision` task handled by `CranlDeprovisionPlatformTaskHandler` → `CranlProvisioningWorkflow` (the REST-walk engine, kept in Phase A to provide the Cranl teardown path).

Provision-side description (~line 660) verified accurate — left unchanged.

### Fix 2 — CLAUDE.md commit range end (consistency)

**File:** `CLAUDE.md` (~line 649)

Changed `commits \`c25cd980\`–\`d69c42bb\`` to `commits \`c25cd980\`–\`c9f2c353\`` so the
range end reflects the correcting/completing commit rather than the over-deletion commit.

### Fix 3 — Plan file Task 4 step list (reader confusion)

**File:** `docs/superpowers/plans/2026-06-29-epic-30-phase-a-v1-to-v2-cutover.md` (Task 4)

Added a blockquote note immediately before Step 1's checkbox:
> **Superseded:** the steps below describe the original delete-all approach; the deviation note above records what actually shipped (engine kept, Cranl platform handlers wired in c9f2c353).

No `.cs` files touched. All three edits are Markdown / CLAUDE.md only.
