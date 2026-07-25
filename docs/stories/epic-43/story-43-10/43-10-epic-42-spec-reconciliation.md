# Story 43-10: Epic 42 Spec Reconciliation — Apply the Verdicts to the Story Files

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then, before writing any code, check the knowledge base:

- `.dev/spikes/` — existing research
- `.dev/bugs/` — known bugs
- `.dev/findings/` — pitfalls and best practices
- `.dev/decisions/` — architecture decisions

## User Story

As **the next person to implement an Epic 42 story**,
I want the story file I open to state the reconciled scope directly, rather than pre-reconciliation text that a boxed note at the top of a different document contradicts,
So that I do not build a second action catalog — a duplicate autonomy floor, a duplicate override store, a second gate on the same code path, a second denial-audit schema and a second escalation machinery — because the story file told me to.

## Priority

P0 — **None of Epic 42 exists in code.** That is precisely why this is cheap now and expensive later: it is a spec edit today and a refactor of shipped governance the moment 42-1 or 42-3 lands. Epic 42's Wave 0 (42-1) is the foundation everything else in that epic depends on; a rewrite that happens *after* 42-1 ships means the contract changes under four downstream stories.

## Architectural Context (READ FIRST)

### What is already done, and what is left

The reconciliation was decided and partly applied on 2026-07-25. **Do not redo it.** Verified state of the tree:

| Already applied | Evidence |
|---|---|
| `docs/stories/epic-42/story-42-2/` **deleted** | directory absent; `docs/sprint-status.yaml:646` marked `superseded` with the full rationale |
| `docs/stories/epic-42/story-42-3/` **deleted** | directory absent; `docs/sprint-status.yaml:647` marked `superseded` with the transplant note |
| Epic 42 README carries the **verdict table** | `docs/stories/epic-42/README.md`, the boxed "⚠️ RECONCILED AGAINST EPIC 43 (2026-07-25)" block above the story table |
| Epic 42 README records the **`ConfigJson` gap** | same file, the boxed "⚠️ Gap opened by the 42-2 deletion" block |
| Story 39-23 marked `superseded` | `docs/sprint-status.yaml:552` |
| **Every Epic 42 implementation plan** carries a "Reconciled scope — differs from the story file" table | `story-42-1/implementation-plan.md:3-18`, and the equivalent in 42-4/5/6/7/8A/8B/9 |

**What is left is the per-story-file edits.** Today the plans say one thing and the story files say another, with a note pointing at a third document. That is the worst of the three possible states: an implementer who reads the story file (the natural entry point, and the one the ACs live in) builds the wrong thing.

### The seven file-level edits

**1. Rewrite 42-1 (`story-42-1/42-1-tool-contract-registry-evolution.md`).** Today's `ToolDescriptor` at `:83-146` is `(Category, PermissionClass, AutonomyFloor, RequiredSecret, Suspends)`. Three of those five die:
- `AutonomyFloor` — Epic 43's `ActionDescriptor.DefaultMinAutonomy` + `action_assignments.min_autonomy` replace it. Also: it re-hardcodes the `[70,100]` bound (`:145-146` cites `AcceptanceRules.cs L85-86`), which Epic 43 D3 forbids a second time.
- `PermissionClass` — becomes `Tamma.Core/Actions/ActionRisk`, member-for-member, applied to all ~153 catalog members rather than 7 tools.
- `Category` — the registration path is decided by the registry, not by a descriptor field.

**The consequential second-order effect: with those three fields gone, the default-interface-member design has nothing left to fail safe to.** The DIM at `:83-86` exists solely to return `new ToolDescriptor(Native, Destructive, 100, null, false)` — a deny-by-default value composed entirely of the three dying fields. `SecretRequirement` has no fail-safe default (a tool needs a specific secret or none) and neither does `Suspends`. So `Descriptor` becomes a **plain abstract interface member**, and the DIM's three carefully-documented caveats at `:135-146` go with it:
- (a) a DIM is not invocable through a concrete-typed reference;
- (b) `Mock<IToolExecutor>` proxies implement the DIM and return `default` — i.e. **null**, not the deny-by-default value;
- (c) distinguishing "declared" from "inherited" needs `Type.GetInterfaceMap`.

