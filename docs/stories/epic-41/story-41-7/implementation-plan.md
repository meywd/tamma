# Implementation Plan — Story 41-7: Standup Synthesis Workflow

> ## 2026-08-01 — NOT BLOCKED. Both stated gates have landed. (This banner supersedes the one below it.)
>
> **What this banner said before (verbatim, superseded):** *"Scheduler note — NOT scheduler-blocked
> (2026-07-25 decision); **still gated on 41-1a** … The story **remains hard-blocked on 41-1a** (the
> `scrum_master` role + `(scrum_master, synthesize-standup)` cell do not exist until 41-1a mints them)."*
> The plan's Blocks/Blocked-by section additionally called the scheduled-trigger seam *"hard, no owner,
> cannot be worked around"* and step 10 *"NOT buildable today"*.
>
> **Both claims are now false. Verified in the tree 2026-08-01:**
>
> - **41-1a is `done`** (`docs/sprint-status.yaml:629`). `AgentRole.ScrumMaster` is
>   `Tamma.Core/Agents/AgentRole.cs:23`; `AgentAction.SynthesizeStandup` is `AgentAction.cs:133`; the cell
>   is eligible at `RolePhaseMap.cs:178-181` and pinned by `RolePhaseMapTests.cs:742`; the alias is gone
>   (`RolePhaseMap.cs:288`, pinned `RolePhaseMapTests.cs:604-605`); the six `Prompts/scrum_master/*.md`
>   files exist, including `synthesize-standup.md`. **The taxonomy count pins have already moved** — they
>   read `Be(11)` / `Be(96)` / `HaveCount(96)` / `HaveCount(11)` today, not the `Be(8)` / `Be(80)` /
>   `HaveCount(80)` / `HaveCount(8)` this plan's D8 quotes. See amended D8.
> - **41-30 (the tenant-aware scheduled-trigger seam) is `done`** and ships every one of the four things
>   step 10 listed as prerequisites: `TenantScheduledTriggerService`
>   (`Tamma.ElsaServer/Workflows/TenantScheduledTriggerService.cs`, 788 lines), a tenant-scoped
>   advisory-lock key (`ScheduleLockKey.Compute(tenantId, trigger.Id, windowKey)`, `:359`), `tenantId`
>   threaded into the dispatch input (`:745`) alongside the trigger's own `InputJson` (`:731`), a
>   **persisted** fire ledger (`scheduled_trigger_fires`, `Tamma.Data/Entities/ScheduledTriggerFire.cs`,
>   `ON CONFLICT` claim), a cron/window shape (`ScheduledTrigger.CronExpression`), and an
>   arbitrary-definition dispatch (`DispatchWorkflowDefinitionRequest(fire.DefinitionId)`, `:648`).
>   Registered in the engine host at `Tamma.ElsaServer/Program.cs:243-252`, off by default
>   (`TenantScheduledTriggerOptions.Enabled = false`, `:30`).
>
> **What still holds from the 2026-07-25 decision:** standup synthesis is **user-initiated**; a cron
> cadence is a *later opt-in*, not part of this story. Concretely, `standup-synthesis` is deliberately
> **absent** from 41-30's closed allowlist `SchedulableDefinitions.Allowed`
> (`Tamma.Api/Endpoints/Admin/ScheduledTriggerEndpoints.cs:25-36`, which lists only `tech-debt-triage`,
> `regression-management`, `pr-triage-sweep`, `security-audit`, `capacity-review`). Adding it there is a
> one-line opt-in a later story or an operator can take; this story does not take it. See amended step 10.
>
> ### Historical — why `HourlyAnalyticsRollupScheduler` was never the pattern
>
> Kept because it is why 41-30 exists, and 41-30's own header cites it. **Line refs corrected 2026-08-01**
> (every ref in the original table was 1–2 lines off):
>
> | What a tenant-aware trigger needs | `HourlyAnalyticsRollupScheduler` has | Line (corrected) | Was written |
> |---|---|---|---|
> | dispatch any workflow | `HourlyAnalyticsRollupWorkflow.DefinitionId` hardcoded | `:199-200` | `:197-198` |
> | a window / cron shape | a single `int FireAtMinute` | `:35` | `:34` |
> | `tenantId` threaded into the dispatch | `// No input variables — the workflow infers the target hour` | `:203` | `:199-203` |
> | a **persisted** last-fired window | `private (int Year, int DayOfYear, int Hour) _lastFired` — in-process | `:84` | `:83` |
> | a tenant component in the advisory-lock key | `ComputeAdvisoryLockKey(int year, int dayOfYear, int hour)` | `:242` | `:241` |
>
> 41-30 fixed all five. This plan's steps 1–9 remain the deliverable; step 10 is now a *documented
> opt-in*, not a blocked TODO.

## Scope & Deliverable

*(Amended 2026-08-01: this paragraph opened "When this story is done **(and the seam exists)**" and
described the run as fired by "a tenant-scoped **scheduled trigger**". Both were written before the
2026-07-25 user-initiated decision and before 41-30 landed. The trigger is a user/API dispatch; the seam
exists but this workflow is deliberately not on it — amended step 10.)*

When this story is done, a scrum master's daily status assembly is a **document the platform produces
from the audit trail**. A new `StandupSynthesisWorkflow` (`DefinitionId = "standup-synthesis"` — free
today) is a THIN BINDING over `document-lifecycle`: a **user/API dispatch** supplies
`(tenant, repository, windowStartUtc, windowEndUtc)`, it reads the DCB event window plus the open
`Decomposition`/`Plan`/PR and blocker signals through a new `FetchEventWindowActivity`, dispatches
`document-lifecycle` with `documentType = "findings"` and the `(scrum_master, synthesize-standup)`
producer cell (41-1a, landed), and routes the typed exit. Zero `Finish`, zero `llm-call`, zero parsing.
Every digest item cites concrete DCB evidence — the *presence* of a citation is enforced by
`FindingsDocumentType`'s `MISSING_EVIDENCE` (`Findings.cs:135-138`, already shipped); making that citation
*resolvable* is this story's D4 ring. A new `STANDUP.*` family rides alongside `DOCUMENT.*`, tagged
`repository`/`tenantId`/window.

## Pre-Reading

- `docs/stories/epic-41/story-41-7/41-7-standup-synthesis.md` — the story (ACs are source of truth modulo
  Corrections)
- `docs/stories/epic-41/README.md` — rule 1 clauses (a)–(f); the Dependencies section's scheduler bullet
  and the Epic 42 caveat table (41-7 is in it: "authenticated HTTP / external API (42-9) — no executor")
- `docs/stories/epic-41/story-41-1/41-1a-agent-taxonomy-extension.md` — **LANDED, read as history.** It
  minted `AgentRole.ScrumMaster`, `synthesize-standup`, `Prompts/scrum_master/_system.md` +
  `Prompts/scrum_master/synthesize-standup.md`, removed the `scrum_master → product_owner` alias, and
  moved the count pins. *(Corrected 2026-08-01: this line cited the alias at `RolePhaseMap.cs:239`. There
  is no alias there — `:235-244` is the `ValidRoles` frozen set. The removal is recorded at
  `RolePhaseMap.cs:273-290` and pinned by `RolePhaseMapTests.cs:601-605`.)*
- **`apps/tamma-elsa/src/Tamma.Api/Prompts/scrum_master/synthesize-standup.md` — read it FIRST.** It is
  shipped, `version: 1`, and it conflicts with D4 (see Correction 7). This story rewrites it.
- `apps/tamma-elsa/src/Tamma.Api/Prompts/product_owner/define-acceptance-criteria.md` +
  `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/AcceptanceCriteriaAuthoringWorkflow.cs:238`, `:243` —
  41-2's landed template-rewrite + feedback-carrier shape, which Correction 7's v2 copies
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TenantScheduledTriggerService.cs` +
  `apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/ScheduledTriggerEndpoints.cs` — **41-30's landed seam**
  and its closed allowlist. Read to understand the step-10 opt-in.
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/HourlyAnalyticsRollupScheduler.cs` — read it to
  understand why it is NOT the pattern (the historical table above), not to copy it
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TaskCreationWorkflow.cs` — the binding skeleton to copy
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaskCreationWorkflowStructureTests.cs` — the
  reference structure-test shape
