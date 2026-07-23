# Epic 40 — Parallel Execution Plan

Derived from the 7 story implementation plans (their *Dependencies & Sequencing*,
*Data & Migrations*, and *Effort Breakdown* sections). It answers: **what can run at once**,
**what is forced serial**, and **where the schedule floor is** — and, crucially, **what Epic 39
must land first**.

## Headline numbers

| Metric | Value |
|---|---|
| Total effort (sum of all stories) | **37.25 person-days** |
| Critical path (Epic-40 internal, 39-10 assumed landed) | **20.5 days** — `40-2 → 40-4 → 40-6 → 40-7` |
| Wave-parallel wall-clock (sum of wave poles) | **~22.75 days** |
| Speedup vs. fully serial | **≈ 1.8×** |
| Hard external gate | **Epic 39-10 must land before 40-2 starts** |
| Hard serialization constraint | one **`TenantDbContext` migration** (40-3 `agent_run_waits`) — joins Epic 39's tenant-migration chain |

The critical path is the resumability spine: **durable suspend → per-task re-entry → event feed →
integration proof**. 40-1 (the runner) is a large but fully **independent** parallel branch — it
ships on the existing dispatch/collect stack and gates nothing on the spine except the final
integration proof (40-7).

## The external gate: Epic 39-10 must land first

Epic 40 **consumes** 39-10's `LifecycleBookmarks` (tenant-folded bookmark builder),
`ResumeBehaviorAttribute`/`ResumeMode`, `CanonicalSuspendActivities`, `LegacyResumeAllowlist`,
and `ResumableStandardStructuralTests`. 40-2's durable bookmark uses the builder; 40-5's
declaration + allowlist burn-down is meaningless without the gate. **39-10 is Epic 39 wave 4** in
that epic's execution plan (needs 39-6 + 39-8). So Epic 40's spine (40-2 onward) cannot start
before Epic 39 reaches 39-10.

**What can start before 39-10:** **40-1** (the `tamma-agent.yml` runner + scaffolding + local
CLI) has **no Epic-39 dependency** — it can run on day 0, entirely in parallel with Epic 39. Given
40-1 is the largest single story (8d) and closes the most visible product gap (the runner does not
exist), **start 40-1 immediately** and let the spine follow 39-10.

## Waves (Epic-40 internal; wave 1 gated on 39-10 for the spine)

Each wave = stories whose hard prerequisites are all in strictly earlier waves (or already
merged). `pole` = the longest single story = the wave's wall-clock if run fully parallel.

| Wave | Stories (effort) | Pole | Notes |
|---|---|---|---|
| **0 (background)** | 40-1 (8) | 8 | **No Epic-39 dep — start day 0**, parallel with all of Epic 39. Ships the runner on the existing dispatch/collect stack. Lands whenever green; only 40-7 consumes it. |
| **1** | 40-2 (6) | 6 | **Gated on 39-10.** The durable-bookmark suspend — the spine root. Adds `WaitForAgentRunActivity` + registers it in `CanonicalSuspendActivities`. |
| **2** | 40-3 (6.75), 40-4 (6.5) | 6.75 | Both hard-need 40-2. 40-3 emits the one `TenantDbContext` migration (`agent_run_waits`) → serialize with Epic 39's tenant chain. 40-3 edits 40-2's `Execute` (row write) — coordinate. |
| **3** | 40-5 (2), 40-6 (3.25) | 3.25 | 40-5 needs 40-2+40-4 (the gate's clauses b/c); 40-6 needs 40-2/3/4 (its emission sites). Both off the longest path. |
| **4** | 40-7 (4.75) | 4.75 | **Solo tail.** Composition proof — hard-needs 40-1..40-6. |

### Wave dependency at a glance
```
(Epic 39 … → 39-10)          40-1  ── background, no 39 dep, land when green ──┐
                    │                                                          │
W1                 40-2  (needs 39-10)                                         │
                  ┌──┴──┐                                                      │
W2              40-3   40-4                                                    │
                  │   ┌─┴────┐                                                 │
W3              40-6 40-5  (40-6 needs 40-2/3/4; 40-5 needs 40-2/4)            │
                  └───┬──────────────────────────────────────────────────────┘
W4                  40-7   (needs 40-1..40-6 — composition proof)
```

## The one hard serial constraint: the `agent_run_waits` migration

**40-3** generates one EF migration against **`TenantDbContext`** (`agent_run_waits`). EF snapshots
the whole tenant model on each `migrations add`, so it **cannot be generated concurrently** with
Epic 39's tenant-context migrations (39-5 `acceptance_rules_overrides`, 39-11 `document_instances`,
39-18 `channel_outbox`, 39-17 configs, 39-21 KB). **Take the single migration-author token**: land
40-3's migration after whatever Epic-39 tenant migration precedes it at merge time, and rebase its
snapshot. Everything else in Epic 40 is additive-append-mergeable (DI registrations, activity
registrations, event constants, the allowlist edit) — standard merge, no ordering constraint.

