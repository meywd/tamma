# Implementation Plan — Story 42-8 (split index): Feature-Flag & Deploy-Control Tools

Story 42-8 is **superseded by 42-8A + 42-8B** and its file is the split index, not a buildable story. This
plan is correspondingly a **coordination plan**: it owns the decisions that span the two halves and nothing
else. The buildable plans are:

- **[`implementation-plan-42-8a.md`](./implementation-plan-42-8a.md)** — Feature-Flag / Config-Toggle Tool
- **[`implementation-plan-42-8b.md`](./implementation-plan-42-8b.md)** — Deploy-Control Tool

## Reconciled scope — differs from the story file

**Epic 42 was reconciled against Epic 43 on 2026-07-25.** The split index's own text — the "share no
implementation" table and the sequencing note — survives **unchanged**; the reconciliation validated it
rather than disturbing it. Three consequences ripple through from the halves:

| Area | Reconciled |
|---|---|
| The index's comparison row **"Class discriminator — environment (prod vs non-prod) resolved from the binding / target (prod vs staging) + verb"** | 42-2 is **deleted**, so there is no binding. Both halves now resolve their declared set from **deployment configuration** (42-8A D3, 42-8B D3), and the *classification* half is gone entirely — Epic 43's catalog owns it. The **containment** half (the model picks a key from a closed declared set; undeclared is refused) survives in both. |
| The index's implicit assumption that both halves carry gating code | **Neither does.** Gating sections are stripped from both; Epic 43's Seam B governs them with zero bespoke code in either half. |
| Nothing in the index anticipated it | **42-8B's Scope 4 / AC7 is DELETED** — the requirement that the always-escalate class bind independently of the dial and of the tool grant. Epic 43 §5 absorbs `AlwaysEscalate` as an `AlwaysHuman` floor composed with `max()`. See 42-8B's plan, D6/U1. |

The index's central argument is **strengthened**, not weakened: the two halves now share even less. Post-
reconciliation their only common ground is the Wave-1 envelope (42-1 / 42-4 / 42-5) that every family
inherits anyway, plus — for 42-8B alone — the `WaitForToolOperationActivity` shared with **42-7**.

## Scope & Deliverable

This plan delivers no code. It records four cross-half decisions so neither half makes them unilaterally:

1. **The shared-asset ledger** — what `WaitForToolOperationActivity` costs, and which story pays it (C1).
2. **The order within Wave 3**, and why it may need to change (C2).
3. **The one structural gap both halves inherit** — the deleted `ConfigJson` — and the instruction not to
   solve it twice (C3).
4. **The catalog-authoring hand-off** to Epic 43 (C4).

## Pre-Reading

