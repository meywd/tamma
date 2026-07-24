# Epic 40 — Parallel Execution Plan

Derived from the 7 story implementation plans (their *Dependencies & Sequencing*,
*Data & Migrations*, and *Effort Breakdown* sections). It answers: **what can run at once**,
**what is forced serial**, and **where the schedule floor is**.

## Headline numbers

| Metric | Value |
|---|---|
| Total effort (sum of all stories) | **37.25 person-days** |
| Critical path | **20.5 days** — `40-2 → 40-4 → 40-6 → 40-7` |
| Wave-parallel wall-clock (sum of wave poles) | **22.75 days** — `8 + 6.75 + 3.25 + 4.75` |
| Speedup vs. fully serial (37.25 ÷ 20.5) | **≈ 1.8×** |
| Hard external gate | **none** — 39-10 has landed (see below) |
| Hard serialization constraint | one **`TenantDbContext` migration** (40-3 `agent_run_waits`) — joins Epic 39's tenant-migration chain |

The critical path is the resumability spine: **durable suspend → per-task re-entry → event feed →
integration proof**. 40-1 (the runner) is a large but fully **independent** parallel branch — it
ships on the existing dispatch/collect stack and gates nothing on the spine except the final
integration proof (40-7).

## Corrected: the 39-10 external gate is gone (39-10 has landed)

Earlier revisions of this plan carried a **"Epic 39-10 must land before 40-2 starts"** hard gate,
a "what can start before 39-10" carve-out for 40-1, a shim contingency for 40-2, and a lever
telling the schedule to protect "the day 39-10 merges". **All of that is deleted: 39-10 is
shipped.** `LifecycleBookmarks` (`LifecycleBookmarks.cs:30`, `Compose` `:38`,
`CanonicalSuspendActivities` `:98`), `ResumeBehaviorAttribute`/`ResumeMode`
(`ResumeBehavior.cs:11`, `:39`), `ComputeReEntryPositionActivity` and the four-clause build gate
(`ResumableStandardStructuralTests.cs:34`) are all in the tree, and 22 production workflows
already declare against them.

**What that changes and what it does not:**

- **Changes — the wave structure.** 40-2 no longer waits on anything external, so it starts on
  day 0 beside 40-1. The old "wave 0 (background)" and "wave 1 (gated)" collapse into a single
  wave 1.
- **Changes — the shim contingency.** Deleted. 40-2 edits the real `LifecycleBookmarks.cs`.
- **Changes — the levers.** "Protect 40-2's start = the day 39-10 merges" is meaningless and has
  been removed; the `agent_run_waits` migration token is now the only real scheduling constraint.
- **Does NOT change — the arithmetic.** The 37.25 / 20.5 / ≈1.8× figures were always computed from
  Epic-40-internal efforts with no 39-10 term; the parenthetical "39-10 assumed landed" simply
  became a fact. The 22.75-day wall-clock already assumed 40-1 and 40-2 both start on day 0 —
  **but the old five-wave listing made the "sum of wave poles" label false** (8 + 6 + 6.75 + 3.25
  + 4.75 = 28.75, not 22.75). Merging waves 0 and 1 makes the label true as written: the pole of
  the merged wave is `max(40-1 8, 40-2 6) = 8`, and 8 + 6.75 + 3.25 + 4.75 = **22.75**.

## Waves (Epic-40 internal; no external gate)

Each wave = stories whose hard prerequisites are all in strictly earlier waves (or already
merged). `pole` = the longest single story = the wave's wall-clock if run fully parallel.

