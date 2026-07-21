# Epic 39 — Parallel Execution Plan

Derived from the 21 story implementation plans (their *Dependencies & Sequencing*,
*Data & Migrations*, and *Effort Breakdown* sections). It answers three questions:
**what can run at once**, **what is forced serial**, and **where the schedule floor is**.

## Headline numbers

| Metric | Value |
|---|---|
| Total effort (sum of all stories) | **135.75 person-days** |
| Critical path (schedule floor) | **46.25 days** — `39-2 → 39-5 → 39-6 → 39-10 → 39-12 → 39-13 → 39-15` |
| Wave-parallel wall-clock (sum of wave poles) | **~52.75 days** |
| Speedup vs. fully serial | **≈ 2.6×** |
| Hard serialization constraint | the **Tenant EF-migration chain** (5 stories, one at a time) |

The critical path is the document-lifecycle spine: **core types → lifecycle workflow →
resumability → pilot → family migrations**. No amount of extra parallel branches beats
46.25 days; the ~6.5-day gap to 52.75 is wave-boundary slack recoverable with async landing
(don't hold an off-path story to a wave boundary — merge it when it's green).

## Waves

Each wave = stories whose hard prerequisites are all in **strictly earlier** waves.
`pole` = the longest single story = the wave's wall-clock if run fully parallel.
Stubbable dependencies do **not** block — the story develops against a fake seam.

| Wave | Stories (effort) | Pole | Notes |
|---|---|---|---|
| **1** | 39-2 (5), 39-1 (4), 39-19 (7.5), 39-20 (8), 39-21 (10.5) | 10.5 | Zero-hard-prereq set. **39-2 is the root — start it day 0.** The user-facing/RAG cluster (19/20/21) has no Epic-39 hard prereqs; it builds entirely against stubbed seams. 39-1 is a docs-only discovery gate (soft-consumed everywhere). |
| **2** | 39-3 (5), 39-4 (6), 39-5 (7), 39-9 (6.5) | 7 | All depend only on 39-2. 39-5 = first Tenant migration (now the wave pole and the wave-2 gate onto the critical path). 39-9 uses fake validator delegates → no wait on the type registry. |
| **3** | 39-6 (8), 39-7 (6.25), 39-8 (5), 39-11 (5), 39-18 (7) | 8 | 39-6/7/8 are mutual **lockstep** (not prereqs). 39-11 + 39-18 both emit Tenant migrations → serialize. |
| **4** | 39-10 (7), 39-17 (8) | 8 | 39-10 needs 39-6+39-8; 39-17 needs 39-8 (only ready now). 39-17 = 4th Tenant migration. |
| **5** | 39-12 (5) | 5 | **Solo neck.** The pilot is the integration proof gating every family migration; needs 39-10 (wave 4). |
| **6** | 39-13 (7), 39-14 (6.75), 39-16 (4) | 7 | All unblocked once 39-12 lands. 39-13 edits `DocumentLifecycleWorkflow.cs` (39-14 does not); the file accretes filed-back hooks cross-wave across 39-12 → 39-13 → 39-15 (additive), so those stories rebase in order on it. |
| **7** | 39-15 (7.25) | 7.25 | **Solo tail.** Hard-prereqs both 39-13 and 39-14 — the largest prereq stack in the epic. |

### Wave dependency at a glance
```
W1  39-2      39-1     39-19  39-20  39-21     (19/20/21 run background, stubbed)
     │                          │(seam owner)
W2  39-3 39-4 39-5 39-9
     └────┬────┘ │
W3        39-6 39-7 39-8 39-11 39-18
               │        │
W4            39-10   39-17
               │
W5            39-12                              (solo — integration proof)
               │
W6        39-13 39-14 39-16
             └───┬───┘
W7            39-15                              (solo — remaining producers)
```

## The one hard serial constraint: Tenant EF migrations

Five stories generate migrations against **`TenantDbContext`**, and EF snapshots the
whole model on each `migrations add` — so they **cannot be generated concurrently even
on separate branches**. Enforce a **single migration-author token** for the tenant
context and rebase in this order:

1. **39-5** — `acceptance_rules_overrides` (wave 2, first)
2. **39-11** — `document_instances` (wave 3)
3. **39-18** — `channel_outbox` (wave 3, after 39-11)
4. **39-17** — orchestrator agent configs (wave 4)
5. **39-21** — knowledge-base / pgvector tables (starts wave 1, lands late — rebase its
   snapshot onto whatever tenant migration precedes it at merge time)

**39-20's migration is against `ControlPlaneDbContext`** — a *separate* snapshot, exempt
from this chain, generates concurrently.