## Cross-story shared edits (sequence within a wave)

- **`LifecycleBookmarks.cs`** — 40-2 adds `ForAgentRun` + the `CanonicalSuspendActivities` entry.
  If 39-10 has not merged, 40-2 lands a shim and rebases onto `LifecycleBookmarks` at merge.
- **`WaitForAgentRunActivity.Execute`** — created by 40-2; 40-3 adds the `agent_run_waits` row
  write; 40-6 adds the `AGENT_RUN.*` emissions. Accretes across waves 1→2→3 (additive) — those
  stories rebase in order on it.
- **`SingleIssueCycleWorkflow.cs`** — 40-2 swaps the loop node; 40-4 inserts the re-entry node;
  40-5 adds the `[ResumeBehavior]` attribute. Additive; sequence 40-2 → 40-4 → 40-5.
- **Placeholder event constants** — 40-2/40-3/40-4 pin local placeholders; 40-6 consolidates them
  into `AgentRunEventTypes`. Agree the exact strings up front; 40-6's migration is a rename.

## Suggested PR grouping

One PR per story is cleanest (each maps to a plan + its tests). Group only the tightest lockstep:

- **PR-1** 40-1 (independent; merge whenever green — do not hold it to the spine)
- **PR-2** 40-2 (spine root; gated on 39-10)
- **PR-3** 40-3 · **PR-4** 40-4 (parallel; 40-3 coordinates the `Execute` row-write with 40-2's merge)
- **PR-5** 40-5 · **PR-6** 40-6 (parallel; 40-6 consolidates the placeholder constants)
- **PR-7** 40-7 (composition proof — do not fan out until 40-1..40-6 are green)

## Levers to compress

1. **Start 40-1 on day 0.** It is the biggest story, has no Epic-39 dependency, and gates only
   40-7 — pure background parallelism against all of Epic 39. If 40-1 is done before 39-10 lands,
   the spine is the only remaining work.
2. **Protect 40-2's start = the day 39-10 merges.** 40-2 is the spine root; every wave-2/3/4 story
   waits on it. Any slip in 39-10 slips all of Epic 40's spine.
3. **Land 40-5 async, not at a wave boundary.** It is a 2-day declaration/gate flip off the longest
   path; merge it the moment 40-4 is green rather than batching with 40-6.
4. **40-7 is an unavoidable solo tail** (hard-needs the whole epic). Keep its review fast; land its
   scenarios incrementally as each prerequisite merges rather than in one block.

## Method note

Generated from the 7 plans, not hand-guessed: each story's hard-vs-soft deps, `MODIFY` file paths,
and migration target were extracted; waves, critical path, the collision matrix, and the migration
order were synthesized. Re-run if the plans change materially. The 39-10 external gate is the single
most important scheduling fact — Epic 40's spine is a downstream continuation of Epic 39's
resumability wave.
