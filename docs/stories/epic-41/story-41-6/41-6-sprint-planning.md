# Story 41-6: Sprint Planning Workflow

Status: drafted

## User Story

As a **scrum master / project manager** (or eligible role-holder), I want a workflow that commits a
capacity-bounded set of prioritised items to a time-box as a typed `SprintPlan` on the lifecycle, so that
sprint commitment is explicit, reviewed, and accepted — with owners and estimates — instead of decided in
an untracked meeting.

## Priority

P2 / Wave 3 — the agile-cadence anchor; consumes 41-3, feeds 41-7/41-8.

## Scope

Thin binding over `document-lifecycle`. `consumes: [BacklogOrdering (41-3), team capacity, prior SprintPlan
carry-over]` / `produces: SprintPlan`. Produce cell `(scrum_master, plan-sprint)` (41-1).

## Produced document

`SprintPlan` (41-1): committed set ≤ stated capacity; every committed item has an owner-role + estimate;
carry-over flagged. `tenantId`/`repository`/sprint lineage — a `SprintPlan` is not issue-scoped, so it
keys on its own lineage anchor `sprint:{repository}:{sprintKey}`. Reviewed by a **`product_owner`**
reviewer: `scrum_master` joins neither review panel ("they produce and accept, they do not critique
documents" — 41-1a D2) and the review-action selector throws for any unlisted role, so "the scrum
master's plan is reviewed by the scrum master" is not an option.

## Events

`SPRINT.PLANNING.STARTED` → `.PLANNED` → `.ACCEPTED` / `.CLOSED` alongside `DOCUMENT.*`.

## Orchestrator / user interaction

~~Accept gate routes per autonomy.~~ **[MISLEADING — CORRECTED 2026-08-01; see Amendment A2.]** The accept
gate routes per autonomy for every stage **except the accept decision itself**, which is pinned to a human
for `sprint-plan` at every legal dial position. Over-commit beyond capacity is a validator rejection, not an
accept-time surprise (`SprintPlanDocumentType.CommitmentExceedsCapacity`,
`apps/tamma-elsa/src/Tamma.Core/Documents/Types/SprintPlan.cs:78`). ~~The accepted plan carries an owner-role
per committed item so that Task View assignment becomes a pure consumer of the document once 39-19/39-20
land — seeding those assignments is **not** reachable today (see AC3).~~ **[WRONG IN PRINCIPLE — CORRECTED
2026-08-01; see Amendment A1.]** The accepted plan carries an owner-role per committed item so that a
**tracker** consumer can apply it; the consumer is **Story 44-4's `POST /api/iterations/{id}/apply-plan`**,
which writes `WorkItem.IterationId` and raises **no** Task-View entry. A sprint commitment is not a
Task-View row at any autonomy level — see AC3.

## Autonomy behavior

~~- **70–84:** agent proposes; scrum master/PM accepts the commitment.~~
~~- **85–100:** agent plans and self-accepts within capacity; commitment beyond a configured capacity band
  always escalates.~~

**[FALSE — REPLACED 2026-08-01; see Amendment A2. `sprint-plan` is pinned to a human acceptor in three
independent places, so there is no dial position at which the agent self-accepts, and the "capacity band"
escalation clause is not expressible in the shipped model.]**

- **70–100 (the whole validated dial, `AutonomyDial.Min`..`AutonomyDial.Max` —
  `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AutonomyDial.cs:27-30`):** the agent authors the draft
  and the review runs per the resolved rules; **a human answers the accept decision**. The dial changes how
  much of the lifecycle short of acceptance the orchestrator routes on its own — it never buys a
  self-accept for `sprint-plan`.
- **Over-commit never reaches the accept gate at all.** It is a validator rejection inside the lifecycle's
  validate→repair loop (`COMMITMENT_EXCEEDS_CAPACITY`, `SprintPlan.cs:78`), so no always-escalate class is
  needed for it — and none could express "beyond a configured capacity band": an `EscalationClass` is
  `(Kind, Key)` with `Kind ∈ {document-type, agent-action}` and `Key` a wire string
  (`Documents/Policy/AcceptanceRules.cs:200-210`), carrying no numeric dimension. The only class this story
  could express is `EscalationClass(DocumentType, "sprint-plan")` — escalate *every* sprint plan — which the
  human-acceptor pin already achieves.
- **The one documented way to lower the pin** is an explicit per-type
  `PUT /api/acceptance-rules/sprint-plan` carrying `"acceptorRequirement": "any"` — the deliberate tier-1
  exemption (`Documents/Policy/AcceptanceFloors.cs`, class doc: "Lowering a shipped human floor must NAME
  THE TYPE"). That is a named operator act on this document type, not a consequence of raising the dial.

## Acceptance Criteria

1. Thin lifecycle binding; `SprintPlan` validated (capacity bound, owner+estimate per item, carry-over).
2. Consumes accepted `BacklogOrdering` (via 41-3's synthetic anchor); a missing ordering is a **typed loud
   exit** — a `SPRINT.PLANNING.FAILED` emission plus `status="failed"` with a named detail (the read seam
   is fail-closed and never throws, and rule 1 forbids a `Finish` terminal, so "hard-fail" is a routed
   outcome, not an exception).
3. ~~**Not claimable until 39-19/39-20 land** (the audience resolver is the fail-closed
   `InitiatorOnlyTaskAudienceResolver` stub and there is no Task View). This story delivers the
   precondition only: the accepted `SprintPlan` carries an owner-role per committed item and acceptance
   publishes the standard `AcceptanceRequest`; role-scoped Task View entries become a consumer of the
   document when 39-19/39-20 land, with no edit to this binding.~~
   **[NARROWED 2026-08-01 — the previous text deferred a row that must never be raised. See Amendment A1.]**

   **AC3 (replacement) — the per-item owner-role is a document guarantee, and the commitment is never a
   Task-View row.** Two clauses, both able to fail today:

   a. **Owner-role guarantee, proved end-to-end.** The accepted `SprintPlan` body deserializes into the
      shipped `Tamma.Core.Documents.Types.SprintPlan` record
      (`apps/tamma-elsa/src/Tamma.Core/Documents/Types/SprintPlan.cs:35-41`) and **every** entry in
      `Committed` carries a non-empty `ownerRole` that parses to an `AgentRole`. This is not aspirational:
      `SprintPlanDocumentType.Validate` already rejects the draft with `COMMITTED_ITEM_MISSING_OWNER_ROLE`
      (`:69`) / `OWNER_ROLE_UNKNOWN` (`:72`), so the execution test asserts it on an *accepted* row and a
      companion case asserts a bad `ownerRole` never reaches acceptance. Fails if the binding accepts a
      payload the validator would have rejected, or persists a body the shipped record cannot read.

   b. **Structural isolation from the task/decision plane.** No file this story creates or edits
      (`SprintPlanningWorkflow.cs`, `SprintBindingHelper.cs`, `SprintEvents.cs`) may reference
      `ITaskAudienceResolver` (`Tamma.Api/Services/Access/ITaskAudienceResolver.cs`), `ChannelAudience`, or
      a string literal beginning `"TASK."`. A source-level test asserts it. This can fail — an implementer
      taking the old AC3 literally would resolve an audience per committed item, and that reference is what
      the test catches. Match `"TASK."` as an **ordinal prefix**, never `Contains`: `AGENT.TASK.*` exists
      (`Tamma.Api/Services/Agents/AgentTrailEventTypes.cs`) and a `Contains` check would match it.

   **Who owns the correct behaviour: Story 44-4.** `POST /api/iterations/{id:guid}/apply-plan` reads the
   accepted `sprint-plan` document, resolves `Committed` entries and sets `WorkItem.IterationId` in one
   transaction, raising no Task-View entry — 44-4 AC9 and AC10
   (`docs/stories/epic-44/story-44-4/44-4-iterations-board-projection-and-the-sprintplan-apply-seam.md:58-89`).
   44-4's Out of Scope explicitly refuses to edit Epic 41 files (`:124` — "rewording 41-6's AC3 is a docs
   edit in Epic 41's own file and is recommended, not performed here"), which is why this narrowing lands
   here. **This story writes no tracker code**; it produces the document 44-4 consumes.

   **Why a Task-View row was wrong in principle, not merely early.** The Task View is the *suspended-decision
   inbox*: four task types (acceptance decision, review, approval, clarification), each backed by a 39-8
   bookmark, resolved through the idempotent resume surface, and cleared when the first authorized
   completion lands (`docs/stories/epic-39/story-39-19/39-19-orchestrator-chat-primary-user-interface-and-task-view.md:33`,
   AC3). A committed sprint item has **no bookmark and no pending decision** — nothing suspends on it, so
   nothing can resume it and nothing can ever clear it. The row would sit in every role-holder's inbox
   forever. Landing 39-19/39-20 does not fix that; it would only make the broken row buildable.
4. `[ResumeBehavior(LatestStateReEntry)]` (a thin binding owns no suspend node — the accept gate suspends
   inside the dispatched child); 39-10 structural test green without allowlist.

## Dependencies

- ~~**Blocking:** **41-1a** (`scrum_master` role + `plan-sprint` cell) **and 41-1b** (`SprintPlan`
  type), 41-3, Epic 39 (lifecycle, store, routing).~~ *[RE-STATED 2026-08-01 — two of these are done and
  the one that is not is the real gate. See Amendment A4.]*
- **Done, no longer blocking:** **41-1a** — `AgentRole.ScrumMaster` (`Tamma.Core/Agents/AgentRole.cs:23`),
  `AgentAction.PlanSprint` (`Agents/AgentAction.cs:132`), the primary-action row
  (`Agents/RolePhaseMap.cs:229`), the `scrum_master → product_owner` alias **removed**
  (`RolePhaseMap.cs:288`, the comment that replaced it), and all three prompt files
  (`Tamma.Api/Prompts/scrum_master/{_system,context-scan,plan-sprint}.md`). Status `done`,
  `docs/sprint-status.yaml:629`. **41-1b** — `DocumentTypeKey.SprintPlan`
  (`Documents/DocumentTypeKey.cs:41`), `SprintPlan`/`SprintPlanDocumentType`
  (`Documents/Types/SprintPlan.cs`), registry row (`Documents/DocumentTypeRegistry.cs:45`), acceptance row
  (`Documents/Policy/AcceptanceDefaults.cs:216`). Status `done`, `docs/sprint-status.yaml:630`.
- **⛔ THE REAL BLOCKER — 41-3 must LAND, not merely be scheduled.** This story *calls* a helper that 41-3
  authors: `BacklogBindingHelper.BuildAnchor(repository, backlogScope)` is the read key for the upstream
  accepted `BacklogOrdering`, and the plan pins it as "called, never re-derived" (this story's
  `implementation-plan.md`, Correction 4 / D3). **The file does not exist**: no `BacklogBindingHelper.cs`
  under `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/`, and 41-3's own plan is where it is
  created (`docs/stories/epic-41/story-41-3/implementation-plan.md:143-144`). 41-3 is `drafted`
  (`docs/sprint-status.yaml:633`). Steps that touch the upstream read cannot be written against a helper
  that has no source file.
- **⚠ The status file's blocker list for 41-6 is incomplete.** `docs/sprint-status.yaml:636` reads
  "Blocked on 41-1a + 41-1b" and does not name 41-3. Both named blockers are now `done`, so a scheduler
  reading only that line sees 41-6 as unblocked. **It is not.** (Coordinator-owned file; recorded here
  rather than edited.)
- **Epic 39** — lifecycle / store / routing: landed (39-6/39-7/39-8/39-10/39-11).
- **Related:** 41-7, 41-8.
- **Consumer, not a dependency:** **44-4** owns the tracker-side apply seam (AC3). 44-4 is non-blocking for
  this story's code and this story is non-blocking for 44-4's code (44-4 tests against a hand-built
  `DocumentInstance` fixture) — but the two are joined by an unresolved `issueId` contract, recorded in the
  Open Items below.

## Estimated Effort

4–5 days *(unchanged by the 2026-08-01 amendments in aggregate: AC3(b)'s source-level test and the D5
prompt-file edit are small additions, offset by AC3 no longer carrying a deferred Task-View story. The
figure was never a costing of AC3 — see the implementation plan's Est. Effort note.)*

## Amendment — 2026-08-01 (scoping round: story vs. tree)

Every claim below was checked against the working tree at commit `6429691`. Where the story was wrong, the
original text is struck through in place rather than removed.

**A1 — AC3 asked for a Task-View row that must never exist; it is narrowed, not merely deferred.** The
2026-07-27 pass (commit `f611234`) already softened AC3 from "Committed items produce role-scoped Task View
entries via 39-20" to "not claimable until 39-19/39-20 land". That was the wrong correction: it treated an
unbuildable row as an early one. The Task View is the suspended-decision inbox — four task types, each
backed by a 39-8 bookmark, each cleared by a resume (39-19 AC3). A committed sprint item has no bookmark
and no pending decision, so a row for it can never be cleared. AC3 is now two testable clauses (the
per-item `ownerRole` document guarantee, and structural isolation from the task/decision plane), and Story
**44-4** is named as the owner of the correct consumer behaviour: `POST /api/iterations/{id}/apply-plan`
writes `WorkItem.IterationId` and raises no Task-View entry (44-4 AC9/AC10). 44-4's Out of Scope refuses to
edit Epic 41 files (`44-4-…md:124`), so if this story did not narrow AC3, nothing would.

*Note for future readers — a stale citation in 44-4.* 44-4 quotes the old AC3 wording and cites it as
`41-6:45` (`44-4-…md:35`). That text was already gone at 44-4's writing (removed 2026-07-27, four days
earlier); line 45 of this file was AC4's `ResumeBehavior` clause, not the Task-View sentence. 44-4's
*substance* — that the correction belongs here and that 44-4 implements the right behaviour — is correct
and is adopted above. The line citation is not; do not chase it.

**A2 — the Autonomy section's "85–100: agent plans and self-accepts" was false. `sprint-plan` is pinned to
a human acceptor in three independent places, each with a green test.**

1. **The acceptance default.** `AcceptanceDefaults.For(DocumentTypeKey.SprintPlan) => s_humanProductOwnerRules`
   (`apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceDefaults.cs:216`); that row is built at
   `:144-147` as the product-owner-reviewer row `with { AcceptorRequirement = AcceptorRequirement.Human }`.
   Pinned by `AcceptanceDefaultsDriftTests.The_41_1b_human_pinned_types_get_a_human_acceptor`
   (`apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/Policy/AcceptanceDefaultsDriftTests.cs:171-183`),
   which additionally asserts `AutonomyLevel == 70` with the reason *"the human pin is independent of the
   autonomy dial, not a lower dial"* — i.e. the code explicitly anticipated and rejected the reading this
   story had.
2. **The governance catalog.** `Doc(DocumentTypeKey.SprintPlan, …, min: AutonomyDial.AlwaysHuman)`
   (`Tamma.Core/Actions/ActionCatalog.Descriptors.cs:253`). `AutonomyDial.AlwaysHuman = Max + 1 = 101`
   (`Documents/Policy/AutonomyDial.cs:38`), strictly above the validated range `[70,100]` (`:27-30`), so
   `currentDial >= MinAutonomy` is **false at every legal dial position** — 100 included. Pinned by
   `ActionCatalogDefaultsTests.DesignDocumentType_MatchesAcceptanceDefaults`
   (`tests/Tamma.Core.Tests/Actions/ActionCatalogDefaultsTests.cs:98-120`), which derives the expectation
   from the real `AcceptanceDefaults.For` switch so the two surfaces cannot drift apart.
3. **The non-lowerable floor.** `AcceptanceFloors.ShippedFloorFor` / `ApplyShippedAcceptorFloor`
   (`Documents/Policy/AcceptanceFloors.cs:69-95`) raise the resolved `AcceptorRequirement` back to `Human`
   by `max()` over a two-element lattice whenever a **base** or **system-default** tier produced it — so a
   deployment-wide acceptance-rules row cannot silently erase the pin. Pinned by
   `AcceptanceFloorsTests.TheShippedHumanFloor_CoversDesign_SprintPlan_AndThreatModel`
   (`tests/Tamma.Core.Tests/Documents/Policy/AcceptanceFloorsTests.cs:48-58`).

   The deliberate exemption: an explicit **per-type** `PUT /api/acceptance-rules/sprint-plan` with
   `"acceptorRequirement": "any"` still lowers it (`AcceptanceFloors.cs` class doc — "Lowering a shipped
   human floor must NAME THE TYPE"). That is an operator naming this document type, not the dial reaching
   85.

Also corrected in the same section: **"commitment beyond a configured capacity band always escalates" is
not expressible.** `EscalationClass` is `(Kind, Key)` with `Kind ∈ {document-type, agent-action}`
(`Documents/Policy/AcceptanceRules.cs:200-210`) — no numeric dimension exists. And it is unnecessary:
over-commit is rejected by `SprintPlanDocumentType.Validate` (`COMMITMENT_EXCEEDS_CAPACITY`,
`Types/SprintPlan.cs:78`) inside the validate→repair loop, before any accept gate.

**A3 — the implementation plan's D5 specified prompt front matter that does not match the file 41-1a
shipped, and the mismatch would have failed silently.** Corrected in
`docs/stories/epic-41/story-41-6/implementation-plan.md` (D5, rewritten; Correction 9 added). Summary: the
shipped `apps/tamma-elsa/src/Tamma.Api/Prompts/scrum_master/plan-sprint.md:1-6` declares
`variables: role, backlogJson, teamCapacity, carryOverJson, conventions` / `maxTokens: 4096` /
`version: 2`; D5 asserted `backlogOrderingJson`, `capacityJson`, `revisionNotes`, `maxTokens: 8192`,
`version: 1`. The load-bearing half is `revisionNotes`: **no** template in the repo declares or renders it
(zero hits across all 123 files under `Prompts/`), `PromptStoreService.Render` substitutes only the
`{{placeholders}}` present in the body, and a supplied-but-unrendered variable is **dropped without a
warning** — the exact render-drop failure `ValidationFeedbackHelper`'s class doc was written to prevent
(`Workflows/Helpers/ValidationFeedbackHelper.cs:5-15`). Every landed lifecycle binding therefore points
`feedbackVariableName` at a carrier its template actually renders (`contextFindings`, `findings`,
`errorContext`, `testTarget`, `previousFindings`); none uses the canonical default.

**A4 — the blocker set is re-stated: 41-1a and 41-1b are done, 41-3 must LAND.** Recorded under
Dependencies, including that `docs/sprint-status.yaml:636` omits 41-3 and that its two named blockers are
both `done` — which is precisely why this story currently reads as schedulable and is not.

## Open Items (recorded 2026-08-01, deliberately not decided here)

- **The `issueId` join between `BacklogOrdering` and `SprintPlan` is unreconciled in shipped code, and this
  story cannot settle it alone.** `BacklogItem` keys entries on `itemId`
  (`Tamma.Core/Documents/Types/BacklogOrdering.cs:15`); `SprintCommittedItem` keys them on `issueId`
  (`Types/SprintPlan.cs:14`), and the shipped `plan-sprint.md` instructs the model to emit
  `"issueId": "the backlog item's issue id"` (`:30`) from a `{{backlogJson}}` whose entries carry `itemId`.
  `SprintPlanDocumentType`'s only rule on the field is "not null/whitespace" (`:133-135`). 44-4 AC9 asks
  **this story** to state that the field carries a work-item key (`44-4-…md:77`), noting its resolver is
  fixture-tested only until then. **Left open** because the value 41-6 threads in comes from 41-3's accepted
  `BacklogOrdering`, and 41-3 has not landed — 41-6 cannot fix a producer contract whose producer does not
  exist. Right home: 41-3, when it lands, states what `BacklogItem.itemId` carries; 41-6 then pins the
  pass-through with a test. Until then 44-4 degrades honestly (an unresolvable string is its `not-found`
  outcome), so nothing is silently wrong — it is merely unproven.
- **`SPRINT.PLANNING.CLOSED` has no emitter in this story** (plan D8): sprint closure happens at the end of
  the time-box, not at planning time, and 44-4 AC4 owns iteration closure. Left as a defined-not-emitted
  constant rather than faked; recorded so a future reader does not read the constant as a delivered
  transition.

## Change Log

| Date       | Version | Changes | Author |
| ---------- | ------- | ------- | ------ |
| 2026-08-01 | 1.2.0   | Scoping round against the tree. AC3 narrowed from a deferred Task-View row to two testable clauses, with 44-4 named as the owner of the correct behaviour and the in-principle argument recorded (A1); 44-4's stale `41-6:45` citation flagged. Autonomy section replaced — "85–100: agent plans and self-accepts" is false, three independent human-acceptor pins cited with their tests, and the capacity-band escalation clause shown to be inexpressible (A2). D5's prompt variable set reconciled against the shipped `plan-sprint.md` in the implementation plan (A3). Blocker set re-stated: 41-1a/41-1b done, 41-3 must land, sprint-status omission recorded (A4). Two open items recorded. | Claude |
| 2026-07-27 | 1.1.0   | `ResumeBehavior(Both)` → `LatestStateReEntry`; AC2 restated as a typed loud exit; AC3 deferred to 39-19/39-20 (superseded by A1 above); reviewer pinned to `product_owner`; sprint lineage anchor named. | Claude |
| —          | 1.0.0   | Initial story creation | Claude |