Everything else in the collision list is **additive-append-mergeable** — DI registrations
in `Program.cs`, entity blocks in `TammaModelConfiguration.cs`, `DbSet`s in
`TenantDbContext.cs`, permission rows in `Permissions.cs`, event constants, drift-test
allowlists. Standard merge, no ordering constraint.

## Seam-owner assignments (avoid two stories minting the same interface)

Land the interface with the story that can start earliest; others adopt it:

| Shared seam | Owner (canonical shape — file may land earlier) | Adopters |
|---|---|---|
| `ITaskAudienceResolver` / `EligibleAudience` | **39-20 (canonical shape + real impl); interface file first-landed by whichever of 39-18/39-19 lands first** (both wave 1) | 39-18, 39-19 |
| `ITaskAssignmentService` | 39-17 defines; **39-20** implements | 39-17 stub → 39-20 real |
| `IChatTranscriptRecorder` | 39-17 defines (contract only) | **39-19** implements (sole recorder) |
| `IWorkflowInitiationAuthorizer` | **39-19** defines | 39-20 implements grant-aware |
| `Review` type / `ReviewDecision` | **39-4** | 39-7 consumes |
| `AcceptanceRequest` / `AcceptanceDecision` / guardrails | **39-5** | 39-6/8/17/18 |
| `DocumentLifecycleWorkflow` review dispatch id `"document-review"` | agree 39-6 ↔ 39-7 up front | — |

## Within-wave file sequencing (same file, same wave)

Not parallel-blockers across waves, but two branches in the *same* wave touching these
must sequence (last-in rebases):

- `DocumentTypeRegistry.cs` — **39-2 → 39-3/39-4** (count-pin; second bumps 0→N)
- `DocumentLifecycleWorkflow.cs` — 39-13 (wave 6) accretes onto it after 39-12 (wave 5); 39-15 (wave 7) adds more. Cross-wave additive hook accretion 39-12 → 39-13 → 39-15 (39-14 does not touch this file).

## Suggested PR grouping

One PR per story is cleanest (each maps to a plan + its tests). Group only where a
lockstep contract is easier landed together:

- **PR-A** 39-2 (unblocks everything — merge first, fast-track review)
- **PR-B** 39-3, **PR-C** 39-4 (parallel; coordinate the registry pin)
- **PR-D** 39-5 · **PR-E** 39-9 (parallel with B/C)
- **PR-F** 39-6 + 39-7 (lockstep review dispatch) · **PR-G** 39-8 · **PR-H** 39-11 · **PR-I** 39-18
- **PR-J** 39-10 · **PR-K** 39-17
- **PR-L** 39-12 (checkpoint — do not fan out the family migrations until this is green)
- **PR-M** 39-13 · **PR-N** 39-14 · **PR-O** 39-16
- **PR-P** 39-15
- **background, land when green:** 39-19, 39-20 (seam owner — land early), 39-21 (long pole, gates nothing)

## Levers to compress below 52.75 days

1. **Land off-path stories async, not at wave boundaries.** 39-17 (wave 4 pole, off
   critical path) and 39-14 (wave 6) pad their waves; merging them when green rather
   than at the boundary recovers most of the 52.75→46.25 gap.
2. **Start 39-19/39-20/39-21 on day 0.** They have no Epic-39 hard prereqs and gate
   nothing on the critical path — pure background parallelism. **39-21 (10.5d) must not
   gate the wave-1→2 handoff** (it doesn't — 39-2 does).
3. **Protect 39-5's start — the gating wave-2 story on the critical path.** 39-5 (7d) is
   the wave-2 pole and the spine's hand-off into 39-6; any slip there slips the whole
   epic. 39-6 in turn is squarely on the path and needs 39-2/3/5 all green (39-5 being
   the wave-2 gate). Same for their reviewers.
4. **39-10 → 39-12 is a confirmed HARD structural gate** (the pilot bakes 39-10's re-entry
   node into its binding graph and must pass ResumableStandardStructuralTests with no
   allowlist entry) — the solo wave 5 cannot be shaved.
5. **Solo waves 5 (39-12) and 7 (39-15) are unavoidable** given hard prereqs — no branch
   reshuffle removes them. Accept them as necks; keep their reviews fast.

## Method note

This plan is generated from the plans, not hand-guessed: 21 extractor passes pulled each
story's hard-vs-stubbable deps, every `MODIFY` file path, and its migration target; a
synthesis pass computed the waves, critical path, collision matrix, and migration order.
Re-run if the plans change materially.
