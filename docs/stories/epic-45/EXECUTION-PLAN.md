# Epic 45 — Execution Plan

Derived from the eight story implementation plans. It answers: **what can run at once**, **what is
forced serial**, and **where the schedule floor is**.

## Headline numbers

| Metric | Value |
|---|---|
| Total effort (sum of all stories) | **16 person-days** |
| Critical path | **8 days** — `45-2 → 45-3 → 45-7` |
| Speedup vs. fully serial (16 ÷ 8) | **2.0×** |
| Hard external gate | **DNS record + origin-certificate SAN coverage** (45-5 AC5/AC6) |
| Longest single story | **45-3** at 4 days, and it sits on the critical path |

The epic is **two independent halves that meet once**. The application half
(`45-0`, `45-1`, `45-2 → 45-3`) fixes what the audit found unfinished. The infrastructure half
(`45-4 → 45-5 → 45-6`) makes the app reachable. They converge only at **45-7**, which repoints the
API's emitted URLs — and which is blocked on *both* halves, because repointing a link at a host that
does not resolve, or at a page that does not exist, moves the breakage rather than fixing it.

## Dependency graph

```
  application half
  ────────────────
  45-0 (1d)   ────────────────────────────────────┐
  45-1 (1.5d) ────────────────────────────────────┤
  45-2 (2d) ──► 45-3 (4d) ────────────────────────┤
                                                   ├─► 45-7 (2d)   ← critical path ends here
  infrastructure half                              │
  ───────────────────                              │
  45-4 (1.5d) ──► 45-5 (2d) ──► 45-6 (2d) ────────┘
```

| Story | Effort | Depends on | On critical path |
|---|---|---|---|
| 45-0 Typecheck guard rail + the error it hid | 1 d | — | no |
| 45-1 The contract the tests certified wrong | 1.5 d | — | no |
| 45-2 Entry points: six URLs, catch-all, honest nav | 2 d | — | **yes** |
| 45-3 The missing account pages | 4 d | 45-2 | **yes** |
| 45-4 Container image + nginx SPA config | 1.5 d | — | no |
| 45-5 Compose service, vhost, hostname, TLS | 2 d | 45-4 | no |
| 45-6 Build, push, deploy, verify | 2 d | 45-4, 45-5 | no |
| 45-7 `Dashboard:Url` split | 2 d | 45-3, 45-5 (in practice 45-6) | **yes** |

## Waves

| Wave | Stories | Pole |
|---|---|---|
| **0** | 45-0, 45-1, 45-2, 45-4 | 2 d |
| **1** | 45-3, 45-5 | 4 d |
| **2** | 45-6, 45-7 | 2 d |

Wave-parallel wall clock is **8 days** against an 8-day critical path — they are identical here
because the graph is two short chains of unequal length running beside each other, and the longer one
(`45-2 → 45-3`, 6 days) fully absorbs the shorter (`45-4 → 45-5`, 3.5 days).

**Wave 0 is genuinely four-way parallel.** 45-0, 45-1, 45-2 and 45-4 share no files: `ci.yml` +
`tsconfig.json`, `api/pricing.ts` + `api/client.ts`, `App.tsx` + `AppLayout.tsx`, and `docker/*`
respectively. With four engineers Wave 0 is 2 days; with two it is 3.

## Where the schedule actually breaks

**1. 45-5's DNS record and certificate check are external and must start on day one.** They are the
only work in the epic with no representation in the repository and no way to accelerate. If the
mounted Cloudflare origin certificate turns out to enumerate hosts rather than being a `*.tamma.dev`
wildcard, a re-issue is an operator action with issuance lead time, and it blocks the vhost — not the
compose service, not the image, but the one thing that makes the host reachable. **45-5's step 1 and
step 2 are both day-one actions,** deliberately placed before any file is edited.

**2. 45-3 is 4 days, it is the longest story, and one component is a third of it.** `InviteAcceptPage`
must carry an invite token across an authentication boundary that may include a registration *and* an
email verification — because `POST /api/v1/orgs/invites/accept` is gated `MemberAccess`
(`Program.cs:2299-2301`) and an invited person may have no account. Every other page in the app is
either fully public or fully guarded. **This is the one place in the epic where the estimate could be
wrong in the expensive direction**, and its plan gives it three separate controls (a stated mechanism,
a token-in-href test, and a manual end-to-end in the DoD).