All three exist only because of the DIM. Consequently **story ACs 2, 4 and 8 disappear** (`:216-221` the DIM fail-safe regression test; `:227-232` the `GetInterfaceMap` startup drift test; `:242-250` the mock-hazard pin) — an abstract member is enforced by the **compiler**, which is strictly stronger than any of them. AC5 (`AutonomyFloor` range validation) also goes.

**Survives verbatim:** §0's **move** of `SecretPurpose` to `Tamma.Core.Enums` (still required, still 7 members, still a move not a mirror — `SecretRow.Purpose` is a `string` column so it is data-safe), `SecretRequirement`, `Suspends` **including its load-bearing wording** ("declares that completion is owned by an engine-side wait" — an `IToolExecutor` cannot suspend a workflow; 42-7 §4 and 42-8B §6 both depend on that sentence), and §3's dynamic `Register`/`Unregister` seam with its platform-scoped-only constraint.

**2. Narrow 42-5 and remove the `InlineToolLoopResult.ToolCalls` dependency.** 42-5 keeps the invocation trio `TOOL.INVOKED` / `TOOL.SUCCEEDED` / `TOOL.FAILED` — invocation audit answers a different question from gate audit and must **not** be merged into `ACTION.GATE.*`. It loses the governance events (Epic 43 owns one event family) and, structurally, its Scope §5 (`:128-133`) requirement to populate `InlineToolLoopResult.ToolCalls` plus AC8 (`:158`). That field is **documented as always empty** (`IInlineToolLoopRunner.cs:133-134`, `:112-114`) and reviving `AGENT.TOOL_CALL.*` would create a second durable tool-call family alongside `TOOL.*`. The documentation half of §5 survives.

**3. Add the catalog-binding prerequisite to 42-6 Part B — and flag that it may be unimplementable as specified.** An MCP tool entering the registry must resolve to a catalog entry; unclassified ⇒ rejected at registration. This is also the answer to Epic 42's open PO question 1 (descriptor default: deny vs read-only) — **deny at registration**.

**But it must not be written as if it were straightforward.** Epic 43's own README states, under the holes its mechanisms cannot close: *"MCP is one coarse member with no drift signal. Adding a server, or a tool on an existing server, changes nothing in the catalog."* A per-MCP-tool catalog binding requires per-tool catalog **members**, which the catalog does not have and which no drift harness can verify — so "resolve to a catalog entry" either means (i) every MCP tool binds to the single coarse `tool:mcp_invoke` member, which is a binding that governs nothing finer than today, or (ii) MCP tools mint catalog members dynamically, which makes the catalog **open** and breaks the closed-vocabulary premise the whole epic rests on. **Neither is specified.** 42-6 Part B must record this as an unresolved design fork, not as a solved prerequisite.

**4. Delete 42-8B Scope 4 / AC7.** `story-42-8/42-8b-deploy-control-tool.md:76-79` requires the Epic 39 always-escalate class to bind **independently of** the autonomy dial and of 42-3's grant; `:141-143` is its AC ("a prod deploy whose acceptance class is always-escalate routes to a human even when autonomy is 100 **and** a valid grant exists"). That is, in plain terms, **two gates, two audit ids and two human decisions for one production deploy**. Delete both, plus the `:213-214` risk-mitigation reference to "the *independent* … class (AC7)".

**Safety is fully preserved, and the story must say how:** `effect:deploy.promote-prod` and `effect:deploy.rollback` ship `AlwaysHuman`; `IActionAuthorizationLedger` makes one grant cover the correlation; the existing business-mode predicate is **OR'd, not replaced** (Story 43-9 AC11). `WaitForToolOperationActivity` is unaffected — it is an *operation* wait, not an *authorization* wait.

**5. Strip the gating sections from 42-7 / 42-8A / 42-8B / 42-9.** Each keeps its capability declaration, its provider abstraction, its secret binding and its audit; each loses its `AutonomyFloor` / `PermissionClass` fields, its `ToolInvocationFacts Describe(argumentsJson)` stage-2 seam and its 42-3 references. Concretely in 42-7: `:72-87` (the per-call `PermissionClass` table and the `Describe` seam), `:129` and `:164` (the "42-3 decision id"), `:193-195` (the 42-3 dependency), `:210-217` (the stage-1-filter risk, which is now moot). 42-8A's "prod-vs-non-prod class resolved from the binding, never asserted by the model" **survives** — re-expressed as a server-sourced `AutonomyQuery.Target`.