- `apps/tamma-elsa/src/Tamma.Core/Documents/Types/Findings.cs` — **read `Validate` in full**: the type
  hard-rejects an empty findings list (`EMPTY_FINDINGS`), a finding with no citations
  (`MISSING_EVIDENCE`), and relevance/confidence outside [0,1] (`RELEVANCE_OUT_OF_RANGE` /
  `CONFIDENCE_OUT_OF_RANGE`, rejected, never clamped). This is why Correction 1 exists.
- `apps/tamma-elsa/src/Tamma.Activities/Documents/LifecycleReEntryService.cs` — **the only in-engine
  precedent for reading `IEventRepository` from activity code**; `FetchEventWindowActivity` copies its
  service-resolution + tenant-resolution posture
- `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs` — the 4-7 query surface. **Read
  `QueryEventsAsync` (`:200-207`), NOT `ListByTenantAsync` (`:46-47`)** — see amended D2 for why the
  original choice was wrong.
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:171-252` (`QueryEvents`) +
  `apps/tamma-elsa/src/Tamma.Api/Program.cs:3091` — the engine-facing HTTP read the amended D2 uses
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TriagePODecisionWorkflow.cs` — the **empty-input
  SKIPPED short-circuit** precedent (emitted before any dispatch), which Correction 1 reuses
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/CreationBindingHelper.cs` — `ScopeIssueId`, the
  producer-scoped lifecycle key D3 generalises to a window key
- `apps/tamma-elsa/src/Tamma.Api/Program.cs:409-415` — `InitiatorOnlyTaskAudienceResolver` (fail-closed,
  `:409-411`) and `AgentOfflineChatRelay` (refuses every message, `:412-415`): why AC4c is asserted absent.
  *(Line ref corrected 2026-08-01: was `:445-451`; the registrations are at `:409-415`. The epic README
  carries the same stale `Program.cs:445-447` at `README.md:507`.)* Read with
  `Tamma.Api/Services/Channels/ChannelOutboxService.cs:140-175` — the fan-out that mints **zero** rows.
- **Test-pin files (paths corrected 2026-08-01 — only `WorkflowInterfaceGraphTests` is under
  `Tamma.Core.Tests`; the other three are under `Tamma.Activities.Tests/Workflows/`):**
  - `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs:36-53` (the edge-count
    pin, now `HaveCount(18)`), `:103-148` (the bidirectional `reconciled` array) — *was cited as `:45`,
    `:102-123`*
  - `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaxonomyDriftBuildTests.cs:110`
    (`MinExpectedDispatchPairs = 21`), `:125-150` (`ExpectedContributingWorkflows`) — both refs accurate
  - `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs:94` (the `Bindings`
    map), `:103-108` (the `(product_owner, research)` seven-token-group entry D8(iv) copies)
  - `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ResumableStandardStructuralTests.cs:43+`
    (`LegacyResumeAllowlist`)

## Corrections to the story

1. **AC2's "empty window ⇒ valid empty digest" is IMPOSSIBLE against the shipped type.**
   `FindingsDocumentType.Validate` (`Findings.cs`) emits `EMPTY_FINDINGS` for a zero-length findings list
   with the explicit rationale *"an empty list is a violation, not a valid 'nothing found'"*. An empty
   digest would loop the repair/revise ring to exhaustion and exit `escalated` — the exact "false noise"
   the AC is trying to avoid, inverted. Two options: (a) relax `EMPTY_FINDINGS` — **rejected**, it would
   weaken `research`, `triage-context-gathering` and every future `Findings` producer; (b) **short-circuit
   before dispatch**: when the window read returns no material events, emit `STANDUP.SYNTHESIS.SKIPPED`
   and expose `status = "skipped"` with **no document produced and no lifecycle dispatch** — the landed
   `TriagePODecisionWorkflow` empty-input precedent. **This plan takes (b)** and AC2 should read: *"an
   empty window produces no document and a `STANDUP.SYNTHESIS.SKIPPED` audit row — never an empty
   `Findings` and never a false digest."*
2. **AC2's "every finding cites concrete DCB evidence; confidence/relevance ∈ [0,1]" is already enforced
   by the type** (`MISSING_EVIDENCE`, `RELEVANCE_OUT_OF_RANGE`, `CONFIDENCE_OUT_OF_RANGE`). It costs this
   story a fixture, not a validator. The **new** work is making the citations *resolvable* — a citation
   string that names no real event is not caught by `Findings.Validate`. D4 adds that as a
   `validationContextJson` ring, which is the honest reading of "concrete DCB evidence".
3. **AC4 is not deliverable and must be re-scoped.** — **AMENDED 2026-08-01; see below.**
   *What this correction said (superseded):* *"…`ITaskAudienceResolver` is stubbed fail-closed by
   `InitiatorOnlyTaskAudienceResolver` (`Program.cs:445-447`), which **admits only the issue initiator**,
   and 39-19 ships no Task View at all (`:448-451`). … Claim the half that exists: 'each flagged blocker
   is emitted as a `STANDUP.BLOCKER_FLAGGED` row … role-scoped delivery is unreachable until
   39-19/39-20.'"*

   **Two things were wrong.** (i) **The line refs.** The registrations are at `Program.cs:409-411`
   (resolver) and `:412-415` (relay), not `:445-451`. (ii) **"Admits only the issue initiator" understates
   it.** The one production call site hardcodes `InitiatorUserId: null`
   (`ChannelOutboxService.cs:143`), so `InitiatorOnlyTaskAudienceResolver.EligibleAudienceAsync`
   (`ITaskAudienceResolver.cs:49-56`) returns `Array.Empty<AudienceMember>()` and the fan-out mints
   **zero** outbox rows, logging *"resolved zero recipients … nothing enqueued (fail-closed)"*
   (`ChannelOutboxService.cs:169-172`). `TrackerAssigneeResolver.cs:15-22` records the same finding
   independently: *"`EligibleAudienceAsync` returns EMPTY today for every input."* The practical
   consequence for this story is stronger than "the wrong person gets it": **nobody gets it, and the run
   looks successful.**

   **What is still right:** the resolver and relay are stubs, and the split "claim the half that exists"
   is the correct posture. **What changed:** the gap must be **pinned by tests, not narrated.** The story's
   AC4 is now 4a/4b (positive) + 4c (three asserted-absent pins, each naming its burn-down story). See the
   story's rewritten AC4 and Amendment A1, and the Test Plan's amended "asserted absent" bullet.
4. **Publication is not reachable on the agent path.** Per the story's own Epic 42 caveat and the README
   table, none of the six registered `IToolExecutor`s (`Program.cs:753-764`: `FileRead`, `FileWrite`,
   `SearchCode`, `ShellExecute`, `GitOperations`, `RunTests`) can post to a chat or tracker. Synthesis is
   agent-reachable; **broadcast is human-assigned until 42-9.** This is stated in the story and is
   correct — it is repeated here because it changes what "delivered to the team" means in the
   Orchestrator/user-interaction section.
5. **Scope line 19's "`HourlyAnalyticsRollupScheduler` cron pattern" is not a pattern.** See the blocked
   banner. The story's Dependencies line already carries the corrected wording; Scope does not, and
   should.
6. **AC3's `[ResumeBehavior(LatestStateReEntry)]` is correct as written** — unlike 41-8/41-9/41-10, whose
   `Both` declarations would fail the 39-10 gate. No change.
7. **NEW (2026-08-01) — the shipped producer template contradicts D4, and must be rewritten in this
   story.** `apps/tamma-elsa/src/Tamma.Api/Prompts/scrum_master/synthesize-standup.md` (`version: 1`,
   shipped by 41-1a) ends its rules block with:

   > - `summary` is required and non-empty; `findings` MUST NOT be empty — **a quiet day still yields a
   >   "nothing moved" finding citing the empty window.**

   Under D4's citation ring, that instruction *is* the false-escalation loop D4 exists to prevent: the
   model is told to cite something it was not given, D4 rejects it, and the repair ring cannot fix it
   because the model is following its instructions. Exhaust → `escalated`, on every quiet day.

   **Resolution: the prompt gives; `EMPTY_FINDINGS` and D4 both stand.** Full reasoning is in the story's
   **Amendment A2**; the short form:
   - D5's short-circuit means a genuinely empty window never reaches the model (`.SKIPPED`, no dispatch),
     so v1's premise describes an unreachable state. Where the model IS invoked, `EventCount > 0` and real
     citable ids exist.
   - Relaxing `EMPTY_FINDINGS` was already rejected (Correction 1: it weakens `research` /
     `triage-context-gathering`; `Findings.cs:113-118`). A D4 sentinel-citation escape would be a hole
     every `Findings` producer could use.
   - The prompt is this cell's own file, unshared, and **already due for a rewrite** for an independent
     reason (below). One v2 fixes both.
   - Precedent: 41-2 rewrote `define-acceptance-criteria.md` to `version: 2` and 41-9 rewrote
     `write-adr.md` v1→v2, both in-story (`docs/sprint-status.yaml:632`, `:639`).

   **The independent reason.** `synthesize-standup.md` declares
   `variables: role, eventWindowJson, sprintPlanJson, previousDigest` and renders no repair/revise
   feedback carrier. The lifecycle threads violation feedback into `feedbackVariableName`
   (`DocumentLifecycleWorkflow.cs:175`), defaulting to `revisionNotes`
   (`DocumentLifecycleHelper.cs:32`, folded by `BuildRevisionVariables` at `:424-435`). With no carrier
   rendered, the model never sees `EMPTY_FINDINGS` / `MISSING_EVIDENCE` / the new citation violation and
   re-emits the identical draft until the ring exhausts. Since **D4 adds a new violation class whose only
   remedy is the model changing its citations**, shipping D4 without a rendered carrier would make the
   ring a pure escalation generator.
   *(Scope honesty: NO shipped prompt declares `revisionNotes`, and neither `research.md` nor
   `triage-context-scan.md` declares any carrier — this is an epic-wide condition, not a 41-1a defect.
   Fix it for this cell only.)*

   **v2 (`version: 1` → `2`) changes exactly three things:**
   1. Replace the quiet-day clause with: *"`findings` MUST NOT be empty. A window with no events never
      reaches you. If the window is quiet, report the quiet as a finding and cite the actual event ids you
      were given in `{{eventWindowJson}}` — never invent a citation, and never cite 'the empty window'."*
   2. Add: *"At least one entry of every finding's `citations` MUST be an `eventId` copied verbatim from
      `{{eventWindowJson}}`."* (matches amended D4 — see Correction 8).
   3. Declare `contextFindings` in the front matter, render `{{contextFindings}}` in the body, and have
      the binding set `["feedbackVariableName"] = "contextFindings"` — 41-2's landed shape
      (`AcceptanceCriteriaAuthoringWorkflow.cs:238`, `:243`;
      `Prompts/product_owner/define-acceptance-criteria.md:2`, `:13`).

   **Do NOT touch** `variables: role, eventWindowJson, sprintPlanJson, previousDigest` beyond adding the
   carrier, and do not change the JSON contract block — `ContractBindingTests` pins the seven `Findings`
   token groups (`ContractBindingTests.cs:103-108`).
8. **NEW (2026-08-01) — D4's rule is "at least one anchored citation", not "every citation".** The plan's
   D4 said *"asserts **every** citation string resolves to an id in the index"*. Against the tree that is
   too strong: the shipped prompt's citation vocabulary is *"the event ids / issue refs / PR refs this is
   based on"* and the story requires `issueId`/`repository` lineage on every finding — so "every citation
   must be an event id" would ban a PR/issue reference and create a *second* false-rejection source right
   beside the one D4 removes. Amended D4 requires **≥1** anchored citation per finding; the rest are
   free-form. The constant is renamed `CITATION_UNANCHORED` (from `CITATION_UNKNOWN_EVENT`, which would
   misdescribe the rule). **41-11 consumes this ring** (`story-41-11/implementation-plan.md:153`) and must
   use the same constant.

## Design Decisions

- **D1 — two components, one story: a window-read activity and a thin binding. AMENDED 2026-08-01.**
  *Superseded:* *"**three components** … a trigger seam consumer, a window-read activity, and a thin
  binding … (ii) the trigger, which this story **consumes but does not build**. Keeping them separate
  means steps 1–9 are shippable … the day 41-1a lands, and only step 10 waits on the seam."*
  The trigger is no longer a component of this story at all: the run is **user/API dispatched** (2026-07-25
  decision) and the seam it would have consumed has landed anyway (41-30). The two genuinely new pieces
  are (i) `FetchEventWindowActivity` — the first *window* read of the DCB stream from workflow code (as
  opposed to 39-10's per-issue slice) — and (ii) the `Findings` citation ring (D4) plus the prompt v2 it
  requires (Correction 7). Everything else is a standard binding. **All of it is shippable now**; nothing
  waits.
- **D2 — `FetchEventWindowActivity` is a new activity in `Tamma.Activities/Documents/`.**
  **RE-POINTED 2026-08-01. The original D2 is quoted and refuted below; read the amended design.**

  > **Superseded text (verbatim):** *"…modelled on `LifecycleReEntryService`. Inputs: `TenantId`,
  > `Repository`, `WindowStartUtc`, `WindowEndUtc`, `TypePrefixesJson` (default `["DOCUMENT.",…]`),
  > `MaxEvents` (bounded, default 2000 …). Outputs: `EventsJson` …, `EventCount`, `EvidenceIndexJson` ….
  > It resolves `IEventRepository` + `ITenantContext` via `context.GetService<T>()` (the
  > `EventPersistenceMiddleware` pattern) **and reads through `ListByTenantAsync(tenantId, typePrefix,
  > limit, offset)` per prefix, filtered to the window in memory.** A missing service is a fail-loud
  > `TammaError STANDUP.WINDOW.SERVICE_UNREGISTERED`, never an empty window…"*

  **Three things in that were wrong.**

  1. **`ListByTenantAsync` has no time filter, and the in-memory window filter silently drops the OLDER
     half of a busy tenant's day.** Interface: `IEventRepository.cs:46-47` — `(tenantId, typePrefix,
     limit, offset)`, no `from`/`to`. Implementation: `EventRepository.cs:263-267` orders
     `OrderByDescending(e => e.CreatedAt)` then `Skip(offset).Take(limit)`. So the page returned is the
     **most-recent N events**, and filtering that page down to `[windowStart, windowEnd)` in memory loses
     everything older than the N-th most recent row. The moment a tenant emits more than `N` matching
     events after `windowStart`, the digest's evidence base is silently truncated at the wrong end —
     exactly the failure the plan's own Risks section says must never be silent.
  2. **The documented page size is 1..200; the plan asked for 2000.** `IEventRepository.cs:45` —
     `<param name="limit">Page size (1..200).</param>`. A `MaxEvents` default of 2000 is 10× outside the
     method's stated contract. (The implementation does not clamp, so it would "work" — which is worse: a
     contract violation with no signal.) Separately, `ListByTenantAsync` runs an **unbounded
     `CountAsync()` on every call** (`EventRepository.cs:262`) — × 7 prefixes, on every standup run,
     for a total nobody reads.
  3. **The service-resolution posture is not the `EventPersistenceMiddleware` pattern, and the services
     are not registered in the engine host.** `EventPersistenceMiddleware` resolves
     `context.GetService<TammaApiClient>()` (`EventPersistenceMiddleware.cs:180`, `:197`) and writes over
     **HTTP** — it never touches `IEventRepository`. And `IEventRepository` / `ITenantContext` are
     registered only by `AddTammaData` (`Tamma.Data/DependencyInjection.cs:47`, `:193`), which is called
     **only** from `Tamma.Api/Program.cs:203`. `Tamma.ElsaServer/Program.cs` registers from `Tamma.Data`
     only `ControlPlaneDbContext` (`:208-215`) and `IScheduledTriggerRepository` (`:243-244`). So
     `context.GetService<IEventRepository>()` inside an activity running in the engine host resolves to
     **null**, and D2's own fail-loud guard would fire on every single run.

  **What is still right in the original D2:** the input/output shape, the neutral Core-visible DTO, and
  above all the fail-loud rule — **a missing/unreachable service is a `TammaError`, never an empty
  window.** An empty window is a business outcome (D5) and must not be indistinguishable from a wiring
  bug. Keep that, and extend it (see the tenant caveat below).

  **Amended D2.**

  - **Inputs** (unchanged): `TenantId`, `Repository`, `WindowStartUtc`, `WindowEndUtc`,
    `TypePrefixesJson` (default `["DOCUMENT.","DECOMPOSITION.","PLAN.","PR.","BLOCKER.","CYCLE.","DEPLOY."]`),
    `MaxEvents` (aggregate cap, default 2000 — now honest, because it is a **paging** cap, not a
    single-page limit; `LifecycleReEntryService.MaxEventsPerFamily = 2000` is the same posture,
    `LifecycleReEntryService.cs:30`).
  - **Outputs**: `EventsJson` (neutral Core DTO list: `{eventId, type, createdAtUtc, issueId, repository,
    status, summary}`), `EventCount`, `EvidenceIndexJson` (the id set D4's ring reads), **`Truncated`**
    (true iff a further page existed when `MaxEvents` was reached — carried into the digest summary and
    the `.DIGEST` event data, per the Risks section).
  - **The read is `QueryEventsAsync`, not `ListByTenantAsync`.** The sibling method has exactly the
    surface needed: `type` + `typeIsPrefix`, a half-open `[from, to)` window pushed **into SQL**
    (`EventRepository.cs:558-568`), keyset pagination on the `SequenceNumber` BIGSERIAL total order
    (`:576-587`), and an **opt-in** total (`:574`). Signature at `IEventRepository.cs:200-207`.
    **This is the method the plan's own cited precedent already uses**: `LifecycleReEntryService`'s
    `LoadEventRowsAsync` calls `QueryEventsAsync(tenant, "DOCUMENT.", typeIsPrefix: true, …)` at
    `LifecycleReEntryService.cs:91-100`. The original D2 named that file as its model and then chose the
    method it does not use.
  - **Paging**: per prefix, call with `from: WindowStartUtc`, `to: WindowEndUtc`, `cursor: null` then the
    last row's `SequenceNumber`, `limit: 200`, `includeTotal: false`; accumulate until a short page or
    `MaxEvents` rows. `Truncated = true` iff the cap was hit with a full page in hand.
  - **Where the call runs — pick D2a or D2b and prove it with a test. Do not leave this implicit.**
    - **D2a (preferred) — over the engine→API hop**, matching every other engine-side data access. Add a
      `QueryEventsAsync`-shaped method to `TammaApiClient` against
      `GET /api/engine/events/query` (`Tamma.Api/Program.cs:3091` → `EngineEndpoints.QueryEvents`,
      `EngineEndpoints.cs:171-252`), which is already backed by `IEventRepository.QueryEventsAsync` with
      native `from`/`to` and its own `Math.Clamp(limit ?? 50, 1, 200)` (`EngineEndpoints.cs:199`) and
      `nextCursor`/`hasMore` (`:232-241`).
      **Open item the implementer MUST close:** the route sits on the `/api/engine` group under
      `RequireAuthorization("WorkflowsView")` (`Program.cs:3082`; policy = `PermissionRequirement(
      "workflows:view")`, `:1695-1699`), and its tenant comes from `ITenantContext`, **not** a parameter —
      an unresolved tenant returns an **empty page** (`EngineEndpoints.cs:203-215`). Either prove the
      engine service principal + `X-Tenant-Id` resolves a tenant on this route, or add an
      `EngineServiceOnly` variant taking `tenantId` explicitly (`Program.cs:1714-1719`).
      **An empty page from an unresolved tenant is precisely the silent-empty-window failure this D2
      forbids** — so the activity must treat "tenant did not resolve" as
      `TammaError STANDUP.WINDOW.NO_TENANT`, distinguishable from `EventCount == 0`.
    - **D2b — register the data layer in the engine host.** Larger blast radius, and not this story's call
      to make alone: `LifecycleReEntryService` is *already* registered in the engine
      (`Tamma.ElsaServer/Program.cs:193`, since `Documents:ReEntryDisabled` is set nowhere) over
      `IDocumentInstanceRepository` + `IEventRepository` + `ITenantContext` that the host does not
      provide. Registering `AddTammaData` there would change that service's behaviour as a side effect.
      **See Risks — this looks like a pre-existing defect and is reported, not fixed, here.**
  - **Fail-loud, unchanged and extended:** a missing/unreachable read path is
    `TammaError STANDUP.WINDOW.SERVICE_UNREGISTERED` (D2b) or `STANDUP.WINDOW.READ_FAILED` (D2a transport
    failure) or `STANDUP.WINDOW.NO_TENANT`; **never** an empty window.
- **D3 — the window IS the lifecycle issue id, and that is what makes AC1's idempotency real.** A standup
  digest has no `issueId`, but `ComputeReEntryPositionActivity`, `GetLatestAcceptedAsync` and the 39-11
  read are all keyed on one. Generalise 39-15's `CreationBindingHelper.ScopeIssueId`: the binding computes
  `issueId = "standup:{repository}:{windowStartUtc:yyyy-MM-dd}"` (normalised through the same segment
  transform). Consequence: **a duplicate fire for the same window re-enters at `Complete` and
  short-circuits** — emitting `DOCUMENT.REENTERED` and no second `DOCUMENT.ACCEPTED` — so AC1's "re-running
  the same window is a no-op re-read" is delivered by the existing 39-10 machinery rather than by new
  code. The scheduler seam still needs its own durable fire-once record (a lost fire is a different
  failure from a duplicate one), but the *document* side is idempotent for free.
- **D4 — "cites concrete DCB evidence" is enforced with a `validationContextJson` ring, not a prose
  hope.** `FetchEventWindowActivity` emits `EvidenceIndexJson` (the set of event ids the window contains);
  the binding forwards it as `validationContextJson`; `FindingsDocumentType` gains a
  `ValidateWithContext` override (the 39-15 D3 seam — **`IDocumentType.cs:43-44`**, a default interface
  member whose default body is `=> Validate(payload)`; *line ref corrected 2026-08-01, was cited as
  `:35-43`*) that — **only when the context is non-empty** — checks citations. Empty context ⇒ identical
  to today ⇒ `research` and `triage-context-gathering` are byte-behaviour-stable. The two landed
  precedents for this override shape are `UxSpec.cs:177-180` and `AcceptanceCriteria.cs:193-196`; 41-10
  uses the same conditional-rule shape for design facets and the two stories should land it consistently.

  **AMENDED 2026-08-01 (Correction 8) — the rule is ≥1 anchored citation, not all.**
  > *Superseded:* *"asserts **every** citation string resolves to an id in the index, with a new violation
  > `CITATION_UNKNOWN_EVENT`."*

  **The rule:** for each finding, **at least one** entry of `citations` must resolve to an event id
  present in the evidence index. Remaining entries are unconstrained. Violation constant:
  **`CITATION_UNANCHORED`** (`"Finding {label} anchors to no event in the reported window — at least one
  citation must be an event id from the window."`). **Why not "every":** the shipped prompt's own citation
  vocabulary is *"the event ids / issue refs / PR refs this is based on"*, and the story's Produced-document
  section requires `issueId`/`repository` lineage per finding — an all-citations rule would reject a PR or
  issue ref and become a second false-rejection source beside the one this ring removes. A wholly
  fabricated finding still fails; a well-evidenced finding that also names a PR does not.

  **41-11 shares this ring** (`story-41-11/implementation-plan.md:153`) and must use the same constant —
  land 41-7 first so there is one author (see Blocks/Blocked by).

  **D4 is inert without Correction 7's prompt v2.** The ring's only remedy is the model changing its
  citations, and the shipped template renders no feedback carrier — so shipping D4 alone converts every
  citation violation into an escalation. Steps 4 and 4b must land together.
