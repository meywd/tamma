# Epic 44 — Execution Plan

Derived from the ten story implementation plans. It answers: **what can run at once**, **what is
forced serial**, and **where the schedule floor is**.

## Headline numbers

| Metric | Value |
|---|---|
| Total effort (sum of all stories) | **49 person-days** |
| Critical path | **28 days** — `44-0 → 44-1 → 44-2 → 44-6` |
| Speedup vs. fully serial (49 ÷ 28) | **≈ 1.75×** |
| Hard external gate | **none for the core** — 39-20 is a soft dependency (see below) |
| Longest single story | **44-6** at 9 days, and it sits on the critical path |

The critical path is **core → storage → API → UI**. Everything else — hierarchy, iterations, events,
loop integration, external link, dogfooding — hangs off the API as parallel branches.

## Dependency graph

```
44-0 (4d) ──► 44-1 (6d) ──► 44-2 (5d) ──┬─► 44-3 (4d) ──┐
                                         │               ├─► 44-4 (4d)
                                         ├─► 44-5 (4d)   │
                                         ├─► 44-6 (9d) ◄─┘   critical path ends here
                                         ├─► 44-7 (5d)
                                         ├─► 44-8 (4d)
                                         └─► 44-9 (4d)  ◄── needs 44-3 + 44-4
```

| Story | Effort | Depends on | On critical path |
|---|---|---|---|
| 44-0 Tracker core | 4 d | — | **yes** |
| 44-1 Storage + the tenant-migration sweep | 6 d | 44-0 | **yes** |
| 44-2 Work-item & project API, RBAC | 5 d | 44-1 | **yes** |
| 44-3 Hierarchy, ranking, `BacklogOrdering` apply seam | 4 d | 44-2 | no |
| 44-4 Iterations, board projection, `SprintPlan` apply seam | 4 d | 44-2, 44-3 | no |
| 44-5 DCB events + the event-name drift ratchet | 4 d | 44-2 | no |
| 44-6 Tracker UI + the missing CI test line | 9 d | 44-2 (44-3/44-4 for board) | **yes** |
| 44-7 Loop integration, `issueId` join | 5 d | 44-2 | no |
| 44-8 External link: GitHub import | 4 d | 44-2 | no |
| 44-9 Dogfood: generated `sprint-status.yaml` | 4 d | 44-3, 44-4 | no |

## Waves

| Wave | Stories | Pole |
|---|---|---|
| **0** | 44-0 | 4 d |
| **1** | 44-1 | 6 d |
| **2** | 44-2 | 5 d |
| **3** | 44-3, 44-5, 44-7, 44-8 | 5 d |
| **4** | 44-4, 44-6, 44-9 | 9 d |

Wave-parallel wall clock is **29 days** against a 28-day critical path — the two are nearly identical
here because the epic is genuinely a spine with a fan-out at the end, not a wide graph.

## Where the schedule actually breaks

**1. 44-6 is 9 days, the longest story, and it is on the critical path.** It is also the story the
survey flagged as the weakest estimate. It shares two React primitives with Epic 43's story 43-7
(`GroupedTable`, `RowToggle`) — whichever epic ships first must put them in `common/`. If 43-7 lands
first, 44-6 gets cheaper; if they run concurrently, agree the primitive owner **before** either
starts or you will build them twice.

**2. 44-1 owns infrastructure nobody else has needed yet.** The migrate-all-provisioned-tenants
sweep does not exist: `MigrateTenantAppAsync` has exactly two production call sites, both
creation-only, and there is no startup sweep. `EfTenantDbMigrator` is already idempotent so the fix
is small — but it is on the critical path and it is the first time a tenant migration in this repo
has had to reach existing tenants. Budget review time, not just build time.

**3. 44-7 is the story that makes the tracker matter, and it is *not* on the critical path.** A
tracker nobody's workflow reads is a database. If the epic has to be cut short, cut 44-8 and 44-9
before touching 44-7.

**4. 39-20 is a soft dependency that is currently a no-op.** Epic 44 consumes
`EligibleAudienceAsync` for assignee pickers and `CanSeeAsync` for list filtering. The shipped
resolver is `InitiatorOnlyTaskAudienceResolver`, and `ChannelOutboxService.cs:143` hardcodes
`InitiatorUserId: null` — so it returns **empty for every input**. In single-user mode the
initiator-only rule is already correct and nothing is lost. In SaaS, 44-2 must degrade honestly
(wire discriminators on `source`/`visibilityMode`) rather than render an empty assignee picker.
**Do not schedule SaaS visibility as done until 39-20 lands.**

## Cut lines

If the epic must be smaller, these are the honest cuts in order:

1. ~~**44-9 (dogfood)** — 4 days.~~ **NO LONGER A CUT LINE (corrected 2026-07-25).** Open question 2
   has been answered: the tracker serves both audiences and **Tamma is tenant #1**, because the
   platform self-maintains. Generating `sprint-status.yaml` from the tracker is therefore not a
   dogfood nicety — it is the evidence that tenant #1 exists at all. Its 4 days are load-bearing.
   If scope must come out, take it from 44-8 or 44-5's non-ratchet half instead.
2. **44-8 (external link)** — 4 days. The native tracker works without GitHub import; import is what
   makes migration painless, not what makes the tracker function.
3. **44-5 (events + ratchet)** — 4 days, *but* the event-name drift ratchet is the only test the repo
   would ever have enforcing `AGGREGATE.ACTION.STATUS` across ~300 constants. Cutting the story
   should not cut the ratchet; move it somewhere else.

**Do not cut 44-1's migration sweep.** Without it a tenant-resident tracker silently fails to reach
every tenant provisioned before the epic shipped, and the failure mode is an empty tracker rather
than an error.

## Assumptions

- One engineer per story; parallelism is people-parallel.
- Efforts are the story plans' own numbers.
- No time budgeted for the six product-owner questions in the epic README. Two of them — whether
  `packages/dashboard-user` is meant to be the customer app, and whether the tracker serves Tamma's
  own work or customers' — change the shape of 44-6 and 44-9 respectively and should be answered
  before Wave 3.