**6. Carry the `ConfigJson` gap into the story files, and do not silently re-close it.** Deleting 42-2 removed the per-principal tool **configuration** store as well as the policy store. Epic 43's `action_assignments` absorbs the policy half **only** — deliberately; it is a governance table, not a settings store. Four stories lost their destination:

| Story | What it lost |
|---|---|
| **42-9** authenticated HTTP tool | the destination / host allowlist |
| **42-8A** feature-flag tool | the environment map |
| **42-8B** deploy-control tool | the target map |
| **42-4** tool credential binding | the secret-name override |

The four **plans** already moved their configuration to `IOptions` (deployment configuration), which is sufficient for single-user mode and for a single-tenant deployment. **`IOptions` is process-wide, so per-tenant tool configuration in SaaS is genuinely lost** — two tenants cannot have different deploy targets or different flag environments. That is recorded as an open product question in Epic 42's README. **This story must carry it into the four story files as an explicit open question**, not paper over it by implying `IOptions` is the answer. Restoring it means either a narrow `tool_configuration` table (config only, explicitly **not** a second governance store) or accepting deployment-scoped tool configuration in v1. Not derivable from code.

**7. Rewrite the self-contradicting Epic 42 README passages.** The verdict box was added above text that still asserts the old model. At minimum: "The tool contract (what a tool declares)" and its five-row field table still list `Category` / `PermissionClass` / `AutonomyFloor`; "Permission + autonomy + secret model" still describes `tool_bindings` and the two-stage 42-3 gate as live; "Sequencing" Wave 1 still names 42-2 and 42-3; "Dependencies" still says 42-3 must ship a live-read seam and cites 39-23. Each is corrected in place with a pointer to the verdict, so the README is internally consistent rather than layered.

## Acceptance Criteria

1. **42-1's story file is rewritten, not annotated.** `ToolDescriptor` reads `(SecretRequirement? RequiredSecret, bool Suspends)`. `ToolCategory` and `ToolPermissionClass` are not introduced. `Descriptor` is a **plain abstract interface member**; the DIM design and all three of its caveats are removed. Story ACs **2, 4, 5 and 8** are deleted and the remaining ACs renumbered. §0 (`SecretPurpose` move), §3 (`Register`/`Unregister`, platform-scoped only) and the `Suspends` wording survive verbatim. A "Superseded by Epic 43" line names each dropped field and where it now lives.

2. **42-1's rewrite is checked against its own implementation plan.** `story-42-1/implementation-plan.md:3-18` already carries the reconciled table and D2 ("`Descriptor` is a plain abstract interface member, not a DIM"). After this story the plan's "Reconciled scope — differs from the story file" table must be **empty or removed**, because the story file no longer differs. A residual delta means the edit was incomplete.

3. **42-5 is narrowed.** The invocation trio survives. Scope §5's "populate `InlineToolLoopResult.ToolCalls`" requirement and AC8 are deleted, with a one-line note that the field is documented always-empty at `IInlineToolLoopRunner.cs:133-134` and that reviving `AGENT.TOOL_CALL.*` would be a second durable tool-call family. Governance events are removed and pointed at `ACTION.GATE.*`. The documentation half of §5 survives.

4. **42-6 Part B gains the catalog-binding prerequisite AND the flag that it may be unimplementable as specified.** The prerequisite is stated (an MCP tool entering the registry must resolve to a catalog entry; unclassified ⇒ rejected at registration; this answers Epic 42 PO question 1 as *deny*). Immediately beside it, the unresolved fork is stated: Epic 43's README says MCP is one coarse member **with no drift signal**, so binding either collapses to the single coarse member (governing nothing finer) or requires dynamically-minted members (which makes the catalog open). 42-6 Part B is marked **blocked on that decision**, and it is added to Epic 42's open questions.

