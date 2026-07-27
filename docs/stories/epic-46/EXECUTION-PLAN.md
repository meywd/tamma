# Epic 46 — Execution Plan

Derived from the four story implementation plans. It answers: **what can run at once**, **what is
forced serial**, and **where the schedule floor is**.

## Headline numbers

| Metric | Value |
|---|---|
| Total effort (sum of all stories) | **13.5 person-days** |
| Critical path | **7 days** — `46-1 → (46-2 ∥ 46-3)` |
| Speedup vs. fully serial (13.5 ÷ 7) | **≈1.9×** |
| External gate | **Epic 45** — 46-3 merges regardless, but customers reach it only after 45-5/45-6; 46-3's merge itself waits for 45-0 (CI typecheck) and 45-2 (shared nav files) |
| Longest single story | **46-1** at 4 days, on the critical path |

The epic is **two backend stories feeding two UI stories.** The backend pair (46-0 listing seam,
46-1 settings store + resolver) is nearly independent — they meet only in two endpoint files — and
the UI pair is fully independent of each other (different apps, zero shared files, by decision).

## Dependency graph

```
  backend
  ───────
  46-0 (3.5d) ──┬────────────────────────┐
                │ (shared endpoint files;│
                │  land 46-0 first)      ├─► 46-2 (3d)   platform UI, packages/dashboard
  46-1 (4d) ────┴────────────────────────┤
                                         └─► 46-3 (3d)   tenant UI, packages/dashboard-user
                                              ▲
  epic 45: 45-0 (CI typecheck) + 45-2 (nav) ──┘   merge gates
  epic 45: 45-4/45-5/45-6 ────────────────────►   reachability only, not a merge gate
```

| Story | Effort | Depends on | On critical path |
|---|---|---|---|
| 46-0 Live model listing seam | 3.5 d | — | no (absorbed by 46-1) |
| 46-1 Persisted model selection | 4 d | 46-0 soft (file ordering) | **yes** |
| 46-2 Platform admin UI | 3 d | 46-0, 46-1 | **yes** (tied with 46-3) |
| 46-3 Tenant UI | 3 d | 46-0, 46-1, 45-0, 45-2 | **yes** (tied with 46-2) |

## Waves

| Wave | Stories | Pole |
|---|---|---|
| **0** | 46-0, 46-1 | 4 d |
| **1** | 46-2, 46-3 | 3 d |

Wave-parallel wall clock is **7 days**. With one person it is the serial 13.5.

**Wave 0 parallelism is real but has one seam:** both stories touch
`ProviderAdminEndpoints.cs` (46-0 creates it; 46-1 adds mutation routes) and
`ProviderCredentialEndpoints.cs` (46-0 adds the tenant models route; 46-1 adds the tenant
model-settings routes). The plans put 46-1's endpoint work last in its sequence precisely so 46-0's
files exist by then. Two people can start both on day one; the endpoint merge lands in 46-1's
final day.

**Wave 1 parallelism is total.** Different packages, different apps, no shared files — 46-2's DoD
greps for zero `packages/dashboard-user` changes and 46-3's for zero `packages/dashboard` changes.

## Where the schedule actually breaks

**1. Epic 45's position decides whether 46-3 is worth starting in Wave 1.** The merge gates
(45-0, 45-2) are small early Epic-45 stories, but if Epic 45 has not started at all, 46-3 would
be editing `App.tsx`/`AppLayout.tsx` ahead of 45-2's rework of the same lines — the wrong order.
If Epic 45 is idle when Wave 1 opens, run 46-2 first and hold 46-3; the epic's value for the
platform owner does not wait on the tenant surface.

**2. 46-1's defaults-refresh task (AC7) needs provider keys.** Verifying shipped model ids
against live lists requires a real key per keyed provider (or the product owner running the
checks). Keys the platform does not hold → the verification records "unverifiable — no key",
which is an acceptable outcome but should be known on day one, not discovered on day four.
Ask the product owner for the key inventory when the epic starts.

**3. The Z.ai re-check (46-0) is a five-minute task with a decision attached.** If a models route
exists, the descriptor gets it and the UIs' free-text fallback loses a row; if not, nothing
changes. Do it first, not last — it is the only wire fact the survey could not settle.

**4. The precedence restructure (46-1 step 3) is the riskiest edit in the epic** — it rewrites the
resolution path every LLM call rides. Its guard is test 16 (golden comparison of
`LoadProviderConfig` output over all 15 keys for a no-row install). Write that test BEFORE the
restructure, on the current code, and carry it across — it is cheap and it converts the risk into
a red/green signal.

## Sequencing recommendation

1. Day 0: start 46-0 and 46-1 in parallel (two people) or 46-0 → 46-1 (one person, seam-free).
   Same day: Z.ai re-check; ask for the key inventory (AC7).
2. 46-1 writes its golden test 16 against the current code before restructuring.
3. Wave 1: 46-2 immediately; 46-3 when 45-0 + 45-2 are merged (check Epic 45's board first —
   they are its two cheapest stories and may already be done).
4. Retro note for the finding: after 46-1 lands, the finding's Phase-2 appendix (added with this
   epic) should have its "settings store" line ticked — keep the finding's ledger honest.
