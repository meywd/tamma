# 2026-08-13 — external CI-run cancellations on PR #511

**Status:** open — prime suspect identified, identity unconfirmed. Recorded so it is not re-litigated.

## What happened

Five CI runs on branch `claude/wiki-docs-sync-r31nvo` (PR #511, the Epic 31 merge branch)
were cancelled **externally, mid-run**:

| Date | Head commit |
|---|---|
| 2026-08-09 | `316d170` |
| 2026-08-09 | `cffb83f` |
| 2026-08-09 | `5659446` |
| 2026-08-13 | `7474d97` |
| 2026-08-13 | `fde545c` |

## What was ruled out (verified at the time)

- **No competing push** — no newer commit landed on the branch while any of the
  five runs was in flight, so GitHub's own superseded-run cancellation does not apply.
- **No human actor** — the repository owner confirmed nobody cancelled them by hand.
- **No concurrency-group collision** — every workflow's `concurrency` group was
  checked and all groups are distinct; none of the five cancellations can be a
  same-group preemption.

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
