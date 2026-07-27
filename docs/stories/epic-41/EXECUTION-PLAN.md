# Epic 41 — Execution Plan

Derived from the 34 story implementation plans (41-1a/41-1b/41-1c plus 41-2..41-32; the 41-1 umbrella
is an index, not work). It answers: **what can run at once**, **what is forced serial**, and **where
the schedule floor is**. Effort figures are the plans' bottom-line totals, which override the story
files' older ranges wherever they differ.

## Headline numbers

| Metric | Value |
|---|---|
| Total effort (sum of all 34 stories, cheap-path assumptions below) | **≈ 169 person-days** |
| Critical path (epic-internal) | **16.4 days** — `41-1b (5.5) → 41-2 (5.0) → 41-15 (5.9)` |
| Wave-parallel wall-clock (sum of wave poles) | **≈ 20 days** — `6.75 + 7.5 + 5.9` (waves 0/1/2; the day-0 independents ride inside wave 0's shadow) |
| Speedup vs. fully serial (169 ÷ 16.4) | **≈ 10×** theoretical; people-bound in practice — the epic is unusually wide (up to ~14 independent work streams on day 0) |
| Hard external gate | **one** — `41-15` (and 41-29 Phase 2) queue file-level behind Epic 40's `40-2 → 40-4 → 40-5` rewrite of `SingleIssueCycleWorkflow.cs` (order registered in epic-40's EXECUTION-PLAN) |
| Hard serialization constraint | **the pinned interface-edge count** — every producer story bumps the same `WorkflowInterfaceGraphTests.cs:45` `HaveCount(16)` integer (+ the `reconciled` array + three sibling drift pins); one-per-story serialized bump, see below |
| Startable day 0 | 41-1a, 41-1b, 41-1c, 41-30 (the four Wave-0 enablers) **plus** 41-12, 41-14, 41-17 (code-review half), 41-18, 41-20 (produce half), 41-21, 41-23 (produce half), 41-29 (Phase 1), 41-31, 41-32 — and the template-rewrite halves of 41-2/41-3/41-4 |

The critical path is the **definition-of-done spine**: types → acceptance criteria → acceptance
verification. It is short because the epic is wide, not deep — 24 of the 34 stories are thin
lifecycle bindings of 3.3–7.5 days each hanging off one of three enablers.

### Gate arithmetic (get this right before scheduling anything)

- **41-1a + 41-1b hard-block seventeen stories** on both execution paths — **fifteen at their produce
  step**: 41-2, 41-3, 41-5, 41-6, 41-7, 41-8 (Phase A), 41-10, 41-11, 41-13, 41-16, 41-17 (PR-triage
  half), 41-19, 41-22, 41-27, 41-28 — **plus 41-24 and 41-25 at their review stage** (the
  `(tech_writer, review-docs)` selector arm). *41-26 left this set:* its default reviewer is now the
  already-reachable `(devops, review-operability)`, making 41-1a an upgrade there, not a gate.
  *41-5 joined it:* its produce cell moved to `(project_manager, report-status)`, which only 41-1a
  mints. (41-15 is blocked transitively through 41-2.)
- **41-1c blocks the eight prose stories**: 41-4, 41-5, 41-8 (Phase B), 41-9, 41-22, 41-24, 41-25,
  41-26 — nothing prose can be produced, persisted, or reviewed before the `prose` type + `Audience`
  field exist. 41-9 is the designated prose reference implementation, so 41-1c is deliberately
  scheduled first among equals (it is independent of 41-1a/41-1b).
- **41-30 gates the five audits** — 41-11, 41-16, 41-17 (PR-sweep half), 41-20, 41-23 — at their
  *scheduled-cadence* AC only; each one's producing half is buildable before it. Per the product
  owner's **2026-07-25 decision** (audits are scheduled, ceremonies are user-initiated),
  **41-5 and 41-7 are NOT scheduler-blocked** — both ship complete on the manual trigger that
  already exists, and a cron cadence is a later opt-in through 41-30.
- **Startable immediately** (no unlanded blocker): 41-12, 41-14, 41-17's code-review half, 41-18,
  41-21, 41-29 Phase 1, 41-30, 41-31, 41-32, and 41-20/41-23's producing halves. **41-2/41-3/41-4
  are NOT 41-1a-blocked** (their cells exist; each instead carries an in-scope template rewrite —
  the shipped templates emit the wrong document shape), so their rewrite + scaffold work can start
  day 0; their *merges* queue behind 41-1b (41-2/41-3: types) and 41-1c + 41-3 (41-4: prose +
  consumed anchor).

## Per-story table

