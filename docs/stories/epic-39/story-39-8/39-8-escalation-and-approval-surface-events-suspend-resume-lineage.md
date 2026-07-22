# Story 39-8: Escalation & Approval Surface — events, suspend/resume, lineage payload

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

As an **audit team member, orchestrator operator, and supervising human**,
I want a **uniform approval & escalation surface: an `APPROVAL.REQUESTED/PROVIDED` + `ESCALATION.TRIGGERED/RESOLVED` DCB event family with channel and time-to-resolve data, a generalized bookmark-suspend gate awaiting decisions, and a secure tenant-folded resume endpoint — with every escalation payload carrying the full document lineage**,
So that human oversight is verifiable from the event stream (the old Story 4-6 goal, absorbed here), a suspended lifecycle resumes exactly where it stopped with the decision injected, and whoever handles an exception sees the whole story — drafts, reviews, rounds, violations — never a bare failure.

## Priority

P0 — This is the exception sink of the entire epic: every 39-6 unhandleable outcome (`ReviewUndecidable`, `AmbiguityAboveThreshold`, `RoundsExhausted`, `ValidationExhausted`) and every accept-gate suspension (the single orchestrator-routed path of the 39-5 acceptor contract) lands here. It also delivers the audit-oversight event capture the platform has owed since Epic 4. Without it, orchestrator-assigned human decisions have no gate and unhandleable outcomes have no exception path.

**This story absorbs Story 4-6** (`docs/stories/4-6-event-capture-approvals-escalations.md`, status ready-for-dev): the `APPROVAL.*` / `ESCALATION.*` event family specified there ships here, extended with document lineage, channel, and resolution timing. 4-6 should be marked superseded-by-39-8 when this story lands.

## Architectural Context (READ FIRST)

**The suspend/resume pattern to generalize — not re-invent — is the tenant-folded bookmark gate, already proven five times:**

- `apps/tamma-elsa/src/Tamma.ElsaServer/Endpoints/DesignResumeEndpoint.cs` — the fullest specimen: bookmark name `design-approval-{tenant}-{session}` (tenant folded in ⇒ cross-tenant lookups 404, no IDOR; unguessable 128-bit session id; >1 bookmark match ⇒ 409 collision refusal; engine-internal route behind `.RequireAuthorization()` with `Tamma.Api` exposing the RBAC-gated tenant-scoped public surface and forwarding). Decision payload injected as workflow input via `IWorkflowClient.RunInstanceAsync`.
- `apps/tamma-elsa/src/Tamma.ElsaServer/Endpoints/ClarifyResumeEndpoint.cs`, `MergeApprovalResumeEndpoint.cs`, `DeploymentApprovalResumeEndpoint.cs`, `BlockerResumeEndpoint.cs` — the four siblings proving the pattern is already a convention; this story extracts the convention into ONE generic document-decision gate instead of minting a sixth copy.
- The suspend side: the `WaitFor*Activity` pattern (e.g. `WaitForDesignApprovalActivity` referenced by `DesignResumeEndpoint`, `apps/tamma-elsa/src/Tamma.Activities/Review/WaitForFixesActivity.cs`) — a canonical bookmark-name builder shared byte-for-byte between suspend and resume sides.

**Event plumbing:** constants class beside the domain (`DocumentEvents.cs` from 39-6, or a sibling `ApprovalEvents.cs`), events flowing through `apps/tamma-elsa/src/Tamma.Activities/Core/EventPersistenceMiddleware.cs` into the DCB store (`apps/tamma-elsa/src/Tamma.Data/Repositories/EventRepository.cs`, `Entities/DomainEvent.cs`), `AGGREGATE.ACTION.STATUS` naming and `issueId` tagging per CLAUDE.md. Existing escalation call-sites to reconcile (they emit ad-hoc escalation signals today): `apps/tamma-elsa/src/Tamma.Activities/Review/EscalateReviewActivity.cs`, `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs`.

**Contracts consumed:** 39-2 envelope + lineage (`issueId`, `correlationId`, parent/supersedes chains); 39-6 outcome enum + the ACCEPT gate (this story provides the gate it suspends on); 39-5 rules (autonomy level, always-escalate classes); `ITammaModeProvider` (`apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/TammaMode.cs`) for the per-mode shape of "who approves."

## Acceptance Criteria

1. **Event family defined and emitted.** `APPROVAL.REQUESTED`, `APPROVAL.PROVIDED`, `ESCALATION.TRIGGERED`, `ESCALATION.RESOLVED` constants ship in an `ApprovalEvents`-style class and are emitted through the standard persistence path. `APPROVAL.REQUESTED` fires whenever the 39-6 ACCEPT stage publishes an `AcceptanceRequest` and suspends (one path — to the orchestrator); `APPROVAL.PROVIDED` on the decision (decider = `orchestrator` with the effective-rules + autonomy reference it decided under, or the server-derived identity of the human the orchestrator assigned — the delegation itself is recorded by the 39-20 `TASK.ASSIGNED` event; feedback/notes), so orchestrator decisions are as auditable as human ones; `ESCALATION.TRIGGERED` on every 39-6 unhandleable outcome and every rules always-escalate hit; `ESCALATION.RESOLVED` when an escalation is dispositioned (resolved/overridden/abandoned, with resolution note). All tagged with `issueId`, `documentId`, `documentType`, `correlationId`.