- `docs/stories/epic-42/story-42-8/42-8-feature-flag-deploy-control-tools.md` — the split index (its "What changed and why" table is the argument this plan coordinates)
- The two half plans above, and **`docs/stories/epic-42/story-42-7/implementation-plan.md`** — which shares the wait machinery with 42-8B and states the same D5/D6/D7 once
- `docs/stories/epic-42/README.md` — the verdict table and the Wave-3 sequencing note
- `docs/stories/epic-43/README.md` — Seam B; §5 "Absorbing the existing always-escalate list"; **Storage** (`action_assignments`' columns, the evidence for C3)

## Coordination Decisions

- **C1 — the shared wait machinery is paid for ONCE, across three stories, and the ledger is here.** Two of
  the epic's families suspend: **42-8B** and **42-7**. Both need the same five artefacts:
  `WaitForToolOperationActivity` (`Tamma.Activities/ToolExecution/`, generic over `{ kind, operationId }`,
  credential-free), `LifecycleBookmarks.ForToolOperation` (**which does not exist** — verified: the builders
  are `Compose` `:38-48`, `ForStageGate` `:55`, `ForDecisionSession` `:66`, `ForDocumentInput` `:82`), its
  entry in `LifecycleBookmarks.CanonicalSuspendActivities` (**exactly two entries today**, `:98-105`, and the
  registration is enforced by `ResumableStandardStructuralTests` clause (b) at `:158-198`), the authenticated
  tenant-folded completion callback endpoint, and the `ToolOperationWaitTests` Testcontainers suite.
  **Whichever of 42-8B / 42-7 lands first ships all five; the second adds only its operation `kind`.**
  42-8A shares none of it — it has no suspend path at all, which is the whole reason the split exists.

  | Story | Standalone | If the sibling landed first |
  |---|---|---|
  | 42-8A | 4.0 d | — (shares nothing) |
  | 42-8B | 6.0 d | ~4.0 d |
  | 42-7 | 6.25 d | ~4.25 d |

  **Budget 42-8B + 42-7 at ~10.25 d together, never ~12.25 d.** Both half plans state both figures and
  neither assumes it is first.

- **C2 — the stated order is `42-9 → 42-8A → 42-8B → 42-7`, and it is now less certain than when it was
  written.** The rationale still holds for 42-8A: it needs **nothing** engine-side, so it is not gated on the
  wait-activity chain and is the cheapest real capability in the wave. What changed: **42-9 is materially
  harder than the order assumed**, because its entire configuration source — 42-2's `ConfigJson`, which
  carried its base URI, host allowlist, method allowlist, auth mode, header allowlist and caps — was deleted,
  and its S1–S11 containment matrix was always the bulk of the work. If 42-9 slips, **42-8A should ship
  first**: it is independent, small, and delivers the incident kill-switch Epic 41's 41-22 needs.
  **Recommended order, restated: `42-8A → 42-9 → 42-8B → 42-7`**, with the caveat that 42-8B-before-42-7
  remains right either way (42-8B's Epic 41 demand is higher and it pays the shared machinery once).

- **C3 — both halves inherit the same missing store; DO NOT solve it twice.** 42-2's `tool_bindings.ConfigJson`
  is deleted and Epic 43's `action_assignments` stores **policy only** — a threshold plus three nullable
  columns — with no config blob. So 42-8A's environment map + kill-switch list + sensitive-key list, and
  42-8B's target map, both lose their home. Each half's plan moves its own map to **deployment configuration
  via `IOptions`, platform-scoped, validated fail-loud at startup** (42-8A D3, 42-8B D3). That is the right
  local answer and it fully serves single-user. **The residual is the same in both: SaaS loses per-tenant
  maps** (42-8A G1, 42-8B G1). The identical gap also appears in **42-4** (per-principal secret names) and,
  most severely, in **42-9** (per-principal endpoint bindings, without which that tool has no destination at
  all). **Four stories, one missing store.** The instruction to both halves — and to 42-4 and 42-9 — is:
  **do not invent a per-tenant tool-config table inside a family story.** That is precisely the duplication
  the Epic 43 reconciliation deleted. It must be decided once, at epic or Epic 43 level, as one of: extend
  Epic 43's storage with a per-action config blob; file a single new Epic 43 story for a tool-config store;
  or accept platform-scoped tool configuration permanently. **Open, and this plan does not decide it.**

- **C4 — catalog rows are admin data authored per half, and neither half writes gating code.** Each half
  authors its Epic 43 catalog entries as configuration, not code: `tool:feature_flag_read` /
  `tool:feature_flag_write` (42-8A) and `tool:deploy_status` / `tool:deploy_control` (42-8B). Two
  recommendations travel with them rather than being left implicit: the **read** halves are safe to enable
  immediately and unconditionally (their executors physically cannot mutate — that guarantee holds with no
  catalog row at all); the **write** halves must **not** be enabled in any deployment before Epic 43 Story 9's
  Seam B is live, because stripping the gating sections means they are auditable and secret-bound but
  ungoverned until then. `tool:deploy_control` additionally wants an always-escalate entry for prod, which
  Epic 43 §5 composes as an `AlwaysHuman` floor that the catalog cannot lower.

## Test Plan

None of its own. Each half's plan carries its suite. **One cross-half assertion is owned here**, and belongs
to whichever story lands second: a test proving that `LifecycleBookmarks.CanonicalSuspendActivities` contains
`WaitForToolOperationActivity` **exactly once** and that `ResumableStandardStructuralTests` stays green — the
cheapest guard against the shared machinery being built twice under two names.

## Definition of Done

This index has no acceptance criteria of its own. It is done when both halves are done and C1–C4 were
honoured: the wait machinery exists once, the order was chosen deliberately, no half minted a tool-config
table, and the catalog rows were authored with C4's enable/do-not-enable guidance.

## Blocks / Blocked by

- **Blocked by — 42-1, 42-4, 42-5** (both halves, hard).
- **Blocked by — Epic 43 Stories 3 / 5 / 9** for governance of both write halves (not for shipping the
  capability).
- **Couples 42-8B to 42-7** through the shared wait machinery (C1). 42-8A is coupled to neither.
- **Blocks — Epic 41:** `deployment-pipeline` (gradual rollout, promotion, rollback), **41-22** (incident
  kill-switch **and** rollback — it needs both halves), **41-24** (release-notes trigger keyed off a promote),
  41-29's `infra` `TaskKind` agent path.
- **Open, shared with 42-4 / 42-7 / 42-9 — C3's missing tool-config store.**

## Risks & Mitigations

- **The shared machinery gets built twice** because two stories each read their own plan and neither reads
  this one. Mitigation: C1's ledger, the precondition-check step at the head of both 42-8B's and 42-7's
  plans, and the single-registration assertion above — `CanonicalSuspendActivities` is build-gated, so a
  duplicate type surfaces immediately rather than at review.
- **Both halves independently invent a config store** to replace `ConfigJson`. Mitigation: C3 forbids it
  explicitly and names the three sanctioned resolutions; both half plans record the gap as G1 rather than
  filling it.
- **The write halves ship enabled before Epic 43's gate exists.** Mitigation: C4's explicit
  do-not-enable guidance, repeated in both half plans' Blocks / Blocked by, plus the read/write split, which
  means the useful read capability can be enabled immediately without waiting.
- **The order is treated as fixed.** Mitigation: C2 states the condition under which it should change and
  what to do instead.

## Est. Effort

**Coordination only — 0.25 d** (this plan; the C3 decision itself is not costed here because it is not
this story's to make).

**Roll-up for the pair, per C1's ledger:** 42-8A **4.0 d** + 42-8B **6.0 d standalone / ~4.0 d if 42-7 is
first**. With 42-7 in the picture the three suspend-and-flag stories total **~14.25 d**, not the ~16.25 d
that summing standalone figures would give.

*(The 42-8 story file itself carried no `Estimated Effort` section — the split index deferred entirely to its
two halves. One has been added to it recording this roll-up, so the epic's effort table is complete.)*