| Story | Effort (plan) | Hard-blocked by | On critical path |
|---|---|---|---|
| 41-1a taxonomy (3 roles / 18+1 cells / selector maps) | 4.5 | — | no |
| 41-1b six document types | 5.5 | — | **yes** |
| 41-1c prose + audience | 3.5 | — | no |
| 41-2 acceptance-criteria authoring | 5.0 | 41-1b | **yes** |
| 41-3 backlog prioritization | 5.5 | 41-1b | no |
| 41-4 roadmap shaping | 5.0 | 41-1c, 41-3 | no |
| 41-5 stakeholder/status reporting (Part A) | 5.75 | 41-1a, 41-1c | no |
| 41-6 sprint planning | 5.25 | 41-1a, 41-1b, 41-3 | no |
| 41-7 standup synthesis | 5.0 | 41-1a | no |
| 41-8 retro (Phase A; B = prose narrative) | 4.25* | A: 41-1a · B: 41-1c + 41-1a amendment | no |
| 41-9 ADR authoring (prose reference impl) | 3.5 | 41-1c | no |
| 41-10 system design document | 4.5 | 41-1a (`design-system`) | no |
| 41-11 tech-debt / risk triage | 5.25* | 41-1a, 41-30 (cadence) | no |
| 41-12 dependency & upgrade planning | 4.0 | — | no |
| 41-13 test-plan authoring | 4.4 | 41-1b, 41-2 | no |
| 41-14 exploratory test charter | 3.3 | — | no |
| 41-15 acceptance verification | 5.9 | 41-1b→41-2; **file-level: Epic 40 (40-2→40-4→40-5) + 41-29** | **yes** |
| 41-16 regression / flaky mgmt (Phase A) | 6.75 | 41-1a, 41-30 (cadence); type decision with 41-1b | no |
| 41-17 code review (A) / PR-triage sweep (B) | 3.75 + 2.5 | A: — · B: 41-1a, 41-30 | no |
| 41-18 refactor planning | 3.9 | — | no |
| 41-19 threat modeling | 3.85 | 41-1b | no |
| 41-20 scheduled security audit | 5.35 | 41-30 (cadence AC only) | no |
| 41-21 security incident analysis | 4.1 | — | no |
| 41-22 incident response & postmortem | 7.5 | 41-1a (`incident-rootcause`), 41-1c | no |
| 41-23 capacity & health review (producing half) | 4.75 (+0.5 trigger wiring after 41-30) | 41-30 (cadence) | no |
| 41-24 release notes & changelog | 5.0 | 41-1c; 41-1a (review stage) | no |
| 41-25 user & API documentation | 4.5* | 41-1c; 41-1a (review stage); 41-24 D6 (else +0.5) | no |
| 41-26 runbook & ops docs | 3.75* | 41-1c (41-1a is an upgrade only — ops-peer reviewer default) | no |
| 41-27 user-flow & wireframe (UxSpec) | 4.5 | 41-1a, 41-1b | no |
| 41-28 design review & a11y audit | 4.5 | 41-1a, 41-1b; 41-27 soft | no |
| 41-29 task-level flow router | 7.0 | Phase 1: — · Phase 2: file-level behind 40-5 | no |
| 41-30 scheduled-trigger seam | 6.75 | — | no |
| 41-31 standalone emergency rollback | 5.0 | — (soft: coordinate DCB reader with 41-5) | no |
| 41-32 alert-triggered response seam | 5.5 | — (soft: 41-30 allowlist/ledger idiom) | no |

\* cheap-path figure — see Assumptions.

## Dependency graph

```
day 0 ─┬─ 41-1a (4.5) ──┬─► 41-7 (5.0) ──► 41-8A (4.25) ─► [41-8B needs 41-1c + 1a amendment]
       │                ├─► 41-10 (4.5)
       │                ├─► 41-5 (5.75, also needs 41-1c)      41-22 (7.5, needs 41-1a + 41-1c)
       │                └─► 41-27 (4.5, also needs 41-1b) ──► 41-28 (4.5, soft)
       │
       ├─ 41-1b (5.5) ──┬─► 41-2 (5.0) ──┬─► 41-15 (5.9)   ◄── CRITICAL PATH (also file-gated on Epic 40 + 41-29)
       │                │                └─► 41-13 (4.4) ──► 41-14 (soft edge only)
       │                ├─► 41-3 (5.5) ──┬─► 41-6 (5.25, also needs 41-1a)
       │                │                └─► 41-4 (5.0, also needs 41-1c)
       │                └─► 41-19 (3.85)
       │
       ├─ 41-1c (3.5) ──┬─► 41-9 (3.5)  ── the prose path's reference implementation
       │                └─► 41-24 (5.0) ──► 41-25 (4.5) · 41-26 (3.75)   [D6 review-docs rewrite inherited]
       │
       ├─ 41-30 (6.75) ─┬─► 41-11 (5.25, also 41-1a) · 41-16 (6.75, also 41-1a)
       │                └─► 41-17B (2.5, also 41-1a) · 41-20 cadence · 41-23 trigger (+0.5)
       │
       └─ independents: 41-12 (4.0) · 41-14 (3.3) · 41-17A (3.75) · 41-18 (3.9) · 41-20 produce (5.35)
                        41-21 (4.1) · 41-23 produce (4.75) · 41-29 P1 (7.0) · 41-31 (5.0) · 41-32 (5.5)
```

