# Epic 43 — Execution Plan

Derived from the eleven story implementation plans. It answers: **what can run at once**, **what is
forced serial**, and **where the schedule floor is**.

## Headline numbers

| Metric | Value |
|---|---|
| Total effort (sum of all stories) | **45 person-days** |
| Critical path | **24 days** — `43-1 → 43-2 → 43-3 → 43-5 → 43-6 → 43-7` |
| Speedup vs. fully serial (45 ÷ 24) | **≈ 1.9×** |
| Hard external gate | **none** — every dependency is internal to this epic |
| Independently shippable today | **43-0** and **43-1**, in either order, gating nothing |

The critical path is the **admin spine**: dial constant → catalog → groups → storage → API → UI.
Enforcement (43-9) is *not* on it — it is a large branch off storage that joins at the end.
That is deliberate: the gate is worthless without something to configure it with, but the
configuration surface does not depend on the gate.

## Dependency graph

```
43-0  (2d) ──────────────────────────────────────────────  independent, ships alone
43-1  (2d) ──┬───────────────────────────────────────────  independent, ships alone
             │
             └─► 43-2  (5d) ──┬─► 43-3 (3d) ──┐
                              │                │
                              ├─► 43-4 (3d) ──┤
                              │                ├─► 43-5 (5d) ──┬─► 43-6 (3d) ──► 43-7 (6d)
                              ├─► 43-8  (5d) ─────────────────┤                      ▲
                              │                                └─► 43-9 (7d)         │
                              └─► 43-10 (2d)                                   critical path
```

| Story | Effort | Depends on | On critical path |
|---|---|---|---|
| 43-0 Prerequisite fixes and dead code | 2 d | — | no |
| 43-1 `AutonomyDial`: one constant | 2 d | — | **yes** |
| 43-2 Catalog core | 5 d | 43-1 | **yes** |
| 43-3 Groups + behaviour-preserving defaults | 3 d | 43-2 | **yes** |
| 43-4 Tool-vocabulary reconciliation | 3 d | 43-2 | no |
| 43-5 Storage, principal resolution, resolver, audit | 5 d | 43-3, 43-4 | **yes** |
| 43-6 Admin API + RBAC | 3 d | 43-5 | **yes** |
| 43-7 Admin UI | 6 d | 43-6 | **yes** |
| 43-8 Drift harnesses | 5 d | 43-2 | no |
| 43-9 Seams, enforcement live, authorization ledger | 7 d | 43-5, 43-8 | no |
| 43-10 Epic 42 spec reconciliation | 2 d | 43-2 | no |

## Waves

| Wave | Stories | Pole | Notes |
|---|---|---|---|
| **0** | 43-0, 43-1 | 2 d | Both independent. 43-0 fixes a live bug and is worth landing on its own merits. |
| **1** | 43-2 | 5 d | The whole epic funnels through the catalog vocabulary. |
| **2** | 43-3, 43-4, 43-8, 43-10 | 5 d | Four-way parallel. 43-8 is the pole and can start with 43-3/43-4. |
| **3** | 43-5 | 5 d | Storage joins the group partition and the tool reconciliation. |
| **4** | 43-6, 43-9 | 7 d | 43-9 is the pole but off the critical path — 43-6 finishes first and releases 43-7. |
| **5** | 43-7 | 6 d | The UI, and the least reliable estimate in the epic. |

Wave-parallel wall clock is **30 days** against a 24-day critical path, because the wave barriers are
artificial — 43-7 does not need 43-9 to finish. **Schedule to the dependency graph, not the wave
table**, if you have the people to do it.

## Where the schedule actually breaks

**1. 43-7 is the least reliable estimate and it sits at the end of the critical path.** It needs
three React primitives with no in-repo precedent — a row-level toggle, a grouped/collapsible table,
and a dimmed row with a why-disabled tooltip. The only real precedent for the last one is Blazor.
Build the primitives first so a slip shows on day two rather than day five. If the schedule is
tight, this is the story to start early against a stubbed API rather than waiting on 43-6.

**2. The group count must be settled before 43-5, not before 43-3.** The partition is 16 groups;
an earlier draft said 15 while listing sixteen. That is a judgment call, not a bug — but group wire
strings become **persisted vocabulary** the moment 43-5 stores an assignment against them. Changing
a wire after that is a migration. Settle it during 43-3's review, which is why 43-3 gets
disproportionate review time relative to its three days.

**3. 43-9 is the largest story and its estimate assumes five seams land together.** They do not have
to. Seams B (tool dispatch) and C (mutating routes) are independently valuable and independently
testable; Seam E (Elsa graphs) needs a new mediation route and is the most likely to slip. If 43-9
overruns, split it rather than delaying the epic — the ledger plus Seams B and C is a coherent
release.

**4. Cross-epic: 43-7 and Epic 44's story 44-6 need the same two React primitives** (`GroupedTable`,
`RowToggle`). Whichever ships first must place them in `common/` so the second imports rather than
reimplements. This is a coordination gate in **both** directions — neither epic owns them outright.

## What ships standalone

**43-0** and **43-1** gate nothing and are worth landing before the epic is scheduled:

- **43-0** fixes a live bug — the acceptance-rules edit dialog omits `acceptorRequirement` from its
  PUT body while the API defaults the missing field, so **every admin save silently resets `design`
  from human-required back to `any`**. That is happening now, independent of this epic.
- **43-1** collapses the `[70,100]` bound to one named constant. Every day it does not land is a day
  a new story can hardcode the bound a fourth time — two unlanded specs already would have.

## Assumptions

- One engineer per story; the parallelism above is people-parallel, not task-parallel.
- Efforts are the story plans' own numbers, unadjusted. Where a plan's estimate differs from the
  epic README's original table, the plan's number is used.
- No time is budgeted for the product-owner decisions listed in the epic README. Two of them —
  MCP tenancy and whether secret-reveal is gateable at all — can block 43-9's descriptor defaults if
  left unanswered.
