# Implementation Plan — Story 43-10: Epic 42 Spec Reconciliation

## Scope & Deliverable

A documentation-only story. When it is done, an implementer who opens any Epic 42 **story file** reads the reconciled scope directly, with no boxed note in a third document contradicting it, and every Epic 42 implementation plan's "Reconciled scope — differs from the story file" table is **empty or removed** because the story file no longer differs.

Concretely: 42-1 rewritten (`ToolDescriptor(RequiredSecret, Suspends)`, `Descriptor` abstract not DIM, ACs 2/4/5/8 deleted); 42-5 narrowed off `InlineToolLoopResult.ToolCalls`; 42-6 Part B gains the catalog-binding prerequisite **and** the flag that it may be unimplementable as specified; 42-8B's Scope 4 / AC7 deleted; gating sections stripped from 42-7 / 42-8A / 42-8B / 42-9; the `ConfigJson` gap carried into the four affected story files as an **open** question; Epic 42's four self-contradicting README passages corrected in place; and one decision record that names what the reconciliation itself broke.

**No code changes. No further story deletions.**

## Pre-Reading

- `docs/stories/epic-43/story-43-10/43-10-epic-42-spec-reconciliation.md` — this story (ACs are source of truth)
- `docs/stories/epic-43/README.md` — "Epic 42 reconciliation" (the verdict table) and, decisively for AC4, "Drift prevention → Holes the mechanisms honestly cannot close": *"MCP is one coarse member with no drift signal"*
- `docs/stories/epic-42/README.md` — the boxed "⚠️ RECONCILED AGAINST EPIC 43 (2026-07-25)" verdict table **and** the boxed "⚠️ Gap opened by the 42-2 deletion — `ConfigJson` has no replacement" block. Both already written; this story propagates them.
- `docs/stories/epic-42/story-42-1/42-1-tool-contract-registry-evolution.md` — **`:83-146`** (the `ToolDescriptor` record, the fail-safe default, the `PermissionClass`-is-the-maximum paragraph, and `:135-146` the DIM justification with its three caveats); **`:214-256`** (ACs 1–10, of which 2/4/5/8 die); `:66-82` §0 (`SecretPurpose` move — survives); `:167-182` §3 (`Register`/`Unregister` — survives)
- `docs/stories/epic-42/story-42-1/implementation-plan.md:3-18` — the already-written reconciled table and **D2** ("`Descriptor` is a plain abstract interface member, not a DIM"). The story rewrite must land on exactly this shape.
- `docs/stories/epic-42/story-42-5/42-5-tool-use-dcb-audit.md:39-40,128-133,158` — the always-empty `ToolCalls` finding, Scope §5, AC8
- `docs/stories/epic-42/story-42-5/implementation-plan.md:13-14` — the deletions already recorded
- `docs/stories/epic-42/story-42-6/42-6-mcp-integration.md:96-206` (§0 port-vs-adopt, Part A, Part B), `:207-241` (ACs), `:258-276` (Dependencies)
- `docs/stories/epic-42/story-42-8/42-8b-deploy-control-tool.md:76-79` (Scope 4), `:141-143` (AC7), `:213-214` (the risk text depending on AC7), `:112-115` (audit — survives, minus the always-escalate clause)
- `docs/stories/epic-42/story-42-7/42-7-cloud-vps-resource-tool.md:72-87` (per-call class table + `Describe` seam), `:129`, `:164` (42-3 decision id), `:193-195` (42-3 dependency), `:210-217` (stage-1-filter risk, now moot)
- `docs/stories/epic-42/story-42-8/42-8a-feature-flag-tool.md`, `docs/stories/epic-42/story-42-9/42-9-authenticated-http-external-api-tool.md` — same treatment
- `docs/sprint-status.yaml:552` (39-23 superseded), `:646` (42-2 superseded), `:647` (42-3 superseded) — **already applied; do not redo**
- `apps/tamma-elsa/src/Tamma.Api/Services/Agents/IInlineToolLoopRunner.cs:112-114,133-135` — the always-empty `ToolCalls` documentation, cited by AC3

## Design Decisions

- **D1 — Rewrite the story files; do not add a second banner.** The current state is the worst of three: plans reconciled, story files not, and a boxed note in a third document. An implementer's natural entry point is the story file, because that is where the ACs live. Adding a banner to each story file would produce *four* layers. The story files are edited in place, and the verdict box in the README stays as the audit trail of **why** — not as the only place the truth is written.