**3. 45-6's production deploy should wait for the application half, and that is a preference the graph
does not express.** Technically 45-6 depends only on 45-4 and 45-5. But deploying with the six entry
points still dead (45-2/45-3), the downgrade warning still silent (45-1) or verification emails still
pointing at the admin console (45-7) ships a **known-defective** customer surface — and once it is
live, "it looks shipped" starts costing real customers instead of hypothetical ones.
**Steps 1–11 of 45-6 run in parallel; only step 12, the production deploy, waits.** That is stated in
45-6's plan and repeated here because it is the kind of sequencing that gets lost when a story is
marked ready.

**4. Two stories in this epic depend on reading the server before writing the client, and one of them
is where the last bug came from.** 45-1 exists because `UpgradePlanModal.test.tsx:160` mocked a server
field that has never existed, and the suite certified it. 45-3's step 1 is *"read and write down the
four server contracts before any component exists"* for exactly that reason. **Do not let a reviewer
compress those steps** — they look like preamble and they are the story.

**5. 45-0 is one day and it should still go first.** Every other application story adds files to
`packages/dashboard-user`, and today the package does not compile under `tsc`
(`TenantAlertFeed.tsx:63`). Landing the guard rail first means four stories inherit a green typecheck
instead of each rediscovering the same error and wondering whether they caused it.

## Cut lines

If the epic must be smaller, these are the honest cuts in order:

1. **45-1 (contract fixes)** — 1.5 days. The downgrade warning is silent today and would remain
   silent; nothing regresses. It is first on the list only because it is the sole story whose absence
   leaves the product exactly as it already is. Cut it and file it, do not forget it.
2. **The `/invites/*` half of 45-3** — roughly 1.5 of its 4 days. Password reset is the half that
   decides whether a locked-out customer becomes a churn event; invites decide whether a tenant can
   have a second user. If the first cohort is single-user tenants, the invite pages can follow — but
   **45-2's placeholders must then stay**, and they say so honestly, which is the entire reason they
   exist.

**Do not cut 45-2.** It is 2 days and it is the difference between shipping a product and shipping
something that looks like one. Six of its entry points are URLs the API is *already emailing to
customers*; every one of them currently renders a blank pane.

**Do not cut 45-0.** One day, and it is the only thing that would have caught the class of defect the
audit found.

**Do not deploy without 45-7.** A reachable customer app whose verification emails still point at
`app.tamma.dev` is a signup flow that ends at a GitHub OAuth wall. That is not a partial ship, it is a
broken one.

## What this epic unblocks

Stated because three planned things are waiting and none of them says so in its own plan:

- **Story 39-19** (orchestrator chat) targets this app.
- **Story 44-6** (tracker UI) is currently planned into `packages/dashboard` *because* this app is not
  deployed — a customer-facing board in the console customers cannot reach. Epic 44's README carries
  it as open question 1; this epic answers it. **If 45-6 lands before 44-6 starts, 44-6 should be
  re-targeted** — and that re-targeting changes its estimate, so it needs to happen at 44-6's plan
  review, not after.
- **Epic 34-9's delivered value.** Plan pricing, the upgrade modal and the entitlement bar are shipped
  code no customer can open.

## Assumptions

- One engineer per story; parallelism is people-parallel. Wave 0 is four-way; the epic's wall clock at
  two engineers is ~10 days rather than 8.
- Efforts are the story plans' own numbers.
- **No time is budgeted for the three product-owner questions in the epic README.** Question 1 (does
  the GitHub install callback target the customer app or the admin console?) is decided inside 45-7 by
  default; questions 2 (hostname naming) and 3 (crawlability) have defaults taken and are reversible.
  Only question 2 would reshape the epic, and only if answered late.
- The audit's finding that all 25 API endpoints exist is treated as settled. **If a story discovers
  otherwise, that is a scope change, not a bug** — the audit checked every route registration in
  `Program.cs` on 2026-07-27.
