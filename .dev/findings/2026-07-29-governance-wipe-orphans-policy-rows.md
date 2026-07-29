# Finding: the dev-mode DB wipe orphans `action_assignments` principal keys — and single-user ambient reads still ENFORCE the orphans

**Date**: 2026-07-29
**Context**: adversarial review of the Story 43-5 action-governance slice (finding F8,
recorded-not-fixed). The review verdict was approve-with-conditions; F1–F5 were fixed in the
same cycle (see the 43-5 story's 2026-07-29 amendment), F8 is documented here.
**Verdict**: Known hazard, mitigations exist (`Tamma:SingleUser:OwnerUserId`,
`TAMMA_PRESERVE_DB=1`). No code change in 43-5; revisit if the wipe posture or the ambient
collapse changes.

## Background

Two deliberate, individually correct decisions compose badly:

1. **The Epic 19 startup wipe** (`apps/tamma-elsa/src/Tamma.Api/Program.cs:3234-3276`) runs
   `DROP TABLE IF EXISTS … CASCADE` over ~55 tables on every startup unless `TAMMA_PRESERVE_DB=1`.
   The dropped set includes `users` and `tenants` (operational data).
2. **Story 43-5 deliberately EXCLUDES `action_assignments` and `action_authorizations` from that
   list** (AC5, pinned by `ActionGovernanceResidencyTests`): a safety table that silently reverted
   every admin tightening on restart would be a governance surface that lies. The tables are also
   FK-free toward `users`/`tenants` (the `provider_settings` survival pattern), so the CASCADE
   cannot touch them.

Consequence: after a wipe, `action_assignments` still holds rows keyed by `user_id`/`tenant_id`
values that no longer exist anywhere. Those are **orphan principal keys**.

## The hazard, per mode

### Single-user: orphan rows keep ENFORCING, and no view can see or delete them

`GovernancePolicySnapshotStore.GetSnapshotForAmbient` (single-user branch) serves
`CollapsedUserRows` — ALL user-keyed rows collapsed last-write-wins per target, regardless of
whether the user id still exists (`GovernancePolicySnapshotStore.cs`, `FullSnapshot.Build`). So
after a wipe mints a fresh sole user:

- The **ambient plane** (engine, sweepers, the 43-9 tool-loop gate) still resolves the OLD user's
  rows — an orphaned `enabled=false` or `AlwaysHuman` tightening keeps enforcing.
- The **per-user surfaces** (`GET /api/actions/policy`, the PUT/DELETE endpoints) key on the NEW
  caller's user id (`ResolvePrincipalKeysAsync`), which has no rows — the UI shows
  `system-default` everywhere, while the ambient plane enforces something else. The orphan rows
  can be neither seen nor deleted from any per-user view (only `POST …/policy/reset` for the OLD
  id could — and nothing can authenticate as it anymore).
- The refresh-time warning ("rows for N distinct user ids in single-user mode … shadowed") is the
  only signal, and it is a log line.

### SaaS: orphan tenant rows become unreachable — tightenings silently GONE

The mirror image: tenant-keyed rows for a dropped/re-provisioned tenant id are unreachable from
every view (a re-created tenant gets a NEW uuid), and the snapshot only serves a tenant's rows to
that exact tenant id. Here the failure is silent LOSS of tightenings: the admin believes "deploy
needs a person below 90" is stored; the row exists but no principal resolves to it, so the
re-provisioned tenant runs on shipped defaults.

## Why this is accepted for now

- The wipe is a DEV-MODE posture (CLAUDE.md: "no migration anxiety"; app not in production with
  users). Production must run `TAMMA_PRESERVE_DB=1`, which removes the orphaning event entirely.
- `Tamma:SingleUser:OwnerUserId` pins the sole-user id across wipes (the `ISoleUserProvider`
  config override, 43-5 AC7) — with it configured, re-seeded installs keep the SAME principal key
  and the rows are neither orphaned nor shadowed.
- The alternative — adding the governance tables to the DROP list — is strictly worse and is the
  exact failure mode AC5 exists to prevent.

## Mitigations / rules

1. **Production**: `TAMMA_PRESERVE_DB=1` is mandatory anyway; with it, no orphaning occurs.
2. **Dev single-user installs**: set `Tamma:SingleUser:OwnerUserId` to a stable uuid so policy
   rows survive wipes attached to a resolvable principal.
3. **After any wipe without those**: manually reconcile — either truncate the two governance
   tables too (accepting the policy reset) or re-key rows to the new principal ids. Check the
   startup log for the "distinct user ids in single-user mode" warning.
4. **If this ever bites in anger**: the clean fix is an admin-plane orphan report/sweep
   (platform-owner endpoint listing rows whose principal key resolves to no live user/tenant),
   NOT auto-deletion — an orphan row may be the only record of an intended tightening.

## Cross-references

- `docs/stories/epic-43/story-43-5/43-5-storage-principal-resolution-resolver-audit.md` — AC5
  (wipe exclusion), AC7 (`ISoleUserProvider`), and the 2026-07-29 review amendment.
- `apps/tamma-elsa/src/Tamma.Api/Services/Actions/GovernancePolicySnapshotStore.cs` — the
  ambient collapse (`CollapsedUserRows`) and its refresh-time warning.
- `apps/tamma-elsa/tests/Tamma.Api.Tests/Actions/ActionGovernanceResidencyTests.cs` — pins the
  DROP-list exclusion.