- **D5 — the empty window short-circuits before the dispatch (Correction 1).** Graph node
  `WindowHasMaterial` `FlowDecision` on `EventCount > 0`: False → `EmitStandupSkipped` → `ExposeOutput`
  with `status = "skipped"`; True → `DispatchLifecycle`. This is a typed-value branch (39-12 D2's
  sanctioned kind), not a quality decision, and the structure test pins the `FlowDecision` id set so a
  parse gate cannot reappear.
- **D6 — `STANDUP.*` is a five-member family.** `SYNTHESIS.STARTED` / `.DIGEST` (the story's two) plus
  `.SKIPPED` (D5), `.BLOCKER_FLAGGED` (per flagged item, carrying the owning role — Correction 3's
  claimable half) and `.FAILED` (LOUD, on `rejected`/`escalated`). New
  `Tamma.Activities/Standup/StandupEvents.cs` + `EmitStandupEventActivity.cs`. All tagged `repository`,
  `tenantId`, `windowStartUtc`, `windowEndUtc`.
- **D7 — acceptance policy is passed through.** `AcceptanceDefaults.For(DocumentTypeKey.Findings)` falls
  to the `_ => Rules` catch-all (single `architect`, unanimous) — wrong for a standup digest, and
  **`AcceptanceDefaults.cs` is not this story's file to edit** (it is per document type, shared with
  `research` and `triage-context-gathering`). The binding forwards a caller-supplied
  `acceptanceRulesJson`, and the story ships a documented default (scrum-master reviewer at autonomy
  70–84, self-accept at 85–100) as configuration, not code.