5. **42-8B Scope 4 and AC7 are deleted**, along with the `:213-214` risk text that depends on AC7. The remaining scope states how safety is preserved without them (ships `AlwaysHuman`; the ledger covers the correlation; the business-mode predicate is OR'd not replaced) and notes that `WaitForToolOperationActivity` is unaffected because it is an operation wait, not an authorization wait.

6. **42-7 / 42-8A / 42-8B / 42-9 lose their gating sections and keep everything else.** No `AutonomyFloor`, no `PermissionClass` field, no `ToolInvocationFacts Describe(argumentsJson)` seam, no 42-3 dependency or decision-id reference. Capability declaration, provider abstraction, secret binding, `TOOL.*` audit and the Epic 41 consumer mapping are untouched. 42-8A's "class resolved from the binding, never asserted by the model" survives, re-expressed as a server-sourced `AutonomyQuery.Target`.

7. **The `ConfigJson` gap is carried into all four affected story files as an open question.** 42-4, 42-8A, 42-8B and 42-9 each state: what configuration they need, that its 42-2 destination was deleted, that the interim answer is `IOptions` (deployment-scoped), and that **per-tenant tool configuration in SaaS is genuinely lost and unresolved**. No file may imply the question is closed. A cross-check asserts the wording in the four files agrees with Epic 42's README block.

8. **Every remaining reference to 42-2 / 42-3 / 39-23 across `docs/stories/epic-42/` resolves.** A grep pass: every mention is either (a) a "superseded, see Epic 43 Story N" pointer, or (b) a credited transplant ("42-3's effective-ceiling insight, transplanted into Epic 43 Seam B"). No story file may state a live dependency on a deleted story. The same pass covers `docs/stories/epic-39/story-39-23/` — if the directory still exists it carries a superseded banner matching `sprint-status.yaml:552`.

9. **Epic 42's README is internally consistent.** The four self-contradicting passages (the tool-contract field table; the permission/autonomy/secret model's `tool_bindings` + two-stage-gate description; Sequencing Wave 1's 42-2/42-3 entries; Dependencies' 42-3 live-read seam and 39-23 citation) are corrected in place. The verdict box stays as the audit trail of *why*, not as the only place the truth is written.

10. **Nothing is deleted that carries analysis worth keeping.** The pre-reconciliation text that is genuinely valuable — 42-3's two-stage siting analysis and its effective-ceiling insight (filtering on the raw descriptor class would have made every Wave-3 write tool unreachable), 42-2's two-scoping resolution prose — is preserved **where it was transplanted**, with attribution. A reviewer check confirms Epic 43 Stories 5 and 9 carry it; if either does not, this story files it rather than losing it.

11. **The decision record is written.** `.dev/decisions/epic-43-action-catalog-design.md` records the reconciliation: the twelve duplications, the per-story verdicts, the four transplants, and the two things the reconciliation itself **broke** (`ConfigJson`, and the MCP catalog-binding fork in AC4) — so the next reader sees the cost as well as the benefit.

## Dependencies

- **Epic 43 Stories 43-2, 43-3, 43-5, 43-9** — the reconciliation asserts that the catalog, its risk taxonomy, its storage and its Seam B *exist as specified*. Those stories need not be **implemented** for this one to land (this is a documentation change), but their **specs must be stable** — a `ToolDescriptor` rewrite that points at `ActionRisk` is wrong if `ActionRisk` is renamed afterwards. Land this after 43-9's plan is settled.
- **Already applied, do not redo:** the 42-2 / 42-3 directory deletions, their `sprint-status.yaml` rows, Epic 42's README verdict table and `ConfigJson` gap block, 39-23's superseded row.
- **Existing, verified:** every Epic 42 implementation plan already carries a "Reconciled scope" table; this story makes those tables empty rather than adding to them.

## Out of Scope

- **Any code change.** None of Epic 42 exists in code. This story edits `docs/stories/epic-42/` and `.dev/decisions/`.
- **Deleting further Epic 42 stories.** 42-4, 42-5, 42-6, 42-7, 42-8A, 42-8B and 42-9 all survive; they shrink.
- **Answering the `ConfigJson` question.** It is recorded, scoped and left open. Deciding between a narrow `tool_configuration` table and deployment-scoped configuration is a product decision that must be taken **before** 42-7, 42-8A, 42-8B or 42-9 is implemented — not here.
- **Answering the MCP catalog-binding fork (AC4).** Recorded as blocking 42-6 Part B; not resolved here. It is entangled with Epic 42's open question 2 (may a tenant admin register a tenant-scoped MCP server?) and with Epic 43's open question 1 — the same question from two directions.
- **Re-planning Epic 42's sequencing.** Waves shift because 42-2 and 42-3 are gone; the README's Sequencing section is corrected (AC9) but the epic is not re-estimated.

## Estimated Effort

2 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-25 | 1.0.0   | Initial story creation | Claude |