## Waves

Each wave = stories whose hard prerequisites are all in strictly earlier waves (or already merged).
`pole` = the longest single story in the wave.

| Wave | Stories | Pole | Notes |
|---|---|---|---|
| **0** | 41-1a, 41-1b, 41-1c, 41-30 — plus the day-0 independents (41-12, 41-14, 41-17A, 41-18, 41-20/41-23 produce halves, 41-21, 41-29 P1, 41-31, 41-32; the 41-2/3/4 template rewrites) | 6.75 (41-30) | The enabler set is ~6 days wall-clock with three engineers (41-1a/1b/1c are mutually independent). The independents fill any spare capacity — this wave can absorb ~14 streams. |
| **1** | after 41-1b: 41-2, 41-3, 41-19 · after 41-1c: 41-9, 41-24 · after 41-1a: 41-7, 41-10 · after 41-1a+41-1b: 41-27, 41-16 · after 41-1a+41-1c: 41-5, 41-22, 41-8A (after 41-7 for the shared activity) · after 41-1a+41-30: 41-11, 41-17B | 7.5 (41-22) | Land **41-7 early** (its `FetchEventWindowActivity` + `Findings.ValidateWithContext` ring are reused by 41-8/41-11, saving 1.5 + 1.0 d) and **41-24 early** (its `review-docs.md` rewrite is inherited by 41-25/41-26, saving 0.5 d each). |
| **2** | 41-15 (after 41-2 + the Epic 40 file gate) · 41-6 (after 41-3) · 41-4 (after 41-3) · 41-13 (after 41-2) · 41-25, 41-26 (after 41-24) · 41-28 (after 41-27) · 41-14's optional `TestPlan` edge (after 41-13) · 41-23 trigger wiring | 5.9 (41-15) | The tail is thin — most of the epic completes in waves 0–1. |

Pole sum ≈ 6.75 + 7.5 + 5.9 = **≈ 20 days** wall-clock, against a 16.4-day internal critical path.
As with Epic 43: the wave barriers are partly artificial — **schedule to the dependency graph, not
the wave table**, if you have the people.

## The serialized edge-count bump (every producer story collides here)

Every producing workflow in this epic must, in the same change: declare a `WorkflowDocumentInterface`
row in `DocumentTypeRegistry.BuildSeed`, bump the **pinned edge count**
(`WorkflowInterfaceGraphTests.Declared_edge_count_is_pinned`, `WorkflowInterfaceGraphTests.cs:45`,
`HaveCount(16)` today), extend the **bidirectional `reconciled` array** in the same file (`:96-132`),
add its `ContractBindingTests.Bindings` entry, and append to
`TaxonomyDriftBuildTests.ExpectedContributingWorkflows`. That is **~24 stories (~30 bindings) all
editing the same integer literal and the same four collection literals.** Any two producer PRs in
flight at once conflict — guaranteed, not probabilistic — and a naive merge that keeps one side's
count silently un-counts the other side's edge until CI fails the build. Treat the pin bump as a
**token**: exactly one producer PR holds it at a time; every producer branch keeps its pin edit in a
final, isolated commit and rebases that commit (re-deriving `16 + n`) immediately before merge. This
is the epic's real merge-rate limiter — with unlimited engineers, merge throughput is bounded by how
fast producer PRs can serialize through the pin, so batch small producers (41-14, 41-18, 41-19,
41-21) into adjacent merge slots rather than letting each camp on the token.

## Where the schedule breaks

**1. The critical path ends in a file-level queue this epic does not control.** 41-15 edits
`SingleIssueCycleWorkflow.cs`'s merge region, and that file's per-task loop is rewritten by Epic 40's
`40-2 → 40-4 → 40-5` followed by 41-29 Phase 2. If Epic 40 slips, 41-15 (and 41-29 P2) slip with it
regardless of this epic's staffing. Everything up to 41-15's final merge is Epic-41-internal; do the
work early and hold the merge.

**2. 41-22 is the largest story and sits behind the widest gate.** 7.5 days, needing both 41-1a
(`incident-rootcause`) and 41-1c (prose postmortem), and internally three producing bindings plus a
sequencer. Its diagnosis + response-plan halves need only 41-1a — if it overruns, land those first
and let the postmortem half trail 41-1c.