- **D2 — The DIM dies as a second-order consequence, and that must be stated as a consequence, not asserted as a preference.** The default-interface-member design (`42-1:83-86,135-146`) exists for exactly one purpose: to return `new ToolDescriptor(Native, Destructive, 100, null, false)` — a fail-safe value composed **entirely** of `Category`, `PermissionClass` and `AutonomyFloor`. Remove those three and the DIM has nothing to fail safe to: `SecretRequirement` has no safe default (a tool needs a specific secret or none) and neither does `Suspends`. So `Descriptor` becomes a plain abstract member and the three caveats at `:135-146` go with it. **This is strictly stronger, and the rewrite must say so**: an abstract member is enforced by the compiler, whereas ACs 2/4/8 were three tests compensating for a language feature that silently succeeds.

- **D3 — Deleting an AC is deleting a guarantee; each deletion states what replaces it.** ACs 2, 4, 5 and 8 die. The rewrite records, per AC: **AC2** (DIM fail-safe regression test) → the compiler; **AC4** (`Type.GetInterfaceMap` startup drift test) → the compiler, plus Epic 43 Story 4's boot validator, which checks the *catalog* binding rather than the *declaration*; **AC5** (`AutonomyFloor` range validation) → `AutonomyDial.IsValidThreshold`, one place, Epic 43 Story 1; **AC8** (the `Mock<IToolExecutor>` returns-null hazard) → nonexistent, because a mock of an interface with an abstract member has no inherited implementation to return `default` from. A deletion with no stated replacement reads as a lost requirement and will be re-litigated.

- **D4 — 42-5 keeps the invocation trio and loses the field dependency; the two must not be conflated.** `TOOL.INVOKED` / `SUCCEEDED` / `FAILED` answer *"what did the tool do"*; `ACTION.GATE.*` answers *"was it permitted"*. Merging them would lose one question's worth of audit. What 42-5 loses is (a) the governance events, which duplicate `ACTION.GATE.*`, and (b) Scope §5's requirement to populate `InlineToolLoopResult.ToolCalls` plus AC8 — the field is documented always-empty at `IInlineToolLoopRunner.cs:133-134` and `:112-114`, and reviving `AGENT.TOOL_CALL.*` from it would create a **second durable tool-call family** beside `TOOL.*`, which is the exact duplication class this reconciliation exists to remove. The documentation half of §5 survives (the invariant is still worth writing down).

- **D5 — 42-6 Part B's prerequisite is added AND flagged as possibly unimplementable. Adding it without the flag would be dishonest.** The verdict says an MCP tool entering the registry must resolve to a catalog entry. But Epic 43's own README lists, among the holes it cannot close: *"MCP is one coarse member with no drift signal. Adding a server, or a tool on an existing server, changes nothing in the catalog."* Those two statements are in tension. A per-MCP-tool catalog binding needs per-tool catalog **members**, which do not exist and which no drift harness can verify. So the prerequisite resolves to one of two unstated options: **(i)** every MCP tool binds to the single coarse `tool:mcp_invoke` member — a binding that governs nothing finer than today, making the "prerequisite" ceremonial; or **(ii)** MCP tools mint catalog members dynamically — which makes the catalog **open** and breaks the closed-vocabulary premise the epic rests on (and would defeat the count pins and keyset-equality tests in Story 43-2 outright). 42-6 Part B is marked **blocked on that fork**, the fork is added to Epic 42's open questions, and this plan does **not** pick a side — it is entangled with Epic 42's open question 2 (tenant-scoped MCP servers) and Epic 43's open question 1, which are the same question from two directions.

- **D6 — 42-8B Scope 4 / AC7 is deleted with its safety argument written out in full.** The requirement (`:76-79`, `:141-143`) was that the Epic 39 always-escalate class bind **independently of** the dial and of 42-3's grant, so a satisfied tool authorization would **not** satisfy the class. In operational terms that is two gates, two audit ids and two human decisions for one production deploy — and the second decision has no distinct question to ask, because it is the same deploy. Deleting it removes no safety, and the story must say why: `effect:deploy.promote-prod` and `effect:deploy.rollback` ship `AlwaysHuman`; `IActionAuthorizationLedger` makes one grant cover the correlation; the existing business-mode predicate is **OR'd, not replaced** (43-9 AC11 / D10). `WaitForToolOperationActivity` is untouched — it is an **operation** wait, not an **authorization** wait, and confusing the two is how the double gate was specified in the first place.

