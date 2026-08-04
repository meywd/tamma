# Story 41-7: Standup Synthesis Workflow

Status: drafted

> **Readiness note (2026-08-01) — no unlanded blocker remains.** Both gates this story named (41-1a, and
> the scheduled-trigger seam now owned by 41-30) are `done` in `docs/sprint-status.yaml`, and every
> artifact is verified present in the tree — see **Dependencies** and **Amendment A3**. The two remaining
> gaps (39-19/39-20 role-scoped delivery, 42-9 broadcast) are **not** blockers: AC4c pins them as
> asserted-absent. This story should land **before 41-11**, which shares `FetchEventWindowActivity` and
> the `Findings` citation ring — see **Sequencing**. The `Status:` token above is the coordinator's to
> move (`docs/sprint-status.yaml` is coordinator-only); this note records only what the tree supports.

## User Story

As a **scrum master** (or eligible role-holder), I want a scheduled workflow that reads the DCB event
stream for a team/repo over the last day and synthesizes a **standup digest** (what moved, what's blocked,
what's at risk) as a typed `Findings` document, so that the daily status picture is assembled from the
audit trail automatically instead of collected by hand.

## Priority

P1 / Wave 2 — recurring, event-sourced, compounding. Replaces a standing daily chore and showcases the
"read the stream on a cron" pattern for 41-11/41-16/41-20/41-23.

## Scope

User-initiated run over a configured team-window (per the 2026-07-25 scheduling decision, ceremonies are
user-initiated — a cron cadence is a later opt-in through 41-30; `HourlyAnalyticsRollupScheduler` is not a
reusable pattern) → thin binding over `document-lifecycle`. `consumes: [DCB events window, open
Decompositions/Plans/PRs, blocker events]` / `produces: Findings`. Produce cell
`(scrum_master, synthesize-standup)` (41-1a — **landed**, see Amendment A3).

**Also in scope (added 2026-08-01, Amendment A2):** a `v1 → v2` rewrite of the shipped producer template
`apps/tamma-elsa/src/Tamma.Api/Prompts/scrum_master/synthesize-standup.md`. The shipped v1 instructs the
model to manufacture a citation on a quiet day, which the citation-validation ring this story adds would
reject on every quiet day. The same rewrite adds the repair/revise feedback carrier the template lacks.
This follows the established Epic 41 shape: 41-2 rewrote `define-acceptance-criteria.md` v1→v2 in-story
(now `version: 2`, `variables: role, workItemJson, contextFindings, conventions`) and 41-9 rewrote
`write-adr.md` the same way.

## Produced document

`Findings`: each item cites its source event(s) as evidence; ranked by risk; blocked/at-risk items flagged
with the owning role. `issueId`/`repository` lineage on every finding.

## Events

`STANDUP.SYNTHESIS.STARTED` → `.DIGEST` (or `.SKIPPED` for an empty window) alongside `DOCUMENT.*`,
tagged `repository`/`tenantId`/window.

## Orchestrator / user interaction

The accepted digest is delivered to the team via the orchestrator (chat post + Task View items for each
flagged blocker, routed to the owning role). An empty window **short-circuits before dispatch**: it emits
`STANDUP.SYNTHESIS.SKIPPED` and produces no document (no false noise — `FindingsDocumentType`
deliberately rejects an empty findings list with `EMPTY_FINDINGS`, so "an empty-but-valid `Findings`" is
not a thing the type permits).

## Autonomy behavior

- **70–84:** agent drafts; scrum master reviews before the digest is broadcast.
- **85–100:** agent synthesizes and self-accepts; each flagged blocker still routes to its owning role's
  Task View as an assigned follow-up.

> **Epic 42 caveat — the agent path cannot *broadcast* yet.** Publishing the digest to a chat/tracker
> needs an authenticated HTTP / external-API tool (**42-9**); the six registered `IToolExecutor`s
> (`Tamma.Api/Program.cs:734-745` — `FileReadTool`, `FileWriteTool`, `SearchCodeTool`, `ShellExecuteTool`,
> `GitOperationsTool`, `RunTestsTool`) are all coding-oriented. Synthesis is agent-reachable; delivery is
> **human-assigned** (rule 4) until 42-9 lands.
> *(Line ref corrected 2026-08-01 — this line said `Program.cs:753-764`; that range is now the run-tap /
> tool-loop registrations. The six executors are at `:734-745`; the count and the conclusion are
> unchanged. The epic README carries the same stale-ref problem at `README.md:507` for
> `InitiatorOnlyTaskAudienceResolver` — flagged there, not fixable from this story directory.)*

## Acceptance Criteria

1. Tenant-scoped, idempotent per window (re-running the same window is a no-op re-read); user-initiated
   per the 2026-07-25 scheduling decision.
2. Every finding cites concrete DCB evidence; confidence/relevance ∈ [0,1]. An empty window produces **no
   document and a `STANDUP.SYNTHESIS.SKIPPED` audit row** — the run short-circuits before dispatch with
   `status = "skipped"`; never an empty `Findings` (the type rejects one with `EMPTY_FINDINGS`) and never
   a false digest.
3. `[ResumeBehavior(LatestStateReEntry)]`; 39-10 structural test green without allowlist.
4. **(narrowed 2026-08-01 — Amendment A1; superseded text quoted there.)** Split into three testable
   parts, two positive and one asserted-absent:
   - **4a — flagging.** Each flagged blocker in the accepted digest emits exactly one
     `STANDUP.BLOCKER_FLAGGED` event carrying `owningRole`, `issueId` and ≥1 evidence event id drawn from
     the window, tagged `repository`/`tenantId`/`windowStartUtc`/`windowEndUtc`. A digest with no flagged
     items emits **zero** such rows (an always-emits implementation fails this).
   - **4b — acceptance request.** Accepting the digest results in exactly one `AcceptanceRequested`
     envelope on the `orchestrator` channel audience. This half is reachable today: `EngineChannelPublisher`
     is the registered `IAcceptanceRequestPublisher` in the engine host
     (`Tamma.ElsaServer/Program.cs:175-183`), and `PublishAsync(AcceptanceRequest, …)` builds a
     `ChannelAudience.Orchestrator` envelope (`EngineChannelPublisher.cs:45-62`).
   - **4c — delivery is ASSERTED ABSENT, not assumed.** Role-scoped delivery of a flagged blocker to the
     owning role does not happen, and the story must **pin that with tests that fail the day it starts
     happening** (ratchet discipline, the `KnownContractViolations` shape). Three pins, each naming its
     burn-down story:
     1. `InitiatorOnlyTaskAudienceResolver.EligibleAudienceAsync(new TaskRef(tenant, InitiatorUserId: null,
        RepoKey: null, IssueId: issue), "scrum_master")` returns an **empty** audience
        (`ITaskAudienceResolver.cs:45-56`). Burn-down: **39-20**.
     2. A role-addressed `TaskAssigned` enqueued through `ChannelOutboxService` mints **zero** outbox rows,
        because its one production call site hardcodes `InitiatorUserId: null`
        (`ChannelOutboxService.cs:143`) and the fan-out logs
        `"…resolved zero recipients… nothing enqueued (fail-closed)"` (`:169-172`). Burn-down: **39-20**.
     3. `StandupSynthesisWorkflow`'s built graph performs **no** publication side effect: exactly one
        `DispatchWorkflow` (target `document-lifecycle`), zero `llm-call`, and no node that reaches a chat
        or tracker. Burn-down: **42-9** (no `IToolExecutor` can post — see the Epic 42 caveat) and
        **39-19** (`AgentOfflineChatRelay` refuses every chat message, `IOrchestratorChatRelay.cs:38-45`).

   Each of these three CAN fail — 4c.1/4c.2 fail when the stub is replaced, 4c.3 fails if a publication
   node is added — so 4c is a real AC, not a prose disclaimer.

## Dependencies

- **Blocking: NONE (amended 2026-08-01 — Amendment A3).** Both former blockers have landed.
  - **41-1a — LANDED.** `AgentRole.ScrumMaster` (`Tamma.Core/Agents/AgentRole.cs:23`,
    `[Wire("scrum_master")]`), `AgentAction.SynthesizeStandup` (`AgentAction.cs:133`,
    `[Wire("synthesize-standup")]`), the cell is eligible (`RolePhaseMap.cs:178-181`), the
    `scrum_master → product_owner` alias is removed (`RolePhaseMap.cs:288`, pinned by
    `RolePhaseMapTests.cs:604-605`), the role's six prompt files exist including
    `Prompts/scrum_master/synthesize-standup.md`, and `docs/sprint-status.yaml:629` records
    `41-1a-agent-taxonomy-extension: done`. The taxonomy pins have already moved: roles `Be(11)`
    (`AgentRoleTests.cs:11`), actions `Be(96)` (`AgentActionTests.cs:42`), `ValidActions` `HaveCount(96)`
    (`RolePhaseMapTests.cs:74`), `RoleSystemPrompts` `HaveCount(11)` (`SystemPromptsTests.cs:63`).
  - **Epic 39 — LANDED** (`Findings`, lifecycle, store, 4-7 query API). `Findings` is
    `Tamma.Core/Documents/Types/Findings.cs`; the 39-15 `ValidateWithContext` seam is
    `IDocumentType.cs:43-44`.
- **Related:** feeds 41-8 retro input. Per the 2026-07-25 scheduling decision, standup synthesis is
  **user-initiated** — this story is not blocked on the scheduled-trigger seam. (*The old blocking line's
  finding stands as history:* `HourlyAnalyticsRollupScheduler` *is hardcoded to one workflow
  (`:199-200`), threads no `tenantId` (`:203`), keeps its last-fired window in-process (`:84`), and its
  advisory-lock key has no tenant component (`:242`) — which is exactly why 41-30 exists.*)
- **41-30 — LANDED (new, 2026-08-01).** The tenant-aware scheduled-trigger seam ships:
  `TenantScheduledTriggerService` (`Tamma.ElsaServer/Workflows/TenantScheduledTriggerService.cs`),
  the `scheduled_triggers` registry + `scheduled_trigger_fires` ledger
  (`Tamma.Data/Entities/ScheduledTrigger.cs`, `ScheduledTriggerFire.cs`), a tenant-scoped advisory-lock
  key (`ScheduleLockKey.Compute(tenantId, trigger.Id, windowKey)`, `:359`), `tenantId` threaded into the
  dispatch input (`:745`), an arbitrary-definition dispatch (`DispatchWorkflowDefinitionRequest(fire.DefinitionId)`,
  `:648`), cron windows, and admin CRUD at `/api/admin/scheduled-triggers`
  (`Tamma.Api/Endpoints/Admin/ScheduledTriggerEndpoints.cs`). `docs/sprint-status.yaml:4` records it
  `done`. It is **off by default** (`TenantScheduledTriggerOptions.Enabled = false`, `:30`) and gated by
  a closed allowlist that does **not** contain `standup-synthesis` (`SchedulableDefinitions.Allowed`,
  `ScheduledTriggerEndpoints.cs:25-36`). Adding this workflow to that allowlist is the "later opt-in"
  the Scope section names — deliberately NOT done in this story, per the 2026-07-25 decision.

### Sequencing (added 2026-08-01)

Land **41-7 before 41-11**. Two surfaces are shared and should have one author:
`FetchEventWindowActivity` (41-11's plan says *"build it per 41-7's D2 if 41-7 has not landed"* —
`story-41-11/implementation-plan.md:196`, and budgets `0–1.0 d` for exactly that fork at `:319`) and the
`Findings.ValidateWithContext` citation ring (`story-41-11/implementation-plan.md:153`). 41-11's own plan
already records this as its *"soft / preferred ordering"* (`:345-346`). Landing 41-7 first removes the
fork and the duplicate-build risk 41-11 names at `:310`.

## Estimated Effort

**5.5–6 days (revised 2026-08-01).** Was 4–5 days. The plan's step table already totalled 5.0 d for steps
1–9; this revision adds the in-scope `synthesize-standup.md` v1→v2 rewrite (Amendment A2, ~0.25 d) and
the window-read re-point onto the engine→API hop (Amendment A4, ~0.5 d over the original repository-call
design). See the implementation plan's Est. Effort table for the breakdown.

---

## Amendments

Every entry states what the story SAID, that it was wrong (or has been overtaken), and what is true in
the tree today, with the evidence. Nothing above was silently rewritten.

### A1 — AC4 narrowed and made asserted-absent (2026-08-01)

**What AC4 said (verbatim, superseded):**

> 4. Each flagged blocker is emitted as a `STANDUP.BLOCKER_FLAGGED` row carrying the owning role, and the
>    accepted digest publishes an `AcceptanceRequest` on the orchestrator channel; **role-scoped Task View
>    delivery is unreachable until 39-19/39-20 land** (the audience resolver is the fail-closed
>    `InitiatorOnlyTaskAudienceResolver` stub).

**Why that was not good enough.** The unreachability claim was correct but was written as prose. An AC
whose second half is an unverified disclaimer cannot fail, and the epic README lists 41-7's AC4 among
three ACs that *"fail at the AC level, not merely in prose"* (`epic-41/README.md:507`, `:511`). A future
reader had no way to tell whether the gap was still real.

**What is true in the tree (verified 2026-08-01).**

| Claim | Evidence |
|---|---|
| The audience resolver is still the fail-closed stub | `Tamma.Api/Program.cs:409-411` registers `ITaskAudienceResolver` → `InitiatorOnlyTaskAudienceResolver`; the implementation is `Services/Access/ITaskAudienceResolver.cs:45-56` |
| Chat relay is still off | `Tamma.Api/Program.cs:412-415` registers `IOrchestratorChatRelay` → `AgentOfflineChatRelay`, which returns `ChatRelayResult.Offline` for both directions (`Services/Channels/IOrchestratorChatRelay.cs:38-45`) |
| **The stub does not merely narrow the audience — it empties it** | `ChannelOutboxService.PersistUserFanOutAsync` builds `new TaskRef(envelope.TenantId, InitiatorUserId: null, RepoKey: null, IssueId: task.IssueId)` (`ChannelOutboxService.cs:143`). With a null initiator the stub returns `Array.Empty<AudienceMember>()`, so **zero** outbox rows are minted and the service logs `"…resolved zero recipients… nothing enqueued (fail-closed)"` (`:169-172`) |
| Only `TaskAssigned` fans out by role at all | `ChannelOutboxService.cs:140` — `if (envelope.Message is not TaskAssigned task) return … PersistSingleAsync(…)` |
| The `AcceptanceRequest` half IS reachable | `EngineChannelPublisher` is registered as `IAcceptanceRequestPublisher` in the engine host (`Tamma.ElsaServer/Program.cs:175-183`) and publishes a `ChannelAudience.Orchestrator` envelope (`EngineChannelPublisher.cs:45-62`) |

Note the failure mode is worse than "narrowed": `TrackerAssigneeResolver`'s own doc comment records the
same finding independently — *"`EligibleAudienceAsync` returns EMPTY today for every input"*
(`Services/Tracker/TrackerAssigneeResolver.cs:15-22`). So a naive implementation would silently deliver
the digest's blockers to nobody and look successful.

**Resolution.** AC4 is split into 4a/4b (positive, deliverable) and 4c (asserted absent, pinned by three
tests that must fail the day 39-19/39-20/42-9 land). See the rewritten AC4 above.

### A2 — the shipped prompt vs. D4's citation ring: the prompt gives (2026-08-01)

**The conflict.** The shipped `apps/tamma-elsa/src/Tamma.Api/Prompts/scrum_master/synthesize-standup.md`
(`version: 1`) ends with:

> - `summary` is required and non-empty; `findings` MUST NOT be empty — a quiet day still yields a
>   "nothing moved" finding citing the empty window.

The implementation plan's D4 adds a `validationContextJson` ring over `FindingsDocumentType` that rejects
a citation which does not resolve to an event in the window. Those two instructions are directly opposed:
v1 tells the model to cite something it was not given, and D4 rejects exactly that. Every quiet day would
produce a violation the repair ring cannot fix — because the model is *following its instructions* — and
the run would exhaust to `escalated`. That is the false-escalation loop D4 exists to prevent, inverted.

**One of the two has to give. The prompt gives. Reasoning:**

1. **v1's premise is already unreachable.** D5's short-circuit means a genuinely empty window never
   reaches the model at all — the workflow emits `STANDUP.SYNTHESIS.SKIPPED` before dispatching
   `document-lifecycle` (AC2, D5). On the only path where the model IS invoked, `EventCount > 0`, so real
   citable event ids exist. v1's "citing the empty window" describes a state the model is never in.
2. **The other two levers are worse.** Relaxing `EMPTY_FINDINGS` was already rejected (plan Correction 1)
   because it weakens `research` and `triage-context-gathering` — the type's own comment calls an empty
   list *"a violation, not a valid 'nothing found'"* (`Findings.cs:113-118`). Adding a sentinel citation
   that D4 waves through would be a hole any `Findings` producer could drive through.
3. **The prompt is the only one of the three that is this cell's own file**, is not shared with
   `research` / `triage-context-scan`, and is **already due for a rewrite for an independent reason** —
   see the feedback-carrier defect below. Folding both fixes into one v2 costs nothing extra.
4. **Precedent.** A producing story owning its own template rewrite is the established Epic 41 shape:
   41-2 rewrote `define-acceptance-criteria.md` to `version: 2` and 41-9 rewrote `write-adr.md` v1→v2
   (`docs/sprint-status.yaml:632`, `:639`).

**The independent defect the same rewrite must fix.** `synthesize-standup.md` declares
`variables: role, eventWindowJson, sprintPlanJson, previousDigest` and its body renders no repair/revise
feedback carrier. The lifecycle threads violation feedback into `feedbackVariableName`, defaulting to
`revisionNotes` (`DocumentLifecycleWorkflow.cs:175`, `DocumentLifecycleHelper.cs:32`,
`BuildRevisionVariables` at `:424-435`). With no carrier rendered, the model never sees
`EMPTY_FINDINGS` / `MISSING_EVIDENCE` / the new citation violation and re-produces the identical draft
until the ring exhausts. 41-2's landed shape is the fix: declare `contextFindings` in the front matter,
render `{{contextFindings}}` in the body, and have the binding set
`["feedbackVariableName"] = "contextFindings"` (`AcceptanceCriteriaAuthoringWorkflow.cs:238`, `:243`;
`Prompts/product_owner/define-acceptance-criteria.md:2`, `:13`).
*(Honest scope note: no shipped prompt declares `revisionNotes`, and neither `research.md` nor
`triage-context-scan.md` declares any carrier either — this is an epic-wide condition, not a
41-1a defect. This story fixes it only for its own cell, because D4 adds a NEW violation class whose
only remedy is the model changing its citations.)*

**v2 must change exactly three things** (and bump `version: 1` → `2`):

1. Replace the quiet-day clause with: *`findings` MUST NOT be empty. A window with no events never
   reaches you. If the window is quiet, report the quiet as a finding and cite the actual event ids you
   were given in the event window — never invent a citation, and never cite "the empty window".*
2. State that at least one entry of every finding's `citations` MUST be an `eventId` copied verbatim from
   the supplied event window (see A2b).
3. Declare and render the `contextFindings` feedback carrier.

### A2b — D4's rule is "at least one anchored citation", not "every citation" (2026-08-01)

**What the plan said (D4, superseded):** *"asserts every citation string resolves to an id in the index,
with a new violation `CITATION_UNKNOWN_EVENT`."*

**Why that is wrong against the tree.** The shipped prompt's own citation vocabulary is
`"citations": ["the event ids / issue refs / PR refs this is based on"]`
(`Prompts/scrum_master/synthesize-standup.md`), and this story's own "Produced document" section requires
`issueId`/`repository` lineage on every finding. A rule that rejects *every* non-event-id string would
ban a PR or issue reference — making the digest strictly less useful than the shipped prompt promises,
and creating a second false-rejection source right next to the one D4 was written to remove.

**What the rule should be:** **at least one** citation per finding must resolve to an event id present in
the window's evidence index; the remaining citations are unconstrained free-form refs. A wholly
fabricated finding still fails; a well-evidenced finding that also names a PR does not. The violation
constant is therefore `CITATION_UNANCHORED` (renamed from the plan's `CITATION_UNKNOWN_EVENT`, which
would have misdescribed the rule). **41-11 must use the same constant** — it references this ring at
`story-41-11/implementation-plan.md:153`.

### A3 — both stated blockers have landed (2026-08-01)

**What the story said:** *"**Blocking:** **41-1a** (`scrum_master` role + `synthesize-standup` cell),
Epic 39 …"*, and the implementation plan's banner said the story *"remains hard-blocked on 41-1a (the
`scrum_master` role + `(scrum_master, synthesize-standup)` cell do not exist until 41-1a mints them)."*

**That is no longer true.** 41-1a is `done` (`docs/sprint-status.yaml:629`) and every artifact it was to
mint is in the tree — see the Dependencies section above for the file:line evidence. Separately, the
implementation plan claimed the scheduled-trigger seam *"does not exist"* and had *"no owner"*; **41-30
has also landed** and ships all four things the plan's step 10 listed as prerequisites. Details and
evidence are in the Dependencies section and in the plan's amended banner.

**Consequence:** this story has **no unlanded blocker**. Its remaining gates are the two
asserted-absent gaps (39-19/39-20 for delivery, 42-9 for broadcast), which AC4c now pins rather than
waits on.

### A4 — the window read is re-pointed (2026-08-01)

The implementation plan's D2 selected `IEventRepository.ListByTenantAsync`, which has no time filter and
a documented page size of 1..200 — it would have silently dropped the older part of a busy tenant's day.
It also resolved `IEventRepository` from the engine service provider, which does not register it. Both
are corrected in the implementation plan's amended D2; the evidence is recorded there.