**3. Two shared activities are single-owner or built twice.** 41-7's `FetchEventWindowActivity` (+
the `Findings` evidence ring) is consumed by 41-8 and 41-11; 41-5's `QueryDcbEvidenceActivity` is
shared with 41-7 and extended by 41-31. The cheap-path total assumes 41-7 lands before 41-8/41-11
and the 41-5/41-7/41-31 owners coordinate; ignoring the ordering costs ~2.5–3.5 duplicated days and,
worse, two divergent DCB read activities to reconcile later.

**4. 41-16 has a second unowned enabler nobody scheduled.** Its Phase B ("mine CI history",
same-commit flaky split) needs a **per-test CI result store** that does not exist and is in no
Wave-0 table — the plan calls it out as unowned. Phase A (6.75 d) ships without it; do not book
Phase B until that store has an owner. 41-16 also needs a type-ownership decision
(`RegressionTriage` — 41-1b's seventh type or its own) settled in its first step.

**5. Reachability is epic-wide, not per-story.** 39-17/39-19/39-20 are unlanded: every accept gate
publishes and suspends, and nothing decides end-to-end except test-side resume. The amended stories
now say which half they claim (workflow + persistence, not routing) — hold that line in review, and
do not let "done" quietly re-inflate to include Task View delivery.

**6. The `review-docs.md` rewrite is a shared fix with a first-mover owner.** 41-24 D6 owns it;
41-25/41-26 inherit. If 41-25 or 41-26 ships first it must carry the rewrite (+0.5 d) — decide the
docs-family order once, at wave-1 start.

## Cut lines

In descending order of what can be dropped without destabilizing the rest:

1. **Phase Bs**: 41-8 Phase B (retro prose narrative — also needs the 41-1a amendment cell),
   41-16 Phase B (needs the unowned CI result store), 41-5 Part B / 41-23 Phase 4 (cron wiring —
   opt-ins through 41-30). All are additive follow-ups by construction.
2. **The Wave-2 depth stories nothing hard consumes**: 41-4 (roadmap), 41-14's TestPlan edge,
   41-28's integration scenarios against a real UxSpec (its diff-subject half stands alone).
3. **The UX pair** (41-27 + 41-28) — a self-contained tail; cutting it strands nothing else.
4. **The audits' cadence** — every audit ships user-triggerable; 41-30 can land late and light the
   crons up afterwards without touching the producing bindings.
5. **Not cuttable**: 41-1a/1b/1c (twenty of the epic's stories wait on some part of them),
   41-2 → 41-15 (the epic's stated point — an explicit, checked definition of done), 41-29
   (the activation story; without it the per-role workflows stay unreachable from the issue
   pipeline).

## Assumptions

- **Efforts are the implementation plans' bottom-line totals**, which supersede the story files'
  older ranges (e.g. 41-2 is 5.0, not the story's 3–4; 41-24 is 5.0, not 3–4). Where a plan gives a
  conditional total, the cheap path is booked: 41-8A **4.25** (41-7 first; else 5.75), 41-11 **5.25**
  (41-7 first; else 6.25), 41-25 **4.5** / 41-26 **3.75** (inheriting 41-24 D6 / ops-peer reviewer;
  else +0.5 each), 41-23 **4.75** producing half (+0.5 trigger wiring after 41-30), 41-5 **5.75**
  Part A only, 41-16 **6.75** Phase A only, 41-29 **7.0** (mid of 6.5–7.5).
- One engineer per story; the parallelism above is people-parallel. The day-0 width (~14 streams) is
  an upper bound on useful staffing, not a request.
- The **2026-07-25 scheduling decision** is settled input: audits scheduled (behind 41-30),
  ceremonies user-initiated (41-5/41-7 unblocked from the seam). The stale "⛔ BLOCKED (scheduler
  seam)" banners in 41-5/41-7's plans were corrected alongside this plan; their remaining blockers
  (41-1a/41-1c) are real and retained.
- Epic 39's landed substrate (39-2..39-11, 39-15) is verified in tree by every plan; no time is
  budgeted for it. No time is budgeted for 39-17/39-19/39-20 — the epic's ACs claim the
  workflow half only.
- The 41-1a effort (4.5 d) absorbs the 41-8 Phase B lockstep amendment
  (`(scrum_master, write-retro-narrative)` — one more cell, one more prompt file) without a
  re-estimate; if 41-1a re-plans, it moves by ~+0.25, not more.

## Method note

Generated from the 34 implementation plans' `Est. Effort` tables and `Blocks / Blocked by` sections,
the epic README's Wave-0/Sequencing sections, and the 2026-07-25 scheduling decision — not
hand-guessed from the story headers. Re-run the reconciliation if the plans change materially. The
single most important scheduling fact in this epic is not a dependency at all: it is the
**serialized edge-count bump** — the one constraint that neither staffing nor reordering can
parallelize away.