2. **Story 4-6 coverage subsumed, explicitly.** A mapping table in the story-completion notes shows each 4-6 acceptance criterion satisfied by this surface (approval events with approver + timestamp; escalation events with reason; resolution capture), and `docs/stories/4-6-event-capture-approvals-escalations.md` is updated to `Status: superseded-by-39-8`.

3. **Channel + timing data.** Both `APPROVAL.*` and `ESCALATION.*` events carry a `channel` field (closed set: `orchestrator | user | api`) recording who/what the request was routed to, and the `*.PROVIDED`/`*.RESOLVED` events carry time-to-resolve (computable from the paired `REQUESTED`/`TRIGGERED` event and ALSO denormalized as `durationMs` in the resolving event's data, so dashboards don't need stream joins). A test asserts the pairing is reconstructable from the stream alone via `correlationId`.

4. **Generic suspend gate.** One reusable wait activity (e.g. `WaitForDocumentDecisionActivity`) registers a bookmark named by a single canonical builder folding **tenant + an unguessable decision-session Guid** (the `DesignResumeEndpoint` posture), suspends the calling lifecycle, and on resume reads the injected decision (mapped onto the 39-5 `AcceptanceDecision` type + feedback + server-derived decider) from workflow input. The 39-6 accept stage registers this ONE gate regardless of who the orchestrator routes the decision to — self-decision and assigned-human decision resume it identically; no new per-document-type gate copies are needed.