- **D7 — "Strip the gating sections" is a scalpel, not a delete key.** Each of 42-7 / 42-8A / 42-8B / 42-9 keeps: capability declaration, provider abstraction, secret binding, `TOOL.*` audit, never-throw contract, Epic 41 consumer mapping. Each loses: `AutonomyFloor`, the `PermissionClass` descriptor field, the `ToolInvocationFacts Describe(argumentsJson)` stage-2 seam, and every 42-3 reference. **42-8A's rule survives** — "the prod-vs-non-prod class is resolved from the binding, never asserted by the model" is a real security property; it is re-expressed as a **server-sourced `AutonomyQuery.Target`** (43-9 AC1). The `Describe` seam's *insight* (the per-call operation and target must reach the gate) survives as `AutonomyQuery.Operation`/`.Target`, which are **audit inputs only, never policy keys** — admitting them as keys would make the catalog open.

- **D8 — The `ConfigJson` gap is propagated as an OPEN question, and the plan says explicitly that it must not be silently re-closed.** The four plans already moved their configuration to `IOptions`, which is correct for single-user and single-tenant. But `IOptions` is **process-wide**: two tenants cannot have different deploy targets or different flag environments. 42-2 would have supported that; deleting it lost it. The four story files each state the loss in their own terms (42-9 destination/host allowlist; 42-8A environment map; 42-8B target map; 42-4 secret-name override) and each carries the same open question. **A story file that presents `IOptions` as the answer, without the SaaS caveat, has re-closed the question by omission** — and that is the specific failure this decision guards against. A cross-check asserts the four wordings agree with Epic 42's README block.

- **D9 — Transplanted analysis is preserved with attribution, and its absence is a failure of this story.** 42-3's two-stage siting analysis and its effective-ceiling insight (filtering on the raw descriptor class would have made **every** Wave-3 write tool unreachable — the model would never emit the call, so stage 2 would never fire, and the whole catalog would be inert) is load-bearing for Epic 43 Seam B. 42-2's two-scoping resolution prose is load-bearing for Epic 43 Story 5. Both story files are already deleted, so if the transplant is missing it is **gone**. AC10 makes verifying the transplant part of this story, with filing it as the remedy.

- **D10 — This story lands after 43-9's plan is settled, not after 43-9 is implemented.** It is a spec edit; it needs the target specs to be **stable**, not shipped. A `ToolDescriptor` rewrite that points at `ActionRisk` and `AutonomyQuery.Target` is wrong the moment either is renamed.

## Corrections to the design

1. **design.md's Story 11 lists "Amend 39-23 to drop `minAutonomyLevel`" as work.** Verified: 39-23 is already marked `superseded` in `docs/sprint-status.yaml:552` with the full rationale (including that its premise — that the always-escalate gate is enforced — was **false**, since `AcceptanceGuardrails.TryPreGate` has zero production call sites). The directory `docs/stories/epic-39/story-39-23/` still exists. So the remaining work is **not** an amendment but a superseded banner on the story file matching the sprint-status row (AC8), and removing Epic 42's Dependencies citation of it (AC9).

2. **design.md's Story 11 lists deleting 42-2 and 42-3 as work.** Both directories are **already absent** and both sprint-status rows already say `superseded`. Do not redo; verify (AC8's grep pass) and move on.

3. **design.md did not anticipate that every Epic 42 implementation plan would already carry a "Reconciled scope" table.** They do (`story-42-1/implementation-plan.md:3-18` and equivalents in 42-4/5/6/7/8A/8B/9). This changes the shape of the work substantially: the reconciled scope is **already written and reviewed** — this story's job is largely to **move it from the plan into the story file** and then empty the plan's delta table. That makes AC2 the cheapest correctness check in the story and the estimate realistic at 2 days.

4. **The verdict table says 42-6 Part B "gains a catalog-binding prerequisite" with no caveat.** Epic 43's own README contradicts that being implementable at any useful granularity (D5). This plan treats the omission as a defect in the verdict, not in the stories, and fixes it by flagging the fork rather than by writing a prerequisite that cannot be satisfied.

