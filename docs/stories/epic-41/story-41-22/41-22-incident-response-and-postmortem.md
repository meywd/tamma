# Story 41-22: Incident Response & Postmortem Workflow

Status: drafted

## User Story

As a **devops** engineer (or eligible role-holder), I want a workflow that runs an operational incident
from diagnosis through response to a written postmortem on the lifecycle, so that incidents produce a
tracked root-cause `Diagnosis`, coordinated response, and a blameless postmortem with action items —
instead of an untracked firefight.

## Priority

P3 / Wave 3 — high-consequence reactive ops; seeds 41-26 runbooks.

## Scope

Reactive trigger (alert / health-review escalation) → thin binding(s) over `document-lifecycle`, run as a
short sequence:
1. `produces: Diagnosis` — cell **`(devops, incident-rootcause)`**, a new cell minted by **41-1a**,
   mirroring the landed `Diagnosis` producer `(senior_developer, debug-rootcause)`
   (`ContractBindingTests.cs:214`).
2. `produces: Plan` (response **and** rollback steps) — cell **`(devops, plan-incident-response)` alone**.
3. `produces: prose (postmortem, audience=engineering)` — cell `(devops, write-postmortem)`.

`consumes: [alert, DCB deployment/health events, affected service context]`.

**Executing** a rollback is out of scope: this story **dispatches the landed `deployment-pipeline`**
workflow, it does not build or re-bind rollback.