| Wave | Stories (effort) | Pole | Notes |
|---|---|---|---|
| **1** | 40-1 (8), 40-2 (6) | 8 | **Both start day 0 — no external gate.** 40-1 ships the runner on the existing dispatch/collect stack; it lands whenever green and only 40-7 consumes it. 40-2 is the spine root: the durable-bookmark suspend, adding `WaitForAgentRunActivity` + registering it in the shipped `CanonicalSuspendActivities`. They share no files. |
| **2** | 40-3 (6.75), 40-4 (6.5) | 6.75 | Both hard-need 40-2. 40-3 emits the one `TenantDbContext` migration (`agent_run_waits`) → serialize with Epic 39's tenant chain. 40-3 edits 40-2's `Execute` (row write) — coordinate. 40-4 also owns the clause-(c) extension seam (see *Cross-story shared edits*). |
| **3** | 40-5 (2), 40-6 (3.25) | 3.25 | 40-5 needs 40-2+40-4 (the gate's clauses b/c); 40-6 needs 40-2/3/4 (its emission sites). Both off the longest path. |
| **4** | 40-7 (4.75) | 4.75 | **Solo tail.** Composition proof — hard-needs 40-1..40-6. Its re-entry assertions require the **real** `TaskLoopReEntryService` registered (or 40-4's flag set) — the DI default is the Null seam. |

Pole sum = 8 + 6.75 + 3.25 + 4.75 = **22.75 days**.

### Wave dependency at a glance
```
day 0 ─┬─ 40-1  (runner; independent, land when green) ───────────────────┐
       │                                                                 │
       └─ 40-2  (spine root: durable suspend)                            │
             ├──────────────┐                                            │
W2        40-3           40-4  (+ clause-(c) seam)                       │
             │              ├──────────┐                                 │
W3           └──────────► 40-6       40-5  (needs 40-2/4; merge after 40-4)
                            └──────────┴─────────────────────────────────┤
W4                                                                     40-7
                                                        (needs 40-1..40-6)
```

## The one hard serial constraint: the `agent_run_waits` migration

**40-3** generates one EF migration against **`TenantDbContext`** (`agent_run_waits`). EF snapshots
the whole tenant model on each `migrations add`, so it **cannot be generated concurrently** with
Epic 39's tenant-context migrations (39-5 `acceptance_rules_overrides`, 39-11 `document_instances`,
39-18 `channel_outbox`, 39-17 configs, 39-21 KB). **Take the single migration-author token**: land
40-3's migration after whatever Epic-39 tenant migration precedes it at merge time, and rebase its
snapshot. Everything else in Epic 40 is additive-append-mergeable (DI registrations, activity
registrations, event constants, the two test-fixture edits) — standard merge, no *snapshot*
serialization. The file-level sequencing below still applies; it is rebase order, not a token.

## Cross-story shared edits (sequence within a wave)

- **`LifecycleBookmarks.cs`** — 40-2 adds `ForAgentRun` (delegating to the shipped `Compose`,
  `:38`) + the `CanonicalSuspendActivities` entry (the dictionary at `:98`, today two entries).
  The file exists; there is no shim path.
- **`WaitForAgentRunActivity.Execute`** — created by 40-2; 40-3 adds the `agent_run_waits` row
  write; 40-6 adds the `AGENT_RUN.*` emissions. Accretes across waves 1→2→3 (additive) — those
  stories rebase in order on it.
- **`SingleIssueCycleWorkflow.cs`** — 40-2 swaps the loop node (`tddForTask`,
  `ExecuteAgentActivity` → `WaitForAgentRunActivity`, currently at `:571`); 40-4 inserts the
  re-entry node; 40-5 adds the `[ResumeBehavior]` attribute. **And, cross-epic: Epic 41's
  story 41-29** wraps the same post-`extractCurrentTask` region in a `FlowSwitch` by task `kind`.
  Sequence **40-2 → 40-4 → 40-5 → 41-29**; 41-29 rebases onto the post-40 loop (its plan still
  describes the `code` case as the *pre-40* `ExecuteAgentActivity` shape). Orthogonal edits, but
  the same ~80 lines and the same connection block (`:1180-1190`) — do not fan them out.
- **`ResumableStandardStructuralTests.cs` (tests project) — two separate edits, both budgeted.**
  (1) **40-4** must widen **clause (c)** (`:252`), which today asserts exact type-identity
  membership of `ComputeReEntryPositionActivity` in the built graph. `ComputeTaskResumeIndexActivity`
  cannot satisfy it, and `ComputeReEntryPositionActivity` is unusable for code re-entry (it is
  document-coupled). 40-4 lands the extension seam **and** registers its type.
  (2) **40-5** deletes the `SingleIssueCycleWorkflow` allowlist entry (`:75`). Both are edits to a
  private static field / test method inside one test fixture — trivially mergeable, but 40-5's
  declaration of `Both` arms clause (c), so **40-4's seam must merge first or 40-5 reddens CI**.
- **Placeholder event constants** — 40-2/40-3/40-4 pin local placeholders; 40-6 consolidates them
  into `AgentRunEventTypes`. Agree the exact strings up front; 40-6's migration is a rename.

## Suggested PR grouping

One PR per story is cleanest (each maps to a plan + its tests). Group only the tightest lockstep:

- **PR-1** 40-1 (independent; merge whenever green — do not hold it to the spine)
- **PR-2** 40-2 (spine root; day 0, no gate)
- **PR-3** 40-3 · **PR-4** 40-4 (parallel; 40-3 coordinates the `Execute` row-write with 40-2's merge)
- **PR-5** 40-5 · **PR-6** 40-6 (parallel; 40-6 consolidates the placeholder constants). **PR-5 must
  merge after PR-4** — 40-5's `Both` declaration arms the clause-(c) gate that 40-4's seam satisfies.
- **PR-7** 40-7 (composition proof — do not fan out until 40-1..40-6 are green)

## Levers to compress

1. **Start 40-1 and 40-2 both on day 0.** Neither has an external dependency and they share no
   files. 40-1 is the biggest story and gates only 40-7; 40-2 is the spine root. Running them
   concurrently is what buys the 22.75-day wall-clock — starting 40-2 late is the single most
   expensive scheduling mistake left in this epic.
2. **Take the `agent_run_waits` migration token early.** It is now the **only** hard scheduling
   constraint: 40-3's `TenantDbContext` migration cannot be generated concurrently with Epic 39's
   tenant migrations. Claim the slot as soon as 40-2 merges rather than discovering the collision
   at 40-3's PR.
3. **Land 40-5 async, not at a wave boundary** — but *after* 40-4. It is a 2-day declaration/gate
   flip off the longest path; merge it the moment 40-4's seam + re-entry node are green, rather
   than batching with 40-6.
4. **40-7 is an unavoidable solo tail** (hard-needs the whole epic). Keep its review fast; land its
   scenarios incrementally as each prerequisite merges rather than in one block. Budget setup time
   for its DI posture: the re-entry assertions are unreachable against 40-4's Null default.

## Method note

Generated from the 7 plans, not hand-guessed: each story's hard-vs-soft deps, `MODIFY` file paths,
and migration target were extracted; waves, critical path, the collision matrix, and the migration
order were synthesized. Re-run if the plans change materially.

**Corrected (was: "the 39-10 external gate is the single most important scheduling fact"):** with
39-10 landed there is no external gate, and the single most important scheduling fact is the
**`agent_run_waits` migration token** — the one constraint that cannot be parallelised away,
because it is shared with Epic 39's tenant-migration chain rather than owned inside Epic 40. The
second-most is the cross-epic merge order on `SingleIssueCycleWorkflow.cs`
(40-2 → 40-4 → 40-5 → 41-29).