5. **The `ConfigJson` gap is described in Epic 42's README as "a defect in the reconciliation, not in the stories".** That framing is correct and must be carried through: the four story files should not read as though they were always deployment-scoped. They lost something.

## Implementation Steps

1. **MODIFY `docs/stories/epic-42/story-42-1/42-1-tool-contract-registry-evolution.md`** (AC1, D2/D3) —
   - `:83-146` → `ToolDescriptor(SecretRequirement? RequiredSecret, bool Suspends)`; delete `enum ToolCategory`, `enum ToolPermissionClass`, the fail-safe-default paragraph, the `PermissionClass`-is-the-maximum paragraph, and the whole "Why a default interface member (verified sound, with three caveats)" block. Replace with two sentences: `Descriptor` is a plain abstract interface member; every implementer declares one or does not compile.
   - Preserve `:66-82` §0 (`SecretPurpose` move) and `:167-182` §3 verbatim. Preserve the `Suspends` wording verbatim (`:107-119`) — 42-7 §4 and 42-8B §6 depend on that sentence.
   - `:214-256` → delete ACs 2, 4, 5, 8; renumber; add the per-AC replacement note from D3.
   - Add a short "Superseded by Epic 43" subsection naming each dropped field and its new home (`AutonomyFloor` → `ActionDescriptor.DefaultMinAutonomy` + `action_assignments.min_autonomy`; `PermissionClass` → `Tamma.Core/Actions/ActionRisk`; `Category` → the registry's registration path).

2. **MODIFY `docs/stories/epic-42/story-42-1/implementation-plan.md`** (AC2) — the "Reconciled scope — differs from the story file" table at `:3-18` becomes empty or is removed, with one line recording that 43-10 applied it and on what date. If any row cannot be emptied, the step-1 edit was incomplete — **fix step 1, do not keep the row**.

3. **MODIFY `docs/stories/epic-42/story-42-5/42-5-tool-use-dcb-audit.md`** (AC3, D4) — delete Scope §5's "populate `InlineToolLoopResult.ToolCalls`" clause (`:128-133`) and AC8 (`:158`); keep the documentation half of §5; remove the governance events with a pointer to `ACTION.GATE.*`; add the one-line note citing `IInlineToolLoopRunner.cs:133-134` and the second-durable-family reason. **MODIFY** its plan's delta table per AC2.

4. **MODIFY `docs/stories/epic-42/story-42-6/42-6-mcp-integration.md`** (AC4, D5) — add to Part B (`:135-206`) the catalog-binding prerequisite (unclassified ⇒ rejected at registration; answers Epic 42 PO question 1 as **deny**), and **immediately beside it** the unresolved fork: coarse-member binding vs dynamically-minted members, with the quote from Epic 43's README and the consequence for the closed-vocabulary premise. Add a matching AC in the Part B block (`:219-241`) and mark Part B blocked on the fork in Dependencies (`:258-276`). **MODIFY `docs/stories/epic-42/README.md`** — add the fork to Open design questions.

5. **MODIFY `docs/stories/epic-42/story-42-8/42-8b-deploy-control-tool.md`** (AC5, D6) — delete Scope 4 (`:76-79`), AC7 (`:141-143`) and the AC7-dependent risk text (`:213-214`); renumber; add the preserved-safety paragraph (ships `AlwaysHuman`; ledger covers the correlation; business-mode predicate OR'd not replaced, cross-referencing 43-9 AC11) and the note that `WaitForToolOperationActivity` is an **operation** wait, unaffected.

6. **MODIFY `docs/stories/epic-42/story-42-7/42-7-cloud-vps-resource-tool.md`, `story-42-8/42-8a-feature-flag-tool.md`, `story-42-8/42-8b-deploy-control-tool.md`, `story-42-9/42-9-authenticated-http-external-api-tool.md`** (AC6, D7) — strip the gating sections per the per-file line references in Pre-Reading. Keep capability, provider abstraction, secret binding, `TOOL.*` audit, never-throw, Epic 41 mapping. Re-express 42-8A's binding-resolved-class rule as a server-sourced `AutonomyQuery.Target`.

7. **MODIFY the same four files plus `story-42-4/42-4-tool-credential-secret-binding.md`** (AC7, D8) — add an identically-shaped "Configuration: open question" block to 42-4, 42-8A, 42-8B and 42-9: what configuration is needed, that its 42-2 destination was deleted, that the interim is `IOptions` (deployment-scoped), and that **per-tenant tool configuration in SaaS is genuinely lost and unresolved** (narrow `tool_configuration` table vs accept deployment scope). Wording agrees with Epic 42's README block.

8. **MODIFY `docs/stories/epic-42/README.md`** (AC9) — correct the four self-contradicting passages in place: the tool-contract field table (drop `Category`/`PermissionClass`/`AutonomyFloor` rows); the permission/autonomy/secret model (remove `tool_bindings` and the live two-stage 42-3 gate; point at Epic 43 Storage + Seam B); Sequencing Wave 1 (remove 42-2/42-3; re-describe the wave); Dependencies (remove 42-3's live-read-seam obligation and the 39-23 citation; note the resolver widening is Epic 43's, done once). Keep the verdict box.

9. **VERIFY (AC8)** — `grep -rn "42-2\|42-3\|39-23" docs/stories/epic-42/` and confirm every hit is a superseded pointer or a credited transplant, never a live dependency. **MODIFY `docs/stories/epic-39/story-39-23/`**'s story file with a superseded banner matching `sprint-status.yaml:552` (or delete the directory if the epic owner prefers; the banner is the lower-risk default because the story's *analysis* of why the always-escalate gate is unenforced is worth keeping).

10. **VERIFY (AC10, D9)** — confirm `docs/stories/epic-43/story-43-9/` carries 42-3's siting analysis and effective-ceiling insight with attribution, and `docs/stories/epic-43/story-43-5/` carries 42-2's two-scoping prose. If either is missing, **file it into that story** as part of this one.

11. **CREATE/MODIFY `.dev/decisions/epic-43-action-catalog-design.md`** (AC11) — the twelve duplications, the per-story verdicts, the four transplants, and — the part that is easy to omit — **the two things the reconciliation itself broke**: the `ConfigJson` loss and the MCP catalog-binding fork. A decision record that lists only benefits is not a decision record.

## Test Plan

There is no code, so "tests" are mechanical verification passes. Each is cheap and each catches a real failure mode of a documentation edit.

- **Delta-table emptiness (AC2).** For each of the 8 surviving Epic 42 stories, the plan's "Reconciled scope — differs from the story file" table is empty or removed. **A non-empty table is a failed story-file edit**, not an acceptable residue. This is the single highest-value check here.
- **Grep pass for dead references (AC8).** `42-2`, `42-3`, `39-23`, `tool_bindings`, `tools:manage`, `ToolsManage`, `AutonomyFloor`, `ToolPermissionClass`, `ToolCategory`, `ToolInvocationFacts`, `minAutonomyLevel` across `docs/stories/epic-42/`. Every hit is a superseded pointer or a credited transplant.
- **AC-numbering integrity (AC1, AC5).** 42-1 and 42-8B renumber after deletions; every internal cross-reference to a renumbered AC — in the same file, in its plan, and in sibling stories — still resolves. Deleted-AC replacements (D3) are present.
- **`ConfigJson` wording cross-check (AC7).** The blocks in 42-4 / 42-8A / 42-8B / 42-9 say the same thing as Epic 42's README block, and none presents `IOptions` as the answer without the SaaS caveat.
- **MCP fork present and unresolved (AC4).** 42-6 Part B states both the prerequisite **and** the fork, cites Epic 43's "one coarse member with no drift signal", and is marked blocked. Epic 42's Open design questions lists it.
- **Transplant presence (AC10).** Epic 43 Stories 5 and 9 carry the 42-2 / 42-3 analysis with attribution.
- **README self-consistency (AC9).** Read `docs/stories/epic-42/README.md` end to end as an implementer with no knowledge of the verdict box; nothing asserts the pre-reconciliation model as current.
- **Preserved-verbatim check (AC1).** 42-1's §0, §3 and the `Suspends` paragraph are byte-identical to their pre-edit text (42-7 §4 and 42-8B §6 depend on the last one).

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — 42-1 rewritten, ACs 2/4/5/8 deleted | 1 | Preserved-verbatim check; AC-numbering integrity |
| 2 — 42-1's plan delta table empty | 2 | Delta-table emptiness |
| 3 — 42-5 narrowed off `ToolCalls` | 3 | Grep pass; delta-table emptiness |
| 4 — 42-6 Part B prerequisite **and** fork | 4 | MCP fork present and unresolved |
| 5 — 42-8B Scope 4 / AC7 deleted | 5 | AC-numbering integrity; grep pass |
| 6 — gating stripped from the four families | 6 | Grep pass (`AutonomyFloor`, `ToolInvocationFacts`, `42-3`) |
| 7 — `ConfigJson` carried as open | 7 | `ConfigJson` wording cross-check |
| 8 — no live dependency on a deleted story | 9 | Grep pass |
| 9 — Epic 42 README consistent | 8 | README self-consistency read-through |
| 10 — transplants preserved | 10 | Transplant presence |
| 11 — decision record incl. what broke | 11 | Reviewer check: both broken things named |

## Risks & Mitigations

- **Silent re-closure of the `ConfigJson` question.** The most likely failure of this story: four plans already say `IOptions`, so the path of least resistance is to write "configuration comes from `IOptions`" in four story files and move on — which answers a product question by omission. Mitigation: D8, AC7 and a dedicated wording cross-check, all pointing at the same README paragraph as the canonical text.
- **Writing 42-6 Part B's prerequisite as if it were solved.** The verdict table says "gains a catalog-binding prerequisite" with no caveat, and Epic 43's README says MCP has no drift signal. Copying the verdict verbatim ships a requirement that cannot be satisfied at any useful granularity. Mitigation: D5 and AC4 require the fork to be stated beside it and Part B marked blocked.
- **Deleting an AC deletes a guarantee nobody notices is gone.** Four ACs die in 42-1 and one in 42-8B. Mitigation: D3's per-AC replacement note; the AC-numbering integrity pass.
- **Losing transplanted analysis.** 42-2 and 42-3's files are already deleted, so any analysis not transplanted is unrecoverable. Mitigation: AC10 verifies it and files it if missing — this story is the last moment that check is possible.
- **The rewrite drifts from Epic 43's evolving spec.** 43-2/43-3/43-5/43-9 are drafted, not implemented; a rename after this lands makes 42-1's rewrite wrong again. Mitigation: D10 — sequence after 43-9's plan is settled; the rewrite names types (`ActionRisk`, `AutonomyQuery.Target`, `action_assignments.min_autonomy`) rather than restating their contents.
- **Documentation-only work is under-reviewed.** The whole point is that an implementer trusts the story file, so a wrong edit here is a wrong build later. Mitigation: the eight verification passes are mechanical and cheap; the delta-table-emptiness check in particular is a hard, objective gate on the largest edit.
- **Estimate risk is low but real.** The reconciled scope is already written and reviewed inside the plans (Correction 3), so most of the work is moving text and then verifying nothing dangles. 2 days holds unless AC10's transplant check turns up a missing transplant, which would add drafting work to Epic 43 Story 5 or 9.

## Blocks / Blocked by

- **Blocked by (spec stability, not implementation):** Epic 43 Stories 43-2 (`ActionRisk`, `ActionKey`, `ActionDescriptor`), 43-3 (groups, shipped defaults), 43-5 (`action_assignments`, `action_authorizations`), 43-9 (Seam B, `AutonomyQuery.Operation`/`.Target`, the ledger). Their **plans** must be settled; their code need not exist.
- **Blocked by nothing in Epic 42** — none of it exists in code.
- **Blocks Epic 42 Wave 0 (42-1).** 42-1 is the contract everything downstream reads; implementing it from the pre-reconciliation story file rebuilds the duplication. **42-1 must not start before this story lands.**
- **Blocks 42-6 Part B** additionally, on the MCP fork (AC4) — which is a product decision, not a spec edit, and is entangled with Epic 42 open question 2 and Epic 43 open question 1.
- **Blocks 42-7 / 42-8A / 42-8B / 42-9** on the `ConfigJson` product decision (AC7) — recorded here, decided elsewhere, and required before any of the four is implemented.
- **Parallel with** every other Epic 43 story: this touches only `docs/stories/epic-42/`, `docs/stories/epic-39/story-39-23/` and `.dev/decisions/`.
