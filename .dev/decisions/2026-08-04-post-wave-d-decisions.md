# Decisions — 2026-08-04 (post-Wave-D)

**Status**: accepted (all three decided by the product owner on 2026-08-04)
**Context**: after PR #508 merged (Waves A–D), an evidence-first investigation of
`origin/main` produced a ranked plan. Three of its items were product/priority calls
rather than technical facts. They were put to the owner and answered. Recorded here
because a decision that lives only in a chat transcript is a decision that gets
re-litigated — twice this week stale prose caused an assistant to "fix" something that
was already fixed, or propose a fix that would have broken a deliberate behaviour.

---

## D1 — The autonomy dial stays floored at 70 in the dashboard, for now

**Decision**: hold the UI floor at 70. Lower it later, deliberately — not as a
side-effect of another change.

**What is true today**: the backend dial accepts `[1, 100]`
(`Tamma.Core/Actions/AutonomyDial.cs`), while the only shipped slider hardcodes a
minimum of 70 (`packages/dashboard/src/components/acceptance-rules/RulesEditDialog.tsx`)
and a test pins it there. Roughly 155 of 174 levelled catalog descriptors sit below 70,
so the shipped UI hides essentially the whole dial.

**Consequence, stated plainly**: the governance model Epic 43 built is real and
enforced server-side, but an operator cannot currently see or move most of it. That is
accepted for now. This is a *known* gap, not an oversight — do not "discover" it again
and file it as a bug.

**When it is lowered**, ship it together with the policy-diff preview
(`GET /api/actions/policy/diff?from&to`, already implemented and wired at
`Program.cs`), because widening the range hands an operator a control that can
de-automate ~150 actions in a single drag.

---

## D2 — A rejected merge triggers a "what is needed to accept?" workflow

**Decision**: rejecting at the merge-approval gate must not simply comment and label.
It should start a workflow that determines **what is required to make the PR
acceptable**, and close the PR only if nothing can make it acceptable.

**What is true today**: `MergeApprovalWorkflow`'s reject edge says "label/comment the
PR" but dispatches `update-issue-status` keyed on the *issue*. The human's rejection
feedback never reaches the PR, which is left open, unlabelled and uncommented. The
`prNumber` variable is in scope on that edge and unused.

**Shape of the work** (not yet designed in detail — this is a story, not a patch):
- the reject edge carries the human's feedback into a producer that answers "what would
  make this acceptable?" — concrete, checkable remediation, not prose;
- if remediation exists → surface it on the PR (comment + label) and route the cycle to
  the appropriate follow-up rather than a terminal;
- if nothing can make it acceptable → close the PR (`effect:git.pull-request.close`,
  level 35, reversible via reopen — which is *why* it is rated 35).

**Explicitly rejected alternative**: the smaller "just comment and label it" change.
It was on the plan; the owner asked for the workflow instead. Do not silently
substitute the cheaper version.

---

## D3 — Multi-git-platform breadth (Epic 31) stays frozen

> **SUPERSEDED 2026-08-05 by owner direction.** The owner unfroze Epic 31 and decided
> the architecture: config-activated platform, every git AND CI call through the
> abstraction (`IGitPlatformClient` / `IGitPlatformActionsClient`), auth a separate
> plane, and (amended 2026-08-07) every platform/third-party action as an explicit
> workflow step with an is-supported check step before it and a defined alternative
> step when unsupported. Execution is governed by
> `docs/stories/epic-31/EXECUTION-PLAN.md` (phases P0–P6), started 2026-08-07 on
> owner instruction ("don't wait"). Demand is asserted by the owner directly — the
> phantom-citation note below stands as history but no longer gates the work.

**Decision**: undecided, therefore frozen. Do not invest in GitLab / Gitea / Forgejo /
Bitbucket / Azure DevOps driver depth until someone names a real prospect.

**What is true today**: GitHub works. The other drivers exist but answer most verbs
with `capability_unsupported` or `ServiceUnavailable`. A tenant can select Gitea, pass
the onboarding probe, receive verified webhooks — and then nothing can act on their
repo. There are three GitHub-hardcoded mediation planes, not one.

**Note on the demand evidence**: the only demand statement found in the repo is a
single unsourced clause in `docs/stories/epic-31/README.md`, which cites
`docs/research/multi-git-platform-2026.md`. **That research note does not exist**,
despite being cited from 11 places. Anyone reopening this epic should establish real
demand first rather than inheriting that citation.

**Trigger to unfreeze**: one named prospect or customer requiring a non-GitHub
platform. At that point the smallest coherent slice is a driver-selection/routing path
(Story 31-2), not per-driver verb breadth.

---

## Two corrections this investigation produced (recorded so they are not re-litigated)

1. **The "human approval floor can be silently erased" defect is CLOSED.** The acceptor
   requirement is *derived*, not stored: `AcceptanceFloors.ShippedFloorFor(type, dial)`
   returns `Human` iff the dial is below that document type's catalog level. There is no
   stored field for a base-row write to shadow. A related "fix" — flooring the principal
   group tier — would have **broken** a deliberate, documented behaviour that Story
   43-15's `409 LEVEL_OWNED` editability predicate is built on. Do not build it.

2. **Cranl / tenancy "Phase B" is largely done; the docs were stale.** The
   "V2 saga requires ≥2 platform-worker processes" limitation no longer holds
   (`ProbeUntilReadyAsync` no longer exists in `src`). It is also unreachable at runtime
   today: `PlatformTaskWorker.RunOnStartup` defaults `false` and Cranl is opt-in.