- **D8 — the lockstep set, enumerated. PIN NUMBERS CORRECTED 2026-08-01.**
  (i) `DocumentTypeRegistry.BuildSeed` += `new WorkflowDocumentInterface("standup-synthesis", empty,
  DocumentTypeKey.Findings, false)` — append after the `adr-authoring` row
  (`DocumentTypeRegistry.cs:201-203`);
  (ii) `WorkflowInterfaceGraphTests.Declared_edge_count_is_pinned` **`HaveCount(18)` → `HaveCount(19)`**
  (`WorkflowInterfaceGraphTests.cs:52`);
  (iii) that test's `reconciled` array += `"standup-synthesis"` (`:109-138`) — note the array is
  **BIDIRECTIONAL** (its own comment, `:131-133`): a non-provisional seed row omitted here fails the
  build, so (i) and (iii) must land in the same commit;
  (iv) `ContractBindingTests.Bindings` (`ContractBindingTests.cs:94`) +=
  `[("scrum_master", "synthesize-standup")] = new("FindingsDocumentType.Validate", [...the seven Findings
  token groups...])` — copy `(product_owner, research)` verbatim from `ContractBindingTests.cs:103-108`
  (`"summary"`, `"findings"`, `"title"`, `"relevance"`, `"confidence"`, `"citations"`,
  `"overallConfidence"`);
  (v) `TaxonomyDriftBuildTests.ExpectedContributingWorkflows` += `"StandupSynthesisWorkflow"`
  (`TaxonomyDriftBuildTests.cs:125-150`);
  (vi) NO `ResumableStandardStructuralTests` allowlist entry (`ResumableStandardStructuralTests.cs:43+`);
  the workflow declares `[ResumeBehavior]`, and the allowlist is a ratchet that only shrinks.

  > **What this said and why it was wrong (2026-08-01).** It said *"(ii) …`HaveCount(16)` → `+1`"* and
  > that the taxonomy pins read *"`AgentRoleTests.cs:12` `Be(8)`, `AgentActionTests.cs:38` `Be(80)`,
  > `RolePhaseMapTests.cs:64` `HaveCount(80)`, `SystemPromptsTests.cs:61` `HaveCount(8)`"*. **Every one of
  > those numbers is stale.** The edge pin moved 16 → 17 (41-2, `WorkflowInterfaceGraphTests.cs:45-49`)
  > → 18 (41-9, `:50-52`), so this story's bump is **18 → 19**. And 41-1a has landed, so the taxonomy pins
  > already read `Be(11)` (`AgentRoleTests.cs:11`), `Be(96)` (`AgentActionTests.cs:42`), `HaveCount(96)`
  > (`RolePhaseMapTests.cs:74`), `HaveCount(11)` (`SystemPromptsTests.cs:63`),
  > `ConventionStoreEndpointsTests.cs:720-722`.

  **The taxonomy count pins are still not this story's to touch** — 41-1a moved them once for all three
  roles and sixteen cells, and they are already at their post-41-1a values. Touching them is a defect.

  **The edge-count pin is the epic's merge-rate limiter** (`epic-41/README.md` planning-artifacts note):
  it serializes against every other Epic 41 producer. Re-read `WorkflowInterfaceGraphTests.cs:52` at
  implementation time rather than trusting `19` — another producer may land first.