5. **Secure tenant-folded resume endpoint.** A generic engine-side resume endpoint (e.g. `DocumentDecisionResumeEndpoint` beside the five existing `*ResumeEndpoint.cs`) mirrors the `DesignResumeEndpoint` security posture exactly: tenant id folded into the bookmark name server-side (cross-tenant attempts 404, never act), collision ⇒ 409 refusal (never resume `bookmarks[0]`), engine route authorized for the Tamma.Api→engine hop only, with `Tamma.Api` exposing the RBAC-gated public surface (e.g. `POST /api/documents/decisions/{sessionId}/resume`) that derives tenant + decider from the authenticated principal — never trusted from the client. The 39-18 channels route their decisions through this same surface (the orchestrator agent's decisions authenticate as the orchestrator principal); no channel applies a decision directly. Per-mode: SaaS deciders are tenant members per RBAC; single-user mode folds the sole user's scope.

6. **Escalation payload carries full document lineage.** `ESCALATION.TRIGGERED`'s data (and the surface any consumer reads) includes the complete lineage the 39-6 outcome assembled: every draft envelope id + state, every `Review` id (member reviews included for panels), rounds used, last domain-phrased violations, the typed outcome name, and the effective policy reference — a handler can reconstruct the whole story from the one event plus the document store, with a test asserting no lineage field is dropped. Never a bare failure string.

7. **Round-trip integration test.** Testcontainers-backed tests drive: (a) supervised gate — lifecycle suspends, `APPROVAL.REQUESTED` emitted, resume endpoint approves (wrong-tenant attempt 404s first, duplicate-bookmark forgery 409s), lifecycle resumes to `Accepted`, `APPROVAL.PROVIDED` carries decider + `durationMs`; (b) rejection — resume with `Approved=false` + feedback lands the document in `Rejected` with the feedback on the trail; (c) escalation — a `RoundsExhausted` outcome emits `ESCALATION.TRIGGERED` with full lineage, and a disposition call emits `ESCALATION.RESOLVED`.

8. **Existing ad-hoc escalations reconciled, not broken.** `EscalateReviewActivity` and the `SingleIssueCycleWorkflow` escalation path either emit the new `ESCALATION.TRIGGERED` alongside their current signals or are documented (with file pointers) as migration debt for 39-14/39-15 — no existing behavior regresses in this story, and the decision is recorded in the story-completion notes.

## Technical Notes

- **Extract, don't multiply.** The five existing resume endpoints stay as-is (their gates are workflow-specific); the *new* generic gate + endpoint is what future lifecycles use. Folding the five legacy gates onto the generic surface is 39-13/39-14 migration work — note it, don't do it here.
- The bookmark-name builder must live in ONE place shared by the wait activity and the endpoint (the `DesignResumeEndpoint.BookmarkName` → `WaitForDesignApprovalActivity.ApprovalBookmarkName` delegation pattern) so suspend/resume names can never drift.
- `channel=orchestrator` covers the orchestrator's self-decisions and its escalation consumption; `user` covers orchestrator-assigned human decisions arriving from the Task View; `api` covers programmatic deciders (e.g. an external system driving the public endpoint). The closed set is deliberately small — extend by conscious enum edit, drift-tested.
- Escalation *notification* delivery (email/push/dashboards) is out of scope — this story defines the events + resume surface; notification fan-out rides the existing alerting/notification infrastructure separately.
- Time-to-resolve data + channel is exactly what the epic README promises from absorbing 4-6 — keep the field names stable; the 39-11 lineage API and dashboards will query them.

## Dependencies

- **Prerequisite:** 39-2 (lineage model), 39-6 (outcome enum + the gate's call-site — lockstep landing), 39-5 (acceptor contract / `AcceptanceDecision` / always-escalate rules).
- **Lockstep:** 39-18 (the channels that carry requests out and decisions back into this surface).
- **Prerequisite (in place):** Elsa 3 bookmarks + the five `*ResumeEndpoint.cs` specimens; DCB store + `EventPersistenceMiddleware`; `ITammaModeProvider`.
- **Absorbs:** Story 4-6 (`docs/stories/4-6-event-capture-approvals-escalations.md`) — mark superseded on landing.
- **Feeds:** 39-10 (suspended-on-bookmark is one of the two legal resumability postures), 39-11 (lineage/approval queries), 39-13/39-14 (legacy gate migrations).

## Estimated Effort

4–5 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-19 | 1.0.0   | Initial story creation | Claude |
| 2026-07-20 | 1.1.0   | Aligned with the 39-5 acceptor redesign: `APPROVAL.*` events cover both acceptors (orchestrator decisions emit with `channel=orchestrator` + effective-rules reference), gate maps onto `AcceptanceDecision` | Claude |
| 2026-07-20 | 1.2.0   | Channel-transport alignment: both acceptor paths suspend on the one generic gate and resume through this surface via the 39-18 channels; no channel applies a decision directly | Claude |

## Completion Notes

### AC2 — Story 4-6 coverage subsumed (mapping table)

Story 4-6 (`docs/stories/4-6-event-capture-approvals-escalations.md`, now `Status: superseded-by-39-8`) specified the `APPROVAL.*` / `ESCALATION.*` event family for audit-oversight capture. Every 4-6 acceptance criterion is satisfied by this surface, extended with document lineage, channel, and resolution timing:

| 4-6 AC | 39-8 mechanism | Where |
|---|---|---|
| AC1 — approval REQUESTED captured | `APPROVAL.REQUESTED` emitted at the gate's `Execute` (request+suspend is one atomic site) with `channel=orchestrator` + `requestedAtUtc` + `rulesReference` | `Tamma.Activities/Documents/WaitForDocumentDecisionActivity.cs` (`BuildRequestedEvent`); `ApprovalEvents.Requested` |
| AC2 — approval PROVIDED with approver + timestamp | `APPROVAL.PROVIDED` emitted at the resume callback with server-derived `deciderId`/`deciderDisplay`, `channel`, `decisionKind`, `feedback`, `durationMs` | `WaitForDocumentDecisionActivity.cs` (`BuildProvidedEvent`); decider derived in `Tamma.Api/Endpoints/DocumentDecisionEndpoints.cs` (`ResolveApprover`) |
| AC3 — escalation TRIGGERED with reason | `ESCALATION.TRIGGERED` (LOUD error row) with typed `outcome`, full `lineage`, `rulesReference`, `channel` | `Tamma.Activities/Documents/EmitEscalationEventActivity.cs`; `ApprovalEvents.EscalationTriggered` |
| AC4 — resolution capture (note + timing) | `ESCALATION.RESOLVED` with `disposition`, `note`, `resolvedBy`, `channel`, denormalized `durationMs` | `Tamma.Api/Services/Documents/EscalationDispositionService.cs`; `POST /api/documents/escalations/{escalationId}/resolve` |
| AC5 — pairing reconstructable from the stream | `correlationId`(+`sessionId`/`escalationId`) tags on all four events; duration recomputable and denormalized | `ApprovalPairingReconstructionTests` |
| AC6 — channel recorded | closed `channel` set (`orchestrator \| user \| api`), server-derived on resume, never body-trusted | `ApprovalChannels.Derive` in `DocumentDecisionEndpoints.cs`; `ApprovalChannel` (39-5) |

### AC8 / D11 — legacy escalation reconciliation (documentation only, zero code change)

Per Design Decision D11, the two pre-existing ad-hoc escalation call-sites are recorded as MIGRATION DEBT for Stories 39-14/39-15 rather than retro-emitting `ESCALATION.TRIGGERED` (which would double-count escalation dashboards and carries no `documentId`/lineage). Both files are left UNCHANGED by this story:

- `apps/tamma-elsa/src/Tamma.Activities/Review/EscalateReviewActivity.cs` — already emits `CODE_REVIEW.ESCALATED` at the raise point (the mentorship-scoped 4-6 partial). Migration to the unified `ESCALATION.*` surface is 39-14/39-15 work.
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs` — the merge-gate "Escalated" terminal region; `CodeReviewWorkflow` emits `CODE_REVIEW.ESCALATION_RESOLVED`. Same migration debt.

No existing behavior regresses; the decision is recorded here with file pointers as D11 requires.
