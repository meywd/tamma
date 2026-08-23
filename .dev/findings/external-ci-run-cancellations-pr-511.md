# 2026-08-13 — external CI-run cancellations on PR #511 (and #512)

**Status:** open — repository-wide (branch-scoping refuted 2026-08-14), identity unconfirmed. Recorded so it is not re-litigated.

## What happened

Six CI runs on branch `claude/wiki-docs-sync-r31nvo` were cancelled
**externally, mid-run** — five while the branch carried PR #511 (the Epic 31
merge branch) and one after it was restarted from `main` for PR #512, which
shows the pattern follows the BRANCH (or the actor watching it), not one PR:

| Date | Head commit | PR |
|---|---|---|
| 2026-08-09 | `316d170` | #511 |
| 2026-08-09 | `cffb83f` | #511 |
| 2026-08-09 | `5659446` | #511 |
| 2026-08-13 | `7474d97` | #511 |
| 2026-08-13 | `fde545c` | #511 |
| 2026-08-13 | `43a85a0` (20:39Z) | #512 |

One further #512 cancellation — `c0c518f` (17:25Z) — is NOT counted here: the
next checkpoint pushed while it ran, so ordinary superseded-run concurrency
explains it.

## What was ruled out (verified at the time)

- **No competing push** — no newer commit landed on the branch while any of the
  five runs was in flight, so GitHub's own superseded-run cancellation does not apply.
- **No human actor** — the repository owner confirmed nobody cancelled them by hand.
- **No concurrency-group collision** — every workflow's `concurrency` group was
  checked and all groups are distinct; none of the five cancellations can be a
  same-group preemption.

## The branch-name hypothesis was tested and REFUTED (2026-08-14)

Commit `755d1b0` was pushed a second time to a differently-named branch
(`verify/epic31-ci-probe`, throwaway PR #513) to test whether the canceller
keyed on the `claude/*` branch name or on whatever watches that branch.

| Run | Branch | Started | Outcome |
|---|---|---|---|
| `755d1b0` | `claude/wiki-docs-sync-r31nvo` | 2026-08-14T01:53:33Z | cancelled |
| `755d1b0` | `verify/epic31-ci-probe` | 2026-08-14T02:29:34Z | **cancelled 02:54:49Z (~25 min in)** |

Identical commit, different branch, no competing push on either — both cancelled.
**The canceller is not branch-scoped**; it acts on this repository's workflow
runs generally. The consistent signature is a cancellation roughly 25–50 minutes
into a long run, while short runs and (earlier in the same period) some full runs
completed normally.

Practical consequence: while this actor is running, a long CI run on this
repository cannot be relied on to reach a verdict, on ANY branch. Verification
has to come from local suites or from whoever can stop the canceller.

## Prime suspect

The **deployed Tamma instance's own automation** reacting to its repository's
PR/CI events (the platform watches its own repo — self-maintenance is the
product), cancelling runs it believes are stale or superseded.

## Resolution path

1. **Org audit log** — query `action:workflow_run` around the five timestamps;
   the audit entry carries the cancelling identity (app installation vs user).
2. **Watch for recurrence** on this branch's runs (the Epic 31 follow-up work
   continues on `claude/wiki-docs-sync-r31nvo`); any new cancellation should be
   captured with its `workflow_run` audit row immediately.
3. If the canceller is the deployed Tamma instance, file the bug against its
   run-supersession logic (it must never cancel runs on a repo/branch it does
   not own the head of) before re-enabling that automation against this repo.


## 2026-08-21 — cases 7 and 8, and the mitigation's bootstrap gap

Two more mid-run cancellations with the same fingerprint (no superseding push,
run's commit still the branch tip): PR #514's run 32320039806 (.NET Tests
killed 25 minutes into an otherwise-green board, 2026-08-20 01:35 UTC) and PR
#515's run 32465179879 (same job, 25 minutes in, 2026-08-21 09:17 UTC).

Mitigation shipped in PR #515: `.github/workflows/ci-rescue.yml` re-runs an
externally-cancelled run automatically (guards: conclusion=cancelled only,
commit still the branch tip, max two rescues). NOTE its bootstrap gap:
workflow_run triggers execute from the DEFAULT branch's file, so the rescue is
inert until merged — it could not protect its own PR. Identification of the
canceller still needs the org audit log (`action:workflow_run`), owner-only.