## Implementation Steps

1. **Precondition gate (no code). AMENDED 2026-08-01 — this gate is already satisfied, except for one
   clause that was never satisfiable.**
   *Superseded text:* *"Verify `AgentRole.ScrumMaster` exists, `(scrum_master, synthesize-standup)` passes
   `RolePhaseMap.IsRoleEligibleForPhase`, and `Prompts/scrum_master/synthesize-standup.md` +
   `Prompts/scrum_master/_system.md` exist with the `Findings` token groups **and a declared
   `contextFindings` carrier** (D8(iv) / the render-drop lesson). Any gap is a 41-1a defect — file it
   there."*
   - The first three clauses are **verified satisfied** (2026-08-01): `AgentRole.cs:23`,
     `RolePhaseMap.cs:178-181` (pinned `RolePhaseMapTests.cs:742`), both prompt files present.
   - **The `contextFindings` clause was never a 41-1a deliverable and is not a 41-1a defect.** No shipped
     Findings-producing prompt declares a feedback carrier — not `research.md`
     (`variables: role, workItemJson, findings, conventions`), not `triage-context-scan.md`
     (`variables: role, workItemType, workItemJson, previousFindings, repository`), not
     `synthesize-standup.md`. Filing it against 41-1a (which is `done`) would bounce. **It is now this
     story's step 4b** — see Correction 7.
   - Re-run this gate anyway as a 5-minute sanity check before writing code; it is cheap and the taxonomy
     is a shared surface.
2. **CREATE `apps/tamma-elsa/src/Tamma.Core/Documents/EventWindowRow.cs`** — the neutral, Core-visible
   window DTO (`Tamma.Core` cannot see `Tamma.Data`, exactly as `LifecycleResumeCalculator`'s
   `ResumeEventRow` cannot).
3. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Documents/FetchEventWindowActivity.cs`** per D2.
4. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/Types/Findings.cs`** per amended D4 —
   `CitationUnanchored` constant (`"CITATION_UNANCHORED"`, renamed from the plan's original
   `CitationUnknownEvent` per Correction 8) + `ValidateWithContext` override (copy the
   `DocumentPayloadGuard.Run(payload, p => ValidateWithContextCore(p, validationContextJson))` shape from
   `UxSpec.cs:177-180` / `AcceptanceCriteria.cs:193-196`) + a context-bearing example. **`Validate` is
   untouched**, so `research` / `triage-context-gathering` are byte-behaviour-stable.
4b. **MODIFY `apps/tamma-elsa/src/Tamma.Api/Prompts/scrum_master/synthesize-standup.md` `v1 → v2`** per
   Correction 7 — the three changes listed there, `version: 1` → `2`. **Must land in the same commit as
   step 4**: D4's ring without the rendered feedback carrier is a pure escalation generator. Do not touch
   the JSON contract block (its seven token groups are pinned by D8(iv)).
5. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Standup/StandupEvents.cs` +
   `EmitStandupEventActivity.cs`** (D6).
6. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/StandupBindingHelper.cs`** — pure:
   `BuildWindowIssueId(repository, windowStartUtc)` (D3), `BuildEvidenceContext(evidenceIndexJson)`,
   `ProjectDigest(documentJson)`, `ExtractFlaggedBlockers(documentJson)` (→ the `.BLOCKER_FLAGGED` rows),
   `BuildFailureDetail(exit)`. `ReadLifecycleResult`/`IsAccepted` from `LifecycleBindingHelper`.
7. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/StandupSynthesisWorkflow.cs`** —
   `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`; graph `ReadInputs` → `ComputeReEntryPosition`
   (`documentType = "findings"`, `IssueId = WindowIssueId`) → `ReadPositionStage` → `FreshRun` → (True)
   `EmitStandupStarted` + `FetchEventWindow` → `WindowHasMaterial` (D5) → `DispatchLifecycle` →
   `ReadLifecycleExit` → `DigestAccepted` → `EmitStandupDigest` + `EmitBlockerFlagged` /
   `EmitStandupFailed` → `ExposeOutput`. Exactly ONE `DispatchWorkflow`, target `document-lifecycle`.
8. **MODIFY `DocumentTypeRegistry.cs` + the four pins** — D8(i)–(v).
9. **CREATE the tests** — see Test Plan. At this point the story is shippable and dispatchable by API;
   only the cadence is missing.
10. **The cadence opt-in — AMENDED 2026-08-01. NOT IN SCOPE, and no longer blocked.**
    *Superseded text:* *"**`TODO(scheduler-seam)` — the trigger.** **NOT buildable today.** When the seam
    exists it must supply: a tenant component in the advisory-lock key; `tenantId` + `repository` +
    `windowStartUtc`/`EndUtc` threaded into the dispatch input; a **persisted** last-fired window per
    `(tenant, workflow, window)`; and a window/cron shape rather than one `FireAtMinute`. …"*

    **"NOT buildable today" is false as of 41-30.** The seam ships all four listed requirements — see the
    amended banner for the file:line evidence. What remains true is that this story **does not** turn the
    cadence on: per the 2026-07-25 decision, standup synthesis is user-initiated. Recorded here so a
    future reader knows the opt-in is a *decision*, not a *gap*:

    - `standup-synthesis` is deliberately absent from `SchedulableDefinitions.Allowed`
      (`ScheduledTriggerEndpoints.cs:25-36`), the closed allowlist that exists precisely because an
      admin-writable `definition_id` is otherwise an arbitrary-workflow-dispatch privilege-escalation
      surface (its own comment, `:11-17`).
    - Turning the cadence on later is: add `"standup-synthesis"` to that set, then `POST
      /api/admin/scheduled-triggers` with `{definitionId, cronExpression, inputJson}`. The seam merges
      `InputJson` into the dispatch (`TenantScheduledTriggerService.cs:731`) and stamps
      `input["tenantId"]` itself (`:745`), so this workflow's consumption surface —
      `tenantId`, `repository`, `windowStartUtc`, `windowEndUtc` — is satisfied by `tenantId` from the
      seam plus the other three from `InputJson`.
    - **Do not build a 41-7-local scheduler.** That warning in Risks stands and is now doubly true: the
      shared seam exists.
    - One caveat for whoever does opt in: `TenantScheduledTriggerOptions.Enabled` defaults to `false`
      (`TenantScheduledTriggerService.cs:30`) and the service is registered only inside the
      control-plane-connection conditional (`Tamma.ElsaServer/Program.cs:205`, `:243-252`).

## Data & Migrations

None **for this story**. `Findings` payloads are JSONB in 39-11's tables; `STANDUP.*`/`DOCUMENT.*` ride
the existing drain. `has-pending-model-changes` stays clean. *(Amended 2026-08-01: this said "The
scheduler seam **will need** its own persisted last-fired table — that is the seam story's migration".
41-30 shipped it — `scheduled_triggers` + `scheduled_trigger_fires`,
`Tamma.Data/Entities/ScheduledTrigger.cs` / `ScheduledTriggerFire.cs`, control-plane residency. Still not
this story's migration; it simply already exists.)*

## Events

- **Emits:** `STANDUP.SYNTHESIS.STARTED` (fresh runs only), `.DIGEST` (on lifecycle `accepted`, data:
  `itemCount`, `blockedCount`, `documentId`), `.SKIPPED` (empty window, D5), `.BLOCKER_FLAGGED` (one per
  flagged item, data: `owningRole`, `issueId`, `evidence`), `.FAILED` (LOUD). Tags `repository`,
  `tenantId`, `windowStartUtc`, `windowEndUtc`, `correlationId`.
- **Consumes (the window read):** the configured type prefixes — `DOCUMENT.`, `DECOMPOSITION.`, `PLAN.`,
  `PR.`, `BLOCKER.`, `CYCLE.`, `DEPLOY.` — via **`IEventRepository.QueryEventsAsync`** (`typeIsPrefix:
  true`, half-open `[windowStart, windowEnd)`, keyset-paged), reached over the engine→API hop per amended
  D2. *(Corrected 2026-08-01: this line said `IEventRepository.ListByTenantAsync`, which has no time
  filter — see amended D2.)* **Read-only; this story adds no consumer to any family.**
- **Emitted by the machinery this story wires in:** `DOCUMENT.*`, `APPROVAL.*`, `ESCALATION.TRIGGERED`.

## Test Plan

- **`FetchEventWindowActivityTests` (mocked read seam) — AMENDED 2026-08-01 for the re-pointed D2.**
  *Superseded first clause: "(Moq'd `IEventRepository` + `ITenantContext`)".* Mock whichever seam D2a/D2b
  selects (D2a ⇒ the `TammaApiClient` window method). Assertions: **the window is pushed into the query,
  not filtered in memory** — assert on the call arguments that `from == WindowStartUtc` and
  `to == WindowEndUtc` were passed (this is the regression that catches a silent re-slide back to
  `ListByTenantAsync`); an event one second outside the window is excluded; the prefix set is honoured
  (one call per prefix, `typeIsPrefix: true`); **paging works** — a full page followed by a short page
  yields the union, and the cursor sent on page 2 is page 1's last `SequenceNumber`; `MaxEvents` caps the
  accumulation and sets `Truncated == true` when a further page existed; an unreachable read seam throws
  `STANDUP.WINDOW.SERVICE_UNREGISTERED` / `STANDUP.WINDOW.READ_FAILED` and an unresolved tenant throws
  `STANDUP.WINDOW.NO_TENANT` (**never** an empty window — D2); zero matching events yields
  `EventCount == 0` with a well-formed empty `EventsJson`; cross-tenant rows are never requested
  (asserted on the call arguments).
- **`FindingsCitationContextTests` (Tamma.Core.Tests, pure) — AMENDED 2026-08-01 for the ≥1-anchor rule.**
  With a non-empty evidence index: a finding whose citations contain **no** id from the index ⇒
  `CITATION_UNANCHORED`; a finding citing one known id ⇒ valid; **a finding citing one known id PLUS a
  free-form PR ref ⇒ valid** (the case the superseded "every citation" rule would have wrongly rejected —
  Correction 8). **Regression pin: the SAME payloads validate clean with an EMPTY context** (`research` /
  `triage-context-gathering` unaffected — this is the guard that keeps `Validate` untouched). Plus the
  inherited matrix: empty findings list ⇒ `EMPTY_FINDINGS` (the fixture that makes Correction 1 concrete);
  a finding with no citations ⇒ `MISSING_EVIDENCE`; relevance 1.5 ⇒ `RELEVANCE_OUT_OF_RANGE`.
  **Covers AC2.**
- **`StandupPromptContractTests` (new, 2026-08-01 — Correction 7).** Over the loaded
  `Prompts/scrum_master/synthesize-standup.md`: front matter is `version: 2`; `variables` declares
  `contextFindings`; the body contains a `{{contextFindings}}` placeholder; and the body does **not**
  contain the phrase "citing the empty window" (the v1 clause that drove the false-escalation loop). The
  last one is the pin that stops a future edit from reintroducing the conflict silently.
- **`StandupSynthesisWorkflowStructureTests`** — the `TaskCreationWorkflowStructureTests` clause set:
  `DefinitionId == "standup-synthesis"`; threads `TenantId`; no retry-plumbing variables; **exactly one
  `DispatchWorkflow`, literal id `document-lifecycle`**; zero `llm-call`; **zero `Finish`**;
  `ComputeReEntryPositionActivity` + `FetchEventWindowActivity` present; declares `LatestStateReEntry`;
  no `Wait*` node; `FlowDecision` id set pinned to exactly `{FreshRun, WindowHasMaterial, DigestAccepted}`;
  `ScanLifecycleBindingDispatches()` contains `(StandupSynthesisWorkflow, DispatchLifecycle, scrum_master,
  synthesize-standup)`; `MaterializeDispatchInput` yields `documentType == "findings"` and the declared
  `feedbackVariableName`. **Covers AC3 (structure half), rule-1 clauses (a)–(e).**
- **`StandupBindingHelperTests`** — `BuildWindowIssueId` is deterministic and tenant/repo/window-folded
  (two repos or two windows never collide); `ProjectDigest`/`ExtractFlaggedBlockers` on valid/unreadable
  JSON; `BuildFailureDetail` names each reachable outcome wire.
- **Pin tests (self-verifying)** — `WorkflowInterfaceGraphTests` (bumped, `standup-synthesis` in
  `reconciled`); `ContractBindingTests` (new entry satisfied by 41-1a's template);
  `TaxonomyDriftBuildTests`; `ResumableStandardStructuralTests` green with **no** allowlist entry.
  **Covers AC3 (gate half).**
- **`StandupSynthesisExecutionTests` (Testcontainers, shared 39-6/39-10 fixture)** — (a) happy path: seed
  a window of `DOCUMENT.*`/`BLOCKER.*` rows → valid digest draft → review → accept → accepted `Findings`
  readable by the window issue id, `.DIGEST` + `.BLOCKER_FLAGGED` rows present with the owning role.
  (b) **AC1 idempotency (D3):** dispatch the SAME window twice → the second run re-enters at `Complete`,
  emits `DOCUMENT.REENTERED`, produces no second document, and the stream carries exactly ONE
  `DOCUMENT.ACCEPTED` and ONE `STANDUP.SYNTHESIS.DIGEST`. (c) **empty window (Correction 1):** no seeded
  events → `STANDUP.SYNTHESIS.SKIPPED`, `status = "skipped"`, **zero** `document-lifecycle` instances
  started, zero `Findings` rows. (d) evidence ring: a draft whose findings anchor to no window event →
  `CITATION_UNANCHORED` → repair (the violation renders through `contextFindings`) → accept.
  (e) tenant isolation: two tenants' windows produce two independent documents and neither reads the
  other's events. (f) **AC4a:** a seeded window with two blocker signals yields exactly two
  `STANDUP.BLOCKER_FLAGGED` rows carrying `owningRole`; a window with none yields **zero**.
  (g) **AC4b:** accepting the digest yields exactly one `AcceptanceRequested` envelope with
  `Audience == orchestrator`. **Covers AC1, AC2, AC3 (re-entry half), AC4a, AC4b.**
- **Asserted ABSENT — AMENDED 2026-08-01. Three named tests, not a note.**
  *Superseded text:* *"**Not tested, by design:** AC4's role-scoped Task View delivery (Correction 3 — the
  resolver is fail-closed) and the broadcast half (Correction 4 — no executor). Both are asserted
  **absent**: a test pins that the workflow performs no publication side effect, so the gap is visible
  rather than implied."* — the intent was right, but "a test" was never specified, so in practice nothing
  would have been written and the gap would have stayed assumed. Specified now (**covers AC4c**). Each
  test carries a comment naming its burn-down story, per the `KnownContractViolations` ratchet
  discipline: **these MUST fail the day the stub is replaced.**
  1. **`TaskAudienceResolverStubPinTests`** (`Tamma.Api.Tests`) —
     `InitiatorOnlyTaskAudienceResolver.EligibleAudienceAsync(new TaskRef(tenant, InitiatorUserId: null,
     RepoKey: null, IssueId: "42"), "scrum_master")` returns an **empty** audience
     (`Tamma.Api/Services/Access/ITaskAudienceResolver.cs:49-56`). Burn-down: **39-20**.
  2. **`ChannelOutboxRoleFanOutIsEmptyPinTests`** (`Tamma.Api.Tests`) — enqueuing a role-addressed
     `TaskAssigned` through `ChannelOutboxService` mints **zero** outbox rows, because the one production
     call site hardcodes `InitiatorUserId: null` (`ChannelOutboxService.cs:143`) and only `TaskAssigned`
     fans out by role at all (`:140`). This is the pin that matters: it is the difference between
     "delivered to the wrong person" and "delivered to nobody while the run reports success". Burn-down:
     **39-20**.
  3. **In `StandupSynthesisWorkflowStructureTests`** — the built graph performs **no** publication side
     effect: exactly one `DispatchWorkflow` (literal `document-lifecycle`), zero `llm-call`, and no node
     reaching a chat or tracker. Burn-down: **42-9** (none of the six registered `IToolExecutor`s can post
     — `Tamma.Api/Program.cs:734-745`) and **39-19** (`AgentOfflineChatRelay` refuses every message,
     `Tamma.Api/Services/Channels/IOrchestratorChatRelay.cs:38-45`).

## Risks & Mitigations

- ~~**The scheduler seam is a separate story (41-30)…**~~ **RETIRED 2026-08-01** — 41-30 has landed; the
  cadence is a documented opt-in (amended step 10), not a risk. **The one line worth keeping: do not build
  a 41-7-local scheduler.** Six stories share the seam, and a local copy would be the second
  non-reusable one.
- ~~**41-1a is a hard gate on both paths.**~~ **RETIRED 2026-08-01** — 41-1a is `done`
  (`docs/sprint-status.yaml:629`) and every artifact is verified present (amended banner, amended step 1).
- ~~**41-1a's `scrum_master` alias removal is a live behaviour change** (`RolePhaseMap.cs:239` maps
  `scrum_master → product_owner` **today**…)~~ **RETIRED and CORRECTED 2026-08-01.** The alias is gone
  (`RolePhaseMap.cs:273-290` records the removal; `RolePhaseMapTests.cs:604-605` pins that
  `NormalizeRole("scrum_master") == "scrum_master"` and that `LegacyRoleAliases` does **not** contain the
  key). The migration risk is 41-1a's and is closed —
  `Tamma.Api.Tests/Agents/ProviderChainAliasMigrationTests.cs` pins the resolver path. Separately, the
  cited line number was wrong: `RolePhaseMap.cs:235-244` is the `ValidRoles` frozen set, not an alias map.
- **Touching `Findings.cs` risks regressing `research` / `triage-context-gathering`.** Mitigation: D4's
  rule is an override that no-ops on empty context; `Validate` is untouched; the regression pin asserts
  the sibling behaviour explicitly in both the unit and execution suites (the same guard 41-10 uses for
  `Design`). Note the two landed override precedents to copy: `UxSpec.cs:177-180`,
  `AcceptanceCriteria.cs:193-196`.
- **The window read is unbounded in principle. AMENDED 2026-08-01 — the original mitigation did not
  actually mitigate.** *Superseded:* *"A busy tenant's day could return far more than 2000 rows and
  silently truncate… Mitigation: `MaxEvents` is an explicit input, a truncated read sets a `Truncated`
  output flag…"* — with the original D2's `ListByTenantAsync` read, `Truncated` could not have been
  computed honestly: that method returns the **most-recent** N with no window predicate
  (`EventRepository.cs:263-267`), so the rows lost are the **oldest** ones in the window and the caller
  cannot tell truncation from a genuinely quiet morning. The amended D2 fixes the cause, not the symptom:
  the window is a SQL predicate (`EventRepository.cs:558-568`), paging walks the whole window in
  `SequenceNumber` order, and `Truncated` means exactly "a further page existed at the `MaxEvents` cap".
  The flag is still carried into the digest summary and the `.DIGEST` event data.
- **NEW 2026-08-01 — `IEventRepository` / `ITenantContext` are not registered in the engine host, and
  this is a *pre-existing* condition this story must route around, not inherit.** `AddTammaData` (which
  registers both — `Tamma.Data/DependencyInjection.cs:47`, `:193`) is called only from
  `Tamma.Api/Program.cs:203`; `Tamma.ElsaServer/Program.cs` registers from `Tamma.Data` only
  `ControlPlaneDbContext` (`:208-215`) and `IScheduledTriggerRepository` (`:243-244`). Mitigation: **D2a**
  (read over the engine→API hop), which is also how every other engine-side data access works
  (`EventPersistenceMiddleware.cs:180`, `PersistDocumentInstanceActivity` — both `TammaApiClient`).
  **Reported, not fixed here:** `LifecycleReEntryService` is registered in the engine host
  (`Tamma.ElsaServer/Program.cs:193`; `Documents:ReEntryDisabled` is set in no appsettings file) over
  exactly those unregistered dependencies. That is a defect in 39-10/39-11's wiring, outside this story's
  scope, and it is the reason D2b is not this story's call to make alone. *(Verified statically from DI
  registration sites only — the engine was not run. Confirm at runtime before relying on either branch.)*
- **Two `Findings` producers per window key.** 41-11's risk `Findings` and this digest must not share a
  lifecycle key; D3's `standup:` prefix and 41-11's own scope prefix keep them disjoint. Assert it in (e).

## Est. Effort

**Revised 2026-08-01.** Changes: step 1 shrinks (its gate is already satisfied), step 2–3 grows (the
engine→API window read + paging replaces a direct repository call), step 4b is new (the prompt v2
rewrite), step 9 grows (three asserted-absent pins + the prompt-contract test), step 10 is no longer
unestimable — it is out of scope by decision.

| Step(s) | Work | Days | Was |
|---|---|---|---|
| 1 | Precondition sanity check (41-1a already landed) | 0.1 | 0.25 |
| 2–3 | `EventWindowRow` + `FetchEventWindowActivity` **over the engine→API hop, with paging** (amended D2) | 1.5 | 1.0 |
| 4 | `Findings.ValidateWithContext` anchored-citation ring | 0.5 | 0.5 |
| **4b** | **`synthesize-standup.md` v1→v2 (Correction 7)** | **0.25** | **— (new)** |
| 5 | `StandupEvents` + emitter | 0.25 | 0.25 |
| 6 | `StandupBindingHelper` (pure) | 0.5 | 0.5 |
| 7 | `StandupSynthesisWorkflow` binding | 0.75 | 0.75 |
| 8 | Registry edge + the five pin edits (D8(i)–(v)) | 0.25 | 0.25 |
| 9 | Core + activity + structure + helper + prompt-contract + Testcontainers suites, **+ the 3 asserted-absent pins** | 1.9 | 1.5 |
| 10 | Cadence opt-in | **out of scope by the 2026-07-25 decision** — the seam (41-30) exists; see amended step 10 | *"not estimable — the seam does not exist"* |
| **Total (steps 1–9)** | | **6.0** | 5.0 |

Story estimate revised 4–5 d → **5.5–6 d** to match.

## Blocks / Blocked by

**AMENDED 2026-08-01 — the whole "Blocked by — hard, no owner" bullet is retired. This story has no
unlanded blocker.**

> *Superseded (verbatim):* *"**Blocked by — hard, no owner, cannot be worked around:** **The tenant-aware
> scheduled-trigger seam.** No story in Epic 41 builds it (`epic-41/README.md:297`, `:454-472`). AC1 is
> unreachable without it. …"* and *"**Blocked by — hard, owned:** **41-1a** …"*
>
> Both are false. **41-30 built the seam** (`docs/sprint-status.yaml:4`; code at
> `Tamma.ElsaServer/Workflows/TenantScheduledTriggerService.cs` + `Tamma.Data/Entities/ScheduledTrigger.cs`
> + `Tamma.Api/Endpoints/Admin/ScheduledTriggerEndpoints.cs`) and **41-1a is `done`**
> (`docs/sprint-status.yaml:629`). Independently, "AC1 is unreachable without it" was already superseded
> by the 2026-07-25 decision (AC1 is user-initiated and idempotent via D3's window key), so that clause
> was doubly wrong by the time it was written.

- **Blocked by — nothing unlanded.**
  - **41-1a — LANDED.** `AgentRole.ScrumMaster` (`AgentRole.cs:23`), the `synthesize-standup` cell
    (`AgentAction.cs:133`, `RolePhaseMap.cs:178-181`), its prompt files, and the alias removal
    (`RolePhaseMap.cs:273-290`).
  - **41-30 — LANDED.** The scheduled-trigger seam. Not needed by this story; relevant only to the step-10
    opt-in.
  - **Epic 39: 39-2/39-3** (`Findings` registered), **39-6**, **39-7**, **39-8**, **39-10**, **39-11**,
    **39-15** (the `ValidateWithContext` seam D4 rides, `IDocumentType.cs:43-44`) — **all landed**,
    verified in tree.
- **NOT blockers — asserted-absent gaps, pinned by AC4c:** **39-19** + **39-20** (role-scoped delivery —
  amended Correction 3), **42-9** (broadcast — Correction 4). *Reclassified 2026-08-01:* these were listed
  as *"Blocked by — for AC-level claimability"*, which invited the story to wait on them. It should not.
  The story claims 4a/4b and **pins** the absence of the rest with tests that fail when the stubs are
  replaced.
- **NOT blocked by:** **41-1b** (reuses `Findings`) and **41-1c** (produces a typed document, not prose).
  41-7 appears in neither the README's 41-1b nor its 41-1c table — correctly.
- **Blocks / feeds:** **41-8** (the retro consumes accepted standup digests — the `Findings` edge is a
  store read, so 41-8 needs 41-7 *landed*, not *scheduled*); **41-11**, **41-16**, **41-20**, **41-23**
  inherit D2's `FetchEventWindowActivity` and D3's window-as-issue-id idempotency trick.
- **Ordering — land 41-7 BEFORE 41-11 (added 2026-08-01).** Two surfaces are shared and must have one
  author, not two: `FetchEventWindowActivity` — 41-11's plan says *"build it per 41-7's D2 if 41-7 has not
  landed"* (`story-41-11/implementation-plan.md:196`) and budgets `0–1.0 d` for exactly that fork
  (`:319`) — and the `Findings.ValidateWithContext` citation ring (`:153`). 41-11 already records this as
  its *"soft / preferred ordering"* (`:345-346`) and names *"`FetchEventWindowActivity` gets built twice"*
  as a risk (`:310`). Landing 41-7 first removes both. **Note for 41-11:** the amended D2 (engine→API hop,
  `QueryEventsAsync`, paging) and the amended D4 constant name (`CITATION_UNANCHORED`) are what 41-11 will
  consume — its plan still describes the superseded shapes.
- **Shared edits:** `FetchEventWindowActivity` (with **41-11** — see ordering above);
  `WorkflowInterfaceGraphTests.Declared_edge_count_is_pinned` (**`WorkflowInterfaceGraphTests.cs:52`,
  `HaveCount(18)` today** — *corrected 2026-08-01, this line said `:45`, `HaveCount(16)`*) — moved by
  41-10 (+1), 41-11 (+2), this story (+1) and every other Epic 41 producer, and it is the epic's merge-rate
  limiter; `Findings.cs`'s `ValidateWithContext` override (this story) versus `Design.cs`'s (41-10) — the
  same conditional-rule pattern, land them consistently.