> **Corrected — this story listed `(devops, rollback)` as a second `Plan` produce cell, and the epic
> matrix listed rollback as missing. Both were wrong; rollback is landed and already executed.**
> `DeploymentPipelineWorkflow.cs:299-329` builds the rollback branch (`emitRollbackStarted` →
> `rollbackCall` → `extractRollbackResult` → `rollbackOk` → `emitRollbackSuccess` / `emitRollbackFailed`),
> wired off production failure at `:546-553`, dispatching the mediated `(devops, rollback)` cell with
> `enableTools = true` and emitting `DEPLOY.ROLLBACK.STARTED` / `.SUCCESS` / `.FAILED`
> (`DeployEvents.cs:61,64,70`). `Prompts/devops/rollback.md` is an **execution** prompt ("Roll the
> {{stage}} environment back to the previous known-good release") returning
> `{status, stage, reason, filesChanged, verification}`.
>
> The cell is CI-pinned as a **non-document producer**: `ContractBindingTests.cs:246-249` binds it to
> `DeploymentPipelineWorkflow.ParseStageStatus`, and `:616-623` enumerates it in
> `NonDocumentTypeResidual` ("a side-effect gate, not a document"). Re-binding it as a `Plan` producer
> would fail the universal pin at `:626` and the stale-residual ratchet at `:645`.
>
> **Decision — authoring and executing are separate cells.** `(devops, plan-incident-response)` is the
> **authoring** cell: the rollback steps are plan content inside the response `Plan`.
> `(devops, rollback)` is the **execution** cell, reached only by dispatching `deployment-pipeline`.

> **Corrected — stage 1's cell was `(devops, diagnose-incident)`, which is also taken.** Found while
> verifying the rollback collision above: that cell is the **devops triage-panel review lens**
> (`RolePhaseMap.GetTriageActionForRole(Devops) => DiagnoseIncident`, `RolePhaseMap.cs:404-412`), and it
> is listed in `ContractBindingTests.ReviewProducerDispatchablePairs` (`:542-543`, "policy-only, no
> compiled emitter"). `ReviewProducerDispatchablePairs_HasNoStaleEntries` (`:579`) fails the build on any
> pair that is *also* in `Bindings`, so binding it as a `Diagnosis` producer breaks CI — and rewriting
> `Prompts/devops/diagnose-incident.md` to emit a `Diagnosis` would change what the triage panel's devops
> reviewer returns. Stage 1 therefore takes a new cell; `(devops, diagnose-incident)` is left alone.

## Produced documents

`Diagnosis` (ranked hypotheses, affected files), one `Plan` (response steps **including** the rollback
steps), and an audience-tagged prose postmortem (timeline / root cause / impact / action items).
`repository`/incident lineage.

## Events

`INCIDENT.STARTED` → `.DIAGNOSED` → `.RESPONSE_ACCEPTED` → `.RESOLVED` → `POSTMORTEM.ACCEPTED` alongside
`DOCUMENT.*`. A dispatched rollback's audit trail is `deployment-pipeline`'s existing `DEPLOY.ROLLBACK.*`
— this story does not mint rollback events.

## Orchestrator / user interaction

Active incident is an always-escalate class that pages the devops role; the response plan's accept gate is
time-sensitive (bounded human window, then orchestrator-decides per policy). Postmortem action items route
to owning roles and can dispatch 41-26.

## Autonomy behavior

- **70–84:** agent diagnoses + drafts response; a human approves before executing rollback/response.
- **85–100:** agent may execute a pre-approved low-risk response class; destructive/prod rollback always
  escalates; postmortem drafted and human-accepted by default.

> **Agent execution of a response class needs Epic 42.** Only six coding-oriented `IToolExecutor`s are
> registered today (`Tamma.Api/Program.cs:753-764`: file read/write, search-code, shell-execute,
> git-operations, run-tests) — there is no cloud/VPS ops executor and no feature-flag kill-switch. Until
> Epic 42 lands, the 85–100 band's "execute a response class" degrades to an unclassified `ShellExecute`
> or to the human-assigned path (rule 4). Diagnosis, planning and the postmortem are unaffected.

## Acceptance Criteria

1. Each stage is a thin lifecycle binding producing its typed/prose document; no bespoke terminals. The
   story adds `Bindings` entries only for `(devops, incident-rootcause)` and
   `(devops, plan-incident-response)`; `ContractBindingTests`'s `(devops, rollback)` entry (`:246-249`)
   with its `NonDocumentTypeResidual` membership (`:616-623`), and the `(devops, diagnose-incident)`
   entry in `ReviewProducerDispatchablePairs` (`:542-543`), are all unchanged — the universal pin
   (`:626`), the stale-residual ratchet (`:645`) and `ReviewProducerDispatchablePairs_HasNoStaleEntries`
   (`:579`) stay green. `(devops, write-postmortem)` is classified in `IntentionallyUnbound` as prose,
   with a justification that passes `UniversalPin_EveryIntentionallyUnbound_IsProseOrCode`.
2. The response `Plan` validates against `PlanDocumentType`: a step with no file map ⇒
   `TASK_MISSING_FILE_MAP`, a step with no testing/verification ⇒ `TASK_MISSING_TESTING`, a cyclic
   ordering ⇒ `CYCLIC_DEPENDS_ON` (`Plan.cs:53-71`) — one fixture per rule.
3. An active incident is an always-escalate class: an integration test at autonomy 100 still routes the
   response-plan accept decision to a human, and a response classified destructive/prod-rollback never
   self-accepts. A rollback is performed by dispatching `deployment-pipeline` — an assertion that no
   `(devops, rollback)` llm-call is issued directly by this workflow.
4. Postmortem action items produce role-scoped Task View entries. **Blocked on 39-20**: today
   `ITaskAudienceResolver` is stubbed fail-closed by `InitiatorOnlyTaskAudienceResolver`
   (`Program.cs:445-447`), so only the issue initiator is admitted — this AC is testable only once
   role-addressed delivery exists, and until then the story asserts the fail-closed behaviour instead of
   silently dropping the entries.
5. `[ResumeBehavior(Both)]` across the sequence; 39-10 structural test green without an allowlist entry.

> Postmortem *blamelessness*, root-cause *correctness* and diagnosis *ranking quality* are not acceptance
> criteria — no deterministic check exists. They are the review stage's and the accept gate's job.

## Dependencies

- **Blocking:**
  - **41-1a** — must mint the `(devops, incident-rootcause)` cell (eligible set + template); see the
    stage-1 correction above.
  - Epic 39 (`Diagnosis`, `Plan`, lifecycle, store, escalation).
  - **41-1c** (prose documents & audience tags) for the postmortem. *Corrected: this line previously
    read "Epic 39 (… prose …)". Epic 39 never chartered prose — 39-1 lists prose/tech-writer output as
    explicitly OUT OF SCOPE of the 10-type table, and no `prose` `DocumentTypeKey`, `Audience` column on
    `DocumentInstance`, or audience vocabulary exists in the code today.*
  - **Epic 40** for the code/infra response step. *Corrected: this previously read "Epic 40 for any
    code/infra response step", which reads as a durability nicety. Epic 40 ships the missing **execution
    substrate**: `.github/workflows/tamma-agent.yml` does not exist in this repo, so the coding step's
    dispatch fails loud with `WorkflowNotFound` (`AgentDispatchMediationService.cs:109`) today. The
    diagnosis / planning / postmortem stages have no Epic 40 dependency and can land first.*
- **Related:** consumes 41-23 escalations; feeds 41-26; sibling to 41-21. Dispatches the landed
  `deployment-pipeline` for rollback.

## Estimated Effort

5–6 days
